using System.Globalization;
using System.Text.RegularExpressions;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayrollSetupRepository
{
    internal sealed record ConstantRecurringInputEvidence(int SourceRow, int ColumnIndex, string Header, string Formula, decimal Value);

    // A numeric entry expression is evaluated independently, never replaced by
    // its cache. Cell references/functions cannot be hidden in a lazy branch.
    internal static ConstantRecurringInputEvidence InspectConstantRecurringInput(ExcelPayrollSetupRequest request,
        ExcelPayslipCalculationSource source, ExcelPayslipRow row, ExcelPayrollSetupComponent component)
    {
        if (!request.ReviewedFixedInputs || !request.ReviewedPolicyScope || component.IsPeriodAdjustment
            || component.CalculationType is not ("Fixed Amount" or "Daily Rate")
            || component.SourceColumnIndex is not int column || column < 0 || column >= source.ColumnCount
            || !request.RowIds.Contains(row.Id)
            || column == request.PayrollDaysColumnIndex || column == request.PayableDaysColumnIndex
            || column == request.PresentDaysColumnIndex || column == request.MonthlyCtcColumnIndex
            || request.Components.Count(c => c.SourceColumnIndex == column) != 1
            || request.Components.Any(c => c.ExpectedColumnIndex == column))
            throw new InvalidOperationException("Review one independent recurring monthly/rate input; financial outputs, day quantities and period adjustments cannot use numeric entry arithmetic.");

        var metadata = source.Columns.Where(c => c.ColumnIndex == column).ToArray();
        var captured = source.Rows.Where(r => r.SourceRow == row.SourceRow).ToArray();
        if (metadata.Length != 1 || metadata[0].Kind is not ("info" or "ignore")
            || request.SourceColumnRoles.GetValueOrDefault(column, metadata[0].Kind) != "info"
            || ProtectedRecurringInputHeader(metadata[0].SourceHeader + " " + metadata[0].Label)
            || captured.Length != 1 || captured[0].Cells.ElementAtOrDefault(column) is not { DataType: "Number" } cell
            || cell.Error is not null || cell.CalculationError is not null || cell.MissingCachedValue
            || string.IsNullOrWhiteSpace(cell.FormulaText))
            throw new InvalidOperationException("A reviewed numeric INFO input and its complete error-free captured entry expression are required; no missing cache, identifier or output is substituted.");

        var expression = cell.FormulaText.Trim();
        if (expression.StartsWith('=')) expression = expression[1..];
        if (expression.Length is 0 or > 1000 || !Regex.IsMatch(expression, @"\A[0-9eE.\s()+*/%\-]+\z", RegexOptions.CultureInvariant))
            throw new InvalidOperationException("A recurring entry expression may contain only bounded numbers, arithmetic, parentheses and percent; cell references, functions and workbook links remain blocked.");

        var value = PayrollFormulaEvaluator.Evaluate(expression, _ =>
            throw new InvalidOperationException("A recurring numeric entry cannot resolve an employee, cell or payroll variable."));
        if (value is < 0 or > 1_000_000_000m || decimal.Round(value, 4) != value)
            throw new InvalidOperationException("The independently evaluated recurring input must be non-negative, bounded and exactly representable in the existing four-decimal salary store.");
        if (!decimal.TryParse(cell.Value, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var cache)
            || cache != value)
            throw new InvalidOperationException("The captured entry cache differs from its independently evaluated numeric expression; no input tolerance or cached amount override is used.");

        return new(row.SourceRow, column, metadata[0].SourceHeader, cell.FormulaText, value);
    }
}
