using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Services;

public static class SalaryTemplateEndpoints
{
    public static void Map(WebApplication app, Func<HttpContext, AuthUser> currentUser, Func<HttpContext, int, bool> canAccessClient)
    {
        app.MapPost("/api/clients/{clientId:int}/salary-templates", async (int clientId, SalaryTemplateCreateRequest request, SettingsRepository repository, HttpContext context) =>
        {
            if (clientId <= 0) return Results.BadRequest(new { error = "Select a client." });
            if (!canAccessClient(context, clientId) || !PayrollAdvisorService.CanAccess(currentUser(context), clientId)) return Results.StatusCode(403);
            try
            {
                var saved = await repository.CreateClientSalaryTemplateAsync(clientId, request);
                return Results.Created($"/api/clients/{clientId}/salary-templates/{saved["id"]}", saved);
            }
            catch (InvalidOperationException error) { return Results.BadRequest(new { error = error.Message }); }
        }).WithName("CreateClientSalaryTemplate").WithOpenApi();
    }
}
