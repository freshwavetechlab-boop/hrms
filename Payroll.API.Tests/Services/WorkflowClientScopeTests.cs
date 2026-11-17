using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class WorkflowClientScopeTests
{
    private static WorkflowMaster Workflow(int id, int? client, bool active = true, string code = "PAYRUN.SUBMIT") =>
        new() { Id = id, ClientId = client, Code = code, IsActive = active };
    private static WorkflowActionRule Rule(int? id = 6) => new() { ActivityCode = "PAYRUN.SUBMIT", WorkflowId = id };

    [Fact]
    public void Shared_payroll_action_does_not_assign_another_clients_approver()
    {
        var candidates = new[] { Workflow(6, 10), Workflow(8, 64), Workflow(4, null) };
        Assert.Equal(6, WorkflowClientScope.Select(Rule(), 10, candidates));
        Assert.Equal(8, WorkflowClientScope.Select(Rule(), 64, candidates));
        Assert.Equal(4, WorkflowClientScope.Select(Rule(), 99, candidates));
        Assert.Null(WorkflowClientScope.Select(Rule(), 64, [Workflow(6, 10)]));
    }

    [Fact]
    public void An_explicit_global_workflow_remains_global_and_defaults_keep_client_precedence()
    {
        var candidates = new[] { Workflow(6, null), Workflow(8, 64), Workflow(2, 10) };
        Assert.Equal(6, WorkflowClientScope.Select(Rule(), 64, candidates));
        Assert.Equal(8, WorkflowClientScope.Select(Rule(null), 64, candidates));
        Assert.Equal(6, WorkflowClientScope.Select(Rule(null), null, candidates));
        Assert.Equal(6, WorkflowClientScope.Select(Rule(null), 99, candidates));
    }

    [Fact]
    public void Inactive_other_activity_and_foreign_defaults_are_not_approval_authority()
    {
        Assert.Equal(9, WorkflowClientScope.Select(Rule(null), 64,
            [Workflow(1, 10), Workflow(2, 64, false), Workflow(3, 64, code: "LEAVE_REQUEST.SUBMIT"), Workflow(9, null)]));
        Assert.Null(WorkflowClientScope.Select(Rule(null), 64, [Workflow(1, 10), Workflow(2, 64, false)]));
        Assert.Equal(2, WorkflowClientScope.Select(Rule(null), 64, [Workflow(3, 64), Workflow(2, 64)]));
    }

    [Fact]
    public void Broken_explicit_configuration_and_unknown_client_fail_closed()
    {
        Assert.Throws<InvalidOperationException>(() => WorkflowClientScope.Select(Rule(), 64, []));
        Assert.Throws<InvalidOperationException>(() => WorkflowClientScope.Select(Rule(), 64, [Workflow(6, 10, false)]));
        Assert.Throws<InvalidOperationException>(() => WorkflowClientScope.Select(Rule(), 64, [Workflow(6, 0)]));
        Assert.Throws<InvalidOperationException>(() => WorkflowClientScope.Select(Rule(), null, [Workflow(6, 10), Workflow(7, null)]));
    }

    [Theory]
    [InlineData(null, true, 64, 64)]
    [InlineData(64, true, 64, 64)]
    [InlineData(0, true, 64, 64)]
    [InlineData(10, false, null, 10)]
    [InlineData(null, false, null, null)]
    public void Authoritative_resource_scope_wins_only_when_metadata_agrees(int? supplied, bool known, int? owner, int? expected) =>
        Assert.Equal(expected, WorkflowClientScope.ResolveClient(supplied, known, owner));

    [Theory]
    [InlineData(10, true, 64)]
    [InlineData(null, true, null)]
    [InlineData(64, true, 0)]
    public void Forged_missing_or_unresolved_known_resource_owner_is_rejected(int? supplied, bool known, int? owner) =>
        Assert.Throws<InvalidOperationException>(() => WorkflowClientScope.ResolveClient(supplied, known, owner));

    [Fact]
    public void Start_guard_blocks_foreign_resource_before_task_creation_but_retains_legacy_custom_start()
    {
        WorkflowClientScope.RequireStartScope(Workflow(6, 10), 10, true, 10);
        WorkflowClientScope.RequireStartScope(Workflow(7, null), 64, true, 64);
        Assert.Throws<InvalidOperationException>(() => WorkflowClientScope.RequireStartScope(Workflow(6, 10), null, true, 64));
        Assert.Throws<InvalidOperationException>(() => WorkflowClientScope.RequireStartScope(Workflow(6, 10), 10, true, 64));
        Assert.Throws<InvalidOperationException>(() => WorkflowClientScope.RequireStartScope(Workflow(6, 10), 64, false, null));
        WorkflowClientScope.RequireStartScope(Workflow(6, 10), null, false, null);
    }
}
