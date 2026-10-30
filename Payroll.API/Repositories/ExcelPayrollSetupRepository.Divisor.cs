using System.Globalization;
using System.Text.RegularExpressions;
using Payroll.API.Models;
namespace Payroll.API.Repositories;
public sealed partial class ExcelPayrollSetupRepository
{
    internal static string SharedPayrollDaysCell(ExcelPayrollSetupRequest request) => (request.PayrollDaysCell ?? "").Replace("$", "").Trim().ToUpperInvariant();
    internal static void ValidateSharedPayrollDaysBinding(ExcelPayrollSetupRequest request,ExcelPayslipCalculationSource source,IReadOnlyList<ExcelPayslipRow> selected,IReadOnlyList<ExcelPayslipRow>? allSavedRows=null)
    {
        var address=SharedPayrollDaysCell(request);if(address.Length==0)return;
        var match=Regex.Match(address,@"^([A-Z]{1,3})([1-9]\d{0,6})$");
        if(!match.Success||request.FixedPayrollDays is null||request.PayrollDaysColumnIndex!=-1)throw new InvalidOperationException("A shared header divisor needs an explicit fixed-day value and one captured cell reference.");
        var column=TransferColumn(match.Groups[1].Value);var line=int.Parse(match.Groups[2].Value,CultureInfo.InvariantCulture);
        var cell=source.Rows.SingleOrDefault(r=>r.SourceRow==line)?.Cells.ElementAtOrDefault(column);
        var originalRole=source.Columns.SingleOrDefault(c=>c.ColumnIndex==column)?.Kind??"";
        var reviewedRole=request.SourceColumnRoles.GetValueOrDefault(column,originalRole);
        static bool ProtectedRole(string role)=>role is "employeeName" or "employeeCode" or "email" or "earning" or "deduction" or "employer" or "netPay" or "grossTotal" or "deductionTotal";
        var outsideNormalizedHeader=line>source.HeaderRow;
        // Combined captures can normalize their displayed header to row 1 while
        // retaining a literal source-block divisor at (for example) K6. Prove it
        // against the ENTIRE saved batch, never just the selected employee subset.
        if(ProtectedRole(originalRole)||ProtectedRole(reviewedRole)||(allSavedRows??selected).Any(r=>r.SourceRow==line)
            ||allSavedRows is not null&&selected.Any(row=>!allSavedRows.Any(saved=>saved.Id==row.Id&&saved.SourceRow==row.SourceRow))
            ||outsideNormalizedHeader&&allSavedRows is null
            ||cell is null||!string.IsNullOrWhiteSpace(cell.FormulaText)||cell.Error is not null||cell.CalculationError is not null||cell.MissingCachedValue
            ||!decimal.TryParse(cell.Value,NumberStyles.Number,CultureInfo.InvariantCulture,out var days)||days!=request.FixedPayrollDays||days is <1 or >31||decimal.Truncate(days)!=days)
            throw new InvalidOperationException("The captured shared divisor is missing, calculated, erroneous, protected employee/output data or different from the reviewed fixed days. Re-read the saved source; no cache is promoted to a recurring input.");
        foreach(var row in selected) {
            var captured=source.Rows.SingleOrDefault(r=>r.SourceRow==row.SourceRow);
            var earning=request.Components.Where(c=>c.Category=="Earning"&&c.ProRata&&!c.IsPeriodAdjustment&&c.ExpectedColumnIndex.HasValue).ToList();
            if(!earning.Any(c=>{var raw=captured?.Cells.ElementAtOrDefault(c.ExpectedColumnIndex!.Value)?.FormulaText??"";var formula=Regex.Replace(raw.ToUpperInvariant(),@"[\s$]","");var absolute=Regex.Replace(raw.ToUpperInvariant(),@"\s","");return (!outsideNormalizedHeader||Regex.IsMatch(absolute,@"/\(?\$"+match.Groups[1].Value+@"\$"+line+@"\)?(?![A-Z0-9_])"))&&Regex.IsMatch(formula,@"/\(?"+address+@"\)?(?![A-Z0-9_])")&&Regex.IsMatch(formula,@"\b"+TransferLetter(request.PayableDaysColumnIndex)+row.SourceRow+@"\b");}))
                throw new InvalidOperationException("A selected earning row does not use the reviewed header divisor and payable input. Split differing source rules or resolve the mapping before transfer.");
        }
    }
}
