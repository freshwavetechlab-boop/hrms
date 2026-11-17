using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Payroll.API.Models;

namespace Payroll.API.Services;

// Pure source-boundary proof only. This class cannot save attendance, profiles,
// salary history, receipts or payroll, and never authenticates Master ownership.
internal static class ReportedPeriodInputProof
{
    internal const string WriterFlag = "Payroll:ReportedPeriodInputsEnabled";
    internal const string Meaning = "ReportedSalaryPeriodFacts";
    private const decimal AmountLimit = 1_000_000_000m;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Regex Reference = new(@"(?<![A-Z0-9_])\$?([A-Z]{1,3})\$?([1-9][0-9]{0,6})(?![A-Z0-9_])", RegexOptions.IgnoreCase);
    private static bool Hash64(string? text) => text is not null && Regex.IsMatch(text, "\\A[a-f0-9]{64}\\z");
    private static string Hash<T>(T value) => ReviewedNamedFormulaInputs.Hash(value!);
    internal static string CellHash(ExcelPayslipCalculationCell cell) => Hash(cell);
    internal static string SourceHash(ExcelPayslipBatch batch, ExcelPayslipCalculationSource source) => Hash(new { Batch = batch, Source = source });
    internal static string DeclarationHash(ReportedPeriodInputProfileV2 profile) => Hash(new
    {
        profile.Version, profile.ClientId, profile.TemplateId, profile.RulesHash,
        profile.Definitions, profile.TotalPayableKeys
    });
    internal static string BoundHash(ReportedPeriodBoundRowV2 bound) => Hash(bound with { ProofHash = "" });
    internal static string ClosureHash(ReportedPeriodCashClosureV2 closure) => Hash(closure with { ProofHash = "" });

    internal static void ValidateProfile(ReportedPeriodInputProfileV2 profile, IEnumerable<string>? componentTokens = null)
    {
        if (profile is null || profile.Version != 2 || profile.ClientId <= 0 || !Regex.IsMatch(profile.TemplateId ?? "", "\\A[1-9][0-9]{0,19}\\z")
            || !Hash64(profile.RulesHash) || profile.Definitions is null || profile.Definitions.Count is < 1 or > 16
            || profile.Definitions.Any(d => d is null) || profile.Definitions.Select(d => d.Key).Distinct(StringComparer.Ordinal).Count() != profile.Definitions.Count
            || profile.TotalPayableKeys is null || profile.TotalPayableKeys.Count is < 1 or > 16
            || profile.TotalPayableKeys.Distinct(StringComparer.Ordinal).Count() != profile.TotalPayableKeys.Count)
            throw new InvalidOperationException("Review one bounded version-two reported-period declaration.");
        var forbidden = PayrollValueCatalogue.Names.Concat(new[] { "SUM", "MIN", "MAX", "ROUND", "ROUNDUP", "ROUNDDOWN", "IF", "IFERROR", "AND", "OR", "ABS", "TRUE", "FALSE" })
            .Concat((componentTokens ?? []).SelectMany(x => new[] { x, x + "_MONTHLY", x + "_EARNED" })).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var d in profile.Definitions)
        {
            if (!Regex.IsMatch(d.Key ?? "", "\\A[A-Z][A-Z0-9_]{1,63}\\z") || forbidden.Contains(d.Key!)
                || d.Key!.EndsWith("_MONTHLY", StringComparison.Ordinal) || d.Key.EndsWith("_EARNED", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(d.Label) || d.Label.Length > 120 || d.Purpose != "FormulaOperandOnly" || !d.Required
                || d.Unit is not ("Days" or "Amount") || d.Minimum < 0 || d.Maximum < d.Minimum || d.Maximum > AmountLimit
                || d.Precision < 0 || d.Precision > (d.Unit == "Days" ? 4 : 18) || d.Unit == "Days" && d.Maximum > 31)
                throw new InvalidOperationException("Version-two operands require explicit Days or independent Amount units, precision and bounds; no legacy validation is widened.");
        }
        var dayKeys = profile.Definitions.Where(d => d.Unit == "Days").Select(d => d.Key).ToHashSet(StringComparer.Ordinal);
        if (!dayKeys.SetEquals(profile.TotalPayableKeys) || profile.DeclarationHash != DeclarationHash(profile))
            throw new InvalidOperationException("Aggregate payable must sum every declared Days operand exactly once, under the exact declaration hash.");
    }

    // Scope is constructed by trusted repository/original capture code, never
    // accepted as a client request or as proof of global government ownership.
    internal sealed record SourceScope(int ClientId, string BatchId, string PayPeriod,
        string SourceHash, string RawBatchHash, string RawSourceHash, string PrimaryWorkbookSha256,
        string CycleStart, string CycleEnd, IReadOnlyDictionary<string, string> SourceIdentityHashes);

    internal static ReportedPeriodBoundRowV2 Bind(ReportedPeriodInputProfileV2 profile, ReportedPeriodInputReview review,
        ExcelPayslipBatch batch, ExcelPayslipCalculationSource source, ExcelPayslipRow row, SourceScope scope,
        IEnumerable<string>? componentTokens = null)
    {
        ValidateProfile(profile, componentTokens);
        ValidateReview(profile, review, batch, source, scope);
        var selected = review.Rows.Where(r => r.RowId == row.Id).ToArray();
        if (selected.Length != 1 || row.SourceRow != selected[0].SourceRow || !batch.Rows.Any(r => r.Id == row.Id && r.SourceRow == row.SourceRow)
            || string.IsNullOrWhiteSpace(row.EmployeeCode) || !scope.SourceIdentityHashes.TryGetValue(row.Id, out var identity)
            || !Hash64(identity) || identity != selected[0].SourceIdentityHash)
            throw new InvalidOperationException("Reported facts need one exact source row and server-bound source identity hash; no code/name or ownership fallback exists.");
        var rowReview = selected[0];
        var keys = profile.Definitions.Select(d => d.Key).ToHashSet(StringComparer.Ordinal);
        if (rowReview.ExpectedValues is null || rowReview.CellHashes is null || !keys.SetEquals(rowReview.ExpectedValues.Keys) || !keys.SetEquals(rowReview.CellHashes.Keys))
            throw new InvalidOperationException("Every operand requires one explicit reviewed value and exact original cell hash; missing values are never zero.");
        var divisorAddress = Address(review.SharedPayrollDivisorCell);
        if (batch.Rows.Any(r => r.SourceRow == divisorAddress.Row)) throw new InvalidOperationException("Shared divisor must remain a separate original header cell, outside every employee row.");
        var divisorCell = Cell(source, divisorAddress.Row, divisorAddress.Column);
        var divisorValue = Number(divisorCell, false);
        if (!string.IsNullOrWhiteSpace(divisorCell.FormulaText) || divisorValue is < 1 or > 31 || decimal.Truncate(divisorValue) != divisorValue)
            throw new InvalidOperationException("Reported period quantities require one separately retained literal 1–31 payroll divisor.");
        var divisor = (int)divisorValue;
        var values = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var evidence = new List<ReportedPeriodInputCellV2>();
        foreach (var definition in profile.Definitions)
        {
            var binding = review.Bindings.Single(b => b.Key == definition.Key);
            var column = Column(source, binding.ColumnIndex);
            var cell = Cell(source, row.SourceRow, binding.ColumnIndex);
            if (column.Kind is not ("info" or "ignore") || ProtectedIdentityHeader(column.SourceHeader + " " + column.Label)
                || binding.HeaderRow >= row.SourceRow || binding.HeaderRow <= 0 || binding.SourceHeader != column.SourceHeader
                || binding.ColumnIndex == divisorAddress.Column && row.SourceRow == divisorAddress.Row)
                throw new InvalidOperationException("An original financial output, identifier, date or divisor cannot become a reported operand through role overrides.");
            var header = Cell(source, binding.HeaderRow, binding.ColumnIndex);
            if (!string.IsNullOrWhiteSpace(header.FormulaText) || header.Value != (binding.OriginalHeaderText ?? binding.SourceHeader)
                || NormalizeHeader(header.Value) != NormalizeHeader(binding.SourceHeader) || !Regex.IsMatch(header.Value, "\\p{L}"))
                throw new InvalidOperationException("The exact original human-readable operand header changed.");
            string? independent = null;
            decimal value;
            if (definition.Unit == "Days")
            {
                if (binding.SourceMode != "ReportedDays" || !DayHeader(column.SourceHeader + " " + column.Label))
                    throw new InvalidOperationException("Days require an explicitly reviewed reported-day column, not a disguised amount or generic INFO cache.");
                // The formula is retained and hashed, never executed or called verified.
                value = Number(cell, false);
                if (value > divisor) throw new InvalidOperationException("Reported period days exceed the separate payroll divisor.");
                if (binding.MaximumColumnIndex is int maximum)
                {
                    if (maximum == binding.ColumnIndex || maximum == divisorAddress.Column && row.SourceRow == divisorAddress.Row)
                        throw new InvalidOperationException("A period maximum must be a distinct original day-capacity literal.");
                    var maxColumn = Column(source, maximum);
                    var maxCell = Cell(source, row.SourceRow, maximum);
                    if (maxColumn.Kind is not ("info" or "ignore") || !DayHeader(maxColumn.SourceHeader + " " + maxColumn.Label)
                        || ProtectedIdentityHeader(maxColumn.SourceHeader + " " + maxColumn.Label) || !string.IsNullOrWhiteSpace(maxCell.FormulaText))
                        throw new InvalidOperationException("Period capacity cannot be a formula cache, amount, identity or financial output.");
                    var cap = Number(maxCell, false);
                    if (cap < 0 || cap > divisor || value > cap) throw new InvalidOperationException("Reported day quantity exceeds its explicit original band capacity.");
                }
            }
            else
            {
                if (binding.SourceMode != "IndependentAmount" || binding.MaximumColumnIndex is not null || DayHeader(column.SourceHeader + " " + column.Label))
                    throw new InvalidOperationException("An Amount must be an independent monetary literal or proven bounded arithmetic, never a day quantity.");
                independent = IndependentAmount(source, row.SourceRow, binding.ColumnIndex, []);
                value = PayrollFormulaEvaluator.Evaluate(independent, _ => throw new InvalidOperationException("Independent monetary arithmetic has an unbound operand."));
                if (value != Number(cell, false)) throw new InvalidOperationException("Independent monetary arithmetic and its saved numeric literal/cache differ; no cache substitutes for the rule.");
            }
            if (value < definition.Minimum || value > definition.Maximum || decimal.Round(value, definition.Precision) != value
                || !rowReview.ExpectedValues.TryGetValue(definition.Key, out var expected) || expected != value
                || rowReview.CellHashes[definition.Key] != CellHash(cell))
                throw new InvalidOperationException("A reported value, original formula/cell hash, precision or range changed after review.");
            values.Add(definition.Key, value);
            var item = new ReportedPeriodInputCellV2(definition.Key, row.SourceRow, binding.ColumnIndex, column.SourceHeader,
                binding.SourceMode, value, CellHash(cell), cell.FormulaText, independent, binding.HeaderRow, header.Value, CellHash(header), "");
            evidence.Add(item with { EvidenceHash = Hash(item) });
        }
        var payable = profile.TotalPayableKeys.Sum(k => values[k]);
        if (payable < 0 || payable > divisor) throw new InvalidOperationException("The sum of declared reported Days must fit the original payroll divisor; no overlapping paid credits are inferred.");
        var bound = new ReportedPeriodBoundRowV2(2, scope.ClientId, scope.BatchId, scope.PayPeriod, row.Id, row.SourceRow,
            rowReview.SourceIdentityHash, scope.SourceHash, scope.RawBatchHash, scope.RawSourceHash, scope.PrimaryWorkbookSha256,
            profile.DeclarationHash, Hash(review), divisor, payable, values, evidence, "");
        return bound with { ProofHash = BoundHash(bound) };
    }

    internal static void ValidateBound(ReportedPeriodBoundRowV2 bound)
    {
        if (bound is null || bound.Version != 2 || bound.ClientId <= 0 || string.IsNullOrWhiteSpace(bound.RowId) || bound.SourceRow <= 0
            || !Hash64(bound.SourceIdentityHash) || !Hash64(bound.SourceHash) || !Hash64(bound.RawBatchHash) || !Hash64(bound.RawSourceHash)
            || !Hash64(bound.PrimaryWorkbookSha256) || !Hash64(bound.DeclarationHash) || !Hash64(bound.ReviewHash)
            || bound.PayrollDivisor is < 1 or > 31 || bound.PayableDays < 0 || bound.PayableDays > bound.PayrollDivisor
            || bound.ProofHash != BoundHash(bound) || bound.Cells is null || bound.Values is null || bound.Cells.Count == 0 || bound.Cells.Count != bound.Values.Count
            || bound.Cells.Select(c => c.Key).Distinct(StringComparer.Ordinal).Count() != bound.Cells.Count
            || bound.Cells.Any(c => c.SourceRow != bound.SourceRow || c.SourceMode is not ("ReportedDays" or "IndependentAmount")
                || !bound.Values.TryGetValue(c.Key, out var value) || value != c.Value || !Hash64(c.CellHash) || !Hash64(c.HeaderCellHash)
                || c.HeaderRow <= 0 || c.HeaderRow >= c.SourceRow || string.IsNullOrWhiteSpace(c.OriginalHeaderText) || string.IsNullOrWhiteSpace(c.Header)
                || NormalizeHeader(c.OriginalHeaderText) != NormalizeHeader(c.Header)
                || c.SourceMode == "ReportedDays" && (c.Value < 0 || c.Value > bound.PayrollDivisor || decimal.Round(c.Value, 4) != c.Value)
                || c.EvidenceHash != Hash(c with { EvidenceHash = "" })))
            throw new InvalidOperationException("Reported period evidence is incomplete, foreign, stale or tampered.");
        var days = bound.Cells.Where(c => c.SourceMode == "ReportedDays").ToArray();
        if (days.Length == 0 || days.Sum(c => c.Value) != bound.PayableDays)
            throw new InvalidOperationException("Whole-period payable must be the exact sum of declared reported Days, never a separate override.");
    }

    internal static ReportedPeriodCashClosureV2 CloseFinancialGraph(ReportedPeriodInputReview review,
        ExcelPayslipBatch batch, ExcelPayslipCalculationSource source, ExcelPayslipRow row, ReportedPeriodBoundRowV2 bound)
    {
        ValidateBound(bound);
        if (row.Id != bound.RowId || row.SourceRow != bound.SourceRow || Hash(review) != bound.ReviewHash || SourceHash(batch, source) != bound.SourceHash)
            throw new InvalidOperationException("Cash closure requires the same exact reviewed employee/source boundary.");
        var divisor = Address(review.SharedPayrollDivisorCell);
        var bindings = bound.Cells.ToDictionary(c => c.ColumnIndex);
        var variables = new Dictionary<string, decimal>(bound.Values, StringComparer.Ordinal) { ["PAYROLL_DAYS"] = bound.PayrollDivisor };
        var usedDays = new HashSet<int>();
        var required = new HashSet<string>(StringComparer.Ordinal)
        {
            "Normal workflow and worker integration not implemented",
            "Actual Master/government identity ownership and protected native receipt still required",
            "Matching native monthly payable snapshot with actual presence NULL still required",
            "Existing effective or independently reviewed initial salary target still required",
            "Native Employee, statutory policies and persisted payroll parity not certified"
        };
        string Expand(int sourceRow, int columnIndex, HashSet<(int, int)> visiting)
        {
            if (visiting.Count > 40 || !visiting.Add((sourceRow, columnIndex))) throw new InvalidOperationException("Circular or excessive source cash dependency depth.");
            try
            {
                if (sourceRow != row.SourceRow)
                {
                    if ((sourceRow, columnIndex) != divisor) throw new InvalidOperationException("Unexpected cross-row dependency outside the exact shared payroll divisor.");
                    var literal = Cell(source, sourceRow, columnIndex);
                    if (!string.IsNullOrWhiteSpace(literal.FormulaText) || Number(literal, false) != bound.PayrollDivisor)
                        throw new InvalidOperationException("The shared divisor changed; same-column employee quantities cannot replace it.");
                    return "PAYROLL_DAYS";
                }
                var metadata = Column(source, columnIndex);
                var cell = Cell(source, sourceRow, columnIndex);
                if (bindings.TryGetValue(columnIndex, out var binding))
                {
                    if (CellHash(cell) != binding.CellHash || metadata.Kind is not ("info" or "ignore") || metadata.SourceHeader != binding.Header
                        || ProtectedIdentityHeader(metadata.SourceHeader + " " + metadata.Label)) throw new InvalidOperationException("A declared source input changed before cash closure.");
                    if (binding.SourceMode == "ReportedDays") usedDays.Add(columnIndex);
                    return binding.Key;
                }
                if (!string.IsNullOrWhiteSpace(cell.FormulaText))
                {
                    var formula = cell.FormulaText.TrimStart('=');
                    if (formula.Length > 2000 || formula.IndexOfAny(['!', '[', ']', '\'', '"']) >= 0)
                        throw new InvalidOperationException("An undeclared external/sheet cash dependency remains; reported day review is not a dependency waiver.");
                    var expanded = Reference.Replace(formula, m => "(" + Expand(int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), ColumnNumber(m.Groups[1].Value), visiting) + ")");
                    expanded = expanded.Replace("$", "");
                    if (expanded.Length > 4096) throw new InvalidOperationException("Expanded source cash formula exceeds the native AST limit.");
                    PayrollFormulaEvaluator.Validate(expanded);
                    return expanded;
                }
                var amount = Number(cell, metadata.BlankAsZero);
                if (metadata.Kind is "earning" or "deduction" or "employer")
                {
                    var key = "SOURCE_PERIOD_CASH_" + ColumnLetter(columnIndex);
                    variables[key] = amount;
                    required.Add("Original literal/blank period cash requires separate approved transaction coverage, not a recurring formula override");
                    return key;
                }
                if (metadata.Kind is not ("info" or "ignore") || ProtectedIdentityHeader(metadata.SourceHeader + " " + metadata.Label)
                    || DayHeader(metadata.SourceHeader + " " + metadata.Label) || CashControlHeader(metadata.SourceHeader + " " + metadata.Label))
                    throw new InvalidOperationException("An unbound identity, day or financial-control cache cannot become a cash-rule literal.");
                return amount.ToString("G29", CultureInfo.InvariantCulture);
            }
            finally { visiting.Remove((sourceRow, columnIndex)); }
        }
        var financial = source.Columns.Where(c => c.Kind is "earning" or "deduction" or "employer" or "grossTotal" or "deductionTotal" or "netPay").ToArray();
        if (financial.Length == 0 || financial.Select(c => c.ColumnIndex).Distinct().Count() != financial.Length)
            throw new InvalidOperationException("A complete unique original source financial/control set is required.");
        var proofs = new List<ReportedPeriodCashCellV2>();
        foreach (var column in financial)
        {
            var original = Cell(source, row.SourceRow, column.ColumnIndex);
            var expression = Expand(row.SourceRow, column.ColumnIndex, []);
            var actual = PayrollFormulaEvaluator.Evaluate(expression, key => variables.TryGetValue(key, out var value) ? value : throw new InvalidOperationException("Unbound cash-rule operand: " + key));
            var saved = Number(original, column.BlankAsZero);
            if (Math.Abs(actual - saved) > 0.000001m) throw new InvalidOperationException("The complete original cash AST differs from its saved source amount; no cached output is substituted.");
            if (saved < 0) required.Add("Signed negative financial output needs its dedicated exact source review; no zero clamp or reclassification is authorized");
            proofs.Add(new(column.ColumnIndex, ColumnLetter(column.ColumnIndex) + row.SourceRow, column.Kind,
                original.FormulaText, expression, Hash(PayrollFormulaEvaluator.CanonicalStructure(expression)), actual, saved, actual - saved));
        }
        var dayColumns = bound.Cells.Where(c => c.SourceMode == "ReportedDays").Select(c => c.ColumnIndex).ToHashSet();
        if (!dayColumns.SetEquals(usedDays)) throw new InvalidOperationException("Every declared reported day quantity must participate in the original cash rules.");
        var earnings = proofs.Where(p => p.Kind == "earning").Sum(p => p.Calculated);
        if (proofs.Any(p => p.Kind == "grossTotal" && Math.Abs(p.Calculated - earnings) > 0.000001m))
            required.Add("Partial gross subtotal requires explicit source-control/full-cash-gross metadata; never an amount tolerance or discarded earning");
        var closure = new ReportedPeriodCashClosureV2(row.Id, row.SourceRow, bound.SourceHash, bound.ProofHash,
            dayColumns.Count, review.SharedPayrollDivisorCell, proofs, required.Order(StringComparer.Ordinal).ToList(), "");
        return closure with { ProofHash = ClosureHash(closure) };
    }

    private static void ValidateReview(ReportedPeriodInputProfileV2 profile, ReportedPeriodInputReview review,
        ExcelPayslipBatch batch, ExcelPayslipCalculationSource source, SourceScope scope)
    {
        if (review is not { Version: 2, Reviewed: true, Meaning: Meaning } || review.ClientId != scope.ClientId || profile.ClientId != scope.ClientId
            || !Guid.TryParseExact(scope.BatchId, "N", out _)
            || batch.ClientId != scope.ClientId || batch.Id != scope.BatchId || batch.Month != scope.PayPeriod
            || scope.SourceHash != SourceHash(batch, source)
            || review.BatchId != scope.BatchId || review.PayPeriod != scope.PayPeriod || review.SourceHash != scope.SourceHash
            || review.RawBatchHash != scope.RawBatchHash || review.RawSourceHash != scope.RawSourceHash || review.PrimaryWorkbookSha256 != scope.PrimaryWorkbookSha256
            || new[] { scope.SourceHash, scope.RawBatchHash, scope.RawSourceHash, scope.PrimaryWorkbookSha256 }.Any(h => !Hash64(h))
            || review.CycleStart != scope.CycleStart || review.CycleEnd != scope.CycleEnd
            || !DateOnly.TryParseExact(scope.CycleStart, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
            || !DateOnly.TryParseExact(scope.CycleEnd, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end) || end.DayNumber - start.DayNumber is < 0 or > 30
            || !DateOnly.TryParseExact(scope.PayPeriod + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            || source.CaptureVersion != 1 || source.DependencyReviewStatus is not ("ReferenceOnly" or "Reviewed")
            || !source.WorkbookFiles.Any(f => f.WorkbookKey == source.WorkbookKey && f.Sha256.ToLowerInvariant() == scope.PrimaryWorkbookSha256)
            || review.Bindings is null || review.Bindings.Count != profile.Definitions.Count || review.Bindings.Any(b => b is null || b.ColumnIndex is < 0 or > 1499)
            || review.Bindings.Select(b => b.Key).Distinct(StringComparer.Ordinal).Count() != review.Bindings.Count
            || review.Bindings.Select(b => b.ColumnIndex).Distinct().Count() != review.Bindings.Count
            || !review.Bindings.Select(b => b.Key).ToHashSet(StringComparer.Ordinal).SetEquals(profile.Definitions.Select(d => d.Key))
            || review.TotalPayableKeys is null || !review.TotalPayableKeys.SequenceEqual(profile.TotalPayableKeys, StringComparer.Ordinal)
            || review.Rows is null || review.Rows.Count is < 1 or > 200 || review.Rows.Any(r => r is null || string.IsNullOrWhiteSpace(r.RowId))
            || review.Rows.Select(r => r.RowId).Distinct(StringComparer.Ordinal).Count() != review.Rows.Count)
            throw new InvalidOperationException("Reported input review needs the exact typed source/client/month/original/cycle/declaration scope; no ReferenceOnly source is marked complete.");
        foreach (var row in review.Rows)
            if (!Hash64(row.SourceIdentityHash) || !scope.SourceIdentityHashes.TryGetValue(row.RowId, out var identity) || identity != row.SourceIdentityHash
                || batch.Rows.Count(r => r.Id == row.RowId && r.SourceRow == row.SourceRow) != 1)
                throw new InvalidOperationException("Every reviewed row must belong to the same exact source selection and server-bound source identity scope.");
        _ = Address(review.SharedPayrollDivisorCell);
    }

    private static string IndependentAmount(ExcelPayslipCalculationSource source, int row, int index, HashSet<int> visiting)
    {
        if (visiting.Count > 20 || !visiting.Add(index)) throw new InvalidOperationException("Independent amount arithmetic is circular or too deep.");
        try
        {
            var column = Column(source, index); var cell = Cell(source, row, index);
            if (column.Kind is not ("info" or "ignore") || ProtectedIdentityHeader(column.SourceHeader + " " + column.Label) || DayHeader(column.SourceHeader + " " + column.Label)
                || BillingHeader(column.SourceHeader + " " + column.Label) || ExplicitFinancialControlHeader(column.SourceHeader + " " + column.Label))
                throw new InvalidOperationException("Independent money may not depend on an earned/financial output, attendance, identifier or control.");
            if (string.IsNullOrWhiteSpace(cell.FormulaText)) return Number(cell, false).ToString("G29", CultureInfo.InvariantCulture);
            var raw = cell.FormulaText.TrimStart('=');
            if (raw.Length > 1000 || raw.IndexOfAny(['!', '[', ']', '\'', '"', ',', ':', '<', '>', '=']) >= 0 || Regex.IsMatch(raw, "[A-Z_][A-Z0-9_]*\\s*\\(", RegexOptions.IgnoreCase))
                throw new InvalidOperationException("An independent money formula must be bounded numeric arithmetic, never a lookup, branch, external or formula-result cache.");
            var result = Reference.Replace(raw, m => int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) != row
                ? throw new InvalidOperationException("Independent money cannot borrow a different row or shared attendance divisor.")
                : "(" + IndependentAmount(source, row, ColumnNumber(m.Groups[1].Value), visiting) + ")");
            result = result.Replace("$", ""); PayrollFormulaEvaluator.Validate(result); return result;
        }
        finally { visiting.Remove(index); }
    }

    private static ExcelPayslipCalculationColumn Column(ExcelPayslipCalculationSource source, int index)
    {
        var matches = source.Columns.Where(c => c.ColumnIndex == index).ToArray();
        return matches.Length == 1 ? matches[0] : throw new InvalidOperationException("An exact unique original column is required.");
    }
    private static ExcelPayslipCalculationCell Cell(ExcelPayslipCalculationSource source, int row, int index)
    {
        var rows = source.Rows.Where(r => r.SourceRow == row).ToArray();
        var cell = rows.Length == 1 && index >= 0 ? rows[0].Cells.ElementAtOrDefault(index) : null;
        if (cell is null || cell.Error is not null || cell.CalculationError is not null || cell.MissingCachedValue)
            throw new InvalidOperationException("An original source dependency/cached input is missing or invalid; missing does not mean zero.");
        return cell;
    }
    private static decimal Number(ExcelPayslipCalculationCell cell, bool blankAsZero)
    {
        if (blankAsZero && cell.DataType == "Blank" && cell.Value == "" && string.IsNullOrWhiteSpace(cell.FormulaText)) return 0;
        if (cell.DataType is not ("Number" or "Text" or "") || string.IsNullOrWhiteSpace(cell.Value)
            || !decimal.TryParse(cell.Value, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var value) || Math.Abs(value) > AmountLimit)
            throw new InvalidOperationException("A bounded explicit numeric source value is required; text, errors and absent caches are not zero.");
        return value;
    }
    private static bool DayHeader(string text) => Regex.IsMatch(text, @"\b(?:days?|LWP|LOP|attendance|present|payable|working)\b", RegexOptions.IgnoreCase);
    private static string NormalizeHeader(string text) => Regex.Replace(text.Trim(), @"\s+", " ");
    private static bool ProtectedIdentityHeader(string text) => Regex.IsMatch(text, @"employee|\bemp\b|\buan\b|\bpan\b|aadh?aar|bank|account|ifsc|identifier|\bdate\b|\bdob\b|birth|joining|e-?mail|earned|net.?pay|net.?salary", RegexOptions.IgnoreCase)
        || RecurringInputHeaderGuards.HasContactIdentifier(text);
    private static bool BillingHeader(string text) => Regex.IsMatch(text, @"invoice|\bI?GST\b|service\s*charge|total\s*/?\s*CTC", RegexOptions.IgnoreCase);
    private static bool ExplicitFinancialControlHeader(string text) => Regex.IsMatch(text, @"\b(?:subtotal|total\s+(?:earned\s+)?earnings?|total\s+deductions?|gross\s+(?:earned|paid))\b", RegexOptions.IgnoreCase);
    private static bool CashControlHeader(string text) => BillingHeader(text) || Regex.IsMatch(text, @"\b(?:subtotal|gross|total\s+earnings?|total\s+deductions?)\b", RegexOptions.IgnoreCase);
    private static (int Row, int Column) Address(string value)
    {
        var m = Regex.Match(value ?? "", "\\A\\$?([A-Z]{1,3})\\$?([1-9][0-9]{0,6})\\z", RegexOptions.IgnoreCase);
        if (!m.Success) throw new InvalidOperationException("A single exact shared payroll divisor cell is required.");
        return (int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), ColumnNumber(m.Groups[1].Value));
    }
    private static int ColumnNumber(string value)
    {
        var number = value.ToUpperInvariant().Aggregate(0, (n, c) => n * 26 + c - 'A' + 1) - 1;
        return number is >= 0 and <= 1499 ? number : throw new InvalidOperationException("Source column is outside the capture limit.");
    }
    private static string ColumnLetter(int number)
    {
        var result = ""; for (number++; number > 0; number = (number - 1) / 26) result = (char)('A' + (number - 1) % 26) + result; return result;
    }
}
