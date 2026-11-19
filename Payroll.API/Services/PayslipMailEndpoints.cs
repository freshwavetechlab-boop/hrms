using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Services;

public static class PayslipMailEndpoints
{
    public static void Map(WebApplication app, Func<HttpContext, bool> canRead, Func<HttpContext, bool> canSend,
        Func<HttpContext, int, bool> canAccessClient, Func<HttpContext, AuthUser> currentUser)
    {
        app.MapGet("/api/pay-runs/{id:int}/payslips/mail-status", async (int id, HttpContext context, PayRunRepository repository,
            EssMssRepository payslips, NotificationRepository notifications) =>
        {
            if (!canRead(context)) return Results.StatusCode(403);
            var run = await repository.GetAsync(id);
            if (run is null) return Results.NotFound();
            if (!canAccessClient(context, run.ClientId)) return Results.StatusCode(403);
            if (run.Status is not ("Approved" or "Partially Paid" or "Paid")) return Results.BadRequest(new { error = "Select an approved pay run." });
            var emails = await payslips.GetPayslipRecipientEmailsAsync(run.ClientId, run.Employees.Where(e => !e.IsSkipped).Select(e => e.EmployeeId).ToArray());
            return Results.Ok(await notifications.GetRegisterMailAsync(run, emails));
        }).WithName("GetRegisterPayslipMailStatus");
        app.MapPost("/api/pay-runs/{id:int}/payslips/send-job", async (int id, PayslipMailJobRequest request, HttpContext context,
            PayRunRepository repository, EssMssRepository payslips, NotificationRepository notifications) =>
        {
            if (!canSend(context)) return Results.StatusCode(403);
            if (NotificationRepository.ValidateRegisterMailRequest(request) is { } invalid) return Results.BadRequest(new { error = invalid });
            var run = await repository.GetAsync(id);
            if (run is null) return Results.NotFound();
            if (!canAccessClient(context, run.ClientId)) return Results.StatusCode(403);
            if (run.Status is not ("Approved" or "Partially Paid" or "Paid")) return Results.BadRequest(new { error = "Only approved payroll can be emailed." });
            if (request.EmployeeIds.Any(employeeId => !run.Employees.Any(e => e.EmployeeId == employeeId && !e.IsSkipped)))
                return Results.BadRequest(new { error = "Every selected employee must belong to this pay run." });
            try
            {
                var emails = await payslips.GetPayslipRecipientEmailsAsync(run.ClientId, request.EmployeeIds.ToArray());
                var documents = await payslips.GetLocationPayslipDocumentsAsync(run.ClientId, id);
                var result = await notifications.QueueRegisterMailAsync(run, request, emails, documents, currentUser(context));
                return result.Item is null ? Results.BadRequest(new { error = result.Error }) : Results.Ok(result.Item);
            }
            catch (InvalidOperationException error) { return Results.BadRequest(new { error = error.Message }); }
        }).WithName("QueueRegisterPayslipMailJob");
    }
}
