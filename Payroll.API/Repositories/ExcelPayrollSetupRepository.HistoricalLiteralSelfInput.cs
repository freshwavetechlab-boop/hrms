using System.Globalization;
using System.Text.RegularExpressions;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayrollSetupRepository
{
    internal static void ValidateHistoricalLiteralSelfInputReview(HistoricalPeriodSalaryRequest request)
    {
        if(request.LiteralSelfInputReview is not { } review)return;
        if(request.SalaryTargetMode!=InitialSourceTargetMode || review.Version!=1 || !review.Reviewed
            || !Regex.IsMatch(review.SourceHash??"",@"\A[a-f0-9]{64}\z") || review.Bindings is null || review.Bindings.Count is <1 or >100
            || review.Bindings.Any(binding=>binding is null || string.IsNullOrWhiteSpace(binding.ComponentKey) || binding.ComponentKey.Length>100
                || !Regex.IsMatch(binding.InputKey??"",@"\A[1-9][0-9]{0,19}\z") || binding.ColumnIndex is <0 or >1499
                || binding.RowIds is null || binding.RowIds.Count is <1 or >100 || binding.RowIds.Any(string.IsNullOrWhiteSpace)
                || binding.RowIds.Distinct(StringComparer.Ordinal).Count()!=binding.RowIds.Count
                || !binding.RowIds.Order(StringComparer.Ordinal).SequenceEqual(request.Setup.RowIds.Order(StringComparer.Ordinal)))
            || review.Bindings.Select(binding=>binding.ComponentKey).Distinct(StringComparer.Ordinal).Count()!=review.Bindings.Count
            || review.Bindings.Select(binding=>binding.InputKey).Distinct(StringComparer.Ordinal).Count()!=review.Bindings.Count
            || review.Bindings.Select(binding=>binding.ColumnIndex).Distinct().Count()!=review.Bindings.Count)
            throw new InvalidOperationException("Literal self-input needs explicit initial closed-source mode, reviewed version one, an exact source hash and unique component/input/column bindings covering every selected row exactly once.");
    }

    internal static IReadOnlyList<HistoricalLiteralSelfInputEvidence> ReviewHistoricalLiteralSelfInputs(HistoricalPeriodSalaryRequest request,
        ExcelPayslipCalculationSource source,IReadOnlyList<ExcelPayslipRow> selected,string sourceHash)
    {
        ValidateHistoricalLiteralSelfInputReview(request);
        if(request.LiteralSelfInputReview is not { } review)return [];
        if(review.SourceHash!=sourceHash || request.InitialSourceTargetReview?.SourceHash!=sourceHash
            || selected.Count!=request.Setup.RowIds.Count || !selected.Select(row=>row.Id).ToHashSet(StringComparer.Ordinal).SetEquals(request.Setup.RowIds))
            throw new InvalidOperationException("The literal self-input source or selected rows changed. Refresh this exact source and both initial-target/literal reviews.");
        var output=new List<HistoricalLiteralSelfInputEvidence>();
        foreach(var binding in review.Bindings)
        {
            var matches=request.Setup.Components.Where(component=>component.Key==binding.ComponentKey).ToArray();
            var component=matches.Length==1?matches[0]:null;
            var column=source.Columns.Where(column=>column.ColumnIndex==binding.ColumnIndex).ToArray();
            var kind=component?.Category switch{"Earning" or "Reimbursement"=>"earning","Deduction"=>"deduction","Benefit"=>"employer",_=>""};
            if(component is null || component.ExistingComponentId!=binding.InputKey || component.SourceColumnIndex!=binding.ColumnIndex || component.ExpectedColumnIndex!=binding.ColumnIndex
                || component.CalculationType!="Fixed Amount" || component.ProRata || component.IsPeriodAdjustment || component.ExemptWhenSourceZero || component.BlankInputAsZero
                || !string.IsNullOrWhiteSpace(component.Formula) || !string.IsNullOrWhiteSpace(component.BaseComponent) || component.RowFormulas.Count!=0
                || !decimal.TryParse(component.Value,NumberStyles.Number|NumberStyles.AllowExponent,CultureInfo.InvariantCulture,out var fixedDefault)
                || fixedDefault<0 || fixedDefault>1_000_000_000m || decimal.Round(fixedDefault,4)!=fixedDefault
                || column.Length!=1 || kind=="" || column[0].Kind!=kind || request.Setup.SourceColumnRoles.GetValueOrDefault(binding.ColumnIndex,column[0].Kind)!=kind
                || ProtectedRecurringInputHeader(column[0].SourceHeader+" "+column[0].Label)
                || Regex.IsMatch(column[0].SourceHeader+" "+column[0].Label,@"\bCTC\b",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant)
                || binding.ColumnIndex==request.Setup.MonthlyCtcColumnIndex || binding.ColumnIndex==request.Setup.PayrollDaysColumnIndex
                || binding.ColumnIndex==request.Setup.PayableDaysColumnIndex || binding.ColumnIndex==request.Setup.PresentDaysColumnIndex
                || request.Setup.Components.Count(candidate=>candidate.ExistingComponentId==binding.InputKey)!=1
                || request.Setup.Components.Count(candidate=>candidate.SourceColumnIndex==binding.ColumnIndex)!=1
                || request.Setup.Components.Count(candidate=>candidate.ExpectedColumnIndex==binding.ColumnIndex)!=1)
                throw new InvalidOperationException("Literal self-input must bind one exact existing non-prorated fixed component to its own financial input/output column, with unchanged category and no formula, alias, blank-zero, CTC, identity, attendance or adjustment semantics.");
            foreach(var row in selected)
            {
                var rows=source.Rows.Where(captured=>captured.SourceRow==row.SourceRow).ToArray();
                var cell=rows.Length==1?rows[0].Cells.ElementAtOrDefault(binding.ColumnIndex):null;
                if(cell is null || cell.DataType is not("" or "Number") || cell.MissingCachedValue || cell.Error is not null || cell.CalculationError is not null
                    || !string.IsNullOrWhiteSpace(cell.FormulaText) || string.IsNullOrWhiteSpace(cell.Value)
                    || !decimal.TryParse(cell.Value,NumberStyles.Number|NumberStyles.AllowExponent,CultureInfo.InvariantCulture,out var literal)
                    || literal<0 || literal>1_000_000_000m || decimal.Round(literal,4)!=literal)
                    throw new InvalidOperationException("Every reviewed self-input row needs its own explicit representable numeric literal. Formula caches, blanks, errors, text, missing values and copied amounts are not accepted.");
                var assignments=request.Assignments.Where(assignment=>assignment.RowId==row.Id).ToArray();
                if(assignments.Length!=1)throw new InvalidOperationException("A literal self-input requires one exact existing employee assignment per reviewed source row.");
                output.Add(new(assignments[0].EmployeeId,row.Id,row.SourceRow,binding.ComponentKey,binding.InputKey,binding.ColumnIndex,literal,sourceHash));
            }
        }
        return output;
    }
}
