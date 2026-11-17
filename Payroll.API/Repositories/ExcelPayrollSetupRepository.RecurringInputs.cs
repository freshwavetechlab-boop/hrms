using System.Globalization;
using System.Text.RegularExpressions;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayrollSetupRepository
{
    internal sealed record CapturedRecurringInput(int SourceRow, int ColumnIndex, string Header, string Kind, string? Formula, string? Literal);
    internal sealed record TransferFormulaTranslation(string Formula, IReadOnlyList<CapturedRecurringInput> Dependencies);
    // Server-owned proof boundary, never accepted in an upload/request DTO.
    internal sealed record InspectedRecurringSourceBoundary(string Token,decimal Literal,bool ReviewedNormalCtc=false,string? CapturedCellHash=null);

    // INFO dependencies are rules, not salary output overrides. Expand their captured
    // arithmetic and bind only reviewed literal bases/rates; never use a formula cache.
    internal static TransferFormulaTranslation TranslateTransferFormulaWithEvidence(string raw, ExcelPayrollSetupRequest request,
        ExcelPayslipCalculationSource source, ExcelPayslipRow row,IReadOnlyDictionary<int,InspectedRecurringSourceBoundary>? inspectedSourceBoundary=null,
        bool bindReviewedMonthlyCtc=true)
    {
        if(bindReviewedMonthlyCtc&&request.ReviewedSourceMonthlyCtcOperand)
        {
            if(inspectedSourceBoundary is not null)throw new InvalidOperationException("A normal source CTC operand cannot replace another inspected boundary.");
            inspectedSourceBoundary=ReviewedNormalSourceCtcBoundary(request,source,row);
        }
        return TranslateRecurringFormulaCore(raw,request,source,row,inspectedSourceBoundary,null);
    }
    internal sealed record NamedRecurringSourceBoundary(string Key,decimal Literal);
    internal static TransferFormulaTranslation TranslateNamedFormulaWithEvidence(string raw,ExcelPayrollSetupRequest request,
        ExcelPayslipCalculationSource source,ExcelPayslipRow row,IReadOnlyDictionary<int,NamedRecurringSourceBoundary> named,
        IReadOnlyDictionary<int,InspectedRecurringSourceBoundary>? inspectedSourceBoundary=null)
        => TranslateRecurringFormulaCore(raw,request,source,row,inspectedSourceBoundary,named);
    private static TransferFormulaTranslation TranslateRecurringFormulaCore(string raw,ExcelPayrollSetupRequest request,
        ExcelPayslipCalculationSource source,ExcelPayslipRow row,IReadOnlyDictionary<int,InspectedRecurringSourceBoundary>? inspectedSourceBoundary,
        IReadOnlyDictionary<int,NamedRecurringSourceBoundary>? named)
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
            if (Regex.Matches(formula, @"\b([A-Z][A-Z0-9_]*)\(").Any(m => m.Groups[1].Value is not ("SUM" or "MIN" or "MAX" or "ROUND" or "ROUNDUP" or "ROUNDDOWN" or "IF" or "IFERROR" or "AND" or "OR" or "ABS")))
                throw new InvalidOperationException("This source function is not supported by exact transfer proof. It cannot be replaced with a cached constant.");

            var translated = Regex.Replace(formula, @"\b([A-Z]+)(\d+)\b", m => {
                var column = TransferColumn(m.Groups[1].Value);
                if (!int.TryParse(m.Groups[2].Value, CultureInfo.InvariantCulture, out var line)) throw new InvalidOperationException("Source dependency exceeds supported worksheet rows.");
                // The explicit shared-cell review is validated against the full
                // saved source before transfer. Its address remains the same
                // input when the formula happens to be on that physical row.
                if (request.FixedPayrollDays.HasValue && request.PayrollDaysCell.Length > 0
                    && string.Equals(SharedPayrollDaysCell(request), m.Value, StringComparison.OrdinalIgnoreCase))
                {
                    var sharedCell = source.Rows.SingleOrDefault(r => r.SourceRow == line)?.Cells.ElementAtOrDefault(column);
                    if (sharedCell is not null && string.IsNullOrEmpty(sharedCell.FormulaText) && sharedCell.Error is null
                        && sharedCell.CalculationError is null && !sharedCell.MissingCachedValue
                        && decimal.TryParse(sharedCell.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var sharedDays)
                        && sharedDays == request.FixedPayrollDays.Value) return "PAYROLL_DAYS";
                    throw new InvalidOperationException("The explicitly reviewed shared divisor changed; no formula cache is substituted.");
                }
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
                if(named?.TryGetValue(column,out var operand)==true)
                {
                    var namedLiteral=capturedRows.Count==1?capturedRows[0].Cells.ElementAtOrDefault(column):null;
                    var namedMetadata=source.Columns.SingleOrDefault(c=>c.ColumnIndex==column);
                    if(outputs.ContainsKey(column)||inputs.ContainsKey(column)||namedMetadata is null||namedMetadata.Kind is not("info" or "ignore")
                        ||namedLiteral is null||!string.IsNullOrWhiteSpace(namedLiteral.FormulaText)||namedLiteral.Error is not null||namedLiteral.CalculationError is not null||namedLiteral.MissingCachedValue
                        ||!decimal.TryParse(namedLiteral.Value,NumberStyles.Number|NumberStyles.AllowExponent,CultureInfo.InvariantCulture,out var number)||number!=operand.Literal)
                        throw new InvalidOperationException("The reviewed independent named source operand changed.");
                    dependencies[column]=new(row.SourceRow,column,namedMetadata.SourceHeader,"info",null,number.ToString("G29",CultureInfo.InvariantCulture));
                    return operand.Key;
                }
                if(inspectedSourceBoundary?.TryGetValue(column,out var boundary)==true)
                {
                    var boundaryMetadata=source.Columns.Where(c=>c.ColumnIndex==column).ToArray();
                    var literalCell=capturedRows.Count==1?capturedRows[0].Cells.ElementAtOrDefault(column):null;
                    if(boundary.ReviewedNormalCtc)
                    {
                        if(inspectedSourceBoundary.Count!=1||boundary.Token!="MONTHLY_CTC"||!request.ReviewedSourceMonthlyCtcOperand
                            ||request.MonthlyCtcColumnIndex!=column||boundaryMetadata.Length!=1||boundaryMetadata[0].Kind is not("info" or "ignore")
                            ||request.SourceColumnRoles.GetValueOrDefault(column,boundaryMetadata[0].Kind)!="info"
                            ||outputs.ContainsKey(column)||inputs.ContainsKey(column)||literalCell is not{DataType:"Number"}
                            ||literalCell.Error is not null||literalCell.CalculationError is not null||literalCell.MissingCachedValue
                            ||!decimal.TryParse(literalCell.Value,NumberStyles.Number|NumberStyles.AllowExponent,CultureInfo.InvariantCulture,out var inspectedMonthly)
                            ||inspectedMonthly!=boundary.Literal
                            ||boundary.CapturedCellHash!=CapturedCtcOperandHash(row.SourceRow,column,boundaryMetadata[0],literalCell))
                            throw new InvalidOperationException("The reviewed source monthly CTC operand changed or aliases another input/output.");
                        dependencies[column]=new(row.SourceRow,column,boundaryMetadata[0].SourceHeader,"reviewed-monthly-ctc",literalCell.FormulaText,boundary.Literal.ToString("G29",CultureInfo.InvariantCulture));
                        return boundary.Token;
                    }
                    if(inspectedSourceBoundary.Count!=1 || boundary.Token!="MONTHLY_CTC" || request.MonthlyCtcColumnIndex!=column
                        || boundaryMetadata.Length!=1 || boundaryMetadata[0].Kind!="info" || request.SourceColumnRoles.GetValueOrDefault(column,boundaryMetadata[0].Kind)!="info"
                        || outputs.ContainsKey(column) || inputs.ContainsKey(column) || literalCell is null || literalCell.MissingCachedValue
                        || literalCell.Error is not null || literalCell.CalculationError is not null || !string.IsNullOrWhiteSpace(literalCell.FormulaText)
                        || !decimal.TryParse(literalCell.Value,NumberStyles.Number|NumberStyles.AllowExponent,CultureInfo.InvariantCulture,out var boundaryLiteral)
                        || boundaryLiteral!=boundary.Literal || boundaryLiteral<=0 || boundaryLiteral>1_000_000_000m/12m || decimal.Round(boundaryLiteral,4)!=boundaryLiteral)
                        throw new InvalidOperationException("The inspected source boundary no longer matches its independent literal monthly CTC cell.");
                    dependencies[column]=new(row.SourceRow,column,boundaryMetadata[0].SourceHeader,"info",null,boundaryLiteral.ToString("G29",CultureInfo.InvariantCulture));
                    return boundary.Token;
                }
                if (outputs.TryGetValue(column, out var output))
                {
                    if (output.Count != 1) throw new InvalidOperationException("Ambiguous financial output dependency; review one component mapping.");
                    return output[0].Code.ToUpperInvariant() + "_EARNED";
                }
                if (inputs.TryGetValue(column, out var input))
                {
                    if (input.Count != 1) throw new InvalidOperationException("Ambiguous recurring input dependency; review one monthly/rate mapping.");
                    if (!string.IsNullOrWhiteSpace(capturedRows.Count == 1 ? capturedRows[0].Cells.ElementAtOrDefault(column)?.FormulaText : null))
                    {
                        var entry = InspectConstantRecurringInput(request, source, row, input[0]);
                        dependencies[column] = new(row.SourceRow, column, entry.Header, "reviewed-constant-arithmetic", entry.Formula, entry.Value.ToString("G29", CultureInfo.InvariantCulture));
                    }
                    var token = input[0].Code.ToUpperInvariant() + "_MONTHLY";
                    return input[0].CalculationType == "Daily Rate" ? "(" + token + "/PAYROLL_DAYS)" : token;
                }
                var columns = source.Columns.Where(c => c.ColumnIndex == column).ToList();
                var metadata = columns.Count == 1 ? columns[0] : null;
                var kind = metadata is null ? "" : request.SourceColumnRoles.GetValueOrDefault(column, metadata.Kind);
                if (metadata?.Kind == "grossTotal" && kind == "grossTotal")
                {
                    var aggregate = capturedRows.Count == 1 ? capturedRows[0].Cells.ElementAtOrDefault(column) : null;
                    if (aggregate is null || aggregate.Error is not null || aggregate.CalculationError is not null
                        || aggregate.MissingCachedValue || string.IsNullOrWhiteSpace(aggregate.FormulaText))
                        throw new InvalidOperationException("A referenced gross control needs its captured error-free formula; a cached total is not a recurring salary input.");
                    _ = ReadNumber(capturedRows[0], column); // Validate the cache; never substitute it into the rule.
                    if (visiting.Contains(column) || visiting.Count >= 20)
                        throw new InvalidOperationException("Circular or deeply nested gross control formula dependency.");
                    dependencies[column] = new(row.SourceRow, column, metadata.SourceHeader, kind, aggregate.FormulaText, null);
                    return "(" + Expand(aggregate.FormulaText, new HashSet<int>(visiting) { column }) + ")";
                }
                if (!request.ReviewedFixedInputs || metadata is null || metadata.Kind is not ("info" or "ignore") || kind != "info"
                    || ProtectedRecurringInputHeader(metadata.SourceHeader + " " + metadata.Label))
                    throw new InvalidOperationException("Unmapped or protected source dependency. Explicitly review numeric INFO bases/rates; employee identifiers, financial outputs and arbitrary cells are not recurring salary inputs.");
                var cell = capturedRows.Count == 1 ? capturedRows[0].Cells.ElementAtOrDefault(column) : null;
                if (cell is null || cell.Error is not null || cell.CalculationError is not null || cell.MissingCachedValue)
                    throw new InvalidOperationException("A recurring INFO dependency is missing or contains a captured calculation error. No cache or zero is substituted.");
                if(request.RecurringBlankInputReview?.ColumnIndices.Contains(column)==true)
                {
                    RequireReviewedBlankOperand(request,source,row,column);
                    dependencies[column]=new(row.SourceRow,column,metadata.SourceHeader,"reviewed-blank-zero",null,"0");
                    return "0";
                }
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
            if (translated.Length > PayrollFormulaEvaluator.MaximumExpressionLength) throw new InvalidOperationException("The expanded recurring source formula exceeds the supported 4096-character native rule length. Review separate native components.");
            return translated;
        }

        // Prove the shared branch factor before cell substitution. Equal literals
        // from distinct reviewed INFO columns are not the same source dependency.
        var sourceRule = NormalizeTransferCapFormula(Regex.Replace(raw.TrimStart('=').ToUpperInvariant(), @"[\s$]", ""));
        return new(Expand(sourceRule, []), dependencies.Values.OrderBy(d => d.ColumnIndex).ToList());
    }

    private static bool ProtectedRecurringInputHeader(string header) => Regex.IsMatch(header,
        @"employee|\bemp\b|\buan\b|uan[_\s-]*(?:no|number)|\bpan\b|pan[_\s-]*(?:no|number)|aadh?aar|bank|account|ifsc|identifier|qualification|\bdate\b|\bdob\b|birth|joining|e-?mail|\besi(?:c)?[_\s-]*(?:no|number)|earned|net[_\s-]*(?:pay|salary)|salary[_\s-]*paid", RegexOptions.IgnoreCase)
        || RecurringInputHeaderGuards.HasContactIdentifier(header);

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
