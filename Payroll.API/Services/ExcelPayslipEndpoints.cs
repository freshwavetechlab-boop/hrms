using System.Text.Json;
using System.Text.RegularExpressions;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Services;

// A separate reporting workspace. These routes never create employees or payroll runs.
public static class ExcelPayslipEndpoints
{
    public static void Map(WebApplication app, Func<HttpContext, bool> canRead,
        Func<HttpContext, bool> canManage, Func<HttpContext, int, bool> canAccessClient,
        Func<HttpContext, AuthUser> currentUser)
    {
        var routes = app.MapGroup("/api/excel-payslips").WithTags("Excel Payslips");
        routes.AddEndpointFilter(async (invocation, next) =>
        {
            var context = invocation.HttpContext;
            context.Response.Headers.CacheControl = "no-store";
            if (!canRead(context)) return Results.StatusCode(403);
            var isExport = HttpMethods.IsPost(context.Request.Method)
                && context.Request.Path.Value!.EndsWith("/pdf", StringComparison.OrdinalIgnoreCase);
            if (!HttpMethods.IsGet(context.Request.Method) && !isExport && !canManage(context))
                return Results.StatusCode(403);
            if (!context.Request.Path.Value!.EndsWith("/clients", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(context.Request.Query["clientId"], out var clientId) || clientId <= 0)
                    return Results.BadRequest(new { error = "Select an active client." });
                if (!canAccessClient(context, clientId)) return Results.StatusCode(403);
                var repository = context.RequestServices.GetRequiredService<ExcelPayslipRepository>();
                if (!await repository.IsSupportedClientAsync(clientId))
                    return Results.BadRequest(new { error = "The selected client is unavailable or inactive." });
            }
            try { return await next(invocation); }
            catch (InvalidOperationException error) { return Results.BadRequest(new { error = error.Message }); }
        });

        routes.MapGet("/clients", async (ExcelPayslipRepository repository, HttpContext context) =>
            Results.Ok((await repository.ListClientsAsync(currentUser(context).ClientId))
                .Where(client => canAccessClient(context, client.Id))))
            .WithName("GetExcelPayslipClients").WithOpenApi();

        routes.MapGet("/profile", async (ExcelPayslipRepository repository, int clientId, string headerSignature) =>
            !ValidSignature(headerSignature) ? Results.BadRequest(new { error = "Invalid worksheet header signature." })
                : Results.Ok(await repository.GetProfileAsync(clientId, headerSignature)))
            .WithName("GetExcelPayslipMapping").WithOpenApi();

        routes.MapGet("/templates", async (ExcelPayslipRepository repository, int clientId) => Results.Ok(await repository.GetTemplatesAsync(clientId)))
            .WithName("GetExcelPayslipSalaryTemplates").WithOpenApi();

        routes.MapPut("/profile", async (ExcelPayslipRepository repository, int clientId, string headerSignature, JsonElement request, HttpContext context) =>
        {
            if (!canManage(context)) return Results.StatusCode(403);
            if (!ValidSignature(headerSignature)) return Results.BadRequest(new { error = "Invalid worksheet header signature." });
            var (ok, error) = await repository.SaveProfileAsync(clientId, headerSignature, request);
            return ok ? Results.Ok(request) : Results.BadRequest(new { error });
        }).WithName("SaveExcelPayslipMapping").WithOpenApi();

        routes.MapGet("/batches", async (ExcelPayslipRepository repository, int clientId) => Results.Ok(await repository.ListAsync(clientId)))
            .WithName("ListExcelPayslipBatches").WithOpenApi();
        routes.MapGet("/batches/{id}", async (ExcelPayslipRepository repository, int clientId, string id) =>
        {
            var batch = await repository.GetAsync(clientId, id);
            return batch is null ? Results.NotFound(new { error = "Payslip batch not found." }) : Results.Ok(batch);
        }).WithName("GetExcelPayslipBatch").WithOpenApi();
        routes.MapPost("/batches", async (ExcelPayslipRepository repository, int clientId, ExcelPayslipBatch request, HttpContext context) =>
        {
            if (!canManage(context)) return Results.StatusCode(403);
            var (item, error) = await repository.SaveAsync(clientId, request, currentUser(context).Email);
            return item is null ? Results.BadRequest(new { error }) : Results.Ok(item);
        }).WithName("CreateExcelPayslipBatch").WithOpenApi();

        routes.MapPost("/batches/{id}/pdf", async (ExcelPayslipRepository repository, ExcelPayslipPdfService pdf,
            int clientId, string id, ExcelPayslipSelection request) =>
        {
            var batch = await repository.GetAsync(clientId, id);
            if (batch is null) return Results.NotFound(new { error = "Payslip batch not found." });
            var (rows, error) = ExcelPayslipRepository.SelectRows(batch, request);
            if (error is not null) return Results.BadRequest(new { error });
            return Results.File(pdf.Create(batch, rows, request.IncludeSeal, request.AmountDecimalPlaces), "application/pdf", $"payslips-{batch.Month}.pdf");
        }).WithName("ExportExcelPayslipPdf").WithOpenApi();

        routes.MapPost("/batches/{id}/send", async (ExcelPayslipRepository repository, int clientId, string id,
            SendExcelPayslipsRequest request, HttpContext context) =>
        {
            if (!canManage(context)) return Results.StatusCode(403);
            var (item, error) = await repository.SendAsync(clientId, id, request, currentUser(context).Email);
            return item is null ? Results.BadRequest(new { error }) : Results.Ok(item);
        }).WithName("SendExcelPayslips").WithOpenApi();
    }

    private static bool ValidSignature(string value) => Regex.IsMatch(value, "^[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant);
}
