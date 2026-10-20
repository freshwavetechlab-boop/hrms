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

    [Fact]
    public void ExplicitMinimumExperienceExceptionPreservesActualExperienceAndDoesNotWaiveOtherRequirements()
    {
        var reviews = new Dictionary<string, RecruitmentEligibilityReviewDecision>
        {
            [RecruitmentEligibilityReviewPolicy.ExperienceMinimumKey] = new() { Decision = "Waived", Requirement = "Minimum total experience: 10 years", Note = "Client authorized considering this candidate's equivalent specialist work.", ReviewerName = "Recruitment admin" }
        };
        Assert.Equal(48, RecruitmentEligibilityReviewPolicy.Experience(reviews, 48));
        Assert.Equal("Eligible", RecruitmentEligibility.Assess(48, "10 years", ["Matched"], minimumExperienceWaived: RecruitmentEligibilityReviewPolicy.ExperienceMinimumWaived(reviews)).Status);
        Assert.Equal("NeedsReview", RecruitmentEligibility.Assess(48, "10 years", ["NeedsReview"], minimumExperienceWaived: true).Status);
        Assert.Single(RecruitmentEligibilityReviewPolicy.ExceptionReasons(reviews));
        Assert.Contains("Recruitment admin", RecruitmentEligibilityReviewPolicy.ExceptionReasons(reviews)[0]);
        reviews[RecruitmentEligibilityReviewPolicy.ExperienceMinimumKey].Decision = "Pending";
        Assert.False(RecruitmentEligibilityReviewPolicy.ExperienceMinimumWaived(reviews));
        Assert.Equal("Ineligible", RecruitmentEligibility.Assess(48, "10 years", ["Matched"]).Status);
    }

    [Fact]
    public void OnlyAnExplicitExceptionCanRelaxKnownSkillDurationAndRawEvidenceRemainsIntact()
    {
        var skill = Skill("SOC", "InsufficientExperience", 5);
        var reviews = new Dictionary<string, RecruitmentEligibilityReviewDecision>
        { [RecruitmentEligibilityReviewPolicy.SkillKey(skill)] = new() { Decision = "Waived", Note = "Client approved this skill-duration exception." } };
        Assert.Equal("Matched", RecruitmentEligibilityReviewPolicy.SkillStatus(skill, reviews));
        Assert.Equal("InsufficientExperience", skill.MatchStatus);
        Assert.Equal(5, skill.MinimumYears);
        reviews.Clear();
        Assert.Equal("InsufficientExperience", RecruitmentEligibilityReviewPolicy.SkillStatus(skill, reviews));
    }

    [Fact]
    public void CertificateExceptionAppliesToOnlyTheNamedRequirement()
    {
        const string credential = "Mandatory certification 'CISSP' needs evidence of a completed, valid credential.";
        const string degree = "Mandatory qualification 'LLB' is not established by the resume/profile.";
        var reviews = new Dictionary<string, RecruitmentEligibilityReviewDecision>
        { [RecruitmentEligibilityReviewPolicy.RequirementKey("requirement", credential)] = new() { Decision = "Waived" } };
        Assert.Equal([degree], RecruitmentEligibilityReviewPolicy.PendingReasons([], [credential, degree], reviews));
        Assert.True(RecruitmentEligibilityReviewPolicy.CanWaiveRequirement(credential));
        Assert.False(RecruitmentEligibilityReviewPolicy.CanWaiveRequirement("The approved JD contains damaged extracted text."));
    }

    [Fact]
    public void ExceptionNeedsAdministrationPermissionReasonAndAnExplicitlyWaivableItem()
    {
        var item = new RecruitmentEligibilityReviewItem { CanReview = false, CanWaive = true };
        var decision = new RecruitmentEligibilityReviewDecision { Decision = "Waived", Note = "Client authorization checked by recruiting administrator." };
        Assert.NotEmpty(RecruitmentEligibilityReviewPolicy.ValidateDecision(decision, item));
        Assert.Empty(RecruitmentEligibilityReviewPolicy.ValidateDecision(decision, item, true));
        decision.Note = "ok";
        Assert.NotEmpty(RecruitmentEligibilityReviewPolicy.ValidateDecision(decision, item, true));
        Assert.NotEmpty(RecruitmentEligibilityReviewPolicy.ValidateDecision(decision, new() { CanReview = true }, true));
        Assert.False(RecruitmentEligibilityReviewPolicy.CanApproveExceptions(new() { Permissions = ["recruitment.candidate.view"] }));
        Assert.True(RecruitmentEligibilityReviewPolicy.CanApproveExceptions(new() { Permissions = ["recruitment.manage"] }));
    }

    [Theory]
    [InlineData("Ineligible", 99, 60, false)]
    [InlineData("NeedsReview", 99, 60, false)]
    [InlineData("NotAssessed", 99, 60, false)]
    [InlineData("Eligible", 59, 60, false)]
    [InlineData("Eligible", 65, 75, false)]
    [InlineData("Eligible", 75, 75, true)]
    public void PipelineRequiresBothEligibilityAndTheConfiguredCutoff(string status, int score, int cutoff, bool expected) =>
        Assert.Equal(expected, RecruitmentEligibility.CanEnterPipeline(status, score, cutoff));
}
