using System.Globalization;
using Payroll.API.Models;

namespace Payroll.API.Services;

internal static class SourceMonthAttendanceReview
{
    internal static void Validate(SourceMonthAttendanceRequest request, DateTime today, bool saving=false)
    {
        if(request.Version!=1 || request.ClientId<=0 || !ReviewedPeriodProfessionalTax.ValidMonth(request.PayPeriod)
            || DateTime.ParseExact(request.PayPeriod+"-01","yyyy-MM-dd",CultureInfo.InvariantCulture)>=new DateTime(today.Year,today.Month,1)
            || !Guid.TryParseExact(request.SourceBatchId,"N",out _) || request.SourceHash is null
            || request.SourceHash.Length>0&&!ReviewedPeriodProfessionalTax.ValidHash(request.SourceHash)
            || saving&&!ReviewedPeriodProfessionalTax.ValidHash(request.SourceHash)
            || request.DivisorColumn is <0 or >1499 || request.PayableColumn is <0 or >1499 || request.DivisorColumn==request.PayableColumn
            || request.CycleStartDay is <1 or >31 || request.CycleEndDay is <1 or >31
            || request.Reason?.Trim().Length is not (>=12 and <=500) || request.Rows is null || request.Rows.Count is <1 or >100
            || request.Rows.Any(row=>row is null||row.EmployeeId<=0||string.IsNullOrWhiteSpace(row.SourceRowId)||row.SourceRowId.Length>100)
            || request.Rows.Select(row=>row.EmployeeId).Distinct().Count()!=request.Rows.Count
            || request.Rows.Select(row=>row.SourceRowId).Distinct(StringComparer.Ordinal).Count()!=request.Rows.Count)
            throw new InvalidOperationException("Review one completed source month, distinct divisor/payable columns, cycle and up to 100 uniquely matched employees. Actual presence remains unknown.");
    }
    internal static (int Divisor,decimal Payable) ReadUnits(ExcelPayslipCalculationSource source,int sourceRow,
        int divisorColumn,int payableColumn,bool extra)
    {
        decimal Number(int column)
        {
            var metadata=source.Columns.Where(value=>value.ColumnIndex==column).ToArray();
            var rows=source.Rows.Where(row=>row.SourceRow==sourceRow).ToArray();
            var cell=rows.Length==1?rows[0].Cells.ElementAtOrDefault(column):null;
            if(metadata.Length!=1||metadata[0].Kind is not("info" or "ignore")||cell is null||cell.Error is not null
                ||cell.CalculationError is not null||cell.MissingCachedValue||cell.DataType is not("" or "Number")
                ||string.IsNullOrWhiteSpace(cell.Value)||!decimal.TryParse(cell.Value,NumberStyles.Number|NumberStyles.AllowExponent,CultureInfo.InvariantCulture,out var value))
                throw new InvalidOperationException("Divisor and payable units need distinct, numeric, error-free captured source cells. Financial outputs or missing values cannot become attendance.");
            return value;
        }
        var divisor=Number(divisorColumn);var payable=Number(payableColumn);
        if(divisorColumn==payableColumn||divisor is <1 or >31||decimal.Truncate(divisor)!=divisor
            ||payable<0||payable>(extra?999.99m:divisor)||decimal.Round(payable,2)!=payable)
            throw new InvalidOperationException("Review a whole-day salary divisor from 1 to 31 and nonnegative payable units to two decimals. Units above the divisor require explicit extra-unit review.");
        return ((int)divisor,payable);
    }
}
