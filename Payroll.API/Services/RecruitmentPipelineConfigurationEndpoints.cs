using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Services;

public static class RecruitmentPipelineConfigurationEndpoints
{
    public static void Map(WebApplication app, Func<HttpContext, AuthUser> currentUser)
    {
        app.MapPost("/api/recruitment-orchestration/pipeline-configurations", async
            (RecruitmentPipelineRepository repository, SaveRecruitmentPipelineConfiguration request, HttpContext context) =>
        {
            try
            {
                var (result, error) = await repository.SavePipelineConfigurationAsync(request, currentUser(context));
                return result is null ? Results.BadRequest(new { error }) : Results.Ok(result);
            }
            catch (UnauthorizedAccessException) { return Results.StatusCode(403); }
            catch (RecruitmentPipelineConfigurationConflict error) { return Results.Conflict(new { error = error.Message }); }
            catch (InvalidOperationException error) { return Results.BadRequest(new { error = error.Message }); }
        });
    }
}
