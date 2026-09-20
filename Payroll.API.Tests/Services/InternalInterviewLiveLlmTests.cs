using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Payroll.API.Models;
using Payroll.API.Services;
using Xunit.Abstractions;

namespace Payroll.API.Tests.Services;

// Explicit opt-in synthetic HTTP validation. No HRMS/website DB, model switch or infrastructure writes.
public sealed class InternalInterviewLiveLlmTests(ITestOutputHelper output)
{
    [LiveInterviewModelFact]
    public async Task Existing_gateway_handles_bounded_interview_followup_and_grounded_review()
    {
        var path = Environment.GetEnvironmentVariable("HRMS_INTERNAL_INTERVIEW_LIVE_LLM_ENV_PATH")!;
        var match = Regex.Match(await File.ReadAllTextAsync(path), "(?im)^\\s*token\\s*=\\s*([^\\r\\n]+)");
        Assert.True(match.Success, "The supplied private environment file has no token entry.");
        var key = match.Groups[1].Value.Trim().Trim('"', '\'');
        Assert.True(key.Length >= 20, "The private client key is missing.");
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(190) };
        var service = new InternalInterviewAiService(new Gateway(http, key, output));
        var bank = new InterviewQuestion { Skill = "API testing", Question = "How did you validate tenant isolation?", EvaluationCriteria = "Explicitly describes testing a cross-client denial." };
        var question = new InternalInterviewEvent { Id = 1, InterviewId = 7, Kind = "question", Text = bank.Question };
        var answer = new InternalInterviewEvent { Id = 2, InterviewId = 7, Kind = "answer", QuestionEventId = 1, Text = "I tested a different client's employee API and verified HTTP 403. I also tested my own client and received HTTP 200." };
        var next = await service.ChooseFollowUpAsync(bank, question, answer, ["How did you test update requests?"], default);
        Assert.True(next is null or 0);
        var review = await service.ReviewAnswerAsync(question, answer, bank.EvaluationCriteria, default);
        Assert.NotEmpty(review);
        Assert.All(review, item => { Assert.Equal(answer.Id, item.AnswerEventId); Assert.Contains(item.EvidenceQuote, answer.Text, StringComparison.Ordinal); });
        var followup = new InternalInterviewEvent { Id = 3, InterviewId = 7, Kind = "question", Text = "How did you test update requests?" };
        var followupAnswer = new InternalInterviewEvent { Id = 4, InterviewId = 7, Kind = "answer", QuestionEventId = 3, Text = "I attempted to update the other client's record and verified it was unchanged after the HTTP 403 response." };
        var summary = await service.ReviewInterviewAsync([question, followup], [answer, followupAnswer], default);
        Assert.Equal(2, summary.AnswerCount); Assert.NotEmpty(summary.Observations);
        Assert.All(summary.Observations.SelectMany(o => o.Evidence), reference => Assert.Contains(reference.AnswerEventId, new long[] { 2, 4 }));
    }

    private sealed class Gateway(HttpClient http, string key, ITestOutputHelper output) : IInternalInterviewLanguageModel
    {
        public async Task<JsonElement> GenerateAsync(string prompt, string instruction, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://eeslindia.org/llm-api.php");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            request.Content = JsonContent.Create(new { model = "hrms-local", stream = false, temperature = 0, max_tokens = 256,
                response_format = new { type = "json_object" }, messages = new[] { new { role = "system", content = instruction }, new { role = "user", content = prompt } } });
            var watch = Stopwatch.StartNew();
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            output.WriteLine("Synthetic interview {0}: HTTP {1} after {2:F3}s", prompt.Contains("followUps") ? "follow-up" : prompt.Contains("\"answers\"") ? "combined review" : "answer review", (int)response.StatusCode, watch.Elapsed.TotalSeconds);
            Assert.True(response.IsSuccessStatusCode, $"Gateway returned HTTP {(int)response.StatusCode}; no retry or cloud fallback was attempted.");
            var bytes = await InternalInterviewSpeechService.ReadBoundedAsync(await response.Content.ReadAsStreamAsync(ct), 65536, ct);
            using var body = JsonDocument.Parse(bytes);
            var choice = body.RootElement.GetProperty("choices")[0];
            Assert.Equal("stop", choice.GetProperty("finish_reason").GetString());
            using var result = JsonDocument.Parse(choice.GetProperty("message").GetProperty("content").GetString()!);
            output.WriteLine("Completed JSON in {0:F3}s; validation uses actual interview evidence guards.", watch.Elapsed.TotalSeconds);
            return result.RootElement.Clone();
        }
    }
}

public sealed class LiveInterviewModelFactAttribute : FactAttribute
{
    public LiveInterviewModelFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HRMS_INTERNAL_INTERVIEW_LIVE_LLM_ENV_PATH")))
            Skip = "Opt in with a private HRMS_INTERNAL_INTERVIEW_LIVE_LLM_ENV_PATH; sends three synthetic prompts to the existing local gateway.";
    }
}
