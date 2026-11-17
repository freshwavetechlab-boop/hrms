using System.Globalization;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayrollSetupRepository
{
    // One bounded source normalization for a genuine daily-rate earning:
    // ((independent rate * fixed whole days) / the same literal days) * payable.
    // This proves the native Daily Rate operation; it does not change runtime
    // _MONTHLY units, infer attendance or normalize arbitrary salary algebra.
    internal static bool HasExactDailyRateSourceNormalization(ExcelPayrollSetupRequest request,
        ExcelPayslipCalculationSource source, ExcelPayslipRow row, ExcelPayrollSetupComponent component,
        TransferFormulaTranslation translation, string configured)
    {
        if (component.CalculationType != "Daily Rate" || component.IsPeriodAdjustment || component.ExemptWhenSourceZero
            || component.Category != "Earning"
            || component.SourceColumnIndex is not int rateColumn || component.ExpectedColumnIndex is not int outputColumn
            || rateColumn < 0 || rateColumn >= source.ColumnCount || rateColumn == outputColumn
            || rateColumn == request.PayableDaysColumnIndex || rateColumn == request.PresentDaysColumnIndex
            || !request.FixedPayrollDays.HasValue && rateColumn == request.PayrollDaysColumnIndex
            || request.Components.Count(c => !c.IsPeriodAdjustment && c.SourceColumnIndex == rateColumn) != 1
            || request.Components.Count(c => c.ExpectedColumnIndex == outputColumn) != 1
            // Inspect the provenance before structural numeric comparison. An
            // INFO literal is a source input, not a captured fixed factor. Even
            // equal values from two separate cells must never cancel here.
            || translation.Dependencies.Any(d => d.Literal is not null || string.IsNullOrWhiteSpace(d.Formula)))
            return false;

        var rows = source.Rows.Where(r => r.SourceRow == row.SourceRow).ToArray();
        if (rows.Length != 1) return false;
        var rate = rows[0].Cells.ElementAtOrDefault(rateColumn);
        if (rate is null || !string.IsNullOrWhiteSpace(rate.FormulaText) || rate.Error is not null
            || rate.CalculationError is not null || rate.MissingCachedValue
            || !decimal.TryParse(rate.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            || value is <= 0 or > 1_000_000_000m || decimal.Round(value, 4) != value)
            return false;

        // The engine publishes _MONTHLY as daily rate * payroll days. Preserve
        // that unit in every source-rate reference; do not treat the monthly
        // value as a rate merely because a threshold happens to give the same
        // branch for this month's employee.
        var token = "(" + component.Code.ToUpperInvariant() + "_MONTHLY/PAYROLL_DAYS)";
        if (PayrollFormulaEvaluator.CanonicalStructure(configured)
            != PayrollFormulaEvaluator.CanonicalStructure(token + "*PAYABLE_DAYS")) return false;
        var actual = PayrollFormulaEvaluator.CanonicalStructure(translation.Formula);
        for (var fixedDays = 1; fixedDays <= 31; fixedDays++)
        {
            var literal = fixedDays.ToString(CultureInfo.InvariantCulture);
            if (actual == PayrollFormulaEvaluator.CanonicalStructure($"(({token}*{literal})/{literal})*PAYABLE_DAYS"))
                return true;
        }
        return false;
    }
}
