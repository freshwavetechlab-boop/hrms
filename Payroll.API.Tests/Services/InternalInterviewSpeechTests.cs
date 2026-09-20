using System.Text;
using Microsoft.Extensions.Configuration;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class InternalInterviewSpeechTests
{
    [Theory]
    [InlineData("transcribe")]
    [InlineData("synthesize")]
    public async Task Speech_is_off_by_default_even_with_credentials_without_affecting_video(string operation)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["InternalInterviews:Enabled"] = "true", ["InternalInterviews:SpeechBaseUrl"] = "http://127.0.0.1:8094",
            ["InternalInterviews:SpeechApiKey"] = "synthetic-private-speech-key-for-tests-only"
        }).Build();
        using var service = new InternalInterviewSpeechService(config, new NoCalls(), new EngineRuntimeMonitor());
        using var content = new StringContent("synthetic");
        Assert.Equal(503, (await Assert.ThrowsAsync<InternalInterviewException>(() => service.SendAsync(7, operation, content, "en", default))).StatusCode);
        Assert.True(InternalInterviewRuntimeState.Read(config).AcceptingNewSessions);
        config["InternalInterviews:SpeechEnabled"] = "true";
        service.RequireEnabled();
        config["InternalInterviews:SpeechEnabled"] = "false";
        Assert.Throws<InternalInterviewException>(service.RequireEnabled);
    }
    private sealed class NoCalls : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("Disabled speech must not send HTTP requests.");
    }
    [Fact]
    public void Voice_processing_grace_is_bounded_and_does_not_depend_on_retries()
    {
        var asked = new DateTime(2026, 9, 20, 10, 0, 0, DateTimeKind.Utc);
        Assert.Equal(asked.AddSeconds(130), InternalInterviewRepository.AnswerDeadline(asked, 120, null));
        Assert.Equal(asked.AddSeconds(240), InternalInterviewRepository.AnswerDeadline(asked, 120, asked.AddSeconds(120)));
        Assert.Equal(asked.AddSeconds(130), InternalInterviewRepository.AnswerDeadline(asked, 120, asked));
    }
    [Theory]
    [InlineData("{\"text\":\"A locally transcribed answer.\"}", true)]
    [InlineData("{\"text\":\"\"}", false)]
    [InlineData("{\"text\":true}", false)]
    [InlineData("not json", false)]
    public void Transcript_contract_never_treats_empty_or_malformed_output_as_success(string json, bool valid)
    {
        if (valid) Assert.Equal("A locally transcribed answer.", InternalInterviewSpeechService.ParseTranscript(Encoding.UTF8.GetBytes(json)));
        else Assert.Throws<Payroll.API.Models.InternalInterviewException>(() => InternalInterviewSpeechService.ParseTranscript(Encoding.UTF8.GetBytes(json)));
    }
    [Fact]
    public async Task Streaming_speech_upload_limit_is_enforced_even_with_incorrect_content_length()
    {
        using var stream = new MemoryStream(new byte[100]);
        await Assert.ThrowsAsync<Payroll.API.Models.InternalInterviewException>(() => InternalInterviewSpeechService.ReadBoundedAsync(stream, 80, CancellationToken.None));
    }
}
