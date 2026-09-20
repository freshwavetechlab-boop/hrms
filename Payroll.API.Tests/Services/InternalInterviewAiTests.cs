using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class InternalInterviewAiTests
{
    [Fact]
    public void Literal_question_dedup_covers_human_handoff_and_duplicate_bank_ids_without_semantic_guessing()
    {
        InternalInterviewEvent[] events = [new() { Kind = "question", Source = "panel", Text = "  Explain\tAPI\r\nisolation? " },
            new() { Kind = "note", Text = "Explain C++?" }];
        Assert.True(InternalInterviewRepository.AlreadyAsked(events, "explain API isolation?"));
        Assert.False(InternalInterviewRepository.AlreadyAsked(events, "Explain API authentication?"));
        Assert.False(InternalInterviewRepository.AlreadyAsked(events, "Explain C++?"));
        Assert.NotEqual(InternalInterviewRepository.QuestionIdentity("Explain C++?"), InternalInterviewRepository.QuestionIdentity("Explain C#?"));
        Assert.Equal(new[] { "How do you test?", "Show an example." }, InternalInterviewRepository.ApprovedFollowUps(new() {
            FollowUpInstructions = "How do you test?\n  HOW   DO YOU TEST?\nShow an example."
        }));
    }
    private static readonly InternalInterviewEvent Question = new() { Id = 1, InterviewId = 7, Kind = "question", Text = "How did you validate the API?" };
    private static readonly InternalInterviewEvent Answer = new() { Id = 2, InterviewId = 7, Kind = "answer", QuestionEventId = 1, Text = "I tested authentication and verified HTTP 403 for a different client." };
    [Fact]
    public void Review_keeps_host_bound_evidence_and_never_model_supplied_ids_scores_or_decisions()
    {
        using var body = JsonDocument.Parse("""{"hire":true,"score":100,"questionEventId":999,"observations":[{"kind":"skillEvidence","note":"Mentions authorization testing.","evidenceQuote":"verified HTTP 403 for a different client"}]}""");
        var review = Assert.Single(InternalInterviewAiService.ValidateReview(body.RootElement, Question, Answer));
        Assert.Equal(Question.Id, review.QuestionEventId); Assert.Equal(Answer.Id, review.AnswerEventId);
        var serialized = JsonSerializer.Serialize(review);
        Assert.DoesNotContain("hire", serialized); Assert.DoesNotContain("score", serialized); Assert.DoesNotContain("999", serialized);
    }
    [Theory]
    [InlineData("honesty", "I tested authentication")]
    [InlineData("reject", "I tested authentication")]
    [InlineData("skillEvidence", "I have 20 years of experience")]
    [InlineData("skillEvidence", "")]
    public void Unsupported_categories_and_fabricated_evidence_are_rejected(string kind, string evidenceQuote)
    {
        var body = JsonSerializer.SerializeToElement(new { observations = new[] { new { kind, note = "Unsupported claim", evidenceQuote } } });
        Assert.Throws<InternalInterviewException>(() => InternalInterviewAiService.ValidateReview(body, Question, Answer));
    }
    [Fact]
    public async Task Browser_and_other_interview_evidence_never_reaches_model()
    {
        var fake = new ForbiddenModel(); var service = new InternalInterviewAiService(fake);
        await Assert.ThrowsAsync<InternalInterviewException>(() => service.ReviewAnswerAsync(Question, new() { InterviewId = 7, Id = 3, Kind = "blur", QuestionEventId = 1 }, "test criteria", default));
        await Assert.ThrowsAsync<InternalInterviewException>(() => service.ReviewAnswerAsync(Question, new() { InterviewId = 8, Id = 3, Kind = "answer", QuestionEventId = 1 }, "test criteria", default));
        Assert.Equal(0, fake.Calls);
    }
    private sealed class ForbiddenModel : IInternalInterviewLanguageModel
    {
        public int Calls;
        public Task<JsonElement> GenerateAsync(string prompt, string instruction, CancellationToken ct) { Calls++; throw new InvalidOperationException("Must not call model."); }
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("3")]
    public void Non_object_responses_are_controlled_failures_not_worker_crash_loops(string json)
    {
        using var response = JsonDocument.Parse(json);
        var error = Assert.Throws<InternalInterviewException>(() => InternalInterviewAiService.ValidateReview(response.RootElement, Question, Answer));
        Assert.Equal(502, error.StatusCode);
    }
}
