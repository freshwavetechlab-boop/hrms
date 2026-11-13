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

public class EssProfileAccessTests
{
    [Fact]
    public async Task ProfileEndpointsDenyInactiveUnlinkedAndUnpermittedActorsBeforeDatabaseAccess()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(new EssMssRepository(builder.Configuration));
        var actor = new AuthUser();
        await using var app = builder.Build();
        app.MapEssProfile(_ => actor);
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };

        foreach (var denied in new[]
        {
            new AuthUser { IsActive = false, EmployeeId = 10, Permissions = ["ess.self"] },
            new AuthUser { IsActive = true, EmployeeId = null, Permissions = ["ess.self"] },
            new AuthUser { IsActive = true, EmployeeId = 0, Permissions = ["ess.self"] },
            new AuthUser { IsActive = true, EmployeeId = 10, Permissions = ["employees.manage"] }
        })
        {
            actor = denied;
            using var read = await http.GetAsync("/api/ess/profile?employeeId=20&clientId=30");
            Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
            using var write = await http.PostAsJsonAsync("/api/ess/profile", new { employeeId = 20, clientId = 30, uanNumber = "012345678901", annualCtc = 1 });
            Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        }
    }
}
