using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public class EmployeeFieldAccessTests
{
    [Fact]
    public async Task FieldEndpointsEnforceEmployeePermissionsAndClientScopeBeforeDatabaseAccess()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.Sources.Clear(); builder.Configuration.AddInMemoryCollection(); builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(new EmployeeAttributeRepository(builder.Configuration));
        builder.Services.AddSingleton(new RecruitmentFormRepository(builder.Configuration, NullLogger<RecruitmentFormRepository>.Instance, null!, null!, null!));
        builder.Services.AddSingleton(new AttachmentRepository(builder.Configuration, null!, null!, null!, null!, NullLogger<AttachmentRepository>.Instance));
        var user = new AuthUser { Id = 1, ClientId = 20, Permissions = [] };
        await using var app = builder.Build();
        app.Use((context, next) => { context.Items["User"] = user; return next(context); });
        app.MapEmployeeFields(); await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        foreach (var path in new[] { "/catalog?clientId=20", "/forms?clientId=20", "/lookups?clientId=20", "/forms/1" })
            Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("/api/employees/field-configuration" + path)).StatusCode);
        user.Permissions = ["reports.view"];
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("/api/employees/field-configuration/forms?clientId=20")).StatusCode);
        user.Permissions = ["employees.manage"];
        foreach (var path in new[] { "/catalog?clientId=21", "/forms?clientId=21", "/lookups?clientId=21" })
            Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("/api/employees/field-configuration" + path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsJsonAsync("/api/employees/field-configuration/forms", new SaveDynamicFormDefinition { ClientId = 21, FormCode = "EXTRA", FormName = "Extra", PurposeCode = "EMPLOYEE_INFOTYPE_0002" })).StatusCode);
    }
}
