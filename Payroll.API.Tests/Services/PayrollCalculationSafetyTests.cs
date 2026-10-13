using System.Reflection;
using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public class PayrollCalculationSafetyTests
{
    private static object Component(string id, string code, string formula, string category = "Earning", int priority = 10, bool prorata = false, bool taxable = true, string statutory = "None", string calculation = "Formula") =>
        new { id, code, name = code, formula, value = formula, category, priority, proRata = prorata, taxable, statutoryType = statutory, calculationType = calculation, active = true, payType = "Fixed Pay" };

    private static PayRunEmployee Calculate(object[] components, decimal days = 30, Dictionary<string, decimal>? overrides = null, decimal tds = -1, decimal seedTds = 0, PayrollAdjustment[]? adjustments = null, string client = "11", decimal annualCtc = 360000m, int payrollDays = 30, PfPolicyPeriod? pfPeriod = null, string salaryStructureId = "1")
    {
        var repo = typeof(PayRunRepository);
        var type = repo.GetNestedType("PayRunSourceEmployee", BindingFlags.NonPublic)!;
        var employee = Activator.CreateInstance(type)!;
        void Set(string name, object value) => type.GetProperty(name)!.SetValue(employee, value);
        Set("Id", 1); Set("ClientId", 11); Set("EmployeeCode", "TEST"); Set("FirstName", "Test"); Set("AnnualCtc", annualCtc); Set("SalaryStructureId", salaryStructureId);
        if (pfPeriod is not null) Set("PfPolicyPeriod", pfPeriod);
        Set("SalaryComponents", overrides ?? []); Set("HasPersonalDetails", true); Set("Tds", seedTds);
        var lines = components.Select(component => new { componentId = component.GetType().GetProperty("id")!.GetValue(component) });
        var json = JsonSerializer.Serialize(new { salaryComponents = components, salaryStructures = new[] { new { id = "1", clientId = client, active = true, lines } } });
        try { return (PayRunEmployee)repo.GetMethod("BuildEmployee", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [1, employee, json, "2026-09", payrollDays, days, days, adjustments ?? [], 0m, 0m, tds, false, "TDS"])!; }
        catch (TargetInvocationException exception) { throw exception.InnerException!; }
    }

    private static decimal Amount(PayRunEmployee row, string code)
    {
        using var doc = JsonDocument.Parse(row.DetailsJson);
        return doc.RootElement.EnumerateArray().Where(line => line.GetProperty("Id").GetString() == code).Sum(line => line.GetProperty("amount").GetDecimal());
    }

    [Theory]
    [InlineData("UNKNOWN + 1")]
    [InlineData("10 / 0")]
    [InlineData("MIN(10,20")]
    [InlineData("10 +")]
    [InlineData("10 20")]
    [InlineData("ROUND(10,2)")]
    [InlineData("BOGUS(10)")]
    [InlineData("")]
    public void InvalidFormulasFailInsteadOfPayingZero(string formula) => Assert.Throws<InvalidOperationException>(() => Calculate([Component("1", "WAGE", formula)]));

    [Fact]
    public void CircularDependenciesFail() => Assert.Throws<InvalidOperationException>(() => Calculate([Component("1", "A", "B"), Component("2", "B", "A")]));

    [Theory]
    [InlineData(0)]
    [InlineData(18000)]
    public void ExplicitEmployeeAmountWorksWithoutMasterDefault(decimal amount)
    {
        var row = Calculate([Component("1", "WAGE", "", prorata: true, calculation: "Fixed Amount")], 15, new() { ["1"] = amount });
        Assert.Equal(amount / 2, row.GrossPay);
    }

    [Fact]
    public void MissingFixedAmountCannotBecomeZero() => Assert.Throws<InvalidOperationException>(() => Calculate([Component("1", "WAGE", "", calculation: "Fixed Amount")]));

    [Fact]
    public void DependenciesAreResolvedBeforeTheirConsumers()
    {
        var row = Calculate([Component("1", "WAGE", "15414", priority: 100, prorata: true), Component("2", "PF", "MIN(WAGE_EARNED,15000)*12%", "Deduction", priority: 1)], 14);
        Assert.Equal(7193.20m, row.GrossPay);
        Assert.Equal(863.18m, Amount(row, "PF"));
    }

    [Fact]
    public void EarningsSumCountsEachEarningOnceAndExcludesDeductions()
    {
        var row = Calculate([Component("1", "BASIC", "10000"), Component("2", "RECOVERY", "1000", "Deduction", 20), Component("3", "FEE", "SUM(EARNINGS)*10%", "Deduction", 30)]);
        Assert.Equal(1000m, Amount(row, "FEE"));
    }

    [Fact]
    public void ResidualDoesNotCountMonthlyAndEarnedAliases()
    {
        var row = Calculate([Component("1", "BASIC", "10000"), Component("2", "ALLOWANCE", "", priority: 20, calculation: "Residual / Balancing")]);
        Assert.Equal(20000m, Amount(row, "ALLOWANCE"));
        Assert.Equal(30000m, row.GrossPay);
    }

    [Fact]
    public void EmployeeOverrideFlowsThroughEarnedDependencies()
    {
        var row = Calculate([Component("1", "WAGE", "10000", prorata: true, calculation: "Fixed Amount"), Component("2", "TAX", "WAGE_EARNED*10%", "Deduction", 20)], 15, new() { ["1"] = 20000, ["2"] = 2000 });
        Assert.Equal(10000m, row.GrossPay);
        Assert.Equal(1000m, Amount(row, "TAX"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(300)]
    public void ProjectedTdsReplacesTemplateAndSeedEvenWhenZero(int projected)
    {
        var row = Calculate([Component("1", "WAGE", "10000"), Component("2", "CLIENT_TAX", "200", "Deduction", 20, statutory: "TDS")], tds: projected, seedTds: 100);
        Assert.Equal(projected, row.ManualTds);
        Assert.Equal(projected, row.StatutoryDeductions);
    }

    [Fact]
    public void ManualTdsTotalIncludesExistingTdsWhenProjectionIsDisabled()
    {
        var row = Calculate([Component("1", "WAGE", "10000")], seedTds: 100);
        Assert.Equal(100, row.ManualTds);
        Assert.Equal(100, row.StatutoryDeductions);
    }

    [Fact]
    public void TaxableSnapshotExcludesBenefitsAndNonTaxableReimbursements()
    {
        var row = Calculate([Component("1", "WAGE", "10000"), Component("2", "BENEFIT", "2000", "Benefit"), Component("3", "REIMBURSE", "1000", "Reimbursement", taxable: false)], adjustments: [new() { Amount = 500, Taxable = false, AdjustmentType = "Earning" }, new() { Amount = 250, Taxable = true, AdjustmentType = "Earning" }]);
        Assert.Equal(10250, Amount(row, "TAXABLE_PAY"));
    }

    [Fact]
    public void TemplateFromAnotherClientIsRejected() => Assert.Throws<InvalidOperationException>(() => Calculate([Component("1", "WAGE", "1000")], client: "12"));

    [Fact]
    public void SharedTemplateClientScopeIsSupported() => Assert.Equal(1000, Calculate([Component("1", "WAGE", "1000")], client: "12:Other;11:Test").GrossPay);

    [Fact]
    public void NumericPercentageUsesCtcRatherThanLiteralAmount() => Assert.Equal(3000, Calculate([Component("1", "WAGE", "10", calculation: "Percentage of CTC")]).GrossPay);

    [Fact]
    public void ComponentPercentageResolvesItsBaseFirst()
    {
        var row = Calculate([Component("1", "BASIC", "10000", priority: 100), Component("2", "HRA", "40%", priority: 1, calculation: "Percentage of Component")]);
        Assert.Equal(4000, Amount(row, "HRA"));
    }

    [Fact]
    public void LegacyRoundedDerivedAmountDoesNotBecomeAnOverride()
    {
        var row = Calculate([Component("1", "WAGE", "15414", prorata: true), Component("2", "ESI", "WAGE_EARNED*0.75%", "Deduction", 20)], 14, new() { ["1"] = 15414, ["2"] = 116 });
        Assert.Equal(53.95m, Amount(row, "ESI"));
    }

    [Theory]
    [InlineData(0, 30, 0)]
    [InlineData(22, 30, 15400)]
    [InlineData(22.5, 30, 15750)]
    [InlineData(28, 28, 19600)]
    [InlineData(31, 31, 21700)]
    public void DailyWagesUsePayableUnitsOnceWithoutCtc(decimal days, int payrollDays, decimal expected)
    {
        var row = Calculate([Component("1", "DAILY_WAGES", "700", prorata: true, calculation: "Daily Rate")], days, annualCtc: 0, payrollDays: payrollDays);
        Assert.Equal(expected, row.GrossPay);
        Assert.Equal(700 * payrollDays, row.MonthlyGross);
        using var doc = JsonDocument.Parse(row.DetailsJson);
        var line = doc.RootElement[0];
        Assert.Equal(700, line.GetProperty("rate").GetDecimal());
        Assert.Equal(days, line.GetProperty("units").GetDecimal());
        Assert.False(line.GetProperty("ProRata").GetBoolean());
    }

    [Theory]
    [InlineData(28)]
    [InlineData(30)]
    [InlineData(31)]
    public void EmployeeDailyRateAndEarnedFormulaDoNotFreezeMonthlyDeductions(int payrollDays)
    {
        var row = Calculate([Component("1", "DAILY_WAGES", "700", calculation: "Daily Rate"), Component("2", "DEDUCTION", "DAILY_WAGES_EARNED*5%", "Deduction", 20)], 22, new() { ["1"] = 850, ["2"] = 1275 }, annualCtc: 0, payrollDays: payrollDays);
        Assert.Equal(18700, row.GrossPay);
        Assert.Equal(935, Amount(row, "DEDUCTION"));
    }

    [Fact]
    public void DailyRateCanSupplyThePublishedPfMonthlyEquivalent()
    {
        var version = new PfPolicyVersion { Id = "daily-v1", ClientId = 11, SalaryStructureId = "1", BaseComponentCode = "DAILY_WAGES", EffectiveFrom = new(2026, 9, 1), CeilingBasis = "CalendarDays", Contributions = [new() { ComponentId = "2", ComponentCode = "PF", StatutoryType = "PF Employee", RatePercent = 12, MonthlyWageCeiling = null }] };
        var row = Calculate([Component("1", "DAILY_WAGES", "700", calculation: "Daily Rate"), Component("2", "PF", "0", "Deduction", 20, statutory: "PF Employee")], 22, annualCtc: 0, pfPeriod: new(new(2026, 9, 1), new(2026, 9, 30), [version], []));
        Assert.Equal(15400, row.GrossPay);
        Assert.Equal(1848, Amount(row, "PF"));
        Assert.Contains("pfPolicy", row.DetailsJson);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-700")]
    [InlineData("WAGE")]
    public void InvalidDailyRatesFail(string value) => Assert.Throws<InvalidOperationException>(() => Calculate([Component("1", "DAILY_WAGES", value, calculation: "Daily Rate")], annualCtc: 0));

    [Fact]
    public void DailyRateTemplateCannotBeSelectedAsImplicitClientFallback() => Assert.Throws<InvalidOperationException>(() => Calculate([Component("1", "DAILY_WAGES", "700", calculation: "Daily Rate")], salaryStructureId: ""));

    [Fact]
    public void ExistingMonthlyClientFallbackStillWorks() => Assert.Equal(18000, Calculate([Component("1", "WAGE", "18000", calculation: "Fixed Amount")], salaryStructureId: "").GrossPay);
}
