using Microsoft.AspNetCore.Mvc;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Services;

public static class SalaryTemplateEndpoints
{
    private const string TemplateRoute = "/api/clients/{clientId:int}/salary-templates/{templateId:long}";

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

        app.MapPut(TemplateRoute, async (int clientId, long templateId, SalaryTemplateUpdateRequest request, SettingsRepository repository, HttpContext context) =>
        {
            if (!canAccessClient(context, clientId) || !PayrollAdvisorService.CanAccess(currentUser(context), clientId)) return Results.StatusCode(403);
            try
            {
                var saved = await repository.UpdateClientSalaryTemplateAsync(clientId, templateId, request);
                return saved is null ? Results.NotFound() : Results.Ok(saved);
            }
            catch (System.Data.DBConcurrencyException error) { return Results.Conflict(new { error = error.Message }); }
            catch (InvalidOperationException error) { return Results.BadRequest(new { error = error.Message }); }
        }).WithName("UpdateClientSalaryTemplate").WithOpenApi();

        app.MapGet(TemplateRoute + "/deletion-preview", async (int clientId, long templateId, SettingsRepository repository, [FromServices] PayslipDesignRepository designs, HttpContext context) =>
        {
            if (!canAccessClient(context, clientId) || !PayrollAdvisorService.CanAccess(currentUser(context), clientId)) return Results.StatusCode(403);
            var (preview, _) = await repository.ReviewSalaryTemplateDeletionAsync(clientId, templateId, designs);
            return preview is null ? Results.NotFound() : Results.Ok(preview);
        }).AddEndpointFilter(HandleMappingRead).WithName("PreviewClientSalaryTemplateDeletion").WithOpenApi();

        app.MapGet(TemplateRoute + "/editable-mappings", async (int clientId, long templateId, int? offset, int? limit, SettingsRepository repository, HttpContext context) =>
        {
            var actor = currentUser(context);
            if (!canAccessClient(context, clientId) || !PayrollAdvisorService.CanAccess(actor, clientId)) return Results.StatusCode(403);
            if (offset is < 0 || limit is < 1 or > 100) return Results.BadRequest(new { error = "Select a valid mapping page." });
            var page = await repository.GetEditableSalaryTemplateMappingsAsync(clientId, templateId, offset ?? 0, limit ?? 25, actor);
            return page is null ? Results.NotFound() : Results.Ok(page);
        }).AddEndpointFilter(HandleMappingRead).WithName("EditableSalaryTemplateMappings").WithOpenApi();

        app.MapPost(TemplateRoute + "/unlink", async (int clientId, long templateId, SalaryTemplateUnlinkRequest request, SettingsRepository repository, HttpContext context) =>
        {
            var actor = currentUser(context);
            if (!canAccessClient(context, clientId) || !SettingsRepository.CanUnlinkMapping(actor, clientId, request.Kind)) return Results.StatusCode(403);
            if (string.IsNullOrWhiteSpace(request.Key) || request.Key.Length > 300 || request.Version is not { Length: 64 } || !request.Version.All(Uri.IsHexDigit))
                return Results.BadRequest(new { error = "Review one current mapping before unlinking." });
            try
            {
                var found = await repository.UnlinkSalaryTemplateMappingAsync(clientId, templateId, request, actor);
                return found ? Results.NoContent() : Results.NotFound();
            }
            catch (InvalidOperationException e) { return Results.Conflict(new { error = e.Message }); }
        }).WithName("UnlinkSalaryTemplateMapping").WithOpenApi();

        app.MapDelete(TemplateRoute, async (int clientId, long templateId, string? version, SettingsRepository repository, [FromServices] PayslipDesignRepository designs, HttpContext context) =>
        {
            if (!canAccessClient(context, clientId) || !PayrollAdvisorService.CanAccess(currentUser(context), clientId)) return Results.StatusCode(403);
            if (version is not { Length: 64 } || !version.All(Uri.IsHexDigit)) return Results.BadRequest(new { error = "Review the template and its mappings before deleting." });
            var (preview, deleted) = await repository.ReviewSalaryTemplateDeletionAsync(clientId, templateId, designs, version);
            if (preview is null) return Results.NotFound();
            if (deleted) return Results.NoContent();
            var error = preview.Mappings.Count > 0 ? "This salary template is in use. Review the listed mappings." : "The salary template has changed. Review its current configuration before deleting.";
            return Results.Conflict(new { error, preview });
        }).WithName("DeleteClientSalaryTemplate").WithOpenApi();
    }

    internal static async ValueTask<object?> HandleMappingRead(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        var context = invocation.HttpContext;
        if (!context.Response.HasStarted) context.Response.Headers.CacheControl = "no-store";
        try
        {
            return await next(invocation);
        }
        catch (Exception error) when (!context.RequestAborted.IsCancellationRequested && !context.Response.HasStarted)
        {
            var requestId = context.TraceIdentifier;
            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("SalaryTemplateMappings");
            logger.LogError(error, "Unable to read salary template mappings for client {ClientId}, template {TemplateId}. Request {RequestId}",
                context.Request.RouteValues["clientId"], context.Request.RouteValues["templateId"], requestId);
            context.Response.Headers["X-Request-ID"] = requestId;
            var status = error is TimeoutException or MySqlConnector.MySqlException { IsTransient: true }
                ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status500InternalServerError;
            return Results.Json(new { error = $"Unable to check salary template mappings. Reference {requestId}.", requestId }, statusCode: status);
        }
    }
}
