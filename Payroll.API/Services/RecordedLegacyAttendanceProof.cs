using System.Globalization;

namespace Payroll.API.Services;

internal sealed record RecordedLegacyAttendanceDay(long Id,int ClientId,int EmployeeId,DateTime Date,string Status,decimal PayableValue,string Remarks);
internal sealed record RecordedLegacyAttendanceGroup(int Id,int ClientId,int EmployeeId,int StartDay,int EndDay,string Mode);
internal sealed record RecordedLegacyAttendanceMonthly(decimal WorkingDays,decimal? PresentDays,decimal PayableDays,
    string InputSource,string SourceType,string PolicyError,bool HasOverride);

// This verifies a persisted legacy rollup for one explicitly reviewed closed
// salary period. It grants no weekly off, leave, entitlement or policy version.
internal static class RecordedLegacyAttendanceProof
{
    internal const string Kind = "Recorded legacy date-wise rollup";

    internal static string? Validate(bool reviewed,int clientId,int employeeId,string period,int memberships,
        RecordedLegacyAttendanceGroup? group,RecordedLegacyAttendanceMonthly monthly,
        IReadOnlyList<RecordedLegacyAttendanceDay> days,DateTime today)
    {
        if(!reviewed || clientId<=0 || employeeId<=0 || memberships!=1 || group is null || group.Id<=0
            || group.ClientId!=clientId || group.EmployeeId!=employeeId || group.Mode!="Legacy")
            return "Legacy dated proof requires the explicit salary-correction review and one owned active Legacy attendance group.";
        if(!DateTime.TryParseExact(period+"-01","yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var start)
            || start.Year<1900 || start>=new DateTime(today.Year,today.Month,1))
            return "Legacy dated proof supports a complete closed calendar month only.";
        var end=start.AddMonths(1).AddDays(-1);var count=DateTime.DaysInMonth(start.Year,start.Month);
        if(group.StartDay!=1 || group.EndDay is <1 or >31 || Math.Min(group.EndDay,count)!=count)
            return "The Legacy group cycle must cover this exact complete calendar month; no cross-cycle dates are inferred.";
        if(monthly.HasOverride || monthly.InputSource!="Monthly snapshot" || monthly.SourceType!="Date-wise"
            || !string.IsNullOrEmpty(monthly.PolicyError) || !monthly.PresentDays.HasValue)
            return "Legacy dated proof needs an unchanged Date-wise monthly rollup with known actual presence and no override or policy error.";
        if(days.Count!=count || days.Select(day=>day.Date).Distinct().Count()!=count || days.Select(day=>day.Id).Distinct().Count()!=count
            || days.Any(day=>day.Id<=0 || day.ClientId!=clientId || day.EmployeeId!=employeeId || day.Date.TimeOfDay!=TimeSpan.Zero || day.Date<start || day.Date>end))
            return "The persisted Legacy ledger must contain one owned positive-ID row for every exact date in this month, without gaps or duplicates.";
        foreach(var day in days)
        {
            if(!string.IsNullOrWhiteSpace(day.Remarks))
                return "Legacy dated proof does not interpret row notes or overrides; review the recorded attendance separately.";
            var supported=day.Status switch {
                "Present"=>day.PayableValue>0 && day.PayableValue<=1,
                "WO" or "H"=>day.PayableValue==1,
                // The ordinary attendance resolver already recognizes A as
                // Absent. Keep the original status in the raw proof witness.
                "Absent" or "A"=>day.PayableValue==0,
                _=>false
            };
            if(!supported)return "A Legacy day has an unsupported status or payable value. No entitlement or missing unit is inferred.";
        }
        var actual=days.Where(day=>day.Status=="Present").Sum(day=>day.PayableValue);
        var payable=days.Sum(day=>day.PayableValue);
        if(monthly.WorkingDays!=count || monthly.PresentDays.Value!=actual || monthly.PayableDays!=payable)
            return "The explicit persisted Legacy day totals differ from the stored monthly working, actual-present or payable inputs.";
        return null;
    }
}
