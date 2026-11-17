using System.Globalization;
using System.Text.RegularExpressions;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayrollSetupRepository
{
    // Some workbooks put a single absolute salary divisor beside an employee,
    // in a separate, otherwise empty column. That is not the employee's own
    // day/value cell. The complete capture and complete saved-row witness must
    // establish this narrow distinction before the existing AST proof runs.
    private static bool IsReviewedIsolatedPayrollDivisor(ExcelPayrollSetupRequest request,
        ExcelPayslipCalculationSource source,IReadOnlyList<ExcelPayslipRow> selected,
        IReadOnlyList<ExcelPayslipRow>? allSavedRows,int column,int line)
    {
        if(allSavedRows is null||allSavedRows.Count==0||!request.ReviewedFixedInputs
            ||source.CaptureVersion!=1||!source.CompleteSheetCapture||source.DependencyReviewStatus!="Reviewed"
            ||source.Rows.Count==0||source.Rows.Select(r=>r.SourceRow).Distinct().Count()!=source.Rows.Count
            ||!source.Rows.Select(r=>r.SourceRow).Order().SequenceEqual(Enumerable.Range(1,source.Rows.Count))
            ||allSavedRows.Select(r=>r.SourceRow).Distinct().Count()!=allSavedRows.Count
            ||allSavedRows.Select(r=>r.Id).Distinct(StringComparer.Ordinal).Count()!=allSavedRows.Count
            ||selected.Any(row=>!allSavedRows.Any(saved=>saved.Id==row.Id&&saved.SourceRow==row.SourceRow))
            ||allSavedRows.Any(row=>source.Rows.Count(captured=>captured.SourceRow==row.SourceRow)!=1))return false;
        var metadata=source.Columns.Where(c=>c.ColumnIndex==column).ToArray();
        if(metadata.Length!=1||metadata[0].Kind!="ignore"||!string.IsNullOrWhiteSpace(metadata[0].SourceHeader)
            ||!string.IsNullOrEmpty(metadata[0].Label)&&metadata[0].Label!=$"Column ({TransferLetter(column)})"
            ||request.SourceColumnRoles.GetValueOrDefault(column,metadata[0].Kind) is not("ignore" or "info"))return false;
        if(source.WorkbookFiles.Count!=1||source.WorkbookFiles[0].WorkbookKey!=source.WorkbookKey
            ||!Guid.TryParseExact(source.WorkbookFiles[0].OriginalId,"N",out _)
            ||!Regex.IsMatch(source.WorkbookFiles[0].Sha256??"",@"\A[a-fA-F0-9]{64}\z"))return false;
        if(column==request.PayrollDaysColumnIndex||column==request.PayableDaysColumnIndex||column==request.PresentDaysColumnIndex
            ||column==request.MonthlyCtcColumnIndex||column==request.SourceWorkLocationColumnIndex
            ||column==request.IdentityColumnReview?.ColumnIndex||column==request.AttendanceReview?.LwpColumnIndex
            ||request.Components.Any(c=>c.SourceColumnIndex==column||c.ExpectedColumnIndex==column)
            ||request.NamedInputReview?.Bindings.Any(binding=>binding.ColumnIndex==column)==true)return false;
        foreach(var row in source.Rows)
        {
            var cell=row.Cells.ElementAtOrDefault(column);
            if(row.SourceRow==line)
            {
                if(cell is null||cell.DataType!="Number"||!string.IsNullOrWhiteSpace(cell.FormulaText)
                    ||cell.Error is not null||cell.CalculationError is not null||cell.MissingCachedValue
                    ||!decimal.TryParse(cell.Value,NumberStyles.Number,CultureInfo.InvariantCulture,out var value)
                    ||value!=request.FixedPayrollDays||value is <1 or >31||decimal.Truncate(value)!=value)return false;
            }
            else if(cell is not null&&(!string.IsNullOrWhiteSpace(cell.Value)||!string.IsNullOrWhiteSpace(cell.FormulaText)
                ||cell.Error is not null||cell.CalculationError is not null||cell.MissingCachedValue
                ||cell.DataType is not("" or "Blank")))return false;
        }
        var letter=TransferLetter(column);var address=letter+line.ToString(CultureInfo.InvariantCulture);
        var reference=@"(?<![A-Z0-9_!])\$?"+letter+@"\$?"+line+@"(?![A-Z0-9_])";
        var division=@"/\(?\$"+letter+@"\$"+line+@"\)?(?![A-Z0-9_])";
        var affected=0;
        // Inspect the whole saved batch, not only the operator's subset. This
        // never repairs a relative/cross-row reference or certifies cached pay.
        foreach(var saved in allSavedRows)
        {
            var captured=source.Rows.Single(row=>row.SourceRow==saved.SourceRow);
            foreach(var output in source.Columns.Where(c=>c.Kind=="earning"))
            {
                var cell=captured.Cells.ElementAtOrDefault(output.ColumnIndex);
                var raw=Regex.Replace(cell?.FormulaText?.ToUpperInvariant()??"",@"\s","");
                var references=Regex.Matches(raw,reference);
                if(references.Count==0)continue;
                if(cell is null||cell.Error is not null||cell.CalculationError is not null||cell.MissingCachedValue
                    ||raw.Contains('!')||raw.Contains('[')||Regex.Matches(raw,division).Count!=references.Count
                    ||!Regex.IsMatch(raw.Replace("$",""),@"\b"+TransferLetter(request.PayableDaysColumnIndex)+saved.SourceRow+@"\b"))return false;
                affected++;
            }
        }
        return affected>0;
    }
}
