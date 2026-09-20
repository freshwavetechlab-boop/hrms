using System.Text;
using System.Text.Json;
using Payroll.API.Models;
using static Payroll.API.Services.InternalInterviewPolicy;

namespace Payroll.API.Services;

public sealed record InterviewSummaryEvidence(long QuestionEventId, long AnswerEventId, DateTime AnsweredAtUtc, string EvidenceQuote);
public sealed record InterviewSummaryObservation(string Kind, string Note, IReadOnlyList<InterviewSummaryEvidence> Evidence);
public sealed record InterviewDraftSummary(int QuestionCount, int AnswerCount, IReadOnlyList<long> UnansweredQuestionEventIds,
    IReadOnlyList<InterviewSummaryObservation> Observations);

public sealed partial class InternalInterviewAiService
{
    // Deliberately one bounded request, not N-squared answer comparisons. Full answers only:
    // an oversized interview fails visibly rather than silently omitting evidence/context.
    public async Task<InterviewDraftSummary> ReviewInterviewAsync(IReadOnlyList<InternalInterviewEvent> questions,
        IReadOnlyList<InternalInterviewEvent> answers, CancellationToken ct)
    {
        ValidateSummaryInput(questions, answers);
        if (answers.Count == 0) return Summary(questions, answers, []);
        var prompt = JsonSerializer.Serialize(new {
            task = "Review this complete question/answer set together. Return up to TWO short grounded observations within 256 output tokens. Prefer a combined interviewSummary and, only if directly supported, a crossAnswerInconsistency needing human clarification; otherwise skillEvidence or needsHumanReview. Quote exact answer substrings. Cross-answer inconsistency requires quotes from TWO DISTINCT answers. Do not call anyone dishonest. An empty list means no supported observation, not approval. No hiring decision or score.",
            questions = questions.Select(q => new { id = q.Id, text = q.Text }),
            answers = answers.Select((a, index) => new { index, questionId = a.QuestionEventId, text = a.Text }),
            output = new { observations = new[] { new { kind = "interviewSummary", note = "short supported observation", evidence = new[] { new { answer = 0, quote = "exact short excerpt" } } } } }
        });
        // Same budget as the saved local provider, including its system preamble, on both adapters.
        Require(Encoding.UTF8.GetByteCount(prompt + Instruction + LocalLlmProtocol.SystemInstruction + "\n") <= LocalLlmProtocol.ContentBytes,
            "Combined interview review exceeds the local model input limit. No partial transcript was analyzed. Use the complete saved timeline and per-answer drafts for human review; a larger validated model context is needed for this combined review.", 422);
        var response = await model.GenerateAsync(prompt, Instruction, ct);
        return ValidateSummary(response, questions, answers);
    }

    internal static InterviewDraftSummary ValidateSummary(JsonElement response, IReadOnlyList<InternalInterviewEvent> questions,
        IReadOnlyList<InternalInterviewEvent> answers)
    {
        ValidateSummaryInput(questions, answers);
        Require(response.ValueKind == JsonValueKind.Object && response.TryGetProperty("observations", out var items)
            && items.ValueKind == JsonValueKind.Array && items.GetArrayLength() <= 2, "Combined AI review did not match the evidence-only contract.", 502);
        var observations = new List<InterviewSummaryObservation>();
        foreach (var item in response.GetProperty("observations").EnumerateArray())
        {
            Require(item.ValueKind == JsonValueKind.Object && item.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String
                && item.TryGetProperty("note", out var note) && note.ValueKind == JsonValueKind.String
                && item.TryGetProperty("evidence", out var refs) && refs.ValueKind == JsonValueKind.Array && refs.GetArrayLength() is > 0 and <= 3,
                "Combined AI review has incomplete evidence.", 502);
            var category = item.GetProperty("kind").GetString()!; var text = item.GetProperty("note").GetString()!;
            Require(category is "interviewSummary" or "skillEvidence" or "needsHumanReview" or "crossAnswerInconsistency",
                "Combined AI review used an unsupported observation category.", 502);
            Require(!string.IsNullOrWhiteSpace(text) && text.Length <= 240, "Combined AI observation is empty or too long.", 502);
            var evidence = new List<InterviewSummaryEvidence>();
            foreach (var reference in item.GetProperty("evidence").EnumerateArray())
            {
                Require(reference.ValueKind == JsonValueKind.Object && reference.TryGetProperty("answer", out var index)
                    && index.ValueKind == JsonValueKind.Number && index.TryGetInt32(out var i) && i >= 0 && i < answers.Count
                    && reference.TryGetProperty("quote", out var quote) && quote.ValueKind == JsonValueKind.String,
                    "Combined AI review referenced unavailable answer evidence.", 502);
                var answer = answers[reference.GetProperty("answer").GetInt32()]; var excerpt = reference.GetProperty("quote").GetString()!;
                Require(!string.IsNullOrWhiteSpace(excerpt) && excerpt.Length <= 160 && answer.Text.Contains(excerpt, StringComparison.Ordinal),
                    "Combined AI evidence was not found in the referenced answer. Human review is required.", 502);
                evidence.Add(new(answer.QuestionEventId!.Value, answer.Id, answer.CreatedAtUtc, excerpt));
            }
            Require(category != "crossAnswerInconsistency" || evidence.Select(e => e.AnswerEventId).Distinct().Count() >= 2,
                "A cross-answer observation needs evidence from at least two distinct answers.", 502);
            observations.Add(new(category, text, evidence));
        }
        return Summary(questions, answers, observations);
    }

    private static void ValidateSummaryInput(IReadOnlyList<InternalInterviewEvent> questions, IReadOnlyList<InternalInterviewEvent> answers)
    {
        Require(questions.All(q => q.Kind == "question") && questions.Select(q => q.Id).Distinct().Count() == questions.Count
            && questions.Select(q => q.InterviewId).Distinct().Count() <= 1
            && answers.Select(a => a.Id).Distinct().Count() == answers.Count
            && answers.All(a => a.Kind == "answer" && questions.Any(q => q.Id == a.QuestionEventId && q.InterviewId == a.InterviewId)),
            "Combined review evidence must be submitted answers and questions from one interview.");
    }

    private static InterviewDraftSummary Summary(IReadOnlyList<InternalInterviewEvent> questions, IReadOnlyList<InternalInterviewEvent> answers,
        IReadOnlyList<InterviewSummaryObservation> observations) => new(questions.Count, answers.Count,
            questions.Where(q => !answers.Any(a => a.QuestionEventId == q.Id)).Select(q => q.Id).ToArray(), observations);
}
