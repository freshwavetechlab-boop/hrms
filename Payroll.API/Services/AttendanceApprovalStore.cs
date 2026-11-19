using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Services;

public sealed class AttendanceApprovalException(string message) : InvalidOperationException(message);

// Raw punches/attendance are retained as working data. A dated, immutable approval
// snapshot makes them final. Any subsequent input or reporting-line change makes
// that approval stale, rather than silently approving a different attendance sheet.
public static class AttendanceApprovalStore
{
    public const string ResourceType = "AttendancePeriod";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static DateTime MonthStart(string month) => !string.IsNullOrEmpty(month) && month.Length == 7 && DateTime.TryParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
        ? date : throw new AttendanceApprovalException("Select a valid attendance month.");

    internal static bool CanAccess(AuthUser user, int clientId) => user.Id > 0 && user.IsActive && !user.MustChangePassword && clientId > 0
        && SelectedClientAccess.Allows(user, clientId)
        && user.Permissions.Any(p => p is "mss.attendance.manage" or "mss.attendance.client.manage" or "settings.manage" or "attendance.manage");

    internal static bool CanReadAll(AuthUser user) => user.Permissions.Any(p => p is "mss.attendance.client.manage" or "settings.manage" or "attendance.manage");

    internal static async Task<AttendanceApprovalPolicy?> PolicyAsync(MySqlConnection db, MySqlTransaction? tx, int clientId, string month)
    {
        MonthStart(month);
        string? json;
        try { json = await db.ExecuteScalarAsync<string?>("SELECT CAST(SettingsJson AS CHAR) FROM modulesettings WHERE client_id=@ClientId AND ModuleCode=@Code", new { ClientId = clientId, Code = "attendance_approval_" + clientId }, tx); }
        catch (MySqlException error) when (error.Number == 1146) { return null; } // Existing installations without this optional module.
        if (string.IsNullOrWhiteSpace(json) || json == "null") return null;
        var policy = JsonSerializer.Deserialize<AttendanceApprovalPolicy>(json, Json) ?? throw new AttendanceApprovalException("Review the client attendance approval policy.");
        if (!policy.Enabled) return null;
        MonthStart(policy.EffectiveFromMonth);
        if (string.CompareOrdinal(month, policy.EffectiveFromMonth) < 0) return null;
        return policy;
    }

    internal static async Task<List<WorkflowStage>> StagesAsync(MySqlConnection db, MySqlTransaction? tx, int clientId, AttendanceApprovalPolicy policy)
    {
        var stages = (await db.QueryAsync<WorkflowStage>(@"SELECT s.* FROM workflowmasters w JOIN workflowstages s ON s.WorkflowId=w.Id
WHERE w.Id=@Id AND w.ClientId=@ClientId AND w.ResourceType=@ResourceType AND w.IsActive=TRUE ORDER BY s.StageOrder", new { Id = policy.WorkflowId, ClientId = clientId, ResourceType }, tx)).ToList();
        ValidateStages(stages);
        return stages;
    }

    internal static void ValidateStages(IReadOnlyList<WorkflowStage> stages)
    {
        if (stages.Count != 2 || stages[0].StageOrder < 1 || stages[0].StageOrder >= stages[1].StageOrder || stages[0].ApproverType != "Reporting Manager"
            || stages[1].ApproverType != "Specific User" || stages[1].ApproverUserId is not > 0)
            throw new AttendanceApprovalException("Configure exactly two attendance stages: Reporting Manager (PI), then the specific Project Cell Approver. Payroll remains blocked.");
    }

    internal sealed class Snapshot
    {
        public int ClientId { get; set; }
        public int EmployeeId { get; set; }
        public string EmployeeCode { get; set; } = "";
        public string EmployeeName { get; set; } = "";
        public string Month { get; set; } = "";
        public string WorkLocation { get; set; } = "";
        public int WorkLocationId { get; set; }
        public string Department { get; set; } = "";
        public string Designation { get; set; } = "";
        public int? PiUserId { get; set; }
        public string PiName { get; set; } = "";
        public string DateOfJoining { get; set; } = "";
        public bool IsActive { get; set; }
        public int StartDay { get; set; }
        public int EndDay { get; set; }
        public string WorkWeek { get; set; } = "";
        public decimal? PresentDays { get; set; }
        public decimal? PayableDays { get; set; }
        public int DailyCount { get; set; }
        public string AttendanceHash { get; set; } = "";
        public string ProjectReference { get; set; } = "";
        public int ProjectCellUserId { get; set; }
        public bool ProjectRollVerified { get; set; }
        public int ProjectRollVerifiedBy { get; set; }
    }

    internal static string Hash(object data) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data, Json))));
    internal static string Key(int clientId, int employeeId, string month) => $"{clientId}:{employeeId}:{month}";

    internal static (DateTime Start, DateTime End) Cycle(string month, int startDay, int endDay)
    {
        var start = MonthStart(month);
        var first = startDay <= endDay ? start : start.AddMonths(-1);
        return (first.AddDays(Math.Clamp(startDay, 1, DateTime.DaysInMonth(first.Year, first.Month)) - 1), start.AddDays(Math.Clamp(endDay, 1, DateTime.DaysInMonth(start.Year, start.Month)) - 1));
    }

    internal static async Task<Dictionary<int, Snapshot>> SnapshotsAsync(MySqlConnection db, MySqlTransaction? tx, int clientId, string month, int[]? employeeIds = null)
    {
        var start = MonthStart(month);
        var people = (await db.QueryAsync<Snapshot>(@"SELECT e.Id EmployeeId,e.ClientId,e.EmployeeCode,CONCAT(e.FirstName,' ',e.LastName) EmployeeName,
e.WorkLocationId,COALESCE(w.Name,'') WorkLocation,e.Department,e.Designation,COALESCE(CAST(e.DateOfJoining AS CHAR),'') DateOfJoining,e.IsActive,
e.ReportingManagerUserId PiUserId,COALESCE(u.DisplayName,'') PiName,
COALESCE(g.attendance_cycle_start_day,1) StartDay,
COALESCE(g.attendance_cycle_end_day,DAY(LAST_DAY(@Start))) EndDay,
COALESCE(g.work_week,lp.work_week,p.work_week,'') WorkWeek
FROM employees e LEFT JOIN worklocations w ON w.Id=e.WorkLocationId LEFT JOIN authusers u ON u.Id=e.ReportingManagerUserId
LEFT JOIN (SELECT membership.employee_id,MIN(ag.id) GroupId FROM attendance_group_employees membership JOIN attendance_groups ag ON ag.id=membership.attendance_group_id AND ag.client_id=@ClientId AND ag.is_active=TRUE GROUP BY membership.employee_id) gm ON gm.employee_id=e.Id
LEFT JOIN attendance_groups g ON g.id=gm.GroupId
LEFT JOIN leave_attendance_preferences p ON p.client_id=e.ClientId AND p.work_location_id=0
LEFT JOIN leave_attendance_preferences lp ON lp.client_id=e.ClientId AND lp.work_location_id=e.WorkLocationId AND lp.work_location_id>0
WHERE e.ClientId=@ClientId AND " + EmployeeEmploymentRules.EligibleInMonth("e", "@Month") + (employeeIds is null ? "" : " AND e.Id IN @Ids") + " ORDER BY e.EmployeeCode,e.Id",
            new { ClientId = clientId, Month = month, Start = start, Ids = employeeIds }, tx)).ToList();
        if (people.Count == 0) return [];
        var ids = people.Select(p => p.EmployeeId).ToArray();
        var monthly = (await db.QueryAsync<EmployeeMonthlyAttendance>(@"SELECT employee_id EmployeeId,working_days WorkingDays,present_days PresentDays,payable_days PayableDays,lop_days LopDays,source_type SourceType,COALESCE(remarks,'') Remarks FROM employee_monthly_attendance WHERE client_id=@ClientId AND attendance_month=@Month AND employee_id IN @Ids", new { ClientId = clientId, Month = month, Ids = ids }, tx)).ToDictionary(r => r.EmployeeId);
        // Native payroll can use calendar-month legacy days or a dated policy
        // cycle. Preserve both sets so changing either cannot reuse an old approval.
        var readStart = new[] { start, people.Min(p => Cycle(month, p.StartDay, p.EndDay).Start) }.Min();
        var readEnd = new[] { start.AddMonths(1), people.Max(p => Cycle(month, p.StartDay, p.EndDay).End).AddDays(1) }.Max();
        var daily = (await db.QueryAsync<EmployeeDailyAttendance>(@"SELECT employee_id EmployeeId,attendance_date AttendanceDate,status Status,payable_value PayableValue,check_in_time CheckInTime,check_out_time CheckOutTime,total_hours TotalHours,COALESCE(remarks,'') Remarks FROM employee_daily_attendance WHERE client_id=@ClientId AND employee_id IN @Ids AND attendance_date>=@Start AND attendance_date<@End ORDER BY employee_id,attendance_date", new { ClientId = clientId, Ids = ids, Start = readStart, End = readEnd }, tx)).ToLookup(r => r.EmployeeId);
        var integration = await AttendanceIntegrationRepository.ReadAsync(db, tx, clientId);
        foreach (var person in people)
        {
            person.Month = month;
            var range = Cycle(month, person.StartDay, person.EndDay);
            var days = daily[person.EmployeeId].Where(d => d.AttendanceDate >= range.Start && d.AttendanceDate <= range.End || d.AttendanceDate >= start && d.AttendanceDate < start.AddMonths(1)).ToArray();
            var totals = monthly.GetValueOrDefault(person.EmployeeId);
            person.PresentDays = totals?.PresentDays; person.PayableDays = totals?.PayableDays; person.DailyCount = days.Length;
            person.AttendanceHash = Hash(new { Person = new { person.ClientId, person.EmployeeId, person.WorkLocationId, person.Department, person.Designation, person.PiUserId, person.DateOfJoining, person.IsActive, person.StartDay, person.EndDay, person.WorkWeek }, Month = month, Totals = totals, Days = days, integration.Rules, integration.WeeklyOffRuleVersions });
        }
        return people.ToDictionary(p => p.EmployeeId);
    }

    internal static Snapshot ReadSnapshot(WorkflowInstance instance) => JsonSerializer.Deserialize<Snapshot>(instance.PayloadJson, Json) ?? throw new AttendanceApprovalException("Attendance approval has no reviewed snapshot.");

    internal static async Task ValidateApproversAsync(MySqlConnection db, MySqlTransaction? tx, int clientId, int? piId, int cellId)
    {
        if (piId is not > 0 || piId == cellId) throw new AttendanceApprovalException("Assign a PI and a different Project Cell Approver to this employee.");
        var count = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM authusers u WHERE u.IsActive=TRUE AND u.ClientId=@ClientId AND
((u.Id=@Pi AND EXISTS(SELECT 1 FROM authuserroles ur JOIN authrolepermissions rp ON rp.RoleId=ur.RoleId JOIN authpermissions p ON p.Id=rp.PermissionId WHERE ur.UserId=u.Id AND p.Code='mss.attendance.manage')) OR
(u.Id=@Cell AND EXISTS(SELECT 1 FROM authuserroles ur JOIN authrolepermissions rp ON rp.RoleId=ur.RoleId JOIN authpermissions p ON p.Id=rp.PermissionId WHERE ur.UserId=u.Id AND p.Code='mss.attendance.client.manage')))", new { ClientId = clientId, Pi = piId, Cell = cellId }, tx);
        if (count != 2) throw new AttendanceApprovalException("Both approvers must be active ICMR-scoped users with their respective PI and Project Cell permissions.");
    }

    internal static string State(WorkflowInstance? instance, Snapshot current, IReadOnlyList<WorkflowStage> stages, IReadOnlyList<WorkflowTask> tasks)
    {
        if (instance is null) return "Not submitted";
        var cellUserId = stages[1].ApproverUserId!.Value;
        var saved = ReadSnapshot(instance);
        if (saved.AttendanceHash != current.AttendanceHash || saved.PiUserId != current.PiUserId || saved.ProjectCellUserId != cellUserId) return "Changed — resubmit";
        if (instance.Status != "Approved") return instance.Status == "Pending" ? tasks.Any(t => t.Status == "Approved") ? "Pending Project Cell" : "Pending PI" : instance.Status;
        var approvals = tasks.Where(t => t.Status == "Approved").ToArray();
        return saved.ProjectRollVerified && saved.ProjectRollVerifiedBy == cellUserId && approvals.Length == 2 && approvals.Select(t => t.ApproverUserId).Distinct().Count() == 2
            && approvals.Any(t => t.ApproverUserId == saved.PiUserId && t.StageId == stages[0].Id) && approvals.Any(t => t.ApproverUserId == cellUserId && t.StageId == stages[1].Id)
            ? "Approved" : "Approval evidence incomplete";
    }

    internal static async Task<List<WorkflowInstance>> InstancesAsync(MySqlConnection db, MySqlTransaction? tx, int clientId, string month) =>
        (await db.QueryAsync<WorkflowInstance>(@"SELECT i.* FROM workflowinstances i JOIN workflowmasters w ON w.Id=i.WorkflowId
WHERE w.ClientId=@ClientId AND i.ResourceType=@ResourceType AND JSON_UNQUOTE(JSON_EXTRACT(i.PayloadJson,'$.month'))=@Month ORDER BY i.Id DESC", new { ClientId = clientId, ResourceType, Month = month }, tx)).ToList();

    internal static async Task<List<WorkflowTask>> TasksAsync(MySqlConnection db, MySqlTransaction? tx, IReadOnlyList<WorkflowInstance> instances) => instances.Count == 0 ? [] :
        (await db.QueryAsync<WorkflowTask>("SELECT * FROM workflowtasks WHERE InstanceId IN @Ids ORDER BY StageId,Id", new { Ids = instances.Select(i => i.Id).ToArray() }, tx)).ToList();

    internal static async Task<Dictionary<int, string>> StatesAsync(MySqlConnection db, MySqlTransaction? tx, int clientId, string month, IEnumerable<int> employeeIds)
    {
        var policy = await PolicyAsync(db, tx, clientId, month);
        if (policy is null) return [];
        var ids = employeeIds.Distinct().ToArray();
        if (ids.Length == 0) return [];
        var stages = await StagesAsync(db, tx, clientId, policy);
        var current = await SnapshotsAsync(db, tx, clientId, month, ids);
        var instances = await InstancesAsync(db, tx, clientId, month);
        var tasks = await TasksAsync(db, tx, instances);
        var result = new Dictionary<int, string>();
        foreach (var id in ids)
        {
            if (!current.TryGetValue(id, out var snapshot)) { result[id] = "Employee is not on the eligible client roll"; continue; }
            var instance = instances.FirstOrDefault(i => i.ResourceId == Key(clientId, id, month));
            result[id] = State(instance, snapshot, stages, tasks.Where(t => t.InstanceId == instance?.Id).ToArray());
        }
        return result;
    }

    public static async Task RequireApprovedAsync(MySqlConnection db, MySqlTransaction? tx, int clientId, string month, IEnumerable<int> employeeIds)
    {
        var states = await StatesAsync(db, tx, clientId, month, employeeIds);
        var blocked = states.Where(pair => pair.Value != "Approved").Select(pair => $"employee #{pair.Key} ({pair.Value})").ToList();
        if (blocked.Count > 0) throw new AttendanceApprovalException($"Attendance for {month} requires PI and Project Cell approval before payroll. Pending or changed: {string.Join(", ", blocked.Take(8))}{(blocked.Count > 8 ? $" (+{blocked.Count - 8} more)" : "")}.");
    }

    internal static async Task ValidateActionAsync(MySqlConnection db, MySqlTransaction tx, WorkflowInstance instance, WorkflowTask task, int actor, string action, bool projectRollVerified)
    {
        var saved = ReadSnapshot(instance);
        await db.ExecuteScalarAsync<int>("SELECT Id FROM clients WHERE Id=@Id FOR UPDATE", new { Id = saved.ClientId }, tx);
        var policy = await PolicyAsync(db, tx, saved.ClientId, saved.Month) ?? throw new AttendanceApprovalException("This attendance approval policy is no longer active. Review configuration.");
        if (policy.WorkflowId != instance.WorkflowId || instance.ResourceId != Key(saved.ClientId, saved.EmployeeId, saved.Month)) throw new AttendanceApprovalException("Attendance approval scope does not match its employee and client.");
        var stages = await StagesAsync(db, tx, saved.ClientId, policy);
        if (saved.ProjectCellUserId != stages[1].ApproverUserId) throw new AttendanceApprovalException("Project Cell assignment changed. Submit attendance again for both current approvers.");
        var expected = task.StageId == stages[0].Id ? saved.PiUserId : task.StageId == stages[1].Id ? saved.ProjectCellUserId : null;
        if (expected != actor) throw new AttendanceApprovalException("This attendance approval belongs to the assigned PI or Project Cell Approver.");
        await ValidateApproversAsync(db, tx, saved.ClientId, saved.PiUserId, stages[1].ApproverUserId!.Value);
        if (task.StageId == stages[1].Id && !await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM workflowtasks WHERE InstanceId=@Id AND StageId=@Stage AND ApproverUserId=@Pi AND Status='Approved')", new { Id = instance.Id, Stage = stages[0].Id, Pi = saved.PiUserId }, tx)) throw new AttendanceApprovalException("PI approval is required before Project Cell approval.");
        if (action != "Approved") return;
        var current = (await SnapshotsAsync(db, tx, saved.ClientId, saved.Month, [saved.EmployeeId])).GetValueOrDefault(saved.EmployeeId);
        if (current is null || current.AttendanceHash != saved.AttendanceHash) throw new AttendanceApprovalException("Attendance or employee assignment changed after submission. Review and submit the current attendance again.");
        if (actor == saved.ProjectCellUserId && !projectRollVerified) throw new AttendanceApprovalException("Verify the employee's project roll and valid employment/contract period before final approval.");
        if (await EssMssRepository.IsLeavePeriodLockedAsync(db, tx, saved.ClientId, saved.EmployeeId, MonthStart(saved.Month), MonthStart(saved.Month).AddMonths(1).AddDays(-1))) throw new AttendanceApprovalException("Attendance belongs to a locked payroll period.");
    }
}
