using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public class PayrollSimulationTests
{
    private static PayrollReviewComponent Wage(string value = "15414") => new("1", "WAGES", "Earning", "Regular Earning", "None", "Fixed Amount", value, "", "", true, true, true, 100, "Fixed Pay");
    private static PayrollReviewComponent Deduction(string formula = "MIN(WAGES_EARNED,15000)*12%") => new("2", "EPF", "Deduction", "Statutory Deduction", "PF Employee", "Formula", "", formula, "", false, false, true, 1, "Fixed Pay");
    private static PayrollSimulationRequest Request(params PayrollReviewComponent[] components) => new()
    {
        ClientId = 11, TemplateId = "1", PayrollDays = 30, PresentDays = 14, PayableDays = 14, AnnualCtc = 184968,
        Snapshot = new("Template", 11, "", 184968, components.ToList(), components.Select(c => new PayrollReviewLine(c.Id)).ToList())
    };
    [Fact] public void CatalogueOnlyAdvertisesImplementedSystemInputs()
    {
        var values = PayrollValueCatalogue.Resolve(25000, 30, 20, 22.5m, new() { ["EARNINGS_BEFORE_THIS"] = 18000, ["FIXED_EARNINGS_BEFORE_THIS"] = 18000, ["FIXED_EARNINGS"] = 18000, ["GROSS_EARNED"] = 13500 });
        Assert.All(PayrollValueCatalogue.SystemValues, value => Assert.True(values.ContainsKey(value.Token), value.Token));
        Assert.DoesNotContain("OVERTIME_HOURS", values.Keys);
        Assert.Equal(25000, values["GROSS"]); Assert.Equal(13500, values["EARNED_EARNINGS_BEFORE_THIS"]);
    }
    [Fact] public void CatalogueMarksMissingDependenciesAndExcludesSelfAndInactive()
    {
        var result = PayrollValueCatalogue.Build(new([new("1", "WAGES", "Wages", true), new("2", "PF", "PF", true), new("3", "OLD", "Old", false)], [], "2"));
        Assert.Contains(result, x => x.Token == "WAGES_EARNED" && x.Availability.StartsWith("Missing"));
        Assert.DoesNotContain(result, x => x.Token.StartsWith("PF") || x.Token.StartsWith("OLD"));
        Assert.Contains(result, x => x.Token == "GROSS" && x.Legacy);
    }
    [Fact] public void ServerSimulationShowsResolvedValuesAndActualRounding()
    {
        var result = PayRunRepository.SimulateTemplate(Request(Wage(), Deduction()));
        Assert.True(result.Complete); Assert.Equal(7193.20m, result.Gross); Assert.Equal(863.18m, result.Deductions); Assert.Equal(6330.02m, result.Net);
        var pf = result.Lines.Single(l => l.Code == "EPF");
        Assert.Equal(7193.20m, pf.Inputs["WAGES_EARNED"]); Assert.Contains("7193.2", pf.Substituted); Assert.Equal(863.184m, pf.Raw);
        Assert.Equal("WAGES", result.Lines[0].Code);
    }
    [Fact] public void ZeroPayableDaysIsDifferentFromMissingDays()
    {
        var request = Request(Wage(), Deduction()); request.PayableDays = 0;
        Assert.Equal(0, PayRunRepository.SimulateTemplate(request).Net);
        request.PayableDays = null; var missing = PayRunRepository.SimulateTemplate(request);
        Assert.False(missing.Complete); Assert.Null(missing.Net); Assert.Empty(missing.Lines);
    }
    [Theory] [InlineData(32)] [InlineData(-1)]
    public void InvalidDaysRejected(decimal days) { var r = Request(Wage()); r.PayableDays = days; Assert.Throws<InvalidOperationException>(() => PayRunRepository.SimulateTemplate(r)); }
    [Fact] public void DayCountsCannotExceedDenominator() { var r = Request(Wage()); r.PayrollDays = 10; Assert.False(PayRunRepository.SimulateTemplate(r).Complete); }
    [Fact] public void MissingAmountIsNotZero()
    {
        Assert.False(PayRunRepository.SimulateTemplate(Request(Wage(""))).Complete);
        Assert.Equal(0, PayRunRepository.SimulateTemplate(Request(Wage("0"))).Gross);
        var r = Request(Wage("")); r.AmountOverrides["1"] = 0;
        Assert.Equal(0, PayRunRepository.SimulateTemplate(r).Gross);
    }
    [Fact] public void OverrideFlowsThroughDependentFormulas()
    {
        var r = Request(Wage(), Deduction()); r.AmountOverrides["1"] = 18000; r.PayableDays = 15;
        var result = PayRunRepository.SimulateTemplate(r); Assert.Equal(9000, result.Gross); Assert.Equal(1080, result.Deductions);
        Assert.Contains("override", result.Lines[0].Source);
    }
    [Fact] public void DailyRateCanComeFromExplicitOverrideWithMissingDefault()
    {
        var r = Request(Wage("") with { CalculationType = "Daily Rate", ProRata = false }); r.AmountOverrides["1"] = 700; r.PayableDays = 22.5m;
        var result = PayRunRepository.SimulateTemplate(r); Assert.True(result.Complete); Assert.Equal(15750, result.Gross);
    }
    [Theory] [InlineData("UNKNOWN*10%")] [InlineData("EPF*10%")] [InlineData("WAGES/0")]
    public void UnresolvedOrInvalidFormulaDoesNotReturnPlausibleTotals(string formula)
    { var result = PayRunRepository.SimulateTemplate(Request(Wage(), Deduction(formula))); Assert.False(result.Complete); Assert.Null(result.Net); }
    [Fact] public void MissingReferencedComponentIsActionable()
    { var r = PayRunRepository.SimulateTemplate(Request(Deduction())); Assert.Contains(r.Issues, i => i.Component == "EPF" && i.Message.Contains("WAGES_EARNED") && i.Action.Contains("Add")); }
    [Fact] public void MissingCtcIsNotSilentlyZero()
    {
        var r = Request(Wage() with { CalculationType = "Formula", Formula = "CTC*50%" }); r.AnnualCtc = null;
        Assert.False(PayRunRepository.SimulateTemplate(r).Complete); r.AnnualCtc = 0;
        Assert.Equal(0, PayRunRepository.SimulateTemplate(r).Gross);
    }
    [Fact] public void LegacyAndClearAggregateNamesHaveSameValue()
    {
        var a = PayRunRepository.SimulateTemplate(Request(Wage(), Deduction("GROSS_EARNED*5%") with { Priority = 200 }));
        var b = PayRunRepository.SimulateTemplate(Request(Wage(), Deduction("EARNED_EARNINGS_BEFORE_THIS*5%") with { Priority = 200 }));
        Assert.Equal(a.Net, b.Net); Assert.Contains(a.Issues, i => i.Message.Contains("Legacy")); Assert.True(b.Complete);
    }
    [Fact] public void BalanceTargetMustExistAndBeResolved()
    {
        var balance = Wage() with { Id = "3", Code = "BALANCE", CalculationType = "Residual / Balancing", BaseComponent = "MISSING", Value = "", Priority = 1 };
        Assert.False(PayRunRepository.SimulateTemplate(Request(Wage(), balance)).Complete);
        var result = PayRunRepository.SimulateTemplate(Request(Wage(), balance with { BaseComponent = "WAGES" }));
        Assert.True(result.Complete); Assert.Equal("WAGES", result.Lines[0].Code);
    }
    [Theory]
    [InlineData("payroll.run", 11, 11, true, true)]
    [InlineData("settings.manage", 11, 12, true, false)]
    [InlineData("client.settings.manage", 11, 11, false, true)]
    [InlineData("client.settings.manage", 11, 11, true, false)]
    [InlineData("ess.profile", 11, 11, true, false)]
    public void PermissionsAreClientScoped(string permission, int userClient, int requested, bool employee, bool expected) =>
        Assert.Equal(expected, PayrollCalculationEndpoints.CanAccess(new AuthUser { Id = 1, IsActive = true, ClientId = userClient, Permissions = [permission] }, requested, employee));
    [Fact] public void InactiveAndPasswordResetUsersCannotSimulate()
    {
        Assert.False(PayrollCalculationEndpoints.CanAccess(new AuthUser { Id = 1, IsActive = false, Permissions = ["settings.manage"] }));
        Assert.False(PayrollCalculationEndpoints.CanAccess(new AuthUser { Id = 1, IsActive = true, MustChangePassword = true, Permissions = ["settings.manage"] }));
    }
    [Fact] public void OutOfTemplateOverrideIsRejected()
    { var r = Request(Wage()); r.AmountOverrides["99"] = 500; Assert.Throws<InvalidOperationException>(() => PayRunRepository.SimulateTemplate(r)); }
}
