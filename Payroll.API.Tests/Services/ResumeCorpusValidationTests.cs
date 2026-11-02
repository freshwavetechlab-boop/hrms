using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class ResumeCorpusValidationTests
{
    private static async Task<ResumeParseResult> Parse(string body)
    {
        var text = "Asha Kumar\nasha@example.test\n+91 9876543210\n" + body;
        var bytes = Encoding.UTF8.GetBytes(text);
        return await new ResumeParsingService(null!, null!, NullLogger<ResumeParsingService>.Instance)
            .ParseAsync(new MemoryStream(bytes), "resume.txt", bytes.Length, CancellationToken.None);
    }

    [Fact]
    public async Task InlineEducationAndSkillHeadingsDoNotLoseTheirValues()
    {
        var result = await Parse("ABOUT ME\nTechnology executive building enterprise platforms.\nSKILLS | SQL, Azure\nEDUCATION | PhD - Computer Engineering\nMaster in Computer Engineering\n");
        Assert.Contains("PhD", result.Facts.HighestQualification);
        Assert.DoesNotContain("ABOUT ME", result.Facts.HighestQualification);
        Assert.Contains("SQL", result.Facts.Skills);
    }

    [Theory]
    [InlineData("Built an in-house regulatory LLM into onboarding and compliance")]
    [InlineData("Designed workflow-based and LLM-based agents")]
    [InlineData("Led 50+ integration channels including Cortana and MS Teams")]
    [InlineData("ABOUT ME")]
    public async Task TechnologyProseCannotBecomeAQualification(string prose)
    {
        var result = await Parse("SUMMARY\n" + prose + "\nEDUCATION\nB.Tech in Computing\n");
        Assert.Equal("B.Tech in Computing", result.Facts.HighestQualification);
    }

    [Fact]
    public async Task HighestCompletedDegreeIsRankedAndPursuingDegreeRemainsInEducation()
    {
        var result = await Parse("EDUCATION\nB.Tech in Computing\nMBA in Management\nExecutive PhD (pursuing)\n");
        Assert.Equal("MBA in Management", result.Facts.HighestQualification);
        Assert.Contains(result.Facts.Education, row => row.Qualification.Contains("pursuing"));
    }

    [Fact]
    public async Task DateFirstRoleOnSameRowDoesNotBorrowTheNextHistoricalEmployer()
    {
        var result = await Parse("EXPERIENCE\n2025 - Till date | Professor of Computing at Example University\n2019 - 2024 | Earlier Systems Ltd | Vice President\nSKILLS\nSQL, Azure\n");
        Assert.Equal("Professor of Computing", result.Facts.CurrentTitle);
        Assert.Equal("Example University", result.Facts.CurrentCompany);
    }

    [Fact]
    public async Task SeparateEmployerBeforeOrAfterDateIsAssociatedWithTheSameRole()
    {
        var result = await Parse("EXPERIENCE\nChief Technology Officer\nNovember 2021 - Present\nExample Financial Services Private Limited | Mumbai, India\nBuilt dependable platforms for enterprise customers.\nSKILLS\nSQL, Azure\n");
        Assert.Equal("Chief Technology Officer", result.Facts.CurrentTitle);
        Assert.Equal("Example Financial Services Private Limited", result.Facts.CurrentCompany);
    }

    [Fact]
    public async Task SpecializationAfterDashStaysInRoleWhenEmployerHasItsOwnLine()
    {
        var result = await Parse("EXPERIENCE\nExample Technologies Pvt. Ltd.\nVice President - Technology Products Aug 2025 - Present\nBuilt dependable platforms for enterprise customers.\nSKILLS\nSQL, Azure\n");
        Assert.Equal("Vice President - Technology Products", result.Facts.CurrentTitle);
        Assert.Equal("Example Technologies Pvt. Ltd.", result.Facts.CurrentCompany);
    }

    [Fact]
    public async Task DateFirstWithEmployerAndCostMetadataExtractsActualRole()
    {
        var result = await Parse("CAREER REVIEW\nOct 2021 Onwards - Chief Technology Officer with Example Capital NBFC | 5000 Crore Company\nBuilt dependable platforms for enterprise customers.\nSKILLS\nSQL, Azure\n");
        Assert.Equal("Chief Technology Officer", result.Facts.CurrentTitle);
        Assert.Equal("Example Capital NBFC", result.Facts.CurrentCompany);
    }

    [Fact]
    public async Task EmployerDescriptionWithNumbersDoesNotHideTheImmediatelyFollowingCurrentRole()
    {
        var result = await Parse("EXPERIENCE\nExample Billpay Limited - Sep 2023 - Present\nBill payment platform serving enterprise customers across 25+ categories,\nChief Transformation Technology I Reporting to: CEO I Team Size: 150+\nAppointed to architect the next phase of platform modernisation.\nSKILLS\nSQL, Azure\n");
        Assert.Equal("Chief Transformation Technology", result.Facts.CurrentTitle);
        Assert.Equal("Example Billpay Limited", result.Facts.CurrentCompany);
    }

    [Fact]
    public async Task UniversityNamedMsDoesNotHideTheActualDoctoralDegreeOnTheSameLine()
    {
        var result = await Parse("EDUCATION\nMS Example University (2022), Ph.D. (Computer Science), Thesis\nExample College (1992), BE (Electrical Engineering)\n");
        Assert.Contains("Ph.D.", result.Facts.HighestQualification);
    }

    [Fact]
    public async Task ExplicitToolsInProfessionalNarrativeAreReadWithoutInventingOtherSkills()
    {
        var result = await Parse("SUMMARY\nBuilt production platforms using OpenShift and Python.\nNo experience with AWS.\nEDUCATION\nB.Tech in Computing\n");
        Assert.Contains("OpenShift", result.Facts.Skills);
        Assert.Contains("Python", result.Facts.Skills);
        Assert.DoesNotContain("AWS", result.Facts.Skills);
        Assert.DoesNotContain("Java", result.Facts.Skills);
    }

    [Theory]
    [InlineData("Dec 2019 till date")]
    [InlineData("Dec 2019 tilldate")]
    [InlineData("Dec 2019 to date")]
    [InlineData("Dec 2019 to hitherto")]
    [InlineData("Dec 2019 -")]
    public async Task EquivalentOngoingMarkersDoNotSelectAnOlderEndedEmployer(string dates)
    {
        var result = await Parse($"EXPERIENCE\nExample Small Finance Bank ({dates})\nChief Information and Digital Officer\nBuilt platforms for enterprise customers.\nEarlier Systems Ltd (Jan 2018 - Nov 2019)\nChief Technology Officer\nEDUCATION\nB.Tech in Computing\n");
        Assert.Equal("Example Small Finance Bank", result.Facts.CurrentCompany);
        Assert.Equal("Chief Information and Digital Officer", result.Facts.CurrentTitle);
    }

    [Fact]
    public async Task ScrumMasterCertificationCannotOutrankAnExplicitDegree()
    {
        var result = await Parse("EDUCATION AND RECOGNITION\nEducation. Bachelor of Technology (B.Tech), Computing, Example University\nCertifications. Certified Scrum Master (CSM); AWS Certified Solutions Architect\n");
        Assert.Contains("Bachelor of Technology", result.Facts.HighestQualification);
        Assert.DoesNotContain("Scrum", result.Facts.HighestQualification);
    }

    [Fact]
    public async Task WrappedEndDateCannotMakeAnOldJobCurrent()
    {
        var result = await Parse("EXPERIENCE\nChief Technology Officer | Current Systems Ltd | 03/2023 - 10/2025\nDirector | Older Systems Ltd | 12/2022 -\n03/2023\nEDUCATION\nB.Tech in Computing\n");
        Assert.Equal("Current Systems Ltd", result.Facts.CurrentCompany);
        Assert.All(result.Facts.Experience, row => Assert.False(row.IsCurrent));
    }

    [Fact]
    public async Task StartOnlyRoleIsFlaggedInsteadOfSilentlyCertifyingAnOlderRoleAsCurrent()
    {
        var result = await Parse("EXPERIENCE\nChief Technology Officer - 11/2025 - Innovation Centre\nGovernment of Example\nChief Technology Officer | Head Engineering, 03/2023 - 10/2025\nExample Systems Ltd\nEDUCATION\nB.Tech in Computing\n");
        Assert.Contains(result.ReviewIssues, issue => issue.Code == "DateReview");
        Assert.Contains("Confirm the latest role", ResumeParsingValidation.ReviewSummary(result));
    }

    [Fact]
    public async Task ExplicitPresentProfileTakesPrecedenceOverAnOlderEndedJob()
    {
        var result = await Parse("Present Profile: - Working as Head - IT with Example Bank, providing strategic leadership\nEXPERIENCE\nEarlier Finance Bank (Jan 2023 - Feb 2024) - Chief Information Officer\nEDUCATION\nB.Tech in Computing\n");
        Assert.Equal("Example Bank", result.Facts.CurrentCompany);
        Assert.Equal("Head - IT", result.Facts.CurrentTitle);
    }
}
