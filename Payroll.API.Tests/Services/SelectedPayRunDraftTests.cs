using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;
using Xunit;

namespace Payroll.API.Tests.Services;

public class SelectedPayRunDraftTests
{
    private static SelectedRunRuntime Safe => new(true,true,true);
    private static CreatePayRunRequest Request() => new()
    {
        ClientId=7,PayPeriod="2026-09",PayDate=new DateOnly(2026,9,28),RunType="Regular",TotalWorkingDays=30,
        AttendanceGroupId=21,AttendanceGroupIds=[21],AttendancePolicyBatchId="synthetic-policy",IncludedEmployeeIds=[11,12]
    };
    private static SelectedRunState Held() => new()
    {
        Run=new PayRun {Id=911,ClientId=7,PayPeriod="2026-09",RunType="Regular",RunCode="REG-SYN",Status=SelectedPayRunDraft.HeldStatus,RequestJson="synthetic immutable scope"},
        Request=Request(),ClientActive=true,EligibleEmployeeIds=[11,12],
        Employees=[new(){EmployeeId=11,EmployeeCode="SYN11"},new(){EmployeeId=12,EmployeeCode="SYN12"}]
    };
    private static SelectedPayRunProcessingRequest Reviewed(SelectedRunState state) => new()
    {ClientId=7,PayPeriod="2026-09",Confirmed=true,ExpectedFingerprint=SelectedPayRunPolicy.Preview(state,Safe).Fingerprint};

    [Fact]
    public void Held_status_stays_outside_shared_worker_Queued_pool_and_requires_separate_review()
    {
        var state=Held();
        Assert.NotEqual("Queued",state.Run.Status);
        Assert.True(SelectedPayRunDraft.IsProcessableStatus(state.Run.Status));
        var preview=SelectedPayRunPolicy.Preview(state,Safe);
        Assert.True(preview.CanProcess);
        var command=Reviewed(state);command.Confirmed=false;
        Assert.Throws<SelectedPayRunConflictException>(()=>SelectedPayRunPolicy.RequireReviewed(state,Safe,command));
        Assert.Equal(SelectedPayRunDraft.HeldStatus,state.Run.Status);
    }

    [Fact]
    public void Valid_creation_keeps_exact_client_period_policy_employee_scope_and_changes_no_data()
    {
        var request=Request();
        SelectedPayRunDraft.RequireCreation(request,Safe);
        SelectedPayRunDraft.RequireExactSelection(request,[11,12],[11,12],[],0);
        Assert.Equal(7,request.ClientId);Assert.Equal("2026-09",request.PayPeriod);
        Assert.Equal("synthetic-policy",request.AttendancePolicyBatchId);
        Assert.Equal(new[]{11,12},request.IncludedEmployeeIds);
        Assert.Empty(request.AdjustmentIds);Assert.Empty(request.ExcludedEmployeeIds);
    }

    [Theory]
    [InlineData(false,true,true)] [InlineData(true,false,true)] [InlineData(true,true,false)]
    public void Unsafe_creation_is_blocked_before_a_connection_is_opened(bool migrations,bool workers,bool outbound)
    {
        Assert.Throws<SelectedPayRunConflictException>(()=>SelectedPayRunDraft.RequireCreation(Request(),new(migrations,workers,outbound)));
    }

    [Fact]
    public async Task Repository_rejects_unsafe_creation_without_accessing_a_database()
    {
        var configuration=new ConfigurationBuilder().Build();
        var repository=new PayRunRepository(configuration,new TaxEngineRepository(configuration));
        var error=await Assert.ThrowsAsync<SelectedPayRunConflictException>(()=>repository.CreateSelectedDraftAsync(Request(),"synthetic operator"));
        Assert.Contains("No payroll request was saved",error.Message);
    }

    [Theory]
    [InlineData("client")] [InlineData("month")] [InlineData("divisor")] [InlineData("offcycle")]
    [InlineData("duplicate")] [InlineData("implicit")] [InlineData("negative")] [InlineData("exclusion")]
    [InlineData("adjustment")] [InlineData("null-list")] [InlineData("policy")] [InlineData("group")]
    [InlineData("group-duplicate")] [InlineData("group-mixed")] [InlineData("paydate")]
    public void Creation_cannot_infer_or_expand_malformed_scope(string reason)
    {
        var request=Request();
        switch(reason)
        {
            case "client":request.ClientId=0;break;
            case "month":request.PayPeriod="2026-13";break;
            case "divisor":request.TotalWorkingDays=0;break;
            case "offcycle":request.RunType="Off Cycle";break;
            case "duplicate":request.IncludedEmployeeIds=[11,11];break;
            case "implicit":request.IncludedEmployeeIds=[];break;
            case "negative":request.IncludedEmployeeIds=[-1];break;
            case "exclusion":request.ExcludedEmployeeIds=[12];break;
            case "adjustment":request.AdjustmentIds=[1,1];break;
            case "null-list":request.AttendanceGroupIds=null!;break;
            case "policy":request.AttendancePolicyBatchId="";break;
            case "group":request.AttendanceGroupId=0;request.AttendanceGroupIds=[];break;
            case "group-duplicate":request.AttendanceGroupIds=[21,21];break;
            case "group-mixed":request.AttendanceGroupId=22;break;
            case "paydate":request.PayDate=new DateOnly(2026,10,28);break;
        }
        Assert.Throws<SelectedPayRunConflictException>(()=>SelectedPayRunDraft.RequireCreation(request,Safe));
    }

    [Theory]
    [InlineData("wrong-client")] [InlineData("policy-member")] [InlineData("covered")] [InlineData("approved-adjustment")]
    public void Changed_native_scope_never_silently_drops_employees_or_applies_adjustments(string reason)
    {
        var request=Request();
        var eligible=reason=="wrong-client"?new[]{11}:new[]{11,12};
        var members=reason=="policy-member"?new[]{11}:new[]{11,12};
        var covered=reason=="covered"?new[]{12}:Array.Empty<int>();
        Assert.Throws<SelectedPayRunConflictException>(()=>SelectedPayRunDraft.RequireExactSelection(request,eligible,members,covered,reason=="approved-adjustment"?1:0));
        Assert.Equal(new[]{11,12},request.IncludedEmployeeIds);
    }

    [Theory]
    [InlineData("Draft")] [InlineData("Failed")] [InlineData("Processing")] [InlineData("Approved")] [InlineData("Paid")]
    public void Persisted_results_and_failed_history_cannot_be_processed_again(string status)
    {
        var state=Held();var reviewed=Reviewed(state);state.Run.Status=status;
        Assert.False(SelectedPayRunPolicy.Preview(state,Safe).CanProcess);
        Assert.Throws<SelectedPayRunConflictException>(()=>SelectedPayRunPolicy.RequireReviewed(state,Safe,reviewed));
        Assert.Equal(status,state.Run.Status);
    }

    [Fact]
    public void Held_to_queued_or_changed_month_invalidates_the_exact_review_fingerprint()
    {
        var state=Held();var reviewed=Reviewed(state);state.Run.Status="Queued";
        Assert.Throws<SelectedPayRunConflictException>(()=>SelectedPayRunPolicy.RequireReviewed(state,Safe,reviewed));
        state=Held();reviewed=Reviewed(state);reviewed.PayPeriod="2026-10";
        Assert.Throws<SelectedPayRunConflictException>(()=>SelectedPayRunPolicy.RequireReviewed(state,Safe,reviewed));
    }

    [Fact]
    public async Task Concurrent_exact_run_actions_share_gate_while_unrelated_run_is_untouched()
    {
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first=SelectedPayRunGate.RunAsync(941,async()=>{entered.SetResult();await release.Task;return 1;});
        await entered.Task;
        await Assert.ThrowsAsync<SelectedPayRunConflictException>(()=>SelectedPayRunGate.RunAsync(941,()=>Task.FromResult(2)));
        Assert.Equal(3,await SelectedPayRunGate.RunAsync(942,()=>Task.FromResult(3)));
        release.SetResult();Assert.Equal(1,await first);
    }

    [Theory]
    [InlineData("inactive")] [InlineData("client-bound")] [InlineData("missing-role")] [InlineData("missing-permission")]
    public async Task Creation_endpoint_metadata_rejects_unqualified_callers_before_business_or_outbound(string reason)
    {
        var user=new AuthUser {Id=7,IsActive=true,Roles=["super_admin"],Permissions=["payroll.run"]};
        if(reason=="inactive")user.IsActive=false;if(reason=="client-bound")user.ClientId=7;
        if(reason=="missing-role")user.Roles=[];if(reason=="missing-permission")user.Permissions=[];
        var context=new DefaultHttpContext();context.Request.Method="POST";context.Request.Path="/api/pay-runs/selected-draft";
        context.Response.Body=new MemoryStream();context.Items["User"]=user;
        context.SetEndpoint(new Endpoint(_=>Task.CompletedTask,new EndpointMetadataCollection(new RequireIsolatedPayrollProcessing()),"held selected draft"));
        var called=false;
        await new WorkflowActionMiddleware(_=>{called=true;return Task.CompletedTask;},NullLogger<WorkflowActionMiddleware>.Instance).InvokeAsync(context,null!,null!);
        Assert.False(called);Assert.Equal(403,context.Response.StatusCode);
    }
}
