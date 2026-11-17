using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Configuration;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed partial class HistoricalProfessionalTaxDatabaseTests
{
    private ExcelPayrollSetupRepository BatchRepository
    {
        get
        {
            var config=new ConfigurationBuilder().AddConfiguration(F.Config).AddInMemoryCollection(new Dictionary<string,string?>{{"Payroll:HistoricalBatchProofEnabled","true"}}).Build();
            return new(config,new EmployeeRepository(config,null!,null!),new PayRunRepository(config,new TaxEngineRepository(config)));
        }
    }
    private HistoricalSalarySimulationAssignment ContextAssignment(HistoricalPeriodSalaryRequest request,int id,bool pt=true)
    {
        var row=id==One?"hist-row-2":"hist-row-3";
        var control=pt?new HistoricalConditionalProfessionalTax(row,request.PeriodProfessionalTaxReview!.SourceHash,request.PeriodProfessionalTaxReview.Reason.Trim(),Actor.Id,Actor.Email,request):null;
        return new(Client,id,"2026-09",Template.ToString(),120000,new Dictionary<string,decimal>{{"9772601",10000}},control);
    }
    private static string ComparablePreview(HistoricalPeriodSalaryPreview value)
    {
        var clone=JsonSerializer.SerializeToNode(value,Json)!.AsObject();clone.Remove("reviewToken");clone.Remove("expiresAtUtc");return clone.ToJsonString();
    }

    [IsolatedTheory]
    [InlineData(false)][InlineData(true)]
    public async Task Historical_context_matches_every_singleton_json_trace_and_input_fingerprint(bool proposed)
    {
        var request=await PtRequestAsync(true);var assignments=new[]{ContextAssignment(request,One),ContextAssignment(request,Two)};
        await using var db=await F.OpenAsync();await using var tx=await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,isReadOnly:true);
        var baseline=new Dictionary<int,HistoricalSalarySimulationResult>();
        foreach(var id in new[]{One,Two})baseline[id]=await F.Runs.SimulateHistoricalSalaryInTransactionAsync(db,tx,new(){Mode="Employee",ClientId=Client,EmployeeId=id,PayPeriod="2026-09"},proposed?assignments.Single(a=>a.EmployeeId==id):null);
        var context=await F.Runs.CreateHistoricalSimulationContextAsync(db,tx,Client,"2026-09",[One,Two],proposed?assignments:null);
        foreach(var id in new[]{One,Two})
        {
            var actual=await F.Runs.SimulateHistoricalSalaryInTransactionAsync(db,tx,new(){Mode="Employee",ClientId=Client,EmployeeId=id,PayPeriod="2026-09"},proposed?assignments.Single(a=>a.EmployeeId==id):null,context:context);
            Assert.Equal(JsonSerializer.Serialize(baseline[id],Json),JsonSerializer.Serialize(actual,Json));
            Assert.Equal(baseline[id].InputFingerprint,actual.InputFingerprint);
        }
        await tx.RollbackAsync();Assert.Equal(0,await WrittenAsync());
    }

    [IsolatedTheory]
    [InlineData("missingAttendance")][InlineData("wrongEmployee")][InlineData("invalidEmployment")]
    public async Task Batch_context_preserves_per_row_missing_and_invalid_outcomes(string mode)
    {
        var request=await PtRequestAsync(true);
        await using var db=await F.OpenAsync();
        if(mode=="missingAttendance")await db.ExecuteAsync("DELETE FROM employee_monthly_attendance WHERE client_id=@Client AND employee_id=@Two AND attendance_month='2026-09'",new{Client,Two});
        if(mode=="invalidEmployment")await db.ExecuteAsync("UPDATE employees SET DateOfJoining='2026-09-25' WHERE Id=@Two",new{Two});
        var ids=mode=="wrongEmployee"?new[]{One,Two+999}:new[]{One,Two};
        await using var tx=await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,isReadOnly:true);
        var context=await F.Runs.CreateHistoricalSimulationContextAsync(db,tx,Client,"2026-09",ids);
        foreach(var id in ids)
        {
            var input=new PayrollSimulationRequest{Mode="Employee",ClientId=Client,EmployeeId=id,PayPeriod="2026-09"};
            HistoricalSalarySimulationResult? expected=null;Exception? error=null;
            try{expected=await F.Runs.SimulateHistoricalSalaryInTransactionAsync(db,tx,input,null);}catch(InvalidOperationException caught){error=caught;}
            if(error is not null){var caught=await Assert.ThrowsAsync<InvalidOperationException>(()=>F.Runs.SimulateHistoricalSalaryInTransactionAsync(db,tx,input,null,context:context));Assert.Equal(error.Message,caught.Message);}
            else{var actual=await F.Runs.SimulateHistoricalSalaryInTransactionAsync(db,tx,input,null,context:context);Assert.Equal(JsonSerializer.Serialize(expected,Json),JsonSerializer.Serialize(actual,Json));}
        }
        await tx.RollbackAsync();
    }

    [IsolatedTheory]
    [InlineData(false)][InlineData(true)]
    public async Task Default_and_batched_full_historical_previews_have_identical_fingerprints_and_rows(bool withPt)
    {
        var request=withPt?await PtRequestAsync(true):Request(true);
        var old=await Repository.PreviewHistoricalSalaryAsync(Client,request,Actor);
        var batch=await BatchRepository.PreviewHistoricalSalaryAsync(Client,request,Actor);
        Assert.Equal(ComparablePreview(old),ComparablePreview(batch));
        Assert.Equal(old.Fingerprint,batch.Fingerprint);Assert.Equal(0,await WrittenAsync());
    }

    [IsolatedFact]
    public async Task Batched_atomic_apply_builds_fresh_actual_control_and_saved_contexts_and_replays()
    {
        var request=await PtRequestAsync(true);var old=await Repository.PreviewHistoricalSalaryAsync(Client,request,Actor);
        var preview=await BatchRepository.PreviewHistoricalSalaryAsync(Client,request,Actor);
        Assert.True(preview.CanApply,string.Join(";",preview.Blockers));Assert.Equal(old.Fingerprint,preview.Fingerprint);
        var before=await WitnessAsync();var id=Guid.NewGuid().ToString("N");
        var command=new HistoricalPeriodSalaryApplyRequest{ReviewToken=preview.ReviewToken,RequestId=id,Acknowledged=true};
        var receipt=await BatchRepository.ApplyHistoricalSalaryAsync(Client,command,Actor);
        Assert.Equal(2,receipt.Rows.Count);Assert.Equal(2,receipt.ProfessionalTax!.Count);Assert.All(receipt.ProfessionalTax,p=>Assert.False(p.ProposedBasisWhatIf));
        Assert.Equal(before,await WitnessAsync());Assert.True((await BatchRepository.ApplyHistoricalSalaryAsync(Client,command,Actor)).Replayed);
        await using var db=await F.OpenAsync();await using var tx=await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,isReadOnly:true);
        var context=await F.Runs.CreateHistoricalSimulationContextAsync(db,tx,Client,"2026-09",[One,Two]);
        foreach(var employeeId in new[]{One,Two})
        {
            var input=new PayrollSimulationRequest{Mode="Employee",ClientId=Client,EmployeeId=employeeId,PayPeriod="2026-09"};
            var expected=await F.Runs.SimulateHistoricalSalaryInTransactionAsync(db,tx,input,null);
            var actual=await F.Runs.SimulateHistoricalSalaryInTransactionAsync(db,tx,input,null,context:context);
            Assert.Equal(JsonSerializer.Serialize(expected,Json),JsonSerializer.Serialize(actual,Json));
        }
        await tx.RollbackAsync();
    }

    [IsolatedFact]
    public async Task Historical_context_cannot_cross_phase_period_assignment_or_repeat_a_row()
    {
        var request=await PtRequestAsync();var assignment=ContextAssignment(request,One);
        await using var db=await F.OpenAsync();await using var tx=await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,isReadOnly:true);
        var context=await F.Runs.CreateHistoricalSimulationContextAsync(db,tx,Client,"2026-09",[One],[assignment]);
        var input=new PayrollSimulationRequest{Mode="Employee",ClientId=Client,EmployeeId=One,PayPeriod="2026-09"};
        await Assert.ThrowsAsync<InvalidOperationException>(()=>F.Runs.SimulateHistoricalSalaryInTransactionAsync(db,tx,input,null,context:context));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>F.Runs.SimulateHistoricalSalaryInTransactionAsync(db,tx,input,assignment with{AnnualSalaryTarget=120001},context:context));
        var first=await F.Runs.SimulateHistoricalSalaryInTransactionAsync(db,tx,input,assignment,context:context);Assert.True(first.Simulation.Complete);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>F.Runs.SimulateHistoricalSalaryInTransactionAsync(db,tx,input,assignment,context:context));
        await tx.RollbackAsync();
    }

    [IsolatedFact]
    public async Task Batched_late_final_guard_failure_rolls_back_all_history_controls_and_receipt()
    {
        var preview=await BatchRepository.PreviewHistoricalSalaryAsync(Client,await PtRequestAsync(true),Actor);Assert.True(preview.CanApply,string.Join(";",preview.Blockers));
        var before=await WitnessAsync();await using var db=await F.OpenAsync();
        await db.ExecuteAsync("CREATE TRIGGER codex_context_97726_late AFTER INSERT ON modulesettings FOR EACH ROW BEGIN IF NEW.client_id=97726 AND NEW.ModuleCode LIKE 'closed_salary_receipt:%' THEN UPDATE employee_monthly_attendance SET payable_days=29 WHERE client_id=97726 AND employee_id=9772602 AND attendance_month='2026-09'; END IF; END");
        try{await Assert.ThrowsAsync<InvalidOperationException>(()=>BatchRepository.ApplyHistoricalSalaryAsync(Client,new(){ReviewToken=preview.ReviewToken,RequestId=Guid.NewGuid().ToString("N"),Acknowledged=true},Actor));Assert.Equal(0,await WrittenAsync());Assert.Equal(before,await WitnessAsync());Assert.Equal(0,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM modulesettings WHERE client_id=@Client",new{Client}));}
        finally{await db.ExecuteAsync("DROP TRIGGER IF EXISTS codex_context_97726_late");}
    }

    [IsolatedTheory]
    [InlineData(false)][InlineData(true)]
    public async Task Context_dated_PF_uses_the_proposed_template_before_loading_its_policy(bool mixedInvalid)
    {
        await using var db=await F.OpenAsync();
        await db.ExecuteAsync("UPDATE salarycomponents SET StatutoryType='PF Employee' WHERE Id=9772602");
        PfPolicyVersion Policy(string template,decimal rate)=>new(){Id="context-pf-"+template,VersionNumber=1,ClientId=Client,
            SalaryStructureId=template,Name="Owned context test",EffectiveFrom=new(2026,1,1),Status="Published",
            BaseComponentCode="H_WAGES_97726",CeilingBasis="PayableDays",
            Contributions=[new(){ComponentId="9772602",ComponentCode="H_DEDUCTION_97726",StatutoryType="PF Employee",RatePercent=rate}]};
        var policies=new List<PfPolicyVersion>{Policy("9772609",5),Policy(Template.ToString(),10)};
        // An invalid sibling policy must retain the singleton's precise outcome,
        // while the valid proposed row still uses its own selected template.
        if(mixedInvalid)policies.Add(Policy("9772609",7));
        await db.ExecuteAsync("INSERT INTO modulesettings(client_id,ModuleCode,SettingsJson) VALUES(@Client,'payroll',JSON_OBJECT('pfPolicyVersions',CAST(@json AS JSON)))",new{Client,json=JsonSerializer.Serialize(policies,Json)});
        var request=Request(true);var assignments=new[]{ContextAssignment(request,One,false),ContextAssignment(request,Two,false) with{TemplateId=mixedInvalid?"9772609":Template.ToString()}};
        await using var tx=await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,isReadOnly:true);
        var context=await F.Runs.CreateHistoricalSimulationContextAsync(db,tx,Client,"2026-09",[One,Two],assignments);
        foreach(var assignment in assignments)
        {
            var input=new PayrollSimulationRequest{Mode="Employee",ClientId=Client,EmployeeId=assignment.EmployeeId,PayPeriod="2026-09"};
            var expected=await F.Runs.SimulateHistoricalSalaryInTransactionAsync(db,tx,input,assignment);
            var actual=await F.Runs.SimulateHistoricalSalaryInTransactionAsync(db,tx,input,assignment,context:context);
            Assert.Equal(JsonSerializer.Serialize(expected,Json),JsonSerializer.Serialize(actual,Json));
            if(assignment.EmployeeId==One){Assert.True(actual.Simulation.Complete);Assert.Equal(1000,actual.Simulation.Lines.Single(l=>l.Code=="H_DEDUCTION_97726").Earned);}
            else if(mixedInvalid){Assert.False(actual.Simulation.Complete);Assert.Contains(actual.Simulation.Issues,i=>i.Message.Contains("duplicate effective dates"));}
        }
        await tx.RollbackAsync();Assert.Equal(0,await WrittenAsync());
    }

    [IsolatedFact]
    public async Task Context_preserves_approved_adjustment_order_inputs_and_non_applicable_rows()
    {
        await using var db=await F.OpenAsync();
        await db.ExecuteAsync(@"INSERT INTO payrolladjustments(ClientId,EmployeeId,ComponentId,ComponentCode,ComponentName,AdjustmentType,Amount,PayPeriod,PayRunType,Status) VALUES
(97726,9772602,9772601,'H_EXTRA_B','Extra B','Earning',12.34,'2026-09','Regular','Approved'),
(97726,9772601,9772601,'H_EXTRA_A','Extra A','Earning',56.78,'2026-09','Regular','Approved'),
(97726,9772601,9772602,'H_RECOVERY_A','Recovery A','Recovery',3.21,'2026-09','Regular','Approved'),
(97726,9772601,9772601,'H_APPLIED','Applied','Earning',99,'2026-09','Regular','Applied'),
(97726,9772601,9772601,'H_OFF','Off cycle','Earning',88,'2026-09','Off Cycle','Approved'),
(97726,9772601,9772601,'H_OTHER_MONTH','Other month','Earning',77,'2026-10','Regular','Approved')");
        await using var tx=await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,isReadOnly:true);
        var context=await F.Runs.CreateHistoricalSimulationContextAsync(db,tx,Client,"2026-09",[Two,One]);
        foreach(var id in new[]{Two,One})
        {
            var input=new PayrollSimulationRequest{Mode="Employee",ClientId=Client,EmployeeId=id,PayPeriod="2026-09"};
            var expected=await F.Runs.SimulateHistoricalSalaryInTransactionAsync(db,tx,input,null);
            var actual=await F.Runs.SimulateHistoricalSalaryInTransactionAsync(db,tx,input,null,context:context);
            Assert.True(actual.Simulation.Complete);Assert.Equal(JsonSerializer.Serialize(expected,Json),JsonSerializer.Serialize(actual,Json));
            Assert.DoesNotContain(actual.Simulation.Lines,l=>l.Code is "H_APPLIED" or "H_OFF" or "H_OTHER_MONTH");
        }
        await tx.RollbackAsync();
    }

    [IsolatedTheory]
    [InlineData("uan")][InlineData("source")][InlineData("missingReceipt")]
    public async Task Context_preserves_singleton_outcomes_for_mixed_changed_PT_evidence(string change)
    {
        await ApplyAsync(await ReadyPtAsync(true));
        await using var db=await F.OpenAsync();
        await db.ExecuteAsync(change switch{
            "uan"=>"UPDATE employeepersonaldetails SET UanNumber='109999999999' WHERE EmployeeId=9772602",
            "source"=>"UPDATE excel_payslip_batches SET batch_json=JSON_SET(batch_json,'$.rows[1].netPay',8801) WHERE client_id=97726",
            _=>"DELETE FROM modulesettings WHERE client_id=97726 AND ModuleCode LIKE 'closed_salary_receipt:%'"});
        await using var tx=await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,isReadOnly:true);
        var context=await F.Runs.CreateHistoricalSimulationContextAsync(db,tx,Client,"2026-09",[One,Two]);
        foreach(var id in new[]{One,Two})
        {
            var input=new PayrollSimulationRequest{Mode="Employee",ClientId=Client,EmployeeId=id,PayPeriod="2026-09"};
            HistoricalSalarySimulationResult? expected=null;InvalidOperationException? error=null;
            try{expected=await F.Runs.SimulateHistoricalSalaryInTransactionAsync(db,tx,input,null);}catch(InvalidOperationException caught){error=caught;}
            if(error is not null){var actual=await Assert.ThrowsAsync<InvalidOperationException>(()=>F.Runs.SimulateHistoricalSalaryInTransactionAsync(db,tx,input,null,context:context));Assert.Equal(error.Message,actual.Message);}
            else{var actual=await F.Runs.SimulateHistoricalSalaryInTransactionAsync(db,tx,input,null,context:context);Assert.Equal(JsonSerializer.Serialize(expected,Json),JsonSerializer.Serialize(actual,Json));}
        }
        await tx.RollbackAsync();
    }

    [IsolatedFact]
    public async Task Context_rejects_unbounded_duplicate_foreign_client_period_and_transaction_use()
    {
        await using var db=await F.OpenAsync();await using var tx=await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,isReadOnly:true);
        foreach(var ids in new[]{Array.Empty<int>(),new[]{One,One},new[]{-1},Enumerable.Range(1,101).ToArray()})
            await Assert.ThrowsAsync<InvalidOperationException>(()=>F.Runs.CreateHistoricalSimulationContextAsync(db,tx,Client,"2026-09",ids));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>F.Runs.CreateHistoricalSimulationContextAsync(db,tx,Client,"bad-period",[One]));
        var context=await F.Runs.CreateHistoricalSimulationContextAsync(db,tx,Client,"2026-09",[One]);
        foreach(var input in new[]{new PayrollSimulationRequest{Mode="Employee",ClientId=Client+1,EmployeeId=One,PayPeriod="2026-09"},
            new(){Mode="Employee",ClientId=Client,EmployeeId=One,PayPeriod="2026-10"},new(){Mode="Employee",ClientId=Client,EmployeeId=Two,PayPeriod="2026-09"}})
            await Assert.ThrowsAsync<InvalidOperationException>(()=>F.Runs.SimulateHistoricalSalaryInTransactionAsync(db,tx,input,null,context:context));
        await using var other=await F.OpenAsync();await using var otherTx=await other.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,isReadOnly:true);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>F.Runs.SimulateHistoricalSalaryInTransactionAsync(other,otherTx,new(){Mode="Employee",ClientId=Client,EmployeeId=One,PayPeriod="2026-09"},null,context:context));
        var result=await F.Runs.SimulateHistoricalSalaryInTransactionAsync(db,tx,new(){Mode="Employee",ClientId=Client,EmployeeId=One,PayPeriod="2026-09"},null,context:context);
        Assert.True(result.Simulation.Complete);await otherTx.RollbackAsync();await tx.RollbackAsync();
    }

    [IsolatedFact]
    public async Task Context_initial_source_mode_has_the_exact_old_preview_and_keeps_current_future_state()
    {
        await using(var db=await F.OpenAsync())
            await db.ExecuteAsync("DELETE FROM employee_it0008_basic_pay WHERE ClientId=97726 AND ActionType='Legacy salary';UPDATE employees SET AnnualCtc=120000 WHERE ClientId=97726");
        source.ColumnCount=11;source.Columns.Add(new(){ColumnIndex=10,SourceHeader="Monthly CTC",Label="Monthly CTC",Kind="info"});
        foreach(var row in source.Rows)row.Cells.Add(new(){Value="10000"});
        foreach(var row in batch.Rows)row.Information.Add(new(){Label="Monthly CTC",Value="10000"});
        await SaveSourceAsync();
        var request=Request(true);request.SalaryTargetMode="ReviewedInitialSourceTarget";request.Setup.MonthlyCtcColumnIndex=10;
        request.InitialSourceTargetReview=new(){Version=1,Reviewed=true,MonthlyCtcColumnIndex=10,SourceHash=request.Setup.AttendanceReview!.SourceHash,
            Meaning="MonthlyPayrollEarningsBudget",ActualJoiningDateNotEstablished=true};
        var before=await WitnessAsync();var expected=await Repository.PreviewHistoricalSalaryAsync(Client,request,Actor);
        var preview=await BatchRepository.PreviewHistoricalSalaryAsync(Client,request,Actor);
        Assert.True(preview.CanApply,string.Join(";",preview.Blockers));Assert.Equal(ComparablePreview(expected),ComparablePreview(preview));
        var receipt=await BatchRepository.ApplyHistoricalSalaryAsync(Client,new(){ReviewToken=preview.ReviewToken,RequestId=Guid.NewGuid().ToString("N"),Acknowledged=true},Actor);
        Assert.Equal(2,receipt.Rows.Count);Assert.Equal(2,receipt.InitialSourceTargets!.Count);Assert.Equal(before,await WitnessAsync());
    }
}
