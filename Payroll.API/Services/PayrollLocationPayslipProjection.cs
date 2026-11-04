using System.Text.Json;
using System.Text.Json.Nodes;
using Payroll.API.Models;

namespace Payroll.API.Services;

internal static class PayrollLocationPayslipProjection
{
    internal sealed record Part(string Key, string AssignmentId, int WorkLocationId, int PayrollDays,
        decimal PresentDays, decimal PayableDays, decimal Gross, decimal Deductions, decimal Net, string DetailsJson);
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    // Presentation from a saved result. Never rerun salary using current Master,
    // distribute a net difference, or prorate an already-earned location amount.
    internal static IReadOnlyList<Part> Build(PayRunEmployee employee, string? referenceAssignmentId)
    {
        var lines = JsonNode.Parse(employee.DetailsJson) as JsonArray ?? throw new InvalidOperationException("Saved payroll component details are unavailable.");
        var native = lines.OfType<JsonObject>().SelectMany(line => ReadParts(line)).ToList();
        if (native.Count == 0) return [];
        var groups = native.GroupBy(part => part.PartKey, StringComparer.Ordinal).ToList();
        if (groups.Any(group => group.Select(p => new { p.AssignmentId, p.WorkLocationId, p.PayrollDays, p.PresentDays, p.PayableDays }).Distinct().Count() != 1))
            throw new InvalidOperationException("Saved location details disagree about attendance or assignment. Review the saved payroll.");
        var parts = groups.Select(group => group.First()).ToList();
        var projected = parts.ToDictionary(part => part.PartKey, _ => new JsonArray(), StringComparer.Ordinal);
        PayrollLocationComponentPart MonthlyTarget() => parts.Count == 1 ? parts[0]
            : parts.FirstOrDefault(p => p.AssignmentId == referenceAssignmentId)
                ?? throw new InvalidOperationException("Review the monthly reference location before allocating monthly-only amounts to separate slips.");
        foreach (var line in lines.OfType<JsonObject>())
        {
            if (line["Category"]?.ToString() == "Summary") continue;
            var values = ReadParts(line);
            var amount = Number(line, "amount");
            void Add(PayrollLocationComponentPart part, decimal earned, decimal monthly)
            {
                var copy = (JsonObject)line.DeepClone(); copy.Remove("locationParts"); copy["amount"] = earned; copy["monthlyAmount"] = monthly;
                projected[part.PartKey].Add(copy);
            }
            if (values.Count == 0) { var target = MonthlyTarget(); Add(target, amount, Number(line, "monthlyAmount")); continue; }
            if (values.Select(v => v.Scope).Distinct().Count() != 1)
                throw new InvalidOperationException("Saved component scope differs across locations.");
            if (values[0].Scope == "Monthly") { var target = MonthlyTarget(); Add(target, amount, Number(line, "monthlyAmount")); }
            else if (values[0].Scope == "SourceFormulaPerPart")
            {
                if (values.GroupBy(v => v.PartKey).Any(group => group.Count() != 1) || values.Sum(v => v.Earned) != amount)
                    throw new InvalidOperationException("Saved location amounts do not reconcile to their payroll component.");
                foreach (var value in values) Add(value, value.Earned, value.Monthly);
            }
            else throw new InvalidOperationException("Saved location component scope is unsupported.");
        }
        var output = parts.Select(part =>
        {
            var rows = projected[part.PartKey].OfType<JsonObject>().ToList();
            var gross = rows.Where(line => Category(line) is "Earning" or "Reimbursement").Sum(line => Number(line, "amount"));
            var deductions = rows.Where(line => Category(line).Contains("Deduction", StringComparison.OrdinalIgnoreCase)).Sum(line => Number(line, "amount"));
            return new Part(part.PartKey, part.AssignmentId, part.WorkLocationId, part.PayrollDays, part.PresentDays, part.PayableDays,
                gross, deductions, gross - deductions, projected[part.PartKey].ToJsonString(Options));
        }).ToList();
        if (output.Sum(p => p.Gross) != employee.GrossPay + employee.OneTimeEarnings
            || output.Sum(p => p.Deductions) != employee.StatutoryDeductions + employee.OneTimeDeductions
            || output.Sum(p => p.Net) != employee.NetPay)
            throw new InvalidOperationException("Location slips do not reconcile to the saved employee payroll. No balance was invented.");
        return output;
    }
    private static string Category(JsonObject row) => row["Category"]?.ToString() ?? "";
    private static decimal Number(JsonObject row, string name) => row[name]?.GetValue<decimal>() ?? 0;
    private static List<PayrollLocationComponentPart> ReadParts(JsonObject row) => row["locationParts"] is JsonArray values
        ? JsonSerializer.Deserialize<List<PayrollLocationComponentPart>>(values.ToJsonString(), Options) ?? [] : [];
}
