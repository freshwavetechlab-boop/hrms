using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Services;

public static class ExcelHistoricalSalaryEndpoints
{
    public static void MapExcelHistoricalSalary(this WebApplication app)
    {
        var routes = app.MapGroup("/api/excel-payroll-setup/historical-period")
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(2_000_000));
        routes.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            try { return await next(context); }
            catch (UnauthorizedAccessException) { return Results.StatusCode(403); }
            catch (SelectedPayRunConflictException error) { return Results.Conflict(new { error = error.Message }); }
            catch (InvalidOperationException error) { return Results.BadRequest(new { error = error.Message }); }
            catch (System.Text.Json.JsonException) { return Results.BadRequest(new { error = "The saved historical source/review is invalid. Refresh its scoped preview; no salary change was confirmed." }); }
            catch (MySqlConnector.MySqlException)
            { return Results.Json(new { error = "Historical salary outcome could not be confirmed. Check or replay the same request ID; do not create another action or assume rollback." }, statusCode: 503); }
        });
        routes.MapPost("/preview", async (int clientId, HistoricalPeriodSalaryRequest request, ExcelPayrollSetupRepository repository, HttpContext context) =>
            context.Items["User"] is AuthUser actor && ExcelPayrollSetupRepository.CanManageHistoricalSalary(actor, clientId)
                ? Results.Ok(await repository.PreviewHistoricalSalaryAsync(clientId, request, actor)) : Results.StatusCode(403))
            .WithMetadata(new CalculationOnlyPayrollReview()).WithName("PreviewClosedHistoricalSalary");
        routes.MapPost("/apply", async (int clientId, HistoricalPeriodSalaryApplyRequest request, ExcelPayrollSetupRepository repository, HttpContext context) =>
            context.Items["User"] is AuthUser actor && ExcelPayrollSetupRepository.CanManageHistoricalSalary(actor, clientId)
                ? Results.Ok(await repository.ApplyHistoricalSalaryAsync(clientId, request, actor)) : Results.StatusCode(403))
            .WithMetadata(new RequireIsolatedPayrollProcessing()).WithName("ApplyClosedHistoricalSalary");
        routes.MapPost("/template/preview",async(int clientId,HistoricalTemplateConfigurationRequest request,ExcelPayrollSetupRepository repository,HttpContext context)=>
            context.Items["User"] is AuthUser actor&&ExcelPayrollSetupRepository.CanCreateHistoricalTemplate(actor,clientId)
                ?Results.Ok(await repository.PreviewHistoricalTemplateConfigurationAsync(clientId,request,actor)):Results.StatusCode(403))
            .WithMetadata(new CalculationOnlyPayrollReview()).WithName("PreviewClosedHistoricalTemplateConfiguration");
        routes.MapPost("/template/apply",async(int clientId,HistoricalTemplateConfigurationApplyRequest request,ExcelPayrollSetupRepository repository,HttpContext context)=>
            context.Items["User"] is AuthUser actor&&ExcelPayrollSetupRepository.CanCreateHistoricalTemplate(actor,clientId)
                ?Results.Ok(await repository.ApplyHistoricalTemplateConfigurationAsync(clientId,request,actor)):Results.StatusCode(403))
            .WithMetadata(new RequireIsolatedPayrollProcessing()).WithName("ApplyClosedHistoricalTemplateConfiguration");
    }
}
