using System.Text;
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

public sealed class ExcelPayslipOverviewAccessTests
{
    [Fact]
    public async Task OverviewPermissionsAndWholeScopeValidationRunBeforeDatabaseAccess()
    {
        var builder = WebApplication.CreateSlimBuilder(); builder.Configuration.Sources.Clear(); builder.Configuration.AddInMemoryCollection(); builder.Logging.ClearProviders();
        builder.Services.AddSingleton(new ExcelPayslipRepository(builder.Configuration, null!, null!)); builder.Services.AddSingleton(new ExcelPayslipPdfService());
        var read = false; var user = new AuthUser { Id = 1, ClientId = 42 };
        await using var app = builder.Build(); ExcelPayslipEndpoints.Map(app, _ => read, _ => false, (_, client) => client == 42, _ => user);
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>().ToArray();
        await Check("GetExcelPayslipOverviewScopes", "", 403);
        await Check("PreviewExcelPayslipOverview", "{\"scopes\":[]}", 403);
        read = true;
        // A read-only POST does not require send/manage permission; invalid scope is rejected without opening a database.
        await Check("PreviewExcelPayslipOverview", "{\"scopes\":[]}", 400);
        await Check("PreviewExcelPayslipOverview", "{\"scopes\":[{\"clientId\":43,\"batchId\":\"0123456789abcdef0123456789abcdef\"}]}", 403);
        await Check("PreviewExcelPayslipOverview", "{\"scopes\":[{\"clientId\":42,\"batchId\":\"0123456789abcdef0123456789abcdef\"},{\"clientId\":43,\"batchId\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\"}]}", 403);
        async Task Check(string name, string json, int expected)
        {
            var endpoint = endpoints.Single(e => e.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == name);
            var ctx = new DefaultHttpContext { RequestServices = app.Services }; ctx.SetEndpoint(endpoint); ctx.Response.Body = new MemoryStream();
            ctx.Request.Path = name == "GetExcelPayslipOverviewScopes" ? "/api/excel-payslips/overview-scopes" : "/api/excel-payslips/overview-preview";
            ctx.Request.Method = name == "GetExcelPayslipOverviewScopes" ? "GET" : "POST";
            if (json.Length > 0) { ctx.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyDetection()); var bytes = Encoding.UTF8.GetBytes(json); ctx.Request.Body = new MemoryStream(bytes); ctx.Request.ContentLength = bytes.Length; ctx.Request.ContentType = "application/json"; }
            await endpoint.RequestDelegate!(ctx); ctx.Response.Body.Position = 0; var response = await new StreamReader(ctx.Response.Body).ReadToEndAsync(); Assert.True(expected == ctx.Response.StatusCode, $"Expected {expected}, got {ctx.Response.StatusCode}: {response}"); Assert.Equal("no-store", ctx.Response.Headers.CacheControl.ToString());
        }
    }
    private sealed class BodyDetection : IHttpRequestBodyDetectionFeature { public bool CanHaveBody => true; }
}
