using Payroll.API.Models;

namespace Payroll.API.Services;

public static class AttendanceApiAdapterEndpoints
{
    public static bool Allowed(AuthUser? user) => user is { ClientId: null } && user.Permissions.Contains("settings.manage", StringComparer.OrdinalIgnoreCase);

    public static void AddAttendanceApiAdapters(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpClient("AttendanceApiAdapter").ConfigurePrimaryHttpMessageHandler(AttendanceApiProtocol.CreateHandler).RemoveAllLoggers();
        services.AddSingleton<AttendanceApiAdapterService>();
        if (configuration.GetValue("BackgroundWorkers:Enabled", true)) services.AddHostedService<AttendanceApiAdapterWorker>();
    }

    public static void MapAttendanceApiAdapters(this WebApplication app)
    {
        var group = app.MapGroup("/api/integrations/attendance/adapters");
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            if (!Allowed(context.HttpContext.Items["User"] as AuthUser)) return Results.StatusCode(403);
            try { return await next(context); }
            catch (InvalidOperationException e) { return Results.BadRequest(new { error = e.Message }); }
            catch (Exception e) when (e is MySqlConnector.MySqlException or System.Text.Json.JsonException)
            { return Results.Json(new { error = "Attendance adapter settings are unavailable. Check database connectivity/configuration." }, statusCode: 503); }
        });
        group.MapGet("", async (int clientId, AttendanceApiAdapterService service) => Results.Ok(await service.ListAsync(clientId)));
        group.MapPost("", async (SaveAttendanceApiAdapter request, AttendanceApiAdapterService service) => Results.Ok(await service.SaveAsync(request)));
        group.MapDelete("/{id}", async (string id, int clientId, AttendanceApiAdapterService service) => { await service.DeleteAsync(clientId, id); return Results.Ok(new { deleted = true }); });
        group.MapPost("/{id}/test", async (string id, int clientId, RunAttendanceApiAdapter request, AttendanceApiAdapterService service, HttpContext context) =>
            Results.Ok(await service.RunAsync(clientId, id, request, true, context.RequestAborted)));
        group.MapPost("/{id}/sync", async (string id, int clientId, RunAttendanceApiAdapter request, AttendanceApiAdapterService service, HttpContext context) =>
            Results.Ok(await service.RunAsync(clientId, id, request, false, context.RequestAborted)));
    }
}
