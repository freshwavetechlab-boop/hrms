using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class ResumePreviewCacheTests
{
    private static readonly ResumeParseResult Local = new("Parsed",
        "Professional experience: April 2021 to Present, Chief Architect at Example Research. Education: Master of Technology completed 2008. Skills: Cloud architecture, Database management.",
        ResumeParsedFacts.Empty with { CurrentCompany = "Example Research", CurrentTitle = "Chief Architect", Skills = ["Cloud architecture"] },
        [], "BuiltIn", "3.0", "");
    private static FormFile File(string text = "synthetic resume bytes", string name = "resume.txt")
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return new(new MemoryStream(bytes), 0, bytes.Length, "file", name);
    }

    [Fact]
    public async Task CompletedPreviewReusesAiReviewAndPreservesHumanEditsAtUpload()
    {
        var ai = new RecordingAi(); var parser = Parser(ai);
        var file = File();
        var reviewed = await parser.ParseForJobAsync(file, 0, true, true, [], default, Local);
        using var cache = new ResumePreviewCache();
        var key = await ResumePreviewCache.KeyAsync(file, new { User = 7, Client = 20, Job = 91, Ai = true }, default);
        var saved = cache.Store(key, Local, reviewed, true)!;
        var receipt = Assert.IsType<ResumePreviewCache.Receipt>(cache.Get(key, saved.Token));
        var edited = receipt.Reviewed with { ParserName = receipt.Reviewed.ParserName + " + HumanReviewed",
            Facts = receipt.Reviewed.Facts with { CurrentTitle = "Head of Architecture", Email = "reviewer-confirmed@example.test" } };
        var final = await parser.ParseForUploadAsync(file, 0, new(true, true), [], default, edited, true);
        Assert.Equal(1, ai.Calls);
        Assert.Equal("Completed", final.AiReviewStatus);
        Assert.Equal("Head of Architecture", final.Facts.CurrentTitle);
        Assert.Equal("reviewer-confirmed@example.test", final.Facts.Email);
        Assert.Equal("Chief Architect", receipt.Local.Facts.CurrentTitle);
    }

    [Fact]
    public async Task ReceiptIsBoundToContentFilenameActorClientJobPolicyAndSettings()
    {
        using var cache = new ResumePreviewCache(); var file = File();
        object Scope(int user = 7, int client = 20, int job = 91, bool ai = true, string settings = "v1") => new { user, client, job, ai, settings };
        var key = await ResumePreviewCache.KeyAsync(file, Scope(), default);
        var saved = cache.Store(key, Local, Local with { AiReviewStatus = "Completed" }, true)!;
        Assert.NotNull(cache.Get(key, saved.Token));
        Assert.Null(cache.Get(key, new string('F', 64)));
        Assert.Null(cache.Get(key, ""));
        foreach (var scope in new[] { Scope(user: 8), Scope(client: 21), Scope(job: 92), Scope(ai: false), Scope(settings: "v2") })
            Assert.Null(cache.Get(await ResumePreviewCache.KeyAsync(File(), scope, default), saved.Token));
        Assert.Null(cache.Get(await ResumePreviewCache.KeyAsync(File("different document"), Scope(), default), saved.Token));
        Assert.Null(cache.Get(await ResumePreviewCache.KeyAsync(File(name: "renamed.txt"), Scope(), default), saved.Token));
    }

    [Theory]
    [InlineData("TimedOut")]
    [InlineData("Unavailable")]
    [InlineData("LocalBusy")]
    [InlineData("LowConfidence")]
    [InlineData("OutputTruncated")]
    public void FailedOrIncompleteAiReviewIsNotCached(string status)
    {
        using var cache = new ResumePreviewCache();
        Assert.Null(cache.Store("key", Local, Local with { AiReviewStatus = status }, true));
        Assert.Null(cache.Get("key"));
    }

    [Fact]
    public void ExpiryEvictionAndRestartSafelyLoseReceipts()
    {
        var time = new ManualTime();
        using var cache = new ResumePreviewCache(time, TimeSpan.FromMinutes(1));
        var receipt = cache.Store("key", Local, Local, false)!;
        Assert.NotNull(cache.Get("key", receipt.Token));
        using var restarted = new ResumePreviewCache();
        Assert.Null(restarted.Get("key", receipt.Token));
        time.Now += TimeSpan.FromMinutes(1);
        Assert.Null(cache.Get("key", receipt.Token));
    }

    [Fact]
    public void NewLearningRefreshesPreviewWithoutInvalidatingAnAlreadyReviewedBatch()
    {
        using var cache = new ResumePreviewCache();
        var receipt = cache.Store("key", Local, Local, false, "learning1")!;
        Assert.NotNull(cache.Get("key", expectedLearningRevision: "learning1"));
        Assert.Null(cache.Get("key", expectedLearningRevision: "learning2"));
        Assert.NotNull(cache.Get("key", receipt.Token));
    }

    [Fact]
    public void CachedFactsCannotBeMutatedByAnEditorAndOversizedSourcesAreNotRetained()
    {
        using var cache = new ResumePreviewCache();
        var receipt = cache.Store("key", Local, Local, false)!;
        ((IList<string>)receipt.Local.Facts.Skills)[0] = "tampered";
        Assert.Equal("Cloud architecture", cache.Get("key")!.Local.Facts.Skills[0]);
        Assert.Null(cache.Store("huge", Local with { Text = new string('x', 1024 * 1024) }, Local, false));
        Assert.Null(cache.Store("failed", Local with { Status = "Failed" }, Local, false));
    }

    [Theory]
    [InlineData(false, "Completed")]
    [InlineData(true, "TimedOut")]
    public async Task MissingOrInvalidReceiptStillRunsAiEvenForHumanReviewedDraft(bool receipt, string status)
    {
        var ai = new RecordingAi(); var parser = Parser(ai);
        var edited = Local with { ParserName = "BuiltIn + HumanReviewed", AiReviewStatus = status };
        var result = await parser.ParseForUploadAsync(File(), 0, new(true, true), [], default, edited, receipt);
        Assert.Equal(1, ai.Calls); Assert.Equal(edited.Facts, result.Facts);
    }

    [Fact]
    public async Task NewSourceWithCompleteParserFieldsStillRunsAiAndDisabledAiDoesNot()
    {
        var ai = new RecordingAi(); var parser = Parser(ai);
        await parser.ParseForUploadAsync(File(), 0, new(true, true), [], default, Local, false);
        Assert.Equal(1, ai.Calls);
        await parser.ParseForUploadAsync(File(), 0, new(true, false), [], default, Local, false);
        Assert.Equal(1, ai.Calls);
    }

    [Fact]
    public void RagRankingCacheReusesExactInputsAndRefreshesChangedClientQueryAndSource()
    {
        using var rag = new RecruitmentDocumentRagService(new ConfigurationBuilder().Build(), null!, NullLogger<RecruitmentDocumentRagService>.Instance);
        var calls = 0;
        List<string> Compute() { calls++; return ["Cloud architecture"]; }
        object Scope(int client = 20, string query = "resume1", string vocabulary = "Cloud") => new { client, query, vocabulary };
        var first = rag.Cached(Scope(), Compute); first[0] = "edited";
        Assert.Equal("Cloud architecture", rag.Cached(Scope(), Compute)[0]);
        Assert.Equal(1, calls);
        rag.Cached(Scope(client: 21), Compute);
        rag.Cached(Scope(query: "resume2"), Compute);
        rag.Cached(Scope(vocabulary: "Database"), Compute);
        Assert.Equal(4, calls);
    }

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 3, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private static ResumeParsingService Parser(RecordingAi ai) => new(ai,
        new RecruitmentDocumentRagService(new ConfigurationBuilder().Build(), null!, NullLogger<RecruitmentDocumentRagService>.Instance),
        NullLogger<ResumeParsingService>.Instance);
    private sealed class RecordingAi : IResumeDocumentSuggestionService
    {
        public int Calls;
        public Task<RecruitmentAiResumeDocumentSuggestion> SuggestResumeDocumentAsync(int clientId, string sourceText,
            byte[]? sourceDocument = null, string sourceContentType = "", RecruitmentDocumentRagContext? retrievalContext = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new RecruitmentAiResumeDocumentSuggestion { Status = "Completed", Confidence = .95m });
        }
    }
}
