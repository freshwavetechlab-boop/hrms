using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Services;

public static class EmployeeProfileCompletionEndpoints
{
    private static AuthUser User(HttpContext context) => (AuthUser)context.Items["User"]!;
    private static bool Has(AuthUser user, string permission) => user.Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase);
    private static bool CanRead(AuthUser user) => Has(user, "employees.view") || Has(user, "employees.manage");
    private static bool InScope(AuthUser user, int clientId) => clientId > 0 && (!user.ClientId.HasValue || user.ClientId == clientId);

    public static void MapEmployeeProfileCompletion(this WebApplication app)
    {
        app.MapGet("/api/employees/profile-completion", async (int? clientId, EmployeeProfileCompletionService service, HttpContext context) =>
        {
            var user = User(context);
            if (!CanRead(user) || (clientId > 0 && !InScope(user, clientId.Value))) return Results.StatusCode(403);
            return Results.Ok(await service.StatusAsync(clientId > 0 ? clientId : null, user));
        });
        app.MapPost("/api/employees/profile-completion/setup", async (EmployeeCompletionSettingsRequest request, EmployeeProfileCompletionService service, EssMssRepository ess, HttpContext context) =>
        {
            var user = User(context);
            if (!Has(user, "employees.manage") || !InScope(user, request.ClientId)) return Results.StatusCode(403);
            var template = await service.EnsureTemplateAsync(request.ClientId, user);
            return Results.Ok(new { FirstEditEnabled = await ess.GetFirstProfileEditAsync(request.ClientId), Template = template });
        });
        app.MapPut("/api/employees/profile-completion/settings", async (EmployeeCompletionSettingsRequest request, EssMssRepository ess, HttpContext context) =>
        {
            var user = User(context);
            if (!Has(user, "employees.manage") || !InScope(user, request.ClientId)) return Results.StatusCode(403);
            await ess.SetFirstProfileEditAsync(request.ClientId, request.FirstEditEnabled, user.Id);
            return Results.Ok(new { request.FirstEditEnabled });
        });
        app.MapPost("/api/employees/profile-completion/preview", async (EmployeeCompletionMailRequest request, EmployeeProfileCompletionService service, CommunicationRepository communications, HttpContext context) =>
        {
            var user = User(context);
            if (!CanRead(user) || !Has(user, "employee.communication.send") || !InScope(user, request.ClientId)) return Results.StatusCode(403);
            try
            {
                var mail = await PrepareAsync(request, service, user);
                return Results.Ok(await communications.PreviewAsync(mail, user));
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
        app.MapPost("/api/employees/profile-completion/send", async (EmployeeCompletionMailRequest request, EmployeeProfileCompletionService service, CommunicationRepository communications, HttpContext context) =>
        {
            var user = User(context);
            if (!CanRead(user) || !Has(user, "employee.communication.send") || !InScope(user, request.ClientId)) return Results.StatusCode(403);
            if (string.IsNullOrWhiteSpace(request.IdempotencyKey)) return Results.BadRequest(new { error = "A send request key is required." });
            try
            {
                var mail = await PrepareAsync(request, service, user);
                var (item, error) = await communications.SendAsync(mail, user);
                return item is null ? Results.BadRequest(new { error }) : Results.Ok(item);
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
        app.MapPost("/api/ess/profile/edit-request", async (WorkflowActionRequest request, EssMssRepository ess, WorkflowRepository workflows, HttpContext context) =>
        {
            var user = User(context);
            if (!Has(user, "ess.self") || !user.EmployeeId.HasValue) return Results.StatusCode(403);
            var (instance, error) = await ess.RequestProfileEditAsync(user, request.Comment, workflows);
            return instance is null ? Results.BadRequest(new { error }) : Results.Ok(await ess.GetProfileAsync(user.EmployeeId.Value, user.ClientId));
        });
    }

    private static async Task<SendEmployeeCommunicationRequest> PrepareAsync(EmployeeCompletionMailRequest request, EmployeeProfileCompletionService service, AuthUser user)
    {
        var ids = (request.EmployeeIds ?? []).Distinct().ToArray();
        if (ids.Length == 0 || ids.Length > CommunicationRepository.MaxRecipients) throw new InvalidOperationException($"Select between 1 and {CommunicationRepository.MaxRecipients:N0} employees per client.");
        var statuses = await service.StatusAsync(request.ClientId, user, ids);
        if (statuses.Count != ids.Length) throw new InvalidOperationException("One or more employees are outside the selected client scope.");
        var eligible = statuses.Where(row => row.CanSendEmail).Select(row => row.EmployeeId).ToList();
        if (eligible.Count == 0) throw new InvalidOperationException("No selected employee needs a reminder with an available email and ESS access.");
        var template = await service.EnsureTemplateAsync(request.ClientId, user);
        if (!template.IsActive) throw new InvalidOperationException("The missing-information email template is disabled. Enable it in Mail templates first.");
        return new SendEmployeeCommunicationRequest { ClientId = request.ClientId, Channel = CommunicationChannels.Email, TemplateId = template.Id, EmployeeIds = eligible, SelectionMode = "SelectedEmployees", IdempotencyKey = request.IdempotencyKey };
    }
}
