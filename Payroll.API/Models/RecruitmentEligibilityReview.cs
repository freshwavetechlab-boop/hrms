using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Payroll.API.Models;

public sealed class RecruitmentEligibilityReviewDecision
{
    public string Key { get; set; } = "";
    public string Decision { get; set; } = "Pending";
    public string Note { get; set; } = "";
    public string Requirement { get; set; } = "";
    public int? VerifiedExperienceMonths { get; set; }
    public int ReviewerUserId { get; set; }
    public string ReviewerName { get; set; } = "";
    public DateTime ReviewedAtUtc { get; set; }
}

public sealed class SaveRecruitmentEligibilityReview
{
    public long ExpectedResumeId { get; set; }
    public long ExpectedScoreId { get; set; }
    public string RequirementsHash { get; set; } = "";
    public List<RecruitmentEligibilityReviewDecision> Decisions { get; set; } = [];
    public bool ExperienceChanged { get; set; }
    public int? VerifiedExperienceMonths { get; set; }
    public string ExperienceEvidence { get; set; } = "";
    public bool RunAtsOnly { get; set; }
}

public sealed class RecruitmentEligibilityReviewItem
{
    public string Key { get; set; } = "";
    public string Requirement { get; set; } = "";
    public string Status { get; set; } = "NeedsReview";
    public string Evidence { get; set; } = "";
    public bool CanReview { get; set; } = true;
    public bool CanWaive { get; set; }
    public RecruitmentEligibilityReviewDecision? Review { get; set; }
}

public sealed class RecruitmentEligibilityReviewContext
{
    public long ApplicationId { get; set; }
    public string ApplicationCode { get; set; } = "";
    public string CandidateName { get; set; } = "";
    public long ResumeId { get; set; }
    public Guid? AttachmentPublicId { get; set; }
    public long ScoreId { get; set; }
    public string RequirementsHash { get; set; } = "";
    public string EligibilityStatus { get; set; } = "";
    public List<string> Reasons { get; set; } = [];
    public decimal? AtsScore { get; set; }
    public int? ParsedExperienceMonths { get; set; }
    public int? VerifiedExperienceMonths { get; set; }
    public string ExperienceEvidence { get; set; } = "";
    public string RequiredExperienceRange { get; set; } = "";
    public bool ScoreIsStale { get; set; }
    public List<RecruitmentEligibilityReviewItem> Items { get; set; } = [];
}

public static class RecruitmentEligibilityReviewPolicy
{
    public const string EvidenceType = "HumanEligibilityReview";
    public const string ExperienceKey = "experience:total";
    public const string ExperienceMinimumKey = "experience:minimum";
    public const string ConfirmedNotMet = "VerifiedNotMet";

    public static bool CanApproveExceptions(AuthUser user) => user.Permissions.Any(x => x is "settings.manage" or "recruitment.manage");
    public static bool ExperienceMinimumWaived(IReadOnlyDictionary<string, RecruitmentEligibilityReviewDecision> reviews) =>
        reviews.TryGetValue(ExperienceMinimumKey, out var review) && review.Decision == "Waived";
    public static bool CanWaiveRequirement(string reason) => reason.StartsWith("Mandatory qualification", StringComparison.Ordinal)
        || reason.StartsWith("Combined qualification", StringComparison.Ordinal)
        || reason.StartsWith("Qualification mode/discipline", StringComparison.Ordinal)
        || reason.StartsWith("Mandatory certification", StringComparison.Ordinal);
    public static IReadOnlyList<string> ExceptionReasons(IReadOnlyDictionary<string, RecruitmentEligibilityReviewDecision> reviews) =>
        reviews.Values.Where(x => x.Decision == "Waived").Select(x =>
            $"Approved eligibility exception: {x.Requirement} — {x.Note} (approved by {x.ReviewerName}).").ToArray();

    public static string RequirementKey(string category, string requirement) => category + ":" + Hash(requirement.Trim().ToLowerInvariant());
    public static string SkillKey(RecruitmentApplicationScoreSkillMatch skill) => RequirementKey("skill",
        $"{skill.SkillName}|{skill.MinimumYears.ToString(System.Globalization.CultureInfo.InvariantCulture)}|{skill.MinimumProficiency}");
    public static string ContextHash(long resumeId, string snapshotHash, IEnumerable<RecruitmentApplicationScoreSkillMatch> skills) =>
        Hash(JsonSerializer.Serialize(new { ResumeId = resumeId, Snapshot = snapshotHash, Skills = skills
            .Where(x => x.SkillType == "Required").OrderBy(x => x.SkillName, StringComparer.OrdinalIgnoreCase)
            .Select(x => new { Name = x.SkillName.Trim().ToLowerInvariant(), x.MinimumYears, x.MinimumProficiency }) }));

    public static string SkillStatus(RecruitmentApplicationScoreSkillMatch skill, IReadOnlyDictionary<string, RecruitmentEligibilityReviewDecision> reviews)
    {
        var key = SkillKey(skill);
        if (reviews.TryGetValue(key, out var exception) && exception.Decision == "Waived") return RecruitmentAtsDomainRules.Matched;
        if (skill.MatchStatus == RecruitmentAtsDomainRules.InsufficientExperience) return skill.MatchStatus;
        if (reviews.TryGetValue(key, out var review))
        {
            if (review.Decision == "Verified") return RecruitmentAtsDomainRules.Matched;
            if (review.Decision == "NotMet") return ConfirmedNotMet;
        }
        return skill.MatchMethod == EvidenceType ? RecruitmentAtsDomainRules.NeedsReview : skill.MatchStatus;
    }

    public static IReadOnlyList<string> PendingReasons(IEnumerable<RecruitmentApplicationScoreSkillMatch> skills,
        IEnumerable<string> mandatoryReasons, IReadOnlyDictionary<string, RecruitmentEligibilityReviewDecision> reviews)
    {
        var reasons = new List<string>();
        foreach (var skill in skills.Where(x => x.SkillType == "Required"))
        {
            var status = SkillStatus(skill, reviews);
            if (status == RecruitmentAtsDomainRules.Matched) continue;
            var expected = skill.MinimumYears > 0 ? $" (minimum {skill.MinimumYears:0.##} years)" : "";
            reasons.Add(status == ConfirmedNotMet ? $"Recruiter confirmed mandatory skill '{skill.SkillName}' is not met."
                : status == RecruitmentAtsDomainRules.InsufficientExperience ? $"Mandatory skill '{skill.SkillName}'{expected}: the recorded duration is below the requirement."
                : $"Mandatory skill '{skill.SkillName}'{expected}: supporting evidence needs verification.");
        }
        foreach (var reason in mandatoryReasons)
        {
            if (reviews.TryGetValue(RequirementKey("requirement", reason), out var review))
            {
                if (review.Decision is "Verified" or "Waived") continue;
                if (review.Decision == "NotMet") { reasons.Add("Recruiter confirmed not met: " + reason); continue; }
            }
            reasons.Add(reason);
        }
        return reasons;
    }

    public static int? Experience(IReadOnlyDictionary<string, RecruitmentEligibilityReviewDecision> reviews, int? parsedMonths) =>
        reviews.TryGetValue(ExperienceKey, out var review) && review.Decision == "Verified" ? review.VerifiedExperienceMonths : parsedMonths;

    public static string ValidateDecision(RecruitmentEligibilityReviewDecision decision, RecruitmentEligibilityReviewItem? item, bool canApproveExceptions = false)
    {
        if (item is null || (!item.CanReview && !item.CanWaive)) return "The selected requirement cannot be approved by this review. Refresh the assessment or correct its source evidence.";
        if (decision.Decision == "Waived" && (!canApproveExceptions || !item.CanWaive)) return "An authorized recruitment administrator must approve this eligibility exception.";
        if ((decision.Decision is "Verified" or "NotMet") && !item.CanReview) return "Correct the recorded evidence or explicitly approve an eligibility exception.";
        if (decision.Decision is not ("Verified" or "NotMet" or "Pending" or "Waived")) return "Choose a valid evidence review or eligibility exception decision.";
        if ((decision.Note ?? "").Trim().Length > 400 || decision.Decision != "Pending" && (decision.Note ?? "").Trim().Length < 8)
            return "Add 8–400 characters of supporting evidence for each reviewed requirement.";
        return "";
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
