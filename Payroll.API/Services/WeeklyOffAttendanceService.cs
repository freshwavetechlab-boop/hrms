using System.Data;
using System.Globalization;
using System.Text;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Services;

public sealed record WeeklyOffAttendanceResult(IReadOnlyList<WeeklyOffResolvedDay> Days, IReadOnlyList<WeeklyOffRuleVersion> Versions);

/// <summary>One bulk-loaded resolver for payroll previews and every attendance writer. Publishing never calls this service.</summary>
public static class WeeklyOffAttendanceService
{
    private sealed record EmployeeScope(int EmployeeId, int WorkLocationId);
    private sealed record Policy(int EmployeeId, string WorkWeek, int StartDay, int EndDay);
    private sealed record Preference(int WorkLocationId, string WorkWeek, int StartDay, int EndDay);
    private sealed record WeekConfig(int ClientId, string Value, string ConfigJson);
    private sealed record Daily(int EmployeeId, DateTime Date, string Status, decimal PayableValue, string Remarks);
    private sealed record LockedPeriod(int EmployeeId, string PayPeriod);
    private sealed record Monthly(int EmployeeId, string Month, string SourceType);
    private sealed record Context(IReadOnlyList<WeeklyOffRuleVersion> Versions, Dictionary<int, Policy> Policies,
        Dictionary<string, string> WorkWeeks, HashSet<string> UnpaidCodes);

    private static async Task<Context> LoadAsync(MySqlConnection db, IDbTransaction? tx, int clientId, int[] ids)
    {
        var versions = (await AttendanceIntegrationRepository.ReadAsync(db, tx, clientId)).WeeklyOffRuleVersions;
        var employees = (await db.QueryAsync<EmployeeScope>("SELECT Id EmployeeId,WorkLocationId FROM employees WHERE ClientId=@ClientId AND Id IN @Ids", new { ClientId = clientId, Ids = ids }, tx)).ToList();
        var policies = (await db.QueryAsync<Policy>(@"SELECT a.employee_id EmployeeId,g.work_week WorkWeek,g.attendance_cycle_start_day StartDay,g.attendance_cycle_end_day EndDay
FROM attendance_group_employees a JOIN attendance_groups g ON g.id=a.attendance_group_id AND g.client_id=@ClientId AND g.is_active=TRUE
WHERE a.employee_id IN @Ids ORDER BY g.id", new { ClientId = clientId, Ids = ids }, tx)).GroupBy(x => x.EmployeeId).ToDictionary(g => g.Key, g => g.First());
        var preferences = (await db.QueryAsync<Preference>(@"SELECT COALESCE(work_location_id,0) WorkLocationId,work_week WorkWeek,attendance_cycle_start_day StartDay,attendance_cycle_end_day EndDay
FROM leave_attendance_preferences WHERE client_id=@ClientId ORDER BY id", new { ClientId = clientId }, tx)).ToList();
        foreach (var employee in employees.Where(e => !policies.ContainsKey(e.EmployeeId)))
        {
            var pref = preferences.FirstOrDefault(p => p.WorkLocationId == employee.WorkLocationId) ?? preferences.FirstOrDefault(p => p.WorkLocationId == 0);
            // Keep the existing payroll/ESS cycle fallback. Preferences supply the
            // workweek; changing legacy cycle assignment is a separate operation.
            policies[employee.EmployeeId] = new(employee.EmployeeId, pref?.WorkWeek ?? "", 1, 31);
        }
        var weeks = (await db.QueryAsync<WeekConfig>("SELECT ClientId,Value,COALESCE(CAST(ConfigJson AS CHAR),'') ConfigJson FROM dropdownmasters WHERE Type='Work Week' AND IsActive=TRUE AND ClientId IN (0,@ClientId) ORDER BY ClientId DESC,Id", new { ClientId = clientId }, tx))
            .GroupBy(w => w.Value, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().ConfigJson, StringComparer.OrdinalIgnoreCase);
        var unpaid = (await db.QueryAsync<string>("SELECT code FROM leave_types WHERE client_id=@ClientId AND type='Unpaid'", new { ClientId = clientId }, tx)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new(versions, policies, weeks, unpaid);
    }

    private static Task<IEnumerable<Daily>> ReadDaysAsync(MySqlConnection db, IDbTransaction? tx, int clientId, int[] ids, DateTime from, DateTime to) =>
        db.QueryAsync<Daily>(@"SELECT employee_id EmployeeId,attendance_date Date,status Status,payable_value PayableValue,COALESCE(remarks,'') Remarks
FROM employee_daily_attendance WHERE client_id=@ClientId AND employee_id IN @Ids AND attendance_date BETWEEN @FromDate AND @ToDate ORDER BY employee_id,attendance_date",
            new { ClientId = clientId, Ids = ids, FromDate = from.Date, ToDate = to.Date }, tx);

    private static IReadOnlyList<WeeklyOffResolvedDay> Resolve(Context context, IEnumerable<Daily> daily, DateTime from, DateTime to) =>
        daily.GroupBy(d => d.EmployeeId).Where(g => context.Policies.ContainsKey(g.Key)).SelectMany(group =>
        {
            var policy = context.Policies[group.Key];
            return WeeklyOffPayableResolver.Resolve(group.Select(d => new WeeklyOffAttendanceDay(d.EmployeeId, d.Date, d.Status, d.PayableValue)),
                context.Versions, policy.WorkWeek, context.WorkWeeks.GetValueOrDefault(policy.WorkWeek), context.UnpaidCodes, from, to);
        }).ToArray();

    public static async Task<WeeklyOffAttendanceResult> ResolveAsync(MySqlConnection db, IDbTransaction? tx, int clientId, int[] employeeIds, DateTime from, DateTime to)
    {
        var ids = employeeIds.Distinct().ToArray();
        if (ids.Length == 0) return new([], []);
        var context = await LoadAsync(db, tx, clientId, ids);
        var days = await ReadDaysAsync(db, tx, clientId, ids, from.AddDays(-31), to.AddDays(31));
        return new(Resolve(context, days, from, to), context.Versions);
    }

    private static async Task<HashSet<(int EmployeeId, string Month)>> LockedAsync(MySqlConnection db, IDbTransaction? tx, int clientId, int[] ids) =>
        (await db.QueryAsync<LockedPeriod>(@"SELECT DISTINCT e.EmployeeId,r.PayPeriod FROM payruns r JOIN payrunemployees e ON e.PayRunId=r.Id
WHERE r.ClientId=@ClientId AND e.EmployeeId IN @Ids AND r.Status NOT IN ('Draft','Failed','Cancelled')", new { ClientId = clientId, Ids = ids }, tx))
        .Select(x => (x.EmployeeId, x.PayPeriod)).ToHashSet();

    public static async Task<string?> ValidateChangesAsync(MySqlConnection db, IDbTransaction? tx, int clientId, IEnumerable<WeeklyOffAttendanceDay> changes)
    {
        var rows = changes.ToArray();
        if (rows.Length == 0) return null;
        var versions = (await AttendanceIntegrationRepository.ReadAsync(db, tx, clientId)).WeeklyOffRuleVersions;
        if (versions.Count == 0) return null; // preserve the legacy path for clients that have not opted in
        var ids = rows.Select(r => r.EmployeeId).Distinct().ToArray();
        var context = await LoadAsync(db, tx, clientId, ids);
        var locked = await LockedAsync(db, tx, clientId, ids);
        return rows.Any(r => context.Policies.TryGetValue(r.EmployeeId, out var policy) && locked.Contains((r.EmployeeId, CycleFor(r.Date, policy.StartDay, policy.EndDay).Month)))
            ? "Attendance belongs to a submitted or completed payroll period. Reopen the period explicitly before changing its inputs." : null;
    }

    public static async Task<string?> ValidateMonthlyChangesAsync(MySqlConnection db, IDbTransaction? tx, int clientId, int[] employeeIds, string month)
    {
        if (employeeIds.Length == 0 || (await AttendanceIntegrationRepository.ReadAsync(db, tx, clientId)).WeeklyOffRuleVersions.Count == 0) return null;
        var locked = await LockedAsync(db, tx, clientId, employeeIds);
        return employeeIds.Any(id => locked.Contains((id, month)))
            ? "Attendance belongs to a submitted or completed payroll period. Reopen the period explicitly before changing its inputs." : null;
    }

    // false means no applicable published rule: callers retain their existing SQL and semantics unchanged.
    public static async Task<bool> TryRollupAsync(MySqlConnection db, IDbTransaction? tx, int clientId, int[] employeeIds,
        DateTime changedFrom, DateTime changedTo, bool preserveManualMonthly = false, int[]? monthlyEmployeeIds = null,
        IReadOnlyCollection<WeeklyOffAttendanceDay>? changedDays = null)
    {
        var ids = employeeIds.Distinct().ToArray();
        if (ids.Length == 0) return false;
        var versions = (await AttendanceIntegrationRepository.ReadAsync(db, tx, clientId)).WeeklyOffRuleVersions;
        if (!versions.Any(v => v.EffectiveFrom.Date <= changedTo.Date.AddDays(31))) return false;
        var context = await LoadAsync(db, tx, clientId, ids);
        // Include adjacent cycles and neighboring scheduled days; a Monday correction may change a previous month's WO.
        var from = new DateTime(changedFrom.Year, changedFrom.Month, 1).AddMonths(-1);
        var to = new DateTime(changedTo.Year, changedTo.Month, 1).AddMonths(2).AddDays(-1);
        var source = (await ReadDaysAsync(db, tx, clientId, ids, from.AddDays(-31), to.AddDays(31))).ToList();
        var resolved = Resolve(context, source, from.AddDays(-31), to.AddDays(31));
        var changedByEmployee = changedDays?.GroupBy(d => d.EmployeeId).ToDictionary(g => g.Key, g => g.Select(d => d.Date.Date).ToHashSet());
        bool IsAffected(WeeklyOffResolvedDay day)
        {
            if (changedByEmployee is null) return WeeklyOffPayableResolver.IsAffectedByChange(day, changedFrom, changedTo);
            return changedByEmployee.TryGetValue(day.EmployeeId, out var dates) && day.RuleVersionId is not null && day.Status.Equals("WO", StringComparison.OrdinalIgnoreCase)
                && (dates.Contains(day.Date) || day.PreviousWorkingDate is DateTime before && dates.Contains(before) || day.NextWorkingDate is DateTime after && dates.Contains(after));
        }
        // A future rule alone must not change a historical save. A cross-boundary WO is
        // eligible only when its own date or an actual scheduled neighboring day changed.
        if (!versions.Any(v => v.EffectiveFrom.Date <= changedTo.Date)
            && !resolved.Any(IsAffected)) return false;
        var original = source.ToDictionary(d => (d.EmployeeId, d.Date.Date));
        var locked = await LockedAsync(db, tx, clientId, ids);
        var monthly = (await db.QueryAsync<Monthly>("SELECT employee_id EmployeeId,attendance_month Month,source_type SourceType FROM employee_monthly_attendance WHERE client_id=@ClientId AND employee_id IN @Ids", new { ClientId = clientId, Ids = ids }, tx))
            .ToDictionary(x => (x.EmployeeId, x.Month));
        var updates = new List<(WeeklyOffResolvedDay Day, string Remarks)>();
        var changedMonths = new HashSet<(int EmployeeId, string Month)>();
        foreach (var day in resolved.Where(d => d.Status.Equals("WO", StringComparison.OrdinalIgnoreCase) && d.RuleVersionId is not null))
        {
            if (!IsAffected(day)) continue;
            var policy = context.Policies[day.EmployeeId];
            var month = CycleFor(day.Date, policy.StartDay, policy.EndDay).Month;
            if (locked.Contains((day.EmployeeId, month))) continue;
            var old = original[(day.EmployeeId, day.Date)];
            var marker = $"[WO rule v{day.RuleVersionNumber} {day.RuleVersionId}: {day.PayableValue:0.##}; pending={day.Pending.ToString().ToLowerInvariant()}; {day.Reason}]";
            var at = old.Remarks.IndexOf("[WO rule v", StringComparison.Ordinal);
            var userRemarks = (at < 0 ? old.Remarks : old.Remarks[..at]).TrimEnd();
            var remarks = (userRemarks.Length == 0 ? "" : userRemarks[..Math.Min(userRemarks.Length, Math.Max(0, 599 - marker.Length))] + " ") + marker;
            remarks = remarks[..Math.Min(600, remarks.Length)];
            if (old.PayableValue != day.PayableValue || old.Remarks != remarks)
            {
                updates.Add((day, remarks));
                changedMonths.Add((day.EmployeeId, month));
            }
        }
        foreach (var id in context.Policies.Keys)
        {
            var p = context.Policies[id];
            if (changedByEmployee is not null)
            {
                foreach (var date in changedByEmployee.GetValueOrDefault(id, [])) changedMonths.Add((id, CycleFor(date, p.StartDay, p.EndDay).Month));
            }
            else for (var date = changedFrom.Date; date <= changedTo.Date; date = date.AddDays(1)) changedMonths.Add((id, CycleFor(date, p.StartDay, p.EndDay).Month));
        }
        foreach (var chunk in updates.Chunk(400))
        {
            var parameters = new DynamicParameters(new { ClientId = clientId });
            var select = new StringBuilder();
            for (var i = 0; i < chunk.Length; i++)
            {
                if (i > 0) select.Append(" UNION ALL ");
                select.Append($"SELECT @Id{i} EmployeeId,@Date{i} AttendanceDate,@Value{i} PayableValue,@Remarks{i} Remarks");
                parameters.Add($"Id{i}", chunk[i].Day.EmployeeId); parameters.Add($"Date{i}", chunk[i].Day.Date);
                parameters.Add($"Value{i}", chunk[i].Day.PayableValue); parameters.Add($"Remarks{i}", chunk[i].Remarks);
            }
            await db.ExecuteAsync($"UPDATE employee_daily_attendance d JOIN ({select}) v ON v.EmployeeId=d.employee_id AND v.AttendanceDate=d.attendance_date SET d.payable_value=v.PayableValue,d.remarks=v.Remarks WHERE d.client_id=@ClientId", parameters, tx);
        }
        var summaries = new List<object>();
        var requestedMonthly = monthlyEmployeeIds?.ToHashSet();
        var updatedDays = updates.ToDictionary(u => (u.Day.EmployeeId, u.Day.Date), u => u.Day);
        var resolvedByEmployee = resolved.GroupBy(d => d.EmployeeId).ToDictionary(g => g.Key, g => g.ToArray());
        foreach (var key in changedMonths.Where(k => !locked.Contains(k)))
        {
            if (requestedMonthly is not null && !requestedMonthly.Contains(key.EmployeeId)
                && (!monthly.TryGetValue(key, out var previous) || previous.SourceType != "Date-wise")) continue;
            if (preserveManualMonthly && monthly.TryGetValue(key, out var saved) && saved.SourceType != "Date-wise") continue;
            var policy = context.Policies[key.EmployeeId];
            var cycle = CycleRange(key.Month, policy.StartDay, policy.EndDay);
            var days = resolvedByEmployee.GetValueOrDefault(key.EmployeeId, []).Where(d => d.Date >= cycle.Start && d.Date <= cycle.End).ToArray();
            if (days.Length == 0) continue;
            // Only affected WOs are rewritten. Other days retain their saved entitlement,
            // so a correction does not silently apply a new rule to an unrelated off-block.
            decimal Payable(WeeklyOffResolvedDay d) => d.Status == "H" ? 1 : d.Status == "WO" && d.RuleVersionId is null ? 1
                : updatedDays.TryGetValue((d.EmployeeId, d.Date), out var updated) ? updated.PayableValue : original[(d.EmployeeId, d.Date)].PayableValue;
            var paid = days.Sum(Payable);
            var used = days.Where(d => d.Status == "WO" && d.RuleVersionId is not null).Select(d => $"v{d.RuleVersionNumber}:{d.RuleVersionId}").Distinct();
            var remarks = $"Rolled up from date-wise attendance; WO rules {string.Join(",", used)}; pending {days.Count(d => d.Pending)}";
            summaries.Add(new { EmployeeId = key.EmployeeId, Month = key.Month, WorkingDays = days.Length,
                PresentDays = days.Where(d => d.Status == "Present").Sum(d => d.PayableValue), PayableDays = paid, LopDays = Math.Max(0, days.Length - paid), Remarks = remarks[..Math.Min(600, remarks.Length)] });
        }
        foreach (var chunk in summaries.Chunk(400))
        {
            var parameters = new DynamicParameters(new { ClientId = clientId });
            var values = new StringBuilder();
            for (var i = 0; i < chunk.Length; i++)
            {
                if (i > 0) values.Append(',');
                values.Append($"(@ClientId,@EmployeeId{i},@Month{i},@WorkingDays{i},@PresentDays{i},@PayableDays{i},@LopDays{i},'Date-wise',@Remarks{i})");
                foreach (var property in chunk[i].GetType().GetProperties()) parameters.Add(property.Name + i, property.GetValue(chunk[i]));
            }
            await db.ExecuteAsync($@"INSERT INTO employee_monthly_attendance(client_id,employee_id,attendance_month,working_days,present_days,payable_days,lop_days,source_type,remarks) VALUES {values}
ON DUPLICATE KEY UPDATE working_days=VALUES(working_days),present_days=VALUES(present_days),payable_days=VALUES(payable_days),lop_days=VALUES(lop_days),source_type='Date-wise',remarks=VALUES(remarks)", parameters, tx);
        }
        return true;
    }

    internal static (DateTime Start, DateTime End, string Month) CycleFor(DateTime date, int startDay, int endDay)
    {
        startDay = Math.Clamp(startDay, 1, 31); endDay = Math.Clamp(endDay, 1, 31);
        var endMonth = new DateTime(date.Year, date.Month, 1);
        if (startDay > endDay && date.Day > endDay) endMonth = endMonth.AddMonths(1);
        var startMonth = startDay > endDay ? endMonth.AddMonths(-1) : endMonth;
        return (Day(startMonth, startDay), Day(endMonth, endDay), endMonth.ToString("yyyy-MM", CultureInfo.InvariantCulture));
    }
    private static (DateTime Start, DateTime End) CycleRange(string month, int startDay, int endDay)
    {
        var endMonth = DateTime.ParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture);
        return (Day(startDay > endDay ? endMonth.AddMonths(-1) : endMonth, startDay), Day(endMonth, endDay));
    }
    private static DateTime Day(DateTime month, int day) => new(month.Year, month.Month, Math.Clamp(day, 1, DateTime.DaysInMonth(month.Year, month.Month)));
}
