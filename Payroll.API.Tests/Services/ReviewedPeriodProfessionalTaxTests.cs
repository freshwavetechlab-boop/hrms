using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class ReviewedPeriodProfessionalTaxTests
{
    internal static ReviewedProfessionalTaxControl Control(decimal amount = 0) => new()
    {
        ClientId = 17, EmployeeId = 21, PayPeriod = "2026-10", Mode = "ReviewedAmount", Amount = amount,
        SourceBatchId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", SourceRowId = "row-2", SourceHash = new('A', 64),
        NativeBasisHash = new('B', 64), Reason = "Reviewed source-period amount", ReviewedById = 8,
        ReviewedBy = "synthetic@example.test", ReviewedAtUtc = new(2026, 11, 14, 0, 0, 0, DateTimeKind.Utc),
        RequestId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
    };
    private static decimal? Resolve(ReviewedProfessionalTaxControl? control, bool locations = false, decimal seeded = 0,
        bool positive = false, bool mixed = false, int lines = 1) => ReviewedPeriodProfessionalTax.Resolve(control,
        17, 21, "2026-10", new('A', 64), new('B', 64), locations, seeded, positive, mixed, lines);

    [Fact]
    public void Unset_automatic_and_explicit_zero_are_distinct()
    {
        Assert.Null(Resolve(null));
        var automatic = Control(); automatic.Mode = "Automatic"; automatic.Amount = null;
        Assert.Null(Resolve(automatic));
        Assert.Equal(0, Resolve(Control()));
        Assert.Equal(175.25m, Resolve(Control(175.25m)));
    }

    [Theory]
    [InlineData("client")][InlineData("employee")][InlineData("month")][InlineData("source")]
    [InlineData("basis")][InlineData("missing")][InlineData("negative")][InlineData("precision")]
    [InlineData("reviewer")][InlineData("mode")]
    public void Wrong_scope_stale_source_or_malformed_evidence_fails_closed(string error)
    {
        var control = Control();
        switch (error)
        {
            case "client": control.ClientId++; break;
            case "employee": control.EmployeeId++; break;
            case "month": control.PayPeriod = "2026-09"; break;
            case "source": control.SourceHash = new('C', 64); break;
            case "basis": control.NativeBasisHash = new('C', 64); break;
            case "missing": control.Amount = null; break;
            case "negative": control.Amount = -1; break;
            case "precision": control.Amount = .001m; break;
            case "reviewer": control.ReviewedById = 0; break;
            case "mode": control.Mode = "Inferred"; break;
        }
        Assert.Throws<InvalidOperationException>(() => Resolve(control));
    }

    [Theory]
    [InlineData(true,0,false,false,1)] [InlineData(false,100,false,false,1)]
    [InlineData(false,0,true,false,1)] [InlineData(false,0,false,true,1)] [InlineData(false,0,false,false,2)]
    public void Effective_allocations_positive_seed_or_rule_and_duplicate_PT_require_separate_review(
        bool locations, decimal seeded, bool positive, bool mixed, int lines) =>
        Assert.Throws<InvalidOperationException>(() => Resolve(Control(), locations, seeded, positive, mixed, lines));

    [Fact]
    public void Request_never_zerofills_an_unspecified_amount_or_accepts_duplicate_employees()
    {
        var request = new ReviewedProfessionalTaxRequest
        {
            ClientId = 17, PayPeriod = "2026-10", SourceBatchId = Control().SourceBatchId, Reason = "Source review",
            Rows = [new() { EmployeeId = 21, SourceRowId = "row-2", Mode = "ReviewedAmount" }]
        };
        Assert.Throws<InvalidOperationException>(() => ReviewedPeriodProfessionalTax.Validate(request));
        request.Rows[0].Amount = 0;
        ReviewedPeriodProfessionalTax.Validate(request);
        Assert.Throws<InvalidOperationException>(() => ReviewedPeriodProfessionalTax.Validate(request, saving: true));
        request.SourceHash = new('A', 64);
        ReviewedPeriodProfessionalTax.Validate(request, saving: true);
        request.Rows.Add(new() { EmployeeId = 21, SourceRowId = "row-3", Mode = "ReviewedAmount", Amount = 0 });
        Assert.Throws<InvalidOperationException>(() => ReviewedPeriodProfessionalTax.Validate(request));
    }

    [Theory]
    [InlineData("inactive")][InlineData("client-admin")][InlineData("weak-role")][InlineData("missing-permission")][InlineData("password-change")]
    public void Restricted_review_does_not_expand_existing_client_or_payroll_permissions(string reason)
    {
        var actor=new AuthUser { Id=8,IsActive=true,Roles=["super_admin"],Permissions=["payroll.run","employees.manage","settings.manage"] };
        Assert.True(PayRunRepository.CanManageReviewedProfessionalTax(actor,17));
        switch(reason)
        {
            case "inactive":actor.IsActive=false;break;
            case "client-admin":actor.ClientId=17;break;
            case "weak-role":actor.Roles=["admin"];break;
            case "missing-permission":actor.Permissions.Remove("employees.manage");break;
            case "password-change":actor.MustChangePassword=true;break;
        }
        Assert.False(PayRunRepository.CanManageReviewedProfessionalTax(actor,17));
    }

    [Fact]
    public async Task Unsafe_runtime_blocks_save_before_opening_any_database()
    {
        var config=new ConfigurationBuilder().Build();var repository=new PayRunRepository(config,new TaxEngineRepository(config));
        var error=await Assert.ThrowsAsync<InvalidOperationException>(()=>repository.SaveReviewedProfessionalTaxAsync(
            new(new(),new('A',64),Guid.NewGuid().ToString("N"),true),new()));
        Assert.Contains("workers disabled",error.Message);
    }

    [Theory]
    [InlineData("Fixed Amount")][InlineData("Manual / Variable")]
    public void Legacy_template_zero_keeps_auto_PT_without_optin(string type)
    {
        var baseline = Build(null, type);
        Assert.Equal(200, baseline.StatutoryDeductions);
        Assert.Equal(29800, baseline.NetPay);
        Assert.Equal(0, Build(Control(), type).StatutoryDeductions);
        Assert.Equal(30000, Build(Control(), type).NetPay);
    }

    [Theory]
    [InlineData(true)][InlineData(false)]
    public void Reviewed_amount_is_one_nonprorated_native_deduction_with_immutable_source_audit(bool ptLine)
    {
        var control = Control(175.25m); var before = JsonSerializer.Serialize(control);
        var row = Build(control, present: 15, payable: 15, ptLine: ptLine);
        Assert.Equal(15000, row.GrossPay); Assert.Equal(175.25m, row.StatutoryDeductions); Assert.Equal(14824.75m, row.NetPay);
        using var details = JsonDocument.Parse(row.DetailsJson);
        Assert.Single(details.RootElement.EnumerateArray(), value => value.GetProperty("Id").GetString() == "PT");
        var audit = Assert.Single(details.RootElement.EnumerateArray(), value => value.GetProperty("Id").GetString() == "REVIEWED_PT_INPUT");
        Assert.Equal("Summary", audit.GetProperty("Category").GetString());
        Assert.Equal(control.SourceHash, audit.GetProperty("reviewedProfessionalTax").GetProperty("SourceHash").GetString());
        Assert.Equal(before, JsonSerializer.Serialize(control));
    }

    [Theory]
    [InlineData("positive")][InlineData("masked-positive")][InlineData("seeded")][InlineData("mixed")][InlineData("location")]
    public void Real_native_build_cannot_hide_existing_PT_or_location_allocations(string conflict)
    {
        Assert.Throws<InvalidOperationException>(() => Build(Control(),
            ptValue: conflict is "positive" or "masked-positive" ? "208" : "0",
            storedPt: conflict == "masked-positive" ? 0 : null, seeded: conflict == "seeded" ? 200 : 0,
            mixed: conflict == "mixed", location: conflict == "location"));
    }

    [Fact]
    public void Portable_review_evidence_survives_instance_restart_but_not_tampering_or_another_purpose()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
            { ["IntegrationCredentialEncryption:MasterKey"] = "synthetic-period-pt-test-key" }).Build();
        PortableIntegrationCredentialProtector New() => new(config, new EphemeralDataProtectionProvider(), NullLogger<PortableIntegrationCredentialProtector>.Instance);
        var first = New(); var payload = first.ProtectReviewedProfessionalTax("source-bound evidence");
        Assert.Equal("source-bound evidence", New().UnprotectReviewedProfessionalTax(payload));
        var last = payload[^2] == 'A' ? 'B' : 'A';
        Assert.Null(New().UnprotectReviewedProfessionalTax(payload[..^2] + last + payload[^1]));
        Assert.Null(New().UnprotectReviewedProfessionalTax(first.ProtectAi("credential-purpose")));
        Assert.False(New().TryUnprotectAi(payload, out _));
    }

    [Fact]
    public async Task Only_marked_POST_preview_skips_workflow_side_effects()
    {
        var ran = false;
        var middleware = new WorkflowActionMiddleware(_ => { ran = true; return Task.CompletedTask; }, NullLogger<WorkflowActionMiddleware>.Instance);
        var context = new DefaultHttpContext(); context.Request.Method = "POST";
        context.Request.Path = "/api/payroll-calculation/period-professional-tax/preview";
        context.Items["User"] = new AuthUser { Id = 8, IsActive = true };
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(new CalculationOnlyPayrollReview()), "Synthetic preview"));
        await middleware.InvokeAsync(context, null!, null!);
        Assert.True(ran);
        Assert.False(WorkflowActionMiddleware.IsCalculationOnlyRequest("POST", "/api/payroll-calculation/period-professional-tax/save"));
    }

    internal static PayRunEmployee Build(ReviewedProfessionalTaxControl? control, string ptType = "Fixed Amount", string ptValue = "0",
        decimal? storedPt = null, decimal seeded = 0, bool mixed = false, bool location = false, decimal present = 30, decimal payable = 30, bool ptLine = true,
        string mixedCode="PT_LWF_WC",bool personalDetails=true,bool separatePtLine=false,bool duplicateCombined=false,
        string workState="Synthetic state",string personalState="")
    {
        object Component(string id,string code,string type,string value,string category,bool proRata,string statutory="None") => new
            { id,code,name=code,calculationType=type,value,formula=type=="Formula"?value:"",category,proRata,priority=id=="1"?100:200,
                active=true,payType="Fixed Pay",componentRole=category=="Deduction"?"Statutory Deduction":"Regular Earning",statutoryType=statutory };
        var components = new List<object> { Component("1","BASIC","Fixed Amount","30000","Earning",true) };
        if(ptLine) components.Add(Component("2",mixed?mixedCode:"PT",ptType,ptValue,"Deduction",true,mixed?"None":"Professional Tax"));
        if(separatePtLine)components.Add(Component("3","PT","Fixed Amount","50","Deduction",false,"Professional Tax"));
        if(duplicateCombined)components.Add(Component("3",mixedCode=="PT_LWF_WC"?"WORKMEN_COMP_LWF_PT":"PT_LWF_WC",ptType,ptValue,"Deduction",true));
        var settings = JsonSerializer.Serialize(new
        {
            salaryComponents=components,salaryStructures=new[]{new{id="SYNTH-PT",clientId="17",annualCtc="360000",active=true,
                lines=components.Select(value=>new{componentId=value.GetType().GetProperty("id")!.GetValue(value)!.ToString()}).ToArray()}},
            statutory=new{pt=true,ptState="Synthetic state",
                ptStateSlabs=new[]{new{state="Synthetic state",salaryFrom="0",salaryTo="",deductionAmount="200",active=true}}}
        });
        var type = typeof(PayRunRepository).GetNestedType("PayRunSourceEmployee", BindingFlags.NonPublic)!;
        var employee = Activator.CreateInstance(type)!;
        void Set(string name,object? value) => type.GetProperty(name)!.SetValue(employee,value);
        Set("Id",21);Set("ClientId",17);Set("EmployeeCode","SYN-21");Set("FirstName","Synthetic");Set("AnnualCtc",360000m);
        Set("SalaryStructureId","SYNTH-PT");Set("SalaryJson","{}");Set("WorkState",workState);Set("PersonalState",personalState);Set("HasPersonalDetails",personalDetails);
        if(!personalDetails)Set("PersonalJson",JsonSerializer.Serialize(new{ptLwfWorkmenComp=seeded}));
        Set("PtLwfWorkmenComp",seeded);Set("ReviewedProfessionalTax",control);
        var inputs = new Dictionary<string,decimal>{{"1",30000}};if(storedPt.HasValue)inputs["2"]=storedPt.Value;
        if(duplicateCombined&&storedPt.HasValue)inputs["3"]=storedPt.Value;
        Set("SalaryComponents",inputs);
        if(location)Set("LocationParts",new PayrollLocationPartPeriod { ClientId=17,EmployeeId=21,PayPeriod="2026-10",Parts=[] });
        try { return (PayRunEmployee)typeof(PayRunRepository).GetMethod("BuildEmployee",BindingFlags.Static|BindingFlags.NonPublic)!
            .Invoke(null,[0,employee,settings,"2026-10",30,present,payable,Array.Empty<PayrollAdjustment>(),0m,0m,-1m,false,"TDS"])!; }
        catch(TargetInvocationException error){throw error.InnerException!;}
    }
}
