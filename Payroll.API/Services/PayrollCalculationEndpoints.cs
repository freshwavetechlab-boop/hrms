using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Services;

public static class PayrollCalculationEndpoints
{
    public static bool CanAccess(AuthUser user, int? clientId = null, bool employee = false) => user.IsActive && user.Id > 0 && !user.MustChangePassword
        && (!clientId.HasValue || clientId > 0 && (!user.ClientId.HasValue || user.ClientId == clientId))
        && (user.Permissions.Contains("payroll.run", StringComparer.OrdinalIgnoreCase) || user.Permissions.Contains("settings.manage", StringComparer.OrdinalIgnoreCase)
            || !employee && user.ClientId is > 0 && user.Permissions.Contains("client.settings.manage", StringComparer.OrdinalIgnoreCase));

    public static void MapPayrollCalculation(this WebApplication app)
    {
        var group = app.MapGroup("/api/payroll-calculation").WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(300000));
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            if (context.HttpContext.Items["User"] is not AuthUser user || !CanAccess(user)) return Results.StatusCode(403);
            try { return await next(context); }
            catch (InvalidOperationException e) { return Results.BadRequest(new { error = e.Message }); }
            catch (MySqlConnector.MySqlException) { return Results.Json(new { error = "Simulation data is unavailable. Check database connectivity and schema. No payroll has been created." }, statusCode: 503); }
        });
        group.MapPost("/catalogue", (PayrollCatalogueRequest request) => Results.Ok(PayrollValueCatalogue.Build(request)));
        group.MapGet("/employees", async (int clientId, string? search, HttpContext context, PayRunRepository repository) =>
            context.Items["User"] is AuthUser user && CanAccess(user, clientId, true)
                ? Results.Ok(await repository.SimulationEmployeesAsync(clientId, search?.Length > 100 ? search[..100] : search, context.RequestAborted)) : Results.StatusCode(403));
        group.MapGet("/template-snapshots",async (int clientId,HttpContext context,PayRunRepository repository) =>
            context.Items["User"] is AuthUser user && CanAccess(user,clientId)
                ? Results.Ok(await repository.SimulationTemplatesAsync(clientId,context.RequestAborted)) : Results.StatusCode(403));
        group.MapPost("/simulate", async (PayrollSimulationRequest request, HttpContext context, PayRunRepository repository) =>
        {
            if (context.Items["User"] is not AuthUser user || !CanAccess(user, request.ClientId, request.Mode == "Employee")) return Results.StatusCode(403);
            PayRunRepository.ValidateSimulationRequest(request);
            return Results.Ok(request.Mode == "Employee" ? await repository.SimulateEmployeeAsync(request, context.RequestAborted) : PayRunRepository.SimulateTemplate(request));
        });
    }
}
