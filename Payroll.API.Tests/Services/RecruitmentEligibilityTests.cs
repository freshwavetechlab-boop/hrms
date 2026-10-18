using Payroll.API.Models;

namespace Payroll.API.Tests.Services;

public class RecruitmentEligibilityTests
{
    [Theory]
    [InlineData("20 years", 240)]
    [InlineData("5+ years", 60)]
    [InlineData("10+ years", 120)]
    [InlineData("3–5 years", 36)]
    [InlineData("3 to 5 years", 36)]
    [InlineData("Minimum 2.5 years", 30)]
    [InlineData("18 months", 18)]
    [InlineData("5 years 6 months", 66)]
    [InlineData("Fresher", 0)]
    [InlineData("", 0)]
    public void ReadsMinimumExperienceWithCorrectUnits(string requirement, int minimum) =>
        Assert.Equal(minimum, RecruitmentEligibility.MinimumExperienceMonths(requirement));

    [Theory]
    [InlineData("relevant experience")]
    [InlineData("5 years or 3 years with a master's degree")]
    [InlineData("-5 years")]
    public void AmbiguousRequirementsNeedReview(string requirement) =>
        Assert.Equal("NeedsReview", RecruitmentEligibility.Assess(120, requirement, ["Matched"]).Status);

    [Fact]
    public void HighScoreCannotCompensateForInsufficientTotalExperience()
    {
        var result = RecruitmentEligibility.Assess(48, "10+ years", ["Matched"], "Completed");
        Assert.Equal("Ineligible", result.Status);
        Assert.Contains(result.Reasons, x => x.Contains("10 years") && x.Contains("4 years"));
        Assert.Equal("Ineligible", RecruitmentAtsDomainRules.EffectiveScoreStatus(99, 60, result.Status));
    }

    [Theory]
    [InlineData(null, "NeedsReview")]
    [InlineData(0, "Ineligible")]
    [InlineData(59, "Ineligible")]
    [InlineData(60, "Eligible")]
    [InlineData(300, "Eligible")]
    public void DistinguishesMissingEvidenceFromVerifiedZeroAndAcceptsTheExactBoundary(int? months, string expected) =>
        Assert.Equal(expected, RecruitmentEligibility.Assess(months, "5+ years", ["Matched"]).Status);

    [Fact]
    public void UnverifiedRequiredSkillsNeverBecomeEligibleFromTheWeightedScore()
    {
        Assert.Equal("NeedsReview", RecruitmentEligibility.Assess(240, "10+ years", ["NeedsReview"]).Status);
        Assert.Equal("NeedsReview", RecruitmentEligibility.Assess(240, "10+ years", ["Missing"]).Status);
        Assert.Equal("NeedsReview", RecruitmentEligibility.Assess(240, "10+ years", ["Missing"], "Ineligible").Status);
        Assert.Equal("Ineligible", RecruitmentEligibility.Assess(240, "10+ years", ["InsufficientExperience"]).Status);
    }

    [Fact]
    public void ExperienceFailureIsVisibleBeforeAtsAndKnownPassRequiresAssessment()
    {
        Assert.Equal("Ineligible", RecruitmentEligibility.Assess(12, "5+ years", hasAssessment: false).Status);
        Assert.Equal("NotAssessed", RecruitmentEligibility.Assess(120, "5+ years", hasAssessment: false).Status);
        Assert.Equal("Eligible", RecruitmentEligibility.Assess(120, "5+ years", hasAssessment: true).Status);
    }

    [Fact]
    public void ExperienceFailureDoesNotInventAMissingSkill()
    {
        var result = RecruitmentEligibility.Assess(12, "5+ years", ["Matched"], "Ineligible");
        Assert.Single(result.Reasons);
        Assert.DoesNotContain("skill", result.Reasons[0]);
    }
}
