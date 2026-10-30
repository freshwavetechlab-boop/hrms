using System.Text.Json;
using System.Text.Json.Nodes;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayrollImportFidelityTests
{
    private static ExcelPayrollSetupComponent Component() => new()
    {
        Key = "basic", Code = "BASIC", Name = "Basic", Taxable = true, Priority = 10,
        SourceColumnIndex = 2, ExpectedColumnIndex = 3
    };

    private static JsonObject Master() => JsonNode.Parse("""
        {"id":"1","code":"BASIC","name":"Basic","category":"Earning","componentRole":"Regular Earning",
        "statutoryType":"None","active":true,"calculationType":"Fixed Amount","taxable":true,"priority":"10","payType":"Fixed Pay"}
        """)!.AsObject();

    [Theory]
    [InlineData("taxable", "false", "Taxable")]
    [InlineData("priority", "999", "Priority")]
    [InlineData("payType", "Variable Pay", "PayType")]
    public void ReuseBlocksMetadataThatTheTemplateCannotOverride(string field, string value, string expectedDifference)
    {
        var master = Master();
        master[field] = value;
        var proposed = Component();
        var beforeMaster = master.ToJsonString();
        var beforeProposed = JsonSerializer.Serialize(proposed);

        Assert.Contains(expectedDifference, ExcelPayrollSetupRepository.ReusedComponentDifferences(master, proposed));
        Assert.Equal(beforeMaster, master.ToJsonString());
        Assert.Equal(beforeProposed, JsonSerializer.Serialize(proposed));
    }

    [Fact]
    public void MatchingMasterAllowsExplicitTemplateFormulaAndProrationOverrides()
    {
        var master = Master();
        var proposed = Component();
        proposed.CalculationType = "Formula";
        proposed.Formula = "ROUND(BASIC_MONTHLY/PAYROLL_DAYS*PAYABLE_DAYS,0)";
        proposed.ProRata = false;
        Assert.Empty(ExcelPayrollSetupRepository.ReusedComponentDifferences(master, proposed));
        proposed.IsPeriodAdjustment = true;
        master["payType"] = "Variable Pay";
        Assert.Empty(ExcelPayrollSetupRepository.ReusedComponentDifferences(master, proposed));
    }

    [Theory]
    [InlineData("taxable", null, "Taxable")]
    [InlineData("taxable", "unknown", "Taxable")]
    [InlineData("priority", null, "Priority")]
    [InlineData("priority", "10.5", "Priority")]
    [InlineData("payType", null, "PayType")]
    public void MissingOrInvalidMasterMetadataCannotBeAssumedToMatch(string field, string? value, string expectedDifference)
    {
        var master = Master();
        master[field] = value;
        Assert.Contains(expectedDifference, ExcelPayrollSetupRepository.ReusedComponentDifferences(master, Component()));
    }

    [Theory]
    [InlineData("", true, "Blocked")]
    [InlineData("", false, "Blocked")]
    [InlineData("C3/A3*B3", true, "Blocked")]
    public void FormulaBackedRecurringInputCannotBeFrozenByLiteralOutputReview(string outputFormula, bool reviewedFixed, string status)
    {
        var request = new ExcelPayrollSetupRequest { ReviewedFixedInputs = reviewedFixed, PayrollDaysColumnIndex = 0, PayableDaysColumnIndex = 1, Components = [Component()] };
        var source = new ExcelPayslipCalculationSource { Rows = [new() { SourceRow = 3, Cells = [
            new() { Value = "30" }, new() { Value = "30" },
            new() { Value = "100", FormulaText = "VLOOKUP(A3,'[1]SalaryGrid'!A1:D9,4,0)" },
            new() { Value = "100", FormulaText = outputFormula }
        ] }] };
        var row = new ExcelPayslipRow { Id = "one", SourceRow = 3 };
        var before = JsonSerializer.Serialize(source);
        var proof = ExcelPayrollSetupRepository.InspectTransferFormula(request, source, row, request.Components[0]);
        Assert.Equal(status, proof.Status);
        Assert.Contains("recurring constant", proof.Reason);
        Assert.Equal(before, JsonSerializer.Serialize(source));
    }

    [Fact]
    public void ReviewedLiteralRecurringInputAndLiteralOutputRetainFixedSourceReview()
    {
        var component = Component();
        var source = new ExcelPayslipCalculationSource { Rows = [new() { SourceRow = 3, Cells = [
            new() { Value = "30" }, new() { Value = "30" }, new() { Value = "100" }, new() { Value = "100" }
        ] }] };
        var proof = ExcelPayrollSetupRepository.InspectTransferFormula(new() { ReviewedFixedInputs = true }, source,
            new() { Id = "one", SourceRow = 3 }, component);
        Assert.Equal("Reviewed fixed source", proof.Status);
    }

    [Fact]
    public void RecurringVariablePayKeepsItsSalaryInputWithoutBecomingAPeriodAdjustment()
    {
        var component = Component();
        component.PayType = "Variable Pay";
        var master = Master();
        master["payType"] = "Variable Pay";
        Assert.Empty(ExcelPayrollSetupRepository.ReusedComponentDifferences(master, component));
        var bonus = new ExcelPayrollSetupComponent
        {
            Key = "bonus", Code = "BONUS", Name = "Fixed-pay bonus", CalculationType = "Formula",
            Formula = "FIXED_EARNINGS_BEFORE_THIS*10%", ProRata = false, Priority = 20, ExpectedColumnIndex = 4
        };
        var request = new ExcelPayrollSetupRequest { PayrollDaysColumnIndex = 0, PayableDaysColumnIndex = 1, Components = [component, bonus] };
        var source = new ExcelPayslipCalculationSourceRow { SourceRow = 3, Cells = [
            new() { Value = "30" }, new() { Value = "15" }, new() { Value = "100" }, new() { Value = "50" }, new() { Value = "0" }
        ] };
        var calculated = ExcelPayrollSetupRepository.CalculateRow(42, request,
            [new(component, "1", false), new(bonus, "2", true)], source, new() { Id = "one", NetPay = 50 });
        Assert.False(component.IsPeriodAdjustment);
        Assert.Equal(100, calculated.Inputs["1"]);
        Assert.Empty(calculated.Adjustments);
        Assert.Equal(0, calculated.Preview.Lines.Single(line => line.Code == "BONUS").CalculatedAmount);
        Assert.Equal(50, calculated.Preview.Net);
    }

    [Theory]
    [InlineData("Hourly", false)]
    [InlineData("Fixed Pay", true)]
    public void InvalidPayTypeAndFixedPayPeriodAdjustmentAreRejected(string payType, bool adjustment)
    {
        var component = Component();
        component.PayType = payType;
        component.IsPeriodAdjustment = adjustment;
        var request = new ExcelPayrollSetupRequest
        {
            BatchId = new string('a', 32), RowIds = ["one"], TemplateName = "Synthetic review", EffectiveFrom = "2026-09-01",
            WorkWeek = "Sunday off", PayrollDaysColumnIndex = 0, PayableDaysColumnIndex = 1, Components = [component]
        };
        Assert.Contains("period adjustment requires Variable Pay", Assert.Throws<InvalidOperationException>(() => ExcelPayrollSetupRepository.ValidateRequest(request)).Message);
    }

    [Fact]
    public void PayTypeSignaturePreservesLegacyDefaultsAndSeparatesRecurringVariableSemantics()
    {
        var component = Component();
        var components = new List<ExcelPayrollSetupRepository.ResolvedComponent> { new(component, "1", false) };
        var legacy = ExcelPayrollSetupRepository.TransferRulesSignature(components);
        component.PayType = "Fixed Pay";
        Assert.Equal(legacy, ExcelPayrollSetupRepository.TransferRulesSignature(components));
        component.PayType = "Variable Pay";
        Assert.NotEqual(legacy, ExcelPayrollSetupRepository.TransferRulesSignature(components));
        component.IsPeriodAdjustment = true;
        var adjustment = ExcelPayrollSetupRepository.TransferRulesSignature(components);
        component.PayType = null;
        Assert.Equal(adjustment, ExcelPayrollSetupRepository.TransferRulesSignature(components));
    }
}
