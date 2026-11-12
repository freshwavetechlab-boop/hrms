using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Services;

public static class BrowserAttendanceEndpoints
{
    internal static bool CanPunch(AuthUser user) => CanRead(user)
        && user.Permissions.Contains("ess.attendance.mark", StringComparer.OrdinalIgnoreCase);

    internal static bool CanRead(AuthUser user) => user.Id > 0 && user.IsActive && !user.MustChangePassword
        && user.EmployeeId is > 0 && user.Permissions.Contains("ess.self", StringComparer.OrdinalIgnoreCase);

    public static void Map(WebApplication app, Func<HttpContext, AuthUser> currentUser)
    {
        app.MapGet("/api/ess/attendance/punches", async (string? date, EssMssRepository repository, HttpContext context) =>
        {
            var user = currentUser(context);
            if (!CanRead(user)) return Results.StatusCode(403);
            context.Response.Headers.CacheControl = "no-store";
            try { return Results.Ok(await repository.GetOwnAttendancePunchesAsync(user, date)); }
            catch (UnauthorizedAccessException) { return Results.StatusCode(403); }
            catch (InvalidOperationException error) { return Results.BadRequest(new { error = error.Message }); }
        });
        app.MapPost("/api/ess/attendance/punch/browser/validate", async (BrowserAttendancePunchRequest request, EssMssRepository repository, AuthRepository auth, HttpContext context) =>
        {
            var user = currentUser(context);
            if (!CanPunch(user)) return Results.StatusCode(403);
            context.Response.Headers.CacheControl = "no-store";
            var result = await repository.ValidateBrowserAttendancePunchAsync(auth, user, request);
            return Results.Ok(result);
        }).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(32768));
        app.MapPost("/api/ess/attendance/punch/browser", async (BrowserAttendancePunchRequest request, EssMssRepository repository, AuthRepository auth, HttpContext context) =>
        {
            var user = currentUser(context);
            if (!CanPunch(user)) return Results.StatusCode(403);
            context.Response.Headers.CacheControl = "no-store";
            var result = await repository.RecordBrowserAttendancePunchAsync(auth, user, request);
            return result.PunchRecorded ? Results.Created($"/api/ess/attendance/punch/{result.PunchId}", result) : Results.BadRequest(result);
        }).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(32768));
    }
}
