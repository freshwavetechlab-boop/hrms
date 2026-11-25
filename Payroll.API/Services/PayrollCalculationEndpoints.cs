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
        group.MapGet("/employees", async (int clientId, string? search, string? templateId, string? payPeriod, string? scope, HttpContext context, PayRunRepository repository) =>
            context.Items["User"] is AuthUser user && CanAccess(user, clientId, true)
                ? scope == "Client" ? Results.Ok(await repository.SimulationClientEmployeesAsync(clientId, search?.Length > 100 ? search[..100] : search, context.RequestAborted))
                    : Results.Ok(await repository.SimulationEmployeesAsync(clientId, search?.Length > 100 ? search[..100] : search, templateId, payPeriod, context.RequestAborted)) : Results.StatusCode(403));
        group.MapGet("/template-snapshots",async (int clientId,HttpContext context,PayRunRepository repository) =>
            context.Items["User"] is AuthUser user && CanAccess(user,clientId)
                ? Results.Ok(await repository.SimulationTemplatesAsync(clientId,context.RequestAborted)) : Results.StatusCode(403));
        group.MapPost("/simulate", async (PayrollSimulationRequest request, HttpContext context, PayRunRepository repository) =>
        {
            if (context.Items["User"] is not AuthUser user || !CanAccess(user, request.ClientId, request.Mode is "Employee" or "EmployeeTemplate")) return Results.StatusCode(403);
            PayRunRepository.ValidateSimulationRequest(request);
            return Results.Ok(request.Mode == "Employee" ? await repository.SimulateEmployeeAsync(request, context.RequestAborted)
                : request.Mode == "EmployeeTemplate" ? await repository.SimulateEmployeeTemplateAsync(request, context.RequestAborted) : PayRunRepository.SimulateTemplate(request));
        });
        group.MapPost("/extract-template", async (SalaryTemplateExcelExtractionRequest request, HttpContext context, SettingsRepository settings) =>
        {
            if (context.Items["User"] is not AuthUser user || !SalaryTemplateExcelExtractionService.CanAccess(user, request.ClientId)) return Results.StatusCode(403);
            var masters = SalaryTemplateExcelExtractionService.ReadMasterCatalogue(await settings.GetForClientAsync(request.ClientId));
            return Results.Ok(SalaryTemplateExcelExtractionService.Extract(request, masters));
        })
            .WithMetadata(new CalculationOnlyPayrollReview(), new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(30_000_000));
        group.MapPost("/formula-workbook",async (PayrollFormulaWorkbookRequest request,HttpContext context,PayRunRepository repository)=>
            context.Items["User"] is AuthUser user && CanAccess(user,request.ClientId,true)
                ? Results.Ok(await repository.FormulaWorkbookAsync(request,context.RequestAborted)) : Results.StatusCode(403));
    }
}
