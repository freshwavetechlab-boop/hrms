using System.Security.Cryptography;
using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Xunit;

namespace Payroll.API.Tests.Services;

public sealed class RecurringBlankInputReviewTests
{
    private static readonly JsonSerializerOptions Wire=new(JsonSerializerDefaults.Web);
    private static (ExcelPayrollSetupRequest Q,ExcelPayslipBatch B,ExcelPayslipCalculationSource S,ExcelPayslipRow R) Fixture()
    {
        var r=new ExcelPayslipRow{Id="blank-operand-row",SourceRow=8,NetPay=300};
        var b=new ExcelPayslipBatch{Id=new string('a',32),ClientId=7,Month="2026-10",Rows=[r]};
        var s=new ExcelPayslipCalculationSource{ColumnCount=6,Rows=[new(){SourceRow=8,Cells=[new(){DataType="Blank",Value=""},new(){DataType="Number",Value="1000"},new(){DataType="Number",Value="15"},new(){DataType="Number",Value="500",FormulaText="B8/30*C8"},new(){DataType="Number",Value="500",FormulaText="D8+A8/30*C8"},new(){DataType="Number",Value="200",FormulaText="IF(E8>400,200,0)"}]}],Columns=[new(){ColumnIndex=0,SourceHeader="Arrears",Label="Arrears",Kind="ignore"},new(){ColumnIndex=1,SourceHeader="Monthly pay",Label="Monthly pay",Kind="info"},new(){ColumnIndex=2,SourceHeader="Payable days",Label="Payable days",Kind="info"},new(){ColumnIndex=3,SourceHeader="Pay",Label="Pay",Kind="earning"},new(){ColumnIndex=4,SourceHeader="Subtotal",Label="Subtotal",Kind="ignore"},new(){ColumnIndex=5,SourceHeader="PT",Label="PT",Kind="deduction"}]};
        var q=new ExcelPayrollSetupRequest{BatchId=b.Id,RowIds=[r.Id],ReviewedFixedInputs=true,ReviewedPolicyScope=true,TransferReviewVersion=1,FixedPayrollDays=30,PayableDaysColumnIndex=2,SourceColumnRoles=new(){{0,"info"},{1,"info"},{4,"info"}},Components=[new(){Key="pay",Code="PAY",ExistingComponentId="106",SourceColumnIndex=1,ExpectedColumnIndex=3,CalculationType="Fixed Amount",ProRata=true},new(){Key="pt",Code="PT",ExistingComponentId="203",Category="Deduction",SourceColumnIndex=null,ExpectedColumnIndex=5,CalculationType="Formula",ProRata=false,Formula="IF((PAY_EARNED+0/30*PAYABLE_DAYS)>400,200,0)"}]};
        q.RecurringBlankInputReview=new(1,true,Hash(b,s),[0],"Reviewed empty arrears operand uses Excel numeric zero semantics.");return(q,b,s,r);
    }
    private static string Hash(ExcelPayslipBatch b,ExcelPayslipCalculationSource s)=>Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new{Batch=b,Source=s},Wire))).ToLowerInvariant();
    [Fact] public void ExplicitCapturedBlankBecomesZeroWithEvidenceAndRealCalculation()
    {
        var f=Fixture();var original=JsonSerializer.Serialize(f.S);ExcelPayrollSetupRepository.BindRecurringBlankInputReview(f.Q,f.B,f.S);
        var translated=ExcelPayrollSetupRepository.TranslateTransferFormulaWithEvidence("IF(E8>400,200,0)",f.Q,f.S,f.R);
        Assert.Contains(translated.Dependencies,d=>d.ColumnIndex==0&&d.Kind=="reviewed-blank-zero"&&d.Literal=="0"&&d.Formula is null);
        Assert.Equal("Exact supported translation",ExcelPayrollSetupRepository.InspectTransferFormula(f.Q,f.S,f.R,f.Q.Components[1]).Status);
        var components=f.Q.Components.Select(c=>new ExcelPayrollSetupRepository.ResolvedComponent(c,c.ExistingComponentId!,false)).ToArray();var actual=ExcelPayrollSetupRepository.CalculateRow(7,f.Q,components,f.S.Rows[0],f.R,calculationSource:f.S);
        Assert.Equal(300,actual.Preview.Net);Assert.Equal(200,actual.Preview.Deductions);Assert.Equal(500,actual.Preview.Gross);Assert.Single(actual.Inputs);Assert.Equal(1000,actual.Inputs["106"]);Assert.Equal(original,JsonSerializer.Serialize(f.S));
    }
    [Fact] public void OmissionPreservesExistingRequestBytesAndBlankRejection()
    {
        var f=Fixture();f.Q.RecurringBlankInputReview=null;Assert.DoesNotContain("recurringBlankInputReview",JsonSerializer.Serialize(f.Q,Wire));
        Assert.Throws<InvalidOperationException>(()=>ExcelPayrollSetupRepository.TranslateTransferFormula("IF(E8>400,200,0)",f.Q,f.S,f.R));
        ExcelPayrollSetupRepository.BindRecurringBlankInputReview(f.Q,f.B,f.S);
    }
    [Theory][InlineData("changed-source")][InlineData("changed-batch")][InlineData("changed-selection")][InlineData("unreviewed")][InlineData("bad-version")][InlineData("missing-reason")][InlineData("duplicate-column")][InlineData("out-of-range")][InlineData("not-fixed-reviewed")][InlineData("not-policy-reviewed")][InlineData("not-transfer")]
    public void ReviewMustBeFreshExplicitAndBounded(string mutation)
    {
        var f=Fixture();var d=f.Q.RecurringBlankInputReview!;
        switch(mutation){case"changed-source":f.S.Rows[0].Cells[1]!.Value="1001";break;case"changed-batch":f.B.Month="2026-11";break;case"changed-selection":f.Q.RowIds=["another-row"];break;case"unreviewed":f.Q.RecurringBlankInputReview=d with{Reviewed=false};break;case"bad-version":f.Q.RecurringBlankInputReview=d with{Version=2};break;case"missing-reason":f.Q.RecurringBlankInputReview=d with{Reason=""};break;case"duplicate-column":f.Q.RecurringBlankInputReview=d with{ColumnIndices=[0,0]};break;case"out-of-range":f.Q.RecurringBlankInputReview=d with{ColumnIndices=[1500]};break;case"not-fixed-reviewed":f.Q.ReviewedFixedInputs=false;break;case"not-policy-reviewed":f.Q.ReviewedPolicyScope=false;break;case"not-transfer":f.Q.TransferReviewVersion=0;break;}
        Assert.Throws<InvalidOperationException>(()=>ExcelPayrollSetupRepository.BindRecurringBlankInputReview(f.Q,f.B,f.S));
    }
    [Theory][InlineData("missing-cell")][InlineData("text")][InlineData("number-zero")][InlineData("value")][InlineData("space")][InlineData("formula")][InlineData("error")][InlineData("calculation-error")][InlineData("missing-cache")][InlineData("metadata-duplicate")][InlineData("output")][InlineData("input")][InlineData("financial-kind")][InlineData("not-info")][InlineData("payable")][InlineData("present")][InlineData("payroll")][InlineData("ctc")][InlineData("lwp")]
    public void ExplicitReviewDoesNotWaiveOtherCellAuthorities(string mutation)
    {
        var f=Fixture();var cell=f.S.Rows[0].Cells[0]!;
        switch(mutation){case"missing-cell":f.S.Rows[0].Cells[0]=null;break;case"text":cell.DataType="Text";break;case"number-zero":cell.DataType="Number";cell.Value="0";break;case"value":cell.Value="1";break;case"space":cell.Value=" ";break;case"formula":cell.FormulaText="0";break;case"error":cell.Error="#VALUE!";break;case"calculation-error":cell.CalculationError="#REF!";break;case"missing-cache":cell.MissingCachedValue=true;break;case"metadata-duplicate":f.S.Columns.Add(f.S.Columns[0]);break;case"output":f.Q.Components[0].ExpectedColumnIndex=0;break;case"input":f.Q.Components[0].SourceColumnIndex=0;break;case"financial-kind":f.S.Columns[0].Kind="earning";break;case"not-info":f.Q.SourceColumnRoles[0]="ignore";break;case"payable":f.Q.PayableDaysColumnIndex=0;break;case"present":f.Q.PresentDaysColumnIndex=0;break;case"payroll":f.Q.PayrollDaysColumnIndex=0;break;case"ctc":f.Q.MonthlyCtcColumnIndex=0;break;case"lwp":f.Q.AttendanceReview=new(){LwpColumnIndex=0};break;}
        f.Q.RecurringBlankInputReview=f.Q.RecurringBlankInputReview! with{SourceHash=Hash(f.B,f.S)};
        Assert.Throws<InvalidOperationException>(()=>ExcelPayrollSetupRepository.BindRecurringBlankInputReview(f.Q,f.B,f.S));
    }
    [Theory][InlineData("UAN")][InlineData("Mobile No")][InlineData("Employee ID")][InlineData("Bank Account")][InlineData("Aadhaar number")][InlineData("Present days")][InlineData("Actual attendance")][InlineData("Overtime hours")][InlineData("Date of joining")][InlineData("Gross salary")][InlineData("Monthly CTC")][InlineData("Subtotal")]
    public void EmptyIdentitiesAttendanceAndTotalsAreNeverNumericZero(string header)
    {
        var f=Fixture();f.S.Columns[0].SourceHeader=f.S.Columns[0].Label=header;f.Q.RecurringBlankInputReview=f.Q.RecurringBlankInputReview! with{SourceHash=Hash(f.B,f.S)};
        Assert.Throws<InvalidOperationException>(()=>ExcelPayrollSetupRepository.BindRecurringBlankInputReview(f.Q,f.B,f.S));
    }
    [Fact] public void EverySelectedSourceRowMustContainTheReviewedBlank()
    {
        var f=Fixture();f.B.Rows.Add(new(){Id="second",SourceRow=9});f.Q.RowIds.Add("second");f.S.Rows.Add(new(){SourceRow=9,Cells=[new(){DataType="Number",Value="5"}]});f.Q.RecurringBlankInputReview=f.Q.RecurringBlankInputReview! with{SourceHash=Hash(f.B,f.S)};
        Assert.Throws<InvalidOperationException>(()=>ExcelPayrollSetupRepository.BindRecurringBlankInputReview(f.Q,f.B,f.S));
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void HistoricalAndReportedPayrollCannotReuseTheConfigurationDeclaration(bool reported)
    {
        var f=Fixture();var request=new HistoricalPeriodSalaryRequest{Setup=f.Q};
        var error=Assert.Throws<InvalidOperationException>(()=>{if(reported)ExcelPayrollSetupRepository.ValidateReportedHistoricalSalaryRequest(request);else ExcelPayrollSetupRepository.ValidateHistoricalSalaryRequest(request);});
        Assert.Contains("Recurring blank operand review cannot authorize",error.Message);
    }
}
