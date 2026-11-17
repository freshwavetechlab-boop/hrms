using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayrollIsolatedDivisorTests
{
    private static (ExcelPayrollSetupRequest Request,ExcelPayslipCalculationSource Source,List<ExcelPayslipRow> Rows) Fixture()
    {
        var saved=Enumerable.Range(2,3).Select(row=>new ExcelPayslipRow{Id="employee-"+row,SourceRow=row}).ToList();
        var source=new ExcelPayslipCalculationSource{HeaderRow=1,ColumnCount=7,CaptureVersion=1,CompleteSheetCapture=true,
            DependencyReviewStatus="Reviewed",WorkbookKey="independent",WorkbookFiles=[new(){WorkbookKey="independent",OriginalId=new string('a',32),Sha256=new string('b',64)}],
            Columns=Enumerable.Range(0,7).Select(column=>new ExcelPayslipCalculationColumn{ColumnIndex=column,
                SourceHeader=column==2?"Monthly basic":column==3?"Payable days":"",Label=column==6?"Column (G)":"source",
                Kind=column==0?"employeeCode":column==1?"employeeName":column==4?"earning":column is 2 or 3?"info":"ignore"}).ToList(),
            Rows=Enumerable.Range(1,5).Select(row=>new ExcelPayslipCalculationSourceRow{SourceRow=row,Cells=Enumerable.Range(0,7).Select(_=>(ExcelPayslipCalculationCell?)new ExcelPayslipCalculationCell{DataType="Blank"}).ToList()}).ToList()};
        foreach(var row in saved){var cells=source.Rows[row.SourceRow-1].Cells;cells[2]=new(){DataType="Number",Value="1000"};cells[3]=new(){DataType="Number",Value="29"};cells[4]=new(){DataType="Number",Value="966.6666666666667",FormulaText=$"C{row.SourceRow}/$G$3*D{row.SourceRow}"};}
        source.Rows[2].Cells[6]=new(){DataType="Number",Value="30"};
        var request=new ExcelPayrollSetupRequest{FixedPayrollDays=30,PayrollDaysColumnIndex=-1,PayrollDaysCell="G3",PayableDaysColumnIndex=3,ReviewedFixedInputs=true,
            Components=[new(){Code="BASIC",Category="Earning",CalculationType="Fixed Amount",SourceColumnIndex=2,ExpectedColumnIndex=4,ProRata=true}]};
        return(request,source,saved);
    }
    [Theory][InlineData(0)][InlineData(1)][InlineData(2)]
    public void Complete_isolated_constant_is_shared_even_on_its_employee_row_without_source_changes(int selectedIndex)
    {
        var(request,source,rows)=Fixture();var before=JsonSerializer.Serialize(source);var requestBefore=JsonSerializer.Serialize(request);
        ExcelPayrollSetupRepository.ValidateSharedPayrollDaysBinding(request,source,[rows[selectedIndex]],rows);
        var proof=ExcelPayrollSetupRepository.InspectTransferFormula(request,source,rows[selectedIndex],request.Components[0]);
        Assert.Equal("Exact supported translation",proof.Status);Assert.Equal(before,JsonSerializer.Serialize(source));Assert.Equal(requestBefore,JsonSerializer.Serialize(request));
        Assert.Equal("BASIC_MONTHLY/PAYROLL_DAYS*PAYABLE_DAYS",ExcelPayrollSetupRepository.TranslateTransferFormula(source.Rows[rows[selectedIndex].SourceRow-1].Cells[4]!.FormulaText!,request,source,rows[selectedIndex]));
    }
    [Theory]
    [InlineData("legacy")][InlineData("incomplete")][InlineData("unreviewed")][InlineData("not-reviewed-inputs")]
    [InlineData("missing-original")][InlineData("bad-sha")][InlineData("wrong-workbook")][InlineData("missing-physical-row")]
    [InlineData("another-value")][InlineData("another-formula")][InlineData("another-error")][InlineData("another-missing-cache")]
    [InlineData("label")][InlineData("header")][InlineData("source-role")][InlineData("review-role")]
    [InlineData("component-input")][InlineData("component-output")][InlineData("ctc-input")][InlineData("location-input")]
    [InlineData("relative-selected")][InlineData("relative-unselected")][InlineData("wrong-payable")][InlineData("multiplied-constant")]
    [InlineData("formula-constant")][InlineData("fractional-constant")][InlineData("text-constant")]
    public void Employee_row_exception_rejects_nonisolated_unowned_or_semantically_different_cells(string change)
    {
        var(request,source,rows)=Fixture();
        switch(change)
        {
            case "legacy":source.CaptureVersion=0;break;case "incomplete":source.CompleteSheetCapture=false;break;
            case "unreviewed":source.DependencyReviewStatus="Pending";break;case "not-reviewed-inputs":request.ReviewedFixedInputs=false;break;
            case "missing-original":source.WorkbookFiles[0].OriginalId="";break;case "bad-sha":source.WorkbookFiles[0].Sha256="bad";break;
            case "wrong-workbook":source.WorkbookFiles[0].WorkbookKey="elsewhere";break;case "missing-physical-row":source.Rows.RemoveAt(4);source.Rows[3].SourceRow=5;break;
            case "another-value":source.Rows[4].Cells[6]=new(){Value="30",DataType="Number"};break;
            case "another-formula":source.Rows[4].Cells[6]!.FormulaText="0";break;case "another-error":source.Rows[4].Cells[6]!.Error="#VALUE!";break;
            case "another-missing-cache":source.Rows[4].Cells[6]!.MissingCachedValue=true;break;
            case "label":source.Columns[6].Label="Another employee's day total";break;case "header":source.Columns[6].SourceHeader="Working days";break;
            case "source-role":source.Columns[6].Kind="info";break;case "review-role":request.SourceColumnRoles[6]="earning";break;
            case "component-input":request.Components[0].SourceColumnIndex=6;break;case "component-output":request.Components[0].ExpectedColumnIndex=6;break;
            case "ctc-input":request.MonthlyCtcColumnIndex=6;break;case "location-input":request.SourceWorkLocationColumnIndex=6;break;
            case "relative-selected":source.Rows[1].Cells[4]!.FormulaText="C2/G3*D2";break;
            case "relative-unselected":source.Rows[3].Cells[4]!.FormulaText="C4/G3*D4";break;
            case "wrong-payable":source.Rows[1].Cells[4]!.FormulaText="C2/$G$3*D4";break;
            case "multiplied-constant":source.Rows[1].Cells[4]!.FormulaText="C2*$G$3*D2";break;
            case "formula-constant":source.Rows[2].Cells[6]!.FormulaText="15*2";break;
            case "fractional-constant":source.Rows[2].Cells[6]!.Value="30.5";break;case "text-constant":source.Rows[2].Cells[6]!.DataType="Text";break;
        }
        var error=Assert.Throws<InvalidOperationException>(()=>ExcelPayrollSetupRepository.ValidateSharedPayrollDaysBinding(request,source,[rows[0]],rows));
        Assert.Contains("captured shared divisor",error.Message);
    }
    [Fact]
    public void Missing_or_subset_saved_batch_witness_cannot_authorize_employee_row_constant()
    {
        var(request,source,rows)=Fixture();
        Assert.Throws<InvalidOperationException>(()=>ExcelPayrollSetupRepository.ValidateSharedPayrollDaysBinding(request,source,[rows[0]]));
        Assert.Throws<InvalidOperationException>(()=>ExcelPayrollSetupRepository.ValidateSharedPayrollDaysBinding(request,source,[rows[0]],[]));
        Assert.Throws<InvalidOperationException>(()=>ExcelPayrollSetupRepository.ValidateSharedPayrollDaysBinding(request,source,[rows[0]],[rows[1]]));
    }
    [Fact]
    public void Same_row_constant_substitution_does_not_repair_changed_literal_or_rules()
    {
        var(request,source,rows)=Fixture();source.Rows[2].Cells[6]!.Value="29";
        Assert.Throws<InvalidOperationException>(()=>ExcelPayrollSetupRepository.TranslateTransferFormula("C3/$G$3*D3",request,source,rows[1]));
        source.Rows[2].Cells[6]!.Value="30";request.Components[0].CalculationType="Formula";request.Components[0].SourceColumnIndex=null;request.Components[0].ProRata=false;request.Components[0].Formula="1000/PAYROLL_DAYS*PAYABLE_DAYS+1";
        Assert.Equal("Blocked",ExcelPayrollSetupRepository.InspectTransferFormula(request,source,rows[1],request.Components[0]).Status);
    }
}
