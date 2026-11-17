using System.Globalization;
using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayrollNumericSumTransferTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(10000, 500)]
    [InlineData(12000, 600)]
    [InlineData(20000, 600)]
    public void SingleCellSumInsideCapturedCapRetainsItsExactNumericAst(decimal basic, decimal deduction)
    {
        const string original = "ROUND(IF(SUM(A2)>=12000,600,SUM(A2)*5%),0)";
        const string native = "ROUND(IF(SUM(BASIC_EARNED)>=12000,600,SUM(BASIC_EARNED)*5%),0)";
        var (request, source, row, component) = Fixture(basic, deduction, original, native);
        var before = JsonSerializer.Serialize(new { request, source, row });
        var translated = ExcelPayrollSetupRepository.TranslateTransferFormula(original, request, source, row);
        Assert.Equal(PayrollFormulaEvaluator.CanonicalStructure(native), PayrollFormulaEvaluator.CanonicalStructure(translated));
        Assert.Equal(deduction, PayrollFormulaEvaluator.Evaluate(translated, name => name == "BASIC_EARNED" ? basic : throw new InvalidOperationException()));
        Assert.Equal("Exact supported translation", ExcelPayrollSetupRepository.InspectTransferFormula(request, source, row, component).Status);
        Assert.Equal(before, JsonSerializer.Serialize(new { request, source, row }));
    }

    [Fact]
    public void NumericSumArgumentsRemainSeparateExpressionsWithoutCachedOutputSubstitution()
    {
        var (request, source, row, component) = Fixture(10000, 502,
            "ROUND(SUM(A2*5%,1,ABS(-1)),0)", "ROUND(SUM(BASIC_EARNED*5%,1,ABS(-1)),0)");
        source.Rows[0].Cells[1]!.Value = "999999";
        var translated = ExcelPayrollSetupRepository.TranslateTransferFormula(source.Rows[0].Cells[1]!.FormulaText!, request, source, row);
        Assert.Equal(502, PayrollFormulaEvaluator.Evaluate(translated, _ => 10000));
        Assert.Equal("Exact supported translation", ExcelPayrollSetupRepository.InspectTransferFormula(request, source, row, component).Status);
        Assert.Equal("999999", source.Rows[0].Cells[1]!.Value);
    }

    [Theory]
    [InlineData("ROUND(IF(BASIC_EARNED>=12000,600,BASIC_EARNED*5%),0)")]
    [InlineData("ROUND(IF(SUM(BASIC_EARNED)>=12000,700,SUM(BASIC_EARNED)*5%),0)")]
    public void EqualCurrentValuesCannotApproveFlattenedOrChangedRules(string native)
    {
        var (request, source, row, component) = Fixture(10000, 500,
            "ROUND(IF(SUM(A2)>=12000,600,SUM(A2)*5%),0)", native);
        Assert.Equal(500, PayrollFormulaEvaluator.Evaluate(native, _ => 10000));
        Assert.Equal("Blocked", ExcelPayrollSetupRepository.InspectTransferFormula(request, source, row, component).Status);
    }

    [Theory]
    [InlineData("SUM(A3)")]
    [InlineData("SUM(A2:A3)")]
    [InlineData("SUM([1]Sheet!A2)")]
    [InlineData("SUM(\"10000\")")]
    [InlineData("SUM(UNSUPPORTED(A2))")]
    public void SumDoesNotAuthorizeSharedExternalTextOrUnsupportedDependencies(string formula)
    {
        var (request, source, row, component) = Fixture(10000, 500, formula, "SUM(BASIC_EARNED)");
        Assert.Throws<InvalidOperationException>(() => ExcelPayrollSetupRepository.TranslateTransferFormula(formula, request, source, row));
        Assert.Equal("Blocked", ExcelPayrollSetupRepository.InspectTransferFormula(request, source, row, component).Status);
    }

    [Fact]
    public void SumDoesNotTurnAnIdentifierIntoAReviewedNumericSalaryInput()
    {
        var (request, source, row, component) = Fixture(10000, 500, "SUM(C2)*5%", "SUM(10000)*5%");
        request.ReviewedFixedInputs = true;
        source.ColumnCount = 3;
        source.Columns.Add(new() { ColumnIndex = 2, SourceHeader = "UAN Number", Kind = "info" });
        source.Rows[0].Cells.Add(new() { Value = "10000" });
        Assert.Equal("Blocked", ExcelPayrollSetupRepository.InspectTransferFormula(request, source, row, component).Status);
    }

    private static (ExcelPayrollSetupRequest, ExcelPayslipCalculationSource, ExcelPayslipRow, ExcelPayrollSetupComponent) Fixture(
        decimal basic, decimal deduction, string original, string native)
    {
        var component = new ExcelPayrollSetupComponent { Key = "deduction", Code = "SOURCE_DEDUCTION", Name = "Source deduction",
            Category = "Deduction", CalculationType = "Formula", ExpectedColumnIndex = 1, ProRata = false, Formula = native };
        var request = new ExcelPayrollSetupRequest { Components = [new() { Key = "basic", Code = "BASIC", ExpectedColumnIndex = 0 }, component] };
        var source = new ExcelPayslipCalculationSource { HeaderRow = 1, ColumnCount = 2,
            Columns = [new() { ColumnIndex = 0, SourceHeader = "Basic", Kind = "earning" }, new() { ColumnIndex = 1, SourceHeader = "Deduction", Kind = "deduction" }],
            Rows = [new() { SourceRow = 2, Cells = [new() { Value = basic.ToString(CultureInfo.InvariantCulture) },
                new() { Value = deduction.ToString(CultureInfo.InvariantCulture), FormulaText = original }] }] };
        return (request, source, new ExcelPayslipRow { Id = "synthetic", SourceRow = 2, EmployeeCode = "SYN-ONLY", EmployeeName = "Synthetic employee" }, component);
    }
}
