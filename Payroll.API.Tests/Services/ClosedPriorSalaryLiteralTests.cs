using System.Globalization;
using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Xunit;

namespace Payroll.API.Tests.Services;

public sealed class ClosedPriorSalaryLiteralTests
{
    private static (ExcelPayrollSetupRequest Request, ExcelPayslipCalculationSource Source, ExcelPayslipRow Row) Fixture()
    {
        var source = new ExcelPayslipCalculationSource
        {
            HeaderRow = 1, ColumnCount = 6,
            Columns = new[] { "Current CTC", "Last Month Salary", "Payroll days", "Payable days", "Bonus", "Basic" }
                .Select((header, column) => new ExcelPayslipCalculationColumn
                {
                    ColumnIndex = column, SourceHeader = header, Label = header,
                    Kind = column >= 4 ? "earning" : "info"
                }).ToList(),
            Rows = [new() { SourceRow = 8, Cells = [
                new() { DataType = "Number", Value = "42000" },
                new() { DataType = "Number", Value = "21000" },
                new() { DataType = "Number", Value = "31" },
                new() { DataType = "Number", Value = "21" },
                new() { DataType = "Number", Value = "200" },
                new() { DataType = "Number", Value = "14025.81", FormulaText = "B8/C8*D8-E8" }
            ] }]
        };
        var request = new ExcelPayrollSetupRequest
        {
            BatchId = new string('a', 32), RowIds = ["synthetic-closed-source"],
            TemplateName = "Reviewed closed-month prior salary", EffectiveFrom = "2026-10-01",
            MonthlyCtcColumnIndex = 0, PayrollDaysColumnIndex = 2, PayableDaysColumnIndex = 3,
            PresentDaysColumnIndex = 3, ReviewedFixedInputs = true, ReviewedPolicyScope = true,
            SourceColumnRoles = new() { [0] = "info", [1] = "info", [2] = "info", [3] = "info", [4] = "earning", [5] = "earning" },
            Components = [
                new() { Key = "basic", ExistingComponentId = "101", Code = "BASIC", Name = "Basic", Category = "Earning",
                    CalculationType = "Formula", Value = "0", ProRata = false, ExpectedColumnIndex = 5, Priority = 10 },
                new() { Key = "bonus", ExistingComponentId = "104", Code = "STAT_BONUS", Name = "Bonus", Category = "Earning",
                    CalculationType = "Fixed Amount", Value = "0", ProRata = false, SourceColumnIndex = 4, ExpectedColumnIndex = 4, Priority = 40 }
            ]
        };
        var row = new ExcelPayslipRow
        {
            Id = "synthetic-closed-source", SourceRow = 8, EmployeeCode = "SYN01", EmployeeName = "Synthetic Example",
            Earnings = [new() { Label = "Basic", Amount = 14025.81m }, new() { Label = "Bonus", Amount = 200m }],
            NetPay = 14225.81m
        };
        request.Components[0].Formula = ExcelPayrollSetupRepository.TranslateTransferFormula(
            source.Rows[0].Cells[5]!.FormulaText!, request, source, row);
        return (request, source, row);
    }

    [Fact]
    public void Closed_source_literal_retains_prior_base_without_aliasing_current_CTC_or_overriding_basic()
    {
        var (request, source, row) = Fixture();
        var original = JsonSerializer.Serialize(source);
        var translation = ExcelPayrollSetupRepository.TranslateTransferFormulaWithEvidence(
            source.Rows[0].Cells[5]!.FormulaText!, request, source, row);
        Assert.Equal("21000/PAYROLL_DAYS*PAYABLE_DAYS-STAT_BONUS_EARNED", translation.Formula);
        var dependency = Assert.Single(translation.Dependencies);
        Assert.Equal(1, dependency.ColumnIndex);
        Assert.Equal("Last Month Salary", dependency.Header);
        Assert.Equal("21000", dependency.Literal);
        Assert.DoesNotContain("CTC", translation.Formula);

        var components = request.Components.Select(component =>
            new ExcelPayrollSetupRepository.ResolvedComponent(component, component.ExistingComponentId!, false)).ToList();
        var result = ExcelPayrollSetupRepository.CalculateRow(1, request, components, source.Rows[0], row,
            calculationSource: source, retainedAnnualCtc: 504000m);
        Assert.DoesNotContain(result.Issues, issue => issue.Severity == "Error");
        Assert.Equal(504000m, result.Preview.AnnualCtc);
        Assert.Equal(14225.81m, result.Preview.Gross);
        Assert.Equal(14225.81m, result.Preview.Net);
        Assert.Equal(200m, Assert.Single(result.Inputs).Value);
        Assert.False(result.Inputs.ContainsKey("101"));
        Assert.Equal(original, JsonSerializer.Serialize(source));

        // A different independent current budget does not change prior-base pay.
        source.Rows[0].Cells[0]!.Value = "51000";
        var changedBudget = ExcelPayrollSetupRepository.CalculateRow(1, request, components, source.Rows[0], row,
            calculationSource: source, retainedAnnualCtc: 612000m);
        Assert.Equal(612000m, changedBudget.Preview.AnnualCtc);
        Assert.Equal(result.Preview.Gross, changedBudget.Preview.Gross);
        Assert.Equal(result.Preview.Net, changedBudget.Preview.Net);
    }

    [Fact]
    public void Changed_prior_literal_requires_fresh_rules_instead_of_reusing_equal_current_CTC()
    {
        var (request, source, row) = Fixture();
        var basic = request.Components[0];
        Assert.Equal("Exact supported translation", ExcelPayrollSetupRepository.InspectTransferFormula(request, source, row, basic).Status);
        source.Rows[0].Cells[1]!.Value = "23000";
        Assert.Equal("Blocked", ExcelPayrollSetupRepository.InspectTransferFormula(request, source, row, basic).Status);
        var changed = ExcelPayrollSetupRepository.TranslateTransferFormula(source.Rows[0].Cells[5]!.FormulaText!, request, source, row);
        Assert.StartsWith("23000/", changed);
        Assert.NotEqual(basic.Formula, changed);
    }

    [Theory]
    [InlineData("blank")]
    [InlineData("text")]
    [InlineData("error")]
    [InlineData("missing-cache")]
    public void Unproved_prior_salary_is_never_replaced_by_current_budget(string problem)
    {
        var (request, source, row) = Fixture();
        var prior = source.Rows[0].Cells[1]!;
        if (problem == "blank") prior.Value = "";
        if (problem == "text") prior.Value = "not supplied";
        if (problem == "error") prior.Error = "#VALUE!";
        if (problem == "missing-cache") prior.MissingCachedValue = true;
        Assert.Equal("Blocked", ExcelPayrollSetupRepository.InspectTransferFormula(request, source, row, request.Components[0]).Status);
    }
}
