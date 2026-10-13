using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public class PayrollCalculationEndpointTests
{
    [Fact]
    public async Task RealHttpRoutesEnforceScopeAndReturnEngineResultsWithoutDatabase()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddSingleton<TaxEngineRepository>(); builder.Services.AddSingleton<PayRunRepository>();
        await using var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (!context.Request.Headers.ContainsKey("Test-No-User")) context.Items["User"] = new AuthUser { Id = 1, ClientId = 11, IsActive = true, Permissions = ["client.settings.manage"] };
            await next();
        });
        app.MapPayrollCalculation(); await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        var catalogue = await http.PostAsJsonAsync("/api/payroll-calculation/catalogue", new PayrollCatalogueRequest([new("1", "WAGES", "Wages", true)]));
        Assert.Equal(HttpStatusCode.OK, catalogue.StatusCode); Assert.Contains("no-store", catalogue.Headers.CacheControl!.ToString());
        var defs = await catalogue.Content.ReadFromJsonAsync<List<PayrollValueDefinition>>(); Assert.Contains(defs!, d => d.Token == "WAGES_EARNED");
        var request = new PayrollSimulationRequest { ClientId = 11, PayrollDays = 30, PresentDays = 12, PayableDays = 15, AnnualCtc = 240000,
            Snapshot = new("Template", 11, "", 240000, [new("1", "WAGES", "Earning", "Regular Earning", "None", "Fixed Amount", "20000", "", "", true, true, true, 100, "Fixed Pay")], [new("1")]) };
        var response = await http.PostAsJsonAsync("/api/payroll-calculation/simulate", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<PayrollSimulationResult>(); Assert.True(result!.Complete); Assert.Equal(10000, result.Net);
        request.ClientId = 12;
        Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsJsonAsync("/api/payroll-calculation/simulate", request)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("/api/payroll-calculation/employees?clientId=11")).StatusCode);
        request.ClientId = 11; request.Mode = "Employee"; request.EmployeeId = 1; request.PayPeriod = "2026-10";
        Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsJsonAsync("/api/payroll-calculation/simulate", request)).StatusCode);
        http.DefaultRequestHeaders.Add("Test-No-User", "true");
        Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsJsonAsync("/api/payroll-calculation/catalogue", new PayrollCatalogueRequest([]))).StatusCode);
        await app.StopAsync();
    }
}
