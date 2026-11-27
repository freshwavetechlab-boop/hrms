using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Repositories;

public sealed class EmployeeEmailReminderTests
{
    private static AuthUser User(int? client = null) => new() { Id = 8, IsActive = true, ClientId = client, Permissions = ["employees.manage"] };
    private static AuthUser Selected(params int[] clients) => new() { Id = 9, IsActive = true, ClientAccessMode = "Selected", AssignedClientIds = [..clients], Permissions = ["employees.manage"] };
    private static EmployeeRepository.EmployeeEmailReminderSource Row(int id, int client, string email = "", bool eligible = true) => new()
    {
        EmployeeId = id, ClientId = client, ClientName = $"Synthetic client {client}", EmployeeCode = $"SYN{id:000}",
        EmployeeName = $"Synthetic employee {id:000}", WorkEmail = email, IsEligible = eligible
    };
    private static EmployeeEmailReminderPage Page(IEnumerable<EmployeeRepository.EmployeeEmailReminderSource> rows, AuthUser user, int offset = 0, int limit = 100)
        => EmployeeRepository.BuildEmployeeEmailReminderPage(rows, user, offset, limit);

    [Fact]
    public void CurrentScopeAlwaysFiltersBothThePageAndItsTotal()
    {
        var rows = new[] { Row(1, 20), Row(2, 21), Row(3, 22) };
        Assert.Equal(new[] { 1, 2, 3 }, Page(rows, User()).Items.Select(item => item.EmployeeId));
        Assert.Equal(new[] { 1 }, Page(rows, User(20)).Items.Select(item => item.EmployeeId));
        Assert.Equal(new[] { 1, 2 }, Page(rows, Selected(20, 21)).Items.Select(item => item.EmployeeId));
        Assert.Empty(Page(rows, Selected()).Items);
        Assert.Equal(0, Page(rows, Selected()).Total);
        var scoped = Selected(20, 21); scoped.ClientId = 21;
        Assert.Equal(new[] { 2 }, Page(rows, scoped).Items.Select(item => item.EmployeeId));
        scoped.ClientId = 22;
        Assert.Empty(Page(rows, scoped).Items);
        var revoked = Selected(20, 21);
        Assert.Equal(2, Page(rows, revoked).Total);
        revoked.AssignedClientIds.Remove(20);
        Assert.Equal(new[] { 2 }, Page(rows, revoked).Items.Select(item => item.EmployeeId));
        Assert.Equal(1, Page(rows, revoked).Total);
    }

    [Fact]
    public void SkippedMissingAndInvalidEmailsRemainUntilTheCurrentMasterValueIsCorrected()
    {
        var missing = Row(1, 20, "   ");
        var invalid = Row(2, 20, "not-an-email");
        var valid = Row(3, 20, "employee@example.test");
        var exited = Row(4, 20, "", false);
        var rows = new[] { missing, invalid, valid, exited };
        var first = Page(rows, User());
        Assert.Equal(new[] { "Missing", "Invalid" }, first.Items.Select(item => item.EmailIssue));
        Assert.Equal(first.Items, Page(rows, User()).Items);
        missing.WorkEmail = "updated@example.test";
        var next = Assert.Single(Page(rows, User()).Items);
        Assert.Equal(2, next.EmployeeId);
        invalid.WorkEmail = "corrected@example.test";
        Assert.Empty(Page(rows, User()).Items);
        Assert.Equal(0, Page(rows, User()).Total);
        // Rehire eligibility is supplied by the same current India-date lifecycle
        // expression as the employee directory, not cached notification history.
        exited.IsEligible = true;
        Assert.Equal(4, Assert.Single(Page(rows, User()).Items).EmployeeId);
    }

    [Fact]
    public void ActorStateAndEmployeeManagementPermissionAreRequiredRegardlessOfRole()
    {
        var rows = new[] { Row(1, 20) };
        foreach (var actor in new[] {
            new AuthUser { Id = 0, IsActive = true, Permissions = ["employees.manage"] },
            new AuthUser { Id = 8, IsActive = false, Permissions = ["employees.manage"] },
            new AuthUser { Id = 8, IsActive = true, MustChangePassword = true, Permissions = ["employees.manage"] },
            new AuthUser { Id = 8, IsActive = true, Roles = ["super_admin"], Permissions = ["settings.manage"] },
            new AuthUser { Id = 8, IsActive = true, ClientAccessMode = "Unknown", Permissions = ["employees.manage"] }
        })
        {
            Assert.False(EmployeeRepository.CanSeeEmployeeEmailReminders(actor));
            Assert.Empty(Page(rows, actor).Items);
        }
        var permitted = User(); permitted.Permissions = ["EMPLOYEES.MANAGE"];
        Assert.Single(Page(rows, permitted).Items);
    }

    [Fact]
    public async Task NoAssignedClientsOrForeignRequestedClientReturnsBeforeOpeningAnyDatabase()
    {
        var repository = new EmployeeRepository(new ConfigurationBuilder().Build(), null!, null!);
        Assert.Empty((await repository.EmployeeEmailRemindersAsync(Selected())).Items);
        Assert.Empty((await repository.EmployeeEmailRemindersAsync(Selected(20), clientId: 21)).Items);
        Assert.Empty((await repository.EmployeeEmailRemindersAsync(User(20), clientId: 21)).Items);
        var unauthorized = User(); unauthorized.Permissions = [];
        Assert.Empty((await repository.EmployeeEmailRemindersAsync(unauthorized)).Items);
    }

    [Fact]
    public void StableBoundedPagingCountsOnlyTheLiveAuthorizedReminders()
    {
        var rows = Enumerable.Range(1, 205).Select(id => Row(id, 20)).Concat([Row(300, 21), Row(301, 20, "valid@example.test")]).ToArray();
        var first = Page(rows.Reverse(), Selected(20));
        var second = Page(rows, Selected(20), 100);
        var last = Page(rows, Selected(20), 200);
        Assert.Equal(205, first.Total);
        Assert.Equal(100, first.Items.Count);
        Assert.Equal(new[] { 1, 100 }, new[] { first.Items[0].EmployeeId, first.Items[^1].EmployeeId });
        Assert.Equal(new[] { 101, 200 }, new[] { second.Items[0].EmployeeId, second.Items[^1].EmployeeId });
        Assert.Equal(5, last.Items.Count);
        Assert.Equal(205, last.Total);
        Assert.Empty(Page(rows, Selected(20), int.MaxValue).Items);
        Assert.Throws<ArgumentOutOfRangeException>(() => Page(rows, User(), -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Page(rows, User(), 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Page(rows, User(), 0, 101));
        Assert.Null(EmployeeRepository.EmployeeEmailReminderPagingError(0, 100));
    }

    [Fact]
    public void ResponseContainsOnlyTheReminderContractWithoutEmailOrPrivateMasterValues()
    {
        var json = JsonSerializer.SerializeToElement(Page([Row(1, 20, "invalid-private-source")], User()), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(new[] { "items", "total", "offset", "limit" }, json.EnumerateObject().Select(property => property.Name));
        Assert.Equal(new[] { "employeeId", "employeeCode", "employeeName", "clientId", "clientName", "emailIssue" },
            json.GetProperty("items")[0].EnumerateObject().Select(property => property.Name));
        Assert.DoesNotContain("invalid-private-source", json.ToString());
    }

    private sealed class Ownership : ISelectedClientOwnershipLookup
    {
        public Task<int?> ClientIdAsync(ScopeOwnershipReference reference, CancellationToken token = default) => Task.FromResult<int?>(null);
    }

    private static DefaultHttpContext Context(AuthUser user, string method, string path, string query = "", string? clientHeader = null)
    {
        var context = new DefaultHttpContext(); context.Items["User"] = user;
        context.Request.Method = method; context.Request.Path = path; context.Request.QueryString = new(query);
        if (clientHeader is not null) context.Request.Headers[SelectedClientScopeMiddleware.Header] = clientHeader;
        context.Response.Body = new MemoryStream();
        return context;
    }

    [Fact]
    public async Task AggregateReminderReadRetainsAuthenticatedAssignmentsAndExplicitClientReadsStillNarrowScope()
    {
        var user = Selected(20, 21);
        var aggregate = Context(user, "GET", "/api/employees/email-reminders", "?offset=0&limit=100");
        var reached = false;
        await new SelectedClientScopeMiddleware(context => { reached = true; Assert.Same(user, context.Items["User"]); return Task.CompletedTask; }).InvokeAsync(aggregate, new Ownership());
        Assert.True(reached);
        var explicitScope = Context(user, "GET", "/api/employees/email-reminders", "?clientId=21");
        reached = false;
        await new SelectedClientScopeMiddleware(context => { reached = true; Assert.Equal(21, ((AuthUser)context.Items["User"]!).ClientId); return Task.CompletedTask; }).InvokeAsync(explicitScope, new Ownership());
        Assert.True(reached);
        Assert.Null(user.ClientId);
    }

    [Theory]
    [InlineData("GET", "/api/employees/email-reminders", "?clientId=99", null)]
    [InlineData("GET", "/api/employees/email-reminders", "", "99")]
    [InlineData("POST", "/api/employees/email-reminders", "", null)]
    [InlineData("GET", "/api/employees/email-reminders/unknown", "", null)]
    public async Task AggregateExceptionDoesNotAuthorizeOtherClientsMutationsOrChildRoutes(string method, string path, string query, string? header)
    {
        var context = Context(Selected(20), method, path, query, header);
        var reached = false;
        await new SelectedClientScopeMiddleware(_ => { reached = true; return Task.CompletedTask; }).InvokeAsync(context, new Ownership());
        Assert.False(reached);
        Assert.Equal(403, context.Response.StatusCode);
    }
}
