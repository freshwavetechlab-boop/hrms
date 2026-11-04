using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class ResumeWarmupContractTests
{
    [Fact]
    public async Task ServerWarmupMatchesTheExactLivePrefixWithoutCandidateOrClientFacts()
    {
        using var artifact = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repository(), "deploy", "local-llm", "resume-warmup.json")));
        Assert.Equal(1, artifact.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(LocalLlmProtocol.ResumePromptVersion, artifact.RootElement.GetProperty("promptVersion").GetString());
        var warm = artifact.RootElement.GetProperty("request");
        using var live = LocalLlmProtocol.CreateRequest(new RecruitmentAiScoringSettings
        { EndpointUrl = "https://example.invalid/llm-api.php", ModelName = "hrms-local" },
            "synthetic-not-a-real-key", LocalLlmProtocol.ResumeEvidencePrompt("UniqueCandidateEvidence"), LocalLlmProtocol.ResumeInstruction);
        using var body = JsonDocument.Parse(await live.Content!.ReadAsStringAsync());
        Assert.Equal(body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString(), warm.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Equal("UNTRUSTED RESUME:", warm.GetProperty("messages")[1].GetProperty("content").GetString()!.Trim());
        Assert.Equal(2, warm.GetProperty("messages").GetArrayLength());
        Assert.Equal(1, warm.GetProperty("max_tokens").GetInt32());
        Assert.Equal(256, body.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal(0, warm.GetProperty("temperature").GetInt32());
        Assert.False(warm.GetProperty("stream").GetBoolean());
        Assert.False(warm.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
        Assert.Equal("json_object", warm.GetProperty("response_format").GetProperty("type").GetString());
        Assert.DoesNotContain("UniqueCandidateEvidence", artifact.RootElement.GetRawText());
    }

    private static string Repository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "deploy", "local-llm"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository deployment contracts unavailable.");
    }
}
