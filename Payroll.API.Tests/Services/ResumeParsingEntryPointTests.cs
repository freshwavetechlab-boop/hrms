using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class ResumeParsingEntryPointTests
{
    [Theory]
    [InlineData(true, true, true, true, true)]
    [InlineData(true, true, false, true, false)]
    [InlineData(true, false, true, false, false)]
    [InlineData(true, null, null, true, false)]
    [InlineData(false, null, null, false, false)]
    [InlineData(false, true, true, true, true)]
    public void JobSwitchesHaveTheSameMeaningForPublicIntakeAndReplacement(bool client, bool? parsing,
        bool? ai, bool expectedParsing, bool expectedAi)
    {
        Assert.Equal(new ResumeParsingJobPolicy(expectedParsing, expectedAi), ResumeParsingJobPolicy.Resolve(client, parsing, ai));
    }

    [Fact]
    public void ExplicitIntakeSwitchesCannotAccidentallyInheritAnotherExistingApplication()
    {
        Assert.Equal(new ResumeParsingJobPolicy(true, false), ResumeParsingJobPolicy.Resolve(true, false, true, true, false));
        Assert.Equal(new ResumeParsingJobPolicy(false, false), ResumeParsingJobPolicy.Resolve(true, true, true, false, true));
    }

    [Theory]
    [InlineData(7, 41, 20, true)]
    [InlineData(8, 41, 20, false)]
    [InlineData(7, 42, 20, false)]
    [InlineData(7, 41, 21, false)]
    [InlineData(7, 41, null, true)]
    public void ReplacementCannotBorrowAnotherCandidateApplicationOrClient(long candidate, long application,
        int? actorClient, bool allowed)
    {
        var context = new RecruitmentTalentRepository.ResumeApplicationParsingContext
        { CandidateId = 7, ApplicationId = 41, PositionId = 9, ClientId = 20, EnableResumeParsing = true, EnableAiParsing = true };
        Assert.Equal(allowed, RecruitmentTalentRepository.IsResumeApplicationContextAllowed(context, candidate, application, actorClient));
        Assert.False(RecruitmentTalentRepository.IsResumeApplicationContextAllowed(null, candidate, application, actorClient));
    }

    [Fact]
    public async Task DisabledParsingNeverReadsTheFileOrCallsTheModel()
    {
        var ai = new RecordingAi(); var parser = Parser(ai);
        var result = await parser.ParseForJobAsync(null!, 20, false, true, [], default);
        Assert.Equal("Disabled", result.Status); Assert.Equal(0, ai.Calls);
    }

    [Fact]
    public async Task IncompleteAcceptedResumeUsesTheSameAiFallbackOnlyWhenJobEnablesIt()
    {
        var ai = new RecordingAi(); var parser = Parser(ai);
        var bytes = Encoding.ASCII.GetBytes("%PDF-1.4\n%%EOF");
        FormFile File() => new(new MemoryStream(bytes), 0, bytes.Length, "file", "resume.pdf");
        // A trusted local extraction receipt avoids OCR/network in this contract test.
        var local = ResumeParseResult.WithoutContent("NeedsReview", "BuiltIn", "3.0", "Review source");
        await parser.ParseForJobAsync(File(), 20, true, false, ["Approved CTO job"], default, local);
        Assert.Equal(0, ai.Calls);
        var result = await parser.ParseForJobAsync(File(), 20, true, true, ["Approved CTO job"], default, local);
        Assert.Equal(1, ai.Calls); Assert.Equal(20, ai.ClientId); Assert.True(ai.HadDocument);
        Assert.Equal(local with { AiReviewStatus = "NotEnabled" }, result);
    }

    [Fact]
    public async Task CompleteSourceFactsStillReachAiWhenTheJobEnablesIt()
    {
        var ai = new RecordingAi(); var parser = Parser(ai);
        var local = new ResumeParseResult("Parsed", "Source evidence", ResumeParsedFacts.Empty with
        {
            FullName = "Asha Kumar", Email = "asha@example.test", CurrentCompany = "Example Labs",
            CurrentTitle = "Chief Architect", HighestQualification = "MBA", Skills = ["SQL"], CharacterCount = 300
        }, [], "BuiltIn", "3.0", "");
        var bytes = Encoding.UTF8.GetBytes(local.Text);
        var file = new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "resume.txt");
        var result = await parser.ParseForJobAsync(file, 20, true, true, [], default, local);
        Assert.Equal(local.Facts, result.Facts); Assert.Equal(1, ai.Calls);
        Assert.Equal("NotEnabled", result.AiReviewStatus);
        Assert.Contains(result.ReviewIssues, issue => issue.Code == "AiReviewUnavailable");
    }

    [Fact]
    public async Task FileAndStreamUploadsBothRunAiForReadableCompleteResumes()
    {
        const string text = "Asha Kumar\nasha@example.test\nSUMMARY\nTechnology executive building enterprise platforms and reliable products.\nPROFESSIONAL EXPERIENCE\nApril 2021 - Current\nChief Technology Officer\nExample Research Labs\nEDUCATION\nMaster of Technology in Computer Science\nExample University\n2010\nSKILLS\nSQL\nDocker";
        var bytes = Encoding.UTF8.GetBytes(text);
        var ai = new RecordingAi(); var parser = Parser(ai);
        var file = new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "resume.txt");
        var uploaded = await parser.ParseAsync(file, 20, [], default);
        var streamed = await parser.ParseAsync(new MemoryStream(bytes), "resume.txt", bytes.Length, 20, [], default);
        Assert.Equal(2, ai.Calls);
        Assert.Equal("Parsed", uploaded.Status);
        Assert.Equal(uploaded.Facts.CurrentCompany, streamed.Facts.CurrentCompany);
        Assert.Equal("NotEnabled", uploaded.AiReviewStatus);
        Assert.Equal(uploaded.AiReviewStatus, streamed.AiReviewStatus);
    }

    [Fact]
    public async Task ReviewedDraftStillRunsAiWhileKeepingRecruiterValues()
    {
        var ai = new RecordingAi { Suggestion = new() { Status = "Completed", CurrentTitle = "Chief Architect" } };
        var bytes = Encoding.UTF8.GetBytes("Asha Kumar\nCurrent designation: Chief Architect");
        var local = new ResumeParseResult("Parsed", Encoding.UTF8.GetString(bytes), ResumeParsedFacts.Empty with
            { CurrentTitle = "Chief Technology Officer" }, [], "BuiltIn + HumanReviewed", "3.0", "");
        var file = new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "resume.txt");
        var result = await Parser(ai).ParseForJobAsync(file, 20, true, true, [], default, local);
        Assert.Equal(1, ai.Calls);
        Assert.Equal(local.Facts, result.Facts);
        Assert.Equal("Completed", result.AiReviewStatus);
    }

    [Fact]
    public async Task AiFallbackMakesEmploymentQualificationAndSkillsAvailableForDraftReview()
    {
        var ai = new RecordingAi
        {
            Suggestion = new()
            {
                Status = "Completed", Provider = "TestModel", Confidence = .95m,
                Sections =
                [
                    new() { SectionCode = "EXPERIENCE", Heading = "Professional Experience", DisplayOrder = 10,
                        Content = "April 2021 - Current\nChief Technology Officer\nExample Research Labs\nBengaluru, India", Confidence = .95m },
                    new() { SectionCode = "EDUCATION", Heading = "Education", DisplayOrder = 20,
                        Content = "Master of Technology in Computer Science\nExample University\n2010", Confidence = .95m },
                    new() { SectionCode = "SKILLS", Heading = "Skills", DisplayOrder = 30,
                        Content = "Cloud architecture\nSQL\nDocker", Confidence = .95m }
                ]
            }
        };
        var bytes = Encoding.ASCII.GetBytes("%PDF-1.4\n%%EOF");
        var file = new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "draft.pdf");
        var local = ResumeParseResult.WithoutContent("NeedsReview", "BuiltIn", "3.0", "Review source");
        var result = await Parser(ai).ParseForJobAsync(file, 20, true, true, [], default, local);

        Assert.Equal(1, ai.Calls);
        Assert.Equal("Example Research Labs", result.Facts.CurrentCompany);
        Assert.Equal("Chief Technology Officer", result.Facts.CurrentTitle);
        Assert.Contains("Master of Technology", result.Facts.HighestQualification);
        Assert.Contains("Docker", result.Facts.Skills);
        Assert.Contains("TestModel", result.ParserName);
        // Suggested document-only facts remain reviewable; they do not invent a
        // contact identity or promote a scanned source into verified ATS evidence.
        Assert.Equal("NeedsReview", result.Status);
        Assert.Empty(result.Facts.Email);
    }

    [Fact]
    public async Task UnavailablePreviewAiRetainsLocalFactsForManualReview()
    {
        var ai = new RecordingAi { Throw = true };
        var bytes = Encoding.ASCII.GetBytes("%PDF-1.4\n%%EOF");
        var file = new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "draft.pdf");
        var local = ResumeParseResult.WithoutContent("NeedsReview", "BuiltIn", "3.0", "Review source");
        var result = await Parser(ai).ParseForJobAsync(file, 20, true, true, [], default, local);
        Assert.Equal(1, ai.Calls);
        Assert.Equal(local with { AiReviewStatus = "Unavailable" }, result);
    }

    [Fact]
    public async Task InvalidReplacementContextIsRejectedBeforeDatabaseOrStorage()
    {
        var repository = new RecruitmentTalentRepository(null!, null!, null!, null!, null!, null!,
            null!, null!, null!, null!, null!, NullLogger<RecruitmentTalentRepository>.Instance);
        var file = new FormFile(new MemoryStream([1]), 0, 1, "file", "resume.pdf");
        var result = await repository.UploadResumeAsync(7, new() { File = file, ApplicationId = -1 }, new AuthUser(), "", "", default);
        Assert.Null(result.Resume); Assert.Contains("valid candidate application", result.Error);
    }

    private static ResumeParsingService Parser(RecordingAi ai) => new(ai,
        new RecruitmentDocumentRagService(new ConfigurationBuilder().Build(), null!, NullLogger<RecruitmentDocumentRagService>.Instance),
        NullLogger<ResumeParsingService>.Instance);
    private sealed class RecordingAi : IResumeDocumentSuggestionService
    {
        public RecruitmentAiResumeDocumentSuggestion Suggestion { get; init; } = new() { Status = "NotEnabled" };
        public bool Throw { get; init; }
        public int Calls { get; private set; }
        public int ClientId { get; private set; }
        public bool HadDocument { get; private set; }
        public Task<RecruitmentAiResumeDocumentSuggestion> SuggestResumeDocumentAsync(int clientId, string sourceText,
            byte[]? sourceDocument = null, string sourceContentType = "", RecruitmentDocumentRagContext? retrievalContext = null,
            CancellationToken cancellationToken = default)
        {
            Calls++; ClientId = clientId; HadDocument = sourceDocument is { Length: > 0 };
            if (Throw) throw new HttpRequestException("Test provider unavailable");
            return Task.FromResult(Suggestion);
        }
    }
}
