using System.Text.Json;
using System.Text.RegularExpressions;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayrollSetupRepository
{
    internal static void ValidateRecurringBlankInputReview(ExcelPayrollSetupRequest request)
    {
        if(request.RecurringBlankInputReview is not{} review)return;
        if(review.Version!=1||!review.Reviewed||!Regex.IsMatch(review.SourceHash??"",@"\A[a-f0-9]{64}\z")
            ||review.ColumnIndices is null||review.ColumnIndices.Count is <1 or >10
            ||review.ColumnIndices.Any(i=>i is <0 or >1499)||review.ColumnIndices.Distinct().Count()!=review.ColumnIndices.Count
            ||string.IsNullOrWhiteSpace(review.Reason)||review.Reason.Trim().Length<10||review.Reason.Length>500
            ||!request.ReviewedFixedInputs||!request.ReviewedPolicyScope||request.TransferReviewVersion!=1
            ||request.NamedInputReview is not null||request.ReportedPeriodInputs is not null||request.LocationAllocations is not null)
            throw new InvalidOperationException("Blank recurring operands need an explicit source-bound version-one review, distinct columns and a reason in the normal reviewed configuration workflow.");
    }

    internal static void BindRecurringBlankInputReview(ExcelPayrollSetupRequest request,ExcelPayslipBatch batch,
        ExcelPayslipCalculationSource source)
    {
        if(request.RecurringBlankInputReview is not{} review)return;
        ValidateRecurringBlankInputReview(request);
        if(request.BatchId!=batch.Id||review.SourceHash!=Hash(JsonSerializer.Serialize(new{Batch=batch,Source=source},Json))
            ||request.RowIds.Count!=request.RowIds.Distinct().Count()||request.RowIds.Any(id=>batch.Rows.Count(r=>r.Id==id)!=1))
            throw new InvalidOperationException("The reviewed blank operands belong to a different saved source or row selection. Refresh their review.");
        foreach(var row in batch.Rows.Where(r=>request.RowIds.Contains(r.Id)))
            foreach(var column in review.ColumnIndices)
                RequireReviewedBlankOperand(request,source,row,column);
    }

    private static void RequireReviewedBlankOperand(ExcelPayrollSetupRequest request,ExcelPayslipCalculationSource source,
        ExcelPayslipRow row,int column)
    {
        ValidateRecurringBlankInputReview(request);
        var metadata=source.Columns.Where(c=>c.ColumnIndex==column).ToArray();
        var rows=source.Rows.Where(r=>r.SourceRow==row.SourceRow).ToArray();
        var cell=rows.Length==1?rows[0].Cells.ElementAtOrDefault(column):null;
        if(request.RecurringBlankInputReview?.ColumnIndices.Contains(column)!=true||!request.RowIds.Contains(row.Id)
            ||column>=source.ColumnCount||metadata.Length!=1||metadata[0].Kind is not("info" or "ignore")
            ||request.SourceColumnRoles.GetValueOrDefault(column,metadata[0].Kind)!="info"
            ||ProtectedRecurringInputHeader(metadata[0].SourceHeader+" "+metadata[0].Label)
            ||Regex.IsMatch(metadata[0].SourceHeader+" "+metadata[0].Label,@"\b(?:days?|hours?|attendance|ctc|salary|total|sub\s*total|gross|lwp|lop|id|code|serial|sr\s*no|name|location|city|state)\b",RegexOptions.IgnoreCase)
            ||column==request.PayableDaysColumnIndex||column==request.PresentDaysColumnIndex||column==request.PayrollDaysColumnIndex
            ||column==request.MonthlyCtcColumnIndex||column==request.SourceWorkLocationColumnIndex
            ||column==request.AttendanceReview?.LwpColumnIndex||column==request.IdentityColumnReview?.ColumnIndex
            ||request.Components.Any(c=>c.SourceColumnIndex==column||c.ExpectedColumnIndex==column)
            ||cell is not{DataType:"Blank"}||cell.Value!=""||!string.IsNullOrWhiteSpace(cell.FormulaText)
            ||cell.Error is not null||cell.CalculationError is not null||cell.MissingCachedValue)
            throw new InvalidOperationException("Only explicitly reviewed, captured empty numeric INFO operands may become zero. Missing cells, formula caches, outputs, identities and attendance are never inferred.");
    }
}
