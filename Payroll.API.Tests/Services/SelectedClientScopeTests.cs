using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class SelectedClientScopeTests
{
    private static AuthUser Selected(params int[] ids)=>new(){Id=8,IsActive=true,ClientAccessMode="Selected",AssignedClientIds=[..ids],Permissions=["employees.manage","reports.view"]};
    private sealed class Ownership : ISelectedClientOwnershipLookup
    {
        public Dictionary<ScopeOwnershipReference,int?> Rows {get;}=[];
        public Task<int?> ClientIdAsync(ScopeOwnershipReference reference,CancellationToken cancellationToken=default)=>Task.FromResult(Rows.GetValueOrDefault(reference));
    }
    private static DefaultHttpContext Context(AuthUser user,string method,string path,string? active="20",string? body=null,string query="")
    {
        var c=new DefaultHttpContext(); c.Items["User"]=user;c.Request.Method=method;c.Request.Path=path;c.Request.QueryString=new(query);
        if(active is not null)c.Request.Headers[SelectedClientScopeMiddleware.Header]=active;
        c.Response.Body=new MemoryStream();
        if(body is not null){var bytes=Encoding.UTF8.GetBytes(body);c.Request.Body=new MemoryStream(bytes);c.Request.ContentLength=bytes.Length;c.Request.ContentType="application/json";}
        return c;
    }
    [Fact] public async Task SelectedRequestUsesEffectiveClientButDoesNotMutateAuthenticatedIdentity()
    {
        var user=Selected(20,21);var c=Context(user,"GET","/api/reports/payroll-summary");var called=false;
        await new SelectedClientScopeMiddleware(ctx=>{called=true;Assert.Equal(20,((AuthUser)ctx.Items["User"]!).ClientId);Assert.Equal("20",ctx.Request.Query["clientId"]);return Task.CompletedTask;}).InvokeAsync(c,new Ownership());
        Assert.True(called);Assert.Null(user.ClientId);Assert.Equal(new[]{20,21},user.AssignedClientIds);
    }
    [Theory][InlineData("20","?clientId=21")][InlineData("99","")][InlineData("0","")][InlineData(null,"")]
    public async Task MissingUnassignedAndConflictingContextNeverReachBusinessHandler(string? active,string query)
    {
        var c=Context(Selected(20,21),"GET","/api/employees",active,query:query);var called=false;
        await new SelectedClientScopeMiddleware(_=>{called=true;return Task.CompletedTask;}).InvokeAsync(c,new Ownership());Assert.False(called);Assert.Equal(403,c.Response.StatusCode);
    }
    [Fact] public async Task ExplicitAssignedContextWithoutHeaderWorksForNativeDownloads()
    {
        var c=Context(Selected(20,21),"GET","/api/reports/payroll-summary",null,query:"?clientId=21");var called=false;
        await new SelectedClientScopeMiddleware(ctx=>{called=true;Assert.Equal(21,((AuthUser)ctx.Items["User"]!).ClientId);return Task.CompletedTask;}).InvokeAsync(c,new Ownership());Assert.True(called);
    }
    [Fact] public async Task ForeignBodyIdCannotBeReassignedByClaimingAnAllowedClient()
    {
        var owner=new Ownership();owner.Rows[new("employee","44")]=21;var c=Context(Selected(20),"POST","/api/employees",body:"{\"id\":44,\"clientId\":20,\"firstName\":\"x\"}");var called=false;
        await new SelectedClientScopeMiddleware(_=>{called=true;return Task.CompletedTask;}).InvokeAsync(c,owner);Assert.False(called);Assert.Equal(403,c.Response.StatusCode);
    }
    [Theory][InlineData("DELETE","/api/employees/44")][InlineData("GET","/api/employees/44")][InlineData("GET","/api/pay-runs/44/export")]
    public async Task RecordAndDownloadOwnershipIsCheckedBeforeHandler(string method,string path)
    {
        var c=Context(Selected(20),method,path);var called=false;
        await new SelectedClientScopeMiddleware(_=>{called=true;return Task.CompletedTask;}).InvokeAsync(c,new Ownership());Assert.False(called);Assert.Equal(403,c.Response.StatusCode);
    }
    [Fact] public async Task NestedEmployeeSelectionsAreValidated()
    {
        var owner=new Ownership();owner.Rows[new("employee","1")]=20;owner.Rows[new("employee","2")]=21;
        var c=Context(Selected(20,21),"POST","/api/employees/mark-resigned",body:"{\"clientId\":20,\"employeeIds\":[1,2]}");var called=false;
        await new SelectedClientScopeMiddleware(_=>{called=true;return Task.CompletedTask;}).InvokeAsync(c,owner);Assert.False(called);Assert.Equal(403,c.Response.StatusCode);
    }
    [Fact] public async Task WorkflowResourcePairIsCheckedIndependentlyOfSharedWorkflowDefinition()
    {
        var owner=new Ownership();owner.Rows[new("employee","8")]=21;owner.Rows[new("workflow","1",true)]=0;
        var c=Context(Selected(20),"POST","/api/workflows/start",body:"{\"workflowId\":1,\"resourceType\":\"Employee\",\"resourceId\":\"8\"}");var called=false;
        await new SelectedClientScopeMiddleware(_=>{called=true;return Task.CompletedTask;}).InvokeAsync(c,owner);Assert.False(called);Assert.Equal(403,c.Response.StatusCode);
    }
    [Fact] public async Task NewCreationGetsContextAndRetainsPayload()
    {
        var c=Context(Selected(20),"POST","/api/employees",body:"{\"id\":0,\"firstName\":\"New\"}");var called=false;
        await new SelectedClientScopeMiddleware(async ctx=>{called=true;var body=await JsonNode.ParseAsync(ctx.Request.Body);Assert.Equal(20,(int)body!["clientId"]!);Assert.Equal("New",(string?)body["firstName"]);}).InvokeAsync(c,new Ownership());Assert.True(called);
    }
    [Theory]
    [InlineData("{\"clientId\":null,\"name\":\"Scoped definition\"}")]
    [InlineData("{\"clientId\":\"\",\"name\":\"Scoped definition\"}")]
    public async Task EmptyClientFieldCannotCreateGlobalRecord(string body)
    {
        var c=Context(Selected(20),"POST","/api/recruitment-orchestration/forms",body:body);var called=false;
        await new SelectedClientScopeMiddleware(async ctx=>{called=true;var submitted=await JsonNode.ParseAsync(ctx.Request.Body);Assert.Equal(20,(int)submitted!["clientId"]!);Assert.Equal("Scoped definition",(string?)submitted["name"]);}).InvokeAsync(c,new Ownership());
        Assert.True(called);
    }
    [Fact] public async Task NestedEmptyCompanyContextIsScopedAndExplicitZeroIsDenied()
    {
        var c=Context(Selected(20),"POST","/api/travel-expense/policies",body:"{\"id\":0,\"companyId\":null,\"rules\":[{\"companyId\":null}]}");var called=false;
        await new SelectedClientScopeMiddleware(async ctx=>{called=true;var submitted=await JsonNode.ParseAsync(ctx.Request.Body);Assert.Equal(20,(int)submitted!["companyId"]!);Assert.Equal(20,(int)submitted["rules"]![0]!["companyId"]!);}).InvokeAsync(c,new Ownership());Assert.True(called);
        var zero=Context(Selected(20),"POST","/api/travel-expense/policies",body:"{\"companyId\":0}");called=false;
        await new SelectedClientScopeMiddleware(_=>{called=true;return Task.CompletedTask;}).InvokeAsync(zero,new Ownership());Assert.False(called);Assert.Equal(403,zero.Response.StatusCode);
    }
    [Fact] public async Task EmptyClientListCannotMeanAllClients()
    {
        var c=Context(Selected(20,21),"POST","/api/reports/payroll-summary",body:"{\"clientIds\":[]}",query:"?clientIds=");var called=false;
        await new SelectedClientScopeMiddleware(async ctx=>{called=true;var submitted=await JsonNode.ParseAsync(ctx.Request.Body);Assert.Equal(20,(int)submitted!["clientIds"]![0]!);Assert.Equal("20",ctx.Request.Query["clientIds"]);}).InvokeAsync(c,new Ownership());Assert.True(called);
    }
    [Fact] public async Task MultipartEmbeddedOptionalClientIsScopedBeforeHandler()
    {
        var c=Context(Selected(20),"POST","/api/recruitment/candidates");c.Request.ContentType="multipart/form-data; boundary=fixture";
        c.Request.Form=new FormCollection(new Dictionary<string,Microsoft.Extensions.Primitives.StringValues>{["candidate"]="{\"id\":0,\"clientId\":null,\"name\":\"Fixture\"}",["companyId"]=""});var called=false;
        await new SelectedClientScopeMiddleware(ctx=>{called=true;var submitted=JsonNode.Parse(ctx.Request.Form["candidate"].ToString());Assert.Equal(20,(int)submitted!["clientId"]!);Assert.Equal("20",ctx.Request.Form["companyId"]);return Task.CompletedTask;}).InvokeAsync(c,new Ownership());Assert.True(called);
    }
    [Fact] public async Task HistoricalInboxRowsFromRevokedClientsAreNotReturned()
    {
        var c=Context(Selected(20),"GET","/api/workflows/tasks/pending");var lookup=new Ownership();lookup.Rows[new("workflow-instance","1")]=20;lookup.Rows[new("workflow-instance","2")]=21;
        await new SelectedClientScopeMiddleware(ctx=>ctx.Response.WriteAsJsonAsync(new[]{new{instanceId=1,name="allowed"},new{instanceId=2,name="other"}})).InvokeAsync(c,lookup);
        c.Response.Body.Position=0;var result=await JsonNode.ParseAsync(c.Response.Body);Assert.Single(result!.AsArray());Assert.Equal("allowed",(string?)result[0]!["name"]);
    }
    [Theory][InlineData("/api/security/users")][InlineData("/api/organization")][InlineData("/api/client-billing/module")][InlineData("/api/new-unreviewed-workspace")]
    public async Task GlobalOrUnknownMutationIsDenied(string path)
    {
        var c=Context(Selected(20),"POST",path,body:"{}");var called=false;
        await new SelectedClientScopeMiddleware(_=>{called=true;return Task.CompletedTask;}).InvokeAsync(c,new Ownership());Assert.False(called);Assert.Equal(403,c.Response.StatusCode);
    }
    [Theory][InlineData(null)][InlineData(20)] public async Task LegacyContextsAreUnchanged(int? client)
    {
        var user=new AuthUser{Id=2,IsActive=true,ClientId=client};var c=Context(user,"GET","/api/employees",null);var called=false;
        await new SelectedClientScopeMiddleware(ctx=>{called=true;Assert.Same(user,ctx.Items["User"]);Assert.False(ctx.Request.Query.ContainsKey("clientId"));return Task.CompletedTask;}).InvokeAsync(c,new Ownership());Assert.True(called);
    }
    [Fact] public async Task OnlyUnrestrictedAdministratorCanOpenExcelWorkspace()
    {
        foreach(var user in new[]{Selected(20),new AuthUser{Id=3,IsActive=true,ClientId=20,Roles=["client_admin"],Permissions=["settings.manage"]},new AuthUser{Id=4,IsActive=true,Permissions=["reports.payroll.view"]}})
        {
            var c=Context(user,"GET","/api/excel-payslips/batches");var called=false;
            await new SelectedClientScopeMiddleware(_=>{called=true;return Task.CompletedTask;}).InvokeAsync(c,new Ownership());Assert.False(called);Assert.Equal(403,c.Response.StatusCode);
        }
        var admin=new AuthUser{Id=1,IsActive=true,Roles=["super_admin"]};var ok=Context(admin,"GET","/api/excel-payslips/batches",null);var allowed=false;
        await new SelectedClientScopeMiddleware(_=>{allowed=true;return Task.CompletedTask;}).InvokeAsync(ok,new Ownership());Assert.True(allowed);
    }
}
