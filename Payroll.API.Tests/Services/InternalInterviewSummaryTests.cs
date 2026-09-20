using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class InternalInterviewSummaryTests
{
    private static InternalInterviewEvent[] Questions() => [new() { Id = 1, InterviewId = 7, Kind = "question", Text = "Describe your deployment frequency." },
        new() { Id = 3, InterviewId = 7, Kind = "question", Text = "How often did you release?" },
        new() { Id = 5, InterviewId = 7, Kind = "question", Text = "How was rollback tested?" }];
    private static InternalInterviewEvent[] Answers() => [new() { Id = 2, InterviewId = 7, Kind = "answer", QuestionEventId = 1, Text = "We deployed daily.", CreatedAtUtc = new(2026, 9, 20, 1, 2, 3) },
        new() { Id = 4, InterviewId = 7, Kind = "answer", QuestionEventId = 3, Text = "We released monthly.", CreatedAtUtc = new(2026, 9, 20, 1, 3, 4) }];
    private static JsonElement Response(string kind = "crossAnswerInconsistency", int secondIndex = 1, string secondQuote = "released monthly") =>
        JsonSerializer.SerializeToElement(new { score = 99, hire = true, observations = new[] { new { kind, note = "Clarify the release frequency and whether the answers refer to different projects.",
            evidence = new[] { new { answer = 0, quote = "deployed daily" }, new { answer = secondIndex, quote = secondQuote } } } } });

    [Fact]
    public void Cross_answer_review_binds_both_quotes_ids_timestamps_and_unanswered_to_original_evidence()
    {
        var summary = InternalInterviewAiService.ValidateSummary(Response(), Questions(), Answers());
        Assert.Equal(3, summary.QuestionCount); Assert.Equal(2, summary.AnswerCount);
        Assert.Equal(new long[] { 5 }, summary.UnansweredQuestionEventIds);
        var observation = Assert.Single(summary.Observations);
        Assert.Equal(new long[] { 2, 4 }, observation.Evidence.Select(e => e.AnswerEventId));
        Assert.Equal(new long[] { 1, 3 }, observation.Evidence.Select(e => e.QuestionEventId));
        Assert.Equal(Answers()[0].CreatedAtUtc, observation.Evidence[0].AnsweredAtUtc);
        var json = JsonSerializer.Serialize(summary);
        Assert.DoesNotContain("score", json, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("hire", json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("reject", 1, "released monthly")]
    [InlineData("crossAnswerInconsistency", 0, "deployed daily")]
    [InlineData("crossAnswerInconsistency", 1, "deployed daily")]
    [InlineData("crossAnswerInconsistency", 999, "released monthly")]
    [InlineData("interviewSummary", -1, "released monthly")]
    [InlineData("interviewSummary", 1, " ")]
    public void Fabrication_foreign_answer_unsupported_kind_and_single_answer_inconsistency_are_rejected(string kind, int index, string quote)
        => Assert.Throws<InternalInterviewException>(() => InternalInterviewAiService.ValidateSummary(Response(kind, index, quote), Questions(), Answers()));

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"observations\":[null]}")]
    [InlineData("{\"observations\":[{\"kind\":\"interviewSummary\",\"note\":\"x\",\"evidence\":[]}]}")]
    public void Malformed_model_summary_is_a_controlled_error(string json)
    {
        using var response = JsonDocument.Parse(json);
        var error = Assert.Throws<InternalInterviewException>(() => InternalInterviewAiService.ValidateSummary(response.RootElement, Questions(), Answers()));
        Assert.Equal(502, error.StatusCode);
    }

    [Fact]
    public async Task Oversize_transcript_is_not_truncated_or_sent_to_model()
    {
        var model = new Model(); var answers = Answers(); answers[0].Text = new string('ह', 12000);
        var error = await Assert.ThrowsAsync<InternalInterviewException>(() => new InternalInterviewAiService(model).ReviewInterviewAsync(Questions(), answers, default));
        Assert.Equal(422, error.StatusCode); Assert.Contains("No partial transcript", error.Message); Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task Zero_answers_is_host_reported_without_inference_or_invented_summary()
    {
        var model = new Model(); var result = await new InternalInterviewAiService(model).ReviewInterviewAsync(Questions(), [], default);
        Assert.Equal(0, result.AnswerCount); Assert.Empty(result.Observations);
        Assert.Equal(new long[] { 1, 3, 5 }, result.UnansweredQuestionEventIds); Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task Browser_events_foreign_session_and_unsaved_transcripts_never_reach_combined_model()
    {
        var model = new Model(); var service = new InternalInterviewAiService(model);
        foreach (var invalid in new[] { "blur", "note", "transcript" })
        {
            var answers = Answers(); answers[0].Kind = invalid;
            await Assert.ThrowsAsync<InternalInterviewException>(() => service.ReviewInterviewAsync(Questions(), answers, default));
        }
        var foreign = Answers(); foreign[0].InterviewId = 8;
        await Assert.ThrowsAsync<InternalInterviewException>(() => service.ReviewInterviewAsync(Questions(), foreign, default));
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task Single_bounded_request_contains_every_full_question_and_answer_but_no_browser_context()
    {
        var model = new Model(); var result = await new InternalInterviewAiService(model).ReviewInterviewAsync(Questions(), Answers(), default);
        Assert.Equal(1, model.Calls); Assert.Single(result.Observations);
        using var request = JsonDocument.Parse(model.Prompt);
        Assert.Equal(3, request.RootElement.GetProperty("questions").GetArrayLength());
        Assert.Equal(2, request.RootElement.GetProperty("answers").GetArrayLength());
        Assert.Equal(Answers()[1].Text, request.RootElement.GetProperty("answers")[1].GetProperty("text").GetString());
        Assert.False(request.RootElement.TryGetProperty("browserEvents", out _));
    }

    private sealed class Model : IInternalInterviewLanguageModel
    {
        public int Calls; public string Prompt = "";
        public Task<JsonElement> GenerateAsync(string prompt, string instruction, CancellationToken ct)
        { Calls++; Prompt = prompt; return Task.FromResult(Response()); }
    }
}
