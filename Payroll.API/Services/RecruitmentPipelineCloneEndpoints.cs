using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Services;

public static class RecruitmentPipelineCloneEndpoints
{
    public static void Map(WebApplication app, Func<HttpContext, AuthUser> currentUser)
    {
        app.MapPost("/api/recruitment-orchestration/pipelines/{definitionId:long}/create-similar", async
            (RecruitmentPipelineRepository repository, long definitionId, CreateSimilarRecruitmentPipelineRequest request, HttpContext context) =>
        {
            try
            {
                var (result, error) = await repository.CreateSimilarPipelineAsync(definitionId, request, currentUser(context));
                return result is null ? Results.BadRequest(new { error }) : Results.Ok(result);
            }
            catch (UnauthorizedAccessException) { return Results.StatusCode(403); }
            catch (InvalidOperationException error) { return Results.BadRequest(new { error = error.Message }); }
        });
    }
}
