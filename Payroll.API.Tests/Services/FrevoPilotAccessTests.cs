using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class FrevoPilotAccessTests
{
    [Theory]
    [InlineData(null, "super_admin", true, true)]
    [InlineData(20, "super_admin", true, false)]
    [InlineData(null, "system_admin", true, false)]
    [InlineData(20, "client_admin", true, false)]
    [InlineData(null, "super_admin", false, false)]
    [InlineData(null, "employee", true, false)]
    public void GlobalActiveSuperAdminOnly(int? clientId, string role, bool active, bool expected)
    {
        var actor = new AuthUser { Id = 3, IsActive = active, ClientId = clientId, Roles = [role], Permissions = ["security.manage", "settings.manage"] };
        Assert.Equal(expected, FrevoPilotAnalyticsService.CanUse(actor));
    }

    [Fact]
    public void ProviderQuotaErrorRetainsActionableDetailsWithoutAccountIdentifiers()
    {
        var result = RecruitmentAiScoringService.AnalyticsProviderFailure("Groq returned HTTP 429: org_private tokens per day (TPD). Please try again in 8m48.5s.", "ProviderError");
        Assert.Contains("HTTP 429", result.Message);
        Assert.Contains("tokens per day", result.Message);
        Assert.DoesNotContain("org_private", result.Message);
        Assert.Equal(TimeSpan.FromSeconds(528.5), result.Cooldown);
    }

    [Fact]
    public void ProviderCooldownIsBoundedAndDoesNotEchoRawErrors()
    {
        var result = RecruitmentAiScoringService.AnalyticsProviderFailure("HTTP 429 secret-key try again in 9999h", "ProviderError");
        Assert.Equal(TimeSpan.FromHours(1), result.Cooldown);
        Assert.DoesNotContain("secret-key", result.Message);
    }
}
