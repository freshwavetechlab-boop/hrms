using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayrollRoundingEvidenceTests
{
    private static PayrollSimulationResult Calculate(string esic = "BASIC_EARNED*0.75%")
    {
        List<PayrollReviewComponent> components = [
            new("basic","BASIC","Earning","Regular Earning","None","Fixed Amount","134","","",true,true,true,1,"Fixed Pay"),
            new("pf","PF","Deduction","Employee Deduction","None","Formula","0","BASIC_EARNED*12%","",false,false,true,2,"Fixed Pay"),
            new("esi","ESIC","Deduction","Employee Deduction","None","Formula","0",esic,"",false,false,true,3,"Fixed Pay"),
            new("epf","PF_ER","Benefit","Employer Contribution","None","Formula","0","BASIC_EARNED*13%","",false,false,true,4,"Fixed Pay"),
            new("eresic","ESIC_ER","Benefit","Employer Contribution","None","Formula","0","BASIC_EARNED*3.25%","",false,false,true,5,"Fixed Pay")
        ];
        return PayRunRepository.SimulateTemplate(new() {
            Mode="Template", ClientId=42, TemplateId="synthetic-rounding", AnnualCtc=1608,
            PayrollDays=30,PresentDays=30,PayableDays=30,
            Snapshot=new("Template",42,"",1608,components,components.Select(c=>new PayrollReviewLine(c.Id,c.Value,c.Formula,c.CalculationType,c.BaseComponent,c.ProRata.ToString().ToLowerInvariant())).ToList())
        });
    }
    private static ExcelPayslipRow Source() => new() {
        Id="synthetic",SourceRow=3,NetPay=116.91499999999999m,
        Earnings=[new(){Label="Basic",Amount=133.99999999999997m}],
        Deductions=[new(){Label="PF",Amount=16.079999999999995m},new(){Label="ESIC",Amount=1.0049999999999997m}],
        EmployerContributions=[new(){Label="Employer PF",Amount=17.42m},new(){Label="Employer ESIC",Amount=4.355m}]
    };

    [Fact] public void ActualCoreRoundsComponentsBeforeSummingNet()
    {
        var result=Calculate();Assert.True(result.Complete);
        var esi=Assert.Single(result.Lines,l=>l.Code=="ESIC");Assert.Equal(1.005m,esi.Raw);Assert.Equal(1m,esi.Earned);
        Assert.Contains("midpoint-to-even",esi.Rounding);Assert.Equal(134m,result.Gross);Assert.Equal(17.08m,result.Deductions);Assert.Equal(116.92m,result.Net);Assert.Equal(21.78m,result.Benefits);
    }
    [Fact] public void SamePrintedNetDoesNotCertifyRawOrComponentParity()
    {
        var source=Source();var result=Calculate();Assert.True(result.Complete);
        Assert.Equal("116.92",ExcelPayslipPdfService.Amount(source.NetPay,2));Assert.Equal(ExcelPayslipPdfService.Amount(source.NetPay,2),ExcelPayslipPdfService.Amount(result.Net!.Value,2));
        Assert.Equal("117",ExcelPayslipPdfService.Amount(source.NetPay,0));Assert.Equal("117",ExcelPayslipPdfService.Amount(result.Net.Value,0));
        Assert.Equal("1.01",ExcelPayslipPdfService.Amount(source.Deductions[1].Amount,2));Assert.Equal("1.00",ExcelPayslipPdfService.Amount(result.Lines.Single(l=>l.Code=="ESIC").Earned,2));
        Assert.True(Math.Abs(result.Net.Value-source.NetPay)>0.000001m);
    }
    [Fact] public void PayslipTotalsPreserveRawSummationAndDeclaredNetRatherThanAddingDisplayedComponents()
    {
        var row=Source();var totals=ExcelPayslipPdfService.TotalsFor(row);
        Assert.Equal(row.Earnings.Sum(x=>x.Amount),totals.Gross);Assert.Equal(row.Deductions.Sum(x=>x.Amount),totals.Deductions);Assert.Equal(row.NetPay,totals.Net);
        Assert.Equal("17.09",ExcelPayslipPdfService.Amount(totals.Deductions,2));Assert.Equal("116.92",ExcelPayslipPdfService.Amount(totals.Net,2));
        // 134.00 minus the independently displayed 17.09 is 116.91, not the independently displayed declared net.
        decimal Printed(decimal value)=>decimal.Parse(ExcelPayslipPdfService.Amount(value,2),System.Globalization.NumberStyles.Number,System.Globalization.CultureInfo.GetCultureInfo("en-IN"));
        Assert.NotEqual(Printed(totals.Gross)-Printed(totals.Deductions),Printed(totals.Net));
    }
    [Fact] public void DisplayNeverRewritesRawSourceOrDeclaredTotals()
    {
        var row=Source();row.DeclaredGross=135.001m;row.DeclaredDeductions=18.001m;var before=JsonSerializer.Serialize(row);
        var totals=ExcelPayslipPdfService.TotalsFor(row);Assert.Equal(row.DeclaredGross,totals.Gross);Assert.Equal(row.DeclaredDeductions,totals.Deductions);
        _=ExcelPayslipPdfService.Amount(totals.Net,0);_=ExcelPayslipPdfService.Amount(totals.Net,2);Assert.Equal(before,JsonSerializer.Serialize(row));
    }
    [Theory][InlineData("1.0049","1.00")][InlineData("1.0049999999999997","1.01")][InlineData("-1.0049999999999997","-1.01")]
    public void SignificantDigitDisplayDoesNotPromoteMeaningfulBelowMidpointValues(string raw,string expected)
        => Assert.Equal(expected,ExcelPayslipPdfService.Amount(decimal.Parse(raw,System.Globalization.CultureInfo.InvariantCulture),2));
    [Fact] public void ExplicitSourceRoundIsARealCalculationRuleAndMustNotBeInventedFromNumberFormatting()
    {
        var original=Calculate();var explicitRound=Calculate("ROUND(BASIC_EARNED*0.75%,2)");Assert.True(explicitRound.Complete);
        Assert.Equal(1m,original.Lines.Single(l=>l.Code=="ESIC").Earned);Assert.Equal(1.01m,explicitRound.Lines.Single(l=>l.Code=="ESIC").Earned);
        Assert.Equal(116.91m,explicitRound.Net);Assert.Equal(116.92m,original.Net);
    }
}
