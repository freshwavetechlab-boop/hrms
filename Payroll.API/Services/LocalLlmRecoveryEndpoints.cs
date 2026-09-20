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
        app.MapGet("/api/integrations/ai/{id:long}/recovery-settings", (long id, HttpContext context,
            RecruitmentAiScoringService models, ILocalLlmRecoverySettingsStore settings) =>
            Configure(id, context, models, settings, null));
        app.MapPut("/api/integrations/ai/{id:long}/recovery-settings", (long id, HttpContext context,
            SaveLocalLlmRecoverySettings request, RecruitmentAiScoringService models, ILocalLlmRecoverySettingsStore settings) =>
            Configure(id, context, models, settings, request));
    }

    private static async Task<IResult> Handle(long id, HttpContext context, RecruitmentAiScoringService models,
        LocalLlmRecoveryService recovery, AuthRepository audit, bool start)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (context.Items["User"] is not AuthUser user || !LocalLlmRecoveryService.IsAllowed(user))
            return Results.StatusCode(403);
        var model = (await models.GetGlobalPoolAsync(user)).Models.FirstOrDefault(row => row.Id == id);
        if (model is null) return Results.NotFound();
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
            var observed = await recovery.SendAsync(model, user, false, Guid.NewGuid(), context.RequestAborted);
            before = observed.State;
            if (!observed.CanStart) return Results.Ok(observed);
            // Fail closed if the audit cannot be persisted; never start an unaudited remote task.
            await Record("requested", null);
        }
        var status = await recovery.SendAsync(model, user, start, requestId, context.RequestAborted);
        if (start) await Record("result", status);
        return Results.Ok(status);
    }

    private static async Task<IResult> Configure(long id, HttpContext context, RecruitmentAiScoringService models,
        ILocalLlmRecoverySettingsStore settings, SaveLocalLlmRecoverySettings? request)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (context.Items["User"] is not AuthUser user || !LocalLlmRecoveryService.IsAllowed(user))
            return Results.StatusCode(403);
        var model = (await models.GetGlobalPoolAsync(user)).Models.FirstOrDefault(row => row.Id == id);
        if (model is null) return Results.NotFound();
        if (model.ClientId != 0 || model.ProviderCode != "LocalOpenAICompatible")
            return Results.BadRequest(new { error = "Recovery settings are only available for a global Local LLM model." });
        try
        {
            if (request is null) return Results.Ok(await settings.GetAsync(context.RequestAborted));
            var result = await settings.SaveAsync(request, model, user, context.RequestAborted);
            return result.Settings is null ? Results.BadRequest(new { error = result.Error }) : Results.Ok(result.Settings);
        }
        catch (Exception exception) when (exception is MySqlConnector.MySqlException or JsonException or InvalidOperationException)
        {
            return Results.Json(new { error = "Recovery settings could not be read/saved. Check database availability and integration encryption configuration; no server action was sent." }, statusCode: 503);
        }
    }
}
