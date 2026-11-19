using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Services;

public static class AttendanceApprovalEndpoints
{
    public static void Map(WebApplication app, Func<HttpContext, AuthUser> currentUser)
    {
        app.MapPost("/api/leave-attendance/attendance/approval/policy", async (AttendanceApprovalRepository repository, ConfigureAttendanceApproval request, HttpContext context) =>
        {
            try { return Results.Ok(await repository.ConfigureAsync(currentUser(context), request)); }
            catch (UnauthorizedAccessException) { return Results.StatusCode(403); }
            catch (AttendanceApprovalException error) { return Results.Conflict(new { error = error.Message }); }
        });
        app.MapGet("/api/ess/mss/attendance/approval", async (AttendanceApprovalRepository repository, string month, int? workLocationId, HttpContext context) =>
        {
            var user = currentUser(context);
            if (user.ClientId is not > 0) return Results.StatusCode(403);
            try { return Results.Ok(await repository.ContextAsync(user, user.ClientId.Value, month, workLocationId)); }
            catch (UnauthorizedAccessException) { return Results.StatusCode(403); }
            catch (AttendanceApprovalException error) { return Results.Conflict(new { error = error.Message }); }
        });
        app.MapPost("/api/ess/mss/attendance/approval/submit", async (AttendanceApprovalRepository repository, SubmitAttendanceApproval request, HttpContext context) =>
        {
            try { return Results.Ok(await repository.SubmitAsync(currentUser(context), request)); }
            catch (UnauthorizedAccessException) { return Results.StatusCode(403); }
            catch (AttendanceApprovalException error) { return Results.Conflict(new { error = error.Message }); }
        });
        app.MapGet("/api/leave-attendance/attendance/approval", async (AttendanceApprovalRepository repository, int clientId, string month, int? workLocationId, HttpContext context) =>
        {
            try { return Results.Ok(await repository.ContextAsync(currentUser(context), clientId, month, workLocationId)); }
            catch (UnauthorizedAccessException) { return Results.StatusCode(403); }
            catch (AttendanceApprovalException error) { return Results.Conflict(new { error = error.Message }); }
        });
    }
}
