using System.Globalization;
using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayrollRecurringInputTests
{
    private static (ExcelPayrollSetupRequest Request, ExcelPayslipCalculationSource Source, ExcelPayslipRow Row) Fixture(int days = 30, int payable = 27)
    {
        var request = new ExcelPayrollSetupRequest { BatchId = new string('a', 32), RowIds = ["source"], TemplateName = "Reviewed numeric source variant",
            EffectiveFrom = "2026-10-28", PayrollDaysColumnIndex = 0, PayableDaysColumnIndex = 1, ReviewedFixedInputs = true };
        var source = new ExcelPayslipCalculationSource { HeaderRow = 1, ColumnCount = 35,
            Columns = Enumerable.Range(0, 35).Select(column => new ExcelPayslipCalculationColumn { ColumnIndex = column, SourceHeader = "Reviewed input " + column, Label = "Reviewed input " + column, Kind = "info" }).ToList(),
            Rows = [new() { SourceRow = 7, Cells = Enumerable.Range(0, 35).Select(_ => (ExcelPayslipCalculationCell?)new ExcelPayslipCalculationCell()).ToList() }] };
        var cells = source.Rows[0].Cells;
        void Literal(int column, decimal value) => cells[column] = new() { Value = value.ToString(CultureInfo.InvariantCulture) };
        void Formula(int column, string formula) => cells[column] = new() { Value = "999999", FormulaText = formula };
        Literal(0, days); Literal(1, payable); Literal(8, 5); Literal(12, 10000); Literal(17, 50001);
        Formula(18, "ROUND(R7*45%,0)"); Formula(19, "ROUND(S7*50%,0)"); Formula(25, "M7*I7%");
        foreach (var (column, amount) in new[] { (20, 300m), (21, 400m), (22, 500m), (24, 600m), (26, 700m) }) Literal(column, amount);
        var monthly = new[] { 22500m, 11250m, 500m, 300m, 400m, 500m, 600m, 700m };
        var names = new[] { "NH_BASIC", "NH_HRA", "NH_INC", "NH_U", "NH_V", "NH_W", "NH_Y", "NH_AA" };
        var inputLetters = new[] { "S", "T", "Z", "U", "V", "W", "Y", "AA" };
        var row = new ExcelPayslipRow { Id = "source", SourceRow = 7, EmployeeCode = "ANON01", EmployeeName = "Anonymous employee" };
        for (var index = 0; index < names.Length; index++)
        {
            var amount = decimal.Round(monthly[index] * payable / days, 0, MidpointRounding.AwayFromZero);
            var output = 27 + index;
            source.Columns[output].Kind = "earning";
            cells[output] = new() { Value = amount.ToString(CultureInfo.InvariantCulture), FormulaText = $"ROUND({inputLetters[index]}7/A7*B7,0)" };
            request.Components.Add(new() { Key = names[index], Code = names[index], Name = names[index], CalculationType = "Formula", Value = "0", ProRata = false,
                ExpectedColumnIndex = output, Priority = index + 1 });
            row.Earnings.Add(new() { Label = names[index], Amount = amount });
            row.NetPay += amount;
        }
        foreach (var component in request.Components)
            component.Formula = ExcelPayrollSetupRepository.TranslateTransferFormula(cells[component.ExpectedColumnIndex!.Value]!.FormulaText!, request, source, row);
        return (request, source, row);
    }

    [Theory]
    [InlineData(30, 27, 33075)]
    [InlineData(31, 17, 20153)]
    [InlineData(30, 0, 0)]
    [InlineData(31, 31, 36750)]
    public void ReviewedInfoRulesUseActualSettingsFormulasWithFullMonthBasisAndNoCacheOverrides(int days, int payable, int expected)
    {
        var (request, source, row) = Fixture(days, payable);
        var before = JsonSerializer.Serialize(new { request, source, row });
        ExcelPayrollSetupRepository.ValidateRequest(request);
        var components = request.Components.Select((component, index) => new ExcelPayrollSetupRepository.ResolvedComponent(component, (index + 1).ToString(), true)).ToList();
        var result = ExcelPayrollSetupRepository.CalculateRow(42, request, components, source.Rows[0], row);
        Assert.Equal(441000m, result.Preview.AnnualCtc); // Full month36750, never partial/zero cached earnings annualized.
        Assert.Equal((decimal)expected, result.Preview.Gross);
        Assert.Equal((decimal)expected, result.Preview.Net);
        Assert.Empty(result.Inputs); // S/T/Z caches are never recurring employee overrides.
        Assert.All(request.Components, component => {
            Assert.False(component.ProRata);
            Assert.Contains("PAYROLL_DAYS", component.Formula);
            Assert.Contains("PAYABLE_DAYS", component.Formula);
            Assert.Equal("Exact supported translation", ExcelPayrollSetupRepository.InspectTransferFormula(request, source, row, component).Status);
        });
        Assert.Equal(before, JsonSerializer.Serialize(new { request, source, row }));
    }

    [Fact]
    public void CapturedInfoFormulaAndLiteralProvenanceChangeProofAndVersionedRules()
    {
        var (request, source, row) = Fixture(); var basic = request.Components[0];
        var original = ExcelPayrollSetupRepository.InspectTransferFormula(request, source, row, basic);
        var proof = ExcelPayrollSetupRepository.TranslateTransferFormulaWithEvidence(source.Rows[0].Cells[27]!.FormulaText!, request, source, row);
        Assert.Equal(new[] { 17, 18 }, proof.Dependencies.Select(dependency => dependency.ColumnIndex));
        Assert.Equal("50001", proof.Dependencies[0].Literal);
        Assert.Equal("ROUND(R7*45%,0)", proof.Dependencies[1].Formula);
        Assert.Null(proof.Dependencies[1].Literal); //999999 cached amount was not a rule/input.
        var components = request.Components.Select((component, index) => new ExcelPayrollSetupRepository.ResolvedComponent(component, (index + 1).ToString(), true)).ToList();
        var signature = ExcelPayrollSetupRepository.TransferRulesSignature(components);
        source.Rows[0].Cells[17]!.Value = "50000"; // Same rounded basic22500, but a different reviewed input/rule version.
        var changed = ExcelPayrollSetupRepository.InspectTransferFormula(request, source, row, basic);
        Assert.NotEqual(original.SourceHash, changed.SourceHash);
        Assert.Equal("Blocked", changed.Status);
        basic.Formula = ExcelPayrollSetupRepository.TranslateTransferFormula(source.Rows[0].Cells[27]!.FormulaText!, request, source, row);
        Assert.NotEqual(signature, ExcelPayrollSetupRepository.TransferRulesSignature(components));
        Assert.Equal("Exact supported translation", ExcelPayrollSetupRepository.InspectTransferFormula(request, source, row, basic).Status);
    }

    [Theory]
    [InlineData("unreviewed")]
    [InlineData("protected")]
    [InlineData("financial")]
    [InlineData("blank")]
    [InlineData("error")]
    [InlineData("missing")]
    [InlineData("unknown-function")]
    [InlineData("cycle")]
    [InlineData("shared-salary")]
    public void UnprovenInfoDependencyCannotBecomeASalaryConstant(string problem)
    {
        var (request, source, row) = Fixture();
        switch (problem)
        {
            case "unreviewed": request.ReviewedFixedInputs = false; break;
            case "protected": source.Columns[17].SourceHeader = "PAN Number"; break;
            case "financial": source.Columns[17].Kind = "employer"; break;
            case "blank": source.Rows[0].Cells[17]!.Value = ""; break;
            case "error": source.Rows[0].Cells[17]!.Error = "#REF!"; break;
            case "missing": source.Rows[0].Cells[17]!.MissingCachedValue = true; break;
            case "unknown-function": source.Rows[0].Cells[18]!.FormulaText = "FOO(R7)"; break;
            case "cycle": source.Rows[0].Cells[18]!.FormulaText = "T7*2"; break;
            case "shared-salary": source.Rows[0].Cells[18]!.FormulaText = "R8*45%"; break;
        }
        Assert.Throws<InvalidOperationException>(() => ExcelPayrollSetupRepository.TranslateTransferFormula(source.Rows[0].Cells[27]!.FormulaText!, request, source, row));
    }

    [Fact]
    public void CtcTargetIsNotSolvedFromItsOwnFormulaOrAnEarnedCache()
    {
        var (request, source, row) = Fixture(); request.Components[0].Formula = "MONTHLY_CTC*45%*PAYABLE_DAYS/PAYROLL_DAYS";
        var components = request.Components.Select((component, index) => new ExcelPayrollSetupRepository.ResolvedComponent(component, (index + 1).ToString(), true)).ToList();
        var error = Assert.Throws<InvalidOperationException>(() => ExcelPayrollSetupRepository.CalculateRow(42, request, components, source.Rows[0], row));
        Assert.Contains("independent salary target", error.Message);
    }

    [Fact]
    public void DownstreamCtcDeductionDoesNotCreateACircularEarningBasis()
    {
        var (request, source, row) = Fixture();
        request.Components.Add(new() { Key = "tax", Code = "NH_TAX", Name = "Monthly target deduction", Category = "Deduction", ComponentRole = "Regular Deduction",
            CalculationType = "Formula", Formula = "MONTHLY_CTC*1%", Value = "0", ProRata = false, Priority = 100 });
        row.NetPay -= 367.50m;
        var components = request.Components.Select((component, index) => new ExcelPayrollSetupRepository.ResolvedComponent(component, (index + 1).ToString(), true)).ToList();
        var result = ExcelPayrollSetupRepository.CalculateRow(42, request, components, source.Rows[0], row);
        Assert.Equal(441000m, result.Preview.AnnualCtc);
        Assert.Equal(33075m, result.Preview.Gross);
        Assert.Equal(367.50m, result.Preview.Deductions);
        Assert.Equal(32707.50m, result.Preview.Net);
        Assert.Empty(result.Inputs);
    }

    [Fact]
    public void MappedFinancialOutputsRemainEarnedTokensAndCannotBeExpandedAsInfo()
    {
        var (request, source, row) = Fixture();
        Assert.Equal("NH_BASIC_EARNED*5%", ExcelPayrollSetupRepository.TranslateTransferFormula("AB7*5%", request, source, row));
        request.Components.Add(new() { ExpectedColumnIndex = 27, Code = "AMBIGUOUS" });
        Assert.Throws<InvalidOperationException>(() => ExcelPayrollSetupRepository.TranslateTransferFormula("AB7*5%", request, source, row));
    }
}
