using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Services;

public static class LocalLlmRecoveryEndpoints
{
    public static void MapLocalLlmRecovery(this WebApplication app)
    {
        app.MapGet("/api/integrations/ai/{id:long}/runtime", (long id, HttpContext context,
            RecruitmentAiScoringService models, LocalLlmRecoveryService recovery, AuthRepository audit) =>
            Handle(id, context, models, recovery, audit, false));
        app.MapPost("/api/integrations/ai/{id:long}/start-runtime", (long id, HttpContext context,
            RecruitmentAiScoringService models, LocalLlmRecoveryService recovery, AuthRepository audit) =>
            Handle(id, context, models, recovery, audit, true));
    }

    private static async Task<IResult> Handle(long id, HttpContext context, RecruitmentAiScoringService models,
        LocalLlmRecoveryService recovery, AuthRepository audit, bool start)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (context.Items["User"] is not AuthUser user || !LocalLlmRecoveryService.IsAllowed(user))
            return Results.StatusCode(403);
        var model = (await models.GetGlobalPoolAsync(user)).Models.FirstOrDefault(row => row.Id == id);
        if (model is null) return Results.NotFound();
        if (recovery.CheckConfiguration(model) is { } unavailable) return Results.Ok(unavailable);
        var requestId = Guid.NewGuid();
        var before = "Unknown";
        async Task Record(string phase, LocalLlmRuntimeStatus? result) => await audit.WriteAuditAsync(user,
            $"engine.local-llm.start.{phase}", "LocalLlmRuntime", context.Request.Method, context.Request.Path,
            result is null ? 202 : result.State is "Starting" or "Running" ? 200 : 409,
            context.Connection.RemoteIpAddress?.ToString() ?? "", context.Request.Headers.UserAgent.ToString(),
            JsonSerializer.Serialize(new { requestId, modelId = id, scope = "Global", task = "EESL-LLM-LocalAI",
                reason = "Super-admin manual recovery", before, after = result?.State ?? "Unknown", outcome = result?.Code ?? "Requested" }));
        if (start)
        {
            before = (await recovery.SendAsync(model, user, false, Guid.NewGuid(), context.RequestAborted)).State;
            // Fail closed if the audit cannot be persisted; never start an unaudited remote task.
            await Record("requested", null);
        }
        var status = await recovery.SendAsync(model, user, start, requestId, context.RequestAborted);
        if (start) await Record("result", status);
        return Results.Ok(status);
    }
}
