using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using System.Data;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public partial class LeaveAttendanceRepository
{
    internal const string ShiftAssignmentSchema = @"CREATE TABLE IF NOT EXISTS attendance_shift_assignments (
id INT PRIMARY KEY AUTO_INCREMENT,client_id INT NOT NULL,employee_id INT NOT NULL,shift_id INT NOT NULL,
effective_from DATE NOT NULL,effective_to DATE NOT NULL,week_days VARCHAR(20) NOT NULL DEFAULT '0,1,2,3,4,5,6',
reason VARCHAR(500) NOT NULL,is_active BOOLEAN NOT NULL DEFAULT TRUE,created_by INT NOT NULL,created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
cancelled_by INT NULL,cancelled_at DATETIME NULL,
INDEX ix_shift_assignment_scope(client_id,employee_id,effective_from,effective_to),
CONSTRAINT fk_shift_assignment_employee FOREIGN KEY(employee_id) REFERENCES employees(Id),
CONSTRAINT fk_shift_assignment_shift FOREIGN KEY(shift_id,client_id) REFERENCES attendance_shifts(id,client_id));";
    internal const string ShiftAssignmentSelect = @"SELECT a.id Id,a.client_id ClientId,a.employee_id EmployeeId,a.shift_id ShiftId,
a.effective_from EffectiveFrom,a.effective_to EffectiveTo,a.week_days WeekDays,a.reason Reason,a.is_active IsActive,
e.EmployeeCode,CONCAT(e.FirstName,' ',e.LastName) EmployeeName,s.shift_name ShiftName
FROM attendance_shift_assignments a JOIN employees e ON e.Id=a.employee_id AND e.ClientId=a.client_id
JOIN attendance_shifts s ON s.id=a.shift_id AND s.client_id=a.client_id";

    public static string? ValidatePolicyShifts(AttendancePolicyShiftConfig config, int? defaultShift)
    {
        if (config.AttendanceMode is not ("Legacy" or "Punch" or "Monthly")) return "Choose punch-based or monthly attendance mode.";
        if (config.AllowedShiftIds is null || config.AllowedShiftIds.Count > 100 || config.AllowedShiftIds.Any(id => id <= 0)) return "Choose valid allowed shifts (maximum 100).";
        if (config.AttendanceMode == "Punch" && config.AllowedShiftIds.Count == 0) return "Punch-based attendance requires at least one allowed shift.";
        if (config.AttendanceMode == "Punch" && defaultShift.HasValue && !config.AllowedShiftIds.Contains(defaultShift.Value)) return "Default shift must be one of the allowed shifts.";
        if (config.AttendanceMode == "Monthly" && (defaultShift.HasValue || config.AllowedShiftIds.Count > 0)) return "Monthly attendance does not use shift assignments. Clear the selected shifts.";
        return null;
    }

    private static async Task<string?> ValidatePolicyShiftsAsync(MySqlConnection db, IDbTransaction tx, int clientId, AttendancePolicyShiftConfig config, int? defaultShift)
    {
        var error = ValidatePolicyShifts(config, defaultShift); if (error is not null) return error;
        var ids = config.AllowedShiftIds.Distinct().ToArray();
        if (ids.Length > 0 && await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM attendance_shifts WHERE client_id=@ClientId AND id IN @Ids AND is_active=TRUE", new { ClientId = clientId, Ids = ids }, tx) != ids.Length)
            return "Allowed shifts must be active shifts belonging to this client.";
        return null;
    }

    private static Task SavePolicyShiftsAsync(MySqlConnection db, IDbTransaction tx, int clientId, int groupId, AttendancePolicyShiftConfig config) =>
        db.ExecuteAsync("UPDATE attendance_groups SET attendance_mode=@AttendanceMode,allowed_shift_ids_json=@AllowedShiftIdsJson WHERE id=@Id AND client_id=@ClientId", new { Id = groupId, ClientId = clientId, config.AttendanceMode, config.AllowedShiftIdsJson }, tx);

    private static async Task<string?> ValidateRosterCoverageAsync(MySqlConnection db, IDbTransaction tx, int clientId)
    {
        var invalid = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM attendance_shift_assignments a
WHERE a.client_id=@ClientId AND a.is_active=TRUE AND a.effective_to>=CURRENT_DATE()
AND (SELECT COUNT(*) FROM attendance_group_employees e JOIN attendance_groups g ON g.id=e.attendance_group_id
WHERE e.employee_id=a.employee_id AND g.client_id=a.client_id AND g.is_active=TRUE AND g.attendance_mode='Punch'
AND JSON_CONTAINS(COALESCE(g.allowed_shift_ids_json,JSON_ARRAY()),CAST(a.shift_id AS JSON)))<>1", new { ClientId = clientId }, tx);
        return invalid > 0 ? "This policy change would leave active roster assignments without an allowed shift. Cancel or replace those future assignments before changing policy coverage." : null;
    }

    public async Task<IEnumerable<AttendanceShiftAssignment>> GetShiftAssignmentsAsync(int clientId, DateTime from, DateTime to)
    {
        if (clientId <= 0 || to < from || (to - from).TotalDays > 366) throw new InvalidOperationException("Select a client and a date range up to one year.");
        await using var db = CreateConnection(); await db.OpenAsync();
        return await db.QueryAsync<AttendanceShiftAssignment>(ShiftAssignmentSelect + " WHERE a.client_id=@ClientId AND a.effective_from<=@To AND a.effective_to>=@From ORDER BY a.effective_from,e.EmployeeCode,a.id", new { ClientId = clientId, From = from.Date, To = to.Date });
    }

    public async Task<IEnumerable<object>> GetRosterEmployeesAsync(int clientId, int shiftId, string? search)
    {
        await using var db = CreateConnection(); await db.OpenAsync();
        var rows = await db.QueryAsync(@"SELECT DISTINCT e.Id id,e.EmployeeCode code,CONCAT(e.FirstName,' ',e.LastName) name
FROM employees e JOIN attendance_group_employees a ON a.employee_id=e.Id
JOIN attendance_groups g ON g.id=a.attendance_group_id AND g.client_id=e.ClientId AND g.is_active=TRUE
WHERE e.ClientId=@ClientId AND e.IsActive=TRUE AND g.attendance_mode='Punch'
AND JSON_CONTAINS(COALESCE(g.allowed_shift_ids_json,JSON_ARRAY()),CAST(@ShiftId AS JSON))
AND (@Search='' OR e.EmployeeCode LIKE @Pattern OR CONCAT(e.FirstName,' ',e.LastName) LIKE @Pattern)
ORDER BY e.EmployeeCode LIMIT 200", new { ClientId = clientId, ShiftId = shiftId.ToString(), Search = search?.Trim() ?? "", Pattern = "%" + (search?.Trim() ?? "") + "%" });
        return rows.Cast<object>();
    }

    public static string? ValidateShiftAssignment(SaveShiftAssignmentsRequest request)
    {
        if (request.ClientId <= 0 || request.ShiftId <= 0 || request.EmployeeIds is null || request.EmployeeIds.Count is 0 or > 1000) return "Select a client, shift and 1-1000 employees.";
        if (request.EffectiveFrom.Year < 1900 || request.EffectiveTo.Year > 9998 || request.EffectiveTo < request.EffectiveFrom || (request.EffectiveTo - request.EffectiveFrom).TotalDays > 366) return "Select a valid assignment period up to one year.";
        var days = (request.WeekDays ?? "").Split(',');
        if (days.Length == 0 || days.Distinct().Count() != days.Length || days.Any(d => d is not ("0" or "1" or "2" or "3" or "4" or "5" or "6"))) return "Select unique weekdays for the assignment.";
        if (!Enumerable.Range(0, Math.Min(7, (request.EffectiveTo.Date - request.EffectiveFrom.Date).Days + 1)).Any(i => request.Applies(request.EffectiveFrom.Date.AddDays(i)))) return "No selected weekday occurs in the assignment period.";
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 500) return "Enter an assignment reason (maximum 500 characters).";
        return null;
    }

    public static bool AssignmentsOverlap(AttendanceShiftAssignment a, AttendanceShiftAssignment b)
    {
        var from = a.EffectiveFrom > b.EffectiveFrom ? a.EffectiveFrom.Date : b.EffectiveFrom.Date;
        var to = a.EffectiveTo < b.EffectiveTo ? a.EffectiveTo.Date : b.EffectiveTo.Date;
        return to >= from && Enumerable.Range(0, Math.Min(7, (to - from).Days + 1)).Any(i => a.Applies(from.AddDays(i)) && b.Applies(from.AddDays(i)));
    }

    public static bool ShiftWindowsOverlap(AttendanceShift? first, DateTime firstDate, AttendanceShift? second, DateTime secondDate)
    {
        if (first?.ShiftType != "Fixed" || second?.ShiftType != "Fixed" || first.StartTime is null || first.EndTime is null || second.StartTime is null || second.EndTime is null) return false;
        var firstStart = firstDate.Date + first.StartTime.Value;
        var firstEnd = firstDate.Date.AddDays(first.IsOvernight ? 1 : 0) + first.EndTime.Value;
        var secondStart = secondDate.Date + second.StartTime.Value;
        var secondEnd = secondDate.Date.AddDays(second.IsOvernight ? 1 : 0) + second.EndTime.Value;
        return firstStart < secondEnd && secondStart < firstEnd;
    }

    public async Task<int> SaveShiftAssignmentsAsync(SaveShiftAssignmentsRequest request, int actor)
    {
        request.IsActive = true;
        request.EffectiveFrom = request.EffectiveFrom.Date;
        request.EffectiveTo = request.EffectiveTo.Date;
        var error = ValidateShiftAssignment(request); if (error is not null) throw new InvalidOperationException(error);
        var ids = request.EmployeeIds.Distinct().Order().ToArray();
        await using var db = CreateConnection(); await db.OpenAsync(); await using var tx = await db.BeginTransactionAsync();
        // Serialize roster/policy edits per client, including the empty-roster case.
        await db.ExecuteScalarAsync<int>("SELECT Id FROM clients WHERE Id=@ClientId FOR UPDATE", request, tx);
        var count = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employees WHERE ClientId=@ClientId AND IsActive=TRUE AND Id IN @Ids", new { request.ClientId, Ids = ids }, tx);
        if (count != ids.Length) throw new InvalidOperationException("Every selected employee must be active and belong to this client.");
        var shift = await db.QuerySingleOrDefaultAsync<AttendanceShift>(ShiftSelectSql + " WHERE id=@ShiftId AND client_id=@ClientId AND is_active=TRUE", request, tx);
        if (shift is null || shift.EffectiveFrom.Date > request.EffectiveFrom.Date || shift.EffectiveTo?.Date < request.EffectiveTo.Date) throw new InvalidOperationException("Shift must be active and effective throughout the assignment period.");
        var policies = (await db.QueryAsync<EmployeeShiftPolicy>(@"SELECT a.employee_id EmployeeId,g.client_id ClientId,g.shift_id ShiftId,g.attendance_mode AttendanceMode,COALESCE(g.allowed_shift_ids_json,'[]') AllowedShiftIdsJson
FROM attendance_group_employees a JOIN attendance_groups g ON g.id=a.attendance_group_id AND g.is_active=TRUE AND g.client_id=@ClientId WHERE a.employee_id IN @Ids", new { request.ClientId, Ids = ids }, tx)).ToList();
        foreach (var id in ids)
        {
            var matches = policies.Where(p => p.EmployeeId == id).ToArray();
            if (matches.Length != 1 || matches[0].AttendanceMode != "Punch" || !matches[0].AllowedShiftIds.Contains(request.ShiftId)) throw new InvalidOperationException($"Employee {id} must have exactly one punch-based policy allowing this shift.");
        }
        var existing = (await db.QueryAsync<AttendanceShiftAssignment>(ShiftAssignmentSelect + " WHERE a.client_id=@ClientId AND a.employee_id IN @Ids AND a.is_active=TRUE AND a.effective_from<=@EffectiveTo AND a.effective_to>=@EffectiveFrom", new { request.ClientId, Ids = ids, EffectiveFrom = request.EffectiveFrom.AddDays(-1), EffectiveTo = request.EffectiveTo.AddDays(1) }, tx)).ToList();
        if (existing.Any(a => AssignmentsOverlap(a, request))) throw new InvalidOperationException("An employee already has an assignment on these dates/weekdays. Cancel the conflicting assignment first. Use split intervals within one shift for split duties.");
        var allShifts = await db.QueryAsync<AttendanceShift>(ShiftSelectSql + " WHERE client_id=@ClientId", request, tx);
        var proposed = ids.Select(id => new AttendanceShiftAssignment { ClientId = request.ClientId, EmployeeId = id, ShiftId = request.ShiftId, EffectiveFrom = request.EffectiveFrom, EffectiveTo = request.EffectiveTo, WeekDays = request.WeekDays }).ToList();
        var resolver = new AttendanceShiftResolver(new() { ClientId = request.ClientId }, allShifts, new Dictionary<int, int>(), policies, existing.Concat(proposed).ToList());
        foreach (var id in ids)
            for (var day = request.EffectiveFrom.Date; day <= request.EffectiveTo.Date; day = day.AddDays(1))
                if (request.Applies(day) && (ShiftWindowsOverlap(resolver.Resolve(id, day.AddDays(-1)).Shift, day.AddDays(-1), shift, day)
                    || ShiftWindowsOverlap(shift, day, resolver.Resolve(id, day.AddDays(1)).Shift, day.AddDays(1))))
                    throw new InvalidOperationException($"Employee {id}: shift overlaps an adjacent day's shift on {day:yyyy-MM-dd}. Correct the night/day transition.");
        await EnsureUnprocessedRosterAsync(db, tx, request, ids);
        await db.ExecuteAsync(@"INSERT INTO attendance_shift_assignments(client_id,employee_id,shift_id,effective_from,effective_to,week_days,reason,created_by)
VALUES(@ClientId,@EmployeeId,@ShiftId,@EffectiveFrom,@EffectiveTo,@WeekDays,@Reason,@Actor)", ids.Select(id => new { request.ClientId, EmployeeId = id, request.ShiftId, EffectiveFrom = request.EffectiveFrom.Date, EffectiveTo = request.EffectiveTo.Date, request.WeekDays, request.Reason, Actor = actor }), tx);
        await tx.CommitAsync(); return ids.Length;
    }

    private static async Task EnsureUnprocessedRosterAsync(MySqlConnection db, IDbTransaction tx, AttendanceShiftAssignment request, int[] ids)
    {
        var cycles = (await db.QueryAsync<(int EmployeeId, int StartDay, int EndDay)>(@"SELECT a.employee_id,g.attendance_cycle_start_day,g.attendance_cycle_end_day
FROM attendance_group_employees a JOIN attendance_groups g ON g.id=a.attendance_group_id
WHERE g.client_id=@ClientId AND g.is_active=TRUE AND a.employee_id IN @Ids", new { request.ClientId, Ids = ids }, tx)).ToList();
        var affected = new HashSet<(int EmployeeId, string Month)>();
        foreach (var id in ids)
        {
            var employeeCycles = cycles.Where(c => c.EmployeeId == id).ToList();
            if (employeeCycles.Count == 0) employeeCycles.Add((id, 1, 31));
            for (var day = request.EffectiveFrom.Date; day <= request.EffectiveTo.Date; day = day.AddDays(1))
                if (request.Applies(day))
                    foreach (var cycle in employeeCycles)
                        affected.Add((id, WeeklyOffAttendanceService.CycleFor(day, cycle.StartDay, cycle.EndDay).Month));
        }
        var months = affected.Select(a => a.Month).Distinct().ToArray();
        var processed = await db.QueryAsync<(int EmployeeId, string Month)>(@"SELECT e.EmployeeId,r.PayPeriod FROM payruns r JOIN payrunemployees e ON e.PayRunId=r.Id
WHERE r.ClientId=@ClientId AND e.EmployeeId IN @Ids AND r.Status NOT IN ('Draft','Failed','Cancelled') AND r.PayPeriod IN @Months",
            new { request.ClientId, Ids = ids, Months = months }, tx);
        if (processed.Any(affected.Contains))
            throw new InvalidOperationException("A processed payroll period overlaps this roster change. Use an unprocessed period; payroll is never recalculated by changing a roster.");
        // Do not silently re-interpret saved attendance or imported payroll inputs.
        var dates = await db.QueryAsync<DateTime>("SELECT attendance_date FROM employee_daily_attendance WHERE client_id=@ClientId AND employee_id IN @Ids AND attendance_date BETWEEN @EffectiveFrom AND @EffectiveTo", new { request.ClientId, Ids = ids, request.EffectiveFrom, request.EffectiveTo }, tx);
        if (dates.Any(request.Applies)) throw new InvalidOperationException("Attendance already exists in this assignment period. Choose future/unprocessed dates; historical attendance is not automatically recalculated.");
        var monthly = await db.QueryAsync<(int EmployeeId, string Month)>("SELECT employee_id,attendance_month FROM employee_monthly_attendance WHERE client_id=@ClientId AND employee_id IN @Ids AND attendance_month IN @Months", new { request.ClientId, Ids = ids, Months = months }, tx);
        if (monthly.Any(affected.Contains))
            throw new InvalidOperationException("Monthly attendance exists in an affected attendance cycle. Use an unprocessed period for roster changes.");
    }

    public async Task CancelShiftAssignmentAsync(int clientId, int id, int actor)
    {
        await using var db = CreateConnection(); await db.OpenAsync(); await using var tx = await db.BeginTransactionAsync();
        await db.ExecuteScalarAsync<int>("SELECT Id FROM clients WHERE Id=@ClientId FOR UPDATE", new { ClientId = clientId }, tx);
        var row = await db.QuerySingleOrDefaultAsync<AttendanceShiftAssignment>(ShiftAssignmentSelect + " WHERE a.client_id=@ClientId AND a.id=@Id AND a.is_active=TRUE", new { ClientId = clientId, Id = id }, tx);
        if (row is null) throw new InvalidOperationException("Active assignment not found for this client.");
        await EnsureUnprocessedRosterAsync(db, tx, row, [row.EmployeeId]);
        await db.ExecuteAsync("UPDATE attendance_shift_assignments SET is_active=FALSE,cancelled_by=@Actor,cancelled_at=UTC_TIMESTAMP() WHERE id=@Id AND client_id=@ClientId", new { Id = id, ClientId = clientId, Actor = actor }, tx);
        await tx.CommitAsync();
    }
}
