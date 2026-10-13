using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Services;

public static class PayrollAdvisorEndpoints
{
    public static void MapPayrollAdvisor(this WebApplication app)
    {
        var routes = app.MapGroup("/api/payroll-advisor").WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(100000));
        routes.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            try { return await next(context); }
            catch (MySqlConnector.MySqlException) { return Results.Json(new { error = "Review storage is unavailable. Check database access and the payroll advisor migration. Existing payroll configuration is unchanged." }, statusCode: 503); }
            catch (JsonException) { return Results.Json(new { error = "Stored review data is unreadable. Request a new review; no configuration was changed." }, statusCode: 503); }
        });
        routes.MapPost("/reviews", async (PayrollReviewRequest request, HttpContext context, PayrollAdvisorRepository repository) =>
        {
            if (request?.Snapshot is null) return Results.BadRequest(new { error = "Provide a configuration to review." });
            if (context.Items["User"] is not AuthUser user || !PayrollAdvisorService.CanAccess(user, request.Snapshot.ClientId)) return Results.StatusCode(403);
            if (!request.Consent) return Results.BadRequest(new { error = "Confirm that this configuration can be sent to the configured AI provider." });
            try { return Results.Ok(View(await repository.QueueAsync(request.Snapshot, user, context.RequestAborted))); }
            catch (InvalidOperationException error) { return Results.BadRequest(new { error = error.Message }); }
        });
        routes.MapGet("/reviews", async (int clientId, HttpContext context, PayrollAdvisorRepository repository) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (context.Items["User"] is not AuthUser user || !PayrollAdvisorService.CanAccess(user, clientId)) return Results.StatusCode(403);
            return Results.Ok((await repository.HistoryAsync(clientId, user.Id, context.RequestAborted)).Select(View));
        });
        routes.MapGet("/reviews/{id:guid}", async (Guid id, HttpContext context, PayrollAdvisorRepository repository) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (context.Items["User"] is not AuthUser user || !user.IsActive) return Results.StatusCode(403);
            var row = await repository.GetAsync(id.ToString(), user.Id, context.RequestAborted);
            return row is null ? Results.NotFound() : !PayrollAdvisorService.CanAccess(user, row.ClientId) ? Results.StatusCode(403) : Results.Ok(View(row));
        });
        routes.MapPost("/reviews/{id:guid}/decisions", async (Guid id, PayrollReviewDecision request, HttpContext context, PayrollAdvisorRepository repository) =>
        {
            if (context.Items["User"] is not AuthUser user || request?.Snapshot is null || !PayrollAdvisorService.CanAccess(user, request.Snapshot.ClientId)) return Results.StatusCode(403);
            var row = await repository.GetAsync(id.ToString(), user.Id, context.RequestAborted);
            if (row is null) return Results.NotFound();
            if (!PayrollAdvisorService.CanAccess(user, row.ClientId)) return Results.StatusCode(403);
            try { PayrollAdvisorService.Validate(request.Snapshot); await repository.DecideAsync(row, request, user.Id); return Results.Ok(new { recorded = true, liveConfigurationChanged = false }); }
            catch (InvalidOperationException error) { return Results.BadRequest(new { error = error.Message }); }
        });
    }
    private static object View(PayrollReviewJob row) => new { row.Id, row.ClientId, row.SnapshotHash, row.Status, row.Error, row.CreatedAt, row.FinishedAt,
        snapshot = JsonSerializer.Deserialize<PayrollReviewSnapshot>(row.SnapshotJson, PayrollAdvisorService.Json),
        report = row.ResultJson is null ? null : JsonSerializer.Deserialize<PayrollReviewReport>(row.ResultJson, PayrollAdvisorService.Json) };
}
