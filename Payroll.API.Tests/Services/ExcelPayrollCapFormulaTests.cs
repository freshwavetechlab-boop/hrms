using System.Globalization;
using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayrollCapFormulaTests
{
    public static IEnumerable<object[]> CapCases()
    {
        foreach (var operation in new[] { ">", ">=", "<", "<=" })
        foreach (var yesIsBase in new[] { true, false })
        foreach (var rate in new[] { "12%", "-12%", "(12%+1%)" })
        {
            var yes = yesIsBase ? "BASIC_EARNED" : "25000";
            var no = yesIsBase ? "25000" : "BASIC_EARNED";
            var maximum = operation.StartsWith('>') == yesIsBase;
            yield return [$"IF(BASIC_EARNED{operation}25000,{yes}*{rate},{no}*{rate})", $"{(maximum ? "MAX" : "MIN")}(BASIC_EARNED,25000)*{rate}"];
        }
    }

    [Theory]
    [MemberData(nameof(CapCases))]
    public void OnlyExactCapBranchesNormalizeAndRetainBoundaryValues(string original, string expected)
    {
        var normalized = ExcelPayrollSetupRepository.NormalizeTransferCapFormula(original);
        Assert.Equal(expected, normalized);
        foreach (var amount in new[] { 0m, 19000m, 25000m, 31000m })
        {
            decimal Resolve(string name) => name == "BASIC_EARNED" ? amount : throw new InvalidOperationException();
            Assert.Equal(PayrollFormulaEvaluator.Evaluate(original, Resolve), PayrollFormulaEvaluator.Evaluate(normalized, Resolve));
        }
    }

    [Fact]
    public void CapturedCapSourceAndUiMinRuleHaveExactProofWithoutChangingCaches()
    {
        foreach (var amount in new[] { 0m, 19000m, 25000m, 31000m })
        {
            var (request, source, row, deduction) = Fixture(amount, "IF(A2>25000,25000*12%,A2*12%)", "MIN(BASIC_EARNED,25000)*12%");
            var before = JsonSerializer.Serialize(new { request, source, row });
            Assert.Equal(deduction.Formula, ExcelPayrollSetupRepository.TranslateTransferFormula(source.Rows[0].Cells[1]!.FormulaText!, request, source, row));
            Assert.Equal("Exact supported translation", ExcelPayrollSetupRepository.InspectTransferFormula(request, source, row, deduction).Status);
            // A retained old native IF rule remains valid; no persisted line/formula must be rewritten.
            deduction.Formula = "IF(BASIC_EARNED>25000,25000*12%,BASIC_EARNED*12%)";
            Assert.Equal("Exact supported translation", ExcelPayrollSetupRepository.InspectTransferFormula(request, source, row, deduction).Status);
            deduction.Formula = "MIN(BASIC_EARNED,25000)*12%";
            Assert.Equal(before, JsonSerializer.Serialize(new { request, source, row }));
        }
    }

    [Fact]
    public void NestedCapsAndParenthesizedCalculatedBasisKeepTheirWholeExpression()
    {
        const string original = "ROUND(IF((BASIC_EARNED+DA_EARNED)>25000,25000*12%,(BASIC_EARNED+DA_EARNED)*12%))+IF(PF_EARNED<10,PF_EARNED*2,10*2)";
        const string expected = "ROUND(MIN(BASIC_EARNED+DA_EARNED,25000)*12%)+MIN(PF_EARNED,10)*2";
        Assert.Equal(expected, ExcelPayrollSetupRepository.NormalizeTransferCapFormula(original));
        decimal Resolve(string name) => name switch { "BASIC_EARNED" => 19000m, "DA_EARNED" => 2000m, "PF_EARNED" => 6m, _ => throw new InvalidOperationException() };
        Assert.Equal(PayrollFormulaEvaluator.Evaluate(original, Resolve), PayrollFormulaEvaluator.Evaluate(expected, Resolve));
    }

    [Theory]
    [InlineData("IF(BASIC_EARNED>25000,25000*13%,BASIC_EARNED*12%)")]
    [InlineData("IF(BASIC_EARNED>25000,30000*12%,BASIC_EARNED*12%)")]
    [InlineData("IF(BASIC_EARNED>25000,25000*12%,HRA_EARNED*12%)")]
    [InlineData("IF(BASIC_EARNED>25000,0,BASIC_EARNED*12%)")]
    [InlineData("IF(BASIC_EARNED=25000,25000*12%,BASIC_EARNED*12%)")]
    [InlineData("IF(BASIC_EARNED<>25000,25000*12%,BASIC_EARNED*12%)")]
    [InlineData("IF(AND(BASIC_EARNED>25000,PAYABLE_DAYS>0),25000*12%,BASIC_EARNED*12%)")]
    public void DifferentBranchOrConditionIsNotApprovedAsAnEquivalentCap(string formula)
    {
        Assert.Equal(formula, ExcelPayrollSetupRepository.NormalizeTransferCapFormula(formula));
    }

    [Theory]
    [InlineData("IF(A2>25000,25000*13%,A2*12%)")]
    [InlineData("IF(A2>25000,30000*12%,A2*12%)")]
    public void SimilarCachedZeroCannotApproveADifferentSourceRule(string formula)
    {
        var (request, source, row, component) = Fixture(0m, formula, "MIN(BASIC_EARNED,25000)*12%");
        Assert.Equal("Blocked", ExcelPayrollSetupRepository.InspectTransferFormula(request, source, row, component).Status);
    }

    [Fact]
    public void NumericIdentityStillCannotBecomeACapSalaryBasis()
    {
        var (request, source, row, component) = Fixture(19000m, "IF(C2>25000,25000*12%,C2*12%)", "MIN(19000,25000)*12%");
        request.ReviewedFixedInputs = true;
        source.ColumnCount = 3;
        source.Columns.Add(new() { ColumnIndex = 2, SourceHeader = "UAN number", Label = "UAN number", Kind = "info" });
        source.Rows[0].Cells.Add(new() { Value = "19000" });
        Assert.Equal("Blocked", ExcelPayrollSetupRepository.InspectTransferFormula(request, source, row, component).Status);
    }

    [Fact]
    public void DistinctSourceRateCellsWithEqualLiteralsDoNotProveASharedCapFactor()
    {
        var (request, source, row, component) = Fixture(19000m, "IF(A2>25000,25000*C2%,A2*D2%)", "MIN(BASIC_EARNED,25000)*12%");
        request.ReviewedFixedInputs = true;
        source.ColumnCount = 4;
        foreach (var index in new[] { 2, 3 })
        {
            source.Columns.Add(new() { ColumnIndex = index, SourceHeader = "Reviewed rate " + index, Label = "Reviewed rate " + index, Kind = "info" });
            source.Rows[0].Cells.Add(new() { Value = "12" });
        }
        var translation = ExcelPayrollSetupRepository.TranslateTransferFormulaWithEvidence(source.Rows[0].Cells[1]!.FormulaText!, request, source, row);
        Assert.StartsWith("IF(", translation.Formula);
        Assert.Equal(new[] { 2, 3 }, translation.Dependencies.Select(d => d.ColumnIndex));
        Assert.Equal("Blocked", ExcelPayrollSetupRepository.InspectTransferFormula(request, source, row, component).Status);
    }

    private static (ExcelPayrollSetupRequest Request, ExcelPayslipCalculationSource Source, ExcelPayslipRow Row, ExcelPayrollSetupComponent Deduction) Fixture(decimal basic, string formula, string native)
    {
        var deduction = new ExcelPayrollSetupComponent { Key = "pf", Code = "PF", Name = "PF", Category = "Deduction", ExpectedColumnIndex = 1, CalculationType = "Formula", ProRata = false, Formula = native };
        var request = new ExcelPayrollSetupRequest { Components = [new() { Key = "basic", Code = "BASIC", Name = "Basic", ExpectedColumnIndex = 0 }, deduction] };
        var source = new ExcelPayslipCalculationSource { HeaderRow = 1, ColumnCount = 2,
            Columns = [new() { ColumnIndex = 0, SourceHeader = "Basic", Label = "Basic", Kind = "earning" }, new() { ColumnIndex = 1, SourceHeader = "PF", Label = "PF", Kind = "deduction" }],
            Rows = [new() { SourceRow = 2, Cells = [new() { Value = basic.ToString(CultureInfo.InvariantCulture) }, new() { Value = (Math.Min(basic, 25000m) * 0.12m).ToString(CultureInfo.InvariantCulture), FormulaText = formula }] }] };
        var row = new ExcelPayslipRow { Id = "synthetic", SourceRow = 2, EmployeeCode = "SYN-1", EmployeeName = "Synthetic employee", NetPay = 0 };
        return (request, source, row, deduction);
    }
}
