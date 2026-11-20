using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Services;

public static class RecruitmentJoiningEndpoints
{
    public static void Map(WebApplication app, Func<HttpContext, AuthUser> currentUser,
        Func<HttpContext,long,string,AuthUser,Task<string>> applyDecision)
    {
        app.MapPost("/api/recruitment/offers/accept-selected", async (RecruitmentTalentRepository repository, RecruitmentDocumentRepository documents, RecruitmentJoiningSelection request, HttpContext context) =>
        {
            var user = currentUser(context);
            if (!RecruitmentPermissions.Has(user, "recruitment.offer.issue")) return Results.StatusCode(403);
            try
            {
                var result = await repository.AcceptSelectedOffersAsync(request, user);
                if (request.RosterRowIds.Count > 0) result.Items.AddRange((await documents.AcceptSelectedRosterOffersAsync(request.ClientId, request.RosterRowIds, user)).Items);
                await Synchronize(result, "Accepted", "Accepted", context, user, applyDecision);
                return Results.Ok(result);
            }
            catch (UnauthorizedAccessException) { return Results.StatusCode(403); }
            catch (InvalidOperationException error) { return Results.BadRequest(new { error = error.Message }); }
        });
        app.MapGet("/api/recruitment/joining-candidates", async (RecruitmentTalentRepository repository, RecruitmentDocumentRepository documents, int clientId, bool? importReleasedForEss, HttpContext context) =>
        {
            var user = currentUser(context);
            if (!CanImport(user) && !RecruitmentPermissions.Has(user, "recruitment.offer.issue", "recruitment.candidate.manage")) return Results.StatusCode(403);
            try
            {
                var simple = importReleasedForEss == true;
                if (simple && !CanImport(user)) return Results.StatusCode(403);
                var rows = (await repository.GetJoiningCandidatesAsync(clientId, user, simple)).Concat(await documents.GetRosterJoiningCandidatesAsync(clientId, user, simple));
                return Results.Ok(rows.OrderBy(row => row.ProposedJoiningDate).ThenBy(row => row.CandidateName).ThenBy(row => row.RowKey));
            }
            catch (UnauthorizedAccessException) { return Results.StatusCode(403); }
        });
        app.MapPost("/api/recruitment/joining-candidates/import", async (RecruitmentTalentRepository repository, RecruitmentDocumentRepository documents, RecruitmentJoiningSelection request, HttpContext context) =>
        {
            var user = currentUser(context);
            if (!CanImport(user)) return Results.StatusCode(403);
            try
            {
                var result = await repository.ImportSelectedJoiningCandidatesAsync(request, user);
                if (request.RosterRowIds.Count > 0)
                    result.Items.AddRange((await documents.ImportSelectedRosterJoiningCandidatesAsync(request.ClientId, request.RosterRowIds, user, request.ImportReleasedForEss)).Items);
                await Synchronize(result, "Imported", "Joined", context, user, applyDecision);
                return Results.Ok(result);
            }
            catch (UnauthorizedAccessException) { return Results.StatusCode(403); }
            catch (InvalidOperationException error) { return Results.BadRequest(new { error = error.Message }); }
        });
    }

    internal static bool CanImport(AuthUser user) => user.Permissions.Any(permission => permission is "employees.manage" or "settings.manage");
    internal static async Task Synchronize(RecruitmentJoiningBatchResult result, string status, string decision, HttpContext context, AuthUser user,
        Func<HttpContext,long,string,AuthUser,Task<string>> applyDecision)
    {
        foreach (var item in result.Items.Where(item => item.Status == status && item.SourceType == "Offer"))
        {
            try { var message = await applyDecision(context, item.ApplicationId, decision, user); if (message.Length > 0) item.Message = message; }
            catch (InvalidOperationException error) { item.Message = "Saved. The hiring pipeline needs review: " + error.Message; }
            catch (UnauthorizedAccessException) { item.Message = "Saved. An authorized hiring user must continue this candidate's pipeline."; }
        }
    }
}
