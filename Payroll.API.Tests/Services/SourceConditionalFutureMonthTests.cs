using Payroll.API.Models;
using Payroll.API.Repositories;
using Xunit;

namespace Payroll.API.Tests.Services;

// Source-defined arithmetic only; no production future payroll or statutory-policy assertion.
public sealed class SourceConditionalFutureMonthTests
{
    [Theory]
    [InlineData(20000,30,15,10000,833,150)]
    [InlineData(30000,30,10,10000,0,0)]
    [InlineData(20000,31,31,20000,1666,150)]
    [InlineData(30000,31,0,0,0,0)]
    public void Native_rules_use_new_month_attendance_and_correct_monthly_versus_earned_basis(decimal basic,int divisor,decimal payable,decimal earned,decimal bonus,decimal esi)
    {
        var components=new List<PayrollReviewComponent>{
            new("1","BASIC","Earning","Regular Earning","None","Fixed Amount","0","","",true,true,true,10,"Fixed Pay"),
            new("2","STAT_BONUS","Earning","Regular Earning","None","Formula","0","IF(BASIC_MONTHLY<=21000,ROUNDUP(BASIC_EARNED*8.33%,0),0)","",false,true,true,20,"Fixed Pay"),
            new("3","ESIC","Deduction","Statutory Deduction","None","Formula","0","IF(BASIC_MONTHLY<=21000,BASIC_MONTHLY*0.75%,0)","",false,false,true,30,"Variable Pay")};
        var lines=components.Select(c=>new PayrollReviewLine(c.Id,"0",c.Formula,c.CalculationType,"",c.ProRata?"true":"false")).ToList();
        var result=PayRunRepository.SimulateTemplate(new(){Mode="Template",ClientId=7,TemplateId="source-test",PayrollDays=divisor,
            PresentDays=payable,PayableDays=payable,AnnualCtc=basic*12,AmountOverrides=new(){["1"]=basic},Snapshot=new("Template",7,"",basic*12,components,lines)});
        Assert.True(result.Complete,string.Join("; ",result.Issues.Select(i=>i.Message)));
        Assert.Equal(earned,result.Lines.Single(l=>l.Code=="BASIC").Earned);
        Assert.Equal(bonus,result.Lines.Single(l=>l.Code=="STAT_BONUS").Earned);
        Assert.Equal(esi,result.Lines.Single(l=>l.Code=="ESIC").Earned);
        Assert.DoesNotContain(result.Lines,l=>l.Code=="TA_DA");
    }
}
