using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayrollSetupRepository
{
    internal static decimal ReviewedSourceAnnualCtc(ExcelPayrollSetupRequest request, ExcelPayslipCalculationSource? source,
        ExcelPayslipCalculationSourceRow cells, ExcelPayslipRow row)
    {
        var index = request.MonthlyCtcColumnIndex ?? throw new InvalidOperationException("Select the reviewed monthly CTC column.");
        var column = source?.Columns.SingleOrDefault(column => column.ColumnIndex == index);
        if (source is null || column is null || !request.ReviewedFixedInputs
            || request.SourceColumnRoles.GetValueOrDefault(index, column.Kind) != "info"
            || column.Kind is "employeeName" or "employeeCode" or "email" or "netPay" or "earning" or "deduction" or "employer"
            || ProtectedRecurringInputHeader(column.SourceHeader + " " + column.Label)
            || System.Text.RegularExpressions.Regex.IsMatch(column.SourceHeader,@"\b(?:ID\s*(?:NO|NUMBER)|SR\s*NO|SERIAL|CODE)\b",System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            || index == request.PayrollDaysColumnIndex || index == request.PayableDaysColumnIndex || index == request.PresentDaysColumnIndex
            || request.Components.Any(component => component.ExpectedColumnIndex == index))
            throw new InvalidOperationException("Monthly CTC requires a reviewed Information column, separate from earned outputs, identities and attendance. No earned salary is annualized.");
        var monthly = ReadNumber(cells, index);
        var cell = cells.Cells[index]!;
        if (!string.IsNullOrWhiteSpace(cell.FormulaText))
        {
            // Validate the actual monthly dependencies; a cached earned output or lookup is not a CTC basis.
            var translation = TranslateTransferFormulaWithEvidence(cell.FormulaText, request, source, row);
            if (System.Text.RegularExpressions.Regex.IsMatch(translation.Formula,
                @"\b(?:[A-Z][A-Z0-9_]*_EARNED|PAYROLL_DAYS|PAYABLE_DAYS|PRESENT_DAYS|LOP_DAYS)\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                throw new InvalidOperationException("Monthly CTC cannot depend on earned outputs or attendance. Select the independent full-month salary total.");
            var evaluated = ExcelPayslipCalculationService.EvaluateCapturedCellAtInputBoundary(source, TransferLetter(index) + cells.SourceRow, null,
                requireExplicitCells: source.CaptureVersion > 0);
            if (SourceDisplayPrecision(evaluated.Value) != SourceDisplayPrecision(monthly))
                throw new InvalidOperationException("The captured monthly CTC formula and its saved value differ. Recalculate/review the source; no cached CTC is substituted.");
        }
        if (monthly <= 0 || monthly * 12m > 1_000_000_000m)
            throw new InvalidOperationException("Monthly CTC must establish a positive bounded annual salary target.");
        return decimal.Round(monthly * 12m, 2, MidpointRounding.AwayFromZero);
    }
}
