using System.Globalization;
using System.Text.RegularExpressions;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayrollSetupRepository
{
    internal sealed record CapturedRecurringInput(int SourceRow, int ColumnIndex, string Header, string Kind, string? Formula, string? Literal);
    internal sealed record TransferFormulaTranslation(string Formula, IReadOnlyList<CapturedRecurringInput> Dependencies);

    // INFO dependencies are rules, not salary output overrides. Expand their captured
    // arithmetic and bind only reviewed literal bases/rates; never use a formula cache.
    internal static TransferFormulaTranslation TranslateTransferFormulaWithEvidence(string raw, ExcelPayrollSetupRequest request,
        ExcelPayslipCalculationSource source, ExcelPayslipRow row)
    {
        var dependencies = new Dictionary<int, CapturedRecurringInput>();
        var outputs = request.Components.Where(c => c.ExpectedColumnIndex.HasValue).GroupBy(c => c.ExpectedColumnIndex!.Value).ToDictionary(g => g.Key, g => g.ToList());
        var inputs = request.Components.Where(c => c.SourceColumnIndex.HasValue && !c.IsPeriodAdjustment).GroupBy(c => c.SourceColumnIndex!.Value).ToDictionary(g => g.Key, g => g.ToList());
        var capturedRows = source.Rows.Where(r => r.SourceRow == row.SourceRow).ToList();

        string Expand(string expression, HashSet<int> visiting)
        {
            var formula = Regex.Replace(expression.TrimStart('=').ToUpperInvariant(), @"[\s$]", "");
            if (formula.Length > 1000 || Regex.IsMatch(formula, @"[^A-Z0-9_.,:+*/()%\-<>=]") || formula.Contains('!') || formula.Contains('['))
                throw new InvalidOperationException("Unsupported or external formula syntax; review its dependencies before transfer.");
            formula = Regex.Replace(formula, @"SUM\(([A-Z]+)(\d+):([A-Z]+)(\d+)\)", m => {
                if (m.Groups[2].Value != m.Groups[4].Value || !int.TryParse(m.Groups[2].Value, CultureInfo.InvariantCulture, out var sourceLine) || sourceLine != row.SourceRow)
                    throw new InvalidOperationException("Only same-row bounded SUM ranges are supported by transfer proof.");
                var first = TransferColumn(m.Groups[1].Value); var last = TransferColumn(m.Groups[3].Value);
                if (last < first || last - first > 100) throw new InvalidOperationException("Review this source range.");
                return "(" + string.Join("+", Enumerable.Range(first, last - first + 1).Select(i => TransferLetter(i) + row.SourceRow)) + ")";
            });
            formula = Regex.Replace(formula, @"ROUND\(([^()]*(?:\([^()]*\)[^()]*)*),0\)", "ROUND($1)");
            if (Regex.Matches(formula, @"\b([A-Z][A-Z0-9_]*)\(").Any(m => m.Groups[1].Value is not ("MIN" or "MAX" or "ROUND" or "ROUNDUP" or "ROUNDDOWN" or "IF" or "IFERROR" or "AND" or "OR" or "ABS")))
                throw new InvalidOperationException("This source function is not supported by exact transfer proof. It cannot be replaced with a cached constant.");

            var translated = Regex.Replace(formula, @"\b([A-Z]+)(\d+)\b", m => {
                var column = TransferColumn(m.Groups[1].Value);
                if (!int.TryParse(m.Groups[2].Value, CultureInfo.InvariantCulture, out var line)) throw new InvalidOperationException("Source dependency exceeds supported worksheet rows.");
                if (line != row.SourceRow)
                {
                    var shared = source.Rows.SingleOrDefault(r => r.SourceRow == line)?.Cells.ElementAtOrDefault(column);
                    if (request.FixedPayrollDays.HasValue && string.Equals((request.PayrollDaysCell.Length > 0 ? SharedPayrollDaysCell(request) : source.MonthDaysCell ?? "").Replace("$", ""), m.Value, StringComparison.OrdinalIgnoreCase)
                        && shared is not null && string.IsNullOrEmpty(shared.FormulaText) && shared.Error is null && shared.CalculationError is null && !shared.MissingCachedValue
                        && decimal.TryParse(shared.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var days) && days == request.FixedPayrollDays.Value) return "PAYROLL_DAYS";
                    throw new InvalidOperationException("Unbound shared-row dependency: explicitly capture the payroll-days cell or review this rule. Shared salary/identity values are not frozen.");
                }
                if (!request.FixedPayrollDays.HasValue && column == request.PayrollDaysColumnIndex) return "PAYROLL_DAYS";
                if (column == request.PayableDaysColumnIndex) return "PAYABLE_DAYS";
                if (request.PresentDaysColumnIndex == column) return "PRESENT_DAYS";
                if (request.AttendanceReview is { Reviewed: true, LwpBasis: "PayrollDivisorMinusPayable" } review && review.LwpColumnIndex == column) return "LOP_DAYS";
                if (outputs.TryGetValue(column, out var output))
                {
                    if (output.Count != 1) throw new InvalidOperationException("Ambiguous financial output dependency; review one component mapping.");
                    return output[0].Code.ToUpperInvariant() + "_EARNED";
                }
                if (inputs.TryGetValue(column, out var input))
                {
                    if (input.Count != 1) throw new InvalidOperationException("Ambiguous recurring input dependency; review one monthly/rate mapping.");
                    return input[0].Code.ToUpperInvariant() + "_MONTHLY";
                }
                var columns = source.Columns.Where(c => c.ColumnIndex == column).ToList();
                var metadata = columns.Count == 1 ? columns[0] : null;
                var kind = metadata is null ? "" : request.SourceColumnRoles.GetValueOrDefault(column, metadata.Kind);
                if (!request.ReviewedFixedInputs || metadata is null || metadata.Kind is not ("info" or "ignore") || kind != "info"
                    || ProtectedRecurringInputHeader(metadata.SourceHeader + " " + metadata.Label))
                    throw new InvalidOperationException("Unmapped or protected source dependency. Explicitly review numeric INFO bases/rates; employee identifiers, financial outputs and arbitrary cells are not recurring salary inputs.");
                var cell = capturedRows.Count == 1 ? capturedRows[0].Cells.ElementAtOrDefault(column) : null;
                if (cell is null || cell.Error is not null || cell.CalculationError is not null || cell.MissingCachedValue)
                    throw new InvalidOperationException("A recurring INFO dependency is missing or contains a captured calculation error. No cache or zero is substituted.");
                if (!string.IsNullOrWhiteSpace(cell.FormulaText))
                {
                    if (visiting.Contains(column) || visiting.Count >= 20) throw new InvalidOperationException("Circular or deeply nested recurring INFO formula dependency.");
                    dependencies[column] = new(row.SourceRow, column, metadata.SourceHeader, kind, cell.FormulaText, null);
                    return "(" + Expand(cell.FormulaText, new HashSet<int>(visiting) { column }) + ")";
                }
                if (!decimal.TryParse(cell.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var literal) || literal is < 0 or > 1_000_000_000m)
                    throw new InvalidOperationException("A reviewed recurring INFO base/rate needs an explicit bounded numeric literal; blank or text is not zero.");
                var text = literal.ToString("0.############################", CultureInfo.InvariantCulture);
                dependencies[column] = new(row.SourceRow, column, metadata.SourceHeader, kind, null, text);
                return text;
            });
            if (translated.Length > 1000) throw new InvalidOperationException("The expanded recurring source formula exceeds the supported rule length. Review separate native components.");
            return translated;
        }

        return new(Expand(raw, []), dependencies.Values.OrderBy(d => d.ColumnIndex).ToList());
    }

    private static bool ProtectedRecurringInputHeader(string header) => Regex.IsMatch(header,
        @"employee|\bemp\b|\buan\b|uan[_\s-]*(?:no|number)|\bpan\b|pan[_\s-]*(?:no|number)|aadh?aar|bank|account|ifsc|phone|mobile|identifier|qualification|\bdate\b|\bdob\b|birth|joining|e-?mail|\besi(?:c)?[_\s-]*(?:no|number)|earned|net[_\s-]*(?:pay|salary)|salary[_\s-]*paid", RegexOptions.IgnoreCase);

    internal static decimal DeriveReviewedFormulaAnnualBasis(int clientId, ExcelPayrollSetupRequest request,
        IReadOnlyList<ResolvedComponent> components, List<PayrollReviewComponent> snapshotComponents, List<PayrollReviewLine> lines,
        IReadOnlyDictionary<string, decimal> inputs, int payrollDays, decimal existingAnnual)
    {
        if (!request.ReviewedFixedInputs || !components.Any(c => !c.Definition.IsPeriodAdjustment
            && c.Definition.Category is "Earning" or "Reimbursement" && c.Definition.CalculationType == "Formula")) return existingAnnual;
        var byCode = components.ToDictionary(c => c.Definition.Code, StringComparer.OrdinalIgnoreCase);
        var earningDependencies = components.Where(c => !c.Definition.IsPeriodAdjustment && c.Definition.Category is "Earning" or "Reimbursement").Select(c => c.Id).ToHashSet();
        var pending = new Queue<ResolvedComponent>(components.Where(c => earningDependencies.Contains(c.Id)));
        while (pending.TryDequeue(out var component))
        {
            foreach (Match match in Regex.Matches(component.Definition.Formula + " " + component.Definition.BaseComponent, @"\b[A-Z][A-Z0-9_]*\b", RegexOptions.IgnoreCase))
            {
                var code = Regex.Replace(match.Value, @"_(MONTHLY|EARNED)$", "", RegexOptions.IgnoreCase);
                if (byCode.TryGetValue(code, out var dependency) && earningDependencies.Add(dependency.Id)) pending.Enqueue(dependency);
            }
        }
        var basisComponents = snapshotComponents.Where(c => earningDependencies.Contains(c.Id)).ToList();
        var basisLines = lines.Where(line => earningDependencies.Contains(line.ComponentId)).ToList();
        // A CTC-dependent rule needs its independently reviewed target; do not
        // solve a circular CTC target from the result of the same formula.
        if (components.Where(c => earningDependencies.Contains(c.Id)).Any(c => Regex.IsMatch(c.Definition.Formula + " " + c.Definition.BaseComponent, @"\b(?:CTC|GROSS|MONTHLY_CTC|ANNUAL_CTC)\b", RegexOptions.IgnoreCase)
            || c.Definition.CalculationType == "Residual / Balancing" && string.IsNullOrWhiteSpace(c.Definition.BaseComponent)))
        {
            if (existingAnnual <= 0) throw new InvalidOperationException("Formula-derived salary uses a CTC target without a proven monthly input. Review an independent salary target; no earned output is annualized.");
            return existingAnnual;
        }
        var projection = PayRunRepository.SimulateTemplate(new PayrollSimulationRequest {
            Mode = "Template", ClientId = clientId, TemplateId = "excel-full-month-basis", PayrollDays = payrollDays,
            PresentDays = payrollDays, PayableDays = payrollDays, AnnualCtc = existingAnnual,
            AmountOverrides = inputs.Where(pair => earningDependencies.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value),
            Snapshot = new("Template", clientId, "", existingAnnual, basisComponents, basisLines)
        });
        if (!projection.Complete || projection.Gross is not decimal fullMonth || fullMonth <= 0 || fullMonth * 12m > 1_000_000_000m)
            throw new InvalidOperationException("The reviewed Settings formulas do not establish a positive bounded full-month salary basis. Review their monthly inputs and dependencies; source earned caches are not annualized.");
        return decimal.Round(fullMonth * 12m, 2);
    }
}
