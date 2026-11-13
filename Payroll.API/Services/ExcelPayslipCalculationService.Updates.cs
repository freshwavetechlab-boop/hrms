using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Payroll.API.Models;

namespace Payroll.API.Services;

public static partial class ExcelPayslipCalculationService
{
    // Existing formulas remain authoritative. This path accepts only reviewed
    // plain numeric inputs and evaluates their bounded dependency closure.
    public static ExcelPayslipCalculationSource RecalculateReviewedInputs(ExcelPayslipCalculationSource source,
        ExcelPayslipBatch batch, IReadOnlyList<ExcelPayslipCellCorrection> edits)
    {
        if (ValidateSource(source, batch) is { } error) throw new InvalidOperationException(error);
        if (edits.Count is < 1 or > 5000) throw new InvalidOperationException("Review between 1 and 5000 numeric inputs.");
        var copy = JsonSerializer.Deserialize<ExcelPayslipCalculationSource>(JsonSerializer.Serialize(source, Json), Json)!;
        var cells = copy.Rows.SelectMany(r => r.Cells.Select((cell, index) => (Address: Address(index, r.SourceRow), Cell: cell)))
            .Where(v => v.Cell is not null).ToDictionary(v => v.Address, v => v.Cell!, StringComparer.OrdinalIgnoreCase);
        var inputs = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var edit in edits)
        {
            var column = copy.Columns.SingleOrDefault(c => c.ColumnIndex == edit.ColumnIndex);
            var address = Address(edit.ColumnIndex, edit.SourceRow);
            if (column is null || column.Kind is "ignore" or "employeeCode" or "employeeName" or "email"
                || IsIdentityInformation(column) || batch.Rows.All(r => r.SourceRow != edit.SourceRow)
                || !cells.TryGetValue(address, out var cell) || !string.IsNullOrWhiteSpace(cell.FormulaText)
                || !inputs.TryAdd(address, EnsureBounded(edit.Amount)))
                throw new InvalidOperationException("Only distinct, included plain numeric inputs can be updated; saved formula expressions cannot be overwritten.");
        }
        var dependants = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var work = 0;
        foreach (var (address, cell) in cells.Where(v => !string.IsNullOrWhiteSpace(v.Value.FormulaText)))
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
        foreach (var (address, value) in inputs)
        {
            cells[address].Value = value.ToString(CultureInfo.InvariantCulture);
            cells[address].DataType = "Number";
            cells[address].Error = null; cells[address].CalculationError = null; cells[address].MissingCachedValue = false;
        }
        var fixedInputs = cells.Where(v => !affected.Contains(v.Key) && v.Value.Error is null && !v.Value.MissingCachedValue && TryNumber(v.Value.Value, out _))
            .ToDictionary(v => v.Key, v => decimal.Parse(v.Value.Value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture), StringComparer.OrdinalIgnoreCase);
        foreach (var input in inputs) fixedInputs[input.Key] = input.Value;
        var blanks = cells.Where(v => PlainBlank(v.Value)).Select(v => v.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var evaluator = new CellEvaluator(cells, copy.SheetName, [], blanks, fixedInputs);
        evaluator.Configure(copy);
        foreach (var address in affected)
        {
            var (col, _) = ParseAddress(address);
            if (copy.Columns.Any(c => c.ColumnIndex == col && (c.Kind is "employeeCode" or "employeeName" or "email" || IsIdentityInformation(c))))
                throw new InvalidOperationException("A numeric dependency reaches an identity field. Review this workbook through the original mapping flow.");
            evaluator.Read(address);
        }
        evaluator.UpdateCachedValues();
        copy.LookupTraces = evaluator.Traces.ToList();
        return copy;
    }
}
