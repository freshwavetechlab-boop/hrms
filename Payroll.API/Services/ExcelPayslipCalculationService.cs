using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Services;

/// <summary>Bounded, independent calculation of saved Excel salary cells. No payroll rules or database access.</summary>
public static class ExcelPayslipCalculationService
{
    private const decimal Limit = 10_000_000_000m;
    private static readonly HashSet<string> AmountKinds = ["earning", "deduction", "employer", "netPay", "grossTotal", "deductionTotal"];
    private static readonly HashSet<string> Kinds = [.. AmountKinds, "employeeName", "employeeCode", "email", "info", "ignore"];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Regex AddressPattern = new(@"^\$?([A-Z]{1,3})\$?([1-9][0-9]{0,5})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex NumberPattern = new(@"^[+-]?(?:\d+(?:,\d{2,3})*(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?$", RegexOptions.CultureInvariant);

    public static string? ValidateSource(ExcelPayslipCalculationSource source, ExcelPayslipBatch batch)
    {
        if (source is null || source.Rows is null || source.Columns is null || source.ExcludedSourceRows is null)
            return "The saved calculation source is incomplete.";
        if (source.ExcludedSourceRows.Count > 100000 || source.ExcludedSourceRows.Any(row => row is < 1 or > 100000)
            || (!string.IsNullOrEmpty(source.SourceBatchId) && !Guid.TryParseExact(source.SourceBatchId, "N", out _)))
            return "Calculation source lineage or excluded rows are invalid.";
        if (source.SheetName != batch.SheetName || source.HeaderRow != batch.HeaderRow || source.HeaderRow is < 1 or > 100000
            || source.ColumnCount is < 1 or > 512 || source.DateSystem is not ("1900" or "1904"))
            return "The calculation worksheet or header does not match this batch.";
        if (source.Rows.Count is < 1 or > 100000 || source.Rows.Any(r => r is null || r.Cells is null || r.SourceRow is < 1 or > 100000 || r.Cells.Count > source.ColumnCount)
            || source.Rows.Select(r => r.SourceRow).Distinct().Count() != source.Rows.Count || source.Rows.Sum(r => (long)r.Cells.Count) > 150000)
            return "Calculation source exceeds its row/cell limits or contains duplicate rows.";
        if (source.Columns.Count is < 1 or > 512 || source.Columns.Any(c => c is null || c.ColumnIndex < 0 || c.ColumnIndex >= source.ColumnCount || !Kinds.Contains(c.Kind)
                || string.IsNullOrWhiteSpace(c.Label) || c.Label.Length > 80 || c.SourceHeader is null || c.SourceHeader.Length > 512)
            || source.Columns.Select(c => c.ColumnIndex).Distinct().Count() != source.Columns.Count)
            return "Calculation column mapping is invalid.";
        if (source.Columns.Count(c => c.Kind == "employeeName") != 1 || source.Columns.Count(c => c.Kind == "netPay") != 1
            || !source.Columns.Any(c => c.Kind == "earning") || new[] { "employeeCode", "email", "grossTotal", "deductionTotal" }.Any(k => source.Columns.Count(c => c.Kind == k) > 1))
            return "Map one employee name, one net pay and at least one earning column.";
        if (source.Rows.SelectMany(r => r.Cells).Any(c => c is not null && (c.Value is null || c.Value.Length > 10000 || c.FormulaText?.Length > 4096 || c.Error?.Length > 100 || c.CalculationError?.Length > 500)))
            return "A source cell exceeds its supported size.";
        if (batch.Rows is null || batch.Rows.Count is < 1 or > 1000 || batch.Rows.Select(r => r.SourceRow).Distinct().Count() != batch.Rows.Count
            || batch.Rows.Select(r => r.Id).Distinct().Count() != batch.Rows.Count)
            return "Calculation requires unique batch rows.";
        var sourceRows = source.Rows.ToDictionary(r => r.SourceRow);
        var excludedRows = source.ExcludedSourceRows.ToHashSet();
        foreach (var row in batch.Rows)
        {
            if (row.SourceRow <= source.HeaderRow || excludedRows.Contains(row.SourceRow) || !sourceRows.TryGetValue(row.SourceRow, out var saved))
                return $"Source row {row.SourceRow} is unavailable or excluded.";
            var positions = new Dictionary<string, int>();
            foreach (var column in source.Columns.Where(c => c.Kind != "ignore"))
            {
                var cell = column.ColumnIndex < saved.Cells.Count ? saved.Cells[column.ColumnIndex] : null;
                if ((cell is null && AmountKinds.Contains(column.Kind)) || cell?.Error is not null || cell?.MissingCachedValue == true)
                    return $"Source row {row.SourceRow}, {column.Label} has no usable saved value.";
                var value = cell?.Value ?? "";
                var text = value.Trim();
                var position = positions.GetValueOrDefault(column.Kind);
                positions[column.Kind] = position + 1;
                if ((column.Kind == "employeeName" && text != row.EmployeeName) || (column.Kind == "employeeCode" && text != row.EmployeeCode)
                    || (column.Kind == "email" && text != row.Email)) return $"Source row {row.SourceRow} does not match its employee details.";
                if (column.Kind == "info" && (position >= row.Information.Count || row.Information[position].Label != column.Label || row.Information[position].Value != value))
                    return $"Source row {row.SourceRow}, {column.Label} does not match its saved information.";
                if (AmountKinds.Contains(column.Kind) && (!TryNumber(value, out var amount) || !MatchesAmount(row, column, amount, position)))
                    return $"Source row {row.SourceRow}, {column.Label} does not match its saved amount.";
            }
            foreach (var kind in new[] { "earning", "deduction", "employer" })
            {
                var amounts = kind == "earning" ? row.Earnings : kind == "deduction" ? row.Deductions : row.EmployerContributions;
                if (amounts.Count != source.Columns.Count(c => c.Kind == kind)) return $"Source row {row.SourceRow} has an incomplete component mapping.";
            }
        }
        return null;
    }

    // XML retains the numeric cache spelling; the existing browser importer serializes an IEEE number.
    // Accept only the same representable number, never a cent-sized approximation or a changed amount.
    private static bool SameNumber(decimal? saved, decimal amount) => saved.HasValue && (saved.Value == amount
        || double.Parse(saved.Value.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) == double.Parse(amount.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture));
    private static bool MatchesAmount(ExcelPayslipRow row, ExcelPayslipCalculationColumn column, decimal amount, int position) => column.Kind switch
    {
        "netPay" => SameNumber(row.NetPay, amount),
        "grossTotal" => SameNumber(row.DeclaredGross, amount),
        "deductionTotal" => SameNumber(row.DeclaredDeductions, amount),
        _ => MatchesComponent(column.Kind == "earning" ? row.Earnings : column.Kind == "deduction" ? row.Deductions : row.EmployerContributions, column.Label, amount, position)
    };
    private static bool MatchesComponent(List<ExcelPayslipAmount> components, string label, decimal amount, int position)
        => position < components.Count && components[position].Label == label && SameNumber(components[position].Amount, amount);
    private static bool IsIdentityInformation(ExcelPayslipCalculationColumn column)
        => column.Kind == "info" && Regex.IsMatch(column.SourceHeader + " " + column.Label, @"account|bank|uan|aadh?aar|aadhar|adhaar|ifsc|mobile|phone|esic|\bpan\b|employee\s*(?:code|id)|identifier", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static ExcelPayslipBatch Calculate(ExcelPayslipCalculationSource source, ExcelPayslipBatch batch, ExcelPayslipCalculateRequest request)
    {
        if (ValidateSource(source, batch) is { } error) throw new InvalidOperationException(error);
        if (request is null) throw new InvalidOperationException("Enter the new month and attendance inputs.");
        if (!DateTime.TryParseExact(request.Month, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw new InvalidOperationException("Select a valid salary month (YYYY-MM).");
        if (request.AttendanceColumnIndex < 0 || request.AttendanceColumnIndex >= source.ColumnCount || request.Attendance is null
            || request.Attendance.Count != batch.Rows.Count || request.Attendance.Any(a => a is null || a.Days is < 0 or > 31)
            || request.Attendance.Select(a => a.RowId).Distinct().Count() != request.Attendance.Count
            || request.Attendance.Any(a => batch.Rows.All(r => r.Id != a.RowId)))
            throw new InvalidOperationException("Enter attendance days between 0 and 31 for every employee exactly once.");
        if (request.MonthDays is < 1 or > 31 || request.MonthDays.HasValue != !string.IsNullOrWhiteSpace(request.MonthDaysCell))
            throw new InvalidOperationException("Enter both a month-days input cell and its value (1-31), or leave both empty.");
        if (request.Overrides is null || request.Overrides.Count > 5000) throw new InvalidOperationException("Too many input overrides.");

        var copy = JsonSerializer.Deserialize<ExcelPayslipCalculationSource>(JsonSerializer.Serialize(source, Json), Json)!;
        copy.SourceBatchId = batch.Id;
        copy.AttendanceColumnIndex = request.AttendanceColumnIndex;
        copy.MonthDaysCell = request.MonthDaysCell;
        var result = JsonSerializer.Deserialize<ExcelPayslipBatch>(JsonSerializer.Serialize(batch, Json), Json)!;
        result.Id = ""; result.Month = request.Month; result.CreatedAtUtc = default; result.CreatedBy = "";
        result.SourceFileName = $"Calculated from {batch.Month} - {batch.SourceFileName}";
        if (result.SourceFileName.Length > 255) result.SourceFileName = result.SourceFileName[..255];
        var cells = copy.Rows.SelectMany(row => row.Cells.Select((cell, index) => (Address: Address(index, row.SourceRow), Cell: cell)))
            .Where(item => item.Cell is not null).ToDictionary(item => item.Address, item => item.Cell!, StringComparer.OrdinalIgnoreCase);
        var assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void SetInput(string rawAddress, decimal value)
        {
            var address = NormalizeAddress(rawAddress);
            if (!assigned.Add(address)) throw new InvalidOperationException($"Input cell {address} was supplied more than once.");
            if (!cells.TryGetValue(address, out var cell) || !string.IsNullOrWhiteSpace(cell.FormulaText) || cell.Error is not null || cell.CalculationError is not null
                || !TryNumber(cell.Value, out _)) throw new InvalidOperationException($"{address} must be an existing numeric input cell, not a formula or identifier.");
            // Never let numeric-looking bank accounts, employee codes or other identity fields be overridden.
            var (columnIndex, rowNumber) = ParseAddress(address);
            if (rowNumber > source.HeaderRow && source.Columns.Any(c => c.ColumnIndex == columnIndex && (c.Kind is "employeeName" or "employeeCode" or "email" || IsIdentityInformation(c))))
                throw new InvalidOperationException($"{address} is an employee identity field and cannot be changed by calculation.");
            EnsureBounded(value); cell.Value = value.ToString(CultureInfo.InvariantCulture);
            cell.MissingCachedValue = false;
        }
        foreach (var attendance in request.Attendance)
        {
            if (request.MonthDays.HasValue && attendance.Days > request.MonthDays.Value)
                throw new InvalidOperationException("Attendance cannot exceed the selected month-days value.");
            var row = batch.Rows.Single(r => r.Id == attendance.RowId);
            SetInput(Address(request.AttendanceColumnIndex, row.SourceRow), attendance.Days);
        }
        if (request.MonthDays.HasValue) SetInput(request.MonthDaysCell!, request.MonthDays.Value);
        foreach (var input in request.Overrides) SetInput(input.Key, input.Value);

        var attendanceAddresses = batch.Rows.Select(row => Address(request.AttendanceColumnIndex, row.SourceRow)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var evaluator = new CellEvaluator(cells, source.SheetName, attendanceAddresses);
        foreach (var row in result.Rows)
        {
            var attendanceDrivesSalary = false;
            row.Earnings = []; row.Deductions = []; row.EmployerContributions = []; row.Information = [];
            row.DeclaredGross = null; row.DeclaredDeductions = null;
            foreach (var column in source.Columns)
            {
                var address = Address(column.ColumnIndex, row.SourceRow);
                if (AmountKinds.Contains(column.Kind))
                {
                    var amount = evaluator.Read(address);
                    attendanceDrivesSalary |= !string.IsNullOrWhiteSpace(cells.GetValueOrDefault(address)?.FormulaText)
                        && evaluator.DependsOn(address, Address(request.AttendanceColumnIndex, row.SourceRow));
                    switch (column.Kind)
                    {
                        case "earning": row.Earnings.Add(new() { Label = column.Label, Amount = amount }); break;
                        case "deduction": row.Deductions.Add(new() { Label = column.Label, Amount = amount }); break;
                        case "employer": row.EmployerContributions.Add(new() { Label = column.Label, Amount = amount }); break;
                        case "netPay": row.NetPay = amount; break;
                        case "grossTotal": row.DeclaredGross = amount; break;
                        case "deductionTotal": row.DeclaredDeductions = amount; break;
                    }
                }
                else if (column.Kind == "info")
                {
                    var cell = cells.GetValueOrDefault(address);
                    // Numeric information such as bonus/rate must follow its saved formula too.
                    // Personal identifiers are immutable, even when their original workbook used a lookup.
                    if (!IsIdentityInformation(column) && cell is not null && TryNumber(cell.Value, out _)
                        && (!string.IsNullOrWhiteSpace(cell.FormulaText) || !string.IsNullOrWhiteSpace(cell.CalculationError))) evaluator.Read(address);
                    row.Information.Add(new() { Label = column.Label, Value = cell?.Value ?? "" });
                }
            }
            if (!attendanceDrivesSalary) throw new InvalidOperationException($"Source row {row.SourceRow}: the selected attendance column does not drive a saved salary formula. Import a formula-based sheet or choose the correct attendance input.");
            row.Warnings = ExcelPayslipRepository.RowWarnings(row).ToList();
        }
        evaluator.UpdateCachedValues();
        foreach (var row in result.Rows)
        {
            var infoIndex = 0;
            foreach (var info in source.Columns.Where(column => column.Kind == "info"))
                row.Information[infoIndex++].Value = cells.GetValueOrDefault(Address(info.ColumnIndex, row.SourceRow))?.Value ?? "";
        }
        result.CalculationSource = copy;
        return result;
    }

    private static bool TryNumber(string value, out decimal amount)
    {
        amount = 0;
        return NumberPattern.IsMatch(value.Trim()) && decimal.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out amount) && amount is >= -Limit and <= Limit;
    }
    private static decimal EnsureBounded(decimal value) => Math.Abs(value) <= Limit ? value : throw new InvalidOperationException("A calculated amount exceeds the supported limit.");
    internal static string Address(int columnIndex, int row)
    {
        var letters = "";
        for (var n = columnIndex + 1; n > 0; n = (n - 1) / 26) letters = (char)('A' + (n - 1) % 26) + letters;
        return letters + row.ToString(CultureInfo.InvariantCulture);
    }
    private static (int Column, int Row) ParseAddress(string address)
    {
        var match = AddressPattern.Match(address);
        if (!match.Success) throw new InvalidOperationException($"Unsupported cell reference: {address}.");
        var column = match.Groups[1].Value.ToUpperInvariant().Aggregate(0, (n, c) => n * 26 + c - 'A' + 1) - 1;
        var row = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        if (column >= 512 || row > 100000) throw new InvalidOperationException("Cell reference exceeds the supported worksheet range.");
        return (column, row);
    }
    private static string NormalizeAddress(string address) { var (column, row) = ParseAddress(address); return Address(column, row); }

    private sealed class CellEvaluator(Dictionary<string, ExcelPayslipCalculationCell> cells, string sheet, HashSet<string> attendanceInputs)
    {
        private readonly Dictionary<string, decimal> cache = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> active = new(StringComparer.OrdinalIgnoreCase);
        private readonly Stack<string> stack = new();
        private readonly Dictionary<string, HashSet<string>> dependencies = new(StringComparer.OrdinalIgnoreCase);
        private int operations;
        public bool DependsOn(string address, string input) => dependencies.TryGetValue(address, out var values) && values.Contains(input);
        private void CountWork() { if (++operations > 150000) throw new InvalidOperationException("Calculation dependency limit exceeded."); }
        private void LinkToParent(string address) { if (stack.TryPeek(out var parent)) dependencies[parent].UnionWith(dependencies[address]); }
        public decimal Read(string reference)
        {
            CountWork();
            var bang = reference.LastIndexOf('!');
            if (bang >= 0)
            {
                if (!reference[..bang].Trim('\'').Replace("''", "'").Equals(sheet, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Cross-worksheet and external references cannot be recalculated. Supply a self-contained salary worksheet.");
                reference = reference[(bang + 1)..];
            }
            var address = NormalizeAddress(reference);
            if (cache.TryGetValue(address, out var known)) { LinkToParent(address); return known; }
            if (active.Count >= 128) throw new InvalidOperationException("Calculation dependency limit exceeded.");
            if (!active.Add(address)) throw new InvalidOperationException($"Circular formula dependency at {address}.");
            dependencies[address] = attendanceInputs.Contains(address) ? [address] : [];
            stack.Push(address);
            try
            {
                if (!cells.TryGetValue(address, out var cell)) throw new InvalidOperationException($"Required input {address} is missing.");
                if (!string.IsNullOrWhiteSpace(cell.CalculationError)) throw new InvalidOperationException($"Saved formula cannot be recalculated: {cell.CalculationError}");
                decimal value;
                if (!string.IsNullOrWhiteSpace(cell.FormulaText)) value = new Formula(cell.FormulaText, this).Parse().Value();
                else if (cell.Error is not null || !TryNumber(cell.Value, out value)) throw new InvalidOperationException($"Required input {address} is not a valid number.");
                return cache[address] = EnsureBounded(value);
            }
            catch (Exception exception) when (exception is OverflowException or DivideByZeroException)
            { throw new InvalidOperationException($"Formula at {address} cannot be calculated: {exception.Message}"); }
            catch (InvalidOperationException exception) { throw new InvalidOperationException($"{address}: {exception.Message}"); }
            finally { active.Remove(address); stack.Pop(); LinkToParent(address); }
        }
        public IEnumerable<decimal> Range(string first, string last)
        {
            var (c1, r1) = ParseAddress(first); var (c2, r2) = ParseAddress(last);
            if (c2 < c1 || r2 < r1 || (long)(c2 - c1 + 1) * (r2 - r1 + 1) > 10000) throw new InvalidOperationException("Formula range is reversed or too large.");
            for (var row = r1; row <= r2; row++) for (var col = c1; col <= c2; col++)
            {
                CountWork();
                var address = Address(col, row);
                if (!cells.TryGetValue(address, out var cell) || (string.IsNullOrWhiteSpace(cell.FormulaText) && cell.Error is null && !TryNumber(cell.Value, out _))) continue;
                yield return Read(address);
            }
        }
        public void UpdateCachedValues() { foreach (var value in cache) { cells[value.Key].Value = value.Value.ToString("G29", CultureInfo.InvariantCulture); cells[value.Key].Error = null; cells[value.Key].MissingCachedValue = false; } }
    }

    private sealed record Node(Func<decimal> Value, Func<IEnumerable<decimal>>? Range = null);
    private sealed class Formula(string text, CellEvaluator cells)
    {
        private int index, depth, nodes;
        private char Current => index < text.Length ? text[index] : '\0';
        public Node Parse()
        {
            Match('='); var value = Compare(); Skip();
            if (index != text.Length) throw new InvalidOperationException($"Unsupported formula syntax near character {index + 1}.");
            return value;
        }
        private Node Compare()
        {
            var left = Sum(); Skip(); var start = index;
            while (Current is '<' or '>' or '=') index++;
            if (index == start) return left;
            var operation = text[start..index]; var right = Sum();
            if (operation is not ("=" or "<>" or "<" or ">" or "<=" or ">=")) throw new InvalidOperationException("Unsupported comparison.");
            return new(() => (operation switch { "=" => left.Value() == right.Value(), "<>" => left.Value() != right.Value(), "<" => left.Value() < right.Value(), ">" => left.Value() > right.Value(), "<=" => left.Value() <= right.Value(), _ => left.Value() >= right.Value() }) ? 1m : 0m);
        }
        private Node Sum()
        {
            var node = Product();
            while (true) { Skip(); var op = Current; if (op is not ('+' or '-')) return node; index++; var left = node; var right = Product(); node = new(() => op == '+' ? left.Value() + right.Value() : left.Value() - right.Value()); }
        }
        private Node Product()
        {
            var node = Atom();
            while (true) { Skip(); var op = Current; if (op is not ('*' or '/')) return node; index++; var left = node; var right = Atom(); node = new(() => op == '*' ? left.Value() * right.Value() : left.Value() / right.Value()); }
        }
        private Node Atom()
        {
            if (++depth > 64 || ++nodes > 512) throw new InvalidOperationException("Formula nesting or size limit exceeded.");
            try
            {
                Skip(); Node node;
                if (Match('+')) node = Atom();
                else if (Match('-')) { var value = Atom(); node = new(() => -value.Value()); }
                else if (Match('(')) { node = Compare(); Require(')'); }
                else if (char.IsDigit(Current) || Current == '.')
                {
                    var start = index;
                    while (char.IsDigit(Current) || Current == '.') index++;
                    if (Current is 'e' or 'E') { index++; if (Current is '+' or '-') index++; while (char.IsDigit(Current)) index++; }
                    if (!decimal.TryParse(text[start..index], NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) throw new InvalidOperationException("Invalid formula number.");
                    node = new(() => number);
                }
                else
                {
                    var name = Identifier();
                    if (Match('('))
                    {
                        var args = new List<Node>();
                        if (!Match(')')) { do { if (args.Count >= 256) throw new InvalidOperationException("Too many formula arguments."); args.Add(Compare()); } while (Match(',')); Require(')'); }
                        node = Function(name.ToUpperInvariant(), args);
                    }
                    else if (Match(':'))
                    {
                        var end = Identifier();
                        node = new(() => throw new InvalidOperationException("Use cell ranges inside SUM, MIN or MAX."), () => cells.Range(name, end));
                    }
                    else if (name.Equals("TRUE", StringComparison.OrdinalIgnoreCase) || name.Equals("FALSE", StringComparison.OrdinalIgnoreCase)) node = new(() => name.Equals("TRUE", StringComparison.OrdinalIgnoreCase) ? 1m : 0m);
                    else node = new(() => cells.Read(name));
                }
                while (Match('%')) { var previous = node; node = new(() => previous.Value() / 100m); }
                return node;
            }
            finally { depth--; }
        }
        private static Node Function(string name, List<Node> args)
        {
            IEnumerable<decimal> Values() => args.SelectMany(arg => arg.Range?.Invoke() ?? [arg.Value()]);
            if (name is "SUM" or "MIN" or "MAX") return new(() => { var values = Values().ToArray(); return name == "SUM" ? values.Sum() : values.Length == 0 ? 0 : name == "MIN" ? values.Min() : values.Max(); });
            if (name == "IF" && args.Count == 3) return new(() => args[0].Value() != 0 ? args[1].Value() : args[2].Value());
            if (name is "AND" or "OR" && args.Count > 0) return new(() => { var values = Values().ToArray(); return (name == "AND" ? values.All(v => v != 0) : values.Any(v => v != 0)) ? 1m : 0m; });
            if (name == "ABS" && args.Count == 1) return new(() => Math.Abs(args[0].Value()));
            if (name is "ROUND" or "ROUNDDOWN" or "ROUNDUP" && args.Count == 2) return new(() =>
            {
                var places = args[1].Value();
                if (places != decimal.Truncate(places) || places is < -6 or > 8) throw new InvalidOperationException("Rounding precision must be between -6 and 8.");
                var scale = (decimal)Math.Pow(10, (int)places); var value = args[0].Value() * scale;
                return (name == "ROUND" ? Math.Round(value, 0, MidpointRounding.AwayFromZero) : name == "ROUNDDOWN" ? decimal.Truncate(value) : Math.Sign(value) * decimal.Ceiling(Math.Abs(value))) / scale;
            });
            throw new InvalidOperationException($"Unsupported function or argument count: {name}.");
        }
        private string Identifier()
        {
            Skip(); var start = index;
            if (Current == '\'')
            {
                index++;
                while (Current != '\0') { if (Current == '\'') { index++; if (Current != '\'') break; } index++; }
                if (!Match('!')) throw new InvalidOperationException("Invalid worksheet reference.");
            }
            while (char.IsLetterOrDigit(Current) || Current is '$' or '_' or '.' or '!') index++;
            if (start == index) throw new InvalidOperationException($"Unsupported formula token at character {index + 1}.");
            return text[start..index];
        }
        private void Skip() { while (char.IsWhiteSpace(Current)) index++; }
        private bool Match(char value) { Skip(); if (Current != value) return false; index++; return true; }
        private void Require(char value) { if (!Match(value)) throw new InvalidOperationException($"Expected '{value}' in formula."); }
    }
}
