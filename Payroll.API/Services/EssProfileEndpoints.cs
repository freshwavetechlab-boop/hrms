using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Services;

public static class EssProfileEndpoints
{
    internal static bool CanAccess(AuthUser user) => user.IsActive && user.EmployeeId > 0
        && user.Permissions.Contains("ess.self", StringComparer.OrdinalIgnoreCase);

    public static void MapEssProfile(this WebApplication app, Func<HttpContext, AuthUser> currentUser)
    {
        app.MapGet("/api/ess/profile", async (EssMssRepository repository, HttpContext context) =>
        {
            var user = currentUser(context);
            if (!CanAccess(user)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var profile = await repository.GetProfileAsync(user.EmployeeId!.Value, user.ClientId);
            return profile is null ? Results.NotFound() : Results.Ok(profile);
        })
        .WithName("GetEssProfile")
        .WithOpenApi();

        app.MapPost("/api/ess/profile", async (EssMssRepository repository, SaveEssProfileRequest request, HttpContext context) =>
        {
            var user = currentUser(context);
            if (!CanAccess(user)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var (profile, error) = await repository.SaveProfileAsync(user.EmployeeId!.Value, user.ClientId, request, user.Email);
            return profile is null ? Results.BadRequest(new { error }) : Results.Ok(profile);
        })
        .WithName("SaveEssProfile")
        .WithOpenApi();
    }
}
