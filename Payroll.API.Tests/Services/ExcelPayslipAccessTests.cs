using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayslipAccessTests
{
    [Fact]
    public async Task PayslipRoutesRejectUnauthorizedAndCrossClientRequestsBeforeDatabaseOrMailAccess()
    {
        // Isolated route host: no production Program, appsettings, database or notification worker.
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(new ExcelPayslipRepository(builder.Configuration, null!, null!));
        builder.Services.AddSingleton(new ExcelPayslipPdfService());
        var user = new AuthUser { Id = 1, ClientId = 20 };
        var canRead = false;
        var canManage = false;
        await using var app = builder.Build();
        ExcelPayslipEndpoints.Map(app, _ => canRead, _ => canManage,
            (_, clientId) => user.ClientId == clientId, _ => user);
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        var signature = new string('a', 64);
        var routes = new[]
        {
            (HttpMethod.Get, "/dashboard"),
            (HttpMethod.Get, "/dashboard?batchId=0123456789abcdef0123456789abcdef"),
            (HttpMethod.Get, "/templates"),
            (HttpMethod.Get, $"/profile?headerSignature={signature}"),
            (HttpMethod.Put, $"/profile?headerSignature={signature}"),
            (HttpMethod.Get, "/batches"),
            (HttpMethod.Post, "/batches"),
            (HttpMethod.Get, "/batches/0123456789abcdef0123456789abcdef"),
            (HttpMethod.Get, "/batches/0123456789abcdef0123456789abcdef/delivery-status"),
            (HttpMethod.Get, "/batches/0123456789abcdef0123456789abcdef/mail-jobs"),
            (HttpMethod.Post, "/batches/0123456789abcdef0123456789abcdef/send-job"),
            (HttpMethod.Post, "/batches/0123456789abcdef0123456789abcdef/email-request"),
            (HttpMethod.Post, "/batches/0123456789abcdef0123456789abcdef/mail-jobs/fedcba9876543210fedcba9876543210/dismiss"),
            (HttpMethod.Get, "/batches/0123456789abcdef0123456789abcdef/calculation"),
            (HttpMethod.Post, "/batches/0123456789abcdef0123456789abcdef/calculate"),
            (HttpMethod.Get, "/batches/0123456789abcdef0123456789abcdef/variance?previousBatchId=fedcba9876543210fedcba9876543210"),
            (HttpMethod.Post, "/batches/0123456789abcdef0123456789abcdef/pdf"),
            (HttpMethod.Post, "/batches/0123456789abcdef0123456789abcdef/send")
        };
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("/api/excel-payslips/clients")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("/api/excel-payslips/mail-jobs")).StatusCode);
        foreach (var (method, path) in routes) await Expect(method, path, "20", HttpStatusCode.Forbidden);
        canRead = canManage = true;
        foreach (var (method, path) in routes) await Expect(method, path, "21", HttpStatusCode.Forbidden);
        canManage = false;
        foreach (var (method, path) in routes.Where(r => r.Item1 != HttpMethod.Get && !r.Item2.EndsWith("/pdf")))
            await Expect(method, path, "20", HttpStatusCode.Forbidden);
        foreach (var clientId in new[] { "", "0", "-1", "wrong" })
            await Expect(HttpMethod.Get, "/batches", clientId, HttpStatusCode.BadRequest);

        async Task Expect(HttpMethod method, string path, string clientId, HttpStatusCode expected)
        {
            var query = path.Contains('?') ? "&" : "?";
            using var request = new HttpRequestMessage(method, "/api/excel-payslips" + path + query + "clientId=" + clientId);
            if (method != HttpMethod.Get) request.Content = JsonContent.Create(new { rowIds = new[] { "row-6" }, rows = Array.Empty<ExcelPayslipRow>() });
            using var response = await http.SendAsync(request);
            Assert.Equal(expected, response.StatusCode);
            Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? "");
        }
    }
}
