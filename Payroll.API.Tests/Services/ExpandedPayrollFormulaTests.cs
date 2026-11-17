using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class ExpandedPayrollFormulaTests
{
    private static (ExcelPayrollSetupRequest Request, ExcelPayslipCalculationSource Source, ExcelPayslipRow Row, string Raw) Fixture(decimal gross)
    {
        var q = new ExcelPayrollSetupRequest { BatchId = new string('a',32), RowIds = ["source"], TemplateName = "Synthetic expanded source rule",
            EffectiveFrom = "2026-10-01", PayrollDaysColumnIndex = 0, PayableDaysColumnIndex = 1,
            ReviewedFixedInputs = true, ReviewedPolicyScope = true, ImportAttendance = false };
        var source = new ExcelPayslipCalculationSource { ColumnCount = 20, HeaderRow = 1,
            Columns = Enumerable.Range(0,20).Select(i => new ExcelPayslipCalculationColumn { ColumnIndex = i, Kind = "info", SourceHeader = "Input " + i, Label = "Input " + i }).ToList(),
            Rows = [new() { SourceRow = 7, Cells = Enumerable.Range(0,20).Select(_ => (ExcelPayslipCalculationCell?)new ExcelPayslipCalculationCell { Value = "0", DataType = "Number" }).ToList() }] };
        var cells = source.Rows[0].Cells; cells[0]!.Value = cells[1]!.Value = "30";
        var row = new ExcelPayslipRow { Id = "source", SourceRow = 7, EmployeeCode = "SYN-1", EmployeeName = "Anonymous employee" };
        for (var i = 0; i < 8; i++)
        {
            var code = $"ALLOWANCE_INPUT_{i + 1:00}"; var amount = i == 0 ? gross : 0;
            cells[i + 2]!.Value = amount.ToString(CultureInfo.InvariantCulture);
            cells[i + 10]!.Value = amount.ToString(CultureInfo.InvariantCulture);
            cells[i + 10]!.FormulaText = $"{(char)('C'+i)}7/A7*B7";
            source.Columns[i + 10].Kind = "earning"; source.Columns[i + 10].Label = code;
            q.Components.Add(new() { Key = code, Code = code, Name = code, Category = "Earning", ComponentRole = "Regular Earning",
                CalculationType = "Fixed Amount", PayType = "Fixed Pay", Value = "0", SourceColumnIndex = i + 2, ExpectedColumnIndex = i + 10, ProRata = true, Priority = i + 1 });
            row.Earnings.Add(new() { Label = code, Amount = amount });
        }
        source.Columns[18].Kind = "grossTotal"; source.Columns[18].SourceHeader = "Gross control";
        cells[18]!.Value = gross.ToString(CultureInfo.InvariantCulture); cells[18]!.FormulaText = "SUM(K7:R7)";
        const string raw = "IF(S7<=11999,0,IF(AND(S7>=12000,S7<=17999),120,IF(AND(S7>=18000,S7<=29999),180,IF(AND(S7>=30000,S7<=44999),300,450))))";
        var deduction = gross <= 11999 ? 0 : gross is >=12000 and <=17999 ? 120 : gross is >=18000 and <=29999 ? 180 : gross is >=30000 and <=44999 ? 300 : 450;
        cells[19]!.Value = deduction.ToString(CultureInfo.InvariantCulture); cells[19]!.FormulaText = raw;
        source.Columns[19].Kind = "deduction"; source.Columns[19].Label = "Combined source deduction";
        q.Components.Add(new() { Key = "combined", Code = "SOURCE_COMBINED", Name = "Combined source deduction", Category = "Deduction",
            ComponentRole = "Regular Deduction", StatutoryType = "None", CalculationType = "Formula", PayType = "Fixed Pay", Value = "0", ProRata = false, ExpectedColumnIndex = 19, Priority = 20 });
        row.Deductions.Add(new() { Label = "Combined source deduction", Amount = deduction }); row.NetPay = gross - deduction;
        q.Components[^1].Formula = ExcelPayrollSetupRepository.TranslateTransferFormula(raw,q,source,row);
        return (q,source,row,raw);
    }

    [Theory]
    [InlineData("0",0)] [InlineData("11999",0)] [InlineData("11999.5",450)] [InlineData("12000",120)]
    [InlineData("17999.5",450)] [InlineData("18000",180)] [InlineData("30000",300)] [InlineData("44999.5",450)] [InlineData("48000",450)]
    public void RepeatedGrossSourceExpandsPast1000WithUnchangedConditionalAstAndActualCalculator(string text, int deduction)
    {
        var gross = decimal.Parse(text,CultureInfo.InvariantCulture); var (q,source,row,raw) = Fixture(gross);
        var before = JsonSerializer.Serialize(new { q,source,row }); var component = q.Components[^1];
        Assert.InRange(component.Formula.Length,1001,PayrollFormulaEvaluator.MaximumExpressionLength);
        ExcelPayrollSetupRepository.ValidateRequest(q);
        Assert.Equal("Exact supported translation",ExcelPayrollSetupRepository.InspectTransferFormula(q,source,row,component).Status);
        Assert.Equal((decimal)deduction,PayrollFormulaEvaluator.Evaluate(raw,key => key == "S7" ? gross : throw new InvalidOperationException()));
        var result = ExcelPayrollSetupRepository.CalculateRow(12,q,q.Components.Select((c,i) => new ExcelPayrollSetupRepository.ResolvedComponent(c,(i+1).ToString(),false)).ToArray(),source.Rows[0],row,calculationSource:source,retainedAnnualCtc:120000m);
        Assert.Equal(gross,result.Preview.Gross); Assert.Equal((decimal)deduction,result.Preview.Deductions);
        Assert.Equal(gross-deduction,result.Preview.Net);
        Assert.Equal(before,JsonSerializer.Serialize(new { q,source,row }));
    }

    [Theory]
    [InlineData("external")] [InlineData("missing-cache")] [InlineData("error-cache")] [InlineData("changed-control-role")] [InlineData("over-expanded-limit")]
    public void LongerNativeLimitDoesNotAcceptUnboundOrOverLimitSourceRules(string problem)
    {
        var (q,source,row,raw) = Fixture(48000);
        switch(problem)
        {
            case "external": raw = "Other!S7+1"; break;
            case "missing-cache": source.Rows[0].Cells[18]!.MissingCachedValue = true; break;
            case "error-cache": source.Rows[0].Cells[18]!.Error = "#VALUE!"; break;
            case "changed-control-role": source.Columns[18].Kind = "ignore"; break;
            case "over-expanded-limit": raw = "SUM(" + string.Join(',',Enumerable.Repeat("S7",30)) + ")"; break;
        }
        Assert.Throws<InvalidOperationException>(() => ExcelPayrollSetupRepository.TranslateTransferFormula(raw,q,source,row));
    }

    [Fact]
    public void LongRuleUsesActualEarnedComponentsInsteadOfCachedGrossOrMonetaryConstants()
    {
        var (q,source,row,raw) = Fixture(48000); source.Rows[0].Cells[18]!.Value = "999999";
        var translated = ExcelPayrollSetupRepository.TranslateTransferFormulaWithEvidence(raw,q,source,row);
        Assert.Equal(q.Components[^1].Formula,translated.Formula);
        Assert.DoesNotContain("999999",translated.Formula); Assert.DoesNotContain("48000",translated.Formula);
        Assert.All(q.Components.Take(8),c => Assert.Contains(c.Code+"_EARNED",translated.Formula));
        Assert.Equal("SUM(K7:R7)",Assert.Single(translated.Dependencies).Formula);
    }

    [Fact]
    public void ComponentAndPerRowLimitsExtendOnlyFormulaStrings()
    {
        var (q,_,_,_) = Fixture(48000); ExcelPayrollSetupRepository.ValidateRequest(q);
        q.Components[0].Value = new string('1',1001); Assert.Throws<InvalidOperationException>(() => ExcelPayrollSetupRepository.ValidateRequest(q)); q.Components[0].Value = "0";
        q.Components[^1].RowFormulas["source"] = new string('1',4097); Assert.Throws<InvalidOperationException>(() => ExcelPayrollSetupRepository.ValidateRequest(q));
        q.Components[^1].RowFormulas.Clear(); q.Components[^1].Formula = new string('1',4097); Assert.Throws<InvalidOperationException>(() => ExcelPayrollSetupRepository.ValidateRequest(q));
        Assert.Throws<InvalidOperationException>(() => PayrollFormulaEvaluator.Validate(string.Join('+',Enumerable.Repeat("1",513))));
        Assert.Throws<InvalidOperationException>(() => PayrollFormulaEvaluator.Validate(new string('(',65)+"1"+new string(')',65)));
    }

    [Fact]
    public async Task StorageGuardsAreReadOnlyAndKeepAllLegacyShortPathsWithoutAConnection()
    {
        await using var closed = new MySqlConnection();
        await PayrollFormulaStorage.RequireAsync(closed,null,["1+2",new string('1',1000)]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => PayrollFormulaStorage.RequireAsync(closed,null,[new string('1',4097)]));
        Assert.Equal(System.Data.ConnectionState.Closed,closed.State);
        Assert.Equal(new[]{"salarycomponents","salarystructurelines"},PayrollFormulaStorage.MissingCapacity(new Dictionary<string,long>()));
        Assert.Equal(new[]{"salarystructurelines"},PayrollFormulaStorage.MissingCapacity(new Dictionary<string,long>{{"salarycomponents",4096},{"salarystructurelines",1000}}));
        Assert.Empty(PayrollFormulaStorage.MissingCapacity(new Dictionary<string,long>{{"salarycomponents",4096},{"salarystructurelines",4096}}));
    }

    [Fact]
    public void GenericSetupGuardsIncludeComponentAndTemplateLineFormulaPayloads()
    {
        var root=JsonNode.Parse("""{"salaryComponents":[{"Formula":"1+2"}],"salaryStructures":[{"lines":[{"formula":"2+3"}]}],"payslipTemplates":[{"formula":"unrelated"}]}""")!.AsObject();
        Assert.Equal(new[]{"1+2","2+3"},PayrollFormulaStorage.SetupJsonFormulas(root));
        var (q,_,_,_)=Fixture(48000);Assert.Equal(q.Components[^1].Formula.Length,PayrollFormulaStorage.RequiredLength(PayrollFormulaStorage.SetupRequestFormulas(q)));
    }
}
