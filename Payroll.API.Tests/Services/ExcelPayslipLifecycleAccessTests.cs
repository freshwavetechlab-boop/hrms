using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayslipLifecycleAccessTests
{
    [Fact]
    public async Task LifecycleRoutesDenyMissingPermissionsAndOtherClientsWithoutStartingHostOrAccessingData()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.Sources.Clear(); builder.Configuration.AddInMemoryCollection(); builder.Logging.ClearProviders();
        // Empty configuration deliberately has no database connection or real file-server services.
        builder.Services.AddSingleton(new ExcelPayslipRepository(builder.Configuration, null!, null!));
        builder.Services.AddSingleton(new ExcelPayslipPdfService());
        builder.Services.AddSingleton(new AttachmentRepository(builder.Configuration, null!, null!, null!, null!, NullLogger<AttachmentRepository>.Instance));
        builder.Services.AddSingleton(new AttachmentStorageService(null!, null!, builder.Configuration, null!));
        var user = new AuthUser { Id = 1, ClientId = 20, Permissions = [] };
        var canRead = true; var canManage = false;
        await using var app = builder.Build();
        ExcelPayslipEndpoints.Map(app, _ => canRead, _ => canManage, (_, clientId) => user.ClientId == clientId, _ => user);
        // Invoke actual compiled route delegates in memory: no TCP listener, Program startup or hosted workers.
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().ToArray();
        var names = new[] { "RetainExcelPayslipOriginal", "GetExcelPayslipOriginals", "DownloadExcelPayslipOriginal", "PreviewExcelPayslipDeletion", "DeleteUnusedExcelPayslipBatch", "ReviewExcelPayslipLegacyVersion", "ResolveExcelPayslipCleanupBlocker" };
        foreach (var name in names) await Denied(name, 20);
        canManage = true;
        foreach (var name in names.Skip(3)) await Denied(name, 20); // payslip manager without settings permission
        user.Permissions = ["settings.manage"];
        foreach (var name in names) await Denied(name, 21); // correct permissions, wrong client
        canRead = false;
        foreach (var name in names) await Denied(name, 20);

        async Task Denied(string name, int clientId)
        {
            var endpoint = endpoints.Single(item => item.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == name);
            var context = new DefaultHttpContext { RequestServices = app.Services };
            context.SetEndpoint(endpoint);
            context.Request.Method = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Single();
            context.Request.Path = "/api/excel-payslips/batches/0123456789abcdef0123456789abcdef";
            context.Request.QueryString = new QueryString("?clientId=" + clientId);
            context.Request.RouteValues["id"] = "0123456789abcdef0123456789abcdef";
            context.Request.RouteValues["originalId"] = "fedcba9876543210fedcba9876543210";
            if (context.Request.Method == "POST")
            {
                var body = Encoding.UTF8.GetBytes("{\"token\":\"test-only\",\"confirmBatchId\":\"0123456789abcdef0123456789abcdef\"}");
                context.Request.Body = new MemoryStream(body); context.Request.ContentLength = body.Length; context.Request.ContentType = "application/json";
            }
            context.Response.Body = new MemoryStream();
            await endpoint.RequestDelegate!(context);
            Assert.Equal(403, context.Response.StatusCode);
            Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
        }
    }
}
