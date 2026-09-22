using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

// Server-owned aliases only. Counts represent distinct applications, not interview rounds.
internal static class RecruitmentHiringProgress
{
    internal class Candidate
    {
        public long ApplicationId { get; set; }
        public long PositionId { get; set; }
        public int ClientId { get; set; }
        public long? JobPostingId { get; set; }
        public string JobLocation { get; set; } = "";
        public string CandidateStageName { get; set; } = "";
        public string CandidateStageType { get; set; } = "";
        public bool HasInterview { get; set; }
        public bool HasCompletedInterviewDecision { get; set; }
        public string LatestOfferStatus { get; set; } = "";
        public string LatestInterviewResult { get; set; } = "";
        public bool IsJoined { get; set; }
    }

    internal static async Task<List<Candidate>> ReadCandidatesAsync(MySqlConnection db, IEnumerable<long> positionIds, AuthUser user)
    {
        var ids = positionIds.Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0) return [];
        var rows = await db.QueryAsync<Candidate>(@"SELECT a.Id ApplicationId,a.PositionId,a.ClientId,a.JobPostingId,p.JobLocation,
COALESCE(stage.StageName,a.CurrentStage,'') CandidateStageName,COALESCE(stage.StageType,'') CandidateStageType,
COALESCE(CASE WHEN interview.Status='No Show' THEN 'No Show' ELSE interview.Result END,'') LatestInterviewResult,
COALESCE(interview.Status IN ('Scheduled','Rescheduled','Completed'),FALSE) HasInterview,
COALESCE(interview.Status='Completed' AND interview.Result='Selected',FALSE) HasCompletedInterviewDecision,
COALESCE(offerRow.Status,'') LatestOfferStatus,
(a.JoinedEmployeeId IS NOT NULL OR LOWER(a.CurrentStage) IN ('joined','joined / hired')) IsJoined
FROM recruitment_candidate_applications a
JOIN recruitment_open_positions p ON p.Id=a.PositionId AND p.ClientId=a.ClientId
LEFT JOIN recruitment_application_pipeline_instances pipeline ON pipeline.ApplicationId=a.Id
LEFT JOIN recruitment_application_stage_instances instance ON instance.Id=pipeline.CurrentStageInstanceId
LEFT JOIN recruitment_pipeline_stages stage ON stage.Id=instance.PipelineStageId
LEFT JOIN recruitment_interviews interview ON interview.Id=(SELECT MAX(i.Id) FROM recruitment_interviews i WHERE i.ApplicationId=a.Id)
LEFT JOIN recruitment_offers offerRow ON offerRow.Id=(SELECT o.Id FROM recruitment_offers o WHERE o.ApplicationId=a.Id ORDER BY o.UpdatedAt DESC,o.Id DESC LIMIT 1)
WHERE a.PositionId IN @Ids AND a.ApplicationType='Application' AND (@ClientId IS NULL OR a.ClientId=@ClientId)
ORDER BY a.Id", new { Ids = ids, user.ClientId });
        return await RecruitmentAccessScope.FilterAsync(db, user, rows, row => row.ClientId, row => row.JobLocation);
    }

    internal static bool Excluded(Candidate row) => !row.IsJoined && (
        new[] { "Rejected", "No Show" }.Contains(row.LatestInterviewResult, StringComparer.OrdinalIgnoreCase)
        || new[] { "Rejected", "Withdrawn", "Expired" }.Contains(row.LatestOfferStatus, StringComparer.OrdinalIgnoreCase)
        || $"{row.CandidateStageType} {row.CandidateStageName}".Contains("reject", StringComparison.OrdinalIgnoreCase)
        || $"{row.CandidateStageType} {row.CandidateStageName}".Contains("withdraw", StringComparison.OrdinalIgnoreCase));

    internal static bool ProfilesReady(Candidate row) => !Excluded(row) && (row.HasInterview || row.IsJoined
        || new[] { "Interview", "HR", "Offer", "Documents", "PreOnboarding", "Joining", "Completed" }.Contains(row.CandidateStageType, StringComparer.OrdinalIgnoreCase)
        || row.CandidateStageName.Contains("shortlisted", StringComparison.OrdinalIgnoreCase)
        || row.CandidateStageName.Contains("sharing", StringComparison.OrdinalIgnoreCase));

    internal static bool InterviewsReady(Candidate row) => !Excluded(row) && (row.HasInterview || row.IsJoined);

    // A selected interview result is still pending while the candidate is in Selection & HR Review.
    internal static bool ReviewComplete(Candidate row) => !Excluded(row) && (row.IsJoined
        || (!row.CandidateStageType.Equals("HR", StringComparison.OrdinalIgnoreCase)
            && !row.CandidateStageName.Contains("HR Review", StringComparison.OrdinalIgnoreCase)
            && (row.HasCompletedInterviewDecision || new[] { "Offer", "Documents", "PreOnboarding", "Joining", "Completed" }.Contains(row.CandidateStageType, StringComparer.OrdinalIgnoreCase))));

    internal static bool OfferAt(Candidate row, params string[] statuses) => !Excluded(row)
        && (row.IsJoined || statuses.Contains(row.LatestOfferStatus, StringComparer.OrdinalIgnoreCase));

    // Signing precedes negotiation. An offer/negotiation result must never be
    // required to enter MoM, and a later-stage label alone is not interview evidence.
    internal static bool MoMReady(Candidate row) => !Excluded(row) && row.HasCompletedInterviewDecision;

    internal static bool Enough(int required, IEnumerable<Candidate> candidates, Func<Candidate, bool> qualifies) =>
        required > 0 && candidates.Count(qualifies) >= required;

    // Requirements for occupying a position stage, also used when choosing a rollback target.
    internal static Func<Candidate, bool>? EntryGate(string code, string name, string type)
    {
        var key = $"{code} {name} {type}".ToUpperInvariant();
        if (key.Contains("REJECT") || key.Contains("WITHDRAW")) return _ => false;
        if (key.Contains("JOIN") || type.Equals("Completed", StringComparison.OrdinalIgnoreCase)) return row => OfferAt(row, "Accepted");
        if (key.Contains("OFFER") && !key.Contains("NEGOTIATION")) return row => OfferAt(row, "Approved", "Pending Candidate", "Released", "Accepted");
        if (key.Contains("HR") && key.Contains("APPROVAL")) return row => OfferAt(row, "Pending Approval", "Approved", "Pending Candidate", "Released", "Accepted");
        if (key.Contains("MOM") && !key.Contains("NEGOTIATION")) return row => MoMReady(row) && ReviewComplete(row);
        if (key.Contains("NEGOTIATION")) return ReviewComplete;
        if ((key.Contains("INTERVIEW") || key.Contains("PANEL")) && !key.Contains("SHARING")) return InterviewsReady;
        if (key.Contains("SHARING") || key.Contains("SHORTLIST")) return ProfilesReady;
        return null;
    }

    internal static RecruitmentHiringProgressView Describe(int required, IEnumerable<Candidate> source, string stage)
    {
        var rows = source.ToList();
        var key = stage.ToUpperInvariant();
        Func<Candidate, bool> gate = ProfilesReady;
        var action = "shortlisted candidate(s)";
        if (key.Contains("SHARING")) { gate = InterviewsReady; action = "candidate(s) with an interview scheduled"; }
        else if (key.Contains("INTERVIEW") || key.Contains("PANEL")) { gate = ReviewComplete; action = "candidate(s) to complete interview selection and HR review"; }
        else if (key.Contains("NEGOTIATION")) { gate = row => OfferAt(row, "Pending Approval", "Approved", "Pending Candidate", "Released", "Accepted"); action = "candidate negotiations submitted to HR"; }
        else if (key.Contains("APPROVAL") && key.Contains("HR")) { gate = row => OfferAt(row, "Approved", "Pending Candidate", "Released", "Accepted"); action = "candidate offers approved by HR"; }
        else if (key.Contains("OFFER")) { gate = row => OfferAt(row, "Accepted"); action = "accepted offers"; }
        var count = rows.Count(gate);
        var pending = required <= 0 ? "No active vacancies."
            : key.Contains("SIGNING") && key.Contains("MOM") ? "Next: complete the required committee MoM signatures."
            : key.Contains("JOIN") ? "Joining dates conveyed; candidate joining continues in each candidate journey."
            : count < required ? $"Next: {Math.Min(count, required)}/{required} ready; need {required - count} more {action}."
            : $"Candidate requirement met ({required}/{required}); next movement follows the configured documents and approvals.";
        return new() { RequiredCount = required, PendingReason = pending,
            CandidateStages = rows.GroupBy(row => string.IsNullOrWhiteSpace(row.CandidateStageName) ? "Not started" : row.CandidateStageName)
                .Select(group => new RecruitmentCandidateStageCount(group.Key, group.Count())).OrderBy(row => row.Stage).ToList() };
    }

    internal sealed class PositionStage
    {
        public long PositionId { get; set; }
        public long PipelineVersionId { get; set; }
        public long Id { get; set; }
        public string StageCode { get; set; } = "";
        public string StageName { get; set; } = "";
        public string StageType { get; set; } = "";
        public int DisplayOrder { get; set; }
    }

    internal static async Task<List<PositionStage>> ReadAssignedStagesAsync(MySqlConnection db, IEnumerable<long> positionIds, AuthUser user)
    {
        var ids = positionIds.Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0) return [];
        return (await db.QueryAsync<PositionStage>(@"SELECT p.Id PositionId,stage.*
FROM recruitment_open_positions p
JOIN recruitment_position_pipeline_assignments assignment ON assignment.Id=(
 SELECT a.Id FROM recruitment_position_pipeline_assignments a
 JOIN recruitment_pipeline_versions v ON v.Id=a.PipelineVersionId AND v.ScopeType IN ('Position','Hybrid')
 WHERE a.PositionId=p.Id AND a.IsActive=TRUE AND a.JobPostingId IS NULL ORDER BY a.AssignedAtUtc DESC,a.Id DESC LIMIT 1)
JOIN recruitment_pipeline_stages stage ON stage.PipelineVersionId=assignment.PipelineVersionId AND stage.IsActive=TRUE AND stage.CardScope='Position'
WHERE p.Id IN @Ids AND (@ClientId IS NULL OR p.ClientId=@ClientId)
ORDER BY p.Id,stage.DisplayOrder,stage.Id", new { Ids = ids, user.ClientId })).ToList();
    }

    // For older direct requests without a runtime case, show the supported stage without
    // inventing completed documents or writing records during a dashboard read.
    internal static PositionStage? SupportedInitialStage(IEnumerable<PositionStage> stages, int required, IReadOnlyList<Candidate> candidates)
    {
        PositionStage? supported = null;
        foreach (var stage in stages.OrderBy(row => row.DisplayOrder))
        {
            var gate = EntryGate(stage.StageCode, stage.StageName, stage.StageType);
            if (gate is not null && !Enough(required, candidates, gate)) break;
            if (supported is not null && gate is null) break; // Unknown/custom stages need their configured workflow.
            supported = stage;
            if (stage.StageName.Contains("MOM", StringComparison.OrdinalIgnoreCase)) break;
        }
        return supported;
    }

    internal static PositionStage? SupportedRollbackStage(IEnumerable<PositionStage> source, int required, IReadOnlyList<Candidate> candidates)
    {
        var stages = source.OrderByDescending(stage => stage.DisplayOrder).ToList();
        return stages.FirstOrDefault(stage => EntryGate(stage.StageCode, stage.StageName, stage.StageType) is { } gate && Enough(required, candidates, gate))
            ?? stages.FirstOrDefault(stage => stage.StageCode is "SHARING_PROFILES" or "PROFILE_SHARING"
                || (stage.StageName.Contains("sharing", StringComparison.OrdinalIgnoreCase) && stage.StageName.Contains("profile", StringComparison.OrdinalIgnoreCase)));
    }

    internal static async Task EnrichPositionsAsync(MySqlConnection db, List<RecruitmentOpenPosition> positions, AuthUser user)
    {
        var ids = positions.Select(row => row.Id).ToArray();
        var candidates = await ReadCandidatesAsync(db, ids, user);
        var stages = await ReadAssignedStagesAsync(db, positions.Where(row => row.PipelineInstanceId is null).Select(row => row.Id), user);
        foreach (var position in positions)
        {
            var rows = candidates.Where(row => row.PositionId == position.Id).ToList();
            if (position.PipelineInstanceId is null && SupportedInitialStage(stages.Where(row => row.PositionId == position.Id), position.RequiredCandidateCount, rows) is { } stage)
            {
                position.PipelineVersionId = stage.PipelineVersionId;
                position.PipelineStageId = stage.Id;
                position.PipelineStageName = stage.StageName;
                position.PipelineStageType = stage.StageType;
            }
            position.HiringProgress = Describe(position.RequiredCandidateCount, rows, position.PipelineStageName);
        }
    }

    internal static string SelectFor(string positionAlias) => $@"
GREATEST(0,COALESCE({positionAlias}.NumberOfPositions,0)-COALESCE({positionAlias}.CancelledPositions,0)-COALESCE({positionAlias}.OnHoldPositions,0)) RequiredCandidateCount,
(SELECT COUNT(*) FROM recruitment_candidate_applications progressApplication
 JOIN recruitment_interviews latestInterview ON latestInterview.Id=(SELECT MAX(roundRow.Id)
 FROM recruitment_interviews roundRow WHERE roundRow.ApplicationId=progressApplication.Id)
 WHERE progressApplication.PositionId={positionAlias}.Id AND progressApplication.ApplicationType='Application'
 AND latestInterview.Status IN ('Scheduled','Rescheduled')
 AND progressApplication.CurrentStage NOT IN ('Rejected','Withdrawn','Joined','Offer Rejected')) InPanelCount,
(SELECT COUNT(*) FROM recruitment_candidate_applications progressApplication
 JOIN recruitment_interviews latestInterview ON latestInterview.Id=(SELECT MAX(roundRow.Id)
 FROM recruitment_interviews roundRow WHERE roundRow.ApplicationId=progressApplication.Id)
 WHERE progressApplication.PositionId={positionAlias}.Id AND progressApplication.ApplicationType='Application'
 AND latestInterview.Status='Completed' AND latestInterview.Result='Selected'
 AND progressApplication.CurrentStage NOT IN ('Rejected','Withdrawn','Offer Rejected')
 AND COALESCE((SELECT latestOffer.Status FROM recruitment_offers latestOffer WHERE latestOffer.ApplicationId=progressApplication.Id
 ORDER BY latestOffer.UpdatedAt DESC,latestOffer.Id DESC LIMIT 1),'') NOT IN ('Rejected','Withdrawn','Expired')) SelectedCandidateCount,
";
}
