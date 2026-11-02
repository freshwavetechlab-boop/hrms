using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dapper;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public partial class PayRunRepository
{
    private const string EmployerCostReason="DRAFT_EMPLOYER_COST";

    internal static void ValidateEmployerCosts(DraftEmployerCostRequest request)
    {
        if(!request.Confirmed||request.ClientId<=0||!Regex.IsMatch(request.PayPeriod??"",@"^\d{4}-(0[1-9]|1[0-2])$")
            ||request.Rows is null||request.Rows.Count is <1 or >1000||request.Rows.Any(row=>row is null||row.EmployeeId<=0
                ||string.IsNullOrWhiteSpace(row.ComponentCode)||row.ComponentCode.Length>50||row.Amount<=0||row.Amount>1000000000m||decimal.Round(row.Amount,2)!=row.Amount
                ||!Regex.IsMatch(row.Reference??"",@"^[A-Za-z0-9:._-]{5,100}$")||string.IsNullOrWhiteSpace(row.Notes)||row.Notes.Length>300)
            ||request.Rows.Select(row=>$"{row.EmployeeId}|{row.ComponentCode.Trim().ToUpperInvariant()}|{row.Reference}").Distinct().Count()!=request.Rows.Count)
            throw new InvalidOperationException("Review the exact draft scope and 1–1000 distinct positive employer-cost amounts, references and reasons before applying.");
    }

    internal static string EmployerCostDetails(string details,IEnumerable<PayrollAdjustment> costs)
    {
        var lines=JsonNode.Parse(details) as JsonArray??throw new InvalidOperationException("The saved draft has invalid calculation details.");
        for(var i=lines.Count-1;i>=0;i--)if(lines[i]?["draftEmployerCost"]?.GetValue<bool>()==true)lines.RemoveAt(i);
        foreach(var group in costs.GroupBy(row=>new{row.ComponentCode,row.ComponentName}))
            lines.Add(JsonSerializer.SerializeToNode(new{Id=group.Key.ComponentCode,Name=group.Key.ComponentName,Category="Benefit",
                ComponentRole="Employer Contribution",monthlyAmount=group.Sum(row=>row.Amount),amount=group.Sum(row=>row.Amount),ProRata=false,
                draftEmployerCost=true,adjustmentIds=group.Select(row=>row.Id).Order().ToArray(),source="Reviewed period employer cost"}));
        return lines.ToJsonString();
    }

    public async Task<DraftEmployerCostResult> AddDraftEmployerCostsAsync(int id,DraftEmployerCostRequest request,string actor)
    {
        ValidateEmployerCosts(request);
        await using var db=CreateConnection();await db.OpenAsync();await using var tx=await db.BeginTransactionAsync();
        var run=await db.QuerySingleOrDefaultAsync<PayRun>("SELECT * FROM payruns WHERE Id=@Id AND ClientId=@ClientId AND PayPeriod=@PayPeriod FOR UPDATE",new{Id=id,request.ClientId,request.PayPeriod},tx);
        if(run is null||run.Status!="Draft"||run.NetPay!=request.ExpectedNetPay||run.PayrollCost!=request.ExpectedPayrollCost)
            throw new InvalidOperationException("The selected payroll draft or its cash totals changed. Refresh before applying employer costs.");
        var ids=request.Rows.Select(row=>row.EmployeeId).Distinct().ToArray();
        var saved=(await db.QueryAsync<PayRunEmployee>("SELECT * FROM payrunemployees WHERE PayRunId=@Id AND ClientId=@ClientId AND EmployeeId IN @Ids AND IsSkipped=FALSE FOR UPDATE",new{Id=id,request.ClientId,Ids=ids},tx)).ToDictionary(row=>row.EmployeeId);
        if(saved.Count!=ids.Length)throw new InvalidOperationException("Select only included employees from this exact saved draft.");
        var setup=ReadPayrollSetup(await PayrollDataTableStore.GetSetupJsonAsync(db,tx));
        var assigned=(await db.QueryAsync<(int Id,string SalaryStructureId)>("SELECT Id,SalaryStructureId FROM employees WHERE ClientId=@ClientId AND Id IN @Ids",new{request.ClientId,Ids=ids},tx)).ToDictionary(row=>row.Id,row=>row.SalaryStructureId);
        var existing=(await db.QueryAsync<PayrollAdjustment>("SELECT * FROM payrolladjustments WHERE PayRunId=@Id AND ClientId=@ClientId AND ReasonCode=@Reason AND Status='Applied' FOR UPDATE",new{Id=id,request.ClientId,Reason=EmployerCostReason},tx)).ToList();
        var pending=new List<PayrollAdjustment>();var unchanged=0;
        foreach(var row in request.Rows)
        {
            var component=setup.Components.SingleOrDefault(c=>c.Active&&c.Category=="Benefit"&&c.ComponentRole=="Employer Contribution"&&c.Code.Equals(row.ComponentCode.Trim(),StringComparison.OrdinalIgnoreCase));
            var template=setup.Structures.SingleOrDefault(s=>s.Active&&s.Id==assigned[row.EmployeeId]&&s.ClientId.Split([';',',','|']).Any(v=>v.Split(':')[0].Trim()==request.ClientId.ToString()));
            if(component is null||template is null||!template.Lines.Any(line=>line.ComponentId==component.Id))
                throw new InvalidOperationException("Each employer cost must use an active employer-contribution component in the employee's assigned client template.");
            var prefix=$"Employer cost ref:{row.Reference};";
            var matches=existing.Where(cost=>cost.EmployeeId==row.EmployeeId&&cost.ComponentCode==component.Code&&cost.Notes.StartsWith(prefix,StringComparison.Ordinal)).ToArray();
            if(matches.Length>1||matches.Any(cost=>cost.Amount!=row.Amount||cost.AdjustmentType!="Benefit"||cost.Taxable))throw new InvalidOperationException("An existing employer-cost reference differs from this import. Review it instead of duplicating or overwriting it.");
            if(matches.Length==1){unchanged++;continue;}
            pending.Add(new(){ClientId=request.ClientId,EmployeeId=row.EmployeeId,EmployeeCode=saved[row.EmployeeId].EmployeeCode,EmployeeName=saved[row.EmployeeId].EmployeeName,
                ComponentId=0,ComponentCode=component.Code,ComponentName=$"Extra period {component.Name}",AdjustmentType="Benefit",Amount=row.Amount,PayPeriod=request.PayPeriod,
                PayRunType="Regular",ReasonCode=EmployerCostReason,Notes=$"{prefix} {row.Notes.Trim()}",Taxable=false,Status="Applied",PayRunId=id});
        }
        if(pending.Count==0){await tx.RollbackAsync();return new(id,0,unchanged,0,run.NetPay);}
        await db.ExecuteAsync(@"INSERT INTO payrolladjustments(ClientId,EmployeeId,EmployeeName,EmployeeCode,ComponentId,ComponentCode,ComponentName,AdjustmentType,Amount,PayPeriod,PayRunType,ReasonCode,Notes,Taxable,Status,PayRunId)
VALUES(@ClientId,@EmployeeId,@EmployeeName,@EmployeeCode,@ComponentId,@ComponentCode,@ComponentName,@AdjustmentType,@Amount,@PayPeriod,@PayRunType,@ReasonCode,@Notes,@Taxable,@Status,@PayRunId)",pending,tx);
        var costs=(await db.QueryAsync<PayrollAdjustment>("SELECT * FROM payrolladjustments WHERE PayRunId=@Id AND ClientId=@ClientId AND ReasonCode=@Reason AND Status='Applied' AND AdjustmentType='Benefit'",new{Id=id,request.ClientId,Reason=EmployerCostReason},tx)).ToList();
        foreach(var employeeId in pending.Select(row=>row.EmployeeId).Distinct())
        {
            var row=saved[employeeId];row.DetailsJson=EmployerCostDetails(row.DetailsJson,costs.Where(cost=>cost.EmployeeId==employeeId));
            await SaveEmployeeAsync(db,tx,row);
            await db.ExecuteAsync("DELETE FROM payroll_calculation_traces WHERE PayRunId=@Id AND EmployeeId=@EmployeeId",new{Id=id,EmployeeId=employeeId},tx);
            await WriteCalculationTracesAsync(db,tx,row,run.TotalWorkingDays);
        }
        await RefreshTotalsAsync(db,tx,id);await WriteReconciliationResultsAsync(db,tx,id);
        var totals=await db.QuerySingleAsync<PayRun>("SELECT * FROM payruns WHERE Id=@Id",new{Id=id},tx);
        if(totals.NetPay!=run.NetPay||totals.PayrollCost!=run.PayrollCost)throw new InvalidOperationException("Employer costs cannot change employee cash earnings or net pay.");
        await db.ExecuteAsync(@"INSERT INTO payrun_step_logs(PayRunId,StepNumber,StepName,StartTime,EndTime,InputJson,OutputJson,Status,PerformedBy,MachineName)
VALUES(@Id,60,'Reviewed period employer costs',UTC_TIMESTAMP(),UTC_TIMESTAMP(),@Input,@Output,'Success',@Actor,@Machine)",
            new{Id=id,Input=JsonSerializer.Serialize(new{request.ClientId,request.PayPeriod,References=pending.Select(row=>row.Notes)}),Output=JsonSerializer.Serialize(new{Entries=pending.Count,Amount=pending.Sum(row=>row.Amount),CashNetUnchanged=run.NetPay}),Actor=actor,Machine=Environment.MachineName},tx);
        await tx.CommitAsync();return new(id,pending.Count,unchanged,pending.Sum(row=>row.Amount),run.NetPay);
    }
}
