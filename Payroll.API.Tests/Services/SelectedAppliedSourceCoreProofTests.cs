using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;
using Xunit;

namespace Payroll.API.Tests.Services;

public class SelectedAppliedSourceCoreProofTests
{
    private static PayrollAdjustment Adjustment()=>new(){Id=101,ClientId=7,EmployeeId=11,ComponentCode="TA_DA",
        ComponentName="Synthetic travel",AdjustmentType="Reimbursement",Amount=22680m,Taxable=false,
        PayPeriod="2026-09",PayRunType="Regular",Status="Approved",ReasonCode="EXCEL_SETUP"};

    private static (PayrollSimulationResult Core,string Raw,List<SelectedSourceFinancialLine> Source,PayrollAdjustment Adjustment) Fixture()
    {
        var adjustment=Adjustment();
        // The shared fixture invokes the actual regular BuildEmployee calculator.
        // These tests do not invent a second financial implementation or call DB.
        var row=SelectedPayRunAdjustmentTests.Calculate([adjustment],30,"2026-09");
        using var details=JsonDocument.Parse(row.DetailsJson);
        string Text(JsonElement line,string key,string fallback="")=>line.TryGetProperty(key,out var value)?value.ToString():fallback;
        var core=new PayrollSimulationResult {Mode="Employee",ClientId=7,EmployeeId=11,PayPeriod="2026-09",
            Gross=row.GrossPay+row.OneTimeEarnings,Deductions=row.StatutoryDeductions+row.OneTimeDeductions,Net=row.NetPay,Benefits=0};
        core.Lines=details.RootElement.EnumerateArray().Where(l=>Text(l,"Category")!="Summary").Select(l=>
            new PayrollSimulationLine(Text(l,"Id"),Text(l,"Name"),Text(l,"Category"),"","",l.GetProperty("amount").GetDecimal(),
                l.GetProperty("monthlyAmount").GetDecimal(),l.GetProperty("amount").GetDecimal(),false,"Actual BuildEmployee fixture","",[])).ToList();
        return(core,row.DetailsJson,[new("WAGES","Earning",145000,145000,145000,145000,"Exact supported translation"){ComponentRole="Regular Earning"},
            new("TA_DA","Reimbursement",22680,22680,22680,22680,"Reviewed existing period literal"){ComponentRole="Regular Earning"}],adjustment);
    }

    [Fact]
    public void Actual_core_retains_zero_template_and_pays_existing_adjustment_once_without_recurrence()
    {
        var f=Fixture();var result=PayRunRepository.CompareAppliedSourceCore(f.Source,f.Core,f.Raw,f.Adjustment);
        Assert.Equal(2,result.Count);Assert.Equal(167680m,f.Core.Net);
        Assert.Equal(2,f.Core.Lines.Count(l=>l.Code=="TA_DA"));
        Assert.Single(f.Core.Lines,l=>l.Code=="TA_DA"&&l.Earned==0);
        Assert.Single(f.Core.Lines,l=>l.Code=="TA_DA"&&l.Earned==22680);
        Assert.Equal(22680m,Assert.Single(result,l=>l.Code=="TA_DA").CoreAmount);
        Assert.Equal(145000m,SelectedPayRunAdjustmentTests.Calculate([],30,"2026-10").NetPay);
        Assert.Equal("Approved",f.Adjustment.Status);Assert.Null(f.Adjustment.PayRunId);
    }

    [Theory]
    [InlineData("extra-projected-tax")] [InlineData("missing-component")] [InlineData("wrong-category")]
    [InlineData("wrong-role")] [InlineData("wrong-statutory")] [InlineData("wrong-period-amount")]
    [InlineData("no-placeholder")] [InlineData("duplicate-payment")]
    public void Equal_net_or_small_scope_does_not_clear_component_and_classification_blockers(string mutation)
    {
        var f=Fixture();var raw=f.Raw;
        switch(mutation)
        {
            case "extra-projected-tax":f.Core.Lines.Add(new("TDS","Projected tax","Deduction","","",1,1,1,false,"","",[]));break;
            case "missing-component":f.Core.Lines.RemoveAll(l=>l.Code=="WAGES");break;
            case "wrong-category":f.Source[0]=f.Source[0] with{Category="Benefit"};break;
            case "wrong-role":f.Source[0]=f.Source[0] with{ComponentRole="Employer Contribution"};break;
            case "wrong-statutory":f.Source[0]=f.Source[0] with{StatutoryType="EPS"};break;
            case "wrong-period-amount":f.Source[1]=f.Source[1] with{SourceRaw=22679.99m};break;
            case "no-placeholder":f.Core.Lines.RemoveAll(l=>l.Code=="TA_DA"&&l.Earned==0);break;
            case "duplicate-payment":raw=raw.Replace("\"Id\":\"TA_DA\",\"Name\":\"Travel\",\"Category\":\"Reimbursement\",\"ComponentRole\":\"\",\"StatutoryType\":\"None\",\"monthlyAmount\":0,\"amount\":0", "\"Id\":\"TA_DA\",\"Name\":\"Travel\",\"Category\":\"Reimbursement\",\"monthlyAmount\":22680,\"amount\":22680");
                f.Core.Lines.Add(f.Core.Lines.Single(l=>l.Code=="TA_DA"&&l.Earned>0));break;
        }
        Assert.Throws<SelectedPayRunConflictException>(()=>PayRunRepository.CompareAppliedSourceCore(f.Source,f.Core,raw,f.Adjustment));
    }

    [Fact]
    public void Currency_delta_needs_exact_source_pre_rounding_trace_not_a_money_epsilon()
    {
        var f=Fixture();f.Source[0]=f.Source[0] with{SourceRaw=145000.005m,SourceDisplay=145000.01m};
        var index=f.Core.Lines.FindIndex(l=>l.Code=="WAGES");
        f.Core.Lines[index]=f.Core.Lines[index] with{UnroundedEarned=145000.005m};
        var result=PayRunRepository.CompareAppliedSourceCore(f.Source,f.Core,f.Raw,f.Adjustment);
        Assert.Contains("explained native currency stage",result[0].FormulaStatus);
        f.Core.Lines[index]=f.Core.Lines[index] with{UnroundedEarned=145000.004m};
        Assert.Throws<SelectedPayRunConflictException>(()=>PayRunRepository.CompareAppliedSourceCore(f.Source,f.Core,f.Raw,f.Adjustment));
    }

    [Theory]
    [InlineData(30)] [InlineData(26)]
    public void Legitimate_monthly_inputs_need_no_invented_daily_records_and_do_not_map_source_worked(int nativePresent)
    {
        var native=new PayRunRepository.PayRunAttendance {EmployeeId=11,WorkingDays=30,PresentDays=nativePresent,PayableDays=30,
            InputSource="Monthly snapshot",SourceType="Monthly"};
        var cycle=PayRunRepository.RequireAppliedSourceNative(new(){PayPeriod="2026-09",TotalWorkingDays=30},30,30,null,native,"Monthly",1,30);
        Assert.Equal(("2026-09-01","2026-09-30"),cycle);Assert.Null(native.PolicySnapshot);
        Assert.Equal(nativePresent,native.PresentDays);
    }

    [Theory]
    [InlineData("policy-error")] [InlineData("dated-required")] [InlineData("paid-mismatch")]
    [InlineData("divisor-mismatch")] [InlineData("source-actual-mismatch")]
    public void Actual_regular_policy_or_source_native_conflict_stays_blocked(string mutation)
    {
        var native=new PayRunRepository.PayRunAttendance {EmployeeId=11,WorkingDays=30,PresentDays=30,PayableDays=30,
            InputSource="Monthly snapshot",SourceType="Monthly"};
        var mode="Monthly";decimal? present=null;
        switch(mutation){case "policy-error":native.PolicyError="Published split requires dated input";break;case "dated-required":mode="DateWise";break;
            case "paid-mismatch":native.PayableDays=29;break;case "divisor-mismatch":native.WorkingDays=31;break;case "source-actual-mismatch":present=29;break;}
        Assert.Throws<SelectedPayRunConflictException>(()=>PayRunRepository.RequireAppliedSourceNative(new(){PayPeriod="2026-09",TotalWorkingDays=30},30,30,present,native,mode,1,30));
    }

    [Fact]
    public void Repeated_tax_review_ignores_only_generated_clock_and_keeps_regime_ytd_and_real_policy_changes()
    {
        string Proof(string clock,string regime="New",decimal ytd=100,string changed="2026-09-01T00:00:00Z")=>
            SelectedPayRunAppliedSourceReview.Hash(PayRunRepository.StableProjectedTaxInputs(JsonSerializer.Serialize(new{
                calculatedAt=clock,regime,tdsAlreadyDeducted=ytd,ruleVersion=new{id=9,updatedAt=changed},grossSalary=167680})));
        var first=Proof("2026-10-29T00:00:00Z");Assert.Equal(first,Proof("2026-10-29T01:00:00Z"));
        Assert.NotEqual(first,Proof("2026-10-29T01:00:00Z","Old"));
        Assert.NotEqual(first,Proof("2026-10-29T01:00:00Z",ytd:101));
        Assert.NotEqual(first,Proof("2026-10-29T01:00:00Z",changed:"2026-09-02T00:00:00Z"));
    }
}
