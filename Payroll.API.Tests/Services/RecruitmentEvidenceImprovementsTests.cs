using System.Text;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class RecruitmentEvidenceImprovementsTests
{
    [Theory]
    [InlineData("PROFESSIONAL EXPERIENCE (5.8 Years)", 70)]
    [InlineData("Advocate with 4.5 years of hands-on experience in litigation.", 54)]
    [InlineData("Senior executive with 20+ years of progressive experience.", 240)]
    [InlineData("Technology leader with 30+ years building and scaling cloud platforms.", 360)]
    [InlineData("Work Ex. 20+ Years", 240)]
    [InlineData("Legal professional with 9 years of post-LL.B. legal experience and 16 years of overall professional experience.", 192)]
    [InlineData("Date of birth 1990. Age: 36 years. Completed a 3 year degree.", null)]
    public async Task CareerClaimsAreReadWithoutConfusingDegreeLengthAndAge(string summary, int? expected)
    {
        var text = "SYNTHETIC CANDIDATE\nsynthetic@example.test\nSUMMARY\n" + summary + "\nSKILLS\nLegal drafting\nEDUCATION\nLL.B. Regular";
        var bytes = Encoding.UTF8.GetBytes(text);
        var parser = new ResumeParsingService(null!, null!, NullLogger<ResumeParsingService>.Instance);
        var parsed = await parser.ParseAsync(new MemoryStream(bytes), "synthetic.txt", bytes.Length, CancellationToken.None);
        Assert.Equal(expected, parsed.Facts.TotalExperienceMonths);
        Assert.Contains("LL.B.", parsed.Facts.HighestQualification);
    }

    [Fact]
    public async Task OrdinaryMeAndBeAreNotEducationalQualifications()
    {
        var bytes = Encoding.UTF8.GetBytes("SYNTHETIC CANDIDATE\nsynthetic@example.test\nSUMMARY\nHelp me be more effective in legal work.\nHOBBIES\nRegular workouts help me stay focused.");
        var parser = new ResumeParsingService(null!, null!, NullLogger<ResumeParsingService>.Instance);
        var parsed = await parser.ParseAsync(new MemoryStream(bytes), "synthetic.txt", bytes.Length, CancellationToken.None);
        Assert.Empty(parsed.Facts.Education);
        Assert.Empty(parsed.Facts.HighestQualification);
    }

    [Theory]
    [InlineData("MCA - Computer Science", "BE / B.Tech / M.Tech / MCA", true)]
    [InlineData("B.Tech - Information Technology", "BE / B.Tech / M.Tech / MCA", true)]
    [InlineData("B.Com - Accounting", "BE / B.Tech / M.Tech / MCA", false)]
    [InlineData("I want to be a leader and help me grow.", "BE / ME", false)]
    [InlineData("B.Tech in Computer Science", "B.Tech AND M.Tech", false)]
    public void ExplicitDegreeAlternativesHaveBoundaries(string resume, string required, bool met)
    {
        var reasons = RecruitmentMandatoryEvidence.ReviewReasons(resume, "", [(required, "")], []);
        Assert.Equal(met, reasons.Count == 0);
    }

    [Theory]
    [InlineData("LL.B. Regular", true)]
    [InlineData("LL.B. Full-time", true)]
    [InlineData("LL.B.\n" + "HOBBIES\nRegular workouts help me focus.", false)]
    [InlineData("LL.B. distance learning", false)]
    public void EducationModeMustBeSupportedNearTheDegree(string resume, bool met) =>
        Assert.Equal(met, RecruitmentMandatoryEvidence.ReviewReasons(resume, "", [("LLB", "Regular")], []).Count == 0);

    [Theory]
    [InlineData("At least 3 years of hands-on experience with Ceph in production.", "Ceph", 3)]
    [InlineData("Minimum 10 years in IT, with at least 5 years in a dedicated storage / SDS architecture role.", "Software Defined Storage (SDS)", 5)]
    [InlineData("At least 3 years of hands-on experience with Ceph in production.", "Linux", 0)]
    [InlineData("Preferred: at least 3 years of experience with Ceph.", "Ceph", 0)]
    [InlineData("Minimum 10 years of total IT experience. Ceph knowledge required.", "Ceph", 0)]
    public void NamedJdDurationsDoNotBecomeEverySkillsMinimum(string source, string skill, int expected) =>
        Assert.Equal(expected, RecruitmentRequestDocumentParsingService.RequiredSkillYears(source, skill));

    [Fact]
    public void NumberedExperienceSectionCannotLeakIntoCertificates()
    {
        const string source = "5. Educational Qualification\nB.Tech or MCA\nCertifications (desirable):\nRHCE\n6. Experience Requirements\nAt least 3 years of experience with Ceph.\n7. Technical Skills\nLinux\n";
        var block = typeof(RecruitmentRequestDocumentParsingService).GetMethod("Block", BindingFlags.Static | BindingFlags.NonPublic)!;
        var certificates = (string)block.Invoke(null, [source, new[] { "certifications" }])!;
        Assert.Contains("RHCE", certificates);
        Assert.DoesNotContain("Experience", certificates);
        Assert.DoesNotContain("Ceph", certificates);
    }

    [Theory]
    [InlineData("Eligible", 80, true)]
    [InlineData("Eligible", 74, false)]
    [InlineData("Eligible", null, false)]
    [InlineData("Ineligible", 99, false)]
    [InlineData("NeedsReview", 99, false)]
    [InlineData("NotAssessed", 99, false)]
    public void NewInterviewRequiresEvidenceAndCutoff(string status, int? score, bool allowed)
    {
        var application = new RecruitmentCandidateApplication { EligibilityStatus = status, AtsScore = score, AtsShortlistThreshold = 75 };
        Assert.Equal(allowed, RecruitmentEligibility.InterviewSchedulingBlockReason(application).Length == 0);
    }

    [Fact]
    public void DamagedTextIsExcludedWithoutRejectingNonEnglishEvidence()
    {
        Assert.False(RecruitmentDocumentTextQuality.IsReadable("Source\u0012\u0013\u0017\u0016bytes"));
        Assert.True(RecruitmentDocumentTextQuality.IsReadable("अनुभव: पाँच वर्ष\nशिक्षा: स्नातक\tकौशल"));
        Assert.True(RecruitmentDocumentTextQuality.IsReadable("LL.B. Regular\n\fExperience: 6 years"));
    }
}
