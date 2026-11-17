using System.Globalization;
using System.Text.RegularExpressions;
using Payroll.API.Models;

namespace Payroll.API.Services;

// Only a literal recurring salary storage boundary. This never changes a source
// cell, a formula, cash output, attendance quantity or independent CTC binding.
internal static class RecurringSalaryInputPrecision
{
    internal static bool TryNormalize(ExcelPayrollSetupRequest request,ExcelPayrollSetupComponent component,
        ExcelPayslipCalculationSource? capture,ExcelPayslipCalculationSourceRow source,decimal original,
        out decimal stored,out string? evidence)
    {
        stored=original;evidence=null;
        if(decimal.Round(original,4)==original||original is <0 or >1_000_000_000m
            ||!request.ReviewedFixedInputs||component.IsPeriodAdjustment
            ||component.CalculationType is not("Fixed Amount" or "Daily Rate")
            ||!string.IsNullOrWhiteSpace(component.Formula)||component.RowFormulas.Values.Any(f=>!string.IsNullOrWhiteSpace(f))
            ||component.SourceColumnIndex is not int index||capture is null
            ||index==request.MonthlyCtcColumnIndex||index==request.PayrollDaysColumnIndex
            ||index==request.PayableDaysColumnIndex||index==request.PresentDaysColumnIndex
            ||index==request.SourceWorkLocationColumnIndex||index==request.IdentityColumnReview?.ColumnIndex
            ||index==request.AttendanceReview?.LwpColumnIndex
            ||request.Components.Any(c=>c.ExpectedColumnIndex==index)
            ||AddressUsesColumn(request.PayrollDaysCell,index)||AddressUsesColumn(capture.MonthDaysCell,index))return false;
        var columns=capture.Columns.Where(c=>c.ColumnIndex==index).ToArray();
        if(columns.Length!=1||columns[0].Kind is not("info" or "ignore")
            ||request.SourceColumnRoles.GetValueOrDefault(index,columns[0].Kind)!="info")return false;
        var header=columns[0].SourceHeader+" "+columns[0].Label;
        if(ProtectedHeader(header)||RecurringInputHeaderGuards.HasContactIdentifier(header)
            ||!Regex.IsMatch(columns[0].SourceHeader,@"basic|hra|allowance|salary|rate|wage|bonus|medical|reimburse|conveyance|overtime|increment|arrear",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant))return false;
        var cell=source.Cells.ElementAtOrDefault(index);
        if(cell is not{DataType:"Number"}||!string.IsNullOrWhiteSpace(cell.FormulaText)
            ||cell.Error is not null||cell.CalculationError is not null||cell.MissingCachedValue
            ||cell.Value.Length is <1 or >128||!Regex.IsMatch(cell.Value,@"\A[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?\z")
            ||!decimal.TryParse(cell.Value,NumberStyles.Float,CultureInfo.InvariantCulture,out var parsed)||parsed!=original)return false;
        var rounded=decimal.Round(original,4,MidpointRounding.ToEven);var delta=Math.Abs(original-rounded);
        if(delta==0||delta>0.00000001m)return false;
        // Parse decimal lexemes directly. A decimal-to-double cast can double
        // round at a midpoint and is not proof of identical source numbers.
        if(!double.TryParse(cell.Value,NumberStyles.Float,CultureInfo.InvariantCulture,out var sourceNumber)
            ||!double.TryParse(rounded.ToString("G29",CultureInfo.InvariantCulture),NumberStyles.Float,CultureInfo.InvariantCulture,out var storageNumber)
            ||!double.IsFinite(sourceNumber)||!double.IsFinite(storageNumber)
            ||BitConverter.DoubleToInt64Bits(sourceNumber)!=BitConverter.DoubleToInt64Bits(storageNumber))return false;
        stored=rounded;
        evidence=$"Recurring source input retained as {cell.Value}; salary storage uses {rounded.ToString("G29",CultureInfo.InvariantCulture)} (four decimal places). Both parse to identical finite IEEE-754 bits {unchecked((ulong)BitConverter.DoubleToInt64Bits(sourceNumber)).ToString("x16",CultureInfo.InvariantCulture)}; decimal representation difference {delta.ToString("G29",CultureInfo.InvariantCulture)} is at most 0.00000001. Original source cells, formulas and comparison amounts remain unchanged.";
        return true;
    }
    private static bool AddressUsesColumn(string? address,int column)
    {
        var m=Regex.Match(address??"",@"\A\$?([A-Z]{1,3})\$?[1-9][0-9]*\z",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
        if(!m.Success)return false;var value=0;foreach(var c in m.Groups[1].Value.ToUpperInvariant())value=value*26+c-'A'+1;return value-1==column;
    }
    private static bool ProtectedHeader(string value)=>Regex.IsMatch(value,
        @"employee|\bemp\b|\buan\b|\bpan\b|aadh?aar|bank|account|ifsc|identifier|qualification|\bdate\b|\bdob\b|birth|joining|e-?mail|\besi(?:c)?[_\s-]*(?:no|number)|earned|net[_\s-]*(?:pay|salary)|salary[_\s-]*paid|attendance|working[_\s-]*days|payable|present|\blwp\b|\blop\b|payroll[_\s-]*days|divisor|\bctc\b|total[_\s-]*(?:earnings|deductions)|\b(?:gross|net)[_\s-]*(?:salary|pay|earnings|subtotal|total)|\bfinal[_\s-]*gross|net[_\s-]*take",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
}
