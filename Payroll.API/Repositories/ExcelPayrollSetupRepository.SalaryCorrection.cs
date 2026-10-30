using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayrollSetupRepository
{
    private static string SetupSelectionHash(ExcelPayrollSetupRequest request)=>request.CorrectionOfImportId is null
        ?SelectionHash(request.RowIds):Hash("salary-correction-v1|"+request.CorrectionOfImportId+"|"+SelectionHash(request.RowIds));

    internal static string? SalaryCorrectionScopeError(ExcelPayrollSetupRequest request, IReadOnlySet<string> applied,
        ExcelPayrollSetupRequest parent, ExcelPayrollSetupResult receipt, bool hasPayroll)
    {
        if(request.CorrectionOfImportId is null)return "An exact prior Applied receipt is required for a salary correction.";
        if(request.TransferReviewVersion!=1||request.TemplateScopeReviewVersion!=1||request.RowIds.Count!=1
            ||!applied.Contains(request.RowIds[0])||request.ImportAttendance||request.ImportPayableOnlyAttendance
            ||request.OverrideSourcePayableAttendance||request.LocationAllocations is not null||request.PresentDaysColumnIndex is not null
            ||request.CreateMissingWorkLocations||request.CreateMissingDropdownValues
            ||request.AttendanceReview is not {Mode:"VerifyExistingNative",Reviewed:true}
            ||request.Components.Any(c=>string.IsNullOrWhiteSpace(c.ExistingComponentId)))
            return "Salary correction supports one already-transferred existing employee, reused components and verified native attendance only; attendance, locations and shared masters remain unchanged.";
        var rowId=request.RowIds[0];var selected=request.EmployeeDecisions.Where(d=>d.RowId==rowId).ToArray();
        var previous=parent.EmployeeDecisions.Where(d=>d.RowId==rowId).ToArray();
        if(parent.LocationAllocations is not null||receipt.LocationAllocations is not null||parent.BatchId!=request.BatchId||receipt.ImportId!=request.CorrectionOfImportId||parent.RowIds.Count(id=>id==rowId)!=1
            ||selected.Length!=1||previous.Length!=1||selected[0].AllowCreate||previous[0].AllowCreate
            ||selected[0].MatchedEmployeeId is not int employeeId||employeeId<=0||previous[0].MatchedEmployeeId!=employeeId
            ||receipt.EmployeeIds.Count(id=>id==employeeId)!=1||receipt.KeptRows.Any(k=>k.RowId==rowId))
            return "The prior Applied receipt must prove this exact existing employee and saved source row; no new or guessed identity is accepted.";
        if(hasPayroll)return "This employee already has payroll data in the source month. Existing results are preserved; salary correction cannot replace a processed or pending payroll.";
        return null;
    }

    private static async Task ReviewSalaryCorrectionAsync(MySqlConnection db,MySqlTransaction tx,int clientId,
        ExcelPayrollSetupRequest request,Plan plan,IReadOnlySet<string> applied)
    {
        try
        {
            var saved=await db.QuerySingleOrDefaultAsync<SavedSetup>(@"SELECT id ImportId,request_id RequestId,status Status,
CAST(request_json AS CHAR) RequestJson,CAST(result_json AS CHAR) ResultJson FROM excel_payroll_setup_imports
WHERE client_id=@ClientId AND batch_id=@BatchId AND id=@ImportId AND status='Applied'",
                new{ClientId=clientId,request.BatchId,ImportId=request.CorrectionOfImportId},tx)
                ??throw new InvalidOperationException("The exact prior Applied source receipt is unavailable for salary correction.");
            var parent=JsonSerializer.Deserialize<ExcelPayrollSetupRequest>(saved.RequestJson,Json)
                ??throw new InvalidOperationException("Prior salary correction source request is invalid.");
            var receipt=JsonSerializer.Deserialize<ExcelPayrollSetupResult>(saved.ResultJson,Json)
                ??throw new InvalidOperationException("Prior salary correction receipt is invalid.");
            var ids=request.EmployeeDecisions.Where(d=>d.MatchedEmployeeId>0).Select(d=>d.MatchedEmployeeId!.Value).Distinct().ToArray();
            var history=ids.Length==0?0:await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM payruns r JOIN payrunemployees e ON e.PayRunId=r.Id
WHERE r.ClientId=@ClientId AND r.PayPeriod=@Month AND e.EmployeeId IN @Ids",new{ClientId=clientId,Month=plan.Batch.Month,Ids=ids},tx);
            if(SalaryCorrectionScopeError(request,applied,parent,receipt,history!=0) is string error)throw new InvalidOperationException(error);
            plan.CorrectionReceiptFingerprint=Hash(saved.RequestJson+"\n"+saved.ResultJson);
            plan.Preview.Issues.Add(new("Info",$"Salary-only correction of Applied receipt {saved.ImportId}; its original values/history and native attendance are retained. Existing source adjustments are checked separately.",request.RowIds[0],"Salary correction"));
        }
        catch(Exception error) when(error is InvalidOperationException or JsonException or NullReferenceException)
        {plan.Preview.Issues.Add(new("Error",error is NullReferenceException?"Prior source receipt is incomplete; salary correction is blocked.":error.Message,null,"Salary correction"));}
    }
}
