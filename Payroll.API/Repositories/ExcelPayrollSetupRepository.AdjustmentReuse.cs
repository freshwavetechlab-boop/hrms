using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayrollSetupRepository
{
    internal static bool AllApprovedAdjustmentsAreReused(IEnumerable<int> approved, IEnumerable<int> reused) =>
        approved.ToHashSet().SetEquals(reused);

    internal static PayrollAdjustment? FindReusableSourceAdjustment(int clientId, string month, string batchId,
        int employeeId, int sourceRow, ExcelPayrollSetupComponent component, decimal amount,
        IReadOnlyList<PayrollAdjustment> candidates)
    {
        var sameSource = new List<PayrollAdjustment>();
        foreach(var candidate in candidates.Where(a=>a.ClientId==clientId&&a.EmployeeId==employeeId&&a.PayPeriod==month
            &&a.PayRunType=="Regular"&&a.ReasonCode=="EXCEL_SETUP"
            &&a.ComponentCode.Equals(component.Code,StringComparison.OrdinalIgnoreCase)))
        {
            var reference=SelectedPayRunAdjustments.Reference(candidate);
            if(reference.BatchId==batchId&&reference.SourceRow==sourceRow)sameSource.Add(candidate);
        }
        if(sameSource.Count>1)throw new InvalidOperationException("Several adjustments reference this source component; review duplicates before configuration.");
        if(sameSource.Count==0)return null;
        var existing=sameSource[0];
        SelectedPayRunAdjustments.RequireUnbound(new(){ClientId=clientId,PayPeriod=month,IncludedEmployeeIds=[employeeId]},existing);
        if(!component.IsPeriodAdjustment||existing.Amount!=amount||existing.AdjustmentType!=component.Category||existing.Taxable!=component.Taxable)
            throw new InvalidOperationException("The existing source adjustment amount/classification differs; it is retained and no second adjustment is created.");
        return existing;
    }

    private static async Task<ExcelPayrollReusedAdjustment?> ReadSourceAdjustmentReuseAsync(MySqlConnection db, MySqlTransaction tx,
        int clientId, Plan plan, PlannedRow row, ResolvedComponent component, decimal amount, bool lockRows)
    {
        if(row.Employee.Id<=0)return null;
        var tail=lockRows?" FOR UPDATE":"";
        var candidates=(await db.QueryAsync<PayrollAdjustment>(@"SELECT * FROM payrolladjustments
WHERE ClientId=@ClientId AND EmployeeId=@EmployeeId AND PayPeriod=@Month AND PayRunType='Regular'
AND ComponentCode=@Code AND ReasonCode='EXCEL_SETUP' ORDER BY Id"+tail,
            new{ClientId=clientId,EmployeeId=row.Employee.Id,Month=plan.Batch.Month,Code=component.Definition.Code},tx)).ToList();
        var existing=FindReusableSourceAdjustment(clientId,plan.Batch.Month,plan.Batch.Id,row.Employee.Id,row.SourceRow,
            component.Definition,amount,candidates);
        if(existing is null)return null;
        var reference=SelectedPayRunAdjustments.Reference(existing);
        var saved=await db.QuerySingleOrDefaultAsync<SavedSetup>(@"SELECT id ImportId,request_id RequestId,status Status,
CAST(request_json AS CHAR) RequestJson,CAST(result_json AS CHAR) ResultJson FROM excel_payroll_setup_imports
WHERE client_id=@ClientId AND batch_id=@BatchId AND id=@ImportId AND status='Applied'"+tail,
            new{ClientId=clientId,BatchId=plan.Batch.Id,reference.ImportId},tx)
            ??throw new InvalidOperationException("The existing adjustment has no Applied source receipt; no adjustment is replaced or duplicated.");
        var request=JsonSerializer.Deserialize<ExcelPayrollSetupRequest>(saved.RequestJson,Json)!;
        var result=JsonSerializer.Deserialize<ExcelPayrollSetupResult>(saved.ResultJson,Json)!;
        if(request?.EmployeeDecisions is null||request.RowIds is null||request.Components is null||result?.EmployeeIds is null||result.KeptRows is null)
            throw new InvalidOperationException("Existing adjustment source receipt is malformed; no amount is changed or duplicated.");
        var decisions=request.EmployeeDecisions.Where(d=>d.RowId==row.RowId&&d.MatchedEmployeeId==row.Employee.Id&&!d.AllowCreate).ToArray();
        var definitions=request.Components.Where(c=>c.Code.Equals(component.Definition.Code,StringComparison.OrdinalIgnoreCase)).ToArray();
        if(request.BatchId!=plan.Batch.Id||result.ImportId!=reference.ImportId||request.RowIds.Count(id=>id==row.RowId)!=1
            ||decisions.Length!=1||result.EmployeeIds.Count(id=>id==row.Employee.Id)!=1||result.KeptRows.Any(k=>k.RowId==row.RowId)
            ||definitions.Length!=1||!definitions[0].IsPeriodAdjustment||definitions[0].Category!=component.Definition.Category
            ||definitions[0].Taxable!=component.Definition.Taxable||definitions[0].SourceColumnIndex!=component.Definition.SourceColumnIndex
            ||definitions[0].ExpectedColumnIndex!=component.Definition.ExpectedColumnIndex)
            throw new InvalidOperationException("Existing adjustment receipt does not prove this exact employee/source row/component; resolve it without duplication.");
        return new(existing.Id,row.Employee.Id,row.RowId,row.SourceRow,existing.ComponentCode,existing.Amount,reference.ImportId,
            SelectedPayRunAdjustments.BusinessFingerprint(existing),SelectedPayRunAdjustments.ReceiptFingerprint(saved.RequestJson,saved.ResultJson));
    }

    private static async Task ReviewSourceAdjustmentReusesAsync(MySqlConnection db, MySqlTransaction tx,int clientId,Plan plan,bool salaryCorrection)
    {
        foreach(var row in plan.Rows.Where(r=>!r.KeepExisting))
        foreach(var adjustment in row.Adjustments.Where(a=>a.Amount!=0))
        {
            try
            {
                var reused=await ReadSourceAdjustmentReuseAsync(db,tx,clientId,plan,row,adjustment.Component,adjustment.Amount,false);
                if(reused is null)continue;
                plan.AdjustmentReuses.Add(reused);
                plan.Preview.Issues.Add(new("Info",$"{reused.ComponentCode}: existing approved adjustment {reused.AdjustmentId} will be reused unchanged; no second amount is created.",row.RowId,"Period adjustment"));
            }
            catch(InvalidOperationException error){plan.Preview.Issues.Add(new("Error",error.Message,row.RowId,"Period adjustment"));}
            catch(JsonException){plan.Preview.Issues.Add(new("Error","Existing adjustment receipt JSON is invalid; review its original source.",row.RowId,"Period adjustment"));}
        }
        if(salaryCorrection)
            foreach(var row in plan.Rows.Where(r=>!r.KeepExisting&&r.Employee.Id>0))
            {
                var approved=(await db.QueryAsync<int>("SELECT Id FROM payrolladjustments WHERE ClientId=@ClientId AND EmployeeId=@EmployeeId AND PayPeriod=@Month AND Status='Approved'",new{ClientId=clientId,EmployeeId=row.Employee.Id,Month=plan.Batch.Month},tx)).ToHashSet();
                if(!AllApprovedAdjustmentsAreReused(approved,plan.AdjustmentReuses.Where(a=>a.EmployeeId==row.Employee.Id).Select(a=>a.AdjustmentId)))
                    plan.Preview.Issues.Add(new("Error","Approved adjustments outside the exact reused source literals remain. Review them before salary correction; no amount is removed or duplicated.",row.RowId,"Period adjustment"));
            }
    }
}
