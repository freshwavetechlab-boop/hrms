using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Services;

public static class ExcelPayrollSetupEndpoints
{
    public static bool CanAccess(AuthUser user, int clientId) => user.IsActive && user.Id > 0 && !user.MustChangePassword && clientId > 0
        && (!user.ClientId.HasValue || user.ClientId == clientId)
        && new[] { "settings.manage", "employees.manage", "payroll.run" }.All(p => user.Permissions.Contains(p,StringComparer.OrdinalIgnoreCase));

    public static void MapExcelPayrollSetup(this WebApplication app)
    {
        var group = app.MapGroup("/api/excel-payroll-setup").WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(2_000_000));
        group.AddEndpointFilter(async (context,next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            if (!int.TryParse(context.HttpContext.Request.Query["clientId"],out var clientId) || context.HttpContext.Items["User"] is not AuthUser user || !CanAccess(user,clientId)) return Results.StatusCode(403);
            try { return await next(context); }
            catch (InvalidOperationException e) { return Results.BadRequest(new { error = e.Message }); }
            catch (MySqlConnector.MySqlException failure) {
                // Never log SQL text/parameters or claim rollback from an uncertain transport/commit error.
                var logger=context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ExcelPayrollSetup");
                logger.LogWarning("Excel payroll setup database request failed. Method={Method}; MySqlNumber={MySqlNumber}; SqlState={SqlState}",context.HttpContext.Request.Method,failure.Number,failure.SqlState);
                var message=HttpMethods.IsGet(context.HttpContext.Request.Method)
                    ? "Unable to read payroll setup. Check database availability and the saved operation status before retrying a write."
                    : "Payroll setup outcome could not be confirmed. Check the saved receipt before retrying; do not assume the operation failed or repeat payroll processing.";
                return Results.Json(new { error=message },statusCode:503);
            }
        });
        group.MapGet("/context", async (int clientId,string batchId,ExcelPayrollSetupRepository repository,HttpContext context) =>
            Results.Ok(await repository.ContextAsync(clientId,batchId,context.Request.Query["rowIds"].ToString().Split(',',StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))));
        group.MapPost("/preview", async (int clientId,ExcelPayrollSetupRequest request,ExcelPayrollSetupRepository repository) => Results.Ok(await repository.PreviewAsync(clientId,request)));
        group.MapPost("/draft", async (int clientId,ExcelPayrollSetupDraftRequest request,ExcelPayrollSetupRepository repository,HttpContext context) =>
            Results.Ok(await repository.SaveDraftAsync(clientId,request,((AuthUser)context.Items["User"]!).Email)));
        group.MapPost("/apply", async (int clientId,ExcelPayrollSetupApplyRequest request,ExcelPayrollSetupRepository repository,HttpContext context) =>
            Results.Ok(await repository.ApplyAsync(clientId,request,((AuthUser)context.Items["User"]!).Email)));
        group.MapPost("/defaults-draft", async (int clientId,ExcelPayrollDefaultsDraftRequest request,ExcelPayrollSetupRepository repository,HttpContext context) =>
            Results.Ok(await repository.SaveDefaultsDraftAsync(clientId,request,((AuthUser)context.Items["User"]!).Email)));
    }
}
