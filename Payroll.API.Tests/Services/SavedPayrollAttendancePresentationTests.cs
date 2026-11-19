using System.Text.Json;
using System.Text.Json.Nodes;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class SavedPayrollAttendancePresentationTests
{
    private static EssMssRepository.EssPayslipRow Row(decimal paid=26)
    {
        var proof=new SourcePayableAttendanceEvidence(2,17,21,"2026-10",new('a',32),"row-7",new('b',32),new('c',64),26,paid,ReviewedExtraDutyUnits:true);
        return new(){ClientId=17,EmployeeId=21,EmployeeCode="SYNTH21",EmployeeName="Example Person",PayPeriod="2026-10",TotalWorkingDays=31,
            PresentDays=null,PayableDays=paid,GrossPay=2600,NetPay=2600,DetailsJson=JsonSerializer.Serialize(new object[]{
                new{Id="ATTENDANCE_INPUT",Category="Summary",payrollDays=26,payableDays=paid,actualPresentDays=(decimal?)null,
                    sourceType=SourcePayableAttendance.SourceType,sourceEvidence=proof},
                new{Id="BASIC",Name="Basic",Category="Earning",monthlyAmount=2600,amount=2600}})};
    }
    [Theory]
    [InlineData(26,0)][InlineData(25,1)][InlineData(40,0)]
    public void Payslip_uses_frozen_employee_divisor_and_never_mutates_saved_salary_or_presence(decimal paid,decimal unpaid)
    {
        var row=Row(paid);var before=JsonSerializer.Serialize(row);
        Assert.Equal(26,SavedPayrollAttendancePresentation.PayrollDays(row.DetailsJson,row.ClientId,row.EmployeeId,row.PayPeriod,row.PresentDays,row.PayableDays));
        var html=EssMssRepository.BuildPayslipHtml(new Organization(),new(),row,new());
        Assert.Contains("<span>Payroll divisor</span><strong>26</strong>",html);
        Assert.Contains($"<span>Unpaid salary units</span><strong>{unpaid:0.00}</strong>",html);
        Assert.DoesNotContain("<span>LOP</span>",html);Assert.Equal(before,JsonSerializer.Serialize(row));
    }
    [Theory]
    [InlineData("employee")][InlineData("client")][InlineData("month")][InlineData("paid")][InlineData("divisor")][InlineData("duplicate")][InlineData("proof")]
    public void A_source_snapshot_must_match_the_saved_employee_period_and_units(string change)
    {
        var row=Row();var array=JsonNode.Parse(row.DetailsJson)!.AsArray();
        if(change=="employee")row.EmployeeId++;
        if(change=="client")row.ClientId++;
        if(change=="month")row.PayPeriod="2026-09";
        if(change=="paid")row.PayableDays--;
        if(change=="divisor")array[0]!["payrollDays"]=31;
        if(change=="duplicate")array.Add(array[0]!.DeepClone());
        if(change=="proof")array[0]!["sourceEvidence"]=null;
        row.DetailsJson=array.ToJsonString();
        Assert.Throws<InvalidOperationException>(()=>EssMssRepository.BuildPayslipHtml(new(),new(),row,new()));
    }
    [Fact]
    public void Ordinary_existing_payroll_keeps_its_saved_run_day_basis()
    {
        var row=Row();row.PresentDays=26;
        Assert.Null(SavedPayrollAttendancePresentation.PayrollDays(row.DetailsJson,row.ClientId,row.EmployeeId,row.PayPeriod,row.PresentDays,row.PayableDays));
        var html=EssMssRepository.BuildPayslipHtml(new(),new(),row,new());
        Assert.Contains("<span>Working Days</span><strong>31</strong>",html);Assert.Contains("<span>LOP</span><strong>5.00</strong>",html);
        Assert.Null(SavedPayrollAttendancePresentation.PayrollDays("[]",17,21,"2026-10",null,26));
    }
}
