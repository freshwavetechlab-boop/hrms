using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Services;

public static class EmployeeFieldEndpoints
{
    private static AuthUser User(HttpContext context) => (AuthUser)context.Items["User"]!;
    private static bool CanManage(AuthUser user) => user.Permissions.Contains("employees.manage", StringComparer.OrdinalIgnoreCase);
    private static bool CanRead(AuthUser user) => CanManage(user) || user.Permissions.Any(permission => permission is "employees.view" or "reports.view");
    private static bool IsEmployee(DynamicFormDefinition? definition) => definition is { ModuleCode: "EMPLOYEE", EntityType: "EMPLOYEE" };

    public static void MapEmployeeFields(this WebApplication app)
    {
        var group = app.MapGroup("/api/employees/field-configuration");
        group.MapGet("/catalog", async (int clientId, bool? includeValues, EmployeeAttributeRepository repository, HttpContext context) =>
        {
            var user = User(context);
            if (!CanRead(user) || clientId <= 0 || user.ClientId.HasValue && user.ClientId != clientId) return Results.StatusCode(403);
            return Results.Ok(await repository.ExchangeAsync(clientId, user, includeValues ?? true));
        });
        group.MapGet("/forms", async (int clientId, RecruitmentFormRepository repository, HttpContext context) =>
        {
            var user = User(context);
            if (!CanManage(user) || clientId <= 0 || user.ClientId.HasValue && user.ClientId != clientId) return Results.StatusCode(403);
            return Results.Ok((await repository.ListAsync(user, clientId)).Where(IsEmployee).Where(row => row.ClientId == clientId));
        });
        group.MapGet("/lookups", async (int clientId, RecruitmentFormRepository forms, AttachmentRepository attachments, HttpContext context) =>
        {
            var user = User(context);
            if (!CanManage(user) || clientId <= 0 || user.ClientId.HasValue && user.ClientId != clientId) return Results.StatusCode(403);
            return Results.Ok(new { lookupSources = await forms.LookupSourcesAsync(), attachmentConfigurations = await attachments.GetConfigurationsAsync(clientId) });
        });
        group.MapGet("/forms/{id:long}", async (long id, RecruitmentFormRepository repository, HttpContext context) =>
        {
            var user = User(context);
            if (!CanManage(user)) return Results.StatusCode(403);
            var item = await repository.GetAsync(id, user);
            return IsEmployee(item) ? Results.Ok(item) : Results.NotFound();
        });
        group.MapPost("/forms", async (SaveDynamicFormDefinition request, RecruitmentFormRepository repository, HttpContext context) =>
        {
            var user = User(context);
            if (!CanManage(user)) return Results.StatusCode(403);
            if (request.ClientId <= 0 || user.ClientId.HasValue && user.ClientId != request.ClientId) return Results.StatusCode(403);
            if (request.Id > 0 && !IsEmployee(await repository.GetAsync(request.Id, user))) return Results.NotFound();
            request.ModuleCode = "EMPLOYEE"; request.EntityType = "EMPLOYEE"; request.RequiresEmailVerification = false;
            if (!EmployeeAttributeRepository.ExchangeInfotypes.Any(code => request.PurposeCode == "EMPLOYEE_INFOTYPE_" + code)) return Results.BadRequest(new { error = "Choose an employee Info Type." });
            var (item, error) = await repository.SaveDefinitionAsync(request, user);
            return item is null ? Results.BadRequest(new { error }) : Results.Ok(item);
        });
        group.MapPost("/forms/{id:long}/versions", async (long id, SaveDynamicFormVersion request, RecruitmentFormRepository repository, HttpContext context) =>
        {
            var user = User(context);
            if (!CanManage(user)) return Results.StatusCode(403);
            if (!IsEmployee(await repository.GetAsync(id, user))) return Results.NotFound();
            request.FormDefinitionId = id;
            var (item, error) = await repository.SaveVersionAsync(request, user);
            return item is null ? Results.BadRequest(new { error }) : Results.Ok(item);
        });
        group.MapPost("/form-versions/{versionId:long}/publish", async (long versionId, long definitionId, RecruitmentFormRepository repository, HttpContext context) =>
        {
            var user = User(context);
            if (!CanManage(user)) return Results.StatusCode(403);
            var definition = await repository.GetAsync(definitionId, user);
            if (!IsEmployee(definition) || !definition!.Versions.Any(version => version.Id == versionId)) return Results.NotFound();
            var (item, error) = await repository.PublishAsync(versionId, user);
            return item is null ? Results.BadRequest(new { error }) : Results.Ok(item);
        });
    }
}
