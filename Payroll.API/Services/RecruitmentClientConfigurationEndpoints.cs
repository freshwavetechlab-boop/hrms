using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Services;

public static class RecruitmentClientConfigurationEndpoints
{
    public static void Map(WebApplication app, Func<HttpContext, AuthUser> currentUser)
    {
        app.MapGet("/api/recruitment/client-configuration-summary", async (RecruitmentPipelineRepository repository, HttpContext context) =>
        {
            try { return Results.Ok(await repository.ClientConfigurationSummaryAsync(currentUser(context))); }
            catch (UnauthorizedAccessException) { return Results.StatusCode(403); }
        });
    }
}
