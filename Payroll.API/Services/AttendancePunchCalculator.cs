using Payroll.API.Models;

namespace Payroll.API.Services;

public static class AttendancePunchCalculator
{
    public record Punch(string Action, DateTime At);
    public record Result(TimeSpan? CheckIn, TimeSpan? CheckOut, decimal Hours, decimal Payable, string Remarks);

    public static DateTime AttendanceDate(DateTime local, AttendanceSettings settings)
    {
        if (settings.CheckOutTime >= settings.CheckInTime) return local.Date;
        // Overnight shifts belong to their start date. Split the gap between shifts.
        var cutoff = settings.CheckOutTime + (settings.CheckInTime - settings.CheckOutTime) / 2;
        return local.TimeOfDay < cutoff ? local.Date.AddDays(-1) : local.Date;
    }

    public static (DateTime Start, DateTime End) Bounds(DateTime date, AttendanceSettings settings)
    {
        if (settings.CheckOutTime >= settings.CheckInTime) return (date.Date, date.Date.AddDays(1));
        var cutoff = settings.CheckOutTime + (settings.CheckInTime - settings.CheckOutTime) / 2;
        return (date.Date + cutoff, date.Date.AddDays(1) + cutoff);
    }

    public static Result Calculate(IEnumerable<Punch> source, DateTime date, AttendanceSettings settings, AttendanceRules rules)
    {
        var punches = source.OrderBy(p => p.At).ToArray();
        DateTime? firstIn = null, lastOut = null, open = null;
        decimal pairedHours = 0;
        var missingIn = false;
        foreach (var punch in punches)
        {
            if (punch.Action == "CheckIn") { firstIn ??= punch.At; open ??= punch.At; }
            else if (open is not null) { pairedHours += (decimal)(punch.At - open.Value).TotalHours; lastOut = punch.At; open = null; }
            else if (lastOut is not null) lastOut = punch.At; // repeated OUT: retain the last OUT
            else { missingIn = true; lastOut = punch.At; }
        }
        var incomplete = open is not null || missingIn || firstIn is null || lastOut is null;
        var hours = settings.WorkingHoursCalculation == "Every valid check-in and check-out" ? pairedHours
            : firstIn is not null && lastOut is not null ? (decimal)(lastOut.Value - firstIn.Value).TotalHours : 0;
        hours = Math.Round(Math.Clamp(hours, 0, settings.MaximumHoursAllowedForFullDay), 2);
        var payable = incomplete ? 0 : hours >= settings.MinimumHoursForFullDay ? 1m : hours >= settings.MinimumHoursForHalfDay ? .5m : 0m;
        var flags = new List<string>();
        if (missingIn || firstIn is null) flags.Add("Missing punch in");
        if (open is not null || lastOut is null) flags.Add("Missing punch out");
        var shiftIn = date.Date + settings.CheckInTime;
        var shiftOut = date.Date + settings.CheckOutTime + (settings.CheckOutTime < settings.CheckInTime ? TimeSpan.FromDays(1) : TimeSpan.Zero);
        if (rules.LateGraceMinutes is int late && firstIn > shiftIn.AddMinutes(late)) flags.Add("Late coming");
        if (rules.EarlyGraceMinutes is int early && lastOut < shiftOut.AddMinutes(-early)) flags.Add("Early going");
        if (!incomplete && payable == .5m) flags.Add("Half day");
        if (!incomplete && payable == 0) flags.Add("Insufficient hours");
        return new(firstIn?.TimeOfDay, lastOut?.TimeOfDay, hours, payable, string.Join("; ", flags));
    }
}
