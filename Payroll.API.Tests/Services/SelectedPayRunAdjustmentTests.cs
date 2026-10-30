using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;
using Xunit;

namespace Payroll.API.Tests.Services;

// Anonymous in-memory fixtures only. SQL contract checks below do not certify
// a real database rollback, transaction isolation, or production payroll result.
public class SelectedPayRunAdjustmentTests
{
    private const string BatchId="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ImportId="bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string SourceHash="cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string ReceiptHash="dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
    private const string RowId="11111111-1111-4111-8111-111111111111";

    private sealed class Fixture
    {
        public CreatePayRunRequest Request {get;}=new(){ClientId=7,PayPeriod="2026-09",PayDate=new(2026,9,28),TotalWorkingDays=30,
            RunType="Regular",IncludedEmployeeIds=[11],AdjustmentIds=[101],AttendanceGroupId=21,AttendanceGroupIds=[21],AttendancePolicyBatchId="synthetic-policy"};
        public PayrollAdjustment Adjustment {get;}=new(){Id=101,ClientId=7,EmployeeId=11,ComponentCode="TA_DA",ComponentName="Synthetic travel",
            AdjustmentType="Reimbursement",Amount=22680m,PayPeriod="2026-09",PayRunType="Regular",ReasonCode="EXCEL_SETUP",Taxable=false,
            Status="Approved",Notes=$"Reviewed salary Excel {BatchId}; import {ImportId}; source row 9",CreatedAt=new(2026,9,1),UpdatedAt=new(2026,9,1)};
        public ExcelPayslipBatch Batch {get;}=new(){Id=BatchId,ClientId=7,Month="2026-09",Rows=[new(){Id=RowId,SourceRow=9,
            EmployeeCode="SYN11",EmployeeName="Synthetic person",Earnings=[new(){Label="Travel",Amount=22680m}],NetPay=22680m}]};
        public ExcelPayslipCalculationSource Source {get;}=new(){Columns=[new(){ColumnIndex=0,SourceHeader="Travel",Label="Travel",Kind="earning"}],
            Rows=[new(){SourceRow=9,Cells=[new(){DataType="Number",Value="22680"}]}]};
        public ExcelPayrollSetupRequest Imported {get;}=new(){BatchId=BatchId,TransferReviewVersion=1,RowIds=[RowId],
            EmployeeDecisions=[new(){RowId=RowId,MatchedEmployeeId=11}],Components=[new(){Code="TA_DA",Category="Reimbursement",Taxable=false,
                IsPeriodAdjustment=true,SourceColumnIndex=0,ExpectedColumnIndex=0,ProRata=false}]};
        public ExcelPayrollSetupResult Receipt {get;}=new(){ImportId=ImportId,EmployeeIds=[11],
            Transfer=new(){Version=1,SourceBatchId=BatchId,SourceMonth="2026-09",SourceHash=SourceHash},
            TransferRows=[new(RowId,11,30,26,30,22680m,[new("TA_DA",22680m,22680m,0){Category="Reimbursement"}])]};
        public SelectedPayRunAdjustmentReview Review(string currentHash=SourceHash)=>SelectedPayRunAdjustments.Review(Request,Adjustment,
            SelectedPayRunAdjustments.Reference(Adjustment),Batch,Source,Imported,Receipt,currentHash,ReceiptHash);
    }

    [Fact]
    public void Exact_source_bound_regular_reimbursement_has_server_derived_business_and_source_fingerprints()
    {
        var f=new Fixture();var result=f.Review();
        Assert.Equal(101,result.AdjustmentId);Assert.Equal(11,result.EmployeeId);Assert.Equal(22680m,result.Amount);
        Assert.Equal("Reimbursement",result.Category);Assert.False(result.Taxable);
        Assert.Equal(SourceHash,result.SourceHash);Assert.Equal(ReceiptHash,result.ReceiptFingerprint);
        Assert.Equal(SelectedPayRunAdjustments.BusinessFingerprint(f.Adjustment),result.BusinessFingerprint);
        Assert.Null(f.Adjustment.PayRunId);Assert.Equal("Approved",f.Adjustment.Status);
        Assert.Null(f.Request.SelectedAdjustmentReviews); // the pure factory cannot save/mutate a held request.
    }

    [Fact]
    public void Creation_accepts_only_explicit_ids_and_rejects_client_supplied_server_reviews()
    {
        var f=new Fixture();SelectedPayRunDraft.RequireCreation(f.Request,new(true,true,true));
        Assert.Null(f.Request.SelectedAdjustmentReviews);
        f.Request.SelectedAdjustmentReviews=[f.Review()];
        Assert.Throws<SelectedPayRunConflictException>(()=>SelectedPayRunDraft.RequireCreation(f.Request,new(true,true,true)));
    }

    [Theory]
    [InlineData("client")] [InlineData("month")] [InlineData("employee")] [InlineData("offcycle")]
    [InlineData("applied")] [InlineData("cancelled")] [InlineData("bound")] [InlineData("reason")]
    [InlineData("zero")] [InlineData("benefit")] [InlineData("invalid-id")]
    public void Wrong_scope_state_or_adjustment_type_never_enters_the_held_lane(string mutation)
    {
        var f=new Fixture();var a=f.Adjustment;
        switch(mutation){case "client":a.ClientId=8;break;case "month":a.PayPeriod="2026-10";break;case "employee":a.EmployeeId=12;break;
            case "offcycle":a.PayRunType="Off Cycle";break;case "applied":a.Status="Applied";break;case "cancelled":a.Status="Cancelled";break;
            case "bound":a.PayRunId=911;break;case "reason":a.ReasonCode="EXPENSE_CLAIM";break;case "zero":a.Amount=0;break;
            case "benefit":a.AdjustmentType="Benefit";break;case "invalid-id":a.Id=0;break;}
        Assert.Throws<SelectedPayRunConflictException>(()=>SelectedPayRunAdjustments.RequireUnbound(f.Request,a));
    }

    [Theory]
    [InlineData("duplicate-selected")] [InlineData("missing-applicable")] [InlineData("unselected-newly-approved")]
    [InlineData("duplicate-database-row")]
    public void Exact_id_set_validation_cannot_drop_or_implicitly_consume_adjustments(string reason)
    {
        var f=new Fixture();List<PayrollAdjustment> applicable=[f.Adjustment];
        if(reason=="duplicate-selected")f.Request.AdjustmentIds=[101,101];
        if(reason=="missing-applicable")applicable=[];
        if(reason=="unselected-newly-approved")applicable.Add(new(){Id=102,ClientId=7,EmployeeId=11,PayPeriod="2026-09",PayRunType="Regular",Status="Approved",ReasonCode="EXCEL_SETUP",Amount=1});
        if(reason=="duplicate-database-row")applicable.Add(f.Adjustment);
        Assert.Throws<SelectedPayRunConflictException>(()=>SelectedPayRunAdjustments.RequireExactIds(f.Request,applicable));
    }

    [Theory]
    [InlineData("changed-source-hash")] [InlineData("legacy-transfer")] [InlineData("missing-transfer")]
    [InlineData("wrong-source-month")] [InlineData("wrong-import")] [InlineData("wrong-batch")] [InlineData("wrong-batch-client")]
    [InlineData("wrong-batch-month")] [InlineData("wrong-bound-employee")] [InlineData("wrong-reviewed-employee")]
    [InlineData("missing-row")] [InlineData("kept-row")] [InlineData("category")] [InlineData("taxable")]
    [InlineData("not-period-adjustment")] [InlineData("column")] [InlineData("source-role")]
    [InlineData("source-value")] [InlineData("mapped-value")] [InlineData("receipt-value")]
    [InlineData("receipt-category")] [InlineData("missing-cache")] [InlineData("error")]
    public void Source_receipt_business_mapping_changes_are_blocked_without_reclassifying_or_forcing_amounts(string mutation)
    {
        var f=new Fixture();string current=SourceHash;
        switch(mutation){
            case "changed-source-hash":current=new('e',64);break;
            case "legacy-transfer":f.Imported.TransferReviewVersion=0;break;
            case "missing-transfer":f.Receipt.Transfer=null;break;
            case "wrong-source-month":f.Receipt.Transfer!.SourceMonth="2026-10";break;
            case "wrong-import":f.Receipt.ImportId=new('e',32);break;
            case "wrong-batch":f.Batch.Id=new('e',32);break;
            case "wrong-batch-client":f.Batch.ClientId=8;break;
            case "wrong-batch-month":f.Batch.Month="2026-10";break;
            case "wrong-bound-employee":f.Receipt.TransferRows=[f.Receipt.TransferRows[0] with {EmployeeId=12}];break;
            case "wrong-reviewed-employee":f.Imported.EmployeeDecisions[0].MatchedEmployeeId=12;break;
            case "missing-row":f.Imported.RowIds=[];break;
            case "kept-row":f.Receipt.KeptRows=[new(RowId,11,"synthetic-template","keep")];break;
            case "category":f.Imported.Components[0].Category="Earning";break;
            case "taxable":f.Imported.Components[0].Taxable=true;break;
            case "not-period-adjustment":f.Imported.Components[0].IsPeriodAdjustment=false;break;
            case "column":f.Imported.Components[0].ExpectedColumnIndex=1;break;
            case "source-role":f.Source.Columns[0].Kind="deduction";break;
            case "source-value":f.Source.Rows[0].Cells[0]!.Value="22679";break;
            case "mapped-value":f.Batch.Rows[0].Earnings[0].Amount=22679;break;
            case "receipt-value":f.Receipt.TransferRows=[f.Receipt.TransferRows[0] with {Lines=[new("TA_DA",22680m,22679m,-1){Category="Reimbursement"}]}];break;
            case "receipt-category":f.Receipt.TransferRows=[f.Receipt.TransferRows[0] with {Lines=[new("TA_DA",22680m,22680m,0){Category="Earning"}]}];break;
            case "missing-cache":f.Source.Rows[0].Cells[0]!.MissingCachedValue=true;break;
            case "error":f.Source.Rows[0].Cells[0]!.Error="#VALUE!";break;
        }
        Assert.Throws<SelectedPayRunConflictException>(()=>f.Review(current));
        Assert.Equal(22680m,f.Adjustment.Amount);Assert.False(f.Adjustment.Taxable);Assert.Null(f.Adjustment.PayRunId);
    }

    [Theory]
    [InlineData("source-column-roles")]
    [InlineData("preserved-cell-value")]
    [InlineData("receipt-line-code")]
    public void Explicit_null_source_metadata_is_an_actionable_conflict_instead_of_a_null_reference(string field)
    {
        var f=new Fixture();
        switch(field)
        {
            case "source-column-roles":f.Imported.SourceColumnRoles=null!;break;
            case "preserved-cell-value":f.Source.Rows[0].Cells[0]!.Value=null!;break;
            case "receipt-line-code":f.Receipt.TransferRows=[f.Receipt.TransferRows[0] with
                {Lines=[new(null!,22680m,22680m,0){Category="Reimbursement"}]}];break;
        }
        Assert.Throws<SelectedPayRunConflictException>(()=>f.Review());
    }

    [Theory]
    [InlineData("amount")] [InlineData("taxable")] [InlineData("category")] [InlineData("notes")]
    [InlineData("code")] [InlineData("status")] [InlineData("run")] [InlineData("updated")]
    public void Every_reviewed_business_change_invalidates_the_frozen_snapshot(string mutation)
    {
        var f=new Fixture();var review=f.Review();f.Request.SelectedAdjustmentReviews=[review];var before=review.BusinessFingerprint;
        switch(mutation){case "amount":f.Adjustment.Amount++;break;case "taxable":f.Adjustment.Taxable=true;break;
            case "category":f.Adjustment.AdjustmentType="Earning";break;case "notes":f.Adjustment.Notes+=" changed";break;
            case "code":f.Adjustment.ComponentCode="OTHER";break;case "status":f.Adjustment.Status="Cancelled";break;
            case "run":f.Adjustment.PayRunId=999;break;case "updated":f.Adjustment.UpdatedAt=f.Adjustment.UpdatedAt.AddSeconds(1);break;}
        var changed=SelectedPayRunAdjustments.BusinessFingerprint(f.Adjustment);Assert.NotEqual(before,changed);
        Assert.Throws<SelectedPayRunConflictException>(()=>SelectedPayRunAdjustments.RequireFrozen(f.Request,[review with {BusinessFingerprint=changed}]));
    }

    [Theory]
    [InlineData("source-hash")] [InlineData("receipt")] [InlineData("source-row")] [InlineData("amount")]
    public void A_stale_frozen_source_review_cannot_be_reused(string mutation)
    {
        var f=new Fixture();var review=f.Review();f.Request.SelectedAdjustmentReviews=[review];var changed=mutation switch {
            "source-hash"=>review with {SourceHash=new('e',64)},"receipt"=>review with {ReceiptFingerprint=new('e',64)},
            "source-row"=>review with {SourceRowId="22222222-2222-4222-8222-222222222222"},_=>review with {Amount=22681m}};
        Assert.Throws<SelectedPayRunConflictException>(()=>SelectedPayRunAdjustments.RequireFrozen(f.Request,[changed]));
    }

    [Fact]
    public void Different_adjustment_ids_cannot_double_pay_one_source_component_across_imports()
    {
        var f=new Fixture();var one=f.Review();var two=one with {AdjustmentId=102,SourceImportId=new('e',32)};
        Assert.Equal(SelectedPayRunAdjustments.BindingKey(one),SelectedPayRunAdjustments.BindingKey(two));
        f.Request.AdjustmentIds=[101,102];f.Request.SelectedAdjustmentReviews=[one,two];
        Assert.Throws<SelectedPayRunConflictException>(()=>SelectedPayRunAdjustments.RequireFrozen(f.Request,[one,two]));
    }

    [Theory]
    [InlineData("null-record")] [InlineData("null-code")] [InlineData("missing-hash")]
    [InlineData("invalid-row")] [InlineData("null-ids")]
    public void Malformed_saved_snapshot_is_an_actionable_conflict_instead_of_a_null_reference(string mutation)
    {
        var f=new Fixture();var review=f.Review();f.Request.SelectedAdjustmentReviews=[review];
        if(mutation=="null-record")f.Request.SelectedAdjustmentReviews=[null!];
        if(mutation=="null-code")f.Request.SelectedAdjustmentReviews=[review with {ComponentCode=null!}];
        if(mutation=="missing-hash")f.Request.SelectedAdjustmentReviews=[review with {BusinessFingerprint=""}];
        if(mutation=="invalid-row")f.Request.SelectedAdjustmentReviews=[review with {SourceRowId=""}];
        if(mutation=="null-ids")f.Request.AdjustmentIds=null!;
        Assert.Throws<SelectedPayRunConflictException>(()=>SelectedPayRunAdjustments.RequireFrozen(f.Request,[review]));
    }

    [Theory]
    [InlineData("source-rows")] [InlineData("source-columns")] [InlineData("source-cells")]
    [InlineData("request-rows")] [InlineData("request-components")] [InlineData("request-decisions")]
    [InlineData("receipt-rows")] [InlineData("receipt-lines")] [InlineData("receipt-employees")] [InlineData("receipt-kept")]
    public void Legacy_explicit_null_collections_remain_blocked_with_an_actionable_error(string mutation)
    {
        var f=new Fixture();
        switch(mutation){case "source-rows":f.Source.Rows=null!;break;case "source-columns":f.Source.Columns=null!;break;
            case "source-cells":f.Source.Rows[0].Cells=null!;break;case "request-rows":f.Imported.RowIds=null!;break;
            case "request-components":f.Imported.Components=null!;break;case "request-decisions":f.Imported.EmployeeDecisions=null!;break;
            case "receipt-rows":f.Receipt.TransferRows=null!;break;case "receipt-lines":f.Receipt.TransferRows=[f.Receipt.TransferRows[0] with {Lines=null!}];break;
            case "receipt-employees":f.Receipt.EmployeeIds=null!;break;case "receipt-kept":f.Receipt.KeptRows=null!;break;}
        Assert.Throws<SelectedPayRunConflictException>(()=>f.Review());
    }

    [Theory]
    [InlineData(1,0)] [InlineData(1,2)] [InlineData(2,1)] [InlineData(0,0)]
    public void Conditional_claim_requires_exact_affected_count_before_outputs_can_commit(int expected,int affected) =>
        Assert.Throws<SelectedPayRunConflictException>(()=>SelectedPayRunAdjustments.RequireAppliedCount(expected,affected));

    [Fact]
    public void Exact_claim_count_is_accepted_without_mutating_adjustment_state()
    {
        var f=new Fixture();SelectedPayRunAdjustments.RequireAppliedCount(1,1);
        Assert.Equal("Approved",f.Adjustment.Status);Assert.Null(f.Adjustment.PayRunId);
    }

    private static SelectedRunState Held(Fixture fixture,List<SelectedPayRunAdjustmentReview> current) => new()
    {
        Run=new(){Id=911,ClientId=7,PayPeriod="2026-09",RunType="Regular",Status=SelectedPayRunDraft.HeldStatus,
            RunCode="REG-SYN",RequestJson="synthetic saved request"},Request=fixture.Request,ClientActive=true,EligibleEmployeeIds=[11],
        Employees=[new(){EmployeeId=11,EmployeeCode="SYN11",EmployeeName="Synthetic person"}],AdjustmentCount=1,AdjustmentReviews=current
    };

    [Fact]
    public void Preview_of_exact_frozen_adjustment_explains_classified_amount_without_business_effects()
    {
        var f=new Fixture();var review=f.Review();f.Request.SelectedAdjustmentReviews=[review];var state=Held(f,[review]);
        var preview=SelectedPayRunPolicy.Preview(state,new(true,true,true));
        Assert.True(preview.CanProcess);Assert.Single(preview.Adjustments);Assert.Equal(22680m,preview.Adjustments[0].Amount);
        Assert.Contains(preview.Effects,text=>text.Contains("Reimbursement",StringComparison.Ordinal)&&text.Contains("22680",StringComparison.Ordinal)&&text.Contains("No new adjustment",StringComparison.Ordinal));
        Assert.Equal(SelectedPayRunDraft.HeldStatus,state.Run.Status);Assert.Equal("Approved",f.Adjustment.Status);Assert.Null(f.Adjustment.PayRunId);
    }

    [Theory]
    [InlineData("business")] [InlineData("source")] [InlineData("receipt")]
    public void A_current_adjustment_snapshot_change_invalidates_an_already_confirmed_processing_preview(string field)
    {
        var f=new Fixture();var review=f.Review();f.Request.SelectedAdjustmentReviews=[review];var state=Held(f,[review]);
        var command=new SelectedPayRunProcessingRequest{ClientId=7,PayPeriod="2026-09",Confirmed=true,
            ExpectedFingerprint=SelectedPayRunPolicy.Preview(state,new(true,true,true)).Fingerprint};
        var changed=field switch{"business"=>review with {BusinessFingerprint=new('e',64)},"source"=>review with {SourceHash=new('e',64)},_=>review with {ReceiptFingerprint=new('e',64)}};
        state.AdjustmentReviews[0]=changed;
        Assert.False(SelectedPayRunPolicy.Preview(state,new(true,true,true)).CanProcess);
        Assert.Throws<SelectedPayRunConflictException>(()=>SelectedPayRunPolicy.RequireReviewed(state,new(true,true,true),command));
        Assert.Equal(SelectedPayRunDraft.HeldStatus,state.Run.Status);Assert.Null(f.Adjustment.PayRunId);
    }

    [Fact]
    public void Default_zero_adjustment_selection_retains_existing_empty_review_behavior()
    {
        var f=new Fixture();f.Request.AdjustmentIds=[];
        SelectedPayRunDraft.RequireCreation(f.Request,new(true,true,true));
        SelectedPayRunAdjustments.RequireExactIds(f.Request,[]);SelectedPayRunAdjustments.RequireFrozen(f.Request,[]);
        SelectedPayRunDraft.RequireExactSelection(f.Request,[11],[11],[],0);
        Assert.Null(f.Request.SelectedAdjustmentReviews);
    }

    [Fact]
    public void Reimbursement_is_paid_once_without_proration_and_does_not_recur_next_month()
    {
        var f=new Fixture();var september=Calculate([f.Adjustment],15,"2026-09");
        Assert.Equal(72500m,september.GrossPay);Assert.Equal(22680m,september.OneTimeEarnings);Assert.Equal(95180m,september.NetPay);
        using var details=JsonDocument.Parse(september.DetailsJson);
        var travel=details.RootElement.EnumerateArray().Where(l=>l.GetProperty("Id").GetString()=="TA_DA").ToArray();
        Assert.Equal(2,travel.Length);Assert.Single(travel,l=>l.GetProperty("amount").GetDecimal()==0);
        Assert.Single(travel,l=>l.GetProperty("amount").GetDecimal()==22680m);
        Assert.All(travel,l=>Assert.Equal("Reimbursement",l.GetProperty("Category").GetString()));
        Assert.Equal(72500m,details.RootElement.EnumerateArray().Single(l=>l.GetProperty("Id").GetString()=="TAXABLE_PAY").GetProperty("amount").GetDecimal());
        var october=Calculate([],30,"2026-10");Assert.Equal(145000m,october.NetPay);Assert.Equal(0m,october.OneTimeEarnings);
        Assert.Equal("Approved",f.Adjustment.Status);Assert.Null(f.Adjustment.PayRunId);
    }

    [Fact]
    public void Compiled_source_reader_requires_Applied_receipt_and_locked_source_without_side_effect_statements()
    {
        var sql=Strings(typeof(PayRunRepository).GetMethod("ReadSelectedAdjustmentsAsync",BindingFlags.NonPublic|BindingFlags.Instance)!);
        Assert.Contains(sql,s=>s.Contains("excel_payroll_setup_imports",StringComparison.Ordinal)&&s.Contains("status='Applied'",StringComparison.Ordinal));
        Assert.Contains(sql,s=>s.Contains("Status='Approved' AND PayRunType='Regular'",StringComparison.Ordinal));
        Assert.Contains(sql,s=>s.Contains("SELECT CAST(source_json AS CHAR) FROM excel_payslip_calculation_sources",StringComparison.Ordinal));
        Assert.Contains(" FOR UPDATE",sql);
        Assert.DoesNotContain(sql,s=>s.Contains("SELECT batch_id FROM excel_payslip_calculation_sources",StringComparison.Ordinal));
        Assert.Contains(sql,s=>s.Contains("Status='Applied' OR PayRunId IS NOT NULL",StringComparison.Ordinal));
        Assert.DoesNotContain(sql,s=>s.TrimStart().StartsWith("UPDATE ",StringComparison.Ordinal)||s.TrimStart().StartsWith("DELETE ",StringComparison.Ordinal)||s.TrimStart().StartsWith("INSERT ",StringComparison.Ordinal));
    }

    [Fact]
    public void Compiled_exact_adjustment_claim_is_scoped_conditional_and_does_not_process_notifications_or_other_jobs()
    {
        var sql=Strings(typeof(PayRunRepository).GetMethod("ApplySelectedAdjustmentsAsync",BindingFlags.NonPublic|BindingFlags.Static)!);
        var update=Assert.Single(sql,s=>s.TrimStart().StartsWith("UPDATE payrolladjustments",StringComparison.Ordinal));
        foreach(var part in new[]{"ClientId=@ClientId","PayPeriod=@PayPeriod","PayRunType='Regular'","Status='Approved'","PayRunId IS NULL","Id IN @Ids"})Assert.Contains(part,update,StringComparison.Ordinal);
        Assert.DoesNotContain(sql,s=>s.Contains("notification",StringComparison.OrdinalIgnoreCase)||s.Contains("DELETE",StringComparison.OrdinalIgnoreCase)||s.Contains("Queued",StringComparison.Ordinal));
    }

    internal static PayRunEmployee Calculate(PayrollAdjustment[] adjustments,decimal payable,string month)
    {
        var type=typeof(PayRunRepository).GetNestedType("PayRunSourceEmployee",BindingFlags.NonPublic)!;var employee=Activator.CreateInstance(type)!;
        void Set(string name,object value)=>type.GetProperty(name)!.SetValue(employee,value);
        Set("Id",11);Set("ClientId",7);Set("EmployeeCode","SYN11");Set("FirstName","Synthetic");Set("AnnualCtc",1740000m);Set("SalaryStructureId","1");
        Set("SalaryComponents",new Dictionary<string,decimal>{{"2",0m}});Set("HasPersonalDetails",true);
        var components=new object[]{new{id="1",code="WAGES",name="Wages",category="Earning",value="145000",formula="145000",calculationType="Formula",payType="Fixed Pay",proRata=true,taxable=true,active=true,priority=10,statutoryType="None"},
            new{id="2",code="TA_DA",name="Travel",category="Reimbursement",value="0",formula="",calculationType="Manual / Variable",payType="Variable Pay",proRata=false,taxable=false,active=true,priority=20,statutoryType="None"}};
        var setup=JsonSerializer.Serialize(new{salaryComponents=components,salaryStructures=new[]{new{id="1",clientId="7",active=true,lines=new[]{new{componentId="1"},new{componentId="2"}}}}});
        try{return (PayRunEmployee)typeof(PayRunRepository).GetMethod("BuildEmployee",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,[911,employee,setup,month,30,payable,payable,adjustments,0m,0m,0m,false,"TDS"])!;}
        catch(TargetInvocationException e){throw e.InnerException!;}
    }

    private static List<string> Strings(MethodInfo method)
    {
        var move=method.GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType.GetMethod("MoveNext",BindingFlags.Instance|BindingFlags.NonPublic)!;
        var body=move.GetMethodBody()!.GetILAsByteArray()!;var codes=typeof(OpCodes).GetFields(BindingFlags.Public|BindingFlags.Static).Select(f=>(OpCode)f.GetValue(null)!).ToDictionary(c=>unchecked((ushort)c.Value));var strings=new List<string>();
        for(int i=0;i<body.Length;){ushort value=body[i++];if(value==0xfe)value=(ushort)(0xfe00|body[i++]);var code=codes[value];if(code.OperandType==OperandType.InlineString)strings.Add(move.Module.ResolveString(BitConverter.ToInt32(body,i)));i+=code.OperandType switch{OperandType.InlineNone=>0,OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar=>1,OperandType.InlineVar=>2,OperandType.InlineI8 or OperandType.InlineR=>8,OperandType.InlineSwitch=>4+4*BitConverter.ToInt32(body,i),_=>4};}return strings;
    }
}
