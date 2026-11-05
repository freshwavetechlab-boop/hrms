using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class SelectedClientSetupFlowTests
{
    private sealed class Lookup(string resource, int owner) : ISelectedClientOwnershipLookup
    {
        public Task<int?> ClientIdAsync(ScopeOwnershipReference reference, CancellationToken cancellationToken = default) =>
            Task.FromResult<int?>(reference.Resource == resource ? owner : null);
    }
    private static DefaultHttpContext Context(string method, string path, string? body = null)
    {
        var context = new DefaultHttpContext(); context.Response.Body = new MemoryStream();
        context.Items["User"] = new AuthUser { Id = 8, IsActive = true, ClientAccessMode = "Selected", AssignedClientIds = [20, 21] };
        context.Request.Method = method; context.Request.Path = path; context.Request.Headers[SelectedClientScopeMiddleware.Header] = "20";
        if (body is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(body); context.Request.Body = new MemoryStream(bytes);
            context.Request.ContentLength = bytes.Length; context.Request.ContentType = "application/json";
        }
        return context;
    }

    [Theory]
    [InlineData("/api/leave-attendance/attendance-settings", "attendance-settings", 20, true)]
    [InlineData("/api/leave-attendance/attendance-settings", "attendance-settings", 21, false)]
    [InlineData("/api/recruitment-admin/ai-scoring", "ai-setting", 20, true)]
    [InlineData("/api/recruitment-admin/ai-scoring", "ai-setting", 21, false)]
    [InlineData("/api/notifications/recruitment-triggers", "recruitment-notification-rule", 20, true)]
    [InlineData("/api/notifications/recruitment-triggers", "recruitment-notification-rule", 21, false)]
    public async Task ExistingSettingsUseTheSettingRowOwnerRatherThanTreatingIdAsClientId(string path, string resource, int owner, bool allowed)
    {
        var context = Context("POST", path, "{\"id\":8,\"clientId\":20}"); var called = false;
        await new SelectedClientScopeMiddleware(_ => { called = true; return Task.CompletedTask; }).InvokeAsync(context, new Lookup(resource, owner));
        Assert.Equal(allowed, called); Assert.Equal(allowed ? 200 : 403, context.Response.StatusCode);
    }

    private sealed class PolicyLookup(int owner) : ISelectedClientOwnershipLookup
    {
        public Task<int?> ClientIdAsync(ScopeOwnershipReference reference, CancellationToken cancellationToken = default) =>
            Task.FromResult<int?>(reference.Resource == "client" ? int.Parse(reference.Value) : reference.Resource == "pf-policy-version" ? owner : null);
    }

    [Theory]
    [InlineData(false, 20, true)]
    [InlineData(false, 21, false)]
    [InlineData(true, 20, true)]
    [InlineData(true, 21, false)]
    public async Task PfDraftEditAndPublicationValidateThePolicyOwner(bool publishing, int owner, bool allowed)
    {
        const string id = "607c7de145ec492197a567a32982604f";
        var path = "/api/clients/20/pf-policy-versions" + (publishing ? "/" + id + "/publish" : "");
        var context = Context("POST", path, publishing ? "{\"reason\":\"Reviewed\"}" : "{\"id\":\"" + id + "\",\"reason\":\"Reviewed\"}");
        var called = false;
        await new SelectedClientScopeMiddleware(_ => { called = true; return Task.CompletedTask; }).InvokeAsync(context, new PolicyLookup(owner));
        Assert.Equal(allowed, called); Assert.Equal(allowed ? 200 : 403, context.Response.StatusCode);
    }

    [Theory]
    [InlineData(20, true)]
    [InlineData(21, false)]
    public async Task RecruitmentTriggerDeletionKeepsClientOwnership(int owner, bool allowed)
    {
        var context = Context("DELETE", "/api/notifications/recruitment-triggers/8"); var called = false;
        await new SelectedClientScopeMiddleware(_ => { called = true; return Task.CompletedTask; }).InvokeAsync(context, new Lookup("recruitment-notification-rule", owner));
        Assert.Equal(allowed, called); Assert.Equal(allowed ? 200 : 403, context.Response.StatusCode);
    }

    [Fact]
    public async Task WorkflowDefinitionsAreAvailableForPolicySelectorsWithinTheActiveClient()
    {
        var context = Context("GET", "/api/workflows");
        await new SelectedClientScopeMiddleware(ctx => ctx.Response.WriteAsJsonAsync(new[] {
            new { id = 1, clientId = (int?)20, name = "Client workflow" }, new { id = 2, clientId = (int?)21, name = "Other workflow" },
            new { id = 3, clientId = (int?)0, name = "Shared workflow" }, new { id = 4, clientId = (int?)null, name = "Global workflow" }
        })).InvokeAsync(context, new Lookup("unused", 20));
        context.Response.Body.Position = 0; var result = (await JsonNode.ParseAsync(context.Response.Body))!.AsArray();
        Assert.Equal(new[] { "Client workflow", "Shared workflow", "Global workflow" }, result.Select(row => row!["name"]!.ToString()));
    }
}
