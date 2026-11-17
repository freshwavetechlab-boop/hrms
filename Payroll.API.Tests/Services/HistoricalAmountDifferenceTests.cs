using System.Globalization;
using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class HistoricalAmountDifferenceTests
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private sealed record Case(HistoricalPeriodSalaryRequest Request,ExcelPayslipBatch Batch,ExcelPayslipCalculationSource Source,
        ExcelPayslipRow Row,ExcelPayrollSetupPreviewRow Calculated,List<ExcelPayrollTransferFormula> Formulas,PayrollSimulationResult Native);
    private static Case Source28()
    {
        string[] codes=["BASIC","BONUS","PF","ESIC","PF_ER","ESIC_ER"],kinds=["Earning","Earning","Deduction","Deduction","Benefit","Benefit"];
        decimal[] raw=[12783.333333333334m,200,1534,95.875m,1661.8333333333335m,415.45833333333337m],core=[12783.33m,200,1534,95.87m,1661.83m,415.46m],pre=[12783.333333333334m,200,1533.9996m,95.874975m,1661.8329m,415.458225m];
        var row=new ExcelPayslipRow{Id="verified-row28",SourceRow=28,NetPay=11353.458333333334m};
        var source=new ExcelPayslipCalculationSource{ColumnCount=6,Rows=[new(){SourceRow=28}],Columns=[]};
        var q=new HistoricalPeriodSalaryRequest{Assignments=[new(row.Id,10,"123")],Setup=new(){RowIds=[row.Id],ReviewedFixedInputs=true}};
        var calculated=new ExcelPayrollSetupPreviewRow{RowId=row.Id,EmployeeId=10,TemplateId="123",PayrollDays=30,PresentDays=19,PayableDays=19,Gross=12983.33m,Deductions=1629.87m,Benefits=2077.29m,Net=11353.46m};
        var native=new PayrollSimulationResult{Mode="Employee",ClientId=64,EmployeeId=10,TemplateId="123",PayPeriod="2026-09",Gross=calculated.Gross,Deductions=calculated.Deductions,Benefits=calculated.Benefits,Net=calculated.Net,
            AttendanceEvidence=new("Monthly snapshot","2026-09",30,19,19,null,null,"2026-09-01","2026-09-30",false,"","Existing actual attendance")};
        for(var i=0;i<6;i++)
        {
            var category=kinds[i];var amounts=category=="Earning"?row.Earnings:category=="Deduction"?row.Deductions:row.EmployerContributions;
            amounts.Add(new(){Label=codes[i],Amount=raw[i]});
            source.Columns.Add(new(){ColumnIndex=i,Label=codes[i],SourceHeader=codes[i],Kind=category=="Earning"?"earning":category=="Deduction"?"deduction":"employer"});source.Rows[0].Cells.Add(new(){Value=raw[i].ToString(CultureInfo.InvariantCulture)});
            q.Setup.Components.Add(new(){Key=codes[i],ExistingComponentId=(i+1).ToString(),Code=codes[i],Category=category,ExpectedColumnIndex=i,CalculationType=i==1?"Fixed Amount":"Formula",Formula=i==1?"":"SUPPORTED_RULE"});
            calculated.Lines.Add(new(codes[i],raw[i],core[i],core[i]-raw[i]){Category=category,UnroundedEarned=pre[i]});
            native.Lines.Add(new(codes[i],codes[i],category,"SUPPORTED_RULE","",pre[i],pre[i],core[i],false,"native","currency",[]){UnroundedEarned=pre[i]});
        }
        var batch=new ExcelPayslipBatch{Id=new string('a',32),ClientId=64,Month="2026-09",Rows=[row]};
        var hash=ExcelPayrollSetupRepository.Hash(JsonSerializer.Serialize(new{Batch=batch,Source=source},Json));
        q.AmountDifferenceReview=new(){Version=1,Reviewed=true,SourceHash=hash,MaxAbsoluteDifference=1,Reason="Explicitly reviewed source/native currency-stage differences; retain every amount."};
        var formulas=codes.Select((code,i)=>new ExcelPayrollTransferFormula(row.Id,code,28,i,hash,i==1?"Reviewed fixed source":"Exact supported translation","Verified independent source rule")).ToList();
        return new(q,batch,source,row,calculated,formulas,native);
    }
    private static ExcelPayrollTransferAmountReview Review(Case c,string? fingerprint=null)=>ExcelPayrollSetupRepository.ReviewHistoricalAmountDifferences(c.Request,c.Batch,c.Source,c.Row,c.Calculated,c.Formulas,c.Native,fingerprint??new string('b',64),new(){Status="Mismatch",RowId=c.Row.Id});
    [Fact] public void Actual_upstream_currency_stage_difference_is_explicit_not_exact_and_does_not_change_money()
    {
        var c=Source28();var before=JsonSerializer.Serialize(c,Json);var result=Review(c);
        Assert.Equal("Reviewed bounded monetary difference",result.Status);Assert.False(result.Acknowledged);Assert.Equal(64,result.ProofHash.Length);
        Assert.Equal(6,result.Lines.Count);Assert.Equal(4,result.BoundedDifference!.Totals.Count);Assert.Equal(3,result.BoundedDifference.SourceAggregates.Count);
        Assert.Equal(95.875m,result.Lines.Single(l=>l.Code=="ESIC").SourceRaw);Assert.Equal(95.87m,result.Lines.Single(l=>l.Code=="ESIC").Calculated);
        Assert.Equal(95.874975m,result.Lines.Single(l=>l.Code=="ESIC").CoreUnrounded);Assert.Equal(before,JsonSerializer.Serialize(c,Json));
        Assert.False(ExcelPayrollSetupRepository.IsAcknowledgedHistoricalAmountDifference(c.Request,result));c.Request.Setup.RoundingReviews[c.Row.Id]=result.ProofHash;
        Assert.Equal(result.ProofHash,Review(c).ProofHash);Assert.True(ExcelPayrollSetupRepository.IsAcknowledgedHistoricalAmountDifference(c.Request,result));
    }
    [Fact] public void Input_trace_and_review_reason_are_in_the_specific_proof()
    {var c=Source28();var hash=Review(c).ProofHash;Assert.NotEqual(hash,Review(c,new string('c',64)).ProofHash);c.Request.AmountDifferenceReview!.Reason+=" Additional specific review.";Assert.NotEqual(hash,Review(c).ProofHash);}
    [Fact] public void Unmapped_source_presence_does_not_replace_independently_known_native_actual_presence()
    {var c=Source28();c.Calculated.PresentDays=30;Assert.Equal("Reviewed bounded monetary difference",Review(c).Status);c.Request.Setup.PresentDaysColumnIndex=0;Assert.Throws<InvalidOperationException>(()=>Review(c));}
    [Theory][InlineData("unsupported")][InlineData("missing")][InlineData("duplicate")][InlineData("category")][InlineData("override")][InlineData("days")][InlineData("unknownPresence")][InlineData("nativeTotal")][InlineData("foreign")][InlineData("fingerprint")][InlineData("sourceHash")]
    public void Monetary_permission_cannot_waive_nonmonetary_or_native_total_gaps(string change)
    {
        var c=Source28();switch(change){case "unsupported":c.Formulas[2]=c.Formulas[2] with{Status="Unsupported"};break;case "missing":c.Native.Lines.RemoveAt(2);break;case "duplicate":c.Native.Lines.Add(c.Native.Lines[2]);break;case "category":c.Native.Lines[2]=c.Native.Lines[2] with{Category="Earning"};break;case "override":c.Native.Lines[2]=c.Native.Lines[2] with{AmountOverride=true};break;case "days":c.Native.AttendanceEvidence=c.Native.AttendanceEvidence! with{TemporaryDayOverrides=true};break;case "unknownPresence":c.Native.AttendanceEvidence=c.Native.AttendanceEvidence! with{PresentDays=null};break;case "nativeTotal":c.Native.Net++;break;case "foreign":c.Native.EmployeeId=11;break;case "sourceHash":c.Request.AmountDifferenceReview!.SourceHash=new string('e',64);break;}
        Assert.Throws<InvalidOperationException>(()=>Review(c,change=="fingerprint"?"bad":null));
    }
    [Theory][InlineData(0)][InlineData(-1)][InlineData(1.01)]
    public void Declaration_never_allows_a_limit_above_one_or_nonpositive(decimal amount)
    {var c=Source28();c.Request.AmountDifferenceReview!.MaxAbsoluteDifference=amount;Assert.Throws<InvalidOperationException>(()=>Review(c));}
    [Fact] public void A_tighter_explicit_limit_rejects_a_line_even_if_net_display_agrees()
    {var c=Source28();c.Request.AmountDifferenceReview!.MaxAbsoluteDifference=.004m;Assert.Throws<InvalidOperationException>(()=>Review(c));}
    [Fact] public void Negative_declared_source_totals_are_not_a_small_monetary_waiver()
    {var c=Source28();c.Row.DeclaredGross=-.25m;c.Request.AmountDifferenceReview!.SourceHash=ExcelPayrollSetupRepository.Hash(JsonSerializer.Serialize(new{Batch=c.Batch,Source=c.Source},Json));Assert.Throws<InvalidOperationException>(()=>Review(c));}
    [Fact] public void Default_null_metadata_is_omitted_from_legacy_protected_shapes()
    {
        Assert.DoesNotContain("amountDifferenceReview",JsonSerializer.Serialize(new HistoricalPeriodSalaryRequest(),Json));
        Assert.DoesNotContain("amountDifferences",JsonSerializer.Serialize(new HistoricalPeriodSalaryReceipt(),Json));
        Assert.DoesNotContain("boundedDifference",JsonSerializer.Serialize(new ExcelPayrollTransferAmountReview(),Json));
        var c=Source28();c.Request.AmountDifferenceReview=null;var old=new ExcelPayrollTransferAmountReview{Status="Mismatch",Reason="Retained legacy outcome"};Assert.Same(old,ExcelPayrollSetupRepository.ReviewHistoricalAmountDifferences(c.Request,c.Batch,c.Source,c.Row,c.Calculated,c.Formulas,c.Native,"",old));
    }
}
