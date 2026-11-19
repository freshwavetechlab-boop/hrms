using System.Text;
using Microsoft.AspNetCore.Http;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;
using Xunit;

namespace Payroll.API.Tests.Services;

public sealed class SelectedRecruitmentAggregateTests
{
    private static AuthUser User() => new() { Id = 8, IsActive = true, ClientAccessMode = "Selected", AssignedClientIds = [20, 55] };
    private sealed class Lookup : ISelectedClientOwnershipLookup
    {
        internal Dictionary<(string, string), int?> Rows { get; } = [];
        public Task<int?> ClientIdAsync(ScopeOwnershipReference value, CancellationToken cancellationToken = default) => Task.FromResult(Rows.GetValueOrDefault((value.Resource, value.Value)));
    }
    private static DefaultHttpContext Context(string method, string path, string? body = null, string? header = null)
    {
        var context = new DefaultHttpContext(); context.Items["User"] = User(); context.Request.Method = method; context.Request.Path = path; context.Response.Body = new MemoryStream();
        if (header is not null) context.Request.Headers[SelectedClientScopeMiddleware.Header] = header;
        if (body is not null) { var bytes = Encoding.UTF8.GetBytes(body); context.Request.Body = new MemoryStream(bytes); context.Request.ContentLength = bytes.Length; context.Request.ContentType = "application/json"; }
        return context;
    }
    [Theory]
    [InlineData("/api/recruitment/dashboard")][InlineData("/api/recruitment/requisitions")][InlineData("/api/recruitment/open-positions")]
    [InlineData("/api/recruitment/applications")][InlineData("/api/recruitment/interviews")][InlineData("/api/recruitment/offers")]
    [InlineData("/api/recruitment/hiring-cases")][InlineData("/api/recruitment/pipeline-workspace")][InlineData("/api/recruitment/pipeline-targets")]
    [InlineData("/api/recruitment/client-configuration-summary")][InlineData("/api/recruitment-orchestration/job-postings")][InlineData("/api/recruitment-orchestration/pipelines")]
    public async Task AuditedAggregateReadsKeepOriginalAssignedIdentity(string path)
    {
        var context = Context("GET", path); var original = context.Items["User"]; var called = false;
        await new SelectedClientScopeMiddleware(c => { called = true; Assert.Same(original, c.Items["User"]); Assert.Null(((AuthUser)c.Items["User"]!).ClientId); Assert.False(c.Request.Query.ContainsKey("clientId")); return Task.CompletedTask; }).InvokeAsync(context, new Lookup());
        Assert.True(called);
    }
    [Theory][InlineData("POST", "/api/recruitment/requisitions")][InlineData("GET", "/api/employees")][InlineData("GET", "/api/excel-payslips/batches")]
    public async Task UnrelatedReadsAndWritesDoNotAcquireAggregateAuthority(string method, string path)
    {
        var context = Context(method, path); var called = false;
        await new SelectedClientScopeMiddleware(_ => { called = true; return Task.CompletedTask; }).InvokeAsync(context, new Lookup());
        Assert.False(called); Assert.Equal(403, context.Response.StatusCode);
    }
    [Fact]
    public async Task SingleClientHeaderStillProjectsAnAuditedRead()
    {
        var context = Context("GET", "/api/recruitment/applications", header: "20"); var called = false;
        await new SelectedClientScopeMiddleware(c => { called = true; Assert.Equal(20, ((AuthUser)c.Items["User"]!).ClientId); return Task.CompletedTask; }).InvokeAsync(context, new Lookup());
        Assert.True(called);
    }
    [Fact]
    public async Task AggregateConfigurationReadRejectsUnassignedRouteOwnership()
    {
        var context = Context("GET", "/api/recruitment-orchestration/pipeline-versions/13"); var lookup = new Lookup(); lookup.Rows[("pipeline-version", "13")] = 99; var called = false;
        await new SelectedClientScopeMiddleware(_ => { called = true; return Task.CompletedTask; }).InvokeAsync(context, lookup);
        Assert.False(called); Assert.Equal(403, context.Response.StatusCode);
    }
    [Theory][InlineData(55, true)][InlineData(99, false)]
    public async Task AtomicConfigurationChecksDefinitionVersionAndStageOwnershipDespiteStaleHeader(int stageClient, bool allowed)
    {
        var context = Context("POST", "/api/recruitment-orchestration/pipeline-configurations", """{"definition":{"id":8,"clientId":55},"configuration":{"id":13,"pipelineDefinitionId":8,"stages":[{"id":33}],"transitions":[]}}""", "20");
        var lookup = new Lookup(); lookup.Rows[("pipeline", "8")] = 55; lookup.Rows[("pipeline-version", "13")] = 55; lookup.Rows[("pipeline-stage", "33")] = stageClient; var called = false;
        await new SelectedClientScopeMiddleware(c => { called = true; Assert.Null(((AuthUser)c.Items["User"]!).ClientId); return Task.CompletedTask; }).InvokeAsync(context, lookup);
        Assert.Equal(allowed, called); if (!allowed) Assert.Equal(403, context.Response.StatusCode);
    }
    [Theory][InlineData(20,55,true)][InlineData(99,55,false)][InlineData(20,99,false)]
    public async Task CloneRequiresBothAssignedSourceAndTarget(int sourceClient, int targetClient, bool allowed)
    {
        var context = Context("POST", "/api/recruitment-orchestration/pipelines/8/create-similar", $$"""{"sourceVersionId":13,"targetClientId":{{targetClient}}}""", "20");
        var lookup = new Lookup(); lookup.Rows[("pipeline", "8")] = sourceClient; lookup.Rows[("pipeline-version", "13")] = sourceClient; var called = false;
        await new SelectedClientScopeMiddleware(c => { called = true; Assert.Null(((AuthUser)c.Items["User"]!).ClientId); return Task.CompletedTask; }).InvokeAsync(context, lookup);
        Assert.Equal(allowed, called); if (!allowed) Assert.Equal(403, context.Response.StatusCode);
    }
    [Theory][InlineData(55,55,true)][InlineData(99,99,false)][InlineData(55,20,false)]
    public async Task ScoreComponentUsesExplicitAssignedClientAndValidatesExistingOwner(int bodyClient, int recordClient, bool allowed)
    {
        var context = Context("POST", "/api/recruitment-orchestration/interview-competencies", $$"""{"id":12,"clientId":{{bodyClient}},"competencyCode":"TEAMWORK"}""");
        var lookup = new Lookup(); lookup.Rows[("interview-competency", "12")] = recordClient; var called = false;
        await new SelectedClientScopeMiddleware(c => { called = true; Assert.Equal(bodyClient, ((AuthUser)c.Items["User"]!).ClientId); return Task.CompletedTask; }).InvokeAsync(context, lookup);
        Assert.Equal(allowed, called); if (!allowed) Assert.Equal(403, context.Response.StatusCode);
    }
    [Fact]
    public async Task ClientWideLocationModeStillFiltersAssignedClientsBeforeReturningRows()
    {
        await using var unusedConnection = new MySqlConnection("Server=127.0.0.1;");
        var rows = await RecruitmentAccessScope.FilterAsync(unusedConnection, User(), new[] { (Id: 1, Client: 20), (Id: 2, Client: 55), (Id: 3, Client: 99) }, row => row.Client, _ => "Location");
        Assert.Equal(new[] { 1, 2 }, rows.Select(row => row.Id));
    }

    [Theory]
    [InlineData("/api/recruitment-admin/templates/8/create-similar", "recruitment-template", 20, 55, true)]
    [InlineData("/api/recruitment-admin/templates/8/create-similar", "recruitment-template", 0, 55, true)]
    [InlineData("/api/recruitment-admin/templates/8/create-similar", "recruitment-template", 99, 55, false)]
    [InlineData("/api/recruitment-admin/templates/8/create-similar", "recruitment-template", 20, 99, false)]
    [InlineData("/api/recruitment-orchestration/forms/8/create-similar", "form", 20, 55, true)]
    [InlineData("/api/recruitment-orchestration/forms/8/create-similar", "form", 0, 55, true)]
    [InlineData("/api/recruitment-orchestration/forms/8/create-similar", "form", 99, 55, false)]
    [InlineData("/api/recruitment-orchestration/forms/8/create-similar", "form", 20, 99, false)]
    public async Task TemplateAndCandidateFormCopiesRequireBothAssignedClientsDespiteStaleHeader(string path, string resource, int sourceClient, int targetClient, bool allowed)
    {
        var context = Context("POST", path, $$"""{"targetClientId":{{targetClient}}}""", "20");
        var lookup = new Lookup(); lookup.Rows[(resource, "8")] = sourceClient; var called = false;
        await new SelectedClientScopeMiddleware(c => { called = true; Assert.Null(((AuthUser)c.Items["User"]!).ClientId); return Task.CompletedTask; }).InvokeAsync(context, lookup);
        Assert.Equal(allowed, called); if (!allowed) Assert.Equal(403, context.Response.StatusCode);
    }

    [Theory][InlineData(20, true)][InlineData(99, false)]
    public async Task CandidateFormCopyChecksRequestedSourceVersionOwner(int versionClient, bool allowed)
    {
        var context = Context("POST", "/api/recruitment-orchestration/forms/8/create-similar", """{"sourceVersionId":13,"targetClientId":55}""", "20");
        var lookup = new Lookup(); lookup.Rows[("form", "8")] = 20; lookup.Rows[("form-version", "13")] = versionClient; var called = false;
        await new SelectedClientScopeMiddleware(_ => { called = true; return Task.CompletedTask; }).InvokeAsync(context, lookup);
        Assert.Equal(allowed, called); if (!allowed) Assert.Equal(403, context.Response.StatusCode);
    }

    [Theory][InlineData("/api/recruitment-admin/templates")][InlineData("/api/recruitment-orchestration/forms")]
    public async Task AllPermittedConfigurationListsKeepTheOriginalSelectedIdentity(string path)
    {
        var context = Context("GET", path); var original = context.Items["User"]; var called = false;
        await new SelectedClientScopeMiddleware(c => { called = true; Assert.Same(original, c.Items["User"]); return Task.CompletedTask; }).InvokeAsync(context, new Lookup());
        Assert.True(called);
    }

    [Theory][InlineData(55, true)][InlineData(99, false)]
    public async Task OrdinaryCopiedConfigurationSaveUsesExplicitClientAndProtectsOwnership(int owner, bool allowed)
    {
        foreach (var (path, resource) in new[] { ("/api/recruitment-admin/templates", "recruitment-template"), ("/api/recruitment-orchestration/forms", "form") })
        {
            var context = Context("POST", path, """{"id":8,"clientId":55}"""); var lookup = new Lookup(); lookup.Rows[(resource, "8")] = owner; var called = false;
            await new SelectedClientScopeMiddleware(c => { called = true; Assert.Equal(55, ((AuthUser)c.Items["User"]!).ClientId); return Task.CompletedTask; }).InvokeAsync(context, lookup);
            Assert.Equal(allowed, called); if (!allowed) Assert.Equal(403, context.Response.StatusCode);
        }
    }
}
