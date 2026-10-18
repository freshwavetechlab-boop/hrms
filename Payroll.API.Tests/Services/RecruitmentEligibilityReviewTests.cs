using Payroll.API.Models;

namespace Payroll.API.Tests.Services;

public class RecruitmentEligibilityReviewTests
{
    private static RecruitmentApplicationScoreSkillMatch Skill(string name, string status = "Missing", decimal years = 0) =>
        new() { SkillName = name, MatchStatus = status, MinimumYears = years };

    [Fact]
    public void MissingEvidenceNamesTheExactRequirement()
    {
        var reasons = RecruitmentEligibilityReviewPolicy.PendingReasons([Skill("Threat landscapes"), Skill("External audits")], [], new Dictionary<string, RecruitmentEligibilityReviewDecision>());
        Assert.Contains(reasons, x => x.Contains("Threat landscapes"));
        Assert.Contains(reasons, x => x.Contains("External audits"));
    }

    [Fact]
    public void ReviewingOneRequirementDoesNotApproveTheOthers()
    {
        var a = Skill("Threat landscapes"); var b = Skill("External audits");
        var reviews = new Dictionary<string, RecruitmentEligibilityReviewDecision> { [RecruitmentEligibilityReviewPolicy.SkillKey(a)] = new() { Decision = "Verified", Note = "CV project includes the required evidence." } };
        var statuses = new[] { a, b }.Select(x => RecruitmentEligibilityReviewPolicy.SkillStatus(x, reviews));
        Assert.Equal("NeedsReview", RecruitmentEligibility.Assess(144, "10+ years", statuses).Status);
        Assert.Single(RecruitmentEligibilityReviewPolicy.PendingReasons([a, b], [], reviews));
        reviews[RecruitmentEligibilityReviewPolicy.SkillKey(b)] = new() { Decision = "Verified", Note = "External audit work verified on page 2." };
        Assert.Equal("Eligible", RecruitmentEligibility.Assess(144, "10+ years", new[] { a, b }.Select(x => RecruitmentEligibilityReviewPolicy.SkillStatus(x, reviews))).Status);
    }

    [Fact]
    public void RecruiterConfirmedFailureCannotBeHiddenByAHighScore()
    {
        var skill = Skill("External audits");
        var reviews = new Dictionary<string, RecruitmentEligibilityReviewDecision> { [RecruitmentEligibilityReviewPolicy.SkillKey(skill)] = new() { Decision = "NotMet" } };
        var result = RecruitmentEligibility.Assess(144, "10+ years", [RecruitmentEligibilityReviewPolicy.SkillStatus(skill, reviews)]);
        Assert.Equal("Ineligible", result.Status);
        Assert.Equal("Ineligible", RecruitmentAtsDomainRules.EffectiveScoreStatus(99, 60, result.Status));
    }

    [Theory]
    [InlineData(96, "Ineligible")]
    [InlineData(120, "Eligible")]
    [InlineData(144, "Eligible")]
    public void VerifiedExperienceStillHasToMeetTheConfiguredMinimum(int months, string expected)
    {
        var reviews = new Dictionary<string, RecruitmentEligibilityReviewDecision> { [RecruitmentEligibilityReviewPolicy.ExperienceKey] = new() { Decision = "Verified", VerifiedExperienceMonths = months } };
        Assert.Equal(expected, RecruitmentEligibility.Assess(RecruitmentEligibilityReviewPolicy.Experience(reviews, 114), "10+ years", ["Matched"]).Status);
    }

    [Fact]
    public void KnownSkillDurationFailureCannotBeWaivedByAnApproval()
    {
        var skill = Skill("SOC", "InsufficientExperience", 5);
        var reviews = new Dictionary<string, RecruitmentEligibilityReviewDecision> { [RecruitmentEligibilityReviewPolicy.SkillKey(skill)] = new() { Decision = "Verified" } };
        Assert.Equal("InsufficientExperience", RecruitmentEligibilityReviewPolicy.SkillStatus(skill, reviews));
    }

    [Fact]
    public void ReviewSurvivesRescoringButNotAResumeOrRequirementChange()
    {
        var a = Skill("SOC"); var b = Skill("Audits");
        var hash = RecruitmentEligibilityReviewPolicy.ContextHash(100, "JD snapshot A", [a, b]);
        a.MatchStatus = "Matched"; a.Id = 999; a.ApplicationScoreId = 123;
        Assert.Equal(hash, RecruitmentEligibilityReviewPolicy.ContextHash(100, "JD snapshot A", [b, a]));
        Assert.NotEqual(hash, RecruitmentEligibilityReviewPolicy.ContextHash(101, "JD snapshot A", [a, b]));
        Assert.NotEqual(hash, RecruitmentEligibilityReviewPolicy.ContextHash(100, "JD snapshot B", [a, b]));
        var oldKey = RecruitmentEligibilityReviewPolicy.SkillKey(a); a.MinimumYears = 5;
        Assert.NotEqual(oldKey, RecruitmentEligibilityReviewPolicy.SkillKey(a));
        Assert.NotEqual(hash, RecruitmentEligibilityReviewPolicy.ContextHash(100, "JD snapshot A", [a, b]));
    }

    [Fact]
    public void PendingOrExpiredHumanEvidenceNeverRemainsAnAutomaticMatch()
    {
        var skill = Skill("SOC", "Matched"); skill.MatchMethod = RecruitmentEligibilityReviewPolicy.EvidenceType;
        Assert.Equal("NeedsReview", RecruitmentEligibilityReviewPolicy.SkillStatus(skill, new Dictionary<string, RecruitmentEligibilityReviewDecision>()));
    }

    [Fact]
    public void SupportingNotesAndAReviewableRequirementAreRequired()
    {
        var decision = new RecruitmentEligibilityReviewDecision { Decision = "Verified", Note = "yes" };
        Assert.NotEmpty(RecruitmentEligibilityReviewPolicy.ValidateDecision(decision, new() { CanReview = true }));
        decision.Note = "Verified the supporting dates and role on page 2.";
        Assert.Empty(RecruitmentEligibilityReviewPolicy.ValidateDecision(decision, new() { CanReview = true }));
        Assert.NotEmpty(RecruitmentEligibilityReviewPolicy.ValidateDecision(decision, new() { CanReview = false }));
        Assert.NotEmpty(RecruitmentEligibilityReviewPolicy.ValidateDecision(decision, null));
    }

    [Fact]
    public void QualificationReviewDoesNotApproveAnUnrelatedCredential()
    {
        const string qualification = "Mandatory qualification LLB needs verification.";
        const string certification = "Mandatory certification needs verification.";
        var reviews = new Dictionary<string, RecruitmentEligibilityReviewDecision>
        { [RecruitmentEligibilityReviewPolicy.RequirementKey("requirement", qualification)] = new() { Decision = "Verified" } };
        Assert.Equal([certification], RecruitmentEligibilityReviewPolicy.PendingReasons([], [qualification, certification], reviews));
    }
}
