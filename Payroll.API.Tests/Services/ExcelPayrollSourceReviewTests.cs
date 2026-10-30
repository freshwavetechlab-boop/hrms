using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public class ExcelPayrollSourceReviewTests
{
    [Fact]
    public void Selected_source_scope_survives_empty_arithmetic_results()
    {
        var batch = new ExcelPayslipBatch { Rows = [new() { Id = "selected", SourceRow = 4 }, new() { Id = "outside", SourceRow = 5 }] };
        var rows = ExcelPayrollSetupRepository.DescribeSelectedSources(batch, new() { RowIds = ["selected"] });
        var row = Assert.Single(rows);
        Assert.Equal("selected", row.RowId); Assert.Equal(4, row.SourceRow); Assert.True(row.SourceFound);
        var preview = new ExcelPayrollSetupPreview { SelectedSourceRows = rows };
        Assert.Empty(preview.Rows); Assert.Single(preview.SelectedSourceRows);
    }

    [Fact]
    public void Missing_source_is_retained_as_an_unresolved_selection()
    {
        var row = Assert.Single(ExcelPayrollSetupRepository.DescribeSelectedSources(new(), new() { RowIds = ["missing"] }));
        Assert.False(row.SourceFound); Assert.Null(row.SourceRow);
    }

    [Fact]
    public void Repeated_saved_row_identity_is_not_certified()
    {
        var batch = new ExcelPayslipBatch { Rows = [new() { Id = "same", SourceRow = 4 }, new() { Id = "same", SourceRow = 5 }] };
        var row = Assert.Single(ExcelPayrollSetupRepository.DescribeSelectedSources(batch, new() { RowIds = ["same"] }));
        Assert.False(row.SourceFound); Assert.Null(row.SourceRow);
    }

    [Theory]
    [InlineData("0", "0")]
    [InlineData("30", "35000")]
    public void Missing_monthly_basis_never_uses_cached_earned_output_as_a_rate(string payable, string cachedEarned)
    {
        var request = new ExcelPayrollSetupRequest { PayrollDaysColumnIndex = 0, PayableDaysColumnIndex = 1,
            Components = [new() { Key="basic",Code="BASIC",Name="Basic",CalculationType="Formula",Formula="ROUND(35000*PAYABLE_DAYS/PAYROLL_DAYS)",ProRata=false,ExpectedColumnIndex=2 }] };
        var resolved = request.Components.Select(component => new ExcelPayrollSetupRepository.ResolvedComponent(component,"synthetic-basic",true)).ToList();
        var source = new ExcelPayslipCalculationSourceRow { SourceRow=4,Cells=new[]{"30",payable,cachedEarned}.Select(value=>(ExcelPayslipCalculationCell?)new ExcelPayslipCalculationCell {Value=value}).ToList() };
        var error=Assert.Throws<InvalidOperationException>(()=>ExcelPayrollSetupRepository.CalculateRow(1,request,resolved,source,new ExcelPayslipRow{Id="selected"}));
        Assert.Contains("Source input column",error.Message);
        Assert.Contains("not a monthly salary input",error.Message);
        Assert.Null(request.Components[0].SourceColumnIndex);
        Assert.Equal(cachedEarned,source.Cells[2]!.Value);
    }
}
