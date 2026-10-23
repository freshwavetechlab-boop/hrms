using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Payroll.API.Models;

namespace Payroll.API.Services;

public static partial class ExcelPayslipCalculationService
{
    private const string ManualCorrection = "This formula amount was manually corrected for this batch. Re-import a reviewed salary formula before creating another month.";

    // Read-only preview using the same bounded Excel evaluator as Create another month.
    // Evaluate only dependants of changed cells; unrelated salary caches and employees stay intact.
    public static ExcelPayslipCalculationSource RecalculateCorrections(ExcelPayslipCorrectionPreview request)
    {
        var batch = request.Batch;
        var source = batch.CalculationSource ?? throw new InvalidOperationException("This batch has no saved formulas. Upload the recalculated workbook.");
        if (ValidateSource(source, batch) is { } error) throw new InvalidOperationException(error);
        if (request.Edits is null || request.Edits.Count is < 1 or > 5000) throw new InvalidOperationException("Enter between 1 and 5000 amount corrections.");
        var copy = JsonSerializer.Deserialize<ExcelPayslipCalculationSource>(JsonSerializer.Serialize(source, Json), Json)!;
        var cells = copy.Rows.SelectMany(r => r.Cells.Select((cell, index) => (Address: Address(index, r.SourceRow), Cell: cell)))
            .Where(x => x.Cell is not null).ToDictionary(x => x.Address, x => x.Cell!, StringComparer.OrdinalIgnoreCase);
        var inputs = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var explicitInputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var editedRows = new HashSet<int>();
        foreach (var edit in request.Edits)
        {
            var column = copy.Columns.SingleOrDefault(c => c.ColumnIndex == edit.ColumnIndex);
            if (column is null || !AmountKinds.Contains(column.Kind) || batch.Rows.All(r => r.SourceRow != edit.SourceRow))
                throw new InvalidOperationException("Only included, mapped salary amount cells can be corrected.");
            var address = Address(edit.ColumnIndex, edit.SourceRow);
            if (!explicitInputs.Add(address) || !cells.TryGetValue(address, out var cell)) throw new InvalidOperationException("A correction cell is missing or duplicated.");
            EnsureBounded(edit.Amount);
            if (inputs.TryGetValue(address, out var previous) && previous != edit.Amount) throw new InvalidOperationException("Conflicting corrections refer to the same salary input.");
            inputs[address] = edit.Amount; editedRows.Add(edit.SourceRow);
            // Older combined workbooks copied each formula into a raw Original column and
            // its mapped counterpart. Resolve that unique mirror, rather than editing an
            // unrelated input or leaving the net formula on the old raw value.
            if (!string.IsNullOrWhiteSpace(cell.FormulaText))
            {
                var mirrors = copy.Columns.Where(c => c.Kind == "ignore" && c.SourceHeader == "Original " + Address(c.ColumnIndex, 1).TrimEnd('1'))
                    .Select(c => Address(c.ColumnIndex, edit.SourceRow)).Where(a => cells.TryGetValue(a, out var raw)
                        && raw.FormulaText == cell.FormulaText && (raw.Value == cell.Value || cell.CalculationError == ManualCorrection
                            || TryNumber(raw.Value, out var left) && TryNumber(cell.Value, out var right) && SameNumber(left, right))).ToArray();
                if (mirrors.Length > 1) throw new InvalidOperationException($"{address}: multiple source formula copies match. Re-upload the corrected workbook.");
                if (mirrors.Length == 1)
                {
                    if (inputs.TryGetValue(mirrors[0], out previous) && previous != edit.Amount) throw new InvalidOperationException("Conflicting corrections refer to the same salary input.");
                    inputs[mirrors[0]] = edit.Amount;
                }
            }
        }
        // Record reverse dependencies, including SUM ranges. Text literals are not references.
        var dependants = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var work = 0;
        foreach (var (address, cell) in cells.Where(x => !string.IsNullOrWhiteSpace(x.Value.FormulaText)))
        {
            var expression = Regex.Replace(cell.FormulaText!, "\"(?:[^\"]|\"\")*\"", "");
            foreach (Match match in Regex.Matches(expression, @"(?<![A-Z0-9_])\$?[A-Z]{1,3}\$?[1-9]\d*(?:\s*:\s*\$?[A-Z]{1,3}\$?[1-9]\d*)?(?![A-Z0-9_(])", RegexOptions.IgnoreCase))
            {
                var bounds = match.Value.Split(':'); var start = ParseAddress(bounds[0].Trim()); var end = ParseAddress(bounds[^1].Trim());
                for (var row = Math.Min(start.Row, end.Row); row <= Math.Max(start.Row, end.Row); row++)
                    for (var col = Math.Min(start.Column, end.Column); col <= Math.Max(start.Column, end.Column); col++)
                    {
                        if (++work > 150000) throw new InvalidOperationException("Calculation dependency limit exceeded.");
                        var input = Address(col, row);
                        if (!dependants.TryGetValue(input, out var targets)) dependants[input] = targets = [];
                        targets.Add(address);
                    }
            }
        }
        var affected = inputs.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase); var queue = new Queue<string>(affected);
        while (queue.TryDequeue(out var input))
            if (dependants.TryGetValue(input, out var targets)) foreach (var target in targets) if (affected.Add(target)) queue.Enqueue(target);
        foreach (var row in editedRows)
        {
            var net = copy.Columns.Single(c => c.Kind == "netPay");
            if (!affected.Contains(Address(net.ColumnIndex, row))) throw new InvalidOperationException($"Source row {row}: no saved formula links this correction to In Hand Salary. Upload a recalculated workbook or turn off formula recalculation to correct totals manually.");
        }
        foreach (var (address, amount) in inputs)
        {
            var cell = cells[address]; cell.Value = amount.ToString(CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(cell.FormulaText)) cell.CalculationError = ManualCorrection;
            else cell.CalculationError = null;
            cell.Error = null; cell.MissingCachedValue = false;
        }
        var fixedInputs = cells.Where(x => !affected.Contains(x.Key) && x.Value.Error is null && !x.Value.MissingCachedValue && TryNumber(x.Value.Value, out _))
            .ToDictionary(x => x.Key, x => decimal.Parse(x.Value.Value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture), StringComparer.OrdinalIgnoreCase);
        foreach (var input in inputs) fixedInputs[input.Key] = input.Value;
        // Excel arithmetic treats an actual blank cell reference as zero; errors and
        // formulas without cached values are never included in this set.
        var blanks = cells.Where(x => PlainBlank(x.Value)).Select(x => x.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var evaluator = new CellEvaluator(cells, copy.SheetName, [], blanks, fixedInputs);
        foreach (var address in affected)
        {
            var (col, row) = ParseAddress(address);
            if (copy.Columns.Any(c => c.ColumnIndex == col && (c.Kind is "employeeName" or "employeeCode" or "email" || IsIdentityInformation(c))))
                throw new InvalidOperationException($"{address} is an employee identity field and cannot be recalculated.");
            evaluator.Read(address);
        }
        evaluator.UpdateCachedValues();
        return copy;
    }
}
