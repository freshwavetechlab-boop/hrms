using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public class ExcelPayrollWhatIfTests
{
    private static (ExcelPayrollSetupRepository.RowArithmetic Row, ExcelPayrollSetupRequest Request) Example()
    {
        var request = new ExcelPayrollSetupRequest {
            BatchId = new string('a',32), RowIds = ["source"], TemplateName = "Anonymous monthly rules", EffectiveFrom = "2026-09-01",
            PayrollDaysColumnIndex = 0, PayableDaysColumnIndex = 1,
            Components = [
                new() { Key="basic",Code="BASIC",Name="Basic",SourceColumnIndex=2,ProRata=true,Priority=10 },
                new() { Key="hra",Code="HRA",Name="HRA",SourceColumnIndex=3,ProRata=true,Priority=20 },
                new() { Key="bonus",Code="BONUS",Name="Conditional bonus",CalculationType="Formula",Formula="IF(BASIC_MONTHLY<=21000,ROUNDUP(BASIC_EARNED*8.33%,0),0)",ProRata=false,Priority=30 },
                new() { Key="pf",Code="PF",Name="Employee PF",Category="Deduction",CalculationType="Formula",Formula="BASIC_EARNED*12%",ProRata=false,Priority=40 },
                new() { Key="er",Code="ER",Name="Employer cost",Category="Benefit",CalculationType="Formula",Formula="BASIC_EARNED*12%",ProRata=false,Priority=50 },
                new() { Key="ta",Code="TA_DA",Name="Source period reimbursement",Category="Reimbursement",SourceColumnIndex=4,IsPeriodAdjustment=true,ProRata=false,Priority=60 }
            ]
        };
        var components = request.Components.Select((c,i)=>new ExcelPayrollSetupRepository.ResolvedComponent(c,(i+1).ToString(),true)).ToList();
        var source = new ExcelPayslipCalculationSourceRow { SourceRow=2,Cells = new[] {"30","30","20000","5000","1000"}.Select(v=>(ExcelPayslipCalculationCell?)new ExcelPayslipCalculationCell {Value=v}).ToList() };
        return (ExcelPayrollSetupRepository.CalculateRow(33,request,components,source,new ExcelPayslipRow {Id="source",NetPay=25266}),request);
    }

    [Fact]
    public void ResolvedSnapshotReplaysAllComponentsIncludingPeriodAmountExactlyOnce()
    {
        var (row, request) = Example();
        var before = JsonSerializer.Serialize(request);
        var scenario = Assert.IsType<PayrollSimulationRequest>(row.Preview.WhatIfSimulation);
        var result = PayRunRepository.SimulateTemplate(scenario);
        Assert.True(result.Complete);
        Assert.Equal(row.Preview.Net,result.Net);
        Assert.Equal(row.Preview.Benefits,result.Benefits);
        Assert.All(row.Preview.Lines,l=>Assert.Equal(l.CalculatedAmount,result.Lines.Single(r=>r.Code==l.Code).Earned));
        Assert.Equal(1000m,result.Lines.Single(l=>l.Code=="TA_DA").Earned);
        Assert.Equal(300000m,scenario.AnnualCtc); // Reimbursement is not annual recurring pay.
        Assert.True(request.Components.Single(c=>c.Code=="TA_DA").IsPeriodAdjustment);
        Assert.Equal(before,JsonSerializer.Serialize(request));
    }

    [Fact]
    public void TemporaryDaysAndMonthlyInputsDriveConditionalRulesWithoutChangingSourcePreview()
    {
        var (row, _) = Example();
        var originalNet=row.Preview.Net;
        var scenario=Assert.IsType<PayrollSimulationRequest>(row.Preview.WhatIfSimulation);
        scenario.PayableDays=15; scenario.PresentDays=14;
        var half=PayRunRepository.SimulateTemplate(scenario);
        Assert.Equal(10000m,half.Lines.Single(l=>l.Code=="BASIC").Earned);
        Assert.Equal(833m,half.Lines.Single(l=>l.Code=="BONUS").Earned);
        Assert.Equal(1000m,half.Lines.Single(l=>l.Code=="TA_DA").Earned); // Explicit period amount, no second proration.
        scenario.AmountOverrides["1"]=25000;
        var changed=PayRunRepository.SimulateTemplate(scenario);
        Assert.Equal(0m,changed.Lines.Single(l=>l.Code=="BONUS").Earned);
        Assert.Equal(12500m,changed.Lines.Single(l=>l.Code=="BASIC").Earned);
        Assert.Equal(originalNet,row.Preview.Net);
        Assert.Equal(20000m,row.Inputs["1"]);
    }

    [Fact]
    public void CtcDoesNotSilentlyRescaleFixedMonthlyInputsAndZeroDaysRemainValid()
    {
        var (row, _) = Example();
        var scenario=Assert.IsType<PayrollSimulationRequest>(row.Preview.WhatIfSimulation);
        scenario.AnnualCtc=600000;
        var ctcOnly=PayRunRepository.SimulateTemplate(scenario);
        Assert.Equal(20000m,ctcOnly.Lines.Single(l=>l.Code=="BASIC").Earned);
        scenario.PayableDays=0; scenario.PresentDays=0; scenario.AmountOverrides["6"]=0;
        var zero=PayRunRepository.SimulateTemplate(scenario);
        Assert.True(zero.Complete);
        Assert.Equal(0m,zero.Net);
        scenario.PayableDays=31;
        Assert.False(PayRunRepository.SimulateTemplate(scenario).Complete);
    }

    [Fact]
    public void CtcFormulaUsesAnnualTargetInSameCoreCalculator()
    {
        var snapshot=new PayrollReviewSnapshot("Template",33,"",300000,
            [new("1","BASIC","Earning","Regular Earning","None","Formula","","ANNUAL_CTC/12*40%","",true,true,true,10,"Fixed Pay")],
            [new("1","","ANNUAL_CTC/12*40%","Formula","","true")]);
        var original=new PayrollSimulationRequest {Mode="Template",ClientId=33,TemplateId="source",Snapshot=snapshot,AnnualCtc=300000,PayrollDays=30,PresentDays=30,PayableDays=30};
        var scenario=ExcelPayrollSetupRepository.SourceWhatIfSimulation(original,[]);
        scenario.AnnualCtc=600000; scenario.PayableDays=15;
        Assert.Equal(10000m,PayRunRepository.SimulateTemplate(scenario).Net);
        Assert.Equal(300000m,original.AnnualCtc);
        Assert.NotSame(original.AmountOverrides,scenario.AmountOverrides);
    }
}
