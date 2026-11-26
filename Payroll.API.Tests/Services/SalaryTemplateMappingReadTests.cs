using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class SalaryTemplateMappingReadTests
{
    [Fact]
    public async Task Successful_mapping_result_passes_through_as_returned()
    {
        var context = new DefaultHttpContext();
        var result = Results.Ok(new { mappings = new[] { "employee salary" } });
        var actual = await SalaryTemplateEndpoints.HandleMappingRead(new DefaultEndpointFilterInvocationContext(context), _ => ValueTask.FromResult<object?>(result));
        Assert.Same(result, actual);
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
    }

    private sealed class StartedResponseFeature : HttpResponseFeature
    {
        public override bool HasStarted => true;
    }

    [Fact]
    public async Task Already_started_responses_keep_their_headers_and_propagate_the_original_error()
    {
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpResponseFeature>(new StartedResponseFeature { Headers = new HeaderDictionary { IsReadOnly = true } });
        var failure = new InvalidOperationException("failure after response started");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => SalaryTemplateEndpoints.HandleMappingRead(
            new DefaultEndpointFilterInvocationContext(context), _ => ValueTask.FromException<object?>(failure)).AsTask());
        Assert.Same(failure, error);
        Assert.Empty(context.Response.Headers);
    }

    [Theory]
    [InlineData("PreviewClientSalaryTemplateDeletion")]
    [InlineData("EditableSalaryTemplateMappings")]
    public async Task Inspection_errors_do_not_turn_into_empty_mappings_or_expose_database_details(string name)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.Sources.Clear();
        // Invalid connection-string syntax fails locally, before any database connection.
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = "PrivateFixtureKey=test" });
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<SettingsRepository>();
        builder.Services.AddSingleton(new PayslipDesignRepository(builder.Configuration, null!, null!));
        await using var app = builder.Build();
        var allowAccess = false;
        var timingOut = false;
        SalaryTemplateEndpoints.Map(app, _ => new AuthUser { Id = 1, IsActive = true, Permissions = ["settings.manage"] }, (context, _) =>
        {
            context.RequestAborted.ThrowIfCancellationRequested();
            if (timingOut) throw new TimeoutException("private timeout details");
            return allowAccess;
        });
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .Single(e => e.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == name);

        async Task<DefaultHttpContext> Invoke(CancellationToken cancellation = default)
        {
            var context = new DefaultHttpContext { RequestServices = app.Services, TraceIdentifier = "fixture-request", RequestAborted = cancellation };
            context.SetEndpoint(endpoint);
            context.Request.Method = HttpMethods.Get;
            context.Request.RouteValues = new RouteValueDictionary { ["clientId"] = "70", ["templateId"] = "101" };
            context.Response.Body = new MemoryStream();
            await endpoint.RequestDelegate!(context);
            return context;
        }

        Assert.Equal(StatusCodes.Status403Forbidden, (await Invoke()).Response.StatusCode);
        allowAccess = true;
        await AssertFailure(StatusCodes.Status500InternalServerError);
        timingOut = true;
        await AssertFailure(StatusCodes.Status503ServiceUnavailable);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Invoke(new CancellationToken(canceled: true)));

        async Task AssertFailure(int status)
        {
            var context = await Invoke();
            Assert.Equal(status, context.Response.StatusCode);
            Assert.Equal("fixture-request", context.Response.Headers["X-Request-ID"].ToString());
            Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
            context.Response.Body.Position = 0;
            using var payload = await JsonDocument.ParseAsync(context.Response.Body);
            Assert.Equal("fixture-request", payload.RootElement.GetProperty("requestId").GetString());
            Assert.Equal("Unable to check salary template mappings. Reference fixture-request.", payload.RootElement.GetProperty("error").GetString());
            Assert.False(payload.RootElement.TryGetProperty("mappings", out _));
        }
    }
}
