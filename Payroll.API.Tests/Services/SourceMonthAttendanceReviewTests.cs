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
    [InlineData("31","31",false)][InlineData("26","25.5",false)][InlineData("26","40",true)]
    public void Reviewed_day_columns_accept_literal_canonical_numeric_text_without_changing_the_source(string divisor,string payable,bool extra)
    {
        var source=Source();source.Rows[0].Cells[0]!.Value=divisor;source.Rows[0].Cells[0]!.DataType="Text";
        source.Rows[0].Cells[1]!.Value=payable;source.Rows[0].Cells[1]!.DataType="Text";
        var snapshot=JsonSerializer.Serialize(source);
        Assert.Equal((int.Parse(divisor),decimal.Parse(payable,System.Globalization.CultureInfo.InvariantCulture)),SourceMonthAttendanceReview.ReadUnits(source,7,0,1,extra));
        Assert.Equal(snapshot,JsonSerializer.Serialize(source));
        if(extra)Assert.Throws<InvalidOperationException>(()=>SourceMonthAttendanceReview.ReadUnits(source,7,0,1,false));
    }
    [Theory]
    [InlineData("31 days")][InlineData("3.1e1")][InlineData("3,1")][InlineData("31.001")][InlineData("#REF!")][InlineData("-1")]
    public void Text_day_units_keep_noncanonical_values_blocked(string value)
    {
        var source=Source();source.Rows[0].Cells[1]!.Value=value;source.Rows[0].Cells[1]!.DataType="Text";
        Assert.Throws<InvalidOperationException>(()=>SourceMonthAttendanceReview.ReadUnits(source,7,0,1,true));
    }
    [Theory]
    [InlineData("formula")][InlineData("error")][InlineData("missing-cache")][InlineData("financial")]
    public void Numeric_text_is_not_a_fallback_for_unsupported_formula_errors_or_money(string change)
    {
        var source=Source();source.Rows[0].Cells[1]!.Value="31";source.Rows[0].Cells[1]!.DataType="Text";
        if(change=="formula")source.Rows[0].Cells[1]!.FormulaText="=A1";
        if(change=="error")source.Rows[0].Cells[1]!.CalculationError="Unresolved formula";
        if(change=="missing-cache")source.Rows[0].Cells[1]!.MissingCachedValue=true;
        if(change=="financial")source.Columns[1].Kind="earning";
        Assert.Throws<InvalidOperationException>(()=>SourceMonthAttendanceReview.ReadUnits(source,7,0,1,true));
    }
    [Fact]
    public void Numeric_text_day_boundary_is_retrieved_from_structural_headings()
    {
        var columns=new[]{"Name","Total Working Days","Actual working days","Payable Days","Monthly Rate"}
            .Select((header,index)=>new ExcelPayslipMappingColumn{ColumnIndex=index,SourceHeader=header,Kind="info"}).ToList();
        var knowledge=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"FrevoPilot","docs","excel-payroll-mapping.md"));
        var lesson=Assert.Single(ExcelPayslipMappingService.Retrieve(columns,knowledge,[]),reference=>reference.Title.StartsWith("Total Working Days Actual working days Payable Days: literal numeric text"));
        Assert.True(lesson.Content.Length<2600);Assert.Contains("physical presence remains unknown",lesson.Content);
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
