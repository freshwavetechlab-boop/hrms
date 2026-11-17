using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

// Reuses only the already isolated 97726 fixture and its cleanup/ownership guard.
public sealed partial class HistoricalProfessionalTaxDatabaseTests
{
    private async Task<HistoricalPeriodSalaryRequest> BoundedRequestAsync(bool aggregateOnly=false,decimal delta=.25m)
    {
        F.Config["Payroll:HistoricalAmountDifferenceReviewEnabled"]="true";
        var row=batch.Rows[0];var captured=source.Rows[0];
        if(!aggregateOnly){row.Deductions[0].Amount=1200+delta;row.DeclaredDeductions=1200+delta;captured.Cells[7]!.Value=(1200+delta).ToString(CultureInfo.InvariantCulture);}
        row.NetPay=8800-delta;captured.Cells[9]!.Value=row.NetPay.ToString(CultureInfo.InvariantCulture);
        await SaveSourceAsync();var q=await PtRequestAsync();
        q.AmountDifferenceReview=new(){Version=1,Reviewed=true,SourceHash=q.Setup.AttendanceReview!.SourceHash,MaxAbsoluteDifference=1,
            Reason="Explicit reviewed bounded monetary/cache difference; preserve all source and native financial values."};return q;
    }
    private async Task<HistoricalPeriodSalaryPreview> ReadyBoundedAsync(HistoricalPeriodSalaryRequest q)
    {
        var first=await Repository.PreviewHistoricalSalaryAsync(Client,q,Actor);Assert.False(first.CanApply);
        var amount=Assert.Single(first.Rows).AmountReview!;Assert.True(amount.Status==ExcelPayrollSetupRepository.BoundedHistoricalDifferenceStatus,string.Join(";",first.Blockers));Assert.Equal(64,amount.ProofHash.Length);Assert.False(amount.Acknowledged);
        q.Setup.RoundingReviews[first.Rows[0].RowId]=amount.ProofHash;
        var ready=await Repository.PreviewHistoricalSalaryAsync(Client,q,Actor);Assert.True(ready.CanApply,string.Join(";",ready.Blockers));Assert.True(ready.Rows[0].AmountReview!.Acknowledged);return ready;
    }
    [IsolatedTheory][InlineData(false,false)][InlineData(false,true)][InlineData(true,false)][InlineData(true,true)]
    public async Task Bounded_historical_atomic_PT_save_preserves_values_complete_receipt_and_stable_replay(bool batchMode,bool aggregateOnly)
    {
        F.Config["Payroll:HistoricalBatchProofEnabled"]=batchMode.ToString();var q=await BoundedRequestAsync(aggregateOnly);var before=await WitnessAsync();var ready=await ReadyBoundedAsync(q);
        var id=Guid.NewGuid().ToString("N");var saved=await ApplyAsync(ready,id);Assert.Equal(1,await WrittenAsync());Assert.Equal(before,await WitnessAsync());
        var variance=Assert.Single(saved.AmountDifferences!);Assert.Equal(One,variance.EmployeeId);Assert.Equal("Reviewed bounded monetary difference",variance.Review.Status);Assert.True(variance.Review.Acknowledged);
        Assert.Equal(8799.75m,variance.Review.SourceNetRaw);Assert.Equal(8800m,variance.Review.CoreNet);Assert.Equal(4,variance.Review.BoundedDifference!.Totals.Count);
        Assert.Equal(aggregateOnly?-.25m:0,variance.Review.BoundedDifference.SourceAggregates.Single(v=>v.Kind=="Net").RawDelta);
        var actual=await NativeAsync();Assert.True(actual.Complete);Assert.Equal(10000,actual.Gross);Assert.Equal(1200,actual.Deductions);Assert.Equal(1300,actual.Benefits);Assert.Equal(8800,actual.Net);
        Assert.False(Assert.Single(saved.ProfessionalTax!).ProposedBasisWhatIf);
        await using(var db=await F.OpenAsync())
        {
            var envelope=JsonNode.Parse((await db.QuerySingleAsync<string>("SELECT CAST(SettingsJson AS CHAR) FROM modulesettings WHERE client_id=@Client AND ModuleCode=@Code",new{Client,Code="closed_salary_receipt:"+Client+":"+id})))!;
            var protector=new PortableIntegrationCredentialProtector(F.Config,new EphemeralDataProtectionProvider(),NullLogger<PortableIntegrationCredentialProtector>.Instance);
            var stored=JsonNode.Parse(protector.UnprotectHistoricalSalary(envelope["payload"]!.ToString())!)!;
            Assert.Equal(1,stored["reviewedRequest"]!["amountDifferenceReview"]!["maxAbsoluteDifference"]!.GetValue<decimal>());
            Assert.Equal(variance.Review.ProofHash,stored["receipt"]!["amountDifferences"]![0]!["review"]!["proofHash"]!.ToString());
        }
        F.Config["Payroll:HistoricalAmountDifferenceReviewEnabled"]="false";
        var replay=await ApplyAsync(ready,id);Assert.True(replay.Replayed);Assert.Equal(saved.Rows,replay.Rows);Assert.Equal(variance.Review.ProofHash,Assert.Single(replay.AmountDifferences!).Review.ProofHash);Assert.Equal(1,await WrittenAsync());
    }
    [IsolatedFact]
    public async Task Bounded_historical_feature_is_default_off_and_old_metadata_is_not_silently_reinterpreted()
    {
        var q=await BoundedRequestAsync();F.Config["Payroll:HistoricalAmountDifferenceReviewEnabled"]=null;var before=await WitnessAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Repository.PreviewHistoricalSalaryAsync(Client,q,Actor));Assert.Equal(0,await WrittenAsync());Assert.Equal(before,await WitnessAsync());
    }
    [IsolatedFact]
    public async Task Bounded_historical_initial_target_composes_with_explicit_source_budget_and_preserves_current_future_basis()
    {
        F.Config["Payroll:HistoricalBatchProofEnabled"]="true";
        await using(var db=await F.OpenAsync())await db.ExecuteAsync("DELETE FROM employee_it0008_basic_pay WHERE ClientId=@Client AND EmployeeId=@One AND ActionType='Legacy salary';UPDATE employees SET AnnualCtc=120000,DateOfJoining='' WHERE Id=@One AND ClientId=@Client",new{Client,One});
        source.ColumnCount=11;source.Columns.Add(new(){ColumnIndex=10,SourceHeader="Current CTC",Label="Current CTC",Kind="info"});
        foreach(var captured in source.Rows)captured.Cells.Add(new(){Value="10000",DataType="Number"});
        foreach(var row in batch.Rows)row.Information.Add(new(){Label="Current CTC",Value="10000"});
        var q=await BoundedRequestAsync();q.SalaryTargetMode="ReviewedInitialSourceTarget";q.Setup.MonthlyCtcColumnIndex=10;
        q.InitialSourceTargetReview=new(){Version=1,Reviewed=true,MonthlyCtcColumnIndex=10,SourceHash=q.AmountDifferenceReview!.SourceHash,Meaning="MonthlyPayrollEarningsBudget",ActualJoiningDateNotEstablished=true};
        var before=await WitnessAsync();var ready=await ReadyBoundedAsync(q);var receipt=await ApplyAsync(ready);
        Assert.Equal(120000,Assert.Single(receipt.InitialSourceTargets!).AnnualSalaryTarget);Assert.True(receipt.InitialSourceTargets![0].ActualJoiningDateNotEstablished);Assert.Single(receipt.AmountDifferences!);
        Assert.Equal(before,await WitnessAsync());Assert.Equal(8800,(await NativeAsync()).Net);Assert.False(Assert.Single(receipt.ProfessionalTax!).ProposedBasisWhatIf);
    }
    [IsolatedTheory][InlineData("lineAboveOne")][InlineData("netAboveOne")][InlineData("identity")][InlineData("unknownActual")][InlineData("unsupportedFormula")]
    public async Task Bounded_historical_review_never_waives_large_or_nonmonetary_gaps(string gap)
    {
        var q=await BoundedRequestAsync(gap=="netAboveOne",gap is "lineAboveOne" or "netAboveOne"?1.01m:.25m);
        if(gap=="identity"){source.Rows[0].Cells[2]!.Value="";batch.Rows[0].Information.First(v=>v.Label=="UAN").Value="";await SaveSourceAsync();q=await PtRequestAsync();q.AmountDifferenceReview=new(){Version=1,Reviewed=true,SourceHash=q.Setup.AttendanceReview!.SourceHash,MaxAbsoluteDifference=1,Reason="Review monetary differences while retaining strict identity checks."};}
        if(gap=="unknownActual"){await using var db=await F.OpenAsync();await db.ExecuteAsync("UPDATE employee_monthly_attendance SET present_days=NULL WHERE client_id=@Client AND employee_id=@One AND attendance_month='2026-09'",new{Client,One});}
        if(gap=="unsupportedFormula"){source.Rows[0].Cells[7]!.FormulaText="UNSUPPORTED(G2)";await SaveSourceAsync();q=await PtRequestAsync();q.AmountDifferenceReview=new(){Version=1,Reviewed=true,SourceHash=q.Setup.AttendanceReview!.SourceHash,MaxAbsoluteDifference=1,Reason="Review only bounded money with every source formula still supported."};}
        var before=await WitnessAsync();
        if(gap=="unknownActual")await Assert.ThrowsAsync<InvalidOperationException>(()=>Repository.PreviewHistoricalSalaryAsync(Client,q,Actor));
        else{var preview=await Repository.PreviewHistoricalSalaryAsync(Client,q,Actor);Assert.False(preview.CanApply);Assert.Empty(preview.ReviewToken);}
        Assert.Equal(0,await WrittenAsync());Assert.Equal(before,await WitnessAsync());
    }
    [IsolatedFact]
    public async Task Bounded_historical_late_saved_native_failure_rolls_back_review_receipt_history_PT_and_audit()
    {
        var ready=await ReadyBoundedAsync(await BoundedRequestAsync());var before=await WitnessAsync();await using var db=await F.OpenAsync();
        const string trigger="codex_historical_bounded_97726_late";
        await db.ExecuteAsync("CREATE TRIGGER "+trigger+" AFTER INSERT ON modulesettings FOR EACH ROW BEGIN IF NEW.client_id=97726 AND NEW.ModuleCode LIKE 'closed_salary_receipt:%' THEN UPDATE employee_monthly_attendance SET payable_days=29 WHERE client_id=97726 AND employee_id=9772601 AND attendance_month='2026-09'; END IF; END");
        try{await Assert.ThrowsAsync<InvalidOperationException>(()=>ApplyAsync(ready));Assert.Equal(0,await WrittenAsync());Assert.Equal(before,await WitnessAsync());Assert.Equal(0,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM modulesettings WHERE client_id=@Client",new{Client}));}
        finally{await db.ExecuteAsync("DROP TRIGGER IF EXISTS "+trigger);}
    }
}
