using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Services;

/// <summary>Bounded, independent calculation of saved Excel salary cells. No payroll rules or database access.</summary>
public static partial class ExcelPayslipCalculationService
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
            || source.ColumnCount is < 1 or > 1500 || source.DateSystem is not ("1900" or "1904"))
            return "The calculation worksheet or header does not match this batch.";
        if (source.Rows.Count is < 1 or > 100000 || source.Rows.Any(r => r is null || r.Cells is null || r.SourceRow is < 1 or > 100000 || r.Cells.Count > source.ColumnCount)
            || source.Rows.Select(r => r.SourceRow).Distinct().Count() != source.Rows.Count || source.Rows.Sum(r => (long)r.Cells.Count) > 150000)
            return "Calculation source exceeds its row/cell limits or contains duplicate rows.";
        if (source.Columns.Count is < 1 or > 1500 || source.Columns.Any(c => c is null || c.ColumnIndex < 0 || c.ColumnIndex >= source.ColumnCount || !Kinds.Contains(c.Kind)
                || string.IsNullOrWhiteSpace(c.Label) || c.Label.Length > 80 || c.SourceHeader is null || c.SourceHeader.Length > 512 || c.BlankAsZero && !AmountKinds.Contains(c.Kind))
            || source.Columns.Select(c => c.ColumnIndex).Distinct().Count() != source.Columns.Count)
            return "Calculation column mapping is invalid.";
        if (source.Columns.Count(c => c.Kind == "employeeName") != 1 || source.Columns.Count(c => c.Kind == "netPay") != 1
            || !source.Columns.Any(c => c.Kind == "earning") || new[] { "employeeCode", "email", "grossTotal", "deductionTotal" }.Any(k => source.Columns.Count(c => c.Kind == k) > 1))
            return "Map one employee name, one net pay and at least one earning column.";
        if (source.Rows.SelectMany(r => r.Cells).Any(c => c is not null && (c.Value is null || c.Value.Length > 10000 || c.FormulaText?.Length > 4096 || c.Error?.Length > 100 || c.CalculationError?.Length > 500 || c.DataType is not ("" or "Number" or "Text" or "Boolean" or "Blank"))))
            return "A source cell exceeds its supported size.";
        try { ValidateWorkbookDependencies(source); } catch (InvalidOperationException error) { return error.Message; }
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
                var reviewedBlank = column.BlankAsZero && PlainBlank(cell);
                if ((cell is null && AmountKinds.Contains(column.Kind) && !reviewedBlank) || cell?.Error is not null || cell?.MissingCachedValue == true)
                    return $"Source row {row.SourceRow}, {column.Label} has no usable saved value.";
                var value = cell?.Value ?? "";
                var text = value.Trim();
                var position = positions.GetValueOrDefault(column.Kind);
                positions[column.Kind] = position + 1;
                if ((column.Kind == "employeeName" && text != row.EmployeeName) || (column.Kind == "employeeCode" && text != row.EmployeeCode)
                    || (column.Kind == "email" && text != row.Email)) return $"Source row {row.SourceRow} does not match its employee details.";
                if (column.Kind == "info" && (position >= row.Information.Count || row.Information[position].Label != column.Label || row.Information[position].Value != value))
                    return $"Source row {row.SourceRow}, {column.Label} does not match its saved information.";
                var amount = 0m;
                if (AmountKinds.Contains(column.Kind) && ((!reviewedBlank && !TryNumber(value, out amount)) || !MatchesAmount(row, column, amount, position)))
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

    // Excel caches and browser JSON can differ in their final IEEE digits. Parse
    // decimal text to double directly: decimal-to-double casts can round twice
    // at a binary midpoint. Keep the 15-digit fallback and preserve stored values.
    private static bool SameNumber(decimal? saved, decimal amount) => saved.HasValue && (saved.Value == amount
        || double.Parse(saved.Value.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)
            == double.Parse(amount.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)
        || saved.Value.ToString("G15", CultureInfo.InvariantCulture) == amount.ToString("G15", CultureInfo.InvariantCulture));
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
    private static bool PlainBlank(ExcelPayslipCalculationCell? cell) => cell is null || string.IsNullOrWhiteSpace(cell.Value)
        && string.IsNullOrWhiteSpace(cell.FormulaText) && cell.Error is null && !cell.MissingCachedValue && cell.CalculationError is null;

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
            EnsureBounded(value); cell.Value = value.ToString(CultureInfo.InvariantCulture); cell.DataType = "Number";
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
        var reviewedBlankInputs = batch.Rows.SelectMany(row => source.Columns.Where(column => column.BlankAsZero && AmountKinds.Contains(column.Kind))
            .Select(column => Address(column.ColumnIndex, row.SourceRow))).Where(address => PlainBlank(cells.GetValueOrDefault(address))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var evaluator = new CellEvaluator(cells, source.SheetName, attendanceAddresses, reviewedBlankInputs);
        evaluator.Configure(copy);
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
        copy.LookupTraces = evaluator.Traces.ToList();
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
    internal static (int Column, int Row) ParseAddress(string address)
    {
        var match = AddressPattern.Match(address);
        if (!match.Success) throw new InvalidOperationException($"Unsupported cell reference: {address}.");
        var column = match.Groups[1].Value.ToUpperInvariant().Aggregate(0, (n, c) => n * 26 + c - 'A' + 1) - 1;
        var row = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        if (column >= 1500 || row > 100000) throw new InvalidOperationException("Cell reference exceeds the supported worksheet range.");
        return (column, row);
    }
    private static string NormalizeAddress(string address) { var (column, row) = ParseAddress(address); return Address(column, row); }

    // Compatibility facade for existing saved-sheet callers; all formula work uses the bounded workbook evaluator.
    private sealed class CellEvaluator(Dictionary<string,ExcelPayslipCalculationCell> cells,string sheet,HashSet<string> attendanceInputs,HashSet<string> reviewedBlankInputs,IReadOnlyDictionary<string,decimal>? fixedInputs=null)
    {
        private WorkbookEvaluator workbook=new(cells,sheet,attendanceInputs,reviewedBlankInputs,fixedInputs);
        public void Configure(ExcelPayslipCalculationSource source)=>workbook=new(cells,sheet,attendanceInputs,reviewedBlankInputs,fixedInputs,source);
        public decimal Read(string reference) { try { return workbook.Read(reference); } catch (WorkbookError error) { throw new InvalidOperationException($"Formula at {reference} cannot be calculated: {error.Message}",error); } }
        public bool DependsOn(string address,string input)=>workbook.DependsOn(address,input);
        public void UpdateCachedValues()=>workbook.UpdateCachedValues();
        public IReadOnlyList<ExcelPayslipLookupTrace> Traces=>workbook.Traces;
    }
}
