using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class ResumeParsingRecoveryTests
{
    [Fact]
    public async Task EducationDeclarationAndSkillCategoryRegression()
    {
        var result = await Parse("ARJUN SHARMA\narjun@example.com\n" + Profile + "SKILLS AND TOOLS\nDevOps & CI/CD: Jenkins, Azure DevOps\nEDUCATION\nB.E. | Computer Science Engineering | VTU | 72% | 2013 - 2017\nDeclaration: I hereby declare that the information provided by me is true and authentic to the best of my knowledge.");
        Assert.Contains("CI/CD", result.Facts.Skills);
        Assert.Contains("Jenkins", result.Facts.Skills);
        var education = Assert.Single(result.Facts.Education);
        Assert.Equal("VTU", education.Institution);
        Assert.Equal(2017, education.CompletionYear);
        Assert.DoesNotContain("Declaration", education.Qualification, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NegatedCategoryAndMissingInstitutionAreNotInvented()
    {
        var result = await Parse("ARJUN SHARMA\narjun@example.com\n" + Profile + "SKILLS\nNo experience in CI/CD: Jenkins\nEDUCATION\nB.Tech | Computer Science | 2020");
        Assert.DoesNotContain("CI/CD",result.Facts.Skills);
        Assert.Empty(Assert.Single(result.Facts.Education).Institution);
    }
    private const string Profile = "\nPROFESSIONAL SUMMARY\nSoftware Engineer with 7 years of experience building enterprise applications.\n";
    private static ResumeParsingService Parser() => new(null!, null!, NullLogger<ResumeParsingService>.Instance);

    private static Task<ResumeParseResult> Parse(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return Parser().ParseAsync(new MemoryStream(bytes), "resume.txt", bytes.Length, CancellationToken.None);
    }

    [Theory]
    [InlineData("+91 98765 43210\n2020 - 2024", "+91 98765 43210")]
    [InlineData("+91\u00a098765\u00a043210", "+91 98765 43210")]
    [InlineData("+91\u201198765\u201143210", "+91-98765-43210")]
    [InlineData("+９１ ９８７６５ ４３２１０", "+91 98765 43210")]
    [InlineData("+44 7911 123456", "+44 7911 123456")]
    public async Task ContactFormatting_PreservesCompletePhone(string phone, string expected)
    {
        var result = await Parse($"ARJUN SHARMA\narjun@example.com\nMobile: {phone}{Profile}");
        Assert.Equal("Parsed", result.Status);
        Assert.Equal(expected, result.Facts.Phone);
    }

    [Theory]
    [InlineData("arjun.sharma @ example . com", "arjun.sharma@example.com")]
    [InlineData("arjun\u200b.sharma@example.com", "arjun.sharma@example.com")]
    [InlineData("ａｒｊｕｎ＠ｅｘａｍｐｌｅ．ｃｏｍ", "arjun@example.com")]
    public async Task ContactFormatting_RecoversEmailWithoutAi(string email, string expected)
    {
        var result = await Parse($"ARJUN SHARMA\nEmail: {email}{Profile}");
        Assert.Equal(expected, result.Facts.Email);
    }

    [Theory]
    [InlineData("98765\n43210")]
    [InlineData("Employee ID: 9876543210\nAccount: 8765432109")]
    [InlineData("+44 7911\n123456")]
    public async Task ContactFormatting_DoesNotInventPhoneFromSeparateLinesOrIdentifiers(string numbers)
    {
        var result = await Parse($"ARJUN SHARMA\n{numbers}{Profile}");
        Assert.Empty(result.Facts.Phone);
    }

    [Theory]
    [InlineData("Technical proficiencies: Jenkins, Terraform and Kubernetes")]
    [InlineData("Hands-on experience with Jenkins, Terraform and Kubernetes.")]
    [InlineData("Strong expertise in Jenkins, Terraform and Kubernetes.")]
    [InlineData("Tools & Technologies\nJenkins, Terraform and Kubernetes")]
    public async Task ExplicitSkillLists_AreRecoveredWithoutAi(string skills)
    {
        var result = await Parse($"ARJUN SHARMA\narjun@example.com{Profile}{skills}");
        Assert.Contains("Jenkins", result.Facts.Skills);
        Assert.Contains("Terraform", result.Facts.Skills);
        Assert.Contains("Kubernetes", result.Facts.Skills);
    }

    [Theory]
    [InlineData("")]
    [InlineData("SKILLS\n")]
    public async Task NegatedOrDesiredSkills_AreNotClaimedAsCandidateSkills(string heading)
    {
        var result = await Parse($"ARJUN SHARMA\narjun@example.com{Profile}{heading}No experience with Rust, Go and Terraform.\nSeeking experience with Docker and Kubernetes.");
        Assert.Empty(result.Facts.Skills);
    }

    [Fact]
    public async Task ExplicitSkillLists_KeepPunctuationInTechnologyNames()
    {
        var result = await Parse($"ARJUN SHARMA\narjun@example.com{Profile}Strong expertise in .NET, Node.js and C#.");
        Assert.Contains(".NET", result.Facts.Skills);
        Assert.Contains("Node.js", result.Facts.Skills);
        Assert.Contains("C#", result.Facts.Skills);
    }

    [Fact]
    public async Task SkillGuards_RetainLegitimateLearningAndAnalysisSkills()
    {
        var result = await Parse($"ARJUN SHARMA\narjun@example.com{Profile}SKILLS\nMachine learning, Requirements analysis, Deep learning\nLearning Python");
        Assert.Contains("Machine learning", result.Facts.Skills);
        Assert.Contains("Requirements analysis", result.Facts.Skills);
        Assert.Contains("Deep learning", result.Facts.Skills);
        Assert.DoesNotContain("Learning Python", result.Facts.Skills);
    }

    [Fact]
    public async Task EmailRecovery_DoesNotJoinContactAcrossLines()
    {
        var result = await Parse($"ARJUN SHARMA\nEmail: arjun @\nexample.com{Profile}");
        Assert.Empty(result.Facts.Email);
    }

    [Fact]
    public async Task Docx_SoftLineBreaks_PreserveSectionsAndContact()
    {
        XNamespace word = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var lines = $"ARJUN SHARMA\narjun@example.com{Profile}TECHNICAL SKILLS\nDocker, Kubernetes".Split('\n');
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        using (var entry = zip.CreateEntry("word/document.xml").Open())
        {
            var runs = lines.SelectMany(line => new[] { new XElement(word + "t", line), new XElement(word + "br") });
            new XDocument(new XElement(word + "document", new XElement(word + "body",
                new XElement(word + "p", new XElement(word + "r", runs))))).Save(entry);
        }
        var bytes = memory.ToArray();
        var result = await Parser().ParseAsync(new MemoryStream(bytes), "resume.docx", bytes.Length, CancellationToken.None);
        Assert.Equal("ARJUN SHARMA", result.Facts.FullName);
        Assert.Equal("arjun@example.com", result.Facts.Email);
        Assert.Contains("Kubernetes", result.Facts.Skills);
        Assert.Contains(result.Sections, section => section.SectionCode == "SKILLS");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AiSkillSections_MustBeGroundedInReadableResume(bool grounded)
    {
        var local = await Parse($"ARJUN SHARMA\narjun@example.com{Profile}Platform toolkit\nDocker, Kubernetes");
        var ai = new RecruitmentAiResumeDocumentSuggestion
        {
            Status = "Completed",
            Sections = [new() { SectionCode = "SKILLS", Content = grounded ? "Docker, Kubernetes" : "Rust, Go", Confidence = .95m }]
        };
        var merged = ResumeParsingService.MergeAiSuggestion(local, ai, hasRetrievalContext: true, documentWasAttached: false);
        if (grounded) Assert.Contains("Kubernetes", merged.Facts.Skills);
        else Assert.DoesNotContain("Rust", merged.Facts.Skills);
        Assert.Equal(local.Facts.Email, merged.Facts.Email);
        Assert.Equal(local.Status, merged.Status);
    }

    [Fact]
    public void AiOnlyScannedResume_RemainsNeedsReview()
    {
        var local = ResumeParseResult.WithoutContent("NeedsReview", "BuiltIn", "test", "Unreadable document");
        var ai = new RecruitmentAiResumeDocumentSuggestion
        {
            Status = "Completed", Email = "invented@example.com",
            Sections = [new() { SectionCode = "SKILLS", Content = "Docker, Kubernetes", Confidence = .95m }]
        };
        var result = ResumeParsingService.MergeAiSuggestion(local, ai, false, true);
        Assert.Equal("NeedsReview", result.Status);
        Assert.Equal(local.Error, result.Error);
        Assert.Empty(result.Facts.Email);
    }

    [Fact]
    public async Task UnavailableAi_PreservesReadableLocalResultInBothEntryPoints()
    {
        var bytes = Encoding.UTF8.GetBytes($"ARJUN SHARMA\narjun@example.com{Profile}");
        var parser = Parser(); // No provider/RAG: exercises the existing safe fallback, no network or DB.
        var streamed = await parser.ParseAsync(new MemoryStream(bytes), "resume.txt", bytes.Length, 20, [], CancellationToken.None);
        var file = new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "resume.txt");
        var uploaded = await parser.ParseAsync(file, 20, [], CancellationToken.None);
        Assert.Equal("Parsed", streamed.Status);
        Assert.Equal(streamed.Facts.Email, uploaded.Facts.Email);
        Assert.Equal(streamed.Facts.TotalExperienceMonths, uploaded.Facts.TotalExperienceMonths);
        Assert.Empty(streamed.Error);
        Assert.Empty(uploaded.Error);
    }
}
