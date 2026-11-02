using System.Globalization;
using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayrollSourceBasisTests
{
    private static (ExcelPayrollSetupRequest Request, ExcelPayslipCalculationSource Source, ExcelPayslipRow Row) Fixture()
    {
        var request = new ExcelPayrollSetupRequest {
            PayrollDaysColumnIndex=0, PayableDaysColumnIndex=1, MonthlyCtcColumnIndex=5,
            ReviewedFixedInputs=true, ReviewedExtraDutyUnits=true, ImportAttendance=false,
            Components=[new(){Key="basic",Code="BASIC",SourceColumnIndex=2,ExpectedColumnIndex=6},
                new(){Key="da",Code="DA",SourceColumnIndex=3,ExpectedColumnIndex=7}]
        };
        string[] values=["26","29","10000","500","874.65","11374.65",(10000m/26*29).ToString(CultureInfo.InvariantCulture),(500m/26*29).ToString(CultureInfo.InvariantCulture)];
        var source = new ExcelPayslipCalculationSource {ColumnCount=8,HeaderRow=2,SheetName="Synthetic",
            Columns=Enumerable.Range(0,8).Select(index=>new ExcelPayslipCalculationColumn {ColumnIndex=index,SourceHeader=index==5?"Monthly total":"Input",Kind=index>=6?"earning":"info"}).ToList(),
            Rows=[new(){SourceRow=3,Cells=values.Select(value=>(ExcelPayslipCalculationCell?)new ExcelPayslipCalculationCell{Value=value}).ToList()}]};
        source.Rows[0].Cells[5]!.FormulaText="SUM(C3:E3)";
        source.Rows[0].Cells[6]!.FormulaText="C3/A3*B3"; source.Rows[0].Cells[7]!.FormulaText="D3/A3*B3";
        return(request,source,new(){Id="synthetic",SourceRow=3});
    }

    [Fact] public void ReviewedMonthlyTotalSetsAnnualCtcWithoutAddingBonusToCashEarningsOrCappingDutyUnits()
    {
        var(q,s,row)=Fixture(); var before=JsonSerializer.Serialize(s);
        var result=ExcelPayrollSetupRepository.CalculateRow(42,q,q.Components.Select((c,i)=>new ExcelPayrollSetupRepository.ResolvedComponent(c,(i+1).ToString(),true)).ToList(),s.Rows[0],row,calculationSource:s);
        Assert.Equal(136495.8m,result.Preview.AnnualCtc); Assert.Equal(29,result.Preview.PayableDays);
        Assert.Equal(26,result.Preview.PresentDays); // unused arithmetic fallback, never attendance evidence
        Assert.Equal(11711.54m,result.Preview.Gross); Assert.Equal(2,result.Inputs.Count);
        Assert.Equal(before,JsonSerializer.Serialize(s));
        q.ReviewedExtraDutyUnits=false;
        Assert.Throws<InvalidOperationException>(()=>ExcelPayrollSetupRepository.CalculateRow(42,q,[],s.Rows[0],row));
    }

    [Theory] [InlineData("cache")][InlineData("earned")][InlineData("days")][InlineData("role")][InlineData("review")][InlineData("identity")][InlineData("identity kind")][InlineData("bare identity")]
    public void UnprovenOrEarnedCtcBasisCannotBeAnnualized(string change)
    {
        var(q,s,row)=Fixture();var cell=s.Rows[0].Cells[5]!;
        if(change=="cache")cell.Value="11375.65";
        if(change=="earned"){cell.FormulaText="G3";cell.Value=s.Rows[0].Cells[6]!.Value;}
        if(change=="days"){cell.FormulaText="C3/A3*B3";cell.Value=s.Rows[0].Cells[6]!.Value;}
        if(change=="role")q.SourceColumnRoles[5]="earning";
        if(change=="review")q.ReviewedFixedInputs=false;
        if(change=="identity")s.Columns[5].SourceHeader="Bank Account";
        if(change=="identity kind"){s.Columns[5].Kind="employeeCode";q.SourceColumnRoles[5]="info";}
        if(change=="bare identity")s.Columns[5].SourceHeader="ID NO";
        Assert.Throws<InvalidOperationException>(()=>ExcelPayrollSetupRepository.ReviewedSourceAnnualCtc(q,s,s.Rows[0],row));
    }

    [Fact] public void LegacyRequestHashesOmitUnselectedNewOptions()
    {
        var json=JsonSerializer.Serialize(new ExcelPayrollSetupRequest(),new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("monthlyCtcColumnIndex",json); Assert.DoesNotContain("reviewedExtraDutyUnits",json);
    }
}
