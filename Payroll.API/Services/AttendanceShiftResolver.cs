using System.Data;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Services;

// Load once for a batch; resolve by employee and shift start date without per-row queries.
public sealed class AttendanceShiftResolver(AttendanceSettings settings, IEnumerable<AttendanceShift> shifts, IReadOnlyDictionary<int, int> employeeShiftIds,
    IReadOnlyList<EmployeeShiftPolicy>? policies = null, IReadOnlyList<AttendanceShiftAssignment>? assignments = null)
{
    internal const string SettingsSql = @"SELECT id Id,client_id ClientId,shift_id ShiftId,check_in_time CheckInTime,check_out_time CheckOutTime,
working_hours_calculation WorkingHoursCalculation,minimum_hours_for_half_day MinimumHoursForHalfDay,
minimum_hours_for_full_day MinimumHoursForFullDay,maximum_hours_allowed_for_full_day MaximumHoursAllowedForFullDay,
allow_regularization_requests AllowRegularizationRequests,regularization_window RegularizationWindow,past_days_allowed PastDaysAllowed,
restrict_regularization_requests_per_month RestrictRegularizationRequestsPerMonth,max_regularization_requests_per_month MaxRegularizationRequestsPerMonth,
created_at CreatedAt,updated_at UpdatedAt FROM attendance_settings WHERE client_id=@ClientId LIMIT 1";
    public AttendanceSettings Settings => settings;
    public IReadOnlyDictionary<int, int> EmployeeShiftIds => employeeShiftIds;
    public IReadOnlyList<EmployeeShiftPolicy> Policies { get; } = policies ?? [];
    public IReadOnlyList<AttendanceShiftAssignment> Assignments { get; } = assignments ?? [];
    private readonly Dictionary<int, EmployeeShiftPolicy[]> policyByEmployee = (policies ?? []).GroupBy(p => p.EmployeeId).ToDictionary(g => g.Key, g => g.ToArray());
    private readonly Dictionary<int, AttendanceShiftAssignment[]> rosterByEmployee = (assignments ?? []).Where(a => a.ClientId == settings.ClientId).GroupBy(a => a.EmployeeId).ToDictionary(g => g.Key, g => g.ToArray());
    public IReadOnlyList<AttendanceShift> Shifts { get; } = shifts.Where(shift => shift.ClientId == settings.ClientId).ToArray();

    public static async Task<AttendanceShiftResolver> LoadAsync(MySqlConnection db, IDbTransaction? tx, int clientId, int? employeeId = null, int? reportingManagerUserId = null)
    {
        var settings = await db.QueryFirstOrDefaultAsync<AttendanceSettings>(SettingsSql, new { ClientId = clientId }, tx) ?? new() { ClientId = clientId };
        settings.Rules = (await AttendanceIntegrationRepository.ReadAsync(db, tx, clientId)).Rules;
        var shifts = await db.QueryAsync<AttendanceShift>(LeaveAttendanceRepository.ShiftSelectSql + " WHERE client_id=@ClientId", new { ClientId = clientId }, tx);
        var policies = (await db.QueryAsync<EmployeeShiftPolicy>(@"SELECT age.employee_id EmployeeId,g.client_id ClientId,g.shift_id ShiftId,g.attendance_mode AttendanceMode,COALESCE(g.allowed_shift_ids_json,'[]') AllowedShiftIdsJson
FROM attendance_group_employees age JOIN attendance_groups g ON g.id=age.attendance_group_id AND g.client_id=@ClientId AND g.is_active=TRUE
JOIN employees e ON e.Id=age.employee_id AND e.ClientId=@ClientId AND e.IsActive=TRUE
WHERE (@EmployeeId IS NULL OR e.Id=@EmployeeId) AND (@ManagerId IS NULL OR e.ReportingManagerUserId=@ManagerId) ORDER BY g.id",
            new { ClientId = clientId, EmployeeId = employeeId, ManagerId = reportingManagerUserId }, tx)).ToList();
        var roster = (await db.QueryAsync<AttendanceShiftAssignment>(LeaveAttendanceRepository.ShiftAssignmentSelect + " WHERE a.client_id=@ClientId AND a.is_active=TRUE AND (@EmployeeId IS NULL OR e.Id=@EmployeeId) AND (@ManagerId IS NULL OR e.ReportingManagerUserId=@ManagerId)", new { ClientId = clientId, EmployeeId = employeeId, ManagerId = reportingManagerUserId }, tx)).ToList();
        return new(settings, shifts, policies.Where(p => p.ShiftId.HasValue).GroupBy(row => row.EmployeeId).ToDictionary(group => group.Key, group => group.First().ShiftId!.Value), policies, roster);
    }

    public AttendanceSettings Resolve(int employeeId, DateTime date)
    {
        AttendanceShift? Applicable(int? id) => Shifts.FirstOrDefault(shift => shift.Id == id && shift.IsActive
            && shift.EffectiveFrom.Date <= date.Date && (shift.EffectiveTo is null || shift.EffectiveTo.Value.Date >= date.Date));
        var matched = policyByEmployee.GetValueOrDefault(employeeId) ?? [];
        var policy = matched.FirstOrDefault();
        AttendanceSettings Missing(string message) => new() { ClientId = settings.ClientId, Rules = settings.Rules, ResolutionError = $"Employee {employeeId}, {date:yyyy-MM-dd}: {message}" };
        if (matched.Length > 1 && matched.Any(p => p.AttendanceMode != "Legacy")) return Missing("multiple attendance policies apply. Resolve employee policy coverage.");
        if (policy?.AttendanceMode == "Monthly") return new() { ClientId = settings.ClientId, Rules = settings.Rules, ResolutionError = "Monthly attendance policy does not accept punch-based calculation. Use monthly attendance entry." };
        var roster = (rosterByEmployee.GetValueOrDefault(employeeId) ?? []).Where(a => a.Applies(date)).ToArray();
        if (roster.Length > 0 && policy?.AttendanceMode != "Punch") return Missing("roster assignment has no matching punch-based attendance policy.");
        if (roster.Length > 1) return Missing("overlapping shift assignments. Correct the roster.");
        var selectedId = roster.FirstOrDefault()?.ShiftId ?? policy?.ShiftId;
        if (policy?.AttendanceMode == "Punch" && (!selectedId.HasValue || !policy.AllowedShiftIds.Contains(selectedId.Value))) return Missing("no allowed shift is assigned. Assign a dated roster or a policy default shift.");
        var shift = policy?.AttendanceMode == "Punch" ? Applicable(selectedId) : Applicable(employeeShiftIds.TryGetValue(employeeId, out var id) ? id : null) ?? Applicable(settings.ShiftId);
        if (policy?.AttendanceMode == "Punch" && shift is null) return Missing("assigned shift is inactive or outside its effective dates. Correct the assignment; no fallback was used.");
        if (shift is null) return settings;
        return new AttendanceSettings
        {
            Id = settings.Id, ClientId = settings.ClientId, ShiftId = shift.Id, Shift = shift,
            RequiresPunches = policy?.AttendanceMode == "Punch",
            CheckInTime = shift.StartTime ?? TimeSpan.Zero, CheckOutTime = shift.EndTime ?? TimeSpan.Zero,
            WorkingHoursCalculation = shift.Rules.HoursMethod == "Pairs" ? "Every valid check-in and check-out" : shift.Rules.HoursMethod == "Span" ? "First check-in and last check-out" : settings.WorkingHoursCalculation,
            MinimumHoursForFullDay = shift.MinimumFullDayHours, MinimumHoursForHalfDay = shift.MinimumHalfDayHours,
            MaximumHoursAllowedForFullDay = shift.Rules.MaximumHours ?? Math.Max(settings.MaximumHoursAllowedForFullDay, shift.MinimumFullDayHours),
            Rules = settings.Rules
        };
    }

    public (DateTime Date, AttendanceSettings Settings) ResolvePunch(int employeeId, DateTime local)
    {
        var date = local < Boundary(employeeId, local.Date) ? local.Date.AddDays(-1) : local.Date;
        return (date, Resolve(employeeId, date));
    }

    public (DateTime Start, DateTime End) Bounds(int employeeId, DateTime date) => (Boundary(employeeId, date.Date), Boundary(employeeId, date.Date.AddDays(1)));

    private DateTime Boundary(int employeeId, DateTime date)
    {
        var previous = Resolve(employeeId, date.AddDays(-1));
        var current = Resolve(employeeId, date);
        if (current.Shift?.Rules.DayBoundary is TimeSpan cutoff) return date + cutoff;
        if (previous.Shift?.Rules.DayBoundary is TimeSpan previousCutoff) return date + previousCutoff;
        static bool Overnight(AttendanceSettings value) => value.Shift?.IsOvernight ?? value.CheckOutTime < value.CheckInTime;
        if (Overnight(previous))
        {
            // Split the gap to the next applicable shift, including a night-to-day effective-date change.
            var nextStart = current.CheckInTime > previous.CheckOutTime ? current.CheckInTime : previous.CheckInTime;
            return date + previous.CheckOutTime + (nextStart - previous.CheckOutTime) / 2;
        }
        return Overnight(current) ? AttendancePunchCalculator.Bounds(date, current).Start : date;
    }
}
