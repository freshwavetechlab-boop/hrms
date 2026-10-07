using System.Text.Json;
using Payroll.API.Models;

namespace Payroll.API.Services;

public sealed record WeeklyOffAttendanceDay(int EmployeeId, DateTime Date, string Status, decimal PayableValue);
public sealed record WeeklyOffResolvedDay(int EmployeeId, DateTime Date, string Status, decimal PayableValue,
    string? RuleVersionId, int? RuleVersionNumber, string Reason, bool Pending, string WorkWeek)
{
    public DateTime? PreviousWorkingDate { get; init; }
    public DateTime? NextWorkingDate { get; init; }
    public string WorkWeekConfig { get; init; } = "";
}

/// <summary>Pure date-based entitlement calculation. No punches/statuses or historical definitions are rewritten.</summary>
public static class WeeklyOffPayableResolver
{
    public static readonly IReadOnlySet<string> Modes = new HashSet<string>(StringComparer.Ordinal)
        { "Paid", "Unpaid", "BothAdjacentAbsent", "EitherAdjacentAbsent" };

    public static string? ValidatePublication(IReadOnlyList<WeeklyOffRuleVersion> versions, PublishWeeklyOffRuleRequest request)
    {
        var latest = versions.OrderByDescending(v => v.VersionNumber).FirstOrDefault();
        if (request.ExpectedLatestVersion != (latest?.VersionNumber ?? 0)) return "Weekly-off rules changed. Refresh the history before saving a new version.";
        if (latest is not null && request.EffectiveFrom.Date <= latest.EffectiveFrom.Date)
            return "A new version must start after the latest published effective date. Published history cannot be edited.";
        return null;
    }

    public static WeeklyOffRuleVersion? ResolveVersion(IEnumerable<WeeklyOffRuleVersion> versions, DateTime date) =>
        versions.Where(v => v.EffectiveFrom.Date <= date.Date).OrderByDescending(v => v.EffectiveFrom).ThenByDescending(v => v.VersionNumber).FirstOrDefault();

    internal static bool IsAffectedByChange(WeeklyOffResolvedDay day, DateTime from, DateTime to)
    {
        bool Changed(DateTime? date) => date is not null && date.Value.Date >= from.Date && date.Value.Date <= to.Date;
        return day.RuleVersionId is not null && day.Status.Equals("WO", StringComparison.OrdinalIgnoreCase)
            && (Changed(day.Date) || Changed(day.PreviousWorkingDate) || Changed(day.NextWorkingDate));
    }

    public static IReadOnlyList<WeeklyOffResolvedDay> Resolve(IEnumerable<WeeklyOffAttendanceDay> source,
        IReadOnlyList<WeeklyOffRuleVersion> versions, string workWeek, string? workWeekConfig,
        IReadOnlySet<string> unpaidLeaveCodes, DateTime from, DateTime to)
    {
        var days = source.GroupBy(d => d.Date.Date).ToDictionary(g => g.Key, g => g.Last());
        var orderedVersions = versions.OrderByDescending(v => v.EffectiveFrom).ThenByDescending(v => v.VersionNumber).ToArray();
        var calendars = new Dictionary<string, AttendanceWorkWeek>(StringComparer.Ordinal);
        return days.Values.Where(d => d.Date.Date >= from.Date && d.Date.Date <= to.Date).OrderBy(d => d.Date)
            .Select(day => ResolveDay(day)).ToArray();

        WeeklyOffResolvedDay ResolveDay(WeeklyOffAttendanceDay day)
        {
            var date = day.Date.Date;
            var version = orderedVersions.FirstOrDefault(v => v.EffectiveFrom.Date <= date);
            var config = version is not null && version.WorkWeekConfigs.TryGetValue(workWeek, out var frozen) ? frozen : workWeekConfig;
            DateTime? before = null, after = null;
            WeeklyOffResolvedDay Result(decimal value, string reason, bool pending = false) =>
                new(day.EmployeeId, date, day.Status, value, version?.VersionId, version?.VersionNumber, reason, pending, workWeek)
                { PreviousWorkingDate = before, NextWorkingDate = after, WorkWeekConfig = config ?? "" };
            if (day.Status.Equals("H", StringComparison.OrdinalIgnoreCase)) return Result(1, "Holiday: existing paid treatment");
            if (!day.Status.Equals("WO", StringComparison.OrdinalIgnoreCase)) return Result(day.PayableValue, "Recorded attendance");
            if (version is null || version.Mode == "Paid") return Result(1, version is null ? "Legacy paid weekly off" : "Paid weekly off");
            if (version.Mode == "Unpaid") return Result(0, "Unpaid weekly-off rule");
            var calendarKey = config ?? "";
            if (!calendars.TryGetValue(calendarKey, out var calendar)) calendars[calendarKey] = calendar = new AttendanceWorkWeek(workWeek, config);
            if (!calendar.IsConfigured || calendar.IsWorking(date)) return Result(1, "Weekly-off calendar does not identify this date; review required", true);
            before = Neighbor(-1);
            after = Neighbor(1);
            var left = Absent(before);
            var right = Absent(after);
            var denies = version.Mode == "BothAdjacentAbsent" ? left == true && right == true : left == true || right == true;
            if (denies) return Result(0, $"{version.Mode}: {before:yyyy-MM-dd} / {after:yyyy-MM-dd}");
            var unresolved = version.Mode == "BothAdjacentAbsent" ? left != false && right != false : left is null || right is null;
            return Result(1, unresolved ? "Adjacent attendance is missing; absence was not assumed" : "Weekly off remains payable", unresolved);

            DateTime? Neighbor(int direction)
            {
                for (var offset = 1; offset <= 31; offset++)
                {
                    var candidate = date.AddDays(direction * offset);
                    if (calendar.IsWorking(candidate)) return candidate;
                }
                return null;
            }
            bool? Absent(DateTime? neighbor)
            {
                if (neighbor is null || !days.TryGetValue(neighbor.Value, out var row)) return null;
                if (row.Status.Equals("A", StringComparison.OrdinalIgnoreCase) || row.Status.Equals("Absent", StringComparison.OrdinalIgnoreCase)) return true;
                if (unpaidLeaveCodes.Contains(row.Status) || row.Status.Equals("LWP", StringComparison.OrdinalIgnoreCase) || row.Status.Equals("LOP", StringComparison.OrdinalIgnoreCase)) return row.PayableValue <= 0;
                // Unknown status/missing punches are not evidence of an absence. Paid leave and half days are not absent.
                return string.IsNullOrWhiteSpace(row.Status) ? null : false;
            }
        }
    }
}

public sealed class AttendanceWorkWeek
{
    private readonly HashSet<int>? _workingDays;
    private readonly HashSet<int> _offSaturdays = [];
    private readonly string _label;
    private static readonly HashSet<string> KnownLabels = new(StringComparer.Ordinal)
    {
        "monday - friday", "monday - saturday", "all days", "sunday off", "saturday-sunday off",
        "second saturday + sunday off", "second & fourth saturday + sunday off", "alternate saturday + sunday off",
        "friday off", "friday-saturday off", "no fixed weekly off", "sunday + 2nd saturday off",
        "sunday + 2nd/4th saturday off", "only 2nd saturday off"
    };
    public bool IsConfigured => _workingDays is not null || KnownLabels.Contains(_label);
    public AttendanceWorkWeek(string label, string? configJson)
    {
        _label = string.Join(" ", (label ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(configJson)) return;
        try
        {
            using var json = JsonDocument.Parse(configJson);
            if (json.RootElement.ValueKind != JsonValueKind.Object) return;
            if (json.RootElement.TryGetProperty("workingDays", out var working) && working.ValueKind == JsonValueKind.Array)
            {
                var entries = working.EnumerateArray().ToArray();
                if (entries.All(x => x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out var value) && value is >= 0 and <= 6))
                    _workingDays = entries.Select(x => x.GetInt32()).ToHashSet();
            }
            if (json.RootElement.TryGetProperty("offSaturdays", out var off) && off.ValueKind == JsonValueKind.Array)
                _offSaturdays = off.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out _)).Select(x => x.GetInt32()).Where(x => x is >= 1 and <= 5).ToHashSet();
        }
        catch (JsonException) { }
    }
    public bool IsWorking(DateTime date)
    {
        var day = (int)date.DayOfWeek;
        var saturday = (date.Day + 6) / 7;
        if (_workingDays is not null) return _workingDays.Contains(day) && (day != 6 || !_offSaturdays.Contains(saturday));
        if (!IsConfigured) return true; // Never invent a Sunday schedule for an unknown custom label.
        if (_label.Length == 0 || _label.Contains("no fixed") || _label.Contains("all days")) return true;
        if (_label.Contains("friday-saturday") || _label.Contains("friday saturday")) return day is not (5 or 6);
        if (_label.Contains("friday off")) return day != 5;
        if (_label.Contains("only 2nd saturday")) return day != 6 || saturday != 2;
        if (_label.Contains("monday - friday") || _label.Contains("saturday-sunday") || _label.Contains("saturday sunday")) return day is not (0 or 6);
        if (day == 0) return false;
        if (day != 6) return true;
        if (_label.Contains("second & fourth") || _label.Contains("alternate saturday") || _label.Contains("2nd/4th") || _label.Contains("2nd and 4th")) return saturday is not (2 or 4);
        if (_label.Contains("second saturday") || _label.Contains("2nd saturday")) return saturday != 2;
        return _label.Contains("saturday") || _label.Contains("sunday off");
    }
}
