using System.Text;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class InternalInterviewSpeechTests
{
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
