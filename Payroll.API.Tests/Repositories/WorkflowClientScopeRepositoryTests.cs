using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Repositories;

public sealed class WorkflowClientScopeRepositoryTests
{
    [LocalWorkflowScopeDatabaseFact]
    public async Task Shared_payroll_rule_preserves_REC_approval_without_creating_a_foreign_IIM_task()
    {
        await using var fixture = await Fixture.CreateAsync();
        var rec = await fixture.WorkflowAsync(10);
        await fixture.Db.ExecuteAsync("UPDATE workflow_action_rules SET WorkflowId=@Id WHERE ActivityCode='PAYRUN.SUBMIT'", new { rec.Id });
        var rule = Assert.Single(await fixture.Workflows.GetActionRulesAsync(), row => row.ActivityCode == "PAYRUN.SUBMIT");
        var resolved = await fixture.Workflows.ResolveActionWorkflowAsync(rule, 64, "PayRun", "82");
        Assert.Equal(64, resolved.ClientId);
        Assert.Null(resolved.WorkflowId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Workflows.ResolveActionWorkflowAsync(rule, 10, "PayRun", "82"));

        await fixture.InvokeSubmitAsync(82);
        Assert.Equal(0, await fixture.EvidenceCountAsync());
        Assert.Null(await fixture.Workflows.StartAsync(new() { WorkflowId = rec.Id, ResourceType = "PayRun", ResourceId = "82" }, 1));
        Assert.Null(await fixture.Workflows.StartAsync(new() { WorkflowId = rec.Id, ResourceType = "PayrollRun", ResourceId = "missing" }, 1));
        Assert.Null(await fixture.Workflows.StartAsync(new() { WorkflowId = rec.Id, ResourceType = "CustomFixture", ResourceId = "82" }, 1, 64));
        Assert.Equal(0, await fixture.EvidenceCountAsync());

        await fixture.InvokeSubmitAsync(83);
        var pending = Assert.Single(await fixture.Workflows.PendingAsync(2));
        Assert.Equal("83", pending.ResourceId);
        Assert.Equal(10, pending.ClientId);
        Assert.False(await fixture.Workflows.ActionAsync(pending.Id, 3, "Approved", "Wrong approver"));
        Assert.Equal("Pending", (await fixture.Workflows.GetInstanceAsync(pending.InstanceId))!.Status);
        Assert.True(await fixture.Workflows.ActionAsync(pending.Id, 2, "Approved", "Assigned approver"));
        Assert.Equal("Approved", (await fixture.Workflows.GetInstanceAsync(pending.InstanceId))!.Status);
        Assert.Null(await fixture.Workflows.GetResourceStateAsync("PayRun", "82"));

        var global = await fixture.WorkflowAsync(null);
        Assert.Equal(global.Id, (await fixture.Workflows.ResolveActionWorkflowAsync(rule, 64, "PayRun", "82")).WorkflowId);
        var iim = await fixture.WorkflowAsync(64);
        Assert.Equal(iim.Id, (await fixture.Workflows.ResolveActionWorkflowAsync(rule, 64, "PayRun", "82")).WorkflowId);
        Assert.Equal(rec.Id, (await fixture.Workflows.ResolveActionWorkflowAsync(rule, 10, "PayRun", "83")).WorkflowId);
    }

    [LocalWorkflowScopeDatabaseFact]
    public async Task Same_transaction_owner_and_legacy_custom_starts_keep_existing_contracts()
    {
        await using var fixture = await Fixture.CreateAsync();
        var rec = await fixture.WorkflowAsync(10);
        await using (var tx = await fixture.Db.BeginTransactionAsync())
        {
            await fixture.Db.ExecuteAsync("INSERT INTO payruns VALUES(84,10)", transaction: tx);
            var instance = await fixture.Workflows.StartInTransactionAsync(fixture.Db, tx,
                new() { WorkflowId = rec.Id, ResourceType = "PayRun", ResourceId = "84" }, 1, 10);
            Assert.NotNull(instance);
            await tx.RollbackAsync();
        }
        Assert.Equal(0, await fixture.EvidenceCountAsync());
        Assert.Equal(0, await fixture.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM payruns WHERE Id=84"));
        Assert.NotNull(await fixture.Workflows.StartAsync(new() { WorkflowId = rec.Id, ResourceType = "CustomFixture", ResourceId = "legacy" }, 1));
        var global = await fixture.WorkflowAsync(null);
        Assert.NotNull(await fixture.Workflows.StartAsync(new() { WorkflowId = global.Id, ResourceType = "PayRun", ResourceId = "82" }, 1, 64));
        Assert.Equal(2, await fixture.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM workflowinstances"));
    }

    private sealed class Fixture(MySqlConnection root, MySqlConnection db, string name, IConfiguration config) : IAsyncDisposable
    {
        internal MySqlConnection Db { get; } = db;
        internal WorkflowRepository Workflows { get; } = new(config);

        internal static async Task<Fixture> CreateAsync()
        {
            var connection = new MySqlConnectionStringBuilder(Environment.GetEnvironmentVariable("FREVO_WORKFLOW_SCOPE_TEST_DB")
                ?? throw new InvalidOperationException("An explicit owned disposable database is required."));
            // This regression never accepts the application's production config.
            Assert.Contains(connection.Server, new[] { "localhost", "127.0.0.1", "::1" });
            Assert.Equal(33331u, connection.Port);
            connection.Database = "";
            var root = new MySqlConnection(connection.ConnectionString);
            await root.OpenAsync();
            Assert.Equal("frevo-workflow-scope-test-20261114", await root.ExecuteScalarAsync<string>("SELECT @@hostname"));
            var name = "hrms_workflow_scope_test_" + Guid.NewGuid().ToString("N");
            await root.ExecuteAsync($"CREATE DATABASE `{name}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci");
            connection.Database = name;
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = connection.ConnectionString,
                ["OutboundDelivery:Suppressed"] = "true"
            }).Build();
            var db = new MySqlConnection(connection.ConnectionString);
            await db.OpenAsync();
            var fixture = new Fixture(root, db, name, config);
            await fixture.Workflows.InitializeAsync();
            await db.ExecuteAsync("CREATE TABLE payruns(Id INT PRIMARY KEY,ClientId INT NOT NULL); INSERT INTO payruns VALUES(82,64),(83,10); CREATE TABLE authusers(Id INT PRIMARY KEY,IsActive BOOLEAN,DisplayName VARCHAR(100)); INSERT INTO authusers VALUES(1,TRUE,'Requester'),(2,TRUE,'Approver'),(3,TRUE,'Other user');");
            return fixture;
        }

        internal async Task<WorkflowMaster> WorkflowAsync(int? client)
        {
            var saved = await Workflows.SaveAsync(new()
            {
                ClientId = client, Code = "PAYRUN.SUBMIT", Name = "Synthetic scope approval", ResourceType = "PayRun",
                Stages = [new() { StageOrder = 1, Name = "Approval", ApproverType = "Specific User", ApproverUserId = 2 }]
            });
            Assert.True(saved.Row is not null, saved.Error);
            return saved.Row;
        }

        internal async Task<int> EvidenceCountAsync() => await Db.ExecuteScalarAsync<int>(
            "SELECT (SELECT COUNT(*) FROM workflowinstances)+(SELECT COUNT(*) FROM workflowtasks)+(SELECT COUNT(*) FROM workflowhistory)+(SELECT COUNT(*) FROM ResourceStates)");

        internal async Task InvokeSubmitAsync(int id)
        {
            var logger = new ObservedLogger();
            var handlerRan = false;
            var middleware = new WorkflowActionMiddleware(_ => { handlerRan = true; return Task.CompletedTask; }, logger);
            var context = new DefaultHttpContext();
            context.Request.Method = "POST";
            context.Request.Path = $"/api/pay-runs/{id}/submit";
            context.Items["User"] = new AuthUser { Id = 1, IsActive = true };
            var notifications = new NotificationRepository(config, null!, null!, NullLogger<NotificationRepository>.Instance);
            await middleware.InvokeAsync(context, Workflows, notifications);
            Assert.True(handlerRan);
            Assert.Equal(0, logger.Errors);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            MySqlConnection.ClearAllPools();
            // Only the random database created by this fixture is removed.
            Assert.Matches("^hrms_workflow_scope_test_[a-f0-9]{32}$", name);
            await root.ExecuteAsync($"DROP DATABASE `{name}`");
            await root.DisposeAsync();
        }
    }

    private sealed class ObservedLogger : ILogger<WorkflowActionMiddleware>
    {
        internal int Errors { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (level >= LogLevel.Error) Errors++; }
    }
}

public sealed class LocalWorkflowScopeDatabaseFactAttribute : FactAttribute
{
    public LocalWorkflowScopeDatabaseFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FREVO_WORKFLOW_SCOPE_TEST_DB")))
            Skip = "Set FREVO_WORKFLOW_SCOPE_TEST_DB for the dedicated loopback disposable workflow-scope server.";
    }
}
