using System.Data;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public partial class PayRunRepository
{
    private sealed record SourceAttendanceReceipt(int ActorId,string Fingerprint,SourceMonthAttendanceResult Result);
    internal sealed record SourceAttendanceInsert(int EmployeeId,int ClientId,string Month,int Divisor,decimal Payable,
        string SourceType,string Remarks);
    private async Task<SourceMonthAttendancePreview> BuildSourceMonthAttendanceAsync(MySqlConnection db,MySqlTransaction tx,
        SourceMonthAttendanceRequest request,AuthUser actor,bool locked)
    {
        SourceMonthAttendanceReview.Validate(request,EmployeeEmploymentRules.Today,locked);
        var preview=new SourceMonthAttendancePreview {Request=request};
        if(!configuration.GetValue("Payroll:PayableOnlyAttendanceEnabled",false)||!await SourcePayableAttendance.SchemaReadyAsync(db,tx))
            preview.Blockers.Add("The compatible payable-only API/worker rollout and nullable-presence schema must be ready before this source import. No migration or runtime setting is changed here.");
        var source=await ReadPtSourceAsync(db,tx,request.ClientId,request.SourceBatchId,locked);
        if(source.Batch.Month!=request.PayPeriod||request.SourceHash.Length>0&&request.SourceHash!=source.Hash)
            preview.Blockers.Add("The saved source month or snapshot changed. Refresh this exact batch.");
        var ids=request.Rows.Select(row=>row.EmployeeId).Order().ToArray();
        var employees=(await db.QueryAsync<PayRunSourceEmployee>(@"SELECT e.*,COALESCE(w.State,'') WorkState FROM employees e
LEFT JOIN worklocations w ON w.Id=e.WorkLocationId AND w.ClientId=e.ClientId
WHERE e.ClientId=@ClientId AND e.Id IN @Ids AND "+EmployeeEmploymentRules.EligibleInMonth("e","@Month")+(locked?" FOR UPDATE":""),
            new {request.ClientId,Ids=ids,Month=request.PayPeriod},tx)).ToList();
        if(employees.Count!=ids.Length)preview.Blockers.Add("A selected source employee is missing, outside this client or ineligible in the month.");
        if(locked)await LockPtNativeInputsAsync(db,tx,request.ClientId,request.PayPeriod,ids);
        await LoadEmployeeTablesAsync(db,tx,employees,request.PayPeriod,loadReviewedProfessionalTax:false);
        if(locked)await LockPtConfigurationAsync(db,tx,employees);
        var setupJson=await PayrollDataTableStore.GetSetupJsonAsync(db,tx);var setup=ReadPayrollSetup(setupJson);
        await LoadPfPolicyPeriodsAsync(db,tx,employees,request.PayPeriod,setupJson);
        var cycle=NativeAttendanceCycle(request.PayPeriod,request.CycleStartDay,request.CycleEndDay);
        var monthly=(await db.QueryAsync<int>("SELECT employee_id FROM employee_monthly_attendance WHERE client_id=@ClientId AND attendance_month=@Month AND employee_id IN @Ids",new {request.ClientId,Month=request.PayPeriod,Ids=ids},tx)).ToHashSet();
        var daily=(await db.QueryAsync<int>("SELECT DISTINCT employee_id FROM employee_daily_attendance WHERE client_id=@ClientId AND employee_id IN @Ids AND attendance_date BETWEEN @Start AND @End",new {request.ClientId,Ids=ids,Start=cycle.Start,End=cycle.End},tx)).ToHashSet();
        var memberships=(await db.QueryAsync<int>(@"SELECT m.employee_id FROM attendance_group_employees m JOIN attendance_groups g ON g.id=m.attendance_group_id
WHERE g.client_id=@ClientId AND g.is_active=TRUE AND m.employee_id IN @Ids GROUP BY m.employee_id HAVING COUNT(*)>1",new {request.ClientId,Ids=ids},tx)).ToHashSet();
        var integration=await AttendanceIntegrationRepository.ReadAsync(db,tx,request.ClientId);
        if(integration.WeeklyOffRuleVersions.Any(version=>version.EffectiveFrom.Date<=DateTime.Parse(cycle.End).Date))
            preview.Blockers.Add("Published dated weekly-off rules require actual dated attendance. Source salary units cannot establish presence or leave entitlement.");
        if(await db.ExecuteScalarAsync<bool>(@"SELECT EXISTS(SELECT 1 FROM payruns r WHERE r.ClientId=@ClientId AND r.PayPeriod=@Month
AND (r.Status IN ('AwaitingProcessing','Queued','Processing') OR EXISTS(SELECT 1 FROM payrunemployees p WHERE p.PayRunId=r.Id AND p.EmployeeId IN @Ids)))",
            new {request.ClientId,Month=request.PayPeriod,Ids=ids},tx))
            preview.Blockers.Add("A selected employee already has a saved payroll result, or a run is queued/processing. Existing payroll and attendance are retained.");
        foreach(var row in request.Rows)
        {
            var errors=new List<string>();var employee=employees.SingleOrDefault(value=>value.Id==row.EmployeeId);
            if(employee is null)continue;
            var divisor=0;decimal payable=0;
            try
            {
                RequirePtIdentity(source,employee,row.SourceRowId,request.PayPeriod,true);
                var captured=source.Batch.Rows.Single(value=>value.Id==row.SourceRowId);
                (divisor,payable)=SourceMonthAttendanceReview.ReadUnits(source.Source,captured.SourceRow,request.DivisorColumn,request.PayableColumn,request.ReviewedExtraPaidUnits);
                employee.PayableOnlyAttendance=new(request.ReviewedExtraPaidUnits?2:1,request.ClientId,employee.Id,request.PayPeriod,
                    request.SourceBatchId,row.SourceRowId,new('b',32),source.Hash,divisor,payable,ReviewedExtraDutyUnits:request.ReviewedExtraPaidUnits);
                ValidatePayableOnlyCalculation(employee,setup,divisor,payable);
            }
            catch(InvalidOperationException error){errors.Add(error.Message);}
            if(monthly.Contains(employee.Id))errors.Add("Existing monthly attendance is retained; this import is insert-only.");
            if(daily.Contains(employee.Id))errors.Add("Existing dated attendance is retained; source totals cannot replace it.");
            if(memberships.Contains(employee.Id))errors.Add("Resolve the ambiguous active attendance-policy membership first.");
            preview.Rows.Add(new(employee.Id,employee.EmployeeCode,row.SourceRowId,divisor,payable,errors));
        }
        request.SourceHash=source.Hash;
        preview.PlanHash=ReviewedPeriodProfessionalTax.Hash(new {actor.Id,Request=request,preview.Rows,preview.Blockers,
            Native=employees.OrderBy(employee=>employee.Id).Select(employee=>new {employee.Id,Basis=PtBasis(employee,setupJson,request.PayPeriod),employee.PfPolicyPeriod,employee.AttendancePolicySnapshot}),
            Monthly=monthly.Order(),Daily=daily.Order(),AmbiguousMemberships=memberships.Order(),cycle});
        return preview;
    }
    public async Task<SourceMonthAttendancePreview> PreviewSourceMonthAttendanceAsync(SourceMonthAttendanceRequest request,AuthUser actor)
    {
        RequirePtActor(actor,request.ClientId);
        await using var db=CreateConnection();await db.OpenAsync();
        await using var tx=await db.BeginTransactionAsync(IsolationLevel.RepeatableRead,isReadOnly:true);
        var result=await BuildSourceMonthAttendanceAsync(db,tx,request,actor,false);await tx.RollbackAsync();return result;
    }
    public async Task<SourceMonthAttendanceResult> SaveSourceMonthAttendanceAsync(SaveSourceMonthAttendanceRequest command,AuthUser actor)
    {
        RequirePtActor(actor,command.Request.ClientId);SourceMonthAttendanceReview.Validate(command.Request,EmployeeEmploymentRules.Today,true);
        if(!command.Acknowledged||!Guid.TryParse(command.RequestId,out var parsed)||!ReviewedPeriodProfessionalTax.ValidHash(command.ExpectedPlanHash))
            throw new InvalidOperationException("Acknowledge the exact source attendance preview before saving.");
        var requestId=parsed.ToString("N");var code="payroll.source.attendance.receipt:"+requestId;
        var fingerprint=ReviewedPeriodProfessionalTax.Hash(new {actor.Id,command.Request,command.ExpectedPlanHash});
        await using var db=CreateConnection();await db.OpenAsync();await using var tx=await db.BeginTransactionAsync(IsolationLevel.Serializable);
        if(await db.ExecuteScalarAsync<int?>("SELECT Id FROM clients WHERE Id=@ClientId AND IsActive=TRUE FOR UPDATE",new {command.Request.ClientId},tx) is null)
            throw new InvalidOperationException("Select an active client.");
        var prior=await db.ExecuteScalarAsync<string?>("SELECT CAST(SettingsJson AS CHAR) FROM modulesettings WHERE client_id=@ClientId AND ModuleCode=@Code FOR UPDATE",new {command.Request.ClientId,Code=code},tx);
        if(prior is not null)
        {
            var receipt=ReadPt<SourceAttendanceReceipt>(prior,"SourceAttendanceReceipt");
            if(receipt.ActorId!=actor.Id||receipt.Fingerprint!=fingerprint)throw new InvalidOperationException("This attendance request reference belongs to another scope.");
            await tx.RollbackAsync();return receipt.Result with {Replayed=true};
        }
        var preview=await BuildSourceMonthAttendanceAsync(db,tx,command.Request,actor,true);
        if(!preview.CanSave||preview.PlanHash!=command.ExpectedPlanHash)
            throw new InvalidOperationException("Source, employee, policy, attendance or payroll state changed. Refresh the source attendance preview; no rows were imported.");
        var employment=await EmployeeAttendanceEligibility.ValidateMonthlyWithOptionalPresenceAsync(db,tx,command.Request.ClientId,command.Request.PayPeriod,
            preview.Rows.Select(row=>(row.EmployeeId,(decimal?)null,row.PayableUnits)));
        if(employment is not null)throw new InvalidOperationException(employment);
        var inserts=preview.Rows.Select(row=>new SourceAttendanceInsert(row.EmployeeId,command.Request.ClientId,command.Request.PayPeriod,row.Divisor,row.PayableUnits,
            SourcePayableAttendance.SourceType,SourcePayableAttendance.Encode(new(command.Request.ReviewedExtraPaidUnits?2:1,command.Request.ClientId,row.EmployeeId,
                command.Request.PayPeriod,command.Request.SourceBatchId,row.SourceRowId,requestId,command.Request.SourceHash,row.Divisor,row.PayableUnits,
                ReviewedExtraDutyUnits:command.Request.ReviewedExtraPaidUnits)))).ToArray();
        await WriteSourceMonthAttendanceAsync(db,tx,inserts);
        var result=new SourceMonthAttendanceResult(requestId,inserts.Length,false);
        await db.ExecuteAsync("INSERT INTO modulesettings(client_id,ModuleCode,IsEnabled,SettingsJson) VALUES(@ClientId,@Code,TRUE,CAST(@Json AS JSON))",
            new {command.Request.ClientId,Code=code,Json=ProtectPt(new SourceAttendanceReceipt(actor.Id,fingerprint,result),"SourceAttendanceReceipt")},tx);
        await tx.CommitAsync();return result;
    }
    internal static async Task WriteSourceMonthAttendanceAsync(IDbConnection db,IDbTransaction tx,IReadOnlyList<SourceAttendanceInsert> rows)
    {
        if(db.State!=ConnectionState.Open||!ReferenceEquals(tx.Connection,db)||rows.Count is <1 or >100
            ||rows.Select(row=>row.EmployeeId).Distinct().Count()!=rows.Count||rows.Select(row=>(row.ClientId,row.Month)).Distinct().Count()!=1)
            throw new InvalidOperationException("Source attendance requires one reviewed transactional client/month and unique employees.");
        foreach(var row in rows)
        {
            var evidence=SourcePayableAttendance.Read(row.Remarks,row.ClientId,row.EmployeeId,row.Month,row.Divisor,row.Payable);
            if(row.SourceType!=SourcePayableAttendance.SourceType||evidence.Origin is not null)throw new InvalidOperationException("Unexpected attendance source.");
            if(await db.ExecuteAsync(@"INSERT INTO employee_monthly_attendance
(client_id,employee_id,attendance_month,working_days,present_days,payable_days,lop_days,source_type,remarks)
VALUES(@ClientId,@EmployeeId,@Month,@Divisor,NULL,@Payable,@Lop,@SourceType,@Remarks)",
                new {row.ClientId,row.EmployeeId,row.Month,row.Divisor,row.Payable,Lop=Math.Max(0,row.Divisor-row.Payable),row.SourceType,row.Remarks},tx)!=1)
                throw new InvalidOperationException("Source attendance insert could not be confirmed.");
        }
    }
}
