using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public class FrevoPilotFailureClassificationTests
{
    [Theory]
    [InlineData("LocalBusy", "", "busy")]
    [InlineData("ProviderError", "HTTP 429 private-account", "busy")]
    [InlineData("ProviderError", "HTTP 504 private-account", "gateway deadline expired")]
    [InlineData("TimedOut", "private-account", "HRMS deadline")]
    [InlineData("InputTooLarge", "private-account", "input/context limit")]
    [InlineData("OutputTruncated", "private-account", "output-token limit")]
    [InlineData("InvalidResponse", "private-account", "invalid JSON")]
    [InlineData("ConfigurationError", "private-account", "configuration is invalid")]
    [InlineData("TransportError", "Local LLM transport failed (ConnectionError). secret-api-key", "transport failed (ConnectionError)")]
    [InlineData("ProviderError", "Local LLM authentication failed. secret-api-key", "authentication failed")]
    [InlineData("ProviderError", "Local LLM rejected the input/context size. secret-api-key", "input/context limit")]
    public void LocalFailuresHaveSpecificSafeActionsWithoutCloudQuotaAdvice(string status, string error, string expected)
    {
        var failure = RecruitmentAiScoringService.AnalyticsProviderFailure(error, status, true);
        var message = RecruitmentAiScoringService.AnalyticsFailureSummary([failure.Message]);
        Assert.Contains(expected, message);
        Assert.DoesNotContain("quota/reset window", message);
        Assert.DoesNotContain("private-account", message);
        Assert.DoesNotContain("secret-api-key", message);
        Assert.Contains("No business records were changed", message);
    }

    [Fact]
    public void CloudRateLimitAloneReceivesQuotaResetAdvice()
    {
        var quota = RecruitmentAiScoringService.AnalyticsProviderFailure("HTTP 429 org-hidden tokens per day. Try again in 8m48.5s", "ProviderError");
        Assert.Contains("quota/reset window", quota.Message);
        Assert.Contains("tokens per day", quota.Message);
        Assert.Contains("8m48.5s", quota.Message);
        Assert.Equal(TimeSpan.FromSeconds(528.5), quota.Cooldown);
        Assert.DoesNotContain("org-hidden", quota.Message);
        var gateway = RecruitmentAiScoringService.AnalyticsProviderFailure("HTTP 504", "ProviderError");
        Assert.DoesNotContain("quota", gateway.Message);
        Assert.Contains("gateway deadline", gateway.Message);
    }

    [Fact]
    public void UnknownStatusesAndErrorsAreNotEchoed()
    {
        var failure = RecruitmentAiScoringService.AnalyticsProviderFailure("candidate details and secret-key", "raw-sensitive-status", true);
        Assert.DoesNotContain("candidate details", failure.Message);
        Assert.DoesNotContain("secret-key", failure.Message);
        Assert.DoesNotContain("raw-sensitive-status", failure.Message);
        Assert.Contains("connectivity", failure.Message);
    }

    [Fact]
    public void ConfigurationAndInputFailuresDoNotCreateMisleadingCooldowns()
    {
        foreach (var status in new[] { "InputTooLarge", "OutputTruncated", "InvalidResponse", "ConfigurationError" })
            Assert.Equal(TimeSpan.Zero, RecruitmentAiScoringService.AnalyticsProviderFailure("", status, true).Cooldown);
        Assert.Contains("No active AI model", RecruitmentAiScoringService.AnalyticsFailureSummary([]));
    }

    [Fact]
    public void TransportClassificationNeverEchoesUnknownProviderOrHostDetails()
    {
        var local = RecruitmentAiScoringService.AnalyticsProviderFailure("private-host (candidate-secret)", "TransportError", true);
        Assert.Contains("(Unknown)", local.Message);
        Assert.DoesNotContain("private-host", local.Message);
        Assert.DoesNotContain("candidate-secret", local.Message);
        Assert.DoesNotContain("Local LLM transport", RecruitmentAiScoringService.AnalyticsProviderFailure("(ConnectionError)", "TransportError", false).Message);
    }
}
