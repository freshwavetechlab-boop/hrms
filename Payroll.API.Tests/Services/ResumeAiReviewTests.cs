using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class ResumeAiReviewTests
{
    private const string Employment = "April 2021 - Current\nChief Technology Officer\nExample Research Labs\nBengaluru, India";
    private const string Education = "Master of Technology in Computer Science\nExample University\n2010";
    private const string Source = "Asha Kumar\nasha@example.test\nSKILLS\nArtificial intelligence design\nDocker\nSQL\nPROFESSIONAL EXPERIENCE\n" + Employment
        + "\nApril 2017 - April 2021\nSoftware Engineer\nFormer Systems Ltd\nEDUCATION\n" + Education;
    private static ResumeParseResult Local() => new("Parsed", Source, ResumeParsedFacts.Empty with
    {
        FullName = "Asha Kumar", Email = "asha@example.test", CurrentCompany = "Artificial intelligence design",
        CurrentTitle = "Software Engineer", HighestQualification = "LLM Design", Skills = ["SQL"], CharacterCount = Source.Length
    }, [new("SKILLS", "Skills", "Docker\nSQL\nArtificial intelligence design", 10, .95m)], "BuiltIn", "3.0", "");
    private static RecruitmentAiResumeDocumentSuggestion Suggestion() => new()
    {
        Status = "Completed", Provider = "TestModel", Confidence = .95m,
        CurrentCompany = "Example Research Labs", CurrentTitle = "Chief Technology Officer", CurrentEmploymentEvidence = Employment,
        HighestQualification = "Master of Technology in Computer Science", QualificationEvidence = Education, Skills = ["Docker"],
        FieldMetadata = new(StringComparer.OrdinalIgnoreCase)
        {
            ["currentCompany"] = new() { SourceType = "exact", Confidence = .95m },
            ["currentTitle"] = new() { SourceType = "exact", Confidence = .95m },
            ["highestQualification"] = new() { SourceType = "exact", Confidence = .95m },
            ["skills"] = new() { SourceType = "exact", Confidence = .95m }
        }
    };

    [Fact]
    public void GroundedAiCorrectsNonemptyButWrongLocalProfessionalFacts()
    {
        var result = ResumeParsingService.MergeAiSuggestion(Local(), Suggestion(), false, false);
        Assert.Equal("Example Research Labs", result.Facts.CurrentCompany);
        Assert.Equal("Chief Technology Officer", result.Facts.CurrentTitle);
        Assert.Equal("Master of Technology in Computer Science", result.Facts.HighestQualification);
        Assert.Contains("Docker", result.Facts.Skills);
        Assert.Contains("SQL", result.Facts.Skills);
        Assert.Empty(result.AiReviewIssues);
        Assert.Equal(Local().Facts.Email, result.Facts.Email);
    }

    [Theory]
    [InlineData("invented")]
    [InlineData("historical")]
    [InlineData("skills")]
    [InlineData("low-confidence")]
    public void UnsupportedOrHistoricalProposalCannotSilentlyReplaceLocalFacts(string scenario)
    {
        var ai = Suggestion();
        if (scenario == "invented") ai.CurrentCompany = "Invented Labs";
        if (scenario == "historical")
        { ai.CurrentCompany = "Former Systems Ltd"; ai.CurrentEmploymentEvidence = "April 2017 - April 2021\nSoftware Engineer\nFormer Systems Ltd"; }
        if (scenario == "skills")
        { ai.CurrentCompany = "Docker"; ai.CurrentEmploymentEvidence = "Docker\nSQL"; }
        if (scenario == "low-confidence") ai.Status = "LowConfidence";
        var result = ResumeParsingService.MergeAiSuggestion(Local(), ai, false, false);
        Assert.Equal(Local().Facts.CurrentCompany, result.Facts.CurrentCompany);
        Assert.Contains(result.AiReviewIssues, issue => issue.Field == "CurrentCompany" && issue.Code == "AiFieldConflict");
    }

    [Fact]
    public void PursuingQualificationDoesNotReplaceCompletedQualification()
    {
        const string pursuing = "PhD in Computer Science - pursuing\nExample University";
        var local = Local() with { Text = Source + "\n" + pursuing, Facts = Local().Facts with { HighestQualification = "Master of Technology in Computer Science" } };
        var ai = Suggestion(); ai.HighestQualification = "PhD in Computer Science"; ai.QualificationEvidence = pursuing;
        var result = ResumeParsingService.MergeAiSuggestion(local, ai, false, false);
        Assert.Equal(local.Facts.HighestQualification, result.Facts.HighestQualification);
        Assert.Contains(result.AiReviewIssues, issue => issue.Field == "HighestQualification");
    }

    [Fact]
    public void RecruiterReviewedDraftFactsCannotBeOverwrittenByAi()
    {
        var local = Local() with { ParserName = "BuiltIn + HumanReviewed" };
        Assert.Equal(local, ResumeParsingService.MergeAiSuggestion(local, Suggestion(), false, false));
    }

    [Fact]
    public void CompactModelProfessionalFieldsAreIndependentlyGrounded()
    {
        var ai = Suggestion(); ai.CurrentCompany = "Invented Labs"; ai.Skills.Add("Invented Skill");
        LocalLlmProtocol.GroundResume(ai, Source);
        Assert.Empty(ai.CurrentCompany);
        Assert.Equal("Chief Technology Officer", ai.CurrentTitle);
        Assert.DoesNotContain("Invented Skill", ai.Skills);
        Assert.Equal("exact", ai.FieldMetadata["currentTitle"].SourceType);
    }
}
