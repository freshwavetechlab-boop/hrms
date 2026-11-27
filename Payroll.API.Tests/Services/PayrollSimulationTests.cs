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
    [Theory]
    [InlineData("2026-01", 31)]
    [InlineData("2026-02", 28)]
    [InlineData("2028-02", 29)]
    [InlineData("2026-11", 30)]
    public void DailyAttendanceDoesNotSetTheInteractiveDivisor(string month, int expected)
    {
        var request = Request(Wage("31000"));
        request.PayPeriod = month;
        request.PayrollDays = null;
        var daily = new PayRunRepository.PayRunAttendance { WorkingDays = 4, PresentDays = 0, PayableDays = 4 };
        var (days, source) = PayRunRepository.InteractiveSimulationDivisor(request, daily);
        Assert.Equal(expected, days);
        Assert.Equal("Calendar month", source);
        request.PresentDays = daily.PresentDays;
        request.PayableDays = daily.PayableDays;
        var result = PayRunRepository.SimulateTemplate(request, interactive: true);
        Assert.True(result.Complete);
        Assert.Equal(decimal.Round(31000m * 4 / expected, 2), result.Gross);
        Assert.Equal(expected, result.Inputs.Single(i => i.Name == "PAYROLL_DAYS").Value);
        Assert.Equal(0, result.Inputs.Single(i => i.Name == "PRESENT_DAYS").Value);
        Assert.Equal(4, result.Inputs.Single(i => i.Name == "PAYABLE_DAYS").Value);
        // Evidence calculations still require the exact divisor from their caller.
        Assert.False(PayRunRepository.SimulateTemplate(request).Complete);
    }

    [Fact]
    public void InteractiveDivisorUsesEmployeeMonthlySnapshotAndSupportsTemporaryOverride()
    {
        var request = Request(Wage());
        request.PayPeriod = "2026-11";
        request.PayrollDays = null;
        var snapshot = new PayRunRepository.PayRunAttendance { FromMonthlySnapshot = true, WorkingDays = 26 };
        Assert.Equal((26, "Saved monthly attendance divisor"), PayRunRepository.InteractiveSimulationDivisor(request, snapshot));
        request.PayrollDays = 24;
        Assert.Equal((24, "Temporary what-if input"), PayRunRepository.InteractiveSimulationDivisor(request, snapshot));
        Assert.Equal(26, snapshot.WorkingDays);
        request.PayrollDays = null;
        Assert.Equal(26, PayRunRepository.InteractiveSimulationDivisor(request, snapshot).Days);
        snapshot.WorkingDays = 32;
        Assert.Equal(30, PayRunRepository.InteractiveSimulationDivisor(request, snapshot).Days);
        snapshot.WorkingDays = 26.5m;
        Assert.Equal(30, PayRunRepository.InteractiveSimulationDivisor(request, snapshot).Days);
    }
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

    [Fact] public void EmployeeWhatIfUsesConfiguredLiteralUntilAnEmployeeSuppliesAnAmount()
    {
        var request = Request(Wage("500"), Deduction());
        request.Snapshot = request.Snapshot! with { Lines = [new("1", Value: "125"), new("2")] };
        var input = PayRunRepository.ResolveEmployeeTemplateComponentInputs(request, new Dictionary<string, decimal>(), "Employee amount");
        Assert.Single(input);
        Assert.Equal(125m, input["1"].Value);
        Assert.Equal("Component / template default", input["1"].Source);
        Assert.Equal(0m, PayRunRepository.ResolveEmployeeTemplateComponentInputs(request, new Dictionary<string, decimal> { ["1"] = 0 }, "Employee amount")["1"].Value);
        request.Snapshot = request.Snapshot with { Lines = [new("1", Value: "0"), new("2")] };
        Assert.Equal(0m, PayRunRepository.ResolveEmployeeTemplateComponentInputs(request, new Dictionary<string, decimal>(), "Employee amount")["1"].Value);
        request.ClearedComponentIds = ["1"];
        Assert.Null(PayRunRepository.ResolveEmployeeTemplateComponentInputs(request, new Dictionary<string, decimal>(), "Employee amount")["1"].Value);
    }
    [Fact] public void EmployeeWhatIfNeverDefaultsOverConflictingInputsOrTreatsMissingDefaultAsZero()
    {
        var request = Request(Wage("150"));
        var conflicting = PayRunRepository.ResolveEmployeeTemplateComponentInputs(request, new Dictionary<string, decimal> { ["1"] = 0, ["WAGES"] = 100 }, "Employee amount");
        Assert.Null(conflicting["1"].Value);
        Assert.Null(PayRunRepository.ResolveEmployeeTemplateComponentInputs(Request(Wage("")), new Dictionary<string, decimal>(), "Employee amount")["1"].Value);
        request.AmountOverrides["1"] = 0;
        Assert.Equal(0m, PayRunRepository.ResolveEmployeeTemplateComponentInputs(request, new Dictionary<string, decimal> { ["1"] = 100 }, "Employee amount")["1"].Value);
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
