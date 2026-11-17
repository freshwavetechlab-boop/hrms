using System.Globalization;
using System.Text.Json;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayrollSetupRepository
{
    internal static void ValidateSourceMonthlyCtcOperandReview(ExcelPayrollSetupRequest request)
    {
        if(!request.ReviewedSourceMonthlyCtcOperand)return;
        if(request.MonthlyCtcColumnIndex is not(>=0 and <=1499)||!request.ReviewedFixedInputs||!request.ReviewedPolicyScope
            ||request.TransferReviewVersion!=1||request.NamedInputReview is not null||request.ReportedPeriodInputs is not null||request.LocationAllocations is not null)
            throw new InvalidOperationException("A source monthly CTC operand requires an explicitly selected independent monthly CTC column and complete normal configuration review.");
    }

    private static string CapturedCtcOperandHash(int row,int column,ExcelPayslipCalculationColumn metadata,ExcelPayslipCalculationCell cell)
        =>Hash(JsonSerializer.Serialize(new{row,column,metadata,cell},Json));

    internal static IReadOnlyDictionary<int,InspectedRecurringSourceBoundary> ReviewedNormalSourceCtcBoundary(
        ExcelPayrollSetupRequest request,ExcelPayslipCalculationSource source,ExcelPayslipRow row)
    {
        ValidateSourceMonthlyCtcOperandReview(request);
        if(!request.ReviewedSourceMonthlyCtcOperand||!request.RowIds.Contains(row.Id))
            throw new InvalidOperationException("An explicit normal source monthly CTC operand review is required.");
        var index=request.MonthlyCtcColumnIndex!.Value;
        var columns=source.Columns.Where(c=>c.ColumnIndex==index).ToArray();
        var captured=source.Rows.Where(r=>r.SourceRow==row.SourceRow).ToArray();
        var cell=captured.Length==1?captured[0].Cells.ElementAtOrDefault(index):null;
        if(index>=source.ColumnCount||columns.Length!=1||columns[0].Kind is not("info" or "ignore")
            ||cell is not{DataType:"Number"}||cell.Error is not null||cell.CalculationError is not null||cell.MissingCachedValue
            ||request.Components.Any(c=>c.ExpectedColumnIndex==index||c.SourceColumnIndex==index)
            ||!decimal.TryParse(cell.Value,NumberStyles.Number|NumberStyles.AllowExponent,CultureInfo.InvariantCulture,out var monthly)
            ||monthly<=0||monthly>1_000_000_000m/12m||decimal.Round(monthly,4)!=monthly||decimal.Round(monthly*12m,2)!=monthly*12m)
            throw new InvalidOperationException("The reviewed source CTC operand needs a unique captured numeric monthly budget, independent of component inputs/outputs and exactly representable in annual/monthly currency.");
        // This validates the captured expression with CTC aliases disabled, and
        // checks its cache against independent source evaluation. A self-reference,
        // lookup, attendance factor or earned dependency cannot prove its own target.
        var annual=ReviewedSourceAnnualCtc(request,source,captured[0],row);
        if(annual!=monthly*12m)
            throw new InvalidOperationException("The native annual target does not establish this exact source monthly CTC operand.");
        return new Dictionary<int,InspectedRecurringSourceBoundary>{{index,new("MONTHLY_CTC",monthly,true,CapturedCtcOperandHash(row.SourceRow,index,columns[0],cell))}};
    }
}
