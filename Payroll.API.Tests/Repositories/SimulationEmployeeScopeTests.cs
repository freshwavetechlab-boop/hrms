using Payroll.API.Repositories;

namespace Payroll.API.Tests.Repositories;

public class SimulationEmployeeScopeTests
{
    [Fact]
    public void EffectiveAssignmentWinsOverUnrelatedTemplates()
    {
        Assert.True(PayRunRepository.MatchesSimulationTemplate("effective-october", null, "effective-october"));
        Assert.False(PayRunRepository.MatchesSimulationTemplate("effective-october", null, "current-master"));
    }

    [Fact]
    public void LocationPartsReplaceMasterAssignmentAndAllPartsRemainSelectable()
    {
        var parts = new[] { "mumbai", "delhi" };
        Assert.True(PayRunRepository.MatchesSimulationTemplate("old-master", parts, "mumbai"));
        Assert.True(PayRunRepository.MatchesSimulationTemplate("old-master", parts, "delhi"));
        Assert.False(PayRunRepository.MatchesSimulationTemplate("old-master", parts, "old-master"));
        Assert.False(PayRunRepository.MatchesSimulationTemplate("old-master", [], "old-master"));
    }

    [Theory]
    [InlineData(null, "2026-10")]
    [InlineData("template", null)]
    [InlineData("template", "2026-13")]
    [InlineData("template", "2026-1")]
    public void PartialOrInvalidScopesFailClosed(string? template, string? month) =>
        Assert.Throws<InvalidOperationException>(() => PayRunRepository.ValidateSimulationEmployeeScope(1, template, month));

    [Fact]
    public void ExactValidMonthIsAccepted() => PayRunRepository.ValidateSimulationEmployeeScope(1, "template", "2026-10");
}
