using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public class EmployeeProfileCompletionAccessTests
{
    [Fact]
    public async Task EndpointsRejectMissingPermissionsAndOtherClientsBeforeAccessingData()
    {
        var builder = WebApplication.CreateSlimBuilder();
        // No application settings, database connection or real mail provider in this host.
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var communications = new CommunicationRepository(builder.Configuration, new EphemeralDataProtectionProvider(), [], NullLogger<CommunicationRepository>.Instance);
        builder.Services.AddSingleton(communications);
        builder.Services.AddSingleton(new EmployeeProfileCompletionService(builder.Configuration, null!, null!, communications, null!));
        builder.Services.AddSingleton(new EssMssRepository(builder.Configuration));
        builder.Services.AddSingleton(new WorkflowRepository(builder.Configuration));
        var user = new AuthUser { Id = 1, ClientId = 20, EmployeeId = 10, Permissions = [] };
        await using var app = builder.Build();
        app.Use((context, next) => { context.Items["User"] = user; return next(context); });
        app.MapEmployeeProfileCompletion();
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        var routes = new[] {
            (HttpMethod.Get, "/api/employees/profile-completion?clientId=20"),
            (HttpMethod.Post, "/api/employees/profile-completion/setup"),
            (HttpMethod.Put, "/api/employees/profile-completion/settings"),
            (HttpMethod.Post, "/api/employees/profile-completion/preview"),
            (HttpMethod.Post, "/api/employees/profile-completion/send"),
            (HttpMethod.Post, "/api/ess/profile/edit-request"),
        };
        foreach (var (method, route) in routes) await Denied(method, route, 20);
        user.Permissions = ["employees.view", "employee.communication.send", "employees.manage", "ess.self"];
        foreach (var (method, route) in routes.Take(5)) await Denied(method, route.Replace("clientId=20", "clientId=30"), 30);
        user.Permissions = ["employees.view"];
        foreach (var (method, route) in routes.Skip(1).Take(4)) await Denied(method, route, 20);
        user.Permissions = ["ess.self"];
        user.EmployeeId = null;
        await Denied(HttpMethod.Post, "/api/ess/profile/edit-request", 20);

        async Task Denied(HttpMethod method, string route, int clientId)
        {
            using var request = new HttpRequestMessage(method, route);
            if (method != HttpMethod.Get) request.Content = JsonContent.Create(new { clientId, firstEditEnabled = true, employeeIds = new[] { 10 }, idempotencyKey = "test-only", comment = "Update my bank details" });
            using var response = await http.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }
}
