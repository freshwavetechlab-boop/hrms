using System.Globalization;
using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayrollTransferRoundingTests
{
    private static (ExcelPayrollSetupRepository.Plan Plan, ExcelPayrollSetupRequest Request) Fixture()
    {
        // Anonymous source-backed arithmetic pattern. No DB, real employees or external calls.
        var request = new ExcelPayrollSetupRequest
        {
            BatchId = new string('a',32), RowIds = ["source"], TemplateName = "Synthetic reviewed variant", EffectiveFrom = "2026-09-01",
            TransferReviewVersion = 1, ReviewedPolicyScope = true, ImportAttendance = false,
            PayrollDaysColumnIndex = 0, PayableDaysColumnIndex = 1,
            TemplateDecisions = [new() { VariantKey = "1000", CreateVariant = true }],
            Components = [
                new() { Key="base", Code="BASIC", SourceColumnIndex=2, ExpectedColumnIndex=3, ProRata=true },
                new() { Key="pf", Code="PF", Category="Deduction", CalculationType="Formula", Formula="BASIC_EARNED*12%", ExpectedColumnIndex=4, ProRata=false, Priority=20 },
                new() { Key="esi", Code="ESI", Category="Deduction", CalculationType="Formula", Formula="BASIC_EARNED*0.75%", ExpectedColumnIndex=5, ProRata=false, Priority=30 },
                new() { Key="pf-er", Code="PF_ER", Category="Benefit", CalculationType="Formula", Formula="BASIC_EARNED*13%", ExpectedColumnIndex=6, ProRata=false, Priority=40 },
                new() { Key="esi-er", Code="ESI_ER", Category="Benefit", CalculationType="Formula", Formula="BASIC_EARNED*3.25%", ExpectedColumnIndex=7, ProRata=false, Priority=50 }
            ]
        };
        string[] values=["30","27","15414","13872.599999999999","1664.7119999999998","104.04449999999999","1803.4379999999999","450.85949999999997"];
        string?[] formulas=[null,null,null,"C6/A6*B6","D6*12%","D6*0.75%","D6*13%","D6*3.25%"];
        var source=new ExcelPayslipCalculationSource {SheetName="Salary",ColumnCount=8,
            Columns=Enumerable.Range(0,8).Select(i=>new ExcelPayslipCalculationColumn {ColumnIndex=i,Kind=i==3?"earning":i is 4 or 5?"deduction":i is 6 or 7?"employer":"info"}).ToList(),
            Rows=[new(){SourceRow=6,Cells=values.Select((v,i)=>(ExcelPayslipCalculationCell?)new ExcelPayslipCalculationCell {Value=v,FormulaText=formulas[i]}).ToList()}]};
        decimal N(int i)=>decimal.Parse(values[i],CultureInfo.InvariantCulture);
        var saved=new ExcelPayslipRow {Id="source",SourceRow=6,EmployeeCode="SYN-1",NetPay=12103.8435m,
            Earnings=[new(){Label="Wage",Amount=N(3)}],Deductions=[new(){Label="PF",Amount=N(4)},new(){Label="ESI",Amount=N(5)}],
            EmployerContributions=[new(){Label="Employer PF",Amount=N(6)},new(){Label="Employer ESI",Amount=N(7)}]};
        var plan=new ExcelPayrollSetupRepository.Plan {Batch=new(){Id=request.BatchId,ClientId=42,Month="2026-09",Rows=[saved]},Source=source};
        plan.Components.AddRange(request.Components.Select((c,i)=>new ExcelPayrollSetupRepository.ResolvedComponent(c,(i+1).ToString(),false)));
        plan.Rows.Add(new(){RowId="source",SourceRow=6,Employee=new(){Id=10},TemplateId="1000",Components=plan.Components});
        Recalculate(plan,request);
        return(plan,request);
    }
    private static void Recalculate(ExcelPayrollSetupRepository.Plan plan,ExcelPayrollSetupRequest request)
    {
        var row=plan.Rows[0]; row.Preview=ExcelPayrollSetupRepository.CalculateRow(42,request,row.Components,plan.Source.Rows[0],plan.Batch.Rows[0]).Preview;
        plan.Preview.Rows=[row.Preview];
    }
    private static ExcelPayrollTransferAmountReview Classify(ExcelPayrollSetupRepository.Plan p,ExcelPayrollSetupRequest q)
    {
        var proofs=p.Rows[0].Components.Select(c=>ExcelPayrollSetupRepository.InspectTransferFormula(q,p.Source,p.Batch.Rows[0],c.Definition)).ToList();
        return ExcelPayrollSetupRepository.ClassifyTransferAmounts(p,p.Rows[0],q,new string('b',64),proofs);
    }
    [Fact] public void ActualCoreTraceExplainsComponentVersusAggregateOnePaisaWithoutChangingOutputs()
    {
        var(p,q)=Fixture(); var before=JsonSerializer.Serialize(new {p.Source,p.Batch,p.Rows[0].Preview});
        var proof=Classify(p,q);
        Assert.Equal("Explained rounding",proof.Status);Assert.Equal(5,proof.Lines.Count);Assert.Equal(64,proof.ProofHash.Length);
        Assert.Equal(12103.84m,proof.SourceNetDisplay);Assert.Equal(12103.85m,proof.CoreNet);Assert.Equal(0.0065m,proof.RawNetDelta);Assert.Equal(0.01m,proof.DisplayNetDelta);
        Assert.Equal(1803.44m,proof.Lines.Single(l=>l.Code=="PF_ER").Calculated);Assert.Equal(450.86m,proof.Lines.Single(l=>l.Code=="ESI_ER").Calculated);
        Assert.All(proof.Lines,l=>Assert.Equal(l.SourceDisplay,l.Calculated));
        Assert.Equal(before,JsonSerializer.Serialize(new {p.Source,p.Batch,p.Rows[0].Preview}));
    }
    [Theory] [InlineData("deduction",0.001)] [InlineData("employer",0.001)] [InlineData("employer",100)] [InlineData("net",0.001)]
    public void UnexplainedSmallOrLargeDifferencesStillBlock(string field,decimal delta)
    {
        var(p,q)=Fixture();var saved=p.Batch.Rows[0];
        if(field=="net") saved.NetPay+=delta;
        else {
            var index=field=="deduction"?4:6;var cell=p.Source.Rows[0].Cells[index]!;cell.Value=(decimal.Parse(cell.Value,CultureInfo.InvariantCulture)+delta).ToString(CultureInfo.InvariantCulture);
            if(field=="deduction"){saved.Deductions[0].Amount+=delta;saved.NetPay-=delta;}else saved.EmployerContributions[0].Amount+=delta;
        }
        Recalculate(p,q);Assert.Equal("Mismatch",Classify(p,q).Status);
    }
    [Theory] [InlineData("trace")] [InlineData("category")] [InlineData("role")] [InlineData("formula")] [InlineData("duplicate")]
    public void MissingTraceClassificationFormulaAndCoverageCannotReceiveRoundingApproval(string change)
    {
        var(p,q)=Fixture();
        if(change=="trace")p.Rows[0].Preview.Lines[0]=p.Rows[0].Preview.Lines[0] with {UnroundedEarned=null};
        if(change=="category")p.Rows[0].Preview.Lines[0]=p.Rows[0].Preview.Lines[0] with {Category="Deduction"};
        if(change=="role")p.Source.Columns[3].Kind="deduction";
        if(change=="formula")p.Source.Rows[0].Cells[4]!.FormulaText="D6*11%";
        if(change=="duplicate")p.Rows[0].Preview.Lines.Add(p.Rows[0].Preview.Lines[0]);
        Assert.Equal("Unverified",Classify(p,q).Status);
    }
    [Theory] [InlineData("core totals")] [InlineData("source totals")] [InlineData("extra source component")]
    public void InconsistentTotalsAndMissingCoverageCannotBeForcedThrough(string change)
    {
        var(p,q)=Fixture();
        if(change=="core totals")p.Rows[0].Preview.Benefits+=0.001m;
        if(change=="source totals")p.Batch.Rows[0].DeclaredGross=13872.61m;
        if(change=="extra source component")p.Batch.Rows[0].EmployerContributions.Add(new(){Label="Unmapped",Amount=0});
        Assert.Equal("Mismatch",Classify(p,q).Status);
    }
    [Fact] public void MatchingCurrencyWithDifferentPreRoundAmountIsNotEvidence()
    {
        var(p,q)=Fixture();var line=p.Rows[0].Preview.Lines[1];
        p.Rows[0].Preview.Lines[1]=line with {UnroundedEarned=line.UnroundedEarned+0.001m};
        Assert.Equal(line.CalculatedAmount,decimal.Round(p.Rows[0].Preview.Lines[1].UnroundedEarned!.Value,2,MidpointRounding.ToEven));
        Assert.Equal("Mismatch",Classify(p,q).Status);
    }
    [Fact] public void ReviewedFingerprintIsStableAcrossFreshRebuildAndCannotClearOtherBlockers()
    {
        var(p,q)=Fixture();ExcelPayrollSetupRepository.FinalizeTransferReview(p,q,42,[]);
        var first=Assert.Single(p.Preview.Transfer!.AmountComparisons);Assert.Equal("Explained rounding",first.Status);Assert.False(first.Acknowledged);Assert.False(p.Preview.CanApply);
        var(fresh,reviewed)=Fixture();reviewed.RoundingReviews["source"]=first.ProofHash;
        ExcelPayrollSetupRepository.FinalizeTransferReview(fresh,reviewed,42,[]);
        var replay=Assert.Single(fresh.Preview.Transfer!.AmountComparisons);Assert.Equal(first.ProofHash,replay.ProofHash);Assert.True(replay.Acknowledged);Assert.True(fresh.Preview.CanApply);
        var(blocked,request)=Fixture();request.RoundingReviews["source"]=first.ProofHash;request.TemplateDecisions.Clear();
        ExcelPayrollSetupRepository.FinalizeTransferReview(blocked,request,42,[]);Assert.False(blocked.Preview.CanApply);Assert.Contains(blocked.Preview.Issues,i=>i.Message.Contains("Review reuse/create"));
        var(changed,stale)=Fixture();stale.RoundingReviews["source"]=first.ProofHash;stale.EffectiveFrom="2026-10-01";
        ExcelPayrollSetupRepository.FinalizeTransferReview(changed,stale,42,[]);Assert.False(Assert.Single(changed.Preview.Transfer!.AmountComparisons).Acknowledged);Assert.False(changed.Preview.CanApply);
    }
    [Fact] public void ForgedUnselectedOrUnboundedReviewRequestsAreRejected()
    {
        var(_,q)=Fixture();q.RoundingReviews["not selected"]=new string('b',64);Assert.Throws<InvalidOperationException>(()=>ExcelPayrollSetupRepository.ValidateTransferRequest(q));
        q.RoundingReviews.Clear();q.RoundingReviews["source"]="not a fingerprint";Assert.Throws<InvalidOperationException>(()=>ExcelPayrollSetupRepository.ValidateTransferRequest(q));
    }
    [Theory] [InlineData(115.605,115.61)] [InlineData(450.85949999999997,450.86)] [InlineData(13872.599999999999,13872.60)]
    public void SourceDisplayUsesExistingFormatterBoundaryNotAComparisonTolerance(decimal raw,decimal display)
        =>Assert.Equal(display,ExcelPayrollSetupRepository.SourceCurrencyDisplay(raw));
}
