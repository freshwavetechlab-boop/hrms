using System.Data;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public partial class PayRunRepository
{
    private sealed record FormulaWorkbookContext(Dictionary<int,PayRunSourceEmployee> Employees,string SetupJson,
        Dictionary<int,PayRunAttendance> Attendance,List<PayrollAdjustment> Adjustments,TdsProjectionAvailability TaxAvailability);

    internal sealed record FormulaWorkbookPolicyGroup(int Id,int ClientId,string PolicyBatchId);
    internal static void ValidateFormulaWorkbookRequest(PayrollFormulaWorkbookRequest request)
    {
        ValidateSimulationRequest(new(){Mode="Employee",ClientId=request.ClientId,EmployeeId=1,PayPeriod=request.PayPeriod});
        if(string.IsNullOrWhiteSpace(request.AttendancePolicyBatchId)||request.AttendancePolicyBatchId.Length>64
            ||request.AttendanceGroupIds is null||request.AttendanceGroupIds.Count is <1 or >1000
            ||request.AttendanceGroupIds.Any(id=>id<=0)||request.AttendanceGroupIds.Distinct().Count()!=request.AttendanceGroupIds.Count)
            throw new InvalidOperationException("Select an attendance policy before downloading Excel with formulas.");
        if(request.EmployeeIds is null||request.EmployeeIds.Count is <1 or >1000||request.EmployeeIds.Any(id=>id<=0)
            ||request.EmployeeIds.Distinct().Count()!=request.EmployeeIds.Count)
            throw new InvalidOperationException("Select 1–1000 distinct employees from the attendance policy for the formula workbook.");
    }

    internal static void RequireFormulaWorkbookPolicy(PayrollFormulaWorkbookRequest request,IReadOnlyList<FormulaWorkbookPolicyGroup> groups,IReadOnlySet<int> members)
    {
        ValidateFormulaWorkbookRequest(request);
        var selected=groups.Where(g=>g.ClientId==request.ClientId
            &&(string.IsNullOrWhiteSpace(g.PolicyBatchId)?$"group:{g.Id}":g.PolicyBatchId)==request.AttendancePolicyBatchId.Trim()).Select(g=>g.Id).ToHashSet();
        if(!selected.SetEquals(request.AttendanceGroupIds))
            throw new InvalidOperationException("The selected attendance policy changed or does not belong to this client. Refresh and select it again.");
        if(request.EmployeeIds.Any(id=>!members.Contains(id)))
            throw new InvalidOperationException("Select only employees belonging to the selected attendance policy. No workbook was created.");
    }

    public async Task<IReadOnlyList<PayrollFormulaWorkbookEmployee>> FormulaWorkbookAsync(PayrollFormulaWorkbookRequest request,CancellationToken token)
    {
        ValidateFormulaWorkbookRequest(request);
        await using var db=CreateConnection(); await db.OpenAsync(token);
        await using var tx=await db.BeginTransactionAsync(IsolationLevel.RepeatableRead,isReadOnly:true,token);
        var policyGroups=(await db.QueryAsync<FormulaWorkbookPolicyGroup>(new CommandDefinition(@"SELECT g.id Id,g.client_id ClientId,COALESCE(g.policy_batch_id,'') PolicyBatchId
FROM attendance_groups g JOIN clients c ON c.Id=g.client_id AND c.IsActive=TRUE WHERE g.client_id=@ClientId AND g.is_active=TRUE",new{request.ClientId},tx,cancellationToken:token))).ToList();
        var members=(await db.QueryAsync<int>(new CommandDefinition(@"SELECT DISTINCT m.employee_id FROM attendance_group_employees m
JOIN attendance_groups g ON g.id=m.attendance_group_id AND g.client_id=@ClientId AND g.is_active=TRUE
WHERE g.id IN @GroupIds AND (COALESCE(g.policy_batch_id,'')=@PolicyBatchId OR (COALESCE(g.policy_batch_id,'')='' AND CONCAT('group:',g.id)=@PolicyBatchId))",
            new{request.ClientId,GroupIds=request.AttendanceGroupIds.ToArray(),PolicyBatchId=request.AttendancePolicyBatchId.Trim()},tx,cancellationToken:token))).ToHashSet();
        RequireFormulaWorkbookPolicy(request,policyGroups,members);
        var rows=(await db.QueryAsync<PayRunSourceEmployee>(new CommandDefinition(@"SELECT e.*,c.Name ClientName,COALESCE(w.State,'') WorkState
FROM employees e JOIN clients c ON c.Id=e.ClientId LEFT JOIN worklocations w ON w.Id=e.WorkLocationId
WHERE e.ClientId=@ClientId AND e.Id IN @Ids AND "+EmployeeEmploymentRules.EligibleInMonth("e","@PayPeriod")+" ORDER BY e.EmployeeCode,e.Id",
            new {request.ClientId,request.PayPeriod,Ids=request.EmployeeIds.ToArray()},tx,cancellationToken:token))).ToList();
        if(rows.Count!=request.EmployeeIds.Count)throw new InvalidOperationException("The selected roster includes an unavailable employee or another client's employee. Refresh the roster.");
        await LoadEmployeeTablesAsync(db,tx,rows,request.PayPeriod);
        var attendance=(await GetPayRunAttendanceAsync(db,tx,request.ClientId,request.PayPeriod)).ToDictionary(row=>row.EmployeeId);
        var missing=rows.Where(row=>!attendance.ContainsKey(row.Id)).ToArray();
        if(missing.Length>0)throw new InvalidOperationException($"Attendance is missing for {missing.Length} selected employees in {request.PayPeriod}. Save/review attendance before downloading the formula workbook; no days are invented.");
        var json=await PayrollDataTableStore.GetSetupJsonAsync(db,tx);
        await LoadPfPolicyPeriodsAsync(db,tx,rows,request.PayPeriod,json);
        var adjustments=await GetApplicableAdjustmentsAsync(db,tx,request.ClientId,request.PayPeriod,"Regular",[]);
        var employmentError=await EmployeeAttendanceEligibility.ValidateMonthlyWithOptionalPresenceAsync(db,tx,request.ClientId,request.PayPeriod,
            rows.Select(row=>(row.Id,attendance[row.Id].PresentDays,attendance[row.Id].PayableDays)),lockEmployees:false);
        if(employmentError is not null)throw new InvalidOperationException(employmentError);
        var taxAvailability=await LoadTdsProjectionAvailabilityAsync(db,tx,request.ClientId,request.PayPeriod);
        var data=new FormulaWorkbookContext(rows.ToDictionary(row=>row.Id),json,attendance,adjustments,taxAvailability);
        var output=new List<PayrollFormulaWorkbookEmployee>();
        foreach(var employee in rows)
        {
            token.ThrowIfCancellationRequested();
            var calculation=await SimulateEmployeeInTransactionAsync(db,tx,new(){Mode="Employee",ClientId=request.ClientId,
                EmployeeId=employee.Id,PayPeriod=request.PayPeriod},token,workbook:data);
            if(!calculation.Complete)throw new InvalidOperationException($"{employee.EmployeeCode}: "+string.Join(" ",calculation.Issues.Where(issue=>issue.Severity=="Error").Select(issue=>issue.Message)));
            if(employee.LocationParts is not null)throw new InvalidOperationException($"{employee.EmployeeCode}: per-location payroll needs its separate attendance inputs; an aggregate attendance workbook cannot represent it.");
            if(calculation.Lines.Any(line=>line.Source.Contains("Effective-dated PF",StringComparison.OrdinalIgnoreCase)
                || line.Source.StartsWith("Projected TDS",StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"{employee.EmployeeCode}: dated PF/projected TDS needs the online payroll calculator. Offline formulas are not exported as frozen policy results.");
            var setup=ReadPayrollSetup(json);
            if(ApplicableProfessionalTaxSlabs(employee,setup,request.PayPeriod).Any() && !calculation.Lines.Any(line=>(line.Code is "PT" or "PT_LWF_WC") && line.CalculationType.Length>0)
                && !ReadSeededPayrollDeductions(employee).Any(line=>line.Code is "PT" or "PT_LWF_WC"))
                throw new InvalidOperationException($"{employee.EmployeeCode}: professional tax uses live policy slabs. Configure an explicit template rule before relying on offline attendance changes.");
            output.Add(new(employee.Id,employee.EmployeeCode,($"{employee.FirstName} {employee.LastName}").Trim(),calculation,attendance[employee.Id].PayableOnlyEvidence?.ReviewedExtraDutyUnits==true));
        }
        await tx.RollbackAsync(token); return output;
    }
}
