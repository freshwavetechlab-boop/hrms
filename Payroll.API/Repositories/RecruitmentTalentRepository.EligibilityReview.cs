using System.Text.Encodings.Web;
using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class RecruitmentTalentRepository
{
    private static readonly JsonSerializerOptions ReviewJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static async Task<List<ManualEligibilityEvidence>> ReadManualEligibilityEvidenceAsync(MySqlConnection db, long[] ids)
    {
        if (ids.Length == 0) return [];
        return (await db.QueryAsync<ManualEligibilityEvidence>(@"SELECT score.ApplicationId,score.ResumeId,evidence.ExpectedValue,evidence.ActualValue
FROM recruitment_application_scores score JOIN recruitment_application_score_evidence evidence ON evidence.ApplicationScoreId=score.Id
WHERE score.ApplicationId IN @Ids AND evidence.EvidenceType=@Type ORDER BY evidence.Id DESC",
            new { Ids = ids, Type = RecruitmentEligibilityReviewPolicy.EvidenceType })).ToList();
    }

    private static Dictionary<string, RecruitmentEligibilityReviewDecision> ValidManualReviews(IEnumerable<ManualEligibilityEvidence> evidence,
        long applicationId, long resumeId, string hash, DateTime? atOrBefore = null)
    {
        var result = new Dictionary<string, RecruitmentEligibilityReviewDecision>();
        foreach (var row in evidence.Where(x => x.ApplicationId == applicationId && x.ResumeId == resumeId && x.ExpectedValue.StartsWith(hash + "|", StringComparison.Ordinal)))
        {
            var key = row.ExpectedValue[(hash.Length + 1)..];
            if (result.ContainsKey(key)) continue;
            try
            {
                var review = JsonSerializer.Deserialize<RecruitmentEligibilityReviewDecision>(row.ActualValue);
                if (review is not null && review.Key == key && review.ReviewerUserId > 0
                    && (!atOrBefore.HasValue || review.ReviewedAtUtc <= atOrBefore.Value)) result[key] = review;
            }
            catch (JsonException) { /* Malformed legacy evidence is never an approval. */ }
        }
        return result;
    }

    public async Task<RecruitmentEligibilityReviewContext?> GetEligibilityReviewAsync(long applicationId, AuthUser user)
    {
        await using var db = Db(); await db.OpenAsync();
        return await EligibilityReviewContextAsync(db, applicationId, user);
    }

    private static async Task<RecruitmentEligibilityReviewContext?> EligibilityReviewContextAsync(MySqlConnection db, long applicationId, AuthUser user)
    {
        var app = await ApplicationByIdAsync(db, applicationId, user);
        if (app is null) return null;
        var facts = await db.QuerySingleAsync<ReviewResumeFacts>(@"SELECT COALESCE(resume.Id,0) ResumeId,resume.AttachmentPublicId,
COALESCE(facts.TotalExperienceMonths,NULLIF(candidate.TotalExperienceMonths,0)) ExperienceMonths,
COALESCE(posting.JobDescriptionVersionId,positionRow.ApprovedJobDescriptionVersionId) JobDescriptionVersionId
FROM recruitment_candidate_applications applicationRow JOIN recruitment_candidates candidate ON candidate.Id=applicationRow.CandidateId
JOIN recruitment_open_positions positionRow ON positionRow.Id=applicationRow.PositionId
LEFT JOIN recruitment_job_postings posting ON posting.Id=applicationRow.JobPostingId AND posting.PositionId=applicationRow.PositionId
LEFT JOIN recruitment_candidate_resumes resume ON resume.Id=applicationRow.ResumeId AND resume.CandidateId=candidate.Id
LEFT JOIN recruitment_resume_parse_facts facts ON facts.ResumeId=resume.Id WHERE applicationRow.Id=@Id", new { Id = applicationId });
        var score = await db.QueryFirstOrDefaultAsync<RecruitmentApplicationScore>(@"SELECT * FROM recruitment_application_scores
WHERE ApplicationId=@Id AND ResumeId=@ResumeId AND IsCurrent=TRUE ORDER BY Id DESC LIMIT 1", new { Id = applicationId, facts.ResumeId });
        var context = new RecruitmentEligibilityReviewContext
        {
            ApplicationId = app.Id, ApplicationCode = app.ApplicationCode, CandidateName = app.CandidateName,
            ResumeId = facts.ResumeId, AttachmentPublicId = facts.AttachmentPublicId, AtsScore = app.AtsScore,
            ParsedExperienceMonths = facts.ExperienceMonths, RequiredExperienceRange = app.RequiredExperienceRange,
            EligibilityStatus = app.EligibilityStatus, Reasons = app.EligibilityReasons, ScoreId = score?.Id ?? 0
        };
        if (score is null) return context;
        await HydrateScoresAsync(db, [score]);
        context.ScoreIsStale = score.PositionSnapshot?.JobDescriptionVersionId != facts.JobDescriptionVersionId
            || score.PositionSnapshot?.ExperienceRange != app.RequiredExperienceRange;
        context.RequirementsHash = RecruitmentEligibilityReviewPolicy.ContextHash(facts.ResumeId, score.PositionSnapshotHash, score.SkillMatches);
        var reviews = context.ScoreIsStale ? [] : ValidManualReviews(await ReadManualEligibilityEvidenceAsync(db, [app.Id]), app.Id, facts.ResumeId, context.RequirementsHash);
        var mandatory = (await MandatoryApplicationReviewAsync(db, [app.Id])).GetValueOrDefault(app.Id) ?? [];
        foreach (var skill in score.SkillMatches.Where(x => x.SkillType == "Required"))
        {
            var key = RecruitmentEligibilityReviewPolicy.SkillKey(skill);
            var status = RecruitmentEligibilityReviewPolicy.SkillStatus(skill, reviews);
            context.Items.Add(new()
            {
                Key = key, Requirement = "Mandatory skill: " + skill.SkillName + (skill.MinimumYears > 0 ? $" — minimum {skill.MinimumYears:0.##} years" : ""),
                Status = status == "Matched" ? "Met" : status is "InsufficientExperience" or "VerifiedNotMet" ? "NotMet" : "NeedsReview",
                Evidence = skill.EvidenceExcerpt, CanReview = skill.MatchStatus != "InsufficientExperience" && !context.ScoreIsStale,
                CanWaive = RecruitmentEligibilityReviewPolicy.CanApproveExceptions(user) && !context.ScoreIsStale,
                Review = reviews.GetValueOrDefault(key)
            });
        }
        foreach (var reason in mandatory)
        {
            var key = RecruitmentEligibilityReviewPolicy.RequirementKey("requirement", reason);
            var review = reviews.GetValueOrDefault(key);
            context.Items.Add(new() { Key = key, Requirement = reason, Review = review,
                Status = review?.Decision == "Verified" ? "Met" : review?.Decision == "NotMet" ? "NotMet" : "NeedsReview", CanReview = !context.ScoreIsStale,
                CanWaive = RecruitmentEligibilityReviewPolicy.CanApproveExceptions(user) && !context.ScoreIsStale && RecruitmentEligibilityReviewPolicy.CanWaiveRequirement(reason) });
        }
        var minimum = RecruitmentEligibility.MinimumExperienceMonths(app.RequiredExperienceRange);
        if (minimum is null or > 0)
            context.Items.Add(new()
            {
                Key = RecruitmentEligibilityReviewPolicy.ExperienceMinimumKey,
                Requirement = "Minimum total experience: " + app.RequiredExperienceRange,
                Status = !minimum.HasValue || !app.CandidateExperienceMonths.HasValue ? "NeedsReview"
                    : app.CandidateExperienceMonths >= minimum ? "Met" : "NotMet",
                Evidence = "Recorded experience and the JD minimum remain unchanged when an exception is approved.",
                CanReview = false, CanWaive = RecruitmentEligibilityReviewPolicy.CanApproveExceptions(user) && !context.ScoreIsStale,
                Review = reviews.GetValueOrDefault(RecruitmentEligibilityReviewPolicy.ExperienceMinimumKey)
            });
        if (reviews.TryGetValue(RecruitmentEligibilityReviewPolicy.ExperienceKey, out var experience) && experience.Decision == "Verified")
        { context.VerifiedExperienceMonths = experience.VerifiedExperienceMonths; context.ExperienceEvidence = experience.Note; }
        return context;
    }

    public async Task<(RecruitmentEligibilityReviewContext? Row, string Error)> SaveEligibilityReviewAsync(long applicationId,
        SaveRecruitmentEligibilityReview request, AuthUser user, CancellationToken cancellationToken)
    {
        request.Decisions ??= [];
        request.ExperienceEvidence ??= "";
        if (!RecruitmentEligibilityReviewPolicy.CanApproveExceptions(user)) return (null, "Recruitment administration permission is required to save an eligibility review.");
        if (request.Decisions.Any(x => x is null)) return (null, "Select valid requirement decisions.");
        if (request.ExpectedResumeId <= 0 || request.Decisions.Count > 100) return (null, "Select the current resume and a bounded set of requirements.");
        if (request.ExperienceChanged && request.VerifiedExperienceMonths.HasValue
            && (request.VerifiedExperienceMonths is < 0 or > 960 || request.ExperienceEvidence.Trim().Length is < 8 or > 400))
            return (null, "Enter verified experience from 0–960 months and 8–400 characters of supporting evidence.");
        var connection = new MySqlConnectionStringBuilder(configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Recruitment database is not configured.")) { Pooling = false };
        await using var db = new MySqlConnection(connection.ConnectionString); await db.OpenAsync(cancellationToken);
        if (await RecruitmentApplicationResumeGuard.AcquireAsync(db, applicationId, cancellationToken) != 1)
            return (null, "This application is being updated. Refresh after it finishes.");
        try
        {
            var context = await EligibilityReviewContextAsync(db, applicationId, user);
            if (context is null) return (null, "Application was not found.");
            if (context.ResumeId != request.ExpectedResumeId) return (null, "The resume changed. Refresh before reviewing.");
            if (!request.RunAtsOnly)
            {
                if (context.ScoreId <= 0 || context.ScoreIsStale || context.ScoreId != request.ExpectedScoreId || context.RequirementsHash != request.RequirementsHash)
                    return (null, "The assessment or JD changed. Refresh ATS before reviewing.");
                if (request.Decisions.Select(x => x.Key).Distinct().Count() != request.Decisions.Count) return (null, "A requirement was supplied more than once.");
                foreach (var decision in request.Decisions)
                {
                    var error = RecruitmentEligibilityReviewPolicy.ValidateDecision(decision, context.Items.FirstOrDefault(x => x.Key == decision.Key), RecruitmentEligibilityReviewPolicy.CanApproveExceptions(user));
                    if (error.Length > 0) return (null, error);
                }
                var decisions = request.Decisions.ToList();
                if (request.ExperienceChanged) decisions.Add(new() { Key = RecruitmentEligibilityReviewPolicy.ExperienceKey,
                    Decision = request.VerifiedExperienceMonths.HasValue ? "Verified" : "Pending", VerifiedExperienceMonths = request.VerifiedExperienceMonths,
                    Note = request.VerifiedExperienceMonths.HasValue ? request.ExperienceEvidence.Trim() : "Reviewed total experience withdrawn; use source resume evidence." });
                foreach (var decision in decisions)
                {
                    decision.Requirement = Truncate(context.Items.FirstOrDefault(x => x.Key == decision.Key)?.Requirement ?? "Verified total experience", 180);
                    decision.ReviewerUserId = user.Id; decision.ReviewerName = Truncate(user.DisplayName.Length > 0 ? user.DisplayName : user.Email, 100);
                    decision.ReviewedAtUtc = DateTime.UtcNow; decision.Note = (decision.Note ?? "").Trim();
                    if (decision.Key != RecruitmentEligibilityReviewPolicy.ExperienceKey) decision.VerifiedExperienceMonths = null;
                }
                var jsonRows = decisions.Select(decision => (Decision: decision, Json: JsonSerializer.Serialize(decision, ReviewJson))).ToList();
                if (jsonRows.Any(x => x.Json.Length > 1000)) return (null, "Review evidence is too long. Shorten the supporting note.");
                await using var tx = await db.BeginTransactionAsync(cancellationToken);
                foreach (var item in jsonRows)
                    await db.ExecuteAsync(@"INSERT INTO recruitment_application_score_evidence
(ApplicationScoreId,CriterionCode,EvidenceType,ExpectedValue,ActualValue,MatchStatus,Confidence)
VALUES (@ScoreId,'eligibilityReview',@Type,@Expected,@Actual,@Status,1)", new { ScoreId = context.ScoreId,
                        Type = RecruitmentEligibilityReviewPolicy.EvidenceType, Expected = context.RequirementsHash + "|" + item.Decision.Key,
                        Actual = item.Json, Status = item.Decision.Decision }, tx);
                var app = await db.QuerySingleAsync<RecruitmentCandidateApplication>("SELECT * FROM recruitment_candidate_applications WHERE Id=@Id", new { Id = applicationId }, tx);
                if (jsonRows.Count > 0) await WriteActivityAsync(db, app.ClientId, app.CandidateId, null, "RECRUITMENT", "ATS_ELIGIBILITY_REVIEWED",
                    decisions.Any(x => x.Decision == "Waived") ? "Eligibility exception approved" : "Eligibility evidence reviewed", $"{jsonRows.Count} reviewed item(s), {decisions.Count(x => x.Decision == "Waived")} explicit exception(s) saved for resume {context.ResumeId}. Candidate facts and pipeline stage preserved.",
                    "RecruitmentApplication", app.Id.ToString(), user, transaction: tx);
                await tx.CommitAsync(cancellationToken);
            }
            await db.ExecuteAsync(@"UPDATE recruitment_ats_scoring_jobs SET Status='Cancelled',LastError='Replaced by recruiter evidence review and ATS recalculation.',UpdatedAt=UTC_TIMESTAMP()
WHERE ApplicationId=@Id AND Status IN ('Queued','Retry','Processing')", new { Id = applicationId });
            using var observation = engineMonitor.Observe("ats-scoring", new EngineActivityContext("Recalculate ATS after evidence review", "Recruitment application", applicationId.ToString(), ApplicationId: applicationId));
            var scoringError = "";
            try
            {
                var (score, error) = await ScoreApplicationWithResumeLockAsync(db, applicationId, user, true, observation);
                scoringError = error; observation.Succeeded = score is not null;
            }
            catch (Exception exception)
            {
                logger.LogWarning("Eligibility evidence was retained but ATS failed for application {Id} ({Type}).", applicationId, exception.GetType().Name);
                scoringError = "ATS could not finish. Your saved review is retained; refresh the checklist before retrying scoring.";
            }
            var updated = await EligibilityReviewContextAsync(db, applicationId, user);
            return (updated, scoringError);
        }
        finally
        {
            try { await RecruitmentApplicationResumeGuard.ReleaseAsync(db, applicationId); }
            catch (Exception exception) { logger.LogWarning("Eligibility review lock release failed ({Type}); its dedicated session will close.", exception.GetType().Name); }
        }
    }

    private sealed class ManualEligibilityEvidence
    {
        public long ApplicationId { get; set; }
        public long ResumeId { get; set; }
        public string ExpectedValue { get; set; } = "";
        public string ActualValue { get; set; } = "";
    }
    private sealed class ReviewResumeFacts
    {
        public long ResumeId { get; set; }
        public Guid? AttachmentPublicId { get; set; }
        public int? ExperienceMonths { get; set; }
        public long? JobDescriptionVersionId { get; set; }
    }
}
