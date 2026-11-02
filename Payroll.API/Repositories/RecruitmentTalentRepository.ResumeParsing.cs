using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class RecruitmentTalentRepository
{
    private static async Task<(ResumeIntakeParsingContext? Context, string Error)> ResumeIntakeParsingContextAsync(
        MySqlConnection db, int clientId, long positionId, long? jobPostingId, bool talentPoolOnly)
    {
        IReadOnlyList<string> jobContext = [];
        JobPostingAtsRow? posting = null;
        if (!talentPoolOnly)
        {
            var position = await ResumeJobContextAsync(db, positionId, clientId);
            if (position is null) return (null, "Selected job/JD was not found for this client.");
            if (position.Status is "Closed" or "Cancelled" or "Filled")
                return (null, $"Resumes cannot be added to a {position.Status} position.");
            jobContext = ResumeJobContextParts(position);
            if (jobPostingId is > 0)
            {
                var columns = await JobPostingParsingColumnsAvailableAsync(db);
                posting = await db.QueryFirstOrDefaultAsync<JobPostingAtsRow>($@"SELECT Id,AutoRunAts,
{(columns ? "EnableResumeParsing,EnableAiParsing" : "TRUE EnableResumeParsing,TRUE EnableAiParsing")}
FROM recruitment_job_postings
WHERE Id=@JobPostingId AND PositionId=@PositionId AND ClientId=@ClientId",
                    new { JobPostingId = jobPostingId, PositionId = positionId, ClientId = clientId });
                if (posting is null) return (null, "Selected job posting does not belong to this open position.");
            }
        }
        var features = await FeatureSettingsAsync(db, clientId);
        return (new ResumeIntakeParsingContext(
            ResumeParsingJobPolicy.Resolve(features.EnableResumeParsing, posting?.EnableResumeParsing, posting?.EnableAiParsing),
            jobContext, posting?.AutoRunAts ?? false), "");
    }

    private sealed record ResumeIntakeParsingContext(
        ResumeParsingJobPolicy Policy, IReadOnlyList<string> JobContext, bool AutoRunAts);

    private async Task<ResumeApplicationParsingContext?> ResumeUploadContextAsync(MySqlConnection db,
        RecruitmentCandidate candidate, long? applicationId, AuthUser user, CancellationToken cancellationToken)
    {
        var columns = await JobPostingParsingColumnsAvailableAsync(db);
        // With an explicit job, authorise exactly that candidate/application/client.
        // Generic profile uploads keep the prior latest-active-application selection.
        var scope = user.ClientId ?? (applicationId.HasValue ? null : candidate.ClientId > 0 ? candidate.ClientId : (int?)null);
        return await db.QueryFirstOrDefaultAsync<ResumeApplicationParsingContext>(new CommandDefinition($@"
SELECT a.Id ApplicationId,a.CandidateId,a.PositionId,a.ClientId,posting.Id JobPostingId,
{(columns ? "posting.EnableResumeParsing" : "CASE WHEN posting.Id IS NOT NULL THEN TRUE ELSE NULL END")} EnableResumeParsing,
{(columns ? "posting.EnableAiParsing" : "CASE WHEN posting.Id IS NOT NULL THEN TRUE ELSE NULL END")} EnableAiParsing
FROM recruitment_candidate_applications a
JOIN recruitment_open_positions positionRow ON positionRow.Id=a.PositionId AND positionRow.ClientId=a.ClientId
LEFT JOIN recruitment_job_postings posting ON posting.Id=a.JobPostingId
 AND posting.PositionId=a.PositionId AND posting.ClientId=a.ClientId
WHERE a.CandidateId=@CandidateId AND (@ApplicationId IS NULL OR a.Id=@ApplicationId)
 AND (@ClientId IS NULL OR a.ClientId=@ClientId)
 AND (@ApplicationId IS NOT NULL OR a.CurrentStage NOT IN ('Rejected','Withdrawn','Joined'))
ORDER BY (a.ApplicationType='Application') DESC,a.UpdatedAt DESC,a.Id DESC LIMIT 1",
            new { CandidateId = candidate.Id, ApplicationId = applicationId, ClientId = scope }, cancellationToken: cancellationToken));
    }

    internal static bool IsResumeApplicationContextAllowed(ResumeApplicationParsingContext? context,
        long candidateId, long? applicationId, int? actorClientId) => context is not null
        && context.CandidateId == candidateId && (!applicationId.HasValue || context.ApplicationId == applicationId)
        && (!actorClientId.HasValue || context.ClientId == actorClientId);

    internal sealed class ResumeApplicationParsingContext
    {
        public long ApplicationId { get; set; }
        public long CandidateId { get; set; }
        public long PositionId { get; set; }
        public int ClientId { get; set; }
        public long? JobPostingId { get; set; }
        public bool? EnableResumeParsing { get; set; }
        public bool? EnableAiParsing { get; set; }
    }
}
