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
        var evidence = (await db.QueryAsync<ApplicationEligibilitySkill>(@"SELECT score.ApplicationId,skill.MatchStatus
FROM recruitment_application_scores score
JOIN recruitment_candidate_applications applicationRow ON applicationRow.Id=score.ApplicationId AND applicationRow.ResumeId=score.ResumeId
JOIN recruitment_application_score_skill_matches skill ON skill.ApplicationScoreId=score.Id AND skill.SkillType='Required'
WHERE score.IsCurrent=TRUE AND score.ApplicationId IN @Ids", new { Ids = rows.Select(x => x.Id).ToArray() }))
            .GroupBy(x => x.ApplicationId).ToDictionary(x => x.Key, x => x.Select(y => y.MatchStatus).ToArray());
        foreach (var row in rows)
        {
            var assessment = RecruitmentEligibility.Assess(row.CandidateExperienceMonths, row.RequiredExperienceRange,
                evidence.GetValueOrDefault(row.Id), row.ScoreStatus, row.AtsScore.HasValue);
            row.EligibilityStatus = assessment.Status;
            row.EligibilityReasons = assessment.Reasons.ToList();
            if (assessment.Status is "Ineligible" or "NeedsReview") row.ScoreStatus = assessment.Status;
        }
    }

    private static async Task ApplyScoreEligibilityAsync(MySqlConnection db, IReadOnlyCollection<RecruitmentApplicationScore> scores)
    {
        if (scores.Count == 0) return;
        var facts = (await db.QueryAsync<ScoreEligibilityFacts>(@"SELECT score.Id,
COALESCE(facts.TotalExperienceMonths,CASE WHEN score.IsCurrent=TRUE THEN NULLIF(candidate.TotalExperienceMonths,0) END) ExperienceMonths
FROM recruitment_application_scores score
JOIN recruitment_candidate_applications applicationRow ON applicationRow.Id=score.ApplicationId
JOIN recruitment_candidates candidate ON candidate.Id=applicationRow.CandidateId
LEFT JOIN recruitment_candidate_resumes resume ON resume.Id=score.ResumeId AND resume.CandidateId=candidate.Id
LEFT JOIN recruitment_resume_parse_facts facts ON facts.ResumeId=resume.Id
WHERE score.Id IN @Ids", new { Ids = scores.Select(x => x.Id).ToArray() })).ToDictionary(x => x.Id);
        foreach (var score in scores)
        {
            var assessment = RecruitmentEligibility.Assess(facts.GetValueOrDefault(score.Id)?.ExperienceMonths,
                score.PositionSnapshot?.ExperienceRange,
                score.SkillMatches.Where(x => x.SkillType == "Required").Select(x => x.MatchStatus), score.ScoreStatus);
            score.EligibilityStatus = assessment.Status;
            score.EligibilityReasons = assessment.Reasons.ToList();
            if (assessment.Status is "Ineligible" or "NeedsReview") score.ScoreStatus = assessment.Status;
        }
    }

    private sealed class ApplicationEligibilitySkill
    {
        public long ApplicationId { get; set; }
        public string MatchStatus { get; set; } = "";
    }

    private sealed class ScoreEligibilityFacts
    {
        public long Id { get; set; }
        public int? ExperienceMonths { get; set; }
    }
}
