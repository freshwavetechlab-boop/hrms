using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public class ExcelPayrollProrationTests
{
    private static ExcelPayrollSetupRequest Request(bool earned, bool proRata) => new()
    {
        BatchId = new string('a', 32), RowIds = ["source"], TemplateName = "Reviewed source variant", EffectiveFrom = "2026-09-01",
        PayrollDaysColumnIndex = 0, PayableDaysColumnIndex = 1,
        Components = [
            new() { Key = "wage", Code = "TEST_WAGE", Name = "Monthly wage", SourceColumnIndex = 2, ExpectedColumnIndex = 3, ProRata = true, Priority = 10 },
            new() { Key = "employer", Code = "TEST_ER", Name = "Employer contribution", Category = "Benefit", ComponentRole = "Employer Contribution", StatutoryType = "PF Employer",
                CalculationType = "Formula", Formula = earned ? "TEST_WAGE_EARNED*13%" : "TEST_WAGE*13%", Value = "", ExpectedColumnIndex = 4, ProRata = proRata, Taxable = false, Priority = 20 }
        ]
    };

    [Theory]
    [InlineData(30, 20000, 2600)]
    [InlineData(27, 18000, 2340)]
    [InlineData(0, 0, 0)]
    public void MonthlyBaseProratesOnceAndEarnedFormulaDoesNotProrateAgain(int payable, int expectedGross, int expectedEmployer)
    {
        var request = Request(earned: true, proRata: false);
        ExcelPayrollSetupRepository.ValidateRequest(request);
        var components = request.Components.Select((c, i) => new ExcelPayrollSetupRepository.ResolvedComponent(c, (i + 1).ToString(), true)).ToList();
        var source = new ExcelPayslipCalculationSourceRow { SourceRow = 6, Cells = new[] { "30", payable.ToString(), "20000", expectedGross.ToString(), expectedEmployer.ToString() }
            .Select(value => (ExcelPayslipCalculationCell?)new ExcelPayslipCalculationCell { Value = value }).ToList() };
        var result = ExcelPayrollSetupRepository.CalculateRow(11, request, components, source, new ExcelPayslipRow { Id = "source", NetPay = expectedGross });
        Assert.Equal((decimal)expectedGross, result.Preview.Gross);
        Assert.Equal((decimal)expectedEmployer, result.Preview.Benefits);
        Assert.Equal(20000m, result.Inputs["1"]);
        Assert.DoesNotContain(result.Issues, issue => issue.Severity == "Error");
        Assert.False(request.Components[1].ProRata);
    }

    [Theory]
    [InlineData("TEST_WAGE_EARNED*13%")]
    [InlineData("MIN(TEST_WAGE*PAYABLE_DAYS/PAYROLL_DAYS,15000)*13%")]
    [InlineData("TEST_WAGE*PRESENT_DAYS/PAYROLL_DAYS*3.25%")]
    [InlineData("IF(LOP_DAYS>0,TEST_WAGE_EARNED*13%,0)")]
    [InlineData("GROSS_EARNED*13%")]
    public void TransferRejectsAnAttendanceFormulaWithASecondProration(string formula)
    {
        var request = Request(earned: true, proRata: true);
        request.Components[1].Formula = formula;
        var error = Assert.Throws<InvalidOperationException>(() => ExcelPayrollSetupRepository.ValidateRequest(request));
        Assert.Contains("Turn off 'Prorate using payable days'", error.Message);
        Assert.True(request.Components[1].ProRata); // Validation never silently edits the reviewed request.
    }

    [Fact]
    public void MonthlyFormulaAndFixedAmountKeepTheirSingleProration()
    {
        var request = Request(earned: false, proRata: true);
        ExcelPayrollSetupRepository.ValidateRequest(request);
        Assert.True(request.Components.All(c => c.ProRata));
        var components = request.Components.Select((c, i) => new ExcelPayrollSetupRepository.ResolvedComponent(c, (i + 1).ToString(), true)).ToList();
        var source = new ExcelPayslipCalculationSourceRow { SourceRow = 6, Cells = new[] { "30", "27", "20000", "18000", "2340" }
            .Select(value => (ExcelPayslipCalculationCell?)new ExcelPayslipCalculationCell { Value = value }).ToList() };
        var result = ExcelPayrollSetupRepository.CalculateRow(11, request, components, source, new ExcelPayslipRow { Id = "source", NetPay = 18000 });
        Assert.Equal(18000m, result.Preview.Gross);
        Assert.Equal(2340m, result.Preview.Benefits);
    }

    [Fact]
    public void VerifiedSourceTwentySevenDayDraftMatchesCoreComponentsWithoutSecondProration()
    {
        // Anonymous replay of the inspected saved-batch wage/day/formula evidence.
        // This proves draft arithmetic, not a saved employee policy or an attendance correction.
        var request = Request(earned: true, proRata: false);
        request.Components.AddRange([
            new() { Key="pf", Code="TEST_PF", Name="Employee PF", Category="Deduction", ComponentRole="Statutory Deduction", StatutoryType="PF Employee", CalculationType="Formula", Formula="TEST_WAGE_EARNED*12%", Value="", ProRata=false, Priority=30 },
            new() { Key="esi", Code="TEST_ESI", Name="Employee ESI", Category="Deduction", ComponentRole="Statutory Deduction", StatutoryType="ESI Employee", CalculationType="Formula", Formula="TEST_WAGE_EARNED*0.75%", Value="", ProRata=false, Priority=40 },
            new() { Key="esi-er", Code="TEST_ESI_ER", Name="Employer ESI", Category="Benefit", ComponentRole="Employer Contribution", StatutoryType="ESI Employer", CalculationType="Formula", Formula="TEST_WAGE_EARNED*3.25%", Value="", ProRata=false, Priority=50 }
        ]);
        ExcelPayrollSetupRepository.ValidateRequest(request);
        var components = request.Components.Select((c,i) => new ExcelPayrollSetupRepository.ResolvedComponent(c,(i+1).ToString(),true)).ToList();
        var source = new ExcelPayslipCalculationSourceRow { SourceRow=43, Cells = new[] { "30", "27", "15414", "13872.60", "1803.438" }
            .Select(value => (ExcelPayslipCalculationCell?)new ExcelPayslipCalculationCell { Value=value }).ToList() };
        var result = ExcelPayrollSetupRepository.CalculateRow(11,request,components,source,new ExcelPayslipRow { Id="source", NetPay=12103.8435m });
        Assert.Equal(13872.60m,result.Preview.Gross);
        Assert.Equal(1768.75m,result.Preview.Deductions);
        Assert.Equal(2254.30m,result.Preview.Benefits);
        Assert.Equal(12103.85m,result.Preview.Net);
        Assert.Equal(1803.44m,result.Preview.Lines.Single(l=>l.Code=="TEST_ER").CalculatedAmount);
        Assert.Equal(450.86m,result.Preview.Lines.Single(l=>l.Code=="TEST_ESI_ER").CalculatedAmount);
        Assert.True(request.Components[0].ProRata);
        Assert.All(request.Components.Skip(1),component=>Assert.False(component.ProRata));
        Assert.Single(result.Inputs); // Only the monthly base, never captured calculated contributions.
    }

    [Fact]
    public void RowSpecificFormulaCannotBypassTheProrationGuard()
    {
        var request = Request(earned: false, proRata: true);
        request.Components[1].RowFormulas["source"] = "TEST_WAGE_EARNED*13%";
        Assert.Throws<InvalidOperationException>(() => ExcelPayrollSetupRepository.ValidateRequest(request));
        var components = request.Components.Select((c, i) => new ExcelPayrollSetupRepository.ResolvedComponent(c, (i + 1).ToString(), true)).ToList();
        Assert.Throws<InvalidOperationException>(() => ExcelPayrollSetupRepository.CalculateRow(11, request, components,
            new ExcelPayslipCalculationSourceRow(), new ExcelPayslipRow { Id = "source" }));
    }

    [Theory]
    [InlineData("2026-10-01", 31, "31", "20000", "2600")]
    [InlineData("2026-10-01", 31, "30", "19354.84", "2516.13")]
    [InlineData("2026-10-01", 31, "30.5", "19677.42", "2558.06")]
    [InlineData("2026-11-01", 30, "0", "0", "0")]
    public void NextPeriodReusesMonthlyRateWithChangedAttendance(string effectiveDate, int divisor, string payable, string expectedGross, string expectedEmployer)
    {
        // Synthetic future-period replay: the reviewed monthly rate/rules stay
        // unchanged, only the period's working/payable inputs vary. This does
        // not invent leave balances, save attendance or generate future payroll.
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        var request = Request(earned: true, proRata: false);
        request.EffectiveFrom = effectiveDate;
        ExcelPayrollSetupRepository.ValidateRequest(request);
        var components = request.Components.Select((component, index) =>
            new ExcelPayrollSetupRepository.ResolvedComponent(component, (index + 1).ToString(culture), true)).ToList();
        var source = new ExcelPayslipCalculationSourceRow { SourceRow = 6,
            Cells = new[] { divisor.ToString(culture), payable, "20000", expectedGross, expectedEmployer }
                .Select(value => (ExcelPayslipCalculationCell?)new ExcelPayslipCalculationCell { Value = value }).ToList() };
        var result = ExcelPayrollSetupRepository.CalculateRow(11, request, components, source,
            new ExcelPayslipRow { Id = "source", NetPay = decimal.Parse(expectedGross, culture) });
        Assert.Equal(decimal.Parse(expectedGross, culture), result.Preview.Gross);
        Assert.Equal(decimal.Parse(expectedEmployer, culture), result.Preview.Benefits);
        Assert.Equal(20000m, result.Inputs["1"]);
        Assert.Single(result.Inputs);
        Assert.Equal("TEST_WAGE_EARNED*13%", request.Components[1].Formula);
        Assert.False(request.Components[1].ProRata);
        Assert.DoesNotContain(result.Issues, issue => issue.Severity == "Error");
    }
}
