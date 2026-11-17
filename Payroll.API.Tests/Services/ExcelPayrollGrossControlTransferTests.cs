using System.Globalization;
using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayrollGrossControlTransferTests
{
    [Theory]
    [InlineData(9000, 0, 0)]
    [InlineData(9000, 1000, 5)]
    [InlineData(19000, 1000, 10)]
    public void GrossControlExpandsItsCapturedSumWithoutCreatingAnEarningOrFreezingItsCache(decimal basic, decimal hra, decimal fee)
    {
        var (request, source, row, component) = Fixture(basic, hra);
        var before = JsonSerializer.Serialize(new { request, source, row });
        const string sum = "((BASIC_EARNED+HRA_EARNED))";
        var expected = $"IF({sum}<=9999,0,IF(AND({sum}>=10000,{sum}<=19999),5,10))";
        var proof = ExcelPayrollSetupRepository.TranslateTransferFormulaWithEvidence(source.Rows[0].Cells[3]!.FormulaText!, request, source, row);
        Assert.Equal(expected, proof.Formula);
        Assert.Equal(fee, PayrollFormulaEvaluator.Evaluate(proof.Formula, name => name switch { "BASIC_EARNED" => basic, "HRA_EARNED" => hra, _ => throw new InvalidOperationException() }));
        var dependency = Assert.Single(proof.Dependencies);
        Assert.Equal("grossTotal", dependency.Kind); Assert.Equal("SUM(A2:B2)", dependency.Formula); Assert.Null(dependency.Literal);
        component.Formula = expected;
        Assert.Equal("Exact supported translation", ExcelPayrollSetupRepository.InspectTransferFormula(request, source, row, component).Status);
        component.Formula = "";
        Assert.Equal(before, JsonSerializer.Serialize(new { request, source, row }));
        Assert.DoesNotContain(request.Components, c => c.ExpectedColumnIndex == 2);
    }

    [Theory]
    [InlineData("literal")]
    [InlineData("error")]
    [InlineData("missing-cache")]
    [InlineData("bad-cache")]
    [InlineData("cycle")]
    [InlineData("shared-row")]
    [InlineData("external")]
    [InlineData("role-change")]
    [InlineData("unsupported")]
    public void UnprovenGrossControlNeverBecomesAConstant(string problem)
    {
        var (request, source, row, component) = Fixture(9000, 1000);
        var gross = source.Rows[0].Cells[2]!;
        switch (problem)
        {
            case "literal": gross.FormulaText = null; break;
            case "error": gross.Error = "#REF!"; break;
            case "missing-cache": gross.MissingCachedValue = true; break;
            case "bad-cache": gross.Value = "not numeric"; break;
            case "cycle": gross.FormulaText = "C2+A2"; break;
            case "shared-row": gross.FormulaText = "SUM(A3:B3)"; break;
            case "external": gross.FormulaText = "[1]Other!A2"; break;
            case "role-change": request.SourceColumnRoles[2] = "info"; break;
            case "unsupported": gross.FormulaText = "UNKNOWN(A2)"; break;
        }
        Assert.Throws<InvalidOperationException>(() => ExcelPayrollSetupRepository.TranslateTransferFormula(source.Rows[0].Cells[3]!.FormulaText!, request, source, row));
        Assert.Equal("Blocked", ExcelPayrollSetupRepository.InspectTransferFormula(request, source, row, component).Status);
    }

    [Fact]
    public void GrossControlCacheIsPreservedAndNeverUsedAsTheNativeFormulaBasis()
    {
        var (request, source, row, _) = Fixture(9000, 1000);
        source.Rows[0].Cells[2]!.Value = "999999";
        var proof = ExcelPayrollSetupRepository.TranslateTransferFormulaWithEvidence(source.Rows[0].Cells[3]!.FormulaText!, request, source, row);
        Assert.DoesNotContain("999999", proof.Formula);
        Assert.Equal("999999", source.Rows[0].Cells[2]!.Value);
        // Structural translation does not approve this inconsistent cache or its amounts.
    }

    private static (ExcelPayrollSetupRequest, ExcelPayslipCalculationSource, ExcelPayslipRow, ExcelPayrollSetupComponent) Fixture(decimal basic, decimal hra)
    {
        var fee = new ExcelPayrollSetupComponent { Key = "fee", Code = "SOURCE_FEE", Name = "Source fee", Category = "Deduction", ExpectedColumnIndex = 3, CalculationType = "Formula", ProRata = false };
        var request = new ExcelPayrollSetupRequest { Components = [new() { Key = "basic", Code = "BASIC", ExpectedColumnIndex = 0 }, new() { Key = "hra", Code = "HRA", ExpectedColumnIndex = 1 }, fee] };
        var source = new ExcelPayslipCalculationSource { HeaderRow = 1, ColumnCount = 4,
            Columns = [new() { ColumnIndex = 0, SourceHeader = "Basic", Kind = "earning" }, new() { ColumnIndex = 1, SourceHeader = "HRA", Kind = "earning" },
                new() { ColumnIndex = 2, SourceHeader = "Gross total", Kind = "grossTotal" }, new() { ColumnIndex = 3, SourceHeader = "Fee", Kind = "deduction" }],
            Rows = [new() { SourceRow = 2, Cells = [new() { Value = basic.ToString(CultureInfo.InvariantCulture) }, new() { Value = hra.ToString(CultureInfo.InvariantCulture) },
                new() { Value = (basic + hra).ToString(CultureInfo.InvariantCulture), FormulaText = "SUM(A2:B2)" },
                new() { Value = "5", FormulaText = "IF(C2<=9999,0,IF(AND(C2>=10000,C2<=19999),5,10))" }] }] };
        return (request, source, new ExcelPayslipRow { Id = "synthetic", SourceRow = 2, EmployeeCode = "SYN-ONLY", EmployeeName = "Synthetic employee" }, fee);
    }
}
