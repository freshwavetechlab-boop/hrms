using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class FrevoPilotPlanIntegrationTests
{
    [Theory]
    [InlineData("{\"userQuestion\":\"Count employees\"}", "{\"properties\":{\"sql\":{}}}", true, false)]
    [InlineData("{\"failedPlan\":{}}", "{\"properties\":{\"sql\":{}}}", true, true)]
    [InlineData("{\"validationError\":\"bad column\"}", "{\"properties\":{\"sql\":{}}}", true, true)]
    [InlineData("{\"question\":\"Count employees\"}", "{\"properties\":{\"summary\":{}}}", false, false)]
    [InlineData("not-json", "{}", false, false)]
    public void MemoryOnlyParticipatesInPlanning(string prompt, string schema, bool planning, bool repair)
    {
        Assert.Equal((planning, repair), RecruitmentAiScoringService.AnalyticsMemoryRequestKind(prompt, schema));
    }

    private static PortableIntegrationCredentialProtector Protector(string key = "synthetic-plan-test-key") => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["IntegrationCredentialEncryption:MasterKey"] = key }).Build(),
        new EphemeralDataProtectionProvider(), NullLogger<PortableIntegrationCredentialProtector>.Instance);

    [Fact]
    public void PlanPersistenceUsesPortablePurposeSeparatedEncryption()
    {
        var first = Protector();
        var text = "{\"sql\":\"SELECT COUNT(*) FROM employees\",\"parameters\":[]}";
        var encrypted = first.ProtectVerifiedPlan(text);
        Assert.DoesNotContain("SELECT", encrypted);
        Assert.Equal(text, Protector().UnprotectVerifiedPlan(encrypted));
        Assert.Null(Protector("different-test-key").UnprotectVerifiedPlan(encrypted));
        Assert.Null(first.UnprotectVerifiedPlan(first.ProtectAi(text)));
        Assert.Null(first.UnprotectVerifiedPlan(first.ProtectStorage(text)));
        Assert.False(first.TryUnprotectAi(encrypted, out _));
        Assert.False(first.TryUnprotectStorage(encrypted, out _));
        Assert.Null(first.UnprotectVerifiedPlan("frevopilot-plan:v1:not-base64"));
    }
}
