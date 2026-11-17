using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed partial class PayrollReferenceInformationTests
{
    private static string H(char value)=>new(value,64);
    private static PayrollReferenceScope Scope()=>new(97790,9779001,9779000,"2026-09",9779011,H('a'),new string('1',32),"synthetic-source-row",H('b'),H('c'));
    private static PayrollReferenceField Field(PayrollReferenceScope scope)=>new(scope.ClientId,scope.EmployeeId,scope.PayRunId,scope.PayPeriod,9779000,H('d'),3,"Monthly target (source)","info",scope.NativeFinancialHash);
    private static PayrollReferenceProfile Profile(PayrollReferenceField field)=>new(field.ClientId,field.TemplateId,field.ProfileHash,[new(field.SourceColumn,field.Label,"info"),new(4,"Gross","earning"),new(1501,"Department","info")]);
    private static PayrollReferenceProcessingEvidence Epoch()=>new(new string('2',32),H('3'),H('4'),H('5'),H('6'),H('7'),H('8'));
    private static PayrollReferenceSelection Parts(params string[] keys)=>new(keys,keys[0],"assignment-"+keys[0],H('f'),
        keys.Select(key=>new PayrollReferencePartEvidence(key,"assignment-"+key,"source-row-"+key,H('c'),Epoch().InputSnapshotHash,Epoch().EffectiveTemplateHash,H('e'))).ToArray());
    private static PayrollReferenceOperation Op(PayrollReferenceOperator kind,params PayrollReferenceExpression[] args)=>new(kind,args);
    private static PayrollReferenceInputExpression Input(string key)=>new(key);
    private static PayrollReferenceOutputExpression Output(string key)=>new(key);
    private static PayrollReferenceDeclaration TargetDeclaration()=>new(1,PayrollReferenceInformation.Purpose,
        [new("ANNUAL_TARGET",PayrollReferenceUnit.Amount,PayrollReferenceInputMeaning.AnnualSalaryTarget,Precision:2)],
        [new("MONTHLY_TARGET",PayrollReferenceUnit.Amount,PayrollReferenceOutputMeaning.MonthlySalaryBudget,
            Op(PayrollReferenceOperator.Divide,Input("ANNUAL_TARGET"),new PayrollReferenceLiteral(12)))]);
    private static PayrollReferenceBinding Bind(PayrollReferenceDeclaration declaration,PayrollReferenceScope scope,PayrollReferenceField field,
        IEnumerable<PayrollReferenceInput> inputs,string output="MONTHLY_TARGET",PayrollReferenceSelection? selection=null)=>
        new(1,scope,field,output,PayrollReferenceInformation.DeclarationHash(declaration),inputs.ToDictionary(input=>input.Key,input=>input.ProofHash),selection);
    private static PayrollReferenceResult ResolveTarget(decimal? target)
    {
        var scope=Scope();var field=Field(scope);var declaration=TargetDeclaration();var input=PayrollReferenceInformation.FromProcessingSalaryTarget("ANNUAL_TARGET",scope,target,Epoch());
        return PayrollReferenceInformation.Resolve(declaration,Bind(declaration,scope,field,[input]),Profile(field),field,[input]);
    }
    [Theory][InlineData(648000,54000)][InlineData(0,0)][InlineData(0.06,0.005)]
    public void Full_monthly_budget_preserves_exact_annual_input_division_and_explicit_zero(decimal annual,decimal monthly)
    {var result=ResolveTarget(annual);Assert.Equal(PayrollReferenceStatus.Resolved,result.Status);Assert.Equal(monthly,result.Value);Assert.Equal(PayrollReferenceUnit.Amount,result.Unit);}

    [Fact]
    public void Same_period_missing_is_not_zero_and_other_month_does_not_need_any_provider()
    {
        Assert.Equal(PayrollReferenceStatus.Missing,ResolveTarget(null).Status);Assert.Null(ResolveTarget(null).Value);
        var scope=Scope();var field=Field(scope);var declaration=TargetDeclaration();var binding=Bind(declaration,scope,field,[]);
        var october=field with{PayPeriod="2026-10",PayRunId=82,EmployeeId=9779002,NativeFinancialHash=H('e')};
        var result=PayrollReferenceInformation.Resolve(declaration,binding,Profile(field),october,null);
        Assert.Equal(PayrollReferenceStatus.NotApplicable,result.Status);Assert.Null(result.Value);
    }
    [Theory][InlineData("client")][InlineData("employee")][InlineData("run")][InlineData("financial")][InlineData("profile")][InlineData("template")][InlineData("column")][InlineData("label")][InlineData("kind")][InlineData("month")]
    public void Exact_native_presentation_and_source_column_axes_cannot_be_reused(string axis)
    {
        var scope=Scope();var field=Field(scope);var declaration=TargetDeclaration();var input=PayrollReferenceInformation.FromProcessingSalaryTarget("ANNUAL_TARGET",scope,648000,Epoch());
        var changed=axis switch{"client"=>field with{ClientId=2},"employee"=>field with{EmployeeId=2},"run"=>field with{PayRunId=2},"financial"=>field with{NativeFinancialHash=H('e')},
            "profile"=>field with{ProfileHash=H('e')},"template"=>field with{TemplateId=2},"column"=>field with{SourceColumn=4},"label"=>field with{Label="Other"},"kind"=>field with{Kind="earning"},_=>field with{PayPeriod="not-a-month"}};
        Assert.Throws<InvalidOperationException>(()=>PayrollReferenceInformation.Resolve(declaration,Bind(declaration,scope,field,[input]),Profile(field),changed,[input]));
    }
    [Theory][InlineData("batch")][InlineData("row")][InlineData("source")][InlineData("identity")][InlineData("nativeRow")]
    public void Provider_origin_is_not_a_code_or_label_match(string axis)
    {
        var scope=Scope();var field=Field(scope);var declaration=TargetDeclaration();var input=PayrollReferenceInformation.FromProcessingSalaryTarget("ANNUAL_TARGET",scope,648000,Epoch());
        var changed=axis switch{"batch"=>scope with{SourceBatchId=new string('2',32)},"row"=>scope with{SourceRowId="another-row"},"source"=>scope with{SourceHash=H('e')},"identity"=>scope with{SourceIdentityHash=H('e')},_=>scope with{NativeRowId=2}};
        var other=PayrollReferenceInformation.FromProcessingSalaryTarget("ANNUAL_TARGET",changed,648000,Epoch());
        Assert.Throws<InvalidOperationException>(()=>PayrollReferenceInformation.Resolve(declaration,Bind(declaration,scope,field,[input]),Profile(field),field,[other]));
    }
    [Fact]
    public void Actual_profile_kind_and_duplicate_columns_are_checked_not_just_caller_info_flag()
    {
        var scope=Scope();var field=Field(scope);var d=TargetDeclaration();var input=PayrollReferenceInformation.FromProcessingSalaryTarget("ANNUAL_TARGET",scope,648000,Epoch());var binding=Bind(d,scope,field,[input]);
        Assert.Throws<InvalidOperationException>(()=>PayrollReferenceInformation.Resolve(d,binding,Profile(field) with{Columns=[new(3,field.Label,"earning")]},field,[input]));
        Assert.Throws<InvalidOperationException>(()=>PayrollReferenceInformation.Resolve(d,binding,Profile(field) with{Columns=[new(3,field.Label,"info"),new(3,field.Label,"info")]},field,[input]));
    }
    [Fact]
    public void New_epoch_or_duplicate_provider_does_not_inherit_old_immutable_value_proof()
    {
        var s=Scope();var f=Field(s);var d=TargetDeclaration();var first=PayrollReferenceInformation.FromProcessingSalaryTarget("ANNUAL_TARGET",s,648000,Epoch());
        var next=PayrollReferenceInformation.FromProcessingSalaryTarget("ANNUAL_TARGET",s,648000,Epoch() with{ProcessingEpochHash=H('f')});var b=Bind(d,s,f,[first]);
        Assert.Throws<InvalidOperationException>(()=>PayrollReferenceInformation.Resolve(d,b,Profile(f),f,[next]));
        Assert.Throws<InvalidOperationException>(()=>PayrollReferenceInformation.Resolve(d,b,Profile(f),f,[first,first]));
    }
    [Fact]
    public void Annual_budget_cannot_be_relabelled_as_basic_or_a_full_month_component_rate()
    {
        var d=TargetDeclaration();d=d with{Outputs=[d.Outputs[0] with{Meaning=PayrollReferenceOutputMeaning.FullMonthReferenceAmount}]};
        Assert.Throws<InvalidOperationException>(()=>PayrollReferenceInformation.Validate(d));
        d=TargetDeclaration();d=d with{Outputs=[d.Outputs[0] with{Expression=Input("ANNUAL_TARGET")}]};
        Assert.Throws<InvalidOperationException>(()=>PayrollReferenceInformation.Validate(d));
    }

    private static PortableIntegrationCredentialProtector Protector()=>new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{
        ["IntegrationCredentialEncryption:MasterKey"]="synthetic-reference-information-test-key"}).Build(),new EphemeralDataProtectionProvider(),NullLogger<PortableIntegrationCredentialProtector>.Instance);
    private static (PayrollReferenceScope Scope,PayrollReferenceHistory History,PayrollReferenceNativeRow Native,JsonObject Receipt) Closed()
    {
        var scope=Scope();var identity=JsonSerializer.SerializeToNode(new{clientId=scope.ClientId,employeeId=scope.EmployeeId,rowId=scope.SourceRowId,sourceRow=3,bankColumnIndex=20,
            sourceHash=scope.SourceHash,sourceCellHash=H('5'),bankOwnershipHash=H('6'),reviewedNamesHash=H('7'),reviewedNameChange=false,reason="Synthetic reviewed ownership"})!;
        scope=scope with{SourceIdentityHash=PayrollReferenceInformation.Hash(identity)};
        var receipt=JsonSerializer.SerializeToNode(new{actorId=1,sourceHash=scope.SourceHash,receipt=new{clientId=scope.ClientId,batchId=scope.SourceBatchId,payPeriod=scope.PayPeriod,
            requestId=new string('3',32),fingerprint=H('8'),rows=new[]{new{employeeId=scope.EmployeeId,rowId=scope.SourceRowId,historyId=42,templateId="source-template"}},
            initialSourceTargets=new[]{new{employeeId=scope.EmployeeId,monthlyCtcColumnIndex=3,sourceMonthlyBudget=54000,annualSalaryTarget=648000,sourceHash=scope.SourceHash,meaning="MonthlyPayrollEarningsBudget",actualJoiningDateNotEstablished=true}},sourceIdentities=new[]{identity}},
            reviewedRequest=new{salaryTargetMode="ReviewedInitialSourceTarget",setup=new{batchId=scope.SourceBatchId},initialSourceTargetReview=new{version=1,reviewed=true,sourceHash=scope.SourceHash,meaning="MonthlyPayrollEarningsBudget",actualJoiningDateNotEstablished=true,monthlyCtcColumnIndex=3}}})!.AsObject();
        return(scope,new(42,scope.ClientId,scope.EmployeeId,"source-template","Closed Excel Salary","Historical",new(2026,9,1),new(2026,9,30),648000,H('9')),
            new(scope.NativeRowId,scope.ClientId,scope.EmployeeId,scope.PayRunId,scope.PayPeriod,scope.NativeFinancialHash),receipt);
    }
    private static string Envelope(JsonObject receipt,PortableIntegrationCredentialProtector protector)=>JsonSerializer.Serialize(new{purpose="ClosedHistoricalSalaryReceipt",payload=protector.ProtectHistoricalSalary(receipt.ToJsonString())});
    private static (PayrollReferenceScope Scope,PayrollReferenceHistory History,PayrollReferenceNativeRow Native,JsonObject Receipt) ClosedRow()
    {
        var c=Closed();c.Receipt["receipt"]!["sourceIdentities"]=new JsonArray();
        c.Receipt["reviewedRequest"]!["setup"]!["rowIds"]=new JsonArray(c.Scope.SourceRowId);
        c.Receipt["reviewedRequest"]!["setup"]!["employeeDecisions"]=new JsonArray(JsonSerializer.SerializeToNode(new{rowId=c.Scope.SourceRowId,matchedEmployeeId=c.Scope.EmployeeId,allowCreate=false}));
        c.Receipt["reviewedRequest"]!["assignments"]=new JsonArray(JsonSerializer.SerializeToNode(new{rowId=c.Scope.SourceRowId,employeeId=c.Scope.EmployeeId,templateId=c.History.TemplateId}));
        return c;
    }
    [Fact]
    public void Authenticated_closed_row_mode_proves_prior_roster_ownership_without_fabricating_bank_review()
    {
        var c=ClosedRow();var p=Protector();var envelope=Envelope(c.Receipt,p);
        var scope=c.Scope with{SourceIdentityHash=PayrollReferenceInformation.AuthenticateClosedReceiptRowIdentity(c.Scope,envelope,p)};
        var provider=PayrollReferenceInformation.FromClosedInitialReceiptRow("ANNUAL_TARGET",scope,c.Native,[c.History],envelope,p);
        var field=Field(scope);var d=TargetDeclaration();
        Assert.Equal(54000,PayrollReferenceInformation.Resolve(d,Bind(d,scope,field,[provider]),Profile(field),field,[provider]).Value);
        Assert.Throws<InvalidOperationException>(()=>PayrollReferenceInformation.FromClosedInitialReceipt("ANNUAL_TARGET",scope,c.Native,[c.History],envelope,p));
        Assert.Throws<InvalidOperationException>(()=>PayrollReferenceInformation.FromClosedInitialReceiptRow("ANNUAL_TARGET",scope,c.Native,[c.History with{Id=99}],envelope,p));
        Assert.Throws<InvalidOperationException>(()=>PayrollReferenceInformation.FromClosedInitialReceiptRow("ANNUAL_TARGET",scope,c.Native with{EmployeeId=99},[c.History],envelope,p));
    }
    [Theory][InlineData("rowMissing")][InlineData("rowDuplicate")][InlineData("assignmentMissing")][InlineData("assignmentDuplicate")]
    [InlineData("assignmentEmployee")][InlineData("assignmentTemplate")][InlineData("selectedMissing")][InlineData("selectedDuplicate")]
    [InlineData("targetDuplicate")][InlineData("decisionMissing")][InlineData("decisionEmployee")][InlineData("decisionCreate")]
    [InlineData("source")][InlineData("period")][InlineData("client")][InlineData("scopeEmployee")][InlineData("scopeRow")]
    public void Closed_row_mode_rejects_missing_duplicate_cross_employee_history_and_source_scope(string mutation)
    {
        var c=ClosedRow();var scope=c.Scope;
        var receipt=c.Receipt["receipt"]!;var request=c.Receipt["reviewedRequest"]!;
        if(mutation=="rowMissing")receipt["rows"]=new JsonArray();
        if(mutation=="rowDuplicate")receipt["rows"]!.AsArray().Add(receipt["rows"]![0]!.DeepClone());
        if(mutation=="assignmentMissing")request["assignments"]=new JsonArray();
        if(mutation=="assignmentDuplicate")request["assignments"]!.AsArray().Add(request["assignments"]![0]!.DeepClone());
        if(mutation=="assignmentEmployee")request["assignments"]![0]!["employeeId"]=99;
        if(mutation=="assignmentTemplate")request["assignments"]![0]!["templateId"]="other";
        if(mutation=="selectedMissing")request["setup"]!["rowIds"]=new JsonArray();
        if(mutation=="selectedDuplicate")request["setup"]!["rowIds"]!.AsArray().Add(scope.SourceRowId);
        if(mutation=="targetDuplicate")receipt["initialSourceTargets"]!.AsArray().Add(receipt["initialSourceTargets"]![0]!.DeepClone());
        if(mutation=="decisionMissing")request["setup"]!["employeeDecisions"]=new JsonArray();
        if(mutation=="decisionEmployee")request["setup"]!["employeeDecisions"]![0]!["matchedEmployeeId"]=99;
        if(mutation=="decisionCreate")request["setup"]!["employeeDecisions"]![0]!["allowCreate"]=true;
        if(mutation=="source")c.Receipt["sourceHash"]=H('e');
        if(mutation=="period")receipt["payPeriod"]="2026-10";
        if(mutation=="client")receipt["clientId"]=99;
        if(mutation=="scopeEmployee")scope=scope with{EmployeeId=99};
        if(mutation=="scopeRow")scope=scope with{SourceRowId="other"};
        var p=Protector();var envelope=Envelope(c.Receipt,p);
        Assert.Throws<InvalidOperationException>(()=>PayrollReferenceInformation.AuthenticateClosedReceiptRowIdentity(scope,envelope,p));
    }
    [Fact]
    public void Closed_row_mode_cannot_downgrade_explicit_signed_identity_or_reuse_tampered_identity_digest()
    {
        var c=Closed();var p=Protector();
        Assert.Throws<InvalidOperationException>(()=>PayrollReferenceInformation.AuthenticateClosedReceiptRowIdentity(c.Scope,Envelope(c.Receipt,p),p));
        c=ClosedRow();var envelope=Envelope(c.Receipt,p);
        Assert.Throws<InvalidOperationException>(()=>PayrollReferenceInformation.FromClosedInitialReceiptRow("ANNUAL_TARGET",c.Scope,c.Native,[c.History],envelope,p));
        var raw=JsonNode.Parse(envelope)!;raw["payload"]="not-authenticated";
        Assert.Throws<InvalidOperationException>(()=>PayrollReferenceInformation.AuthenticateClosedReceiptRowIdentity(c.Scope,raw.ToJsonString(),p));
    }
    [Fact]
    public void Authenticated_closed_row_complete_roster_supports_five_thousand_rows_within_metadata_byte_bound()
    {
        var c=ClosedRow();var rows=new JsonArray();var assignments=new JsonArray();var targets=new JsonArray();var selected=new JsonArray();
        c.Receipt["reviewedRequest"]!["setup"]!.AsObject().Remove("employeeDecisions");
        for(var index=0;index<5000;index++)
        {
            var employee=c.Scope.EmployeeId+index;var row=index==0?c.Scope.SourceRowId:"synthetic-row-"+index;
            rows.Add(JsonSerializer.SerializeToNode(new{employeeId=employee,rowId=row,historyId=42L+index,templateId="source-template"}));
            assignments.Add(JsonSerializer.SerializeToNode(new{employeeId=employee,rowId=row,templateId="source-template"}));
            targets.Add(JsonSerializer.SerializeToNode(new{employeeId=employee}));selected.Add(row);
        }
        c.Receipt["receipt"]!["rows"]=rows;c.Receipt["receipt"]!["initialSourceTargets"]=targets;
        c.Receipt["reviewedRequest"]!["assignments"]=assignments;c.Receipt["reviewedRequest"]!["setup"]!["rowIds"]=selected;
        var protector=Protector();var envelope=Envelope(c.Receipt,protector);
        Assert.Equal(64,PayrollReferenceInformation.AuthenticateClosedReceiptRowIdentity(c.Scope,envelope,protector).Length);
    }
    [Fact]
    public void Actual_authenticated_closed_receipt_and_exact_history_supply_annual_input_not_source_money_or_current_master()
    {
        var c=Closed();var p=Protector();var provider=PayrollReferenceInformation.FromClosedInitialReceipt("ANNUAL_TARGET",c.Scope,c.Native,[c.History],Envelope(c.Receipt,p),p);
        Assert.Equal(c.History.AnnualSalaryTarget,provider.Value);Assert.Null(provider.Processing);
        var f=Field(c.Scope);var d=TargetDeclaration();var binding=Bind(d,c.Scope,f,[provider]);
        var currentMaster=new Dictionary<string,decimal>{{"AnnualCtc",648000}};currentMaster["AnnualCtc"]=999999;
        Assert.Equal(54000,PayrollReferenceInformation.Resolve(d,binding,Profile(f),f,[provider]).Value);
        Assert.DoesNotContain(typeof(PayrollReferenceInformation).GetMethods(System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic),method=>method.Name.Contains("CurrentMaster"));
    }
    [Theory][InlineData("id")][InlineData("owner")][InlineData("template")][InlineData("from")][InlineData("to")][InlineData("annual")][InlineData("status")]
    public void Protected_closed_target_requires_exact_unchanged_whole_period_history(string mutation)
    {
        var c=Closed();var h=mutation switch{"id"=>c.History with{Id=43},"owner"=>c.History with{EmployeeId=2},"template"=>c.History with{TemplateId="other"},
            "from"=>c.History with{EffectiveFrom=new(2026,9,2)},"to"=>c.History with{EffectiveTo=new(2026,10,31)},"annual"=>c.History with{AnnualSalaryTarget=1},_=>c.History with{Status="Active"}};
        var p=Protector();Assert.Throws<InvalidOperationException>(()=>PayrollReferenceInformation.FromClosedInitialReceipt("ANNUAL_TARGET",c.Scope,c.Native,[h],Envelope(c.Receipt,p),p));
    }
    [Theory][InlineData("mode")][InlineData("source")][InlineData("purpose")][InlineData("version")][InlineData("duplicate")][InlineData("zero")]
    public void Crypto_and_signed_source_mode_are_required_not_an_origin_enum_or_boolean(string mutation)
    {
        var c=Closed();var p=Protector();
        if(mutation=="mode")c.Receipt["reviewedRequest"]!["salaryTargetMode"]="PreserveEffectiveHistoricalTarget";
        if(mutation=="source")c.Receipt["sourceHash"]=H('e');
        if(mutation=="version")c.Receipt["version"]=2;
        if(mutation=="duplicate")c.Receipt["receipt"]!["rows"]!.AsArray().Add(c.Receipt["receipt"]!["rows"]![0]!.DeepClone());
        if(mutation=="zero"){c.Receipt["receipt"]!["initialSourceTargets"]![0]!["sourceMonthlyBudget"]=0;c.Receipt["receipt"]!["initialSourceTargets"]![0]!["annualSalaryTarget"]=0;}
        var envelope=Envelope(c.Receipt,p);if(mutation=="purpose")envelope=envelope.Replace("ClosedHistoricalSalaryReceipt","OtherPurpose");
        Assert.Throws<InvalidOperationException>(()=>PayrollReferenceInformation.FromClosedInitialReceipt("ANNUAL_TARGET",c.Scope,c.Native,[c.History],envelope,p));
    }
    [Fact]
    public void Wrong_encryption_key_or_replayed_native_row_cannot_authorize_reference_data()
    {
        var c=Closed();var p=Protector();var envelope=Envelope(c.Receipt,p);
        var wrong=new PortableIntegrationCredentialProtector(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["IntegrationCredentialEncryption:MasterKey"]="different-test-key"}).Build(),new EphemeralDataProtectionProvider(),NullLogger<PortableIntegrationCredentialProtector>.Instance);
        Assert.Throws<InvalidOperationException>(()=>PayrollReferenceInformation.FromClosedInitialReceipt("ANNUAL_TARGET",c.Scope,c.Native,[c.History],envelope,wrong));
        Assert.Throws<InvalidOperationException>(()=>PayrollReferenceInformation.FromClosedInitialReceipt("ANNUAL_TARGET",c.Scope,c.Native with{PayRunId=2},[c.History],envelope,p));
        Assert.Throws<InvalidOperationException>(()=>PayrollReferenceInformation.FromClosedInitialReceipt("ANNUAL_TARGET",c.Scope,c.Native,[c.History,c.History],envelope,p));
    }
    private static PayrollReferenceDeclaration BonusDeclaration()=>new(1,PayrollReferenceInformation.Purpose,
        [new("BASE",PayrollReferenceUnit.Amount,PayrollReferenceInputMeaning.TrueMonthlyBase),new("DA_BASE",PayrollReferenceUnit.Amount,PayrollReferenceInputMeaning.TrueMonthlyBase),
            new("DIVISOR",PayrollReferenceUnit.DayUnits,PayrollReferenceInputMeaning.PayrollDivisor,1,31,0),new("REGULAR",PayrollReferenceUnit.DayUnits,PayrollReferenceInputMeaning.RegularDutyUnits,0,31,4),new("EXTRA",PayrollReferenceUnit.DayUnits,PayrollReferenceInputMeaning.ExtraDutyUnits,0,999.99m,4)],
        [new("MONTHLY_BONUS",PayrollReferenceUnit.Amount,PayrollReferenceOutputMeaning.FullMonthReferenceAmount,Op(PayrollReferenceOperator.Multiply,Op(PayrollReferenceOperator.Add,Input("BASE"),Input("DA_BASE")),Op(PayrollReferenceOperator.Percent,new PayrollReferenceLiteral(8.33m,PayrollReferenceUnit.PercentPoints)))),
            new("PERIOD_BONUS",PayrollReferenceUnit.Amount,PayrollReferenceOutputMeaning.PeriodInformationAmount,Op(PayrollReferenceOperator.Multiply,Op(PayrollReferenceOperator.Divide,Output("MONTHLY_BONUS"),Input("DIVISOR")),Op(PayrollReferenceOperator.Add,Input("REGULAR"),Input("EXTRA"))))]);
    private static List<PayrollReferenceInput> BonusInputs(PayrollReferenceScope s,decimal? extra=1.5m)=>[
        PayrollReferenceInformation.FromProcessingMonthlyReference("BASE",s,20000,"BASIC",H('a'),"FullMonthReferenceProjection",Epoch()),
        PayrollReferenceInformation.FromProcessingMonthlyReference("DA_BASE",s,1000,"DA",H('b'),"FullMonthReferenceProjection",Epoch()),
        PayrollReferenceInformation.FromProcessingDutyUnits("DIVISOR",s,PayrollReferenceInputMeaning.PayrollDivisor,26,Epoch()),
        PayrollReferenceInformation.FromProcessingDutyUnits("REGULAR",s,PayrollReferenceInputMeaning.RegularDutyUnits,26,Epoch()),
        PayrollReferenceInformation.FromProcessingDutyUnits("EXTRA",s,PayrollReferenceInputMeaning.ExtraDutyUnits,extra,Epoch())];
    [Fact]
    public void Noncash_monthly_and_extra_duty_bonus_preserve_native_decimal_operator_order_without_financial_rows()
    {
        var s=Scope();var f=Field(s);var d=BonusDeclaration();var inputs=BonusInputs(s);var b=Bind(d,s,f,inputs,"PERIOD_BONUS");
        var result=PayrollReferenceInformation.Resolve(d,b,Profile(f),f,inputs);
        Assert.Equal(PayrollFormulaEvaluator.Evaluate("((20000+1000)*8.33%)/26*(26+1.5)",_=>throw new InvalidOperationException()),result.Value);
        Assert.Equal(PayrollReferenceStatus.Resolved,result.Status);Assert.DoesNotContain("PERIOD_BONUS",PayrollValueCatalogue.Names);
        Assert.Equal(PayrollReferenceStatus.Missing,PayrollReferenceInformation.Resolve(d,Bind(d,s,f,BonusInputs(s,null),"PERIOD_BONUS"),Profile(f),f,BonusInputs(s,null)).Status);
        var monthly=PayrollReferenceInformation.Resolve(d,Bind(d,s,f,inputs,"MONTHLY_BONUS"),Profile(f),f,inputs);Assert.Equal(1749.3m,monthly.Value);
    }
    [Theory][InlineData("monthlyAmount")][InlineData("Earned")][InlineData("DailyRate")]
    public void Numerically_equal_earned_or_daily_values_are_not_true_full_month_bases(string stage)=>
        Assert.Throws<InvalidOperationException>(()=>PayrollReferenceInformation.FromProcessingMonthlyReference("BASE",Scope(),20000,"BASIC",H('a'),stage,Epoch()));
    [Fact]
    public void Missing_extra_units_do_not_block_an_independent_monthly_field_but_never_become_zero()
    {
        var s=Scope();var f=Field(s);var d=BonusDeclaration();var inputs=BonusInputs(s,null);
        Assert.Equal(1749.3m,PayrollReferenceInformation.Resolve(d,Bind(d,s,f,inputs,"MONTHLY_BONUS"),Profile(f),f,inputs).Value);
        Assert.Null(PayrollReferenceInformation.Resolve(d,Bind(d,s,f,inputs,"PERIOD_BONUS"),Profile(f),f,inputs).Value);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void Supplied_null_input_still_requires_exact_ownership_and_immutable_proof(bool foreignOwner)
    {
        var s=Scope();var f=Field(s);var d=TargetDeclaration();
        var input=PayrollReferenceInformation.FromProcessingSalaryTarget("ANNUAL_TARGET",s,null,Epoch());var binding=Bind(d,s,f,[input]);
        if(foreignOwner)input=PayrollReferenceInformation.FromProcessingSalaryTarget("ANNUAL_TARGET",s with{EmployeeId=s.EmployeeId+1},null,Epoch());
        else binding=binding with{InputProofs=new Dictionary<string,string>{{"ANNUAL_TARGET",H('f')}}};
        Assert.Throws<InvalidOperationException>(()=>PayrollReferenceInformation.Resolve(d,binding,Profile(f),f,[input]));
    }
    [Fact]
    public void Ast_rejects_wrong_units_salary_constants_unknown_nodes_cycles_and_unapproved_schema()
    {
        var d=BonusDeclaration();Assert.Throws<InvalidOperationException>(()=>PayrollReferenceInformation.Validate(d with{Version=2}));
        Assert.Throws<InvalidOperationException>(()=>PayrollReferenceInformation.Validate(d with{Outputs=[d.Outputs[0] with{Expression=new PayrollReferenceLiteral(20000,PayrollReferenceUnit.Amount)}]}));
        Assert.Throws<InvalidOperationException>(()=>PayrollReferenceInformation.Validate(d with{Outputs=[d.Outputs[0] with{Expression=Op(PayrollReferenceOperator.Add,Input("BASE"),Input("DIVISOR"))}]}));
        Assert.Throws<InvalidOperationException>(()=>PayrollReferenceInformation.Validate(d with{Outputs=[d.Outputs[0] with{Expression=Output("MONTHLY_BONUS") }]}));
        Assert.Throws<InvalidOperationException>(()=>PayrollReferenceInformation.Validate(d with{Outputs=[d.Outputs[0] with{Expression=Op((PayrollReferenceOperator)999,Input("BASE"))}]}));
        Assert.Throws<InvalidOperationException>(()=>PayrollReferenceInformation.Validate(d with{Outputs=[d.Outputs[0] with{Expression=new PayrollReferenceInputExpression("SOURCE!A1") }]}));
    }
    [Fact]
    public void Raw_percentage_points_cannot_be_multiplied_as_a_fraction()
    {
        var d=BonusDeclaration();d=d with{Outputs=[d.Outputs[0] with{Expression=Op(PayrollReferenceOperator.Multiply,Input("BASE"),new PayrollReferenceLiteral(8.33m,PayrollReferenceUnit.PercentPoints))}]};
        Assert.Throws<InvalidOperationException>(()=>PayrollReferenceInformation.Validate(d));
    }
    [Theory][InlineData(0)][InlineData(1.5)]
    public void Typed_zero_conditional_has_equal_unit_branches_without_defaulting_missing_units(decimal extra)
    {
        var s=Scope();var f=Field(s);var d=BonusDeclaration();
        d=d with{Outputs=[d.Outputs[0],d.Outputs[1] with{Expression=Op(PayrollReferenceOperator.If,
            Op(PayrollReferenceOperator.Greater,Input("EXTRA"),new PayrollReferenceLiteral(0,PayrollReferenceUnit.DayUnits)),
            d.Outputs[1].Expression,new PayrollReferenceLiteral(0,PayrollReferenceUnit.Amount))}]};
        var inputs=BonusInputs(s,extra);
        var result=PayrollReferenceInformation.Resolve(d,Bind(d,s,f,inputs,"PERIOD_BONUS"),Profile(f),f,inputs);
        Assert.Equal(extra==0?0:1749.3m/26m*(26m+extra),result.Value);
        var missing=BonusInputs(s,null);
        Assert.Equal(PayrollReferenceStatus.Missing,PayrollReferenceInformation.Resolve(d,Bind(d,s,f,missing,"PERIOD_BONUS"),Profile(f),f,missing).Status);
        Assert.Throws<InvalidOperationException>(()=>PayrollReferenceInformation.Validate(d with{Outputs=[d.Outputs[0],d.Outputs[1] with{Expression=new PayrollReferenceLiteral(1,PayrollReferenceUnit.DayUnits)}]}));
    }
    [Fact]
    public void Frozen_snapshot_copies_proofs_detects_changed_value_and_has_no_native_money_side_effect()
    {
        var s=Scope();var f=Field(s);var d=TargetDeclaration();var input=PayrollReferenceInformation.FromProcessingSalaryTarget("ANNUAL_TARGET",s,648000,Epoch());var b=Bind(d,s,f,[input]);
        var salary=new PayRunEmployee{PayRunId=s.PayRunId,EmployeeId=s.EmployeeId,GrossPay=100,NetPay=90,DetailsJson="[]"};var before=JsonSerializer.Serialize(salary);
        var names=PayrollValueCatalogue.Names.ToArray();var snapshot=PayrollReferenceInformation.Freeze(d,b,Profile(f),f,[input]);PayrollReferenceInformation.ValidateSnapshot(snapshot,b,f);
        Assert.Equal(before,JsonSerializer.Serialize(salary));Assert.Equal(names,PayrollValueCatalogue.Names);
        Assert.Throws<InvalidOperationException>(()=>PayrollReferenceInformation.ValidateSnapshot(snapshot with{Value=1},b,f));
        ((Dictionary<string,string>)b.InputProofs)["ANNUAL_TARGET"]=H('f');Assert.Equal(input.ProofHash,snapshot.InputProofs["ANNUAL_TARGET"]);
        Assert.Throws<InvalidOperationException>(()=>PayrollReferenceInformation.ValidateSnapshot(snapshot,b,f));
        Assert.Throws<NotSupportedException>(()=>JsonSerializer.Deserialize<PayrollReferenceInput>("{}"));
    }
    [Fact]
    public void Actual_monthly_target_capture_is_not_inferred_from_effective_annual_zero_or_earned_money()
    {
        var s=Scope();var f=Field(s);var input=PayrollReferenceInformation.FromProcessingMonthlyTarget("ACTUAL_TARGET",s,54000.125m,Epoch());
        var d=new PayrollReferenceDeclaration(1,PayrollReferenceInformation.Purpose,[new("ACTUAL_TARGET",PayrollReferenceUnit.Amount,PayrollReferenceInputMeaning.NativeMonthlySalaryTarget)],
            [new("MONTHLY_TARGET",PayrollReferenceUnit.Amount,PayrollReferenceOutputMeaning.MonthlySalaryTarget,Input("ACTUAL_TARGET"))]);
        Assert.Equal(54000.125m,PayrollReferenceInformation.Resolve(d,Bind(d,s,f,[input]),Profile(f),f,[input]).Value);
        var bad=d with{Outputs=[d.Outputs[0] with{Meaning=PayrollReferenceOutputMeaning.FullMonthReferenceAmount}]};
        Assert.Throws<InvalidOperationException>(()=>PayrollReferenceInformation.Validate(bad));
    }
    [Fact]
    public void Ast_depth_bounds_invalid_divisors_and_arithmetic_errors_never_supply_zero()
    {
        var s=Scope();var f=Field(s);var d=BonusDeclaration();var expr=(PayrollReferenceExpression)Input("BASE");
        for(var i=0;i<40;i++)expr=Op(PayrollReferenceOperator.Abs,expr);
        Assert.Throws<InvalidOperationException>(()=>PayrollReferenceInformation.Validate(d with{Outputs=[d.Outputs[0] with{Expression=expr}]}));
        Assert.Throws<InvalidOperationException>(()=>PayrollReferenceInformation.FromProcessingDutyUnits("DIVISOR",s,PayrollReferenceInputMeaning.PayrollDivisor,0,Epoch()));
        d=d with{Outputs=[d.Outputs[0] with{Expression=Op(PayrollReferenceOperator.Divide,Input("BASE"),new PayrollReferenceLiteral(0))}]};
        var inputs=BonusInputs(s);Assert.Throws<InvalidOperationException>(()=>PayrollReferenceInformation.Resolve(d,Bind(d,s,f,inputs,"MONTHLY_BONUS"),Profile(f),f,inputs));
    }
    [Fact]
    public void Input_precision_is_not_silently_rounded_and_undeclared_values_do_not_enter_info()
    {
        var s=Scope();var f=Field(s);var d=TargetDeclaration();var input=PayrollReferenceInformation.FromProcessingSalaryTarget("ANNUAL_TARGET",s,648000.001m,Epoch());
        Assert.Throws<InvalidOperationException>(()=>PayrollReferenceInformation.Resolve(d,Bind(d,s,f,[input]),Profile(f),f,[input]));
        var extra=PayrollReferenceInformation.FromProcessingSalaryTarget("UNDECLARED",s,0,Epoch());
        Assert.Throws<InvalidOperationException>(()=>PayrollReferenceInformation.Resolve(d,Bind(d,s,f,[input]),Profile(f),f,[input,extra]));
    }
    [Fact]
    public void Full_month_part_rates_cannot_be_summed_or_select_a_different_reference()
    {
        var s=Scope();var f=Field(s);var selection=Parts("part-a","part-b");
        var a=PayrollReferenceInformation.FromProcessingMonthlyReference("BASE_A",s,1000,"BASIC",H('a'),"FullMonthReferenceProjection",Epoch(),"part-a",selection);
        var b=PayrollReferenceInformation.FromProcessingMonthlyReference("BASE_B",s,2000,"BASIC",H('b'),"FullMonthReferenceProjection",Epoch(),"part-b",selection);
        var d=new PayrollReferenceDeclaration(1,PayrollReferenceInformation.Purpose,[new("BASE_A",PayrollReferenceUnit.Amount,PayrollReferenceInputMeaning.TrueMonthlyBase),new("BASE_B",PayrollReferenceUnit.Amount,PayrollReferenceInputMeaning.TrueMonthlyBase)],
            [new("FULL_MONTH",PayrollReferenceUnit.Amount,PayrollReferenceOutputMeaning.FullMonthReferenceAmount,Op(PayrollReferenceOperator.Add,Input("BASE_A"),Input("BASE_B")))]);
        Assert.Throws<InvalidOperationException>(()=>PayrollReferenceInformation.Resolve(d,Bind(d,s,f,[a,b],"FULL_MONTH",selection),Profile(f),f,[a,b]));
        d=d with{Outputs=[d.Outputs[0] with{Expression=Input("BASE_B")}]};
        Assert.Throws<InvalidOperationException>(()=>PayrollReferenceInformation.Resolve(d,Bind(d,s,f,[a,b],"FULL_MONTH",selection),Profile(f),f,[a,b]));
        d=d with{Outputs=[d.Outputs[0] with{Expression=Input("BASE_A")}]};
        Assert.Equal(1000,PayrollReferenceInformation.Resolve(d,Bind(d,s,f,[a,b],"FULL_MONTH",selection),Profile(f),f,[a,b]).Value);
    }
    [Fact]
    public void Explicit_part_earned_info_uses_each_own_base_divisor_and_units_then_sums_amounts()
    {
        var s=Scope();var f=Field(s);var selection=Parts("a","b");
        var specs=new List<PayrollReferenceInputDefinition>();var inputs=new List<PayrollReferenceInput>();var outputs=new List<PayrollReferenceOutputDefinition>();
        foreach(var (part,baseValue,units)in new[]{("a",1000m,2m),("b",2000m,1.5m)})
        {
            var key=part.ToUpperInvariant();specs.Add(new("BASE_"+key,PayrollReferenceUnit.Amount,PayrollReferenceInputMeaning.TrueMonthlyBase));
            specs.Add(new("DIV_"+key,PayrollReferenceUnit.DayUnits,PayrollReferenceInputMeaning.PayrollDivisor));specs.Add(new("UNITS_"+key,PayrollReferenceUnit.DayUnits,PayrollReferenceInputMeaning.RegularDutyUnits));
            inputs.Add(PayrollReferenceInformation.FromProcessingMonthlyReference("BASE_"+key,s,baseValue,"BASIC",H('a'),"FullMonthReferenceProjection",Epoch(),part,selection));
            inputs.Add(PayrollReferenceInformation.FromProcessingDutyUnits("DIV_"+key,s,PayrollReferenceInputMeaning.PayrollDivisor,30,Epoch(),part,selection));
            inputs.Add(PayrollReferenceInformation.FromProcessingDutyUnits("UNITS_"+key,s,PayrollReferenceInputMeaning.RegularDutyUnits,units,Epoch(),part,selection));
            outputs.Add(new("PERIOD_"+key,PayrollReferenceUnit.Amount,PayrollReferenceOutputMeaning.PeriodInformationAmount,Op(PayrollReferenceOperator.Multiply,Op(PayrollReferenceOperator.Divide,Input("BASE_"+key),Input("DIV_"+key)),Input("UNITS_"+key))));
        }
        outputs.Add(new("TOTAL_PERIOD",PayrollReferenceUnit.Amount,PayrollReferenceOutputMeaning.AggregatePeriodInformationAmount,Op(PayrollReferenceOperator.Add,Output("PERIOD_A"),Output("PERIOD_B"))));
        var d=new PayrollReferenceDeclaration(1,PayrollReferenceInformation.Purpose,specs,outputs);
        var result=PayrollReferenceInformation.Resolve(d,Bind(d,s,f,inputs,"TOTAL_PERIOD",selection),Profile(f),f,inputs);
        Assert.Equal(1000m/30m*2m+2000m/30m*1.5m,result.Value);
        var bad=d with{Outputs=outputs.Select(output=>output.Key=="PERIOD_B"?output with{Expression=Op(PayrollReferenceOperator.Multiply,Op(PayrollReferenceOperator.Divide,Input("BASE_A"),Input("DIV_B")),Input("UNITS_B"))}:output).ToArray()};
        Assert.Throws<InvalidOperationException>(()=>PayrollReferenceInformation.Resolve(bad,Bind(bad,s,f,inputs,"TOTAL_PERIOD",selection),Profile(f),f,inputs));
        Assert.Throws<InvalidOperationException>(()=>PayrollReferenceInformation.Resolve(d,Bind(d,s,f,inputs,"TOTAL_PERIOD",selection with{CompletePartKeys=["a"]}),Profile(f),f,inputs));
    }
    [Fact]
    public void Source_part_context_needs_its_own_row_identity_input_and_template_evidence()
    {
        var selection=Parts("a","b");var s=Scope();
        Assert.Throws<InvalidOperationException>(()=>PayrollReferenceInformation.FromProcessingMonthlyReference("BASE",s,1000,"BASIC",H('a'),"FullMonthReferenceProjection",Epoch() with{InputSnapshotHash=H('9')},"a",selection));
        Assert.Throws<InvalidOperationException>(()=>PayrollReferenceInformation.FromProcessingMonthlyReference("BASE",s,1000,"BASIC",H('a'),"FullMonthReferenceProjection",Epoch(),"a",selection with{Parts=null}));
        Assert.Throws<InvalidOperationException>(()=>PayrollReferenceInformation.FromProcessingMonthlyReference("BASE",s,1000,"BASIC",H('a'),"FullMonthReferenceProjection",Epoch(),"a",selection with{ReferenceAssignmentId="wrong-assignment"}));
    }
    [Fact]
    public void Snapshot_versions_ambiguous_or_malformed_period_bindings_fail_without_legacy_fallback()
    {
        var s=Scope();var f=Field(s);var d=TargetDeclaration();var input=PayrollReferenceInformation.FromProcessingSalaryTarget("ANNUAL_TARGET",s,648000,Epoch());var b=Bind(d,s,f,[input]);
        var snapshot=PayrollReferenceInformation.Freeze(d,b,Profile(f),f,[input]);
        Assert.Throws<InvalidOperationException>(()=>PayrollReferenceInformation.ValidateSnapshot(snapshot with{Version=2},b,f));
        Assert.Throws<InvalidOperationException>(()=>PayrollReferenceInformation.Resolve(d,b with{Scope=s with{PayPeriod="2026-10"}},Profile(f),f,[input]));
        Assert.Throws<InvalidOperationException>(()=>PayrollReferenceInformation.Resolve(d,b with{Scope=s with{PayPeriod="bad"}},Profile(f),f,null));
        Assert.Throws<InvalidOperationException>(()=>PayrollReferenceInformation.Resolve(d,b with{Version=2},Profile(f),f,null));
    }
}
