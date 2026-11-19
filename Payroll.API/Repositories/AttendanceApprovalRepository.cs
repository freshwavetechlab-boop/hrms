using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed class AttendanceApprovalRepository(IConfiguration configuration)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private MySqlConnection Db() => new(configuration.GetConnectionString("Default"));

    public async Task<object> ConfigureAsync(AuthUser user, ConfigureAttendanceApproval request)
    {
        if (!AttendanceApprovalStore.CanAccess(user, request.ClientId) || !user.Permissions.Contains("settings.manage")) throw new UnauthorizedAccessException();
        var effective = AttendanceApprovalStore.MonthStart(request.EffectiveFromMonth);
        await using var db = Db(); await db.OpenAsync(); await using var tx = await db.BeginTransactionAsync();
        await db.ExecuteScalarAsync<int>("SELECT Id FROM clients WHERE Id=@Id FOR UPDATE", new { Id = request.ClientId }, tx);
        var policy = new AttendanceApprovalPolicy { Enabled = request.Enabled, WorkflowId = request.WorkflowId, EffectiveFromMonth = request.EffectiveFromMonth };
        var stages = request.Enabled ? await AttendanceApprovalStore.StagesAsync(db, tx, request.ClientId, policy) : [];
        var assigned = 0;
        if (request.AssignUnassignedEmployees)
        {
            if (!request.Enabled) throw new AttendanceApprovalException("Configure an active approval policy before assigning a PI.");
            await AttendanceApprovalStore.ValidateApproversAsync(db, tx, request.ClientId, request.DefaultPiUserId, stages[1].ApproverUserId!.Value);
            await db.ExecuteAsync(@"INSERT INTO employee_audit_trail(EmployeeId,EmployeeCode,ActionType,InfotypeCode,FieldName,OldValue,NewValue,EffectiveFrom,ChangedBy)
SELECT Id,EmployeeCode,'Attendance approval setup','IT0001','ReportingManagerUserId',COALESCE(CAST(ReportingManagerUserId AS CHAR),''),CAST(@Pi AS CHAR),@Effective,@Actor
FROM employees WHERE ClientId=@ClientId AND IsActive=TRUE AND COALESCE(ReportingManagerUserId,0)=0", new { request.ClientId, Pi = request.DefaultPiUserId, Effective = effective, Actor = user.Email }, tx);
            assigned = await db.ExecuteAsync("UPDATE employees SET ReportingManagerUserId=@Pi WHERE ClientId=@ClientId AND IsActive=TRUE AND COALESCE(ReportingManagerUserId,0)=0", new { request.ClientId, Pi = request.DefaultPiUserId }, tx);
        }
        // A dedicated, client-qualified module key also works with legacy global
        // ModuleCode indexes. Never overwrite another client's attendance settings.
        await db.ExecuteAsync(@"INSERT INTO modulesettings(client_id,ModuleCode,IsEnabled,SettingsJson) VALUES(@ClientId,@Code,@Enabled,CAST(@Json AS JSON))
ON DUPLICATE KEY UPDATE IsEnabled=@Enabled,SettingsJson=CAST(@Json AS JSON)", new { request.ClientId, Code = "attendance_approval_" + request.ClientId, request.Enabled, Json = JsonSerializer.Serialize(policy, Json) }, tx);
        if (request.Enabled && await AttendanceApprovalStore.PolicyAsync(db, tx, request.ClientId, request.EffectiveFromMonth) is not { Enabled: true })
            throw new AttendanceApprovalException("Attendance approval policy was not persisted in this client scope. No configuration changes were committed.");
        await tx.CommitAsync();
        return new { request.ClientId, policy.Enabled, policy.WorkflowId, policy.EffectiveFromMonth, AssignedEmployeeCount = assigned };
    }

    public async Task<AttendanceApprovalContext> ContextAsync(AuthUser user, int clientId, string month, int? workLocationId = null)
    {
        if (!AttendanceApprovalStore.CanAccess(user, clientId)) throw new UnauthorizedAccessException();
        await using var db = Db(); await db.OpenAsync();
        await using var tx = await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, isReadOnly: true);
        var context = new AttendanceApprovalContext { Month = month };
        var policy = await AttendanceApprovalStore.PolicyAsync(db, tx, clientId, month);
        if (policy is null) return context;
        context.Enabled = true;
        context.Message = "Working attendance is final for payroll only after PI and Project Cell approval. Changes require fresh approval.";
        var stages = await AttendanceApprovalStore.StagesAsync(db, tx, clientId, policy);
        var snapshots = await AttendanceApprovalStore.SnapshotsAsync(db, tx, clientId, month);
        var instances = await AttendanceApprovalStore.InstancesAsync(db, tx, clientId, month);
        var tasks = await AttendanceApprovalStore.TasksAsync(db, tx, instances);
        foreach (var current in snapshots.Values.Where(s => (!workLocationId.HasValue || workLocationId <= 0 || s.WorkLocationId == workLocationId) && (AttendanceApprovalStore.CanReadAll(user) || s.PiUserId == user.Id)))
        {
            var instance = instances.FirstOrDefault(i => i.ResourceId == AttendanceApprovalStore.Key(clientId, current.EmployeeId, month));
            var history = tasks.Where(t => t.InstanceId == instance?.Id).ToArray();
            var pending = history.FirstOrDefault(t => t.Status == "Pending");
            var status = AttendanceApprovalStore.State(instance, current, stages, history);
            context.Rows.Add(new() { EmployeeId = current.EmployeeId, EmployeeCode = current.EmployeeCode, EmployeeName = current.EmployeeName,
                WorkLocation = current.WorkLocation, Designation = current.Designation, PiUserId = current.PiUserId, PiName = current.PiName,
                Status = status, InstanceId = instance?.Id, TaskId = pending?.Id, CanApprove = pending?.ApproverUserId == user.Id && status is "Pending PI" or "Pending Project Cell",
                RequiresRollVerification = pending?.StageId == stages[1].Id, PresentDays = current.PresentDays, PayableDays = current.PayableDays,
                CanSubmit = current.PiUserId > 0 && (current.DailyCount > 0 || current.PayableDays.HasValue) && status is not ("Approved" or "Pending PI" or "Pending Project Cell"),
                ProjectReference = instance is null ? "" : AttendanceApprovalStore.ReadSnapshot(instance).ProjectReference,
                Detail = current.PiUserId is not > 0 ? "Assign a PI before submitting attendance." : current.DailyCount == 0 && !current.PayableDays.HasValue ? "Save actual attendance first; no days are assumed." : status == "Changed — resubmit" ? "Attendance or employee assignment changed. Both approvals are required again." : "" });
        }
        return context;
    }

    public async Task<AttendanceApprovalContext> SubmitAsync(AuthUser user, SubmitAttendanceApproval request)
    {
        if (!AttendanceApprovalStore.CanAccess(user, request.ClientId)) throw new UnauthorizedAccessException();
        if (request.EmployeeIds.Count is < 1 or > 250 || request.EmployeeIds.Any(id => id <= 0) || request.EmployeeIds.Distinct().Count() != request.EmployeeIds.Count)
            throw new AttendanceApprovalException("Select 1–250 distinct employees to submit.");
        if (string.IsNullOrWhiteSpace(request.ProjectReference) || request.ProjectReference.Trim().Length > 180)
            throw new AttendanceApprovalException("Enter the actual project or work-order reference for this attendance submission (maximum 180 characters).");
        await using var db = Db(); await db.OpenAsync();
        await using var tx = await db.BeginTransactionAsync();
        await db.ExecuteScalarAsync<int>("SELECT Id FROM clients WHERE Id=@Id FOR UPDATE", new { Id = request.ClientId }, tx);
        var policy = await AttendanceApprovalStore.PolicyAsync(db, tx, request.ClientId, request.Month) ?? throw new AttendanceApprovalException("Two-stage attendance approval is not enabled for this client and month.");
        var stages = await AttendanceApprovalStore.StagesAsync(db, tx, request.ClientId, policy);
        var current = await AttendanceApprovalStore.SnapshotsAsync(db, tx, request.ClientId, request.Month, request.EmployeeIds.ToArray());
        if (current.Count != request.EmployeeIds.Count || current.Values.Any(s => !AttendanceApprovalStore.CanReadAll(user) && s.PiUserId != user.Id)) throw new UnauthorizedAccessException();
        var instances = await AttendanceApprovalStore.InstancesAsync(db, tx, request.ClientId, request.Month);
        var tasks = await AttendanceApprovalStore.TasksAsync(db, tx, instances);
        foreach (var snapshot in current.Values)
        {
            await AttendanceApprovalStore.ValidateApproversAsync(db, tx, request.ClientId, snapshot.PiUserId, stages[1].ApproverUserId!.Value);
            if (snapshot.DailyCount == 0 && !snapshot.PayableDays.HasValue) throw new AttendanceApprovalException($"{snapshot.EmployeeCode}: save the actual attendance first. Missing attendance is not assumed to be full month or zero.");
            var range = AttendanceApprovalStore.Cycle(request.Month, snapshot.StartDay, snapshot.EndDay);
            if (await EssMssRepository.IsLeavePeriodLockedAsync(db, tx, request.ClientId, snapshot.EmployeeId, range.Start, range.End)) throw new AttendanceApprovalException("This attendance period is already locked by payroll.");
            var key = AttendanceApprovalStore.Key(request.ClientId, snapshot.EmployeeId, request.Month);
            var previous = instances.FirstOrDefault(i => i.ResourceId == key);
            if (AttendanceApprovalStore.State(previous, snapshot, stages, tasks.Where(t => t.InstanceId == previous?.Id).ToArray()) is "Approved" or "Pending PI" or "Pending Project Cell")
                throw new AttendanceApprovalException($"{snapshot.EmployeeCode}: this attendance is already approved or pending approval. It was not submitted twice.");
            if (previous?.Status == "Pending")
            {
                await db.ExecuteAsync("UPDATE workflowinstances SET Status='Superseded',CompletedAt=UTC_TIMESTAMP() WHERE Id=@Id; UPDATE workflowtasks SET Status='Superseded',ActionedAt=UTC_TIMESTAMP() WHERE InstanceId=@Id AND Status='Pending'; INSERT INTO workflowhistory(InstanceId,Action,ActorUserId,Comment) VALUES(@Id,'Superseded',@Actor,'Attendance changed; a fresh approval snapshot was submitted.')", new { previous.Id, Actor = user.Id }, tx);
            }
            snapshot.ProjectReference = request.ProjectReference.Trim(); snapshot.ProjectCellUserId = stages[1].ApproverUserId!.Value;
            var id = await db.ExecuteScalarAsync<long>(@"INSERT INTO workflowinstances(WorkflowId,ResourceType,ResourceId,RequestorUserId,PayloadJson,Status) VALUES(@WorkflowId,@ResourceType,@ResourceId,@Actor,@Payload,'Pending'); SELECT LAST_INSERT_ID();",
                new { WorkflowId = policy.WorkflowId, AttendanceApprovalStore.ResourceType, ResourceId = key, Actor = user.Id, Payload = JsonSerializer.Serialize(snapshot, Json) }, tx);
            await db.ExecuteAsync(@"INSERT INTO workflowtasks(InstanceId,StageId,ApproverUserId) VALUES(@Id,@Stage,@Pi);
INSERT INTO workflowhistory(InstanceId,Action,ActorUserId,Comment) VALUES(@Id,'Submitted',@Actor,'Attendance submitted for PI then Project Cell approval.');
INSERT INTO ResourceStates(ResourceType,ResourceId,CurrentState,WorkflowInstanceId,CreatedBy,ModifiedBy) VALUES(@ResourceType,@ResourceId,'Pending',@Id,@Actor,@Actor)
ON DUPLICATE KEY UPDATE CurrentState='Pending',WorkflowInstanceId=@Id,ModifiedBy=@Actor,ModifiedOn=CURRENT_TIMESTAMP;",
                new { Id = id, Stage = stages[0].Id, Pi = snapshot.PiUserId, Actor = user.Id, AttendanceApprovalStore.ResourceType, ResourceId = key }, tx);
        }
        await tx.CommitAsync();
        return await ContextAsync(user, request.ClientId, request.Month);
    }
}
