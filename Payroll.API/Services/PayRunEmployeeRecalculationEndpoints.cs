using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Services;

public static class PayRunEmployeeRecalculationEndpoints
{
    internal static bool CanUse(HttpContext context, int clientId) => context.Items["User"] is AuthUser actor && actor.IsActive
        && !actor.MustChangePassword && actor.Permissions.Contains("payroll.run", StringComparer.OrdinalIgnoreCase)
        && SelectedClientAccess.Allows(actor, clientId);
    public static void MapPayRunEmployeeRecalculation(this WebApplication app)
    {
        var routes = app.MapGroup("/api/pay-runs/{runId:int}/employees/{employeeId:int}");
        routes.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            try { return await next(context); }
            catch (InvalidOperationException error) { return Results.BadRequest(new { error = error.Message }); }
            catch (MySqlConnector.MySqlException) { return Results.Json(new { error = "Employee recalculation could not be confirmed. Refresh the run before retrying; no automatic retry is performed." }, statusCode: 503); }
        });
        routes.MapGet("/recalculation-preview", async (int runId, int employeeId, PayRunRepository repository, HttpContext context) =>
        {
            var run = await repository.GetAsync(runId); if (run is null) return Results.NotFound();
            if (!CanUse(context, run.ClientId)) return Results.StatusCode(403);
            var preview = await repository.PreviewEmployeeRecalculationAsync(runId, employeeId);
            return preview is null ? Results.NotFound() : Results.Ok(preview);
        }).WithMetadata(new CalculationOnlyPayrollReview());
        routes.MapPost("/recalculate", async (int runId, int employeeId, RecalculatePayRunEmployeeRequest request, PayRunRepository repository, HttpContext context) =>
        {
            var run = await repository.GetAsync(runId); if (run is null) return Results.NotFound();
            if (!CanUse(context, run.ClientId)) return Results.StatusCode(403);
            var result = await repository.RecalculateEmployeeAsync(runId, employeeId, request, ((AuthUser)context.Items["User"]!).Email);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });
    }
}
