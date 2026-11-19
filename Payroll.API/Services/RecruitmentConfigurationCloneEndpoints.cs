using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Services;

public static class RecruitmentConfigurationCloneEndpoints
{
    public static void Map(WebApplication app, Func<HttpContext, AuthUser> currentUser)
    {
        app.MapGet("/api/recruitment-admin/templates", async (RecruitmentAdminRepository repository, int? clientId, HttpContext context) =>
        {
            try { return Results.Ok(await repository.ListTemplatesAsync(currentUser(context), clientId)); }
            catch (UnauthorizedAccessException) { return Results.StatusCode(403); }
        });
        app.MapPost("/api/recruitment-admin/templates/{id:int}/create-similar", async
            (RecruitmentAdminRepository repository, int id, CreateSimilarRecruitmentTemplate request, HttpContext context) =>
        {
            try
            {
                var (row, error) = await repository.CreateSimilarTemplateAsync(id, request, currentUser(context));
                return row is null ? Results.BadRequest(new { error }) : Results.Ok(row);
            }
            catch (UnauthorizedAccessException) { return Results.StatusCode(403); }
            catch (MySqlException error) when (error.Number == 1062) { return Results.Conflict(new { error = "That template code already exists for the target client. Choose another code." }); }
        });
        app.MapPost("/api/recruitment-orchestration/forms/{id:long}/create-similar", async
            (RecruitmentFormRepository repository, long id, CreateSimilarRecruitmentForm request, HttpContext context) =>
        {
            try
            {
                var (row, error) = await repository.CreateSimilarFormAsync(id, request, currentUser(context));
                return row is null ? Results.BadRequest(new { error }) : Results.Ok(row);
            }
            catch (UnauthorizedAccessException) { return Results.StatusCode(403); }
            catch (MySqlException error) when (error.Number == 1062) { return Results.Conflict(new { error = "That form code already exists for the target client. Choose another code." }); }
            catch (InvalidOperationException error) { return Results.BadRequest(new { error = error.Message }); }
        });
    }
}
