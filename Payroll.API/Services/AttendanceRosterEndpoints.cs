using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Services;

public static class AttendanceRosterEndpoints
{
    public static void MapAttendanceRoster(this WebApplication app, Func<HttpContext, int, bool> canRead, Func<HttpContext, int, bool> canManage)
    {
        var group = app.MapGroup("/api/leave-attendance/shift-assignments");
        group.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (InvalidOperationException exception) { return Results.BadRequest(new { error = exception.Message }); }
        });
        group.MapGet("", async (int clientId, DateTime from, DateTime to, HttpContext context, LeaveAttendanceRepository repository) =>
            canRead(context, clientId) ? Results.Ok(await repository.GetShiftAssignmentsAsync(clientId, from, to)) : Results.StatusCode(403));
        group.MapGet("/employees", async (int clientId, int shiftId, string? search, HttpContext context, LeaveAttendanceRepository repository) =>
            canManage(context, clientId) ? Results.Ok(await repository.GetRosterEmployeesAsync(clientId, shiftId, search?.Length > 100 ? search[..100] : search)) : Results.StatusCode(403));
        group.MapPost("", async (SaveShiftAssignmentsRequest request, HttpContext context, LeaveAttendanceRepository repository) =>
        {
            if (!canManage(context, request.ClientId) || context.Items["User"] is not AuthUser user) return Results.StatusCode(403);
            return Results.Ok(new { assigned = await repository.SaveShiftAssignmentsAsync(request, user.Id) });
        });
        group.MapDelete("/{id:int}", async (int id, int clientId, HttpContext context, LeaveAttendanceRepository repository) =>
        {
            if (!canManage(context, clientId) || context.Items["User"] is not AuthUser user) return Results.StatusCode(403);
            await repository.CancelShiftAssignmentAsync(clientId, id, user.Id); return Results.NoContent();
        });
    }
}
