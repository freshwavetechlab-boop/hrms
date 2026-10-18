using Dapper;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public sealed partial class RecruitmentTalentRepository
{
    internal static async Task<Dictionary<long, RecruitmentCandidateApplication>> ApplicationEligibilityByIdAsync(MySqlConnection db, long[] ids)
    {
        if (ids.Length == 0) return [];
        var rows = (await db.QueryAsync<RecruitmentCandidateApplication>(@"SELECT applicationRow.Id,
COALESCE(facts.TotalExperienceMonths,NULLIF(candidate.TotalExperienceMonths,0)) CandidateExperienceMonths,
positionRow.ExperienceRange RequiredExperienceRange,COALESCE(score.OverrideScore,score.TotalScore) AtsScore,
COALESCE(score.ScoreStatus,'Not Scored') ScoreStatus
FROM recruitment_candidate_applications applicationRow
JOIN recruitment_candidates candidate ON candidate.Id=applicationRow.CandidateId
JOIN recruitment_open_positions positionRow ON positionRow.Id=applicationRow.PositionId
LEFT JOIN recruitment_candidate_resumes resume ON resume.Id=applicationRow.ResumeId AND resume.CandidateId=candidate.Id
LEFT JOIN recruitment_resume_parse_facts facts ON facts.ResumeId=resume.Id
LEFT JOIN recruitment_application_scores score ON score.ApplicationId=applicationRow.Id AND score.IsCurrent=TRUE
 AND (applicationRow.ResumeId IS NULL OR score.ResumeId=applicationRow.ResumeId)
WHERE applicationRow.Id IN @Ids", new { Ids = ids })).ToList();
        await ApplyApplicationEligibilityAsync(db, rows);
        return rows.ToDictionary(row => row.Id);
    }

    internal static async Task ApplyBoardEligibilityAsync(MySqlConnection db, IReadOnlyCollection<RecruitmentPipelineBoardCard> cards)
    {
        var assessments = await ApplicationEligibilityByIdAsync(db, cards.Select(x => x.ApplicationId).Distinct().ToArray());
        foreach (var card in cards)
        {
            if (!assessments.TryGetValue(card.ApplicationId, out var row)) continue;
            card.AtsScore = row.AtsScore;
            card.EligibilityStatus = row.EligibilityStatus;
            card.EligibilityReasons = row.EligibilityReasons;
        }
    }

    private static async Task ApplyApplicationEligibilityAsync(MySqlConnection db, IReadOnlyCollection<RecruitmentCandidateApplication> rows)
    {
        if (rows.Count == 0) return;
        var reviewReasons = await MandatoryApplicationReviewAsync(db, rows.Select(x => x.Id).ToArray());
        var evidence = (await db.QueryAsync<ApplicationEligibilitySkill>(@"SELECT score.ApplicationId,skill.*
FROM recruitment_application_scores score
JOIN recruitment_candidate_applications applicationRow ON applicationRow.Id=score.ApplicationId AND applicationRow.ResumeId=score.ResumeId
JOIN recruitment_application_score_skill_matches skill ON skill.ApplicationScoreId=score.Id AND skill.SkillType='Required'
WHERE score.IsCurrent=TRUE AND score.ApplicationId IN @Ids", new { Ids = rows.Select(x => x.Id).ToArray() }))
            .GroupBy(x => x.ApplicationId).ToDictionary(x => x.Key, x => x.Cast<RecruitmentApplicationScoreSkillMatch>().ToArray());
        var scoreIdentities = (await db.QueryAsync<ReviewScoreIdentity>(@"SELECT score.ApplicationId,score.ResumeId,score.PositionSnapshotHash,
(snapshot.JobDescriptionVersionId <=> COALESCE(posting.JobDescriptionVersionId,positionRow.ApprovedJobDescriptionVersionId)
 AND snapshot.ExperienceRange=positionRow.ExperienceRange) SnapshotStillCurrent
FROM recruitment_application_scores score JOIN recruitment_candidate_applications applicationRow ON applicationRow.Id=score.ApplicationId AND applicationRow.ResumeId=score.ResumeId
JOIN recruitment_open_positions positionRow ON positionRow.Id=applicationRow.PositionId
LEFT JOIN recruitment_job_postings posting ON posting.Id=applicationRow.JobPostingId AND posting.PositionId=applicationRow.PositionId
LEFT JOIN recruitment_application_score_position_snapshots snapshot ON snapshot.ApplicationScoreId=score.Id
WHERE score.IsCurrent=TRUE AND score.ApplicationId IN @Ids", new { Ids = rows.Select(x => x.Id).ToArray() })).ToDictionary(x => x.ApplicationId);
        var manualEvidence = await ReadManualEligibilityEvidenceAsync(db, rows.Select(x => x.Id).ToArray());
        foreach (var row in rows)
        {
            var skills = evidence.GetValueOrDefault(row.Id) ?? [];
            var identity = scoreIdentities.GetValueOrDefault(row.Id);
            var hash = identity is null ? "" : RecruitmentEligibilityReviewPolicy.ContextHash(identity.ResumeId, identity.PositionSnapshotHash, skills);
            var reviews = identity?.SnapshotStillCurrent == true ? ValidManualReviews(manualEvidence, row.Id, identity.ResumeId, hash) : [];
            var mandatory = reviewReasons.GetValueOrDefault(row.Id) ?? [];
            var statuses = skills.Select(skill => RecruitmentEligibilityReviewPolicy.SkillStatus(skill, reviews)).ToList();
            if (mandatory.Any(reason => reviews.GetValueOrDefault(RecruitmentEligibilityReviewPolicy.RequirementKey("requirement", reason))?.Decision == "NotMet")) statuses.Add(RecruitmentEligibilityReviewPolicy.ConfirmedNotMet);
            var pending = RecruitmentEligibilityReviewPolicy.PendingReasons(skills, mandatory, reviews).ToList();
            if (identity?.SnapshotStillCurrent == false) pending.Add("The job description changed. Refresh ATS before reviewing its requirements.");
            row.CandidateExperienceMonths = RecruitmentEligibilityReviewPolicy.Experience(reviews, row.CandidateExperienceMonths);
            var assessment = RecruitmentEligibility.Assess(row.CandidateExperienceMonths, row.RequiredExperienceRange,
                statuses, skills.Length > 0 || mandatory.Count > 0 || reviews.Count > 0 ? "" : row.ScoreStatus, row.AtsScore.HasValue, pending);
            row.EligibilityStatus = assessment.Status;
            row.EligibilityReasons = assessment.Reasons.Where(reason => reason != "Mandatory eligibility evidence needs recruiter verification." || pending.Count == 0).ToList();
            if (assessment.Status is "Ineligible" or "NeedsReview") row.ScoreStatus = assessment.Status;
        }
    }

    private static async Task ApplyScoreEligibilityAsync(MySqlConnection db, IReadOnlyCollection<RecruitmentApplicationScore> scores)
    {
        if (scores.Count == 0) return;
        var facts = (await db.QueryAsync<ScoreEligibilityFacts>(@"SELECT score.Id,
COALESCE(facts.TotalExperienceMonths,CASE WHEN score.IsCurrent=TRUE THEN NULLIF(candidate.TotalExperienceMonths,0) END) ExperienceMonths,
COALESCE(resume.ParsedText,'') ResumeText,CASE WHEN score.IsCurrent=TRUE THEN candidate.HighestQualification ELSE '' END QualificationEvidence
FROM recruitment_application_scores score
JOIN recruitment_candidate_applications applicationRow ON applicationRow.Id=score.ApplicationId
JOIN recruitment_candidates candidate ON candidate.Id=applicationRow.CandidateId
LEFT JOIN recruitment_candidate_resumes resume ON resume.Id=score.ResumeId AND resume.CandidateId=candidate.Id
LEFT JOIN recruitment_resume_parse_facts facts ON facts.ResumeId=resume.Id
WHERE score.Id IN @Ids", new { Ids = scores.Select(x => x.Id).ToArray() })).ToDictionary(x => x.Id);
        var reviews = await MandatoryReviewAsync(db, scores.Select(score => new MandatoryEvidenceContext
        {
            Id = score.Id, JobDescriptionVersionId = score.PositionSnapshot?.JobDescriptionVersionId,
            ResumeText = facts.GetValueOrDefault(score.Id)?.ResumeText ?? "", QualificationEvidence = facts.GetValueOrDefault(score.Id)?.QualificationEvidence ?? ""
        }).ToList());
        var manualEvidence = await ReadManualEligibilityEvidenceAsync(db, scores.Select(x => x.ApplicationId).Distinct().ToArray());
        foreach (var score in scores)
        {
            var skills = score.SkillMatches.Where(x => x.SkillType == "Required").ToArray();
            var hash = RecruitmentEligibilityReviewPolicy.ContextHash(score.ResumeId, score.PositionSnapshotHash, skills);
            var manual = ValidManualReviews(manualEvidence, score.ApplicationId, score.ResumeId, hash, score.IsCurrent ? null : score.ScoredAt);
            var mandatory = reviews.GetValueOrDefault(score.Id) ?? [];
            var statuses = skills.Select(skill => RecruitmentEligibilityReviewPolicy.SkillStatus(skill, manual)).ToList();
            if (mandatory.Any(reason => manual.GetValueOrDefault(RecruitmentEligibilityReviewPolicy.RequirementKey("requirement", reason))?.Decision == "NotMet")) statuses.Add(RecruitmentEligibilityReviewPolicy.ConfirmedNotMet);
            var pending = RecruitmentEligibilityReviewPolicy.PendingReasons(skills, mandatory, manual);
            var assessment = RecruitmentEligibility.Assess(RecruitmentEligibilityReviewPolicy.Experience(manual, facts.GetValueOrDefault(score.Id)?.ExperienceMonths),
                score.PositionSnapshot?.ExperienceRange,
                statuses, skills.Length > 0 || mandatory.Count > 0 || manual.Count > 0 ? "" : score.ScoreStatus,
                additionalReviewReasons: pending);
            score.EligibilityStatus = assessment.Status;
            score.EligibilityReasons = assessment.Reasons.Where(reason => reason != "Mandatory eligibility evidence needs recruiter verification." || pending.Count == 0).ToList();
            if (assessment.Status is "Ineligible" or "NeedsReview") score.ScoreStatus = assessment.Status;
        }
    }

    private sealed class ApplicationEligibilitySkill : RecruitmentApplicationScoreSkillMatch
    {
        public long ApplicationId { get; set; }
    }

    private sealed class ReviewScoreIdentity
    {
        public long ApplicationId { get; set; }
        public long ResumeId { get; set; }
        public string PositionSnapshotHash { get; set; } = "";
        public bool SnapshotStillCurrent { get; set; }
    }

    private sealed class ScoreEligibilityFacts
    {
        public long Id { get; set; }
        public int? ExperienceMonths { get; set; }
        public string ResumeText { get; set; } = "";
        public string QualificationEvidence { get; set; } = "";
    }
}
