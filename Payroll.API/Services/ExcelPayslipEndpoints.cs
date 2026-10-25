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
        bool CanDelete(HttpContext context) => canManage(context) && currentUser(context).Permissions.Contains("settings.manage", StringComparer.OrdinalIgnoreCase);
        routes.AddEndpointFilter(async (invocation, next) =>
        {
            var context = invocation.HttpContext;
            context.Response.Headers.CacheControl = "no-store";
            if (!canRead(context)) return Results.StatusCode(403);
            if (context.GetEndpoint()?.Metadata.GetMetadata<OriginalWorkbookAccess>() is not null && !canManage(context))
                return Results.StatusCode(403);
            if (context.GetEndpoint()?.Metadata.GetMetadata<DeleteBatchAccess>() is not null && !CanDelete(context))
                return Results.StatusCode(403);
            var isExport = HttpMethods.IsPost(context.Request.Method)
                && context.Request.Path.Value!.EndsWith("/pdf", StringComparison.OrdinalIgnoreCase);
            if (!HttpMethods.IsGet(context.Request.Method) && !isExport && !canManage(context))
                return Results.StatusCode(403);
            var isOwnJobFeed = HttpMethods.IsGet(context.Request.Method)
                && context.Request.Path.Equals(new PathString("/api/excel-payslips/mail-jobs"));
            if (!context.Request.Path.Value!.EndsWith("/clients", StringComparison.OrdinalIgnoreCase) && !isOwnJobFeed)
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

        routes.MapGet("/mail-jobs", async (ExcelPayslipRepository repository, HttpContext context) =>
        {
            var user = currentUser(context);
            return Results.Ok(await repository.GetOwnMailJobsAsync(user.Id, user.ClientId, clientId => canAccessClient(context, clientId)));
        }).WithName("GetMyExcelPayslipMailJobs").WithOpenApi();

        routes.MapGet("/batches/{id}/mail-jobs", async (ExcelPayslipRepository repository, int clientId, string id) =>
        {
            var jobs = await repository.GetBatchMailJobsAsync(clientId, id);
            return jobs is null ? Results.NotFound(new { error = "Payslip batch not found." }) : Results.Ok(jobs);
        }).WithName("GetExcelPayslipBatchMailJobs").WithOpenApi();

        routes.MapPost("/batches/{id}/send-job", async (ExcelPayslipRepository repository, int clientId, string id,
            SendExcelPayslipsRequest request, HttpContext context) =>
        {
            var user = currentUser(context);
            var (item, error) = await repository.SubmitMailJobAsync(clientId, id, request, user.Id, user.Email);
            return item is null ? Results.BadRequest(new { error }) : Results.Ok(item);
        }).WithName("QueueExcelPayslipMailJob").WithOpenApi();

        routes.MapPost("/batches/{id}/email-request", async (ExcelPayslipRepository repository, int clientId, string id,
            ExcelPayslipEmailRequest request, HttpContext context) =>
            Results.Ok(await repository.RequestEmailIdsAsync(clientId, id, request, currentUser(context).Email)))
            .WithName("RequestExcelPayslipEmailIds").WithOpenApi();

        // A distinct route prevents an older API from accepting the checkbox while ignoring its PDF option.
        routes.MapPost("/batches/{id}/email-request-pdf", async (ExcelPayslipRepository repository, int clientId, string id,
            ExcelPayslipEmailRequest request, HttpContext context) =>
            !request.AttachPayslips ? Results.BadRequest(new { error = "Select the salary PDF attachment option." })
                : Results.Ok(await repository.RequestEmailIdsAsync(clientId, id, request, currentUser(context).Email)))
            .WithName("RequestExcelPayslipEmailIdsWithPdf").WithOpenApi();

        routes.MapPost("/batches/{id}/mail-jobs/{jobId}/dismiss", async (ExcelPayslipRepository repository, int clientId,
            string id, string jobId, HttpContext context) =>
        {
            var error = await repository.DismissMailJobAsync(clientId, id, jobId, currentUser(context).Id);
            return error is null ? Results.Ok(new { ok = true }) : Results.BadRequest(new { error });
        }).WithName("DismissExcelPayslipMailJob").WithOpenApi();

        routes.MapPost("/batches/{id}/email-updates", async (ExcelPayslipRepository repository, int clientId, string id,
            ExcelPayslipEmailUpdates request, HttpContext context) =>
        {
            if (context.Request.ContentLength > 1024 * 1024) return Results.BadRequest(new { error = "The email update list exceeds 1 MiB." });
            var (item, error) = await repository.UpdateEmailsAsync(clientId, id, request, currentUser(context).Email);
            return item is null ? Results.BadRequest(new { error }) : Results.Ok(item);
        }).WithName("UpdateExcelPayslipEmails").WithOpenApi();

        routes.MapGet("/profile", async (ExcelPayslipRepository repository, int clientId, string headerSignature) =>
            !ValidSignature(headerSignature) ? Results.BadRequest(new { error = "Invalid worksheet header signature." })
                : Results.Ok(await repository.GetProfileAsync(clientId, headerSignature)))
            .WithName("GetExcelPayslipMapping").WithOpenApi();

        routes.MapGet("/templates", async (ExcelPayslipRepository repository, int clientId) => Results.Ok(await repository.GetTemplatesAsync(clientId)))
            .WithName("GetExcelPayslipSalaryTemplates").WithOpenApi();

        routes.MapPost("/suggest-mapping", async (ExcelPayslipRepository repository,
            int clientId, ExcelPayslipMappingRequest request, HttpContext context) =>
        {
            if (context.Request.ContentLength > 256 * 1024) return Results.BadRequest(new { error = "Mapping evidence exceeds 256 KiB." });
            ExcelPayslipMappingService.ValidateRequest(request);
            var templates = await repository.GetTemplatesAsync(clientId);
            var examples = await repository.GetMappingExamplesAsync(clientId, context.RequestAborted);
            return Results.Ok(await context.RequestServices.GetRequiredService<ExcelPayslipMappingService>()
                .SuggestAsync(request, templates, examples, context.RequestAborted));
        }).WithName("SuggestExcelPayslipMapping").WithOpenApi();

        routes.MapPost("/correction-preview", (int clientId, ExcelPayslipCorrectionPreview request, HttpContext context) =>
        {
            if (context.Request.ContentLength > 24 * 1024 * 1024) return Results.BadRequest(new { error = "Correction source exceeds 24 MiB." });
            if (ExcelPayslipRepository.ValidateBatch(request.Batch) is { } error) return Results.BadRequest(new { error });
            if (request.Batch.ClientId != clientId) return Results.BadRequest(new { error = "The correction belongs to another client." });
            return Results.Ok(ExcelPayslipCalculationService.RecalculateCorrections(request));
        }).WithName("PreviewExcelPayslipCorrections").WithOpenApi();

        routes.MapGet("/dashboard", async (ExcelPayslipRepository repository, int clientId, string? batchId) =>
        {
            var dashboard = await repository.GetDashboardAsync(clientId, batchId);
            return dashboard is null ? Results.NotFound(new { error = "The selected batch was not found for this client." }) : Results.Ok(dashboard);
        }).WithName("GetExcelPayslipDashboard").WithOpenApi();

        routes.MapPut("/profile", async (ExcelPayslipRepository repository, int clientId, string headerSignature, JsonElement request, HttpContext context) =>
        {
            if (!canManage(context)) return Results.StatusCode(403);
            if (!ValidSignature(headerSignature)) return Results.BadRequest(new { error = "Invalid worksheet header signature." });
            var (ok, error) = await repository.SaveProfileAsync(clientId, headerSignature, request);
            return ok ? Results.Ok(request) : Results.BadRequest(new { error });
        }).WithName("SaveExcelPayslipMapping").WithOpenApi();

        routes.MapPost("/originals", async ([Microsoft.AspNetCore.Mvc.FromServices] AttachmentRepository attachments, int clientId, HttpContext context) =>
        {
            if (!canManage(context)) return Results.StatusCode(403);
            if (!context.Request.HasFormContentType || context.Request.ContentLength > 26L * 1024 * 1024)
                return Results.BadRequest(new { error = "Upload a workbook up to the attachment size limit." });
            var limit = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
            if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = 26L * 1024 * 1024;
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            if (form.Files.Count != 1) return Results.BadRequest(new { error = "Upload exactly one original workbook." });
            return Results.Ok(await attachments.RetainExcelWorkbookAsync(clientId, form.Files[0], currentUser(context),
                context.Connection.RemoteIpAddress?.ToString() ?? "", context.Request.Headers.UserAgent.ToString(), context.RequestAborted));
        }).WithMetadata(new OriginalWorkbookAccess()).WithName("RetainExcelPayslipOriginal");
        routes.MapGet("/batches/{id}/originals", async (ExcelPayslipRepository repository, int clientId, string id, HttpContext context) =>
            !canManage(context) ? Results.StatusCode(403) : Results.Ok(await repository.GetOriginalsAsync(clientId, id)))
            .WithMetadata(new OriginalWorkbookAccess()).WithName("GetExcelPayslipOriginals");
        routes.MapGet("/batches/{id}/originals/{originalId}", async ([Microsoft.AspNetCore.Mvc.FromServices] AttachmentRepository attachments, [Microsoft.AspNetCore.Mvc.FromServices] AttachmentStorageService storage,
            int clientId, string id, string originalId, HttpContext context) =>
        {
            if (!canManage(context)) return Results.StatusCode(403);
            var file = await attachments.GetExcelWorkbookForContentAsync(clientId, originalId, id, currentUser(context),
                context.Connection.RemoteIpAddress?.ToString() ?? "", context.Request.Headers.UserAgent.ToString(), context.RequestAborted);
            return file is null ? Results.NotFound() : (IResult)new AttachmentContentResult(storage, file.Value.Server, file.Value.Attachment, false);
        }).WithMetadata(new OriginalWorkbookAccess()).WithName("DownloadExcelPayslipOriginal");
        // A stronger existing settings permission is required in addition to normal payslip management.
        routes.MapGet("/batches/{id}/delete-preview", async (ExcelPayslipRepository repository, int clientId, string id, HttpContext context) =>
        {
            if (!CanDelete(context)) return Results.StatusCode(403);
            var preview = await repository.PreviewDeleteAsync(clientId, id);
            return preview is null ? Results.NotFound() : Results.Ok(preview);
        }).WithMetadata(new DeleteBatchAccess()).WithName("PreviewExcelPayslipDeletion");
        routes.MapPost("/batches/{id}/delete", async (ExcelPayslipRepository repository, int clientId, string id, ExcelPayslipDeleteRequest request, HttpContext context) =>
            !CanDelete(context) ? Results.StatusCode(403) : Results.Ok(await repository.DeleteBatchAsync(clientId, id, request, currentUser(context).Email)))
            .WithMetadata(new DeleteBatchAccess()).WithName("DeleteUnusedExcelPayslipBatch");
        routes.MapPost("/batches/{id}/cleanup-review", async (ExcelPayslipRepository repository, int clientId, string id, ExcelPayslipSupersessionRequest request, HttpContext context) =>
            !CanDelete(context) ? Results.StatusCode(403) : Results.Ok(await repository.PreviewSupersessionAsync(clientId, id, request.KeeperBatchId)))
            .WithMetadata(new DeleteBatchAccess()).WithName("ReviewExcelPayslipLegacyVersion");
        routes.MapPost("/batches/{id}/cleanup-action", async (ExcelPayslipRepository repository, int clientId, string id, ExcelPayslipCleanupActionRequest request, HttpContext context) =>
            !CanDelete(context) ? Results.StatusCode(403) : Results.Ok(await repository.ResolveCleanupAsync(clientId, id, request, currentUser(context).Email)))
            .WithMetadata(new DeleteBatchAccess()).WithName("ResolveExcelPayslipCleanupBlocker");

        routes.MapGet("/batches", async (ExcelPayslipRepository repository, int clientId) => Results.Ok(await repository.ListAsync(clientId)))
            .WithName("ListExcelPayslipBatches").WithOpenApi();
        routes.MapGet("/batches/{id}", async (ExcelPayslipRepository repository, int clientId, string id) =>
        {
            var batch = await repository.GetAsync(clientId, id);
            return batch is null ? Results.NotFound(new { error = "Payslip batch not found." }) : Results.Ok(batch);
        }).WithName("GetExcelPayslipBatch").WithOpenApi();
        routes.MapGet("/batches/{id}/delivery-status", async (ExcelPayslipRepository repository, int clientId, string id) =>
        {
            var status = await repository.GetDeliveryStatusAsync(clientId, id);
            return status is null ? Results.NotFound(new { error = "Payslip batch not found." }) : Results.Ok(status);
        }).WithName("GetExcelPayslipDeliveryStatus").WithOpenApi();
        routes.MapPost("/batches", async (ExcelPayslipRepository repository, int clientId, ExcelPayslipBatch request, HttpContext context) =>
        {
            if (!canManage(context)) return Results.StatusCode(403);
            var (item, error) = await repository.SaveAsync(clientId, request, currentUser(context).Email);
            return item is null ? Results.BadRequest(new { error }) : Results.Ok(item);
        }).WithName("CreateExcelPayslipBatch").WithOpenApi();

        routes.MapGet("/batches/{id}/calculation", async (ExcelPayslipRepository repository, int clientId, string id) =>
        {
            var source = await repository.GetCalculationSourceAsync(clientId, id);
            return source is null ? Results.NotFound(new { error = "This batch has no saved formulas. Import the original salary workbook again to save its calculation source." }) : Results.Ok(source);
        }).WithName("GetExcelPayslipCalculationSource").WithOpenApi();

        routes.MapPost("/batches/{id}/calculate", async (ExcelPayslipRepository repository, int clientId, string id, ExcelPayslipCalculateRequest request) =>
        {
            var batch = await repository.GetAsync(clientId, id);
            var source = await repository.GetCalculationSourceAsync(clientId, id);
            if (batch is null || source is null) return Results.NotFound(new { error = "Saved batch or calculation source was not found for this client." });
            return Results.Ok(ExcelPayslipCalculationService.Calculate(source, batch, request));
        }).WithName("PreviewExcelPayslipCalculation").WithOpenApi();

        routes.MapGet("/batches/{id}/variance", async (ExcelPayslipRepository repository, int clientId, string id, string previousBatchId) =>
        {
            var current = await repository.GetAsync(clientId, id);
            var previous = await repository.GetAsync(clientId, previousBatchId);
            if (current is null || previous is null) return Results.NotFound(new { error = "Select saved batches belonging to this client." });
            return Results.Ok(ExcelPayslipVarianceService.Compare(current, previous));
        }).WithName("CompareExcelPayslipsWithPreviousMonth").WithOpenApi();

        routes.MapPost("/batches/{id}/pdf", async (ExcelPayslipRepository repository, ExcelPayslipPdfService pdf,
            int clientId, string id, ExcelPayslipSelection request) =>
        {
            var batch = await repository.GetAsync(clientId, id);
            if (batch is null) return Results.NotFound(new { error = "Payslip batch not found." });
            var (rows, error) = ExcelPayslipRepository.SelectRows(batch, request);
            if (error is not null) return Results.BadRequest(new { error });
            var source = batch.SimpleLayout ? await repository.GetCalculationSourceAsync(clientId, id) : null;
            return Results.File(pdf.Create(batch, rows, request.IncludeSeal, request.AmountDecimalPlaces, source), "application/pdf", $"payslips-{batch.Month}.pdf");
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
    private sealed record OriginalWorkbookAccess;
    private sealed record DeleteBatchAccess;
}
