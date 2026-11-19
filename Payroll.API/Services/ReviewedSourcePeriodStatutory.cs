using System.Globalization;
using Payroll.API.Models;

namespace Payroll.API.Services;

internal sealed record SourcePeriodStatutoryComponent(string Code, string Category, string StatutoryType);
internal static class ReviewedSourcePeriodStatutory
{
    internal static void RequireNoPositiveSourcePt(ExcelPayslipCalculationSource source,ExcelPayslipRow row)
    {
        var columns=source.Columns.Where(column=>column.Kind=="deduction"&&System.Text.RegularExpressions.Regex.IsMatch(
            column.SourceHeader+" "+column.Label,@"\bP[\s.]*T\b|PROF(?:ESSIONAL)?[\s.]*TAX|\bP[\s.]*TAX\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase|System.Text.RegularExpressions.RegexOptions.CultureInvariant)).ToArray();
        if(columns.Length==0)return; // Absence still requires explicit operator PT-zero review.
        var rows=source.Rows.Where(value=>value.SourceRow==row.SourceRow).ToArray();
        if(rows.Length!=1)throw new InvalidOperationException("The source PT row is missing or ambiguous.");
        foreach(var column in columns)
        {
            var cell=rows[0].Cells.ElementAtOrDefault(column.ColumnIndex);
            if(cell is null||cell.Error is not null||cell.CalculationError is not null||cell.MissingCachedValue
                ||cell.DataType is not("" or "Number")||string.IsNullOrWhiteSpace(cell.Value)
                ||!decimal.TryParse(cell.Value,NumberStyles.Number|NumberStyles.AllowExponent,CultureInfo.InvariantCulture,out var value)||value!=0)
                throw new InvalidOperationException("A captured PT or mixed PT deduction is positive, missing or invalid. It cannot be replaced with a source-month zero.");
        }
    }
    internal static void RequireClosedZeroReview(ReviewedProfessionalTaxRequest request, DateTime today)
    {
        ReviewedPeriodProfessionalTax.Validate(request);
        if (request.SourcePeriodReviewVersion != 2 ||
            DateTime.ParseExact(request.PayPeriod + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture) >= new DateTime(today.Year, today.Month, 1))
            throw new InvalidOperationException("Review zero statutory amounts for one completed source payroll month. Future-month exemptions are not inferred.");
    }
    internal static void RequireCapturedZeros(ExcelPayslipCalculationSource source, ExcelPayslipRow row,
        IReadOnlyDictionary<string, int>? columns, IReadOnlyList<SourcePeriodStatutoryComponent> components)
    {
        ReviewedPeriodProfessionalTax.ValidateSourceZeros(columns);
        if (columns is null) return;
        var captured = source.Rows.Where(value => value.SourceRow == row.SourceRow).ToArray();
        if (captured.Length != 1) throw new InvalidOperationException("This statutory source row is missing or ambiguous.");
        foreach (var pair in columns)
        {
            var matches = components.Where(component => component.Code == pair.Key).ToArray();
            var component = matches.Length == 1 ? matches[0] : null;
            var metadata = source.Columns.Where(column => column.ColumnIndex == pair.Value).ToArray();
            var cell = captured[0].Cells.ElementAtOrDefault(pair.Value);
            if (component is null || !ReviewedPeriodProfessionalTax.CanReviewSourceZero(component.Category, component.StatutoryType)
                || metadata.Length != 1 || metadata[0].Kind != (component.Category == "Deduction" ? "deduction" : "employer")
                || cell is null || cell.Error is not null || cell.CalculationError is not null || cell.MissingCachedValue
                || cell.DataType is not ("" or "Number") || string.IsNullOrWhiteSpace(cell.Value)
                || !decimal.TryParse(cell.Value, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var value) || value != 0)
                throw new InvalidOperationException("Each PF/ESIC zero must bind its own captured, numeric, error-free zero output and an unchanged unique statutory component. Blank, positive or unrelated cells cannot authorize a zero.");
        }
    }
}
