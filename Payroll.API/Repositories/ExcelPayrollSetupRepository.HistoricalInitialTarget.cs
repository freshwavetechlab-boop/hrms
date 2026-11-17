using System.Globalization;
using System.Text.RegularExpressions;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayrollSetupRepository
{
    internal const string InitialSourceTargetMode="ReviewedInitialSourceTarget";
    internal const string InitialSourceTargetMeaning="MonthlyPayrollEarningsBudget";
    internal const string InitialSourceTargetExplanation="Source-corroborated initial closed-period payroll earnings budget, annualized from its reviewed literal monthly CTC column and equal to current Master annual target. No retained prior-period salary basis or actual joining/hire date is asserted; current and future Master/history remain unchanged.";

    internal static IReadOnlyDictionary<int,InspectedRecurringSourceBoundary>? HistoricalInitialSourceBoundary(
        HistoricalPeriodSalaryRequest request,HistoricalInitialSourceTargetEvidence? target)
    {
        if(request.SalaryTargetMode!=InitialSourceTargetMode)return null;
        ValidateInitialSourceTargetReview(request);
        if(target is null || target.MonthlyCtcColumnIndex!=request.InitialSourceTargetReview!.MonthlyCtcColumnIndex
            || target.SourceHash!=request.InitialSourceTargetReview.SourceHash || target.SourceMonthlyBudget<=0
            || target.SourceMonthlyBudget*12m!=target.AnnualSalaryTarget || target.Meaning!=InitialSourceTargetMeaning || !target.ActualJoiningDateNotEstablished)
            throw new InvalidOperationException("An exact validated initial-source target is required before binding its monthly CTC formula reference.");
        return new Dictionary<int,InspectedRecurringSourceBoundary>{{target.MonthlyCtcColumnIndex,new("MONTHLY_CTC",target.SourceMonthlyBudget)}};
    }

    internal static void ValidateInitialSourceTargetReview(HistoricalPeriodSalaryRequest request)
    {
        if(request.SalaryTargetMode!=InitialSourceTargetMode)
        {
            if(request.InitialSourceTargetReview is not null)throw new InvalidOperationException("Initial source target review requires its explicit opt-in salary target mode.");
            return;
        }
        var review=request.InitialSourceTargetReview;
        if(review is not{Version:1,Reviewed:true,ActualJoiningDateNotEstablished:true,MonthlyCtcColumnIndex:>=0 and <=1499}
            || review.Meaning!=InitialSourceTargetMeaning || !Regex.IsMatch(review.SourceHash??"","\\A[a-f0-9]{64}\\z")
            || request.Setup.MonthlyCtcColumnIndex!=review.MonthlyCtcColumnIndex)
            throw new InvalidOperationException("Review the exact saved source hash, literal monthly CTC information column, monthly payroll earnings-budget meaning and unchanged/unknown joining-date boundary for this initial closed-period target.");
    }

    internal static HistoricalInitialSourceTargetEvidence ReadInitialSourceTarget(HistoricalPeriodSalaryRequest request,
        ExcelPayslipCalculationSource source,ExcelPayslipRow row,int employeeId,decimal currentAnnual,string sourceHash)
    {
        ValidateInitialSourceTargetReview(request);
        var review=request.InitialSourceTargetReview??throw new InvalidOperationException("An explicit initial source target review is required.");
        if(request.SalaryTargetMode!=InitialSourceTargetMode || review.SourceHash!=sourceHash)
            throw new InvalidOperationException("Initial target review source hash differs. Refresh and review this exact saved source; no current salary is copied into the past.");
        var index=review.MonthlyCtcColumnIndex!.Value;
        var column=source.Columns.SingleOrDefault(c=>c.ColumnIndex==index);
        static string Key(string text)=>Regex.Replace(text.ToLowerInvariant(),"[^a-z0-9]","");
        var permittedHeaders=new[]{"ctc","currentctc","monthlyctc","currentmonthlyctc","currentctcmonthly","ctcmonthly"};
        if(column is null || column.Kind!="info" || request.Setup.SourceColumnRoles.GetValueOrDefault(index,column.Kind)!="info"
            || !permittedHeaders.Contains(Key(column.SourceHeader))
            || Regex.IsMatch(column.Label,@"\b(?:annual|yearly|daily|hourly|rate|earned|payable)\b",RegexOptions.IgnoreCase)
            || index==request.Setup.PayrollDaysColumnIndex || index==request.Setup.PayableDaysColumnIndex || index==request.Setup.PresentDaysColumnIndex
            || request.Setup.Components.Any(c=>c.ExpectedColumnIndex==index))
            throw new InvalidOperationException("Initial salary target needs an unambiguous CTC Information header, separate from earned outputs, generic rates, identities and attendance.");
        var cells=source.Rows.SingleOrDefault(c=>c.SourceRow==row.SourceRow);
        var cell=cells?.Cells.ElementAtOrDefault(index);
        if(cell is null || cell.MissingCachedValue || cell.Error is not null || cell.CalculationError is not null
            || !string.IsNullOrWhiteSpace(cell.FormulaText) || string.IsNullOrWhiteSpace(cell.Value)
            || !decimal.TryParse(cell.Value,NumberStyles.Number|NumberStyles.AllowExponent,CultureInfo.InvariantCulture,out var monthly)
            || monthly<0 || monthly>1_000_000_000m/12m || decimal.Round(monthly,4)!=monthly)
            throw new InvalidOperationException("Initial monthly CTC must be an explicit independent numeric literal with representable precision. Formula caches, blanks, day-dependent earnings and inferred amounts are not accepted.");
        var annual=monthly*12m;
        if(decimal.Round(annual,2)!=annual || annual!=currentAnnual)
            throw new InvalidOperationException("Literal source monthly CTC ×12 must exactly equal this employee's current Master annual target, with no rounding or fallback. Review a different salary interpretation separately.");
        return new(employeeId,index,monthly,annual,sourceHash,InitialSourceTargetMeaning,true);
    }
}
