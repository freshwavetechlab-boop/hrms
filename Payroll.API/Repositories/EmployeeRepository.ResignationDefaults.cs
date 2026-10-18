using System.Globalization;
using System.Text.Json;
using Dapper;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public partial class EmployeeRepository
{
    internal static string? PayrollMonthEnd(string? month) => DateTime.TryParseExact(month,"yyyy-MM",CultureInfo.InvariantCulture,DateTimeStyles.None,out var date) && date.Year<9999
        ? date.AddMonths(1).AddDays(-1).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture) : null;

    public async Task<IReadOnlyList<EmployeeResignationDefault>> ResignationDefaultsAsync(EmployeeResignationDefaultsRequest request)
    {
        if (request.ClientId<=0 || request.EmployeeIds is null || request.EmployeeIds.Count is <1 or >1000 || request.EmployeeIds.Distinct().Count()!=request.EmployeeIds.Count)
            throw new InvalidOperationException("Select 1–1,000 employees from one client.");
        await using var db=Connection(); await db.OpenAsync();
        await using var tx=await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,true);
        var identities=await ExcelPayrollSetupRepository.ReadIdentitiesAsync(db,tx,request.ClientId,DateTime.Today.ToString("yyyy-MM"));
        var selected=identities.Where(e=>request.EmployeeIds.Contains(e.Id)).ToList();
        if(selected.Count!=request.EmployeeIds.Count) throw new InvalidOperationException("Every selected employee must belong to this client.");
        var native=await db.QueryAsync<(int EmployeeId,string Month)>(@"SELECT pe.EmployeeId,MAX(pr.PayPeriod) Month
FROM payrunemployees pe JOIN payruns pr ON pr.Id=pe.PayRunId WHERE pr.ClientId=@ClientId AND pe.EmployeeId IN @Ids
AND pe.IsSkipped=FALSE AND pr.Status IN ('Draft','Queued','Processing','Pending Approval','Approved','Partially Paid','Paid') GROUP BY pe.EmployeeId",new {request.ClientId,Ids=request.EmployeeIds},tx);
        var months=native.Where(r=>PayrollMonthEnd(r.Month)!=null).ToDictionary(r=>r.EmployeeId,r=>r.Month);
        var sources=await db.QueryAsync<(string BatchJson,string? SourceJson)>(@"SELECT CAST(b.batch_json AS CHAR) BatchJson,CAST(s.source_json AS CHAR) SourceJson
FROM excel_payslip_batches b LEFT JOIN excel_payslip_calculation_sources s ON s.batch_id=b.id AND s.client_id=b.client_id
WHERE b.client_id=@ClientId AND NOT EXISTS(SELECT 1 FROM excel_payslip_batches newer WHERE newer.client_id=b.client_id
AND JSON_UNQUOTE(JSON_EXTRACT(newer.batch_json,'$.month'))=JSON_UNQUOTE(JSON_EXTRACT(b.batch_json,'$.month'))
AND (newer.created_at>b.created_at OR newer.created_at=b.created_at AND newer.id>b.id))",new {request.ClientId},tx);
        foreach(var record in sources)
        {
            var batch=JsonSerializer.Deserialize<ExcelPayslipBatch>(record.BatchJson,ExcelPayslipRepository.JsonOptions)!;
            if(PayrollMonthEnd(batch.Month)==null) continue;
            var source=record.SourceJson is null ? null : JsonSerializer.Deserialize<ExcelPayslipCalculationSource>(record.SourceJson,ExcelPayslipRepository.JsonOptions);
            AccumulatePayrollMonths(batch,source,identities,request.EmployeeIds.ToHashSet(),months);
        }
        await tx.RollbackAsync();
        return selected.Select(e=>new EmployeeResignationDefault(e.Id,e.EmployeeCode,e.Name,months.GetValueOrDefault(e.Id),PayrollMonthEnd(months.GetValueOrDefault(e.Id)))).ToList();
    }

    internal static void AccumulatePayrollMonths(ExcelPayslipBatch batch,ExcelPayslipCalculationSource? source,
        IReadOnlyList<ExcelPayrollSetupRepository.Identity> identities,IReadOnlySet<int> selected,Dictionary<int,string> months)
    {
        if(PayrollMonthEnd(batch.Month)==null) return;
        var scoped=identities.Where(e=>e.ClientId==batch.ClientId).ToList();
        foreach(var row in batch.Rows)
        {
            var issues=new List<ExcelPayrollSetupIssue>();
            var match=ExcelPayrollSetupRepository.MatchIdentity(row,ExcelPayrollSetupRepository.SourceInformation(row,source),scoped,null,issues);
            if(match is null || issues.Count>0 || !selected.Contains(match.Id)) continue;
            if(!months.TryGetValue(match.Id,out var month) || string.CompareOrdinal(batch.Month,month)>0) months[match.Id]=batch.Month;
        }
    }
}
