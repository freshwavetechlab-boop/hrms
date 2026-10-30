using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Payroll.API.Models;
using Payroll.API.Services;
using Xunit;

namespace Payroll.API.Tests.Services;

public class SelectedPayRunProcessingTests
{
    private static AuthUser ActiveCaller() => new() { Id = 7, IsActive = true, Roles = ["super_admin"], Permissions = ["payroll.run"] };

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    public void Active_authorized_global_operator_can_preview_and_process_a_valid_client(int clientId)
    {
        var user = ActiveCaller();
        Assert.True(SelectedPayRunAuthorization.CanRequest(user));
        Assert.True(SelectedPayRunAuthorization.CanAccess(user, clientId));
    }

    [Theory]
    [InlineData("inactive")]
    [InlineData("invalid-user")]
    [InlineData("missing-role")]
    [InlineData("missing-permission")]
    [InlineData("null-role")]
    [InlineData("null-permission")]
    public void Unqualified_callers_cannot_preview_or_process(string reason)
    {
        var user = ActiveCaller();
        switch(reason)
        {
            case "inactive": user.IsActive = false; break;
            case "invalid-user": user.Id = 0; break;
            case "missing-role": user.Roles = []; break;
            case "missing-permission": user.Permissions = []; break;
            case "null-role": user.Roles = null!; break;
            case "null-permission": user.Permissions = null!; break;
        }
        Assert.False(SelectedPayRunAuthorization.CanRequest(user));
        Assert.False(SelectedPayRunAuthorization.CanAccess(user, 7));
    }

    [Theory]
    [InlineData(7,7)]
    [InlineData(7,8)]
    [InlineData(8,7)]
    public void Client_bound_admin_never_crosses_into_restricted_global_processing(int scope, int requestedClient)
    {
        var user = ActiveCaller(); user.ClientId = scope;
        Assert.False(SelectedPayRunAuthorization.CanRequest(user));
        Assert.False(SelectedPayRunAuthorization.CanAccess(user, requestedClient));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Missing_or_invalid_target_client_is_denied(int clientId) => Assert.False(SelectedPayRunAuthorization.CanAccess(ActiveCaller(), clientId));

    [Fact]
    public void Missing_caller_and_deactivated_caller_cannot_reuse_prior_preview_authority()
    {
        Assert.False(SelectedPayRunAuthorization.CanRequest(null));
        Assert.False(SelectedPayRunAuthorization.CanAccess(null,7));
        var user = ActiveCaller(); Assert.True(SelectedPayRunAuthorization.CanAccess(user,7));
        user.IsActive = false; Assert.False(SelectedPayRunAuthorization.CanAccess(user,7));
    }

    [Fact]
    public void Existing_case_insensitive_role_permission_convention_is_preserved()
    {
        var user = ActiveCaller(); user.Roles = ["SUPER_ADMIN"]; user.Permissions = ["PAYROLL.RUN"];
        Assert.True(SelectedPayRunAuthorization.CanAccess(user,7));
    }

    [Theory]
    [InlineData("inactive")]
    [InlineData("client-bound")]
    [InlineData("missing-role")]
    [InlineData("missing-permission")]
    public async Task Marked_processing_rejects_unqualified_caller_before_workflow_or_business_execution(string reason)
    {
        var user = ActiveCaller();
        if (reason == "inactive") user.IsActive = false;
        if (reason == "client-bound") user.ClientId = 8;
        if (reason == "missing-role") user.Roles = [];
        if (reason == "missing-permission") user.Permissions = [];
        var context = new DefaultHttpContext(); context.Request.Method = "POST"; context.Request.Path = "/api/pay-runs/911/process-selected";
        context.Response.Body = new MemoryStream(); context.Items["User"] = user;
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask,new EndpointMetadataCollection(new RequireIsolatedPayrollProcessing()),"selected draft"));
        var called = false;
        await new WorkflowActionMiddleware(_ => { called = true; return Task.CompletedTask; }, NullLogger<WorkflowActionMiddleware>.Instance).InvokeAsync(context,null!,null!);
        Assert.False(called); Assert.Equal(403,context.Response.StatusCode);
        context.Response.Body.Position = 0;
        Assert.Contains("active authorized",await new StreamReader(context.Response.Body).ReadToEndAsync());
        // Null workflow/notification repositories would make an attempted lookup
        // produce a different 409 response. The 403 proves the early return.
    }

    private static SelectedRunRuntime Safe => new(true, true, true);
    private static SelectedRunState State(string status = "Queued", int[]? eligible = null, int[]? covered = null,
        int[]? otherRuns = null, long evidence = 0, long adjustments = 0, CreatePayRunRequest? request = null) => new()
    {
        Run = new PayRun { Id = 911, ClientId = 7, ClientName = "Synthetic client", PayPeriod = "2026-09", RunType = "Regular", RunCode = "REG-SYN", Status = status, RequestJson = "synthetic immutable saved request", UpdatedAt = new DateTime(2026, 10, 1) },
        Request = request ?? new CreatePayRunRequest { ClientId = 7, PayPeriod = "2026-09", RunType = "Regular", IncludedEmployeeIds = [11, 12], TotalWorkingDays = 30 },
        ClientActive = true, EligibleEmployeeIds = eligible ?? [11, 12], AlreadyCoveredEmployeeIds = covered ?? [],
        Employees = (eligible ?? [11,12]).Select(id => new SelectedPayRunEmployee { EmployeeId = id, EmployeeCode = $"SYN{id}", EmployeeName = "Synthetic person" }).ToList(),
        OtherRunningRunIds = otherRuns ?? [], FinancialEvidenceCount = evidence, AdjustmentCount = adjustments
    };
    private static SelectedPayRunProcessingRequest Command(SelectedRunState state) => new()
    {
        ClientId = 7, PayPeriod = "2026-09", Confirmed = true,
        ExpectedFingerprint = SelectedPayRunPolicy.Preview(state, Safe).Fingerprint
    };

    [Fact]
    public void Empty_explicit_queued_scope_can_be_reviewed_without_calculating_or_touching_data()
    {
        var state = State(); var preview = SelectedPayRunPolicy.Preview(state, Safe);
        Assert.True(preview.CanProcess); Assert.Equal(2, preview.EmployeeCount);
        Assert.Equal(new[] { "SYN11", "SYN12" }, preview.Employees.Select(employee => employee.EmployeeCode));
        SelectedPayRunPolicy.RequireReviewed(state, Safe, Command(state));
        Assert.Equal("Queued", state.Run.Status);
        Assert.DoesNotContain("synthetic immutable saved request", System.Text.Json.JsonSerializer.Serialize(preview));
        Assert.Contains(preview.Effects, text => text.Contains("not an amount or parity certification"));
    }

    [Theory]
    [InlineData("Draft")]
    [InlineData("Failed")]
    [InlineData("Processing")]
    [InlineData("Pending Approval")]
    [InlineData("Approved")]
    [InlineData("Paid")]
    public void Existing_attempts_cannot_be_reprocessed_or_implicitly_deleted(string status)
    {
        var state = State(status);
        Assert.False(SelectedPayRunPolicy.Preview(state, Safe).CanProcess);
        Assert.Throws<SelectedPayRunConflictException>(() => SelectedPayRunPolicy.RequireReviewed(state, Safe, Command(state)));
        Assert.Equal(status, state.Run.Status);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void Unsafe_runtime_blocks_processing(bool migrationsDisabled, bool workersDisabled, bool outboundSuppressed)
    {
        var state = State(); var runtime = new SelectedRunRuntime(migrationsDisabled, workersDisabled, outboundSuppressed);
        Assert.False(SelectedPayRunPolicy.Preview(state, runtime).CanProcess);
        Assert.Throws<SelectedPayRunConflictException>(() => SelectedPayRunPolicy.RequireReviewed(state, runtime, Command(state)));
    }

    [Fact]
    public void Runtime_defaults_do_not_accidentally_enable_restricted_processing()
    {
        Assert.False(SelectedRunRuntime.Read(new ConfigurationBuilder().Build()).Safe);
        Assert.True(SelectedRunRuntime.Read(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        {
            ["Database:AutoMigrate"] = "false", ["BackgroundWorkers:Enabled"] = "false", ["OutboundDelivery:Suppressed"] = "true"
        }).Build()).Safe);
    }

    [Fact]
    public void Missing_cross_client_or_ineligible_employees_block_entire_scope_instead_of_shrinking_it()
    {
        var state = State(eligible: [11]);
        Assert.False(SelectedPayRunPolicy.Preview(state, Safe).CanProcess);
        Assert.Throws<SelectedPayRunConflictException>(() => SelectedPayRunPolicy.RequireReviewed(state, Safe, Command(state)));
    }

    [Fact]
    public void Duplicate_implicit_and_mixed_employee_selection_are_actionable_blockers()
    {
        foreach (var ids in new int[][] { [], [11,11], [-1,11] })
        {
            var state = State(); state.Request!.IncludedEmployeeIds = ids.ToList();
            Assert.False(SelectedPayRunPolicy.Preview(state, Safe).CanProcess);
        }
        var mixed = State(); mixed.Request!.ExcludedEmployeeIds = [12];
        Assert.False(SelectedPayRunPolicy.Preview(mixed, Safe).CanProcess);
    }

    [Fact]
    public void Covered_employees_other_running_jobs_existing_evidence_and_adjustments_block()
    {
        foreach (var state in new[] { State(covered: [11]), State(otherRuns: [912]), State(evidence: 1), State(adjustments: 1) })
        {
            var preview = SelectedPayRunPolicy.Preview(state, Safe);
            Assert.False(preview.CanProcess);
            Assert.Throws<SelectedPayRunConflictException>(() => SelectedPayRunPolicy.RequireReviewed(state, Safe, Command(state)));
        }
    }

    [Fact]
    public void New_job_or_changed_scope_after_preview_invalidates_acknowledgment()
    {
        var command = Command(State());
        foreach (var state in new[] { State(otherRuns: [912]), State(eligible: [11]), State(evidence: 1) })
        {
            var error = Assert.Throws<SelectedPayRunConflictException>(() => SelectedPayRunPolicy.RequireReviewed(state, Safe, command));
            Assert.Contains("changed after review", error.Message);
        }
    }

    [Theory]
    [InlineData(8, "2026-09")]
    [InlineData(7, "2026-10")]
    public void Wrong_client_or_month_is_rejected(int clientId, string period)
    {
        var state = State(); var command = Command(state); command.ClientId = clientId; command.PayPeriod = period;
        Assert.Throws<SelectedPayRunConflictException>(() => SelectedPayRunPolicy.RequireReviewed(state, Safe, command));
    }

    [Fact]
    public void Confirm_and_fresh_preview_are_both_required()
    {
        var state = State(); var command = Command(state); command.Confirmed = false;
        Assert.Throws<SelectedPayRunConflictException>(() => SelectedPayRunPolicy.RequireReviewed(state, Safe, command));
        command.Confirmed = true; command.ExpectedFingerprint = "";
        Assert.Throws<SelectedPayRunConflictException>(() => SelectedPayRunPolicy.RequireReviewed(state, Safe, command));
    }

    [Fact]
    public void Malformed_saved_request_scope_or_null_lists_are_blocked()
    {
        var state = State(); state.Request!.IncludedEmployeeIds = null!;
        Assert.False(SelectedPayRunPolicy.Preview(state, Safe).CanProcess);
        state = State(); state.Request!.ClientId = 8;
        Assert.False(SelectedPayRunPolicy.Preview(state, Safe).CanProcess);
        state = State(); state.Request!.TotalWorkingDays = 0;
        Assert.False(SelectedPayRunPolicy.Preview(state, Safe).CanProcess);
    }

    [Fact]
    public async Task Concurrent_same_run_clicks_cannot_start_two_actions_and_gate_is_released()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var first = SelectedPayRunGate.RunAsync(991, async () => { Interlocked.Increment(ref calls); entered.SetResult(); await release.Task; return 1; });
        await entered.Task;
        await Assert.ThrowsAsync<SelectedPayRunConflictException>(() => SelectedPayRunGate.RunAsync(991, () => { calls++; return Task.FromResult(2); }));
        Assert.Equal(1, calls); release.SetResult(); Assert.Equal(1, await first);
        Assert.Equal(3, await SelectedPayRunGate.RunAsync(991, () => Task.FromResult(3)));
    }

    [Fact]
    public async Task A_failed_action_releases_gate_but_failed_state_does_not_enable_automatic_retry()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => SelectedPayRunGate.RunAsync<int>(993, () => throw new InvalidOperationException("synthetic failure")));
        Assert.Equal(1, await SelectedPayRunGate.RunAsync(993, () => Task.FromResult(1)));
        var failed = State("Failed"); Assert.False(SelectedPayRunPolicy.Preview(failed, Safe).CanProcess);
    }

    [Fact]
    public async Task Workflow_lookup_failure_blocks_before_business_handler_or_notifications()
    {
        var called = false;
        var context = new DefaultHttpContext(); context.Request.Method = "POST"; context.Request.Path = "/api/pay-runs/911/process-selected";
        context.Response.Body = new MemoryStream();
        context.Items["User"] = new AuthUser { Id = 7, IsActive = true, Roles = ["super_admin"], Permissions = ["payroll.run"] };
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(new RequireIsolatedPayrollProcessing()), "selected draft"));
        var middleware = new WorkflowActionMiddleware(_ => { called = true; return Task.CompletedTask; }, NullLogger<WorkflowActionMiddleware>.Instance);
        await middleware.InvokeAsync(context, null!, null!);
        Assert.False(called); Assert.Equal(409, context.Response.StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_marked_processing_never_reaches_business_handler()
    {
        var called = false; var context = new DefaultHttpContext();
        context.Request.Method = "POST"; context.Request.Path = "/api/pay-runs/911/process-selected"; context.Response.Body = new MemoryStream();
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(new RequireIsolatedPayrollProcessing()), "selected draft"));
        await new WorkflowActionMiddleware(_ => { called = true; return Task.CompletedTask; }, NullLogger<WorkflowActionMiddleware>.Instance).InvokeAsync(context, null!, null!);
        Assert.False(called); Assert.Equal(403, context.Response.StatusCode);
    }
}
