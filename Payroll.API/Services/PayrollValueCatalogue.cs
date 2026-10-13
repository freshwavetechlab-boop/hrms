using Payroll.API.Models;
using System.Text.RegularExpressions;

namespace Payroll.API.Services;

public static class PayrollValueCatalogue
{
    public static readonly IReadOnlyList<PayrollValueDefinition> SystemValues = new List<PayrollValueDefinition>
    {
        new("CTC", "Monthly CTC target, not actual gross pay.", "Annual CTC / 12", "Amount", "300000 / 12 = 25000", "Annual salary or template target"),
        new("MONTHLY_CTC", "Same monthly target as CTC.", "Annual CTC / 12", "Amount", "300000 / 12 = 25000", "Annual salary or template target"),
        new("ANNUAL_CTC", "Annual salary target.", "Employee salary / template", "Amount", "300000 per year", "Annual salary or template target"),
        new("GROSS", "Legacy alias of monthly CTC; NOT actual gross. Prefer MONTHLY_CTC.", "Annual CTC / 12", "Amount", "300000 / 12 = 25000", "Annual salary or template target", true),
        new("PAYROLL_DAYS", "Denominator days for the employee's payroll period.", "Attendance policy / pay period", "Days", "30", "Required payroll input"),
        new("TOTAL_DAYS", "Alias of PAYROLL_DAYS.", "Attendance policy / pay period", "Days", "30", "Required payroll input"),
        new("WORKING_DAYS", "Legacy alias of PAYROLL_DAYS; does not independently exclude weekends.", "Attendance policy / pay period", "Days", "30, even with fewer calendar working days", "Required payroll input", true),
        new("PAYABLE_DAYS", "Days payable, including fractions under attendance policy.", "Attendance result", "Days", "22.5", "Required input; zero is valid"),
        new("PRESENT_DAYS", "Recorded present days, which may differ from payable days.", "Attendance result", "Days", "20 present plus 2 paid leave may mean 22 payable", "Required input; zero is valid"),
        new("LOP_DAYS", "Payroll days minus payable days, minimum zero.", "Payroll engine", "Days", "30 - 26 = 4", "Resolved attendance"),
        new("EARNINGS_BEFORE_THIS", "Full-period earnings/reimbursements calculated before this line.", "Earlier components", "Amount", "10000 + 2000 = 12000", "Order-dependent; not final gross"),
        new("FIXED_EARNINGS_BEFORE_THIS", "Earlier full-period earnings marked Fixed Pay.", "Earlier components", "Amount", "Excludes Variable Pay incentive", "Order-dependent; not final gross"),
        new("FIXED_EARNINGS", "Alias of FIXED_EARNINGS_BEFORE_THIS.", "Earlier components", "Amount", "Excludes Variable Pay incentive", "Order-dependent; not final gross"),
        new("GROSS_EARNED", "Legacy name for earned earnings BEFORE this line. Prefer EARNED_EARNINGS_BEFORE_THIS.", "Earlier components", "Amount", "15000 wages + 1000 allowance = 16000", "Order-dependent; not final gross", true),
        new("EARNED_EARNINGS_BEFORE_THIS", "Earned earnings/reimbursements calculated before this line.", "Earlier components", "Amount", "15000 wages + 1000 allowance = 16000", "Order-dependent; not final gross"),
        new("MONTHLY_EARNINGS_BEFORE_THIS", "Explicit alias of EARNINGS_BEFORE_THIS: before attendance.", "Earlier components", "Amount", "Monthly 18000 remains 18000 here", "Order-dependent; not final gross")
    }.AsReadOnly();
    public static readonly HashSet<string> Names = SystemValues.Select(v => v.Token).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static Dictionary<string, decimal> Resolve(decimal monthly, int period, decimal present, decimal payable, Dictionary<string, decimal> calculated) => new(calculated, StringComparer.OrdinalIgnoreCase)
    {
        ["CTC"] = monthly, ["GROSS"] = monthly, ["MONTHLY_CTC"] = monthly, ["ANNUAL_CTC"] = monthly * 12,
        ["PAYROLL_DAYS"] = period, ["TOTAL_DAYS"] = period, ["WORKING_DAYS"] = period,
        ["PRESENT_DAYS"] = present, ["PAYABLE_DAYS"] = payable, ["LOP_DAYS"] = Math.Max(0, period - payable),
        ["EARNED_EARNINGS_BEFORE_THIS"] = calculated.GetValueOrDefault("GROSS_EARNED"),
        ["MONTHLY_EARNINGS_BEFORE_THIS"] = calculated.GetValueOrDefault("EARNINGS_BEFORE_THIS")
    };

    public static IReadOnlyList<PayrollValueDefinition> Build(PayrollCatalogueRequest request)
    {
        if (request.Components is null || request.Components.Count > 2000 || request.Components.Any(c => c is null || string.IsNullOrWhiteSpace(c.Id) || c.Id.Length > 40 || c.Name is null || c.Name.Length > 200 || !Regex.IsMatch(c.Code ?? "", @"^[A-Za-z][A-Za-z0-9_]{0,79}$"))) throw new InvalidOperationException("Provide valid component names and codes (maximum 2000).");
        var rows = new List<PayrollValueDefinition>(SystemValues);
        foreach (var c in request.Components.Where(c => c.Active && c.Id != request.ExcludeComponentId).DistinctBy(c => c.Code.ToUpperInvariant()))
        {
            var available = request.IncludedComponentIds is null ? "Add this component to templates referencing it" : request.IncludedComponentIds.Contains(c.Id) ? "Included in this template" : "Missing from this template; add it before using";
            foreach (var suffix in new[] { "", "_MONTHLY", "_EARNED" })
                rows.Add(new(c.Code.ToUpperInvariant() + suffix, $"{c.Name}: {(suffix == "_EARNED" ? "after attendance" : "full-period value; daily rate x payroll days for daily wages")}", "Calculated component / employee salary assignment", "Amount", suffix == "_EARNED" ? "18000 x 15/30 = 9000" : "Monthly 18000; daily 700 x 30 = 21000", available, false, c.Id));
        }
        return rows;
    }
}
