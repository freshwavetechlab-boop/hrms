using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Services;

/// <summary>Read-only adapter over the existing source-rule translator and native calculator.</summary>
public static class SalaryTemplateExcelExtractionService
{
    private static readonly HashSet<string> Roles = ["earning", "deduction", "employer", "input", "payrollDays", "presentDays", "payableDays", "monthlyCtc", "annualCtc", "netPay", "grossTotal", "deductionTotal", "ignore"];
    private static readonly HashSet<string> Functions = ["SUM", "MIN", "MAX", "ROUND", "ROUNDUP", "ROUNDDOWN", "IF", "IFERROR", "AND", "OR", "ABS"];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const decimal Tolerance = 0.01m;
    public static bool CanAccess(AuthUser user, int clientId) => clientId > 0 && PayrollAdvisorService.CanAccess(user, clientId);
    private static bool Financial(string role) => role is "earning" or "deduction" or "employer";

    public static SalaryTemplateExcelExtractionResult Extract(SalaryTemplateExcelExtractionRequest request, IReadOnlyList<PayrollReviewComponent>? currentMasters = null)
    {
        Validate(request);
        // Never modify the supplied capture or let proposed roles mutate another caller's source.
        var source = JsonSerializer.Deserialize<ExcelPayslipCalculationSource>(JsonSerializer.Serialize(request.Source, Json), Json)!;
        var result = new SalaryTemplateExcelExtractionResult { TemplateName = request.TemplateName.Trim() };
        var reusedMasters = new Dictionary<string, PayrollReviewComponent>();
        void Error(string code, int? column, string text) => result.Issues.Add(new("Error", code, column, text));
        var captured = source.Rows.Single(row => row.SourceRow == request.SampleRow);
        var selected = request.SelectedColumns;
        var byColumn = selected.ToDictionary(column => column.ColumnIndex);
        source.Columns = Enumerable.Range(0, source.ColumnCount).Select(index => {
            var saved = request.Source.Columns.SingleOrDefault(column => column.ColumnIndex == index);
            var chosen = byColumn.GetValueOrDefault(index);
            return new ExcelPayslipCalculationColumn { ColumnIndex = index, SourceHeader = saved?.SourceHeader ?? "", Label = saved?.Label ?? saved?.SourceHeader ?? "",
                Kind = chosen?.Kind switch { "earning" => "earning", "deduction" => "deduction", "employer" => "employer", "grossTotal" => "grossTotal", "deductionTotal" => "deductionTotal", "netPay" => "netPay", "input" or "monthlyCtc" or "annualCtc" => "info", _ => "ignore" } };
        }).ToList();
        // Unselected columns must remain unbound: the generic translator can expand reviewed INFO,
        // but must not silently inline an arbitrary literal just because it is in the workbook.
        var setup = new ExcelPayrollSetupRequest { ReviewedFixedInputs = true, FixedPayrollDays = request.FixedPayrollDays,
            PayrollDaysCell = request.PayrollDaysCell, PayrollDaysColumnIndex = Role("payrollDays")?.ColumnIndex ?? -1,
            PresentDaysColumnIndex = Role("presentDays")?.ColumnIndex, PayableDaysColumnIndex = Role("payableDays")?.ColumnIndex ?? -1 };
        SalaryTemplateExcelColumn? Role(string kind) => selected.SingleOrDefault(column => column.Kind == kind);
        decimal? Literal(int index, bool allowFormula = false)
        {
            var cell = captured.Cells.ElementAtOrDefault(index);
            return cell is not null && cell.Error is null && cell.CalculationError is null && !cell.MissingCachedValue
                && (allowFormula || string.IsNullOrWhiteSpace(cell.FormulaText)) && decimal.TryParse(cell.Value, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var value) && value is >= 0 and <= 1_000_000_000m ? value : null;
        }
        var payrollDays = request.FixedPayrollDays ?? (Role("payrollDays") is { } days && Literal(days.ColumnIndex) is decimal divisor && divisor == decimal.Truncate(divisor) ? (int?)divisor : null);
        if (!string.IsNullOrWhiteSpace(request.PayrollDaysCell))
        {
            try
            {
                var sharedDays = ReadSharedPayrollDays(request, source);
                if (payrollDays.HasValue && payrollDays != sharedDays) throw new InvalidOperationException("The selected fixed/column divisor disagrees with the shared source cell.");
                payrollDays = sharedDays;
                setup.FixedPayrollDays = sharedDays;
            }
            catch (InvalidOperationException error) { Error("PAYROLL_DAYS", null, error.Message); }
        }
        var payableDays = Role("payableDays") is { } payable ? Literal(payable.ColumnIndex) : null;
        var actualPresent = Role("presentDays") is { } present ? Literal(present.ColumnIndex) : null;
        if (payrollDays is null) Error("PAYROLL_DAYS", null, "Select a numeric payroll-days column or explicitly enter the contract/calendar divisor.");
        if (payableDays is null && actualPresent is not null)
        {
            payableDays = actualPresent;
            result.Issues.Add(new("Info", "PAYABLE_DAYS", null, "No payable column was supplied. This temporary comparison uses actual present units as hypothetical payable units; the source PRESENT_DAYS references remain distinct and no paid leave is inferred."));
        }
        if (payableDays is null) Error("PAYABLE_DAYS", null, "Select a numeric payable-days or actual-present-days column; missing attendance is not zero.");
        if (payrollDays is < 1 or > 31 || actualPresent is < 0 or > 31 || payableDays is < 0 or > 31 || actualPresent > payrollDays || payableDays > payrollDays)
            Error("ATTENDANCE", null, "This ordinary template preview requires days between zero and its 1–31 divisor. Extra-duty/source-period units need their separate reviewed configuration.");
        if (actualPresent is null && payableDays is not null) result.Issues.Add(new("Info", "PRESENT_DAYS", null, "Actual presence was not supplied. For arithmetic comparison only, the temporary scenario uses payable units; it does not create attendance or certify present days."));
        foreach (var input in selected.Where(column => column.Kind == "input"))
        {
            setup.SourceColumnRoles[input.ColumnIndex] = "ignore";
            var paired = selected.Any(column => Financial(column.Kind) && column.Code.Equals(input.Code, StringComparison.OrdinalIgnoreCase));
            var computedInput = !string.IsNullOrWhiteSpace(captured.Cells.ElementAtOrDefault(input.ColumnIndex)?.FormulaText);
            if (!paired && !input.ReviewedFixedValue) Error(input.Code, input.ColumnIndex, "This independent input needs a paired salary component or explicit review as a common fixed template value; a sample employee amount is not a general rule.");
            if (ProtectedHeader(source.Columns[input.ColumnIndex].SourceHeader)) Error(input.Code, input.ColumnIndex, "Employee identifiers and contact/bank/date fields cannot become salary inputs.");
            if (paired && !computedInput && Literal(input.ColumnIndex) is null) Error(input.Code, input.ColumnIndex, "A paired literal monthly input needs an explicit numeric value. A blank, text or error cannot become an employee salary base.");
            // Computed monthly bases remain captured expressions. The shared translator
            // expands their dependency graph; they never become an override or a *_MONTHLY
            // alias to the output component itself.
            if (paired && computedInput) setup.SourceColumnRoles[input.ColumnIndex] = "info";
            if (!paired && input.ReviewedFixedValue) setup.SourceColumnRoles[input.ColumnIndex] = "info";
        }
        var annualCtc = 0m;
        int? computedMonthlyCtc = null;
        if (Role("monthlyCtc") is { } monthly)
        {
            if (!string.IsNullOrWhiteSpace(captured.Cells.ElementAtOrDefault(monthly.ColumnIndex)?.FormulaText))
            {
                computedMonthlyCtc = monthly.ColumnIndex;
                setup.MonthlyCtcColumnIndex = monthly.ColumnIndex;
                setup.SourceColumnRoles[monthly.ColumnIndex] = "info";
            }
            else if (Literal(monthly.ColumnIndex) is decimal monthlyValue && monthlyValue * 12m <= 1_000_000_000m)
            {
                // This read-only worksheet review is not a persisted source/policy approval.
                // Bind the explicitly selected independent scalar below; do not manufacture
                // TransferReviewVersion/ReviewedPolicyScope to bypass the saved-source proof.
                setup.SourceColumnRoles[monthly.ColumnIndex] = "ignore"; annualCtc = monthlyValue * 12m;
                if (ProtectedHeader(source.Columns[monthly.ColumnIndex].SourceHeader)) Error("MONTHLY_CTC", monthly.ColumnIndex, "An employee identifier cannot supply a salary target.");
            }
            else Error("MONTHLY_CTC", monthly.ColumnIndex, "Select an independent monthly amount or a captured formula over explicitly bound full-month inputs; missing values are not a CTC target.");
        }
        if (Role("annualCtc") is { } annual)
        {
            setup.SourceColumnRoles[annual.ColumnIndex] = "ignore";
            if (ProtectedHeader(source.Columns[annual.ColumnIndex].SourceHeader)) Error("ANNUAL_CTC", annual.ColumnIndex, "An employee identifier cannot supply a salary target.");
            if (Literal(annual.ColumnIndex) is decimal annualValue)
            {
                if (Role("monthlyCtc") is not null && computedMonthlyCtc is null && annualCtc != annualValue) Error("ANNUAL_CTC", annual.ColumnIndex, "The selected annual and monthly CTC inputs disagree; review which full-period target applies.");
                annualCtc = annualValue;
            }
            else Error("ANNUAL_CTC", annual.ColumnIndex, "Annual CTC must be an independent numeric literal, not a formula cache.");
        }
        foreach (var column in selected.Where(column => Financial(column.Kind)).OrderBy(column => column.ColumnIndex))
        {
            var paired = selected.SingleOrDefault(input => input.Kind == "input" && input.Code.Equals(column.Code, StringComparison.OrdinalIgnoreCase));
            var existingId = string.IsNullOrWhiteSpace(column.ExistingComponentId) ? null : column.ExistingComponentId;
            var candidate = new SalaryTemplateExcelCandidate { Id = existingId ?? "extract_" + column.ColumnIndex, ExistingComponentId = existingId,
                Code = column.Code.ToUpperInvariant(), Name = column.Name, ColumnIndex = column.ColumnIndex, InputColumnIndex = paired?.ColumnIndex, Priority = result.Components.Count + 1,
                Category = column.Kind == "employer" ? "Benefit" : column.Kind == "deduction" ? "Deduction" : "Earning",
                ComponentRole = column.Kind == "employer" ? "Employer Contribution" : column.Kind == "deduction" ? "Other Deduction" : "Regular Earning" };
            var cell = captured.Cells.ElementAtOrDefault(column.ColumnIndex);
            var pairedLiteral = paired is not null && string.IsNullOrWhiteSpace(captured.Cells.ElementAtOrDefault(paired.ColumnIndex)?.FormulaText);
            var literal = paired is not null ? Literal(paired.ColumnIndex) : Literal(column.ColumnIndex);
            var fixedInput = pairedLiteral || string.IsNullOrWhiteSpace(cell?.FormulaText);
            candidate.CalculationType = fixedInput ? "Fixed Amount" : "Formula";
            candidate.ProRata = fixedInput && (pairedLiteral || column.InputMode == "monthly");
            candidate.RequiresEmployeeInput = fixedInput && !(paired ?? column).ReviewedFixedValue;
            if (fixedInput && literal is decimal value && !candidate.RequiresEmployeeInput) candidate.Value = value.ToString(CultureInfo.InvariantCulture);
            if (fixedInput && literal is null) Error(candidate.Code, paired?.ColumnIndex ?? column.ColumnIndex, "The selected fixed input is missing, non-numeric, or a formula cache; it cannot be replaced with zero.");
            if (ProtectedHeader(source.Columns[column.ColumnIndex].SourceHeader)) Error(candidate.Code, column.ColumnIndex, "This protected employee identifier cannot be a financial component.");
            if (existingId is null && currentMasters?.Any(master => master.Code.Equals(candidate.Code, StringComparison.OrdinalIgnoreCase)) == true)
                Error(candidate.Code, candidate.ColumnIndex, "A saved component already uses this code. Reuse its compatible saved ID or choose a unique new code; shared components will not be overwritten.");
            if (existingId is not null)
            {
                var matches = currentMasters?.Where(master => master.Id == existingId).ToArray() ?? [];
                var master = matches.Length == 1 ? matches[0] : null;
                if (master is null || !master.Active || !master.Code.Equals(candidate.Code, StringComparison.OrdinalIgnoreCase) || master.Category != candidate.Category)
                    Error(candidate.Code, candidate.ColumnIndex, "The selected saved component is unavailable, inactive or has a different code/category. Choose a compatible component; its shared master will not be changed.");
                else
                {
                    candidate.ComponentRole = master.ComponentRole; candidate.StatutoryType = master.StatutoryType;
                    candidate.Taxable = master.Taxable; candidate.Priority = master.Priority; candidate.PayType = master.PayType;
                    reusedMasters[candidate.Id] = master;
                    if (candidate.RequiresEmployeeInput && (!string.IsNullOrWhiteSpace(master.Value) || !string.IsNullOrWhiteSpace(master.Formula)))
                        Error(candidate.Code, candidate.ColumnIndex, "This shared component has a default value or formula, so a blank template would inherit it. Create a separate blank component with a unique code to require individual employee inputs.");
                }
            }
            result.Components.Add(candidate);
            setup.Components.Add(new ExcelPayrollSetupComponent { Key = candidate.Code, Code = candidate.Code, Name = candidate.Name, ExistingComponentId = candidate.Id,
                Category = candidate.Category, ComponentRole = candidate.ComponentRole, CalculationType = candidate.CalculationType, Value = candidate.Value,
                SourceColumnIndex = fixedInput ? paired?.ColumnIndex ?? column.ColumnIndex : null, ExpectedColumnIndex = column.ColumnIndex,
                ProRata = candidate.ProRata, Priority = candidate.Priority });
        }
        var row = new ExcelPayslipRow { Id = "extract-sample", SourceRow = request.SampleRow };
        if (!string.IsNullOrWhiteSpace(request.PayrollDaysCell) && payrollDays.HasValue)
        {
            try { ExcelPayrollSetupRepository.ValidateSharedPayrollDaysBinding(setup, source, [row]); }
            catch (InvalidOperationException error) { Error("PAYROLL_DAYS", null, error.Message); }
        }
        if (computedMonthlyCtc is int ctcColumn)
        {
            try
            {
                var target = ExcelPayrollSetupRepository.ReviewedSourceAnnualCtc(setup, source, captured, row);
                if (Role("annualCtc") is { } annualBinding && Literal(annualBinding.ColumnIndex) is decimal annualValue && annualValue != target)
                    Error("ANNUAL_CTC", annualBinding.ColumnIndex, "The selected annual and full-month formula CTC disagree.");
                annualCtc = target;
            }
            catch (Exception error) when (error is InvalidOperationException or FormatException or OverflowException)
            { Error("MONTHLY_CTC", ctcColumn, error.Message); }
        }
        foreach (var candidate in result.Components)
        {
            var definition = setup.Components.Single(component => component.Code == candidate.Code);
            var cell = captured.Cells.ElementAtOrDefault(candidate.ColumnIndex);
            try
            {
                if (cell is null || cell.Error is not null || cell.CalculationError is not null || cell.MissingCachedValue || Literal(candidate.ColumnIndex, true) is null)
                    throw new InvalidOperationException("The sample output has no usable numeric source value. Recalculate the workbook in Excel before extracting.");
                if (!string.IsNullOrWhiteSpace(cell.FormulaText))
                {
                    var sourceCalculated = ExcelPayslipCalculationService.EvaluateCapturedCell(source, ExcelLetter(candidate.ColumnIndex) + request.SampleRow, requireExplicitCells: true).Value;
                    if (Math.Abs(sourceCalculated - Literal(candidate.ColumnIndex, true)!.Value) > Tolerance)
                        throw new InvalidOperationException("The shared Excel calculator disagrees with the saved sample value. Recalculate the workbook and review its dependencies; the cached output will not be frozen.");
                    ValidateOriginalFormula(cell.FormulaText);
                    var raw = cell.FormulaText;
                    if (computedMonthlyCtc is null && Role("monthlyCtc") is { } monthlyBinding)
                        raw = Regex.Replace(raw, @"(?<![A-Za-z0-9_])\$?" + ExcelLetter(monthlyBinding.ColumnIndex) + @"\$?" + request.SampleRow + @"(?![0-9])", "MONTHLY_CTC", RegexOptions.IgnoreCase);
                    if (Role("annualCtc") is { } annualBinding)
                        raw = Regex.Replace(raw, @"(?<![A-Za-z0-9_])\$?" + ExcelLetter(annualBinding.ColumnIndex) + @"\$?" + request.SampleRow + @"(?![0-9])", "ANNUAL_CTC", RegexOptions.IgnoreCase);
                    var translated = ExcelPayrollSetupRepository.TranslateTransferFormulaWithEvidence(raw, setup, source, row).Formula;
                    PayrollFormulaEvaluator.Validate(translated);
                    if (candidate.CalculationType == "Fixed Amount")
                    {
                        // A paired full-period input may also be paid unchanged (for
                        // example a fixed deduction). Prove the direct source binding
                        // before deciding that monthly/earned headings imply proration.
                        if (candidate.InputColumnIndex.HasValue && PayrollFormulaEvaluator.Canonicalize(translated) == PayrollFormulaEvaluator.Canonicalize(candidate.Code + "_MONTHLY"))
                            candidate.ProRata = definition.ProRata = false;
                        var expected = candidate.ProRata ? candidate.Code + "_MONTHLY/PAYROLL_DAYS*PAYABLE_DAYS" : candidate.Code + "_MONTHLY";
                        if (PayrollFormulaEvaluator.Canonicalize(translated) != PayrollFormulaEvaluator.Canonicalize(expected))
                            throw new InvalidOperationException("This rule is more than ordinary monthly-input proration. Keep it as an explicit reviewed source formula; its cached output will not be frozen as a salary amount.");
                    }
                    else candidate.Formula = definition.Formula = translated;
                }
            }
            catch (Exception error) when (error is InvalidOperationException or FormatException or OverflowException)
            { Error(candidate.Code, candidate.ColumnIndex, error.Message); }
        }
        result.Lines = result.Components.Select(component => new PayrollReviewLine(component.Id, component.Value, component.Formula, component.CalculationType, component.BaseComponent, component.ProRata.ToString().ToLowerInvariant())).ToList();
        if (!result.CanCreateDraft) return result;
        var snapshot = new PayrollReviewSnapshot("Template", request.ClientId, "", annualCtc,
            result.Components.Select(component => reusedMasters.TryGetValue(component.Id, out var master) ? master : new PayrollReviewComponent(component.Id, component.Code, component.Category, component.ComponentRole, component.StatutoryType,
                component.CalculationType, component.Value, component.Formula, component.BaseComponent, component.ProRata, component.Taxable, true, component.Priority, component.PayType)).ToList(), result.Lines);
        var scenario = new PayrollSimulationRequest { Mode = "Template", ClientId = request.ClientId, TemplateId = "excel-extraction", PayPeriod = request.Month,
            Snapshot = snapshot, PayrollDays = payrollDays, PresentDays = actualPresent ?? payableDays, PayableDays = payableDays, AnnualCtc = annualCtc };
        foreach (var component in result.Components.Where(component => component.CalculationType == "Fixed Amount")) scenario.AmountOverrides[component.Id] = Literal(component.InputColumnIndex ?? component.ColumnIndex)!.Value;
        result.Scenario = scenario;
        try { result.Simulation = PayRunRepository.SimulateTemplate(scenario); }
        catch (Exception error) when (error is InvalidOperationException or FormatException or OverflowException) { Error("SIMULATION", null, error.Message); return result; }
        foreach (var issue in result.Simulation.Issues.Where(issue => issue.Severity == "Error")) Error(issue.Component, null, issue.Message);
        foreach (var candidate in result.Components)
        {
            var expected = Literal(candidate.ColumnIndex, true);
            var amount = result.Simulation.Lines.SingleOrDefault(line => line.Code == candidate.Code)?.Earned;
            Compare(candidate.ColumnIndex, candidate.Code, expected, amount);
        }
        foreach (var control in selected.Where(column => column.Kind is "grossTotal" or "deductionTotal" or "netPay"))
            Compare(control.ColumnIndex, control.Kind, Literal(control.ColumnIndex, true), control.Kind == "netPay" ? result.Simulation.Net : control.Kind == "grossTotal" ? result.Simulation.Gross : result.Simulation.Deductions);
        if (result.Components.Any(component => component.RequiresEmployeeInput)) result.Issues.Add(new("Info", "EMPLOYEE_INPUT", null, "Sample salary bases are temporary overrides only. The new template leaves these values blank, so each employee must have their own salary input before actual payroll."));
        return result;
        void Compare(int column, string code, decimal? expected, decimal? amount)
        {
            var delta = amount - expected;
            var matched = delta.HasValue && Math.Abs(delta.Value) <= Tolerance;
            result.Comparisons.Add(new(column, code, expected, amount, delta, matched ? "Matched" : "Review"));
            if (!matched) Error(code, column, "Native sample amount does not match the captured source within currency precision. Review roles, selected dependencies and rounding before creating a template.");
        }
    }

    private static bool ProtectedHeader(string header) => Regex.IsMatch(header, @"\b(?:uan|pan|aadh?aar|ifsc|dob|employee\s*(?:code|id)|account|e-?mail|date)\b", RegexOptions.IgnoreCase) || RecurringInputHeaderGuards.HasContactIdentifier(header);
    private static int ReadSharedPayrollDays(SalaryTemplateExcelExtractionRequest request, ExcelPayslipCalculationSource source)
    {
        var address = request.PayrollDaysCell.Replace("$", "").Trim().ToUpperInvariant();
        var match = Regex.Match(address, @"^([A-Z]{1,3})([1-9]\d{0,5})$");
        if (!match.Success || !int.TryParse(match.Groups[2].Value, out var row) || row > source.HeaderRow)
            throw new InvalidOperationException("Choose a captured shared divisor cell at or above the salary header row.");
        var column = match.Groups[1].Value.Aggregate(0, (value, character) => value * 26 + character - 'A' + 1) - 1;
        var cell = source.Rows.SingleOrDefault(item => item.SourceRow == row)?.Cells.ElementAtOrDefault(column);
        if (cell is null || !string.IsNullOrWhiteSpace(cell.FormulaText) || cell.Error is not null || cell.CalculationError is not null || cell.MissingCachedValue
            || !decimal.TryParse(cell.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var days) || days < 1 || days > 31 || days != decimal.Truncate(days))
            throw new InvalidOperationException("The shared divisor must contain an explicit whole number from 1 to 31; a blank or formula cache is not a payroll divisor.");
        return (int)days;
    }
    public static IReadOnlyList<PayrollReviewComponent> ReadMasterCatalogue(string setupJson)
    {
        using var document = JsonDocument.Parse(setupJson);
        if (!document.RootElement.TryGetProperty("salaryComponents", out var entries) || entries.ValueKind != JsonValueKind.Array) return [];
        return entries.EnumerateArray().Select(entry => {
            string Text(string name) => entry.TryGetProperty(name, out var value) ? value.ToString() : "";
            bool Flag(string name) => bool.TryParse(Text(name), out var value) && value;
            return new PayrollReviewComponent(Text("id"), Text("code"), Text("category"), Text("componentRole"), Text("statutoryType"), Text("calculationType"),
                Text("value"), Text("formula"), Text("baseComponent"), Flag("proRata"), Flag("taxable"), Flag("active"), int.TryParse(Text("priority"), out var priority) ? priority : 999, Text("payType"));
        }).ToList();
    }
    private static void ValidateOriginalFormula(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 2000 || raw.Contains('!') || raw.Contains('[') || raw.Contains(']')) throw new InvalidOperationException("External or cross-sheet dependencies must be supplied and reviewed separately; their caches are not salary rules.");
        // Anchored A1 references are still captured cells, not workbook-defined names.
        // Normalize only complete cell tokens so unresolved names retain their guard.
        var normalized = Regex.Replace(raw.ToUpperInvariant(), @"(?<![A-Z0-9_])\$?([A-Z]{1,3})\$?([1-9][0-9]*)(?![A-Z0-9_])", "$1$2");
        foreach (Match token in Regex.Matches(normalized, @"\b[A-Z_][A-Z0-9_]*\b"))
            if (!Regex.IsMatch(token.Value, @"^[A-Z]{1,3}[1-9][0-9]*$") && token.Value is not ("TRUE" or "FALSE") && !(Functions.Contains(token.Value) && Regex.IsMatch(normalized[(token.Index + token.Length)..], @"^\s*\(")))
                throw new InvalidOperationException("Workbook-defined names and unresolved symbols need an explicit captured binding; a formula cache is not a replacement.");
    }
    private static string ExcelLetter(int index) { var value = ""; for (var n = index + 1; n > 0; n = (n - 1) / 26) value = (char)('A' + (n - 1) % 26) + value; return value; }
    private static void Validate(SalaryTemplateExcelExtractionRequest request)
    {
        if (request is null || request.ClientId <= 0 || string.IsNullOrWhiteSpace(request.TemplateName) || request.TemplateName.Length > 160
            || !DateTime.TryParseExact(request.Month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            || request.Source is null || request.Source.Rows is null || request.Source.Columns is null || request.SelectedColumns is null || request.PayrollDaysCell is null
            || request.FixedPayrollDays is < 1 or > 31 || request.PayrollDaysCell.Length > 20)
            throw new InvalidOperationException("Select a client, template name, valid month and captured worksheet.");
        var source = request.Source;
        if (source.ColumnCount is < 1 or > 1500 || source.HeaderRow is < 1 or > 100000 || source.Rows.Count is < 1 or > 100000
            || source.Columns.Count > 1500 || source.Rows.Any(row => row is null || row.Cells is null || row.SourceRow is < 1 or > 100000 || row.Cells.Count > source.ColumnCount)
            || source.Rows.Select(row => row.SourceRow).Distinct().Count() != source.Rows.Count || source.Rows.Sum(row => (long)row.Cells.Count) > 250000
            || source.Rows.SelectMany(row => row.Cells).Any(cell => cell is not null && (cell.Value is null || cell.Value.Length > 10000 || cell.FormulaText?.Length > 2000))
            || source.Columns.Any(column => column is null || column.ColumnIndex < 0 || column.ColumnIndex >= source.ColumnCount || column.SourceHeader is null || column.SourceHeader.Length > 512)
            || source.Columns.Select(column => column.ColumnIndex).Distinct().Count() != source.Columns.Count
            || request.SampleRow <= source.HeaderRow || source.Rows.Count(row => row.SourceRow == request.SampleRow) != 1)
            throw new InvalidOperationException("Use one bounded captured sample row and header with unique worksheet coordinates.");
        if (request.SelectedColumns.Count is < 1 or > 150 || request.SelectedColumns.Any(column => column is null || column.ColumnIndex < 0 || column.ColumnIndex >= source.ColumnCount || !Roles.Contains(column.Kind)
            || column.Code is null || column.Name is null || column.Name.Length > 160 || column.InputMode is not ("monthly" or "period") || column.ExistingComponentId?.Length > 40)
            || request.SelectedColumns.Select(column => column.ColumnIndex).Distinct().Count() != request.SelectedColumns.Count)
            throw new InvalidOperationException("Choose distinct worksheet columns and supported roles.");
        foreach (var group in request.SelectedColumns.Where(column => Financial(column.Kind)).GroupBy(column => column.Code, StringComparer.OrdinalIgnoreCase))
            if (group.Count() != 1 || !Regex.IsMatch(group.Key, @"^[A-Za-z][A-Za-z0-9_]{0,79}$") || PayrollValueCatalogue.Names.Contains(group.Key.ToUpperInvariant()))
                throw new InvalidOperationException("Financial components need unique non-reserved codes.");
        if (!request.SelectedColumns.Any(column => column.Kind == "earning") || request.SelectedColumns.Count(column => Financial(column.Kind)) > 100
            || new[] { "payrollDays", "presentDays", "payableDays", "monthlyCtc", "annualCtc", "netPay", "grossTotal", "deductionTotal" }.Any(role => request.SelectedColumns.Count(column => column.Kind == role) > 1)
            || request.SelectedColumns.Where(column => column.Kind == "input").GroupBy(column => column.Code, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new InvalidOperationException("Select at least one earning, up to 100 financial components and unambiguous day/control/input bindings.");
    }
}
