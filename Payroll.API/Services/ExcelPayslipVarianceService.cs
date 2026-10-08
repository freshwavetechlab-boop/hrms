using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Payroll.API.Models;

namespace Payroll.API.Services;

// Independent snapshot comparison: no payroll recalculation, master lookup or data changes.
public static class ExcelPayslipVarianceService
{
    private static readonly HashSet<string> LocationLabels =
    ["WORKLOCATION", "WORKLOCATIONNAME", "LOCATION", "LOCATIONNAME", "LOCATIONNAMETEHSILSUBTEHSIL", "TEHSILSUBTEHSIL"];

    public static ExcelPayslipVarianceResult Compare(ExcelPayslipBatch current, ExcelPayslipBatch previous)
    {
        if (current.ClientId <= 0 || current.ClientId != previous.ClientId)
            throw new InvalidOperationException("Compare batches belonging to the same client.");
        if (!DateTime.TryParseExact(current.Month, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month)
            || !DateTime.TryParseExact(previous.Month, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var baseline)
            || month.Year == 1 && month.Month == 1 || baseline != month.AddMonths(-1))
            throw new InvalidOperationException("Select a batch from the immediately previous calendar month.");
        var result = new ExcelPayslipVarianceResult
        {
            CurrentBatchId = current.Id, PreviousBatchId = previous.Id,
            CurrentMonth = current.Month, PreviousMonth = previous.Month
        };
        var now = Entries(current); var before = Entries(previous);
        var nowCodes = Index(now, x => x.Code); var beforeCodes = Index(before, x => x.Code);
        var nowKeys = Index(now, x => x.Key); var beforeKeys = Index(before, x => x.Key);
        var usedNow = new HashSet<Entry>(); var usedBefore = new HashSet<Entry>();
        var reviewed = new HashSet<string>();

        void Add(Entry? next, Entry? old, string status, string category, string component,
            decimal? previousAmount = null, decimal? currentAmount = null, string message = "")
        {
            var employee = next ?? old!;
            result.Rows.Add(new()
            {
                Id = $"variance:{result.Rows.Count + 1}", EmployeeName = employee.Row.EmployeeName,
                EmployeeCode = employee.Row.EmployeeCode, Location = employee.Location,
                Status = status, Category = category, Component = component,
                PreviousAmount = previousAmount, CurrentAmount = currentAmount,
                Difference = previousAmount.HasValue && currentAmount.HasValue ? currentAmount - previousAmount : null,
                PreviousRowId = old?.Row.Id, CurrentRowId = next?.Row.Id, Message = message
            });
            if (status == "Review") reviewed.Add(next != null ? $"current:{next.Row.Id}" : $"previous:{old!.Row.Id}");
        }

        void Review(Entry entry, bool isCurrent, string message)
        {
            (isCurrent ? usedNow : usedBefore).Add(entry);
            Add(isCurrent ? entry : null, isCurrent ? null : entry, "Review", "Employee", "Employee match", message: message);
        }

        void Match(Entry next, Entry old)
        {
            usedNow.Add(next); usedBefore.Add(old); result.MatchedEmployees++;
            if (next.LocationAmbiguous || old.LocationAmbiguous)
                Add(next, old, "Review", "Employee", "Work location", message: "Conflicting location fields; matched using the unique employee code.");
            else if (NormalizeText(next.Row.EmployeeName) != NormalizeText(old.Row.EmployeeName)
                || NormalizeText(next.Location) != NormalizeText(old.Location))
                Add(next, old, "Changed", "Employee", "Employee details", message:
                    $"Previous: {old.Row.EmployeeName} / {old.Location}. Current: {next.Row.EmployeeName} / {next.Location}.");

            // Work location already participates in identity matching / employee-detail changes.
            var newInformation = next.Row.Information.Where(x => !LocationLabels.Contains(NormalizeLabel(x.Label))).ToLookup(x => NormalizeLabel(x.Label));
            var oldInformation = old.Row.Information.Where(x => !LocationLabels.Contains(NormalizeLabel(x.Label))).ToLookup(x => NormalizeLabel(x.Label));
            foreach (var label in oldInformation.Select(x => x.Key).Union(newInformation.Select(x => x.Key)))
            {
                var newValues = newInformation[label].ToArray(); var oldValues = oldInformation[label].ToArray();
                var display = newValues.FirstOrDefault()?.Label ?? oldValues[0].Label;
                if (label.Length == 0 || newValues.Length > 1 || oldValues.Length > 1)
                {
                    Add(next, old, "Review", "Information", display, message:
                        "Blank or duplicate information label; its values could not be compared unambiguously.");
                    continue;
                }
                var newValue = newValues.FirstOrDefault()?.Value.Trim(); var oldValue = oldValues.FirstOrDefault()?.Value.Trim();
                var newNumber = InformationNumber(newValue); var oldNumber = InformationNumber(oldValue);
                if (newValue == oldValue || newNumber.HasValue && oldNumber.HasValue
                    && SourcePrecision(newNumber.Value) == SourcePrecision(oldNumber.Value)) continue;
                var status = oldValue is null ? "Added component" : newValue is null ? "Removed component" : "Changed";
                Add(next, old, status, "Information", display, oldNumber, newNumber,
                    $"Previous: {DisplayInformation(oldValue)}. Current: {DisplayInformation(newValue)}.");
            }

            var currentComponents = Components(next.Row).ToLookup(x => NormalizeLabel(x.Label));
            var previousComponents = Components(old.Row).ToLookup(x => NormalizeLabel(x.Label));
            foreach (var label in previousComponents.Select(x => x.Key).Union(currentComponents.Select(x => x.Key)))
            {
                var newItems = currentComponents[label].ToArray(); var oldItems = previousComponents[label].ToArray();
                var display = newItems.FirstOrDefault()?.Label ?? oldItems[0].Label;
                if (label.Length == 0 || newItems.GroupBy(x => x.Category).Any(g => g.Count() > 1)
                    || oldItems.GroupBy(x => x.Category).Any(g => g.Count() > 1))
                {
                    Add(next, old, "Review", "Components", display, message:
                        "Blank or duplicate component label in the same category; amounts were not combined or compared.");
                    continue;
                }
                if (newItems.Length == 1 && oldItems.Length == 1 && newItems[0].Category != oldItems[0].Category)
                {
                    Add(next, old, "Category changed", $"{oldItems[0].Category} → {newItems[0].Category}", display,
                        oldItems[0].Amount, newItems[0].Amount);
                    continue;
                }
                var remainingNew = newItems.Where(n => !oldItems.Any(o => o.Category == n.Category)).ToArray();
                var remainingOld = oldItems.Where(o => !newItems.Any(n => n.Category == o.Category)).ToArray();
                var ambiguousMove = remainingNew.Length > 0 && remainingOld.Length > 0;
                foreach (var item in oldItems)
                {
                    var updated = newItems.FirstOrDefault(n => n.Category == item.Category);
                    if (updated != null && !SameCurrency(updated.Amount, item.Amount))
                        Add(next, old, "Changed", item.Category, updated.Label, item.Amount, updated.Amount);
                    else if (updated == null && !ambiguousMove)
                        Add(next, old, "Removed component", item.Category, item.Label, item.Amount, 0,
                            "Component is absent from the current batch.");
                }
                if (ambiguousMove)
                    Add(next, old, "Review", "Components", display, message:
                        "This label appears in multiple categories; unmatched categories require review.");
                else
                    foreach (var item in remainingNew)
                        Add(next, old, "Added component", item.Category, item.Label, 0, item.Amount,
                            "Component is absent from the previous batch.");
            }
            Add(next, old, SameCurrency(next.Row.NetPay, old.Row.NetPay) ? "Unchanged" : "Changed", "Net pay", "Net pay", old.Row.NetPay, next.Row.NetPay);
        }

        // Duplicate codes cannot safely fall back to a name, even when one name looks familiar.
        var duplicateCodes = nowCodes.Keys.Union(beforeCodes.Keys)
            .Where(code => Count(nowCodes, code) > 1 || Count(beforeCodes, code) > 1).ToHashSet();
        foreach (var entry in now.Where(x => duplicateCodes.Contains(x.Code)))
            Review(entry, true, "Employee code is duplicated in one of the batches; choose distinct employee identifiers.");
        foreach (var entry in before.Where(x => duplicateCodes.Contains(x.Code)))
            Review(entry, false, "Employee code is duplicated in one of the batches; choose distinct employee identifiers.");
        foreach (var next in now.Where(x => x.Code.Length > 0 && !usedNow.Contains(x)))
            if (beforeCodes.TryGetValue(next.Code, out var matches) && matches.Length == 1 && !usedBefore.Contains(matches[0]))
                Match(next, matches[0]);

        // Check uniqueness against the entire snapshot, including code-matched rows.
        foreach (var key in nowKeys.Keys.Union(beforeKeys.Keys))
        {
            nowKeys.TryGetValue(key, out var newGroup); beforeKeys.TryGetValue(key, out var oldGroup);
            newGroup ??= []; oldGroup ??= [];
            if (newGroup.Length > 1 || oldGroup.Length > 1)
            {
                foreach (var item in newGroup.Where(x => !usedNow.Contains(x) && (oldGroup.Length > 0 || x.Code.Length == 0)))
                    Review(item, true, "Name and work location are not unique; no automatic employee match was made.");
                foreach (var item in oldGroup.Where(x => !usedBefore.Contains(x) && (newGroup.Length > 0 || x.Code.Length == 0)))
                    Review(item, false, "Name and work location are not unique; no automatic employee match was made.");
                continue;
            }
            if (newGroup.Length != 1 || oldGroup.Length != 1) continue;
            var next = newGroup[0]; var old = oldGroup[0];
            if (usedNow.Contains(next) && usedBefore.Contains(old)) continue;
            if (usedNow.Contains(next) || usedBefore.Contains(old))
            {
                if (!usedNow.Contains(next)) Review(next, true, "The matching name and location belong to an already matched employee.");
                if (!usedBefore.Contains(old)) Review(old, false, "The matching name and location belong to an already matched employee.");
            }
            else if (next.Code.Length > 0 && old.Code.Length > 0)
            {
                Review(next, true, "Name and location match, but employee codes differ; no automatic employee match was made.");
                Review(old, false, "Name and location match, but employee codes differ; no automatic employee match was made.");
            }
            else Match(next, old);
        }

        foreach (var item in now.Where(x => !usedNow.Contains(x)))
        {
            if (item.Code.Length == 0 && item.Key.Length == 0)
                Review(item, true, "Provide a unique employee code or employee name with one unambiguous work location; name alone is not matched.");
            else
            {
                result.NewEmployees++;
                Add(item, null, "New employee", "Net pay", "Net pay", currentAmount: item.Row.NetPay,
                    message: "Employee was not found in the previous month's batch.");
            }
        }
        foreach (var item in before.Where(x => !usedBefore.Contains(x)))
        {
            if (item.Code.Length == 0 && item.Key.Length == 0)
                Review(item, false, "Provide a unique employee code or employee name with one unambiguous work location; name alone is not matched.");
            else
            {
                result.MissingEmployees++;
                Add(null, item, "Missing employee", "Net pay", "Net pay", previousAmount: item.Row.NetPay,
                    message: "Employee is absent from the current batch.");
            }
        }
        result.ReviewEmployees = reviewed.Count;
        return result;
    }

    private sealed record Entry(ExcelPayslipRow Row, string Code, string Location, string Key, bool LocationAmbiguous);
    private sealed record Component(string Label, string Category, decimal Amount);

    private static Entry[] Entries(ExcelPayslipBatch batch)
    {
        if (batch.Rows.Any(x => string.IsNullOrWhiteSpace(x.Id)) || batch.Rows.Select(x => x.Id).Distinct().Count() != batch.Rows.Count)
            throw new InvalidOperationException("Batch employee rows must have unique identifiers.");
        return batch.Rows.Select(row =>
        {
            var locations = row.Information.Where(x => LocationLabels.Contains(NormalizeLabel(x.Label)) && !string.IsNullOrWhiteSpace(x.Value))
                .GroupBy(x => NormalizeText(x.Value)).Select(g => g.First().Value.Trim()).ToArray();
            var location = locations.Length == 1 ? locations[0] : "";
            var name = NormalizeText(row.EmployeeName);
            var key = name.Length > 0 && location.Length > 0 ? name + "\u001f" + NormalizeText(location) : "";
            return new Entry(row, NormalizeText(row.EmployeeCode), location, key, locations.Length > 1);
        }).ToArray();
    }

    private static Dictionary<string, Entry[]> Index(IEnumerable<Entry> rows, Func<Entry, string> selector) =>
        rows.Where(x => selector(x).Length > 0).GroupBy(selector).ToDictionary(g => g.Key, g => g.ToArray());
    private static int Count(Dictionary<string, Entry[]> index, string key) => index.TryGetValue(key, out var rows) ? rows.Length : 0;
    internal static string NormalizeText(string? value) => Regex.Replace((value ?? "").Normalize(NormalizationForm.FormKC).Trim(), @"\s+", " ").ToUpperInvariant();
    private static string NormalizeLabel(string? value) => new(NormalizeText(value).Where(char.IsLetterOrDigit).ToArray());
    // Match displayed paise using the existing Excel precision/rounding rule; source values and deltas stay raw.
    private static bool SameCurrency(decimal current, decimal previous) => ExcelPayslipPdfService.Amount(current, 2) == ExcelPayslipPdfService.Amount(previous, 2);
    private static decimal SourcePrecision(decimal value) => decimal.Parse(value.ToString("G15", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture);
    private static string DisplayInformation(string? value) => value is null ? "(not mapped)" : value.Length == 0 ? "(blank)" : value;
    private static decimal? InformationNumber(string? value) => value is not null
        // Leading-zero account / identity strings stay text. Exact source values also remain in Message.
        && Regex.IsMatch(value, @"^[+-]?(?:0|[1-9]\d*)(?:\.\d+)?$", RegexOptions.CultureInvariant)
        && decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number)
        && number is >= -10_000_000_000m and <= 10_000_000_000m
        ? number : null;
    private static IEnumerable<Component> Components(ExcelPayslipRow row) =>
        row.Earnings.Select(x => new Component(x.Label, "Earnings", x.Amount))
            .Concat(row.Deductions.Select(x => new Component(x.Label, "Deductions", x.Amount)))
            .Concat(row.EmployerContributions.Select(x => new Component(x.Label, "Employer contributions", x.Amount)));
}
