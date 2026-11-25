using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using MySqlConnector;
using Payroll.API.Repositories;

namespace Payroll.API.Services;

public static class SalaryComponentEndpoints
{
    public sealed record SaveRequest(JsonObject Component, JsonObject? Expected = null);

    public static void Map(WebApplication app, Func<HttpContext, bool> canManage)
    {
        app.MapPost("/api/salary-components", async (SaveRequest request, SettingsRepository repository, HttpContext context) =>
        {
            if (!canManage(context)) return Results.StatusCode(403);
            if (request.Component is null) return Results.BadRequest(new { error = "Submit a salary component." });
            try { return Results.Ok(await repository.SaveSalaryComponentAsync(request.Component, request.Expected)); }
            catch (DBConcurrencyException error) { return Results.Conflict(new { error = error.Message }); }
            catch (InvalidOperationException error) { return Results.BadRequest(new { error = error.Message }); }
            catch (JsonException) { return Results.BadRequest(new { error = "Submit valid component fields." }); }
            catch (MySqlException error) when (error.Number == 1062)
            { return Results.Conflict(new { error = "This component already exists. Refresh and edit the saved record." }); }
        }).WithName("SaveSalaryComponent").WithOpenApi();
    }
}
