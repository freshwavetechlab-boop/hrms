using System.Text;
using Microsoft.AspNetCore.Http;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class SelectedClientAttachmentScopeTests
{
    private sealed class Lookup(int owner) : ISelectedClientOwnershipLookup
    {
        public Task<int?> ClientIdAsync(ScopeOwnershipReference reference, CancellationToken cancellationToken = default) =>
            Task.FromResult<int?>(reference.Resource == "attachment-attribute" && reference.AllowGlobal ? 0 : owner);
    }

    [Theory]
    [InlineData("/api/attachment-attributes", 20, true)]
    [InlineData("/api/attachment-attributes", 21, false)]
    [InlineData("/api/attachment-attributes", 0, false)]
    [InlineData("/api/attachment-configurations", 20, true)]
    [InlineData("/api/attachment-configurations", 21, false)]
    [InlineData("/api/attachment-configurations", 0, false)]
    public async Task TenantConfigurationCanUseSharedAttributeButCannotEditForeignOrSharedRoot(string path, int owner, bool allowed)
    {
        var context = new DefaultHttpContext(); context.Response.Body = new MemoryStream();
        context.Items["User"] = new AuthUser { Id = 8, IsActive = true, ClientAccessMode = "Selected", AssignedClientIds = [20] };
        context.Request.Method = "POST"; context.Request.Path = path; context.Request.Headers[SelectedClientScopeMiddleware.Header] = "20";
        var payload = Encoding.UTF8.GetBytes("{\"id\":8,\"clientId\":20,\"attachmentAttributeId\":9}");
        context.Request.Body = new MemoryStream(payload); context.Request.ContentLength = payload.Length; context.Request.ContentType = "application/json";
        var called = false;
        await new SelectedClientScopeMiddleware(_ => { called = true; return Task.CompletedTask; }).InvokeAsync(context, new Lookup(owner));
        Assert.Equal(allowed, called);
        Assert.Equal(allowed ? 200 : 403, context.Response.StatusCode);
    }
}
