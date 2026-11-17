using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayrollSetupRepository
{
    internal const string HistoricalNamedInputsFlag="Payroll:HistoricalNamedInputsEnabled";
    private sealed record HistoricalNamedInputPlan(NamedFormulaInputProfile Profile,NamedFormulaInputContext Context,
        List<NamedFormulaInputCell> Cells);

    internal static void ValidateHistoricalNamedInputReview(HistoricalPeriodSalaryRequest request)
    {
        if(request.NamedInputReview is not{} review)return;
        if(!Regex.IsMatch(review.TemplateId??"",@"\A[0-9]{1,20}\z")||!Regex.IsMatch(review.DeclarationHash??"",@"\A[a-f0-9]{64}\z")
            ||request.Assignments.Any(a=>a.TemplateId!=review.TemplateId)||review.Bindings is null||review.Bindings.Count is <1 or >16
            ||review.Bindings.Any(b=>b is null||string.IsNullOrWhiteSpace(b.Key)||b.ColumnIndex is <0 or >1499||b.HeaderRow<=0||string.IsNullOrWhiteSpace(b.SourceHeader))
            ||review.Bindings.Select(b=>b.Key).Distinct(StringComparer.Ordinal).Count()!=review.Bindings.Count
            ||review.Bindings.Select(b=>b.ColumnIndex).Distinct().Count()!=review.Bindings.Count)
            throw new InvalidOperationException("Review one exact managed named-operand template, declaration and independent literal/header binding for this closed salary selection.");
        if(review.Bindings.Any(b=>request.Setup.SourceColumnRoles.GetValueOrDefault(b.ColumnIndex)!="info"
            ||request.Setup.Components.Any(c=>c.SourceColumnIndex==b.ColumnIndex||c.ExpectedColumnIndex==b.ColumnIndex)))
            throw new InvalidOperationException("Historical operands need explicitly reviewed independent Information bindings, separate from financial component inputs and outputs.");
    }

    private void RequireHistoricalNamedInputsEnabled(HistoricalPeriodSalaryRequest request)
    {
        if(request.NamedInputReview is not null&&!configuration.GetValue<bool>(HistoricalNamedInputsFlag))
            throw new InvalidOperationException("Closed historical named-operand writes are disabled. Deploy compatible API and payroll-worker readers before enabling Payroll:HistoricalNamedInputsEnabled; an old reader cannot calculate the independent operand.");
    }

    private async Task<NamedFormulaInputProfile?> ReadHistoricalNamedProfileAsync(MySqlConnection db,MySqlTransaction tx,int clientId,
        HistoricalPeriodSalaryRequest request,ExcelPayslipCalculationSource source,bool locked)
    {
        ValidateHistoricalNamedInputReview(request);RequireHistoricalNamedInputsEnabled(request);
        var templates=request.Assignments.Select(a=>a.TemplateId).Distinct(StringComparer.Ordinal).ToArray();
        var profiles=await NamedFormulaInputStore.ReadProfilesAsync(db,tx,configuration,clientId,templates);
        if(profiles.Count==0){if(request.NamedInputReview is not null)throw new InvalidOperationException("The exact protected named-operand template declaration is missing.");return null;}
        // The writer gate is enforced from the actual managed template too; a
        // caller cannot bypass it by omitting its review or a template intent flag.
        if(!configuration.GetValue<bool>(HistoricalNamedInputsFlag))throw new InvalidOperationException("This historical template needs compatible named-operand API/worker readers before its writer capability can be enabled.");
        if(request.NamedInputReview is not{} review||profiles.Count!=1||!profiles.TryGetValue(review.TemplateId,out var profile)
            ||profile.DeclarationHash!=review.DeclarationHash)
            throw new InvalidOperationException("Every managed historical named-operand template needs its exact reviewed declaration and source bindings; no legacy context or zero fallback is accepted.");
        RequireReviewedNamedSourceRoles(request.Setup,source,review.Bindings);
        if(review.Bindings.Any(b=>b.ColumnIndex==request.Setup.MonthlyCtcColumnIndex||b.ColumnIndex==request.Setup.PayrollDaysColumnIndex
            ||b.ColumnIndex==request.Setup.PayableDaysColumnIndex||b.ColumnIndex==request.Setup.PresentDaysColumnIndex))
            throw new InvalidOperationException("An independent named operand cannot replace CTC, an earning/component input, identifiers or attendance.");
        var setup=JsonNode.Parse(await PayrollDataTableStore.GetSetupJsonAsync(db,tx))!;
        var template=setup["salaryStructures"]!.AsArray().OfType<JsonObject>().SingleOrDefault(t=>Text(t,"id")==profile.TemplateId&&Text(t,"active")=="true")
            ??throw new InvalidOperationException("The managed historical named template is unavailable.");
        var masters=setup["salaryComponents"]!.AsArray().OfType<JsonObject>().ToArray();
        ReviewedNamedFormulaInputs.ValidateDefinitions(profile.Definitions,masters.SelectMany(m=>new[]{Text(m,"id"),Text(m,"code")}));
        if(LocationSettingsRulesHash(template,masters)!=profile.RulesHash)throw new InvalidOperationException("The managed named-template monetary rules changed; review a new reusable rule version instead of substituting an operand.");
        return profile;
    }

    private static HistoricalNamedInputPlan? BindHistoricalNamedInputs(HistoricalPeriodSalaryRequest request,
        NamedFormulaInputProfile? profile,ExcelPayslipCalculationSource source,ExcelPayslipRow row)
    {
        if(profile is null)return null;
        var bound=ReviewedNamedFormulaInputs.Bind(profile,request.NamedInputReview!.Bindings,source,row,
            request.Setup.Components.SelectMany(c=>new[]{c.ExistingComponentId??"",c.Code}));
        return new(profile,bound.Context,bound.Cells);
    }
}
