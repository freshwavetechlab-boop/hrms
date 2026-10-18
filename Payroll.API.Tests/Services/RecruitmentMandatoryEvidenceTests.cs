using Payroll.API.Models;

namespace Payroll.API.Tests.Services;

public class RecruitmentMandatoryEvidenceTests
{
    [Fact]
    public void OptionalRequirementsDoNotCreateEligibilityFailures() =>
        Assert.Empty(RecruitmentMandatoryEvidence.ReviewReasons("Career summary", "", [], []));

    [Fact]
    public void LlbAndRegularModeRequireEvidenceSeparately()
    {
        var incomplete = RecruitmentMandatoryEvidence.ReviewReasons("LL.B., University", "LLB", [("LLB", "Regular")], []);
        Assert.Single(incomplete);
        Assert.Contains("mode/discipline", incomplete[0]);
        Assert.Empty(RecruitmentMandatoryEvidence.ReviewReasons("LL.B. Regular", "LLB", [("LLB", "Regular")], []));
    }

    [Fact]
    public void MissingQualificationIsReviewNotAnInventedRejection()
    {
        var reasons = RecruitmentMandatoryEvidence.ReviewReasons("Experienced analyst", "", [("LLB", "")], []);
        Assert.Equal("NeedsReview", RecruitmentEligibility.Assess(120, "5+ years", ["Matched"], additionalReviewReasons: reasons).Status);
    }

    [Theory]
    [InlineData("CISSP certified", true)]
    [InlineData("CISM certified", true)]
    [InlineData("CCSP certified", true)]
    [InlineData("Preparing for CISSP", false)]
    [InlineData("CISSP expired", false)]
    [InlineData("Information security specialist", false)]
    public void CertificationAlternativesNeedACompletedCredential(string resume, bool established) =>
        Assert.Equal(established, RecruitmentMandatoryEvidence.ReviewReasons(resume, "", [], ["CISSP / CISM / CCSP"]).Count == 0);

    [Fact]
    public void AmbiguousMultipleMandatoryDegreesNeedHumanInterpretation() =>
        Assert.Single(RecruitmentMandatoryEvidence.ReviewReasons("BE MS", "BE", [("BE", ""), ("MS", "")], []));
}
