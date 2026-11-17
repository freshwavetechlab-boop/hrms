using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Payroll.API.Models;
using Payroll.API.Services;
using static Payroll.API.Repositories.RecruitmentDocumentRepository;

namespace Payroll.API.Tests.Services;

public sealed class RecruitmentDocumentClientSummaryScopeTests
{
    private sealed class NoOwnershipLookup : ISelectedClientOwnershipLookup
    {
        public Task<int?> ClientIdAsync(ScopeOwnershipReference reference, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The aggregate summary must enforce its own client scope.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("20")]
    public async Task Summary_preserves_original_identity_and_returns_all_permitted_client_counts(string? activeClient)
    {
        var user = new AuthUser { Id = 7, IsActive = true, ClientAccessMode = "Selected", AssignedClientIds = [20, 55], Permissions = ["recruitment.offer.issue"] };
        var context = new DefaultHttpContext(); context.Items["User"] = user;
        context.Request.Method = "GET"; context.Request.Path = "/api/recruitment/document-roster/client-summary";
        if (activeClient is not null) context.Request.Headers[SelectedClientScopeMiddleware.Header] = activeClient;
        context.Response.Body = new MemoryStream();
        await new SelectedClientScopeMiddleware(async ctx =>
        {
            Assert.Same(user, ctx.Items["User"]);
            Assert.Null(((AuthUser)ctx.Items["User"]!).ClientId);
            Assert.False(ctx.Request.Query.ContainsKey("clientId"));
            var result = BuildClientSummary(user, [20, 55, 99],
                [new() { ClientId = 20, RosterCount = 2 }, new() { ClientId = 55, OfferCount = 3 }, new() { ClientId = 99, OfferCount = 7 }],
                [], new Dictionary<int, int?>());
            await ctx.Response.WriteAsJsonAsync(result);
        }).InvokeAsync(context, new NoOwnershipLookup());
        context.Response.Body.Position = 0;
        var rows = (await JsonNode.ParseAsync(context.Response.Body))!.AsArray();
        Assert.Equal(new[] { 20, 55 }, rows.Select(row => (int)row!["clientId"]!));
        Assert.Equal(2, (int)rows[0]!["rosterCount"]!); Assert.Equal(3, (int)rows[1]!["offerCount"]!);
    }

    [Theory]
    [InlineData("POST", "/api/recruitment/document-roster/client-summary")]
    [InlineData("GET", "/api/recruitment/document-roster/rows")]
    [InlineData("GET", "/api/recruitment/document-roster/client-summary/other")]
    public async Task The_aggregate_exception_does_not_bypass_context_for_mutations_or_other_routes(string method, string path)
    {
        var context = new DefaultHttpContext(); context.Items["User"] = new AuthUser { Id = 7, IsActive = true, ClientAccessMode = "Selected", AssignedClientIds = [20] };
        context.Request.Method = method; context.Request.Path = path; context.Response.Body = new MemoryStream();
        var called = false;
        await new SelectedClientScopeMiddleware(_ => { called = true; return Task.CompletedTask; }).InvokeAsync(context, new NoOwnershipLookup());
        Assert.False(called); Assert.Equal(403, context.Response.StatusCode);
    }
}
