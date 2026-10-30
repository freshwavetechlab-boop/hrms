using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;
public sealed class ExcelNativeConditionalTests
{
    [Theory]
    [InlineData("IF(1=1,28,1/0)",28)]
    [InlineData("IF(1<>1,1/0,29.5)",29.5)]
    [InlineData("IF(AND(20>0,20<=21),ROUNDUP(20*8.33%,0),0)",2)]
    [InlineData("IF(OR(FALSE,TRUE),12,1/0)",12)]
    [InlineData("IFERROR(1/0,7)",7)]
    [InlineData("IFERROR(IF(FALSE,1/0,8),0)",8)]
    [InlineData("IFERROR(ROUND(1,99),9)",9)]
    [InlineData("ROUNDUP(-1.25,1)",-1.3)]
    [InlineData("ROUNDDOWN(-1.29,1)",-1.2)]
    [InlineData("ROUND(-1.25,1)",-1.3)]
    [InlineData("ROUNDUP(-123,-1)",-130)]
    [InlineData("ROUNDDOWN(-129,-1)",-120)]
    [InlineData("ROUNDUP(8.33,0)",9)]
    [InlineData("MIN(25000,30000)*12%",3000)]
    [InlineData("SUM(1,2,3)+ABS(-2)",8)]
    [InlineData("IF(TRUE=1,1,2)",2)]
    public void NativeAstMatchesBoundedCapturedExcelSemantics(string formula,double expected) {
        var s=new ExcelPayslipCalculationSource { SheetName="Synthetic",ColumnCount=1,CompleteSheetCapture=true,Rows=[new() {SourceRow=1,Cells=[new() { Value="0",DataType="Number",FormulaText=formula }] }] };
        var excel=ExcelPayslipCalculationService.EvaluateCapturedCell(s,"A1").Value;
        var native=PayrollFormulaEvaluator.Evaluate(formula,_=>throw new InvalidOperationException("Unmapped input"));
        Assert.Equal((decimal)expected,native);Assert.Equal(excel,native);
    }
    [Fact] public void LazyBranchesDoNotResolveUnusedInputsButStructuralMissingInputsCannotBeMasked() {
        Assert.Equal(1,PayrollFormulaEvaluator.Evaluate("IF(TRUE,1,MISSING)",_=>throw new InvalidOperationException("Missing native attendance input")));
        Assert.Throws<InvalidOperationException>(()=>PayrollFormulaEvaluator.Evaluate("IFERROR(MISSING,0)",_=>throw new InvalidOperationException("Missing native attendance input")));
        Assert.Throws<InvalidOperationException>(()=>PayrollFormulaEvaluator.Evaluate("IFERROR(VLOOKUP(1,2,3),0)",_=>0));
        Assert.Throws<InvalidOperationException>(()=>PayrollFormulaEvaluator.Evaluate("IFERROR("+new string('(',65)+"1"+new string(')',65)+",0)",_=>0));
    }
    [Fact] public void MainCalculatorUsesConditionalAstAndReturnsAllContributionAmounts() {
        var components=new List<PayrollReviewComponent> {
            new("1","WAGES","Earning","Regular Earning","None","Fixed Amount","30000","","",true,true,true,1,"Fixed Pay"),
            new("2","BONUS","Earning","Regular Earning","None","Formula","0","IF(WAGES_MONTHLY<=31000,ROUNDUP(WAGES_EARNED*8.33%,0),0)","",false,true,true,2,"Fixed Pay"),
            new("3","PF","Deduction","Statutory Deduction","PF Employee","Formula","0","IF(WAGES_MONTHLY>25000,25000*12%,WAGES_EARNED*12%)","",false,false,true,3,"Fixed Pay"),
            new("4","ER","Benefit","Employer Benefit","None","Formula","0","IFERROR(WAGES_EARNED/0,ROUNDUP(WAGES_EARNED*1%,0))","",false,false,true,4,"Fixed Pay") };
        var r=PayRunRepository.SimulateTemplate(new() { ClientId=42,TemplateId="synthetic",PayrollDays=30,PresentDays=20,PayableDays=28,AnnualCtc=0,Snapshot=new("Template",42,"",0,components,components.Select(c=>new PayrollReviewLine(c.Id)).ToList()) });
        Assert.True(r.Complete,string.Join(";",r.Issues.Select(i=>i.Message)));Assert.Equal(30333,r.Gross);Assert.Equal(3000,r.Deductions);Assert.Equal(280,r.Benefits);Assert.Equal(27333,r.Net);
    }
    [Fact] public void TransferProofTranslatesConditionalStructureWithoutFreezingMonthlyLookups() {
        var request=new ExcelPayrollSetupRequest { PayableDaysColumnIndex=2,PayrollDaysColumnIndex=3,Components=[new() { Code="BASIC",SourceColumnIndex=0,ExpectedColumnIndex=1 }] };
        var source=new ExcelPayslipCalculationSource();var row=new ExcelPayslipRow {SourceRow=3};
        var translated=ExcelPayrollSetupRepository.TranslateTransferFormula("IF(AND(A3<=25000,C3>0),ROUNDUP(B3*8.33%,0),0)",request,source,row);
        Assert.Equal("IF(AND(BASIC_MONTHLY<=25000,PAYABLE_DAYS>0),ROUNDUP(BASIC_EARNED*8.33%,0),0)",translated);
        PayrollFormulaEvaluator.Validate(translated);
        Assert.Throws<InvalidOperationException>(()=>ExcelPayrollSetupRepository.TranslateTransferFormula("IFERROR(VLOOKUP(A3,Attendance!A:D,4,0),0)",request,source,row));
    }
    [Fact] public void ClLwpDayImpactUsesMainEngineForEarningsDeductionsEmployerAndNet() {
        var c=new List<PayrollReviewComponent> {
            new("1","WAGES","Earning","Regular Earning","None","Fixed Amount","30000","","",true,true,true,1,"Fixed Pay"),
            new("2","DEDUCTION","Deduction","Regular Deduction","None","Formula","0","WAGES_EARNED*12%","",false,false,true,2,"Fixed Pay"),
            new("3","EMPLOYER","Benefit","Employer Benefit","None","Formula","0","ROUNDUP(WAGES_EARNED*1%,0)","",false,false,true,3,"Fixed Pay") };
        var request=new PayrollSimulationRequest {ClientId=42,TemplateId="synthetic",PayrollDays=30,PresentDays=20,PayableDays=26,AnnualCtc=0,Snapshot=new("Template",42,"",0,c,c.Select(x=>new PayrollReviewLine(x.Id)).ToList())};
        var before=PayRunRepository.SimulateTemplate(request);request.PayableDays=28;var after=PayRunRepository.SimulateTemplate(request);
        Assert.True(before.Complete);Assert.True(after.Complete);Assert.Equal(2000,after.Gross-before.Gross);Assert.Equal(240,after.Deductions-before.Deductions);Assert.Equal(20,after.Benefits-before.Benefits);Assert.Equal(1760,after.Net-before.Net);
        request.PayrollDays=26;var mismatch=PayRunRepository.SimulateTemplate(request);Assert.False(mismatch.Complete);Assert.Null(mismatch.Net);
        request.PayrollDays=30;request.PresentDays=null;var missing=PayRunRepository.SimulateTemplate(request);Assert.False(missing.Complete);Assert.Null(missing.Net);
    }
}
