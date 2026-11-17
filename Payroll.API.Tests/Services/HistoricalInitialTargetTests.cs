using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class HistoricalInitialTargetTests
{
    private static readonly string SourceHash=new('a',64);
    private static (HistoricalPeriodSalaryRequest Request,ExcelPayslipCalculationSource Source,ExcelPayslipRow Row) Fixture()
    {
        var request=new HistoricalPeriodSalaryRequest{SalaryTargetMode="ReviewedInitialSourceTarget",Setup=new(){MonthlyCtcColumnIndex=0,Components=[new(){ExpectedColumnIndex=1}]},
            InitialSourceTargetReview=new(){Version=1,Reviewed=true,MonthlyCtcColumnIndex=0,SourceHash=SourceHash,Meaning="MonthlyPayrollEarningsBudget",ActualJoiningDateNotEstablished=true}};
        var source=new ExcelPayslipCalculationSource{Columns=[new(){ColumnIndex=0,Kind="info",SourceHeader="Current CTC",Label="Current CTC"}],Rows=[new(){SourceRow=2,Cells=[new(){Value="10000"}]}]};
        return(request,source,new(){Id="source",SourceRow=2});
    }
    [Fact]
    public void An_independent_reviewed_literal_establishes_only_the_closed_source_target()
    {
        var(q,s,r)=Fixture();var target=ExcelPayrollSetupRepository.ReadInitialSourceTarget(q,s,r,10,120000,SourceHash);
        Assert.Equal(10000,target.SourceMonthlyBudget);Assert.Equal(120000,target.AnnualSalaryTarget);Assert.True(target.ActualJoiningDateNotEstablished);
        var future=new EmployeeRepository.ClosedSalaryHistoryRow{Id=1,EmployeeId=10,ClientId=1,EffectiveFrom=new(2026,11,10),AnnualCtc=120000};
        var state=new EmployeeRepository.ClosedSalaryHistoryState(1,"current",[future],new Dictionary<long,string>{{1,"future"}});
        EmployeeRepository.RequireClosedInitialSalaryWindow(state,10,new(2026,9,1),new(2026,9,30),target.AnnualSalaryTarget,120000);
        Assert.Throws<InvalidOperationException>(()=>EmployeeRepository.RequireClosedSalaryBasis(state,10,new(2026,9,1),new(2026,9,30)));
        s.Rows[0].Cells[0]!.Value="0";Assert.Equal(0,ExcelPayrollSetupRepository.ReadInitialSourceTarget(q,s,r,10,0,SourceHash).AnnualSalaryTarget);
        Assert.Throws<InvalidOperationException>(()=>ExcelPayrollSetupRepository.ReadInitialSourceTarget(q,s,r,10,120000,SourceHash));
    }
    [Theory]
    [InlineData("Rate")][InlineData("Monthly rate")][InlineData("Earned CTC")][InlineData("Annual CTC")]
    public void A_generic_rate_or_earned_annual_heading_cannot_be_relabelled_as_monthly_CTC(string heading)
    {
        var(q,s,r)=Fixture();s.Columns[0].SourceHeader=heading;
        Assert.Throws<InvalidOperationException>(()=>ExcelPayrollSetupRepository.ReadInitialSourceTarget(q,s,r,10,120000,SourceHash));
    }
    [Theory]
    [InlineData("formula")][InlineData("earned")][InlineData("blank")][InlineData("missing")][InlineData("error")]
    [InlineData("mismatch")][InlineData("precision")][InlineData("stale")]
    public void Missing_cached_derived_or_mismatched_source_values_never_become_a_target(string kind)
    {
        var(q,s,r)=Fixture();var cell=s.Rows[0].Cells[0]!;
        if(kind=="formula")cell.FormulaText="B2/30*20";
        if(kind=="earned"){s.Columns[0].Kind="earning";q.Setup.SourceColumnRoles[0]="info";}
        if(kind=="blank")cell.Value="";if(kind=="missing")cell.MissingCachedValue=true;if(kind=="error")cell.Error="#VALUE!";
        if(kind=="precision")cell.Value="10000.0001";
        Assert.Throws<InvalidOperationException>(()=>ExcelPayrollSetupRepository.ReadInitialSourceTarget(q,s,r,10,kind=="mismatch"?120001:120000,kind=="stale"?new('b',64):SourceHash));
    }
    [Theory]
    [InlineData("default")][InlineData("missing")][InlineData("false")][InlineData("version")][InlineData("meaning")][InlineData("joining")][InlineData("column")]
    public void Initial_target_is_never_an_automatic_fallback_or_an_unacknowledged_claim(string kind)
    {
        var(q,_,_)=Fixture();
        if(kind=="default")q.SalaryTargetMode="PreserveEffectiveHistoricalTarget";if(kind=="missing")q.InitialSourceTargetReview=null;
        if(kind=="false")q.InitialSourceTargetReview!.Reviewed=false;if(kind=="version")q.InitialSourceTargetReview!.Version=0;
        if(kind=="meaning")q.InitialSourceTargetReview!.Meaning="Source rate";if(kind=="joining")q.InitialSourceTargetReview!.ActualJoiningDateNotEstablished=false;
        if(kind=="column")q.Setup.MonthlyCtcColumnIndex=1;
        Assert.Throws<InvalidOperationException>(()=>ExcelPayrollSetupRepository.ValidateInitialSourceTargetReview(q));
    }
    [Theory]
    [InlineData("Active")][InlineData("Historical")][InlineData("Superseded")]
    public void A_prior_or_overlapping_history_is_not_an_initial_salary_gap(string status)
    {
        var prior=new EmployeeRepository.ClosedSalaryHistoryRow{Id=1,EmployeeId=10,ClientId=1,Status=status,EffectiveFrom=new(2026,8,1),EffectiveTo=new(2026,8,31),AnnualCtc=120000};
        var state=new EmployeeRepository.ClosedSalaryHistoryState(1,"current",[prior],new Dictionary<long,string>{{1,"prior"}});
        Assert.Throws<InvalidOperationException>(()=>EmployeeRepository.RequireClosedInitialSalaryWindow(state,10,new(2026,9,1),new(2026,9,30),120000,120000));
    }
    [Fact]
    public void Default_request_and_receipt_JSON_keep_their_old_hash_shape()
    {
        var json=new JsonSerializerOptions(JsonSerializerDefaults.Web);var q=new HistoricalPeriodSalaryRequest();
        Assert.Equal(JsonSerializer.Serialize(new{q.Setup,q.Assignments,q.SalaryTargetMode},json),JsonSerializer.Serialize(q,json));
        var receipt=new HistoricalPeriodSalaryReceipt();
        Assert.Equal(JsonSerializer.Serialize(new{receipt.ClientId,receipt.BatchId,receipt.PayPeriod,receipt.RequestId,receipt.Fingerprint,receipt.AppliedAtUtc,receipt.Replayed,receipt.Rows},json),JsonSerializer.Serialize(receipt,json));
        Assert.DoesNotContain("initialSource",JsonSerializer.Serialize(new HistoricalPeriodSalaryPreviewRow(),json));
    }

    [Fact]
    public void Only_the_exact_selected_literal_CTC_reference_can_bind_the_monthly_target_token()
    {
        var(q,s,r)=Fixture();q.Setup.ReviewedFixedInputs=true;
        var component=new ExcelPayrollSetupComponent{Code="BASIC",CalculationType="Formula",Formula="MONTHLY_CTC",ExpectedColumnIndex=1,ProRata=false};q.Setup.Components=[component];
        s.ColumnCount=3;s.Columns.Add(new(){ColumnIndex=1,SourceHeader="Basic",Label="Basic",Kind="earning"});
        s.Columns.Add(new(){ColumnIndex=2,SourceHeader="Last month salary",Label="Last month salary",Kind="info"});
        s.Rows[0].Cells.Add(new(){Value="10000",FormulaText="A2"});s.Rows[0].Cells.Add(new(){Value="10000"});
        var target=ExcelPayrollSetupRepository.ReadInitialSourceTarget(q,s,r,10,120000,SourceHash);
        var boundary=ExcelPayrollSetupRepository.HistoricalInitialSourceBoundary(q,target);
        var before=JsonSerializer.Serialize(new{q,s});
        Assert.Equal("Blocked",ExcelPayrollSetupRepository.InspectTransferFormula(q.Setup,s,r,component).Status);
        Assert.Equal("Exact supported translation",ExcelPayrollSetupRepository.InspectTransferFormula(q.Setup,s,r,component,boundary).Status);
        Assert.Equal(before,JsonSerializer.Serialize(new{q,s}));
        s.Rows[0].Cells[1]!.FormulaText="C2";
        Assert.Equal("Blocked",ExcelPayrollSetupRepository.InspectTransferFormula(q.Setup,s,r,component,boundary).Status);
        s.Rows[0].Cells[1]!.FormulaText="A2";s.Rows[0].Cells[0]!.Value="10001";
        Assert.Equal("Blocked",ExcelPayrollSetupRepository.InspectTransferFormula(q.Setup,s,r,component,boundary).Status);
        s.Rows[0].Cells[0]!.Value="10000";s.Rows[0].Cells[0]!.FormulaText="C2";
        Assert.Equal("Blocked",ExcelPayrollSetupRepository.InspectTransferFormula(q.Setup,s,r,component,boundary).Status);
        q.SalaryTargetMode="PreserveEffectiveHistoricalTarget";
        Assert.Null(ExcelPayrollSetupRepository.HistoricalInitialSourceBoundary(q,target));
    }
}
