using Payroll.API.Models;

namespace Payroll.API.Services;

// Opt-in effective PF calculation. Existing template formulas remain the legacy path.
public static class PfPolicyCalculator
{
    public static string? ValidateDefinition(SavePfPolicyVersionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SalaryStructureId)) return "Select a salary template.";
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 120) return "Enter a policy name (maximum 120 characters).";
        if (request.EffectiveFrom == default) return "Select the effective date.";
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 1000) return "Enter a change reason (maximum 1000 characters).";
        if (string.IsNullOrWhiteSpace(request.BaseComponentCode)) return "Select the monthly base earning component.";
        if (request.CeilingBasis is not ("CalendarDays" or "PayableDays")) return "Explicitly choose calendar-day or payable-day ceiling proration.";
        if (request.Contributions is null || request.Contributions.Count == 0 || request.Contributions.Count > 10) return "Map the employee PF component and optional employer PF components.";
        if (request.Contributions.Any(r => r is null || string.IsNullOrWhiteSpace(r.ComponentId)) || request.Contributions.Select(r => r.ComponentId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.Contributions.Count) return "Each PF component must be mapped once.";
        if (request.Contributions.Any(r => r.RatePercent < 0 || r.RatePercent > 100 || r.MonthlyWageCeiling < 0) || request.EdliMonthlyWageCeiling < 0) return "Rates must be between 0 and 100; ceilings cannot be negative.";
        return null;
    }

    public static IReadOnlyList<PfPolicyVersion> ApplicableVersions(IEnumerable<PfPolicyVersion> all, string structureId, DateOnly start, DateOnly end)
    {
        var published = all.Where(v => v.Status == "Published" && v.SalaryStructureId == structureId && v.EffectiveFrom <= end).OrderBy(v => v.EffectiveFrom).ToList();
        if (published.Count == 0) return [];
        if (published.GroupBy(v => v.EffectiveFrom).Any(g => g.Count() != 1)) throw new InvalidOperationException("PF policies have duplicate effective dates for the salary template.");
        var baseline = published.LastOrDefault(v => v.EffectiveFrom <= start)
            ?? throw new InvalidOperationException($"PF policy needs a published baseline effective on or before {start:yyyy-MM-dd}; monthly days cannot be allocated to an unknown earlier rule.");
        return new[] { baseline }.Concat(published.Where(v => v.EffectiveFrom > start)).ToArray();
    }

    public static void ValidateAttendance(PfPolicyPeriod period, decimal payableDays)
    {
        if (period.Versions.Count <= 1) return; // A single rule needs no guessed within-month distribution.
        var days = period.Days.Where(d => d.Date >= period.Start && d.Date <= period.End).ToList();
        var expected = period.End.DayNumber - period.Start.DayNumber + 1;
        if (days.Count != expected || days.Select(d => d.Date).Distinct().Count() != expected)
            throw new InvalidOperationException("Date-split PF needs one recorded attendance row for every day in the payroll cycle; monthly totals alone cannot identify the effective-date split.");
        if (days.Any(d => d.PayableValue < 0 || d.PayableValue > 1)) throw new InvalidOperationException("Date-split PF attendance payable values must be between zero and one.");
        if (days.Sum(d => d.PayableValue) != payableDays)
            throw new InvalidOperationException("Date-split PF daily payable days do not equal the stored monthly payable days. Reconcile attendance before calculating payroll.");
    }

    public static PfComponentSnapshot? Calculate(PfPolicyPeriod? period, string structureId, string componentId,
        string componentCode, string statutoryType, IReadOnlyDictionary<string, decimal> monthlyBases, int payrollDays, decimal payableDays)
    {
        if (period is null || period.Versions.Count == 0 || !period.Versions.Any(v => v.Contributions.Any(r => r.ComponentId == componentId))) return null;
        if (payrollDays <= 0) throw new InvalidOperationException("PF policy needs a positive payroll-day denominator.");
        ValidateAttendance(period, payableDays);
        var segments = new List<PfPolicySegmentSnapshot>();
        for (var i = 0; i < period.Versions.Count; i++)
        {
            var version = period.Versions[i];
            var rule = version.Contributions.SingleOrDefault(r => r.ComponentId == componentId)
                ?? throw new InvalidOperationException("A PF component must be explicitly mapped in every version covering the payroll cycle.");
            if (rule.ComponentCode != componentCode || rule.StatutoryType != statutoryType || statutoryType is not ("PF Employee" or "PF Employer"))
                throw new InvalidOperationException("The mapped PF component classification changed after publication. Publish a new policy for the intended component.");
            if (!monthlyBases.TryGetValue(version.BaseComponentCode, out var monthlyBase))
                throw new InvalidOperationException($"PF monthly base {version.BaseComponentCode} must be calculated before the mapped PF component.");
            var from = version.EffectiveFrom > period.Start ? version.EffectiveFrom : period.Start;
            var to = i + 1 < period.Versions.Count ? period.Versions[i + 1].EffectiveFrom.AddDays(-1) : period.End;
            if (to > period.End) to = period.End;
            var dates = period.Days.Where(d => d.Date >= from && d.Date <= to).OrderBy(d => d.Date).ToArray();
            var paid = period.Versions.Count == 1 ? payableDays : dates.Sum(d => d.PayableValue);
            var calendar = to.DayNumber - from.DayNumber + 1;
            var units = version.CeilingBasis == "CalendarDays" ? calendar : version.CeilingBasis == "PayableDays" ? paid : throw new InvalidOperationException("PF ceiling proration basis is missing.");
            var ceilingDenominator = version.CeilingBasis == "CalendarDays" ? period.End.DayNumber - period.Start.DayNumber + 1 : payrollDays;
            var earned = monthlyBase * paid / payrollDays;
            var wages = rule.MonthlyWageCeiling is decimal cap ? Math.Min(earned, cap * units / ceilingDenominator) : earned;
            decimal? edli = version.EdliMonthlyWageCeiling is decimal edliCap ? Math.Min(earned, edliCap * units / ceilingDenominator) : null;
            segments.Add(new(version.Id, version.VersionNumber, from, to, version.BaseComponentCode, monthlyBase, calendar, paid,
                version.CeilingBasis, rule.MonthlyWageCeiling, wages, rule.RatePercent, wages * rule.RatePercent / 100m, edli, dates));
        }
        var contribution = decimal.Round(segments.Sum(s => s.UnroundedContribution), 2, MidpointRounding.ToEven);
        var employee = statutoryType == "PF Employee";
        return new(1, structureId, componentId, componentCode, statutoryType, period.Start, period.End, payrollDays, payableDays, contribution,
            employee ? decimal.Round(segments.Sum(s => s.WageBase), 6) : null,
            employee && segments.All(s => s.EdliWageBase.HasValue) ? decimal.Round(segments.Sum(s => s.EdliWageBase!.Value), 6) : null, segments);
    }
}
