using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class SourceMonthAttendanceReviewTests
{
    private static SourceMonthAttendanceRequest Request()=>new(){ClientId=9,PayPeriod="2026-10",SourceBatchId=new('a',32),DivisorColumn=0,PayableColumn=1,
        Reason="Reviewed final source salary units",Rows=[new(71,"row-7")]};
    private static ExcelPayslipCalculationSource Source()=>new(){ColumnCount=2,Columns=[new(){ColumnIndex=0,Kind="info"},new(){ColumnIndex=1,Kind="info"}],
        Rows=[new(){SourceRow=7,Cells=[new(){Value="26",DataType="Number"},new(){Value="40",DataType="Number"}]}]};
    [Fact]
    public void Extra_units_are_explicit_and_source_cells_are_not_changed_or_promoted_to_presence()
    {
        var source=Source();var before=JsonSerializer.Serialize(source);
        Assert.Throws<InvalidOperationException>(()=>SourceMonthAttendanceReview.ReadUnits(source,7,0,1,false));
        Assert.Equal((26,40m),SourceMonthAttendanceReview.ReadUnits(source,7,0,1,true));
        Assert.Equal(before,JsonSerializer.Serialize(source));
    }
    [Theory]
    [InlineData("blank")][InlineData("error")][InlineData("money")][InlineData("duplicate-row")][InlineData("fractional-divisor")][InlineData("precision")]
    public void Unsupported_or_ambiguous_source_values_cannot_be_seeded(string invalid)
    {
        var source=Source();
        if(invalid=="blank")source.Rows[0].Cells[1]!.Value="";
        if(invalid=="error")source.Rows[0].Cells[1]!.Error="#REF!";
        if(invalid=="money")source.Columns[1].Kind="earning";
        if(invalid=="duplicate-row")source.Rows.Add(source.Rows[0]);
        if(invalid=="fractional-divisor")source.Rows[0].Cells[0]!.Value="26.5";
        if(invalid=="precision")source.Rows[0].Cells[1]!.Value="40.001";
        Assert.Throws<InvalidOperationException>(()=>SourceMonthAttendanceReview.ReadUnits(source,7,0,1,true));
    }
    [Fact]
    public void Scope_version_completed_month_and_unique_employee_rows_are_required()
    {
        var request=Request();SourceMonthAttendanceReview.Validate(request,new(2026,11,20));
        Assert.Throws<InvalidOperationException>(()=>SourceMonthAttendanceReview.Validate(request,new(2026,11,20),true));
        request.SourceHash=new('a',64);SourceMonthAttendanceReview.Validate(request,new(2026,11,20),true);
        request.PayPeriod="2026-11";Assert.Throws<InvalidOperationException>(()=>SourceMonthAttendanceReview.Validate(request,new(2026,11,20)));
        request=Request();request.Rows.Add(new(71,"row-8"));Assert.Throws<InvalidOperationException>(()=>SourceMonthAttendanceReview.Validate(request,new(2026,11,20)));
        request=Request();request.DivisorColumn=request.PayableColumn;Assert.Throws<InvalidOperationException>(()=>SourceMonthAttendanceReview.Validate(request,new(2026,11,20)));
    }
}
