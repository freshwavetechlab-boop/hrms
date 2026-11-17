using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public partial class PayRunRepository
{
    // Internal, caller-transaction-owned data, never a request/model snapshot or
    // cross-request cache. A new phase is required after historical writes.
    internal sealed class HistoricalSimulationContext
    {
        internal MySqlConnection Connection { get; }
        internal MySqlTransaction Transaction { get; }
        internal int ClientId { get; }
        internal string Period { get; }
        internal string Mode { get; }
        internal object State { get; }
        internal Dictionary<int,string> AssignmentHashes { get; } = [];
        internal Dictionary<int,ExceptionDispatchInfo> PreparationErrors { get; } = [];
        internal HashSet<int> DeferredPt { get; } = [];
        internal HashSet<int> DeferredPf { get; } = [];
        internal HashSet<int> Consumed { get; } = [];
        internal HashSet<int> Selected { get; }
        internal bool Fallback { get; set; }
        internal HistoricalSimulationContext(MySqlConnection db,MySqlTransaction tx,int clientId,string period,string mode,object state,int[] ids)
        {Connection=db;Transaction=tx;ClientId=clientId;Period=period;Mode=mode;State=state;Selected=ids.ToHashSet();}
    }

    private static string HistoricalAssignmentHash(HistoricalSalarySimulationAssignment assignment) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(assignment))));

    internal async Task<HistoricalSimulationContext> CreateHistoricalSimulationContextAsync(MySqlConnection db,MySqlTransaction tx,
        int clientId,string period,IReadOnlyList<int> employeeIds,IReadOnlyList<HistoricalSalarySimulationAssignment>? assignments=null,
        string? exactSetupJson=null,bool controlBasisOnly=false)
    {
        var ids=employeeIds.ToArray();
        if(tx.Connection!=db||clientId<=0||ids.Length is <1 or >100||ids.Any(id=>id<=0)||ids.Distinct().Count()!=ids.Length
            ||controlBasisOnly&&assignments is not null||assignments is not null&&(assignments.Count!=ids.Length
                ||assignments.Select(a=>a.EmployeeId).Distinct().Count()!=ids.Length||assignments.Any(a=>a.ClientId!=clientId||a.PayPeriod!=period||!ids.Contains(a.EmployeeId))))
            throw new InvalidOperationException("Historical simulation context requires one exact bounded client/period/transaction roster.");
        ValidateSimulationRequest(new(){Mode="Employee",ClientId=clientId,EmployeeId=ids[0],PayPeriod=period});
        var employees=(await db.QueryAsync<PayRunSourceEmployee>(@"SELECT e.*,c.Name ClientName,COALESCE(w.State,'') WorkState
FROM employees e JOIN clients c ON c.Id=e.ClientId LEFT JOIN worklocations w ON w.Id=e.WorkLocationId
WHERE e.ClientId=@ClientId AND e.Id IN @Ids AND "+EmployeeEmploymentRules.EligibleInMonth("e","@Period"),new{ClientId=clientId,Ids=ids,Period=period},tx)).ToList();
        var setupJson=exactSetupJson??await PayrollDataTableStore.GetSetupJsonAsync(db,tx);
        var map=employees.ToDictionary(e=>e.Id,e=>e.HistoricalContextClone());
        var data=new FormulaWorkbookContext(map,setupJson,[],[],null!);
        var result=new HistoricalSimulationContext(db,tx,clientId,period,controlBasisOnly?"ActualControlBasis":assignments is null?"Saved":"Proposed",data,ids);
        if(assignments is not null)foreach(var assignment in assignments)result.AssignmentHashes[assignment.EmployeeId]=HistoricalAssignmentHash(assignment);
        try{await LoadEmployeeTablesAsync(db,tx,employees,period,loadReviewedProfessionalTax:false);}
        catch(InvalidOperationException){result.Fallback=true;return result;}
        map.Clear();foreach(var employee in employees)map.Add(employee.Id,employee.HistoricalContextClone());
        if(controlBasisOnly)return result;
        try
        {
            foreach(var row in await GetSelectedPayRunAttendanceAsync(db,tx,clientId,period,ids))data.Attendance.Add(row.EmployeeId,row);
        }
        catch(InvalidOperationException){result.Fallback=true;return result;}
        var adjustmentRows=await GetApplicableAdjustmentsAsync(db,tx,clientId,period,"Regular",[]);
        data.Adjustments.AddRange(adjustmentRows);
        var tax=await LoadTdsProjectionAvailabilityAsync(db,tx,clientId,period);
        data=data with{TaxAvailability=tax};
        // Recreate only the wrapper; the owned maps are not shared with another
        // phase and every selected employee is an independent source clone.
        var complete=new HistoricalSimulationContext(db,tx,clientId,period,result.Mode,data,ids);
        foreach(var pair in result.AssignmentHashes)complete.AssignmentHashes.Add(pair.Key,pair.Value);
        result=complete;
        if(assignments is not null)
        {
            foreach(var assignment in assignments)
            {
                if(!map.TryGetValue(assignment.EmployeeId,out var employee))continue;
                try
                {
                    ApplyHistoricalSimulationAssignment(employee,assignment,clientId,period);
                    if(assignment.ProfessionalTax is { } pt)
                        pt.Evidence=await PrepareHistoricalConditionalPtAsync(db,tx,employee,setupJson,period,pt);
                    else await LoadReviewedProfessionalTaxAsync(db,tx,[employee],period);
                }
                catch(InvalidOperationException error){result.PreparationErrors[employee.Id]=ExceptionDispatchInfo.Capture(error);}
            }
        }
        else
        {
            try{await LoadReviewedProfessionalTaxAsync(db,tx,map.Values.ToList(),period);}
            catch(InvalidOperationException)
            {
                // A corrupt control for one row must not change a different
                // row's previous success/error outcome. Defer to its old reader.
                foreach(var employee in map.Values){employee.ReviewedProfessionalTax=null;result.DeferredPt.Add(employee.Id);}
            }
        }
        var pfRows=map.Values.Where(e=>!result.PreparationErrors.ContainsKey(e.Id)).ToList();
        foreach(var employee in pfRows)employee.PayableOnlyAttendance=data.Attendance.GetValueOrDefault(employee.Id)?.PayableOnlyEvidence;
        try{await LoadPfPolicyPeriodsAsync(db,tx,pfRows,period,setupJson);}
        catch(InvalidOperationException)
        {
            foreach(var employee in pfRows){employee.PfPolicyPeriod=null;result.DeferredPf.Add(employee.Id);}
        }
        return result;
    }

    private static void ApplyHistoricalSimulationAssignment(PayRunSourceEmployee employee,HistoricalSalarySimulationAssignment assignment,int clientId,string period)
    {
        if(assignment.ClientId!=clientId||assignment.EmployeeId!=employee.Id||assignment.PayPeriod!=period||string.IsNullOrWhiteSpace(assignment.TemplateId)
            ||assignment.AnnualSalaryTarget<=0||assignment.AnnualSalaryTarget>1_000_000_000m
            ||assignment.SalaryInputs.Any(input=>input.Value<0||input.Value>1_000_000_000m)||employee.LocationParts is not null)
            throw new InvalidOperationException("Historical salary proof needs an exact existing employee/period, bounded annual target and source inputs without location allocations.");
        employee.SalaryStructureId=assignment.TemplateId;employee.AnnualCtc=assignment.AnnualSalaryTarget;
        employee.SalaryComponents=assignment.SalaryInputs.ToDictionary(p=>p.Key,p=>p.Value,StringComparer.OrdinalIgnoreCase);
        employee.SalaryJson=JsonSerializer.Serialize(employee.SalaryComponents);
        employee.NamedInputs=assignment.NamedInputs;
    }

    private static FormulaWorkbookContext? UseHistoricalSimulationContext(MySqlConnection db,MySqlTransaction tx,PayrollSimulationRequest request,
        HistoricalSalarySimulationAssignment? assignment,HistoricalSimulationContext context,string? expectedMode=null)
    {
        if(context.Connection!=db||context.Transaction!=tx||tx.Connection!=db||context.ClientId!=request.ClientId||context.Period!=request.PayPeriod
            ||request.EmployeeId is not int id||!context.Selected.Contains(id)||context.State is not FormulaWorkbookContext data
            ||expectedMode is not null&&context.Mode!=expectedMode||expectedMode is null&&(assignment is null?context.Mode!="Saved":context.Mode!="Proposed")
            ||assignment is not null&&(!context.AssignmentHashes.TryGetValue(id,out var hash)||hash!=HistoricalAssignmentHash(assignment))
            ||!context.Consumed.Add(id))
            throw new InvalidOperationException("The historical simulation context is stale, consumed, foreign or belongs to a different calculation phase.");
        if(context.Fallback)return null;
        return data;
    }
}
