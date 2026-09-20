using Microsoft.Extensions.Configuration;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class InternalInterviewRuntimeTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void Admission_and_maintenance_are_separate_without_enabling_the_feature(bool enabled, bool drain, bool maintenance)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["InternalInterviews:Enabled"] = enabled.ToString(), ["InternalInterviews:DrainMode"] = drain.ToString(),
            ["InternalInterviews:MaintenanceEnabled"] = maintenance.ToString()
        }).Build();
        var state = InternalInterviewRuntimeState.Read(config);
        Assert.Equal(enabled && !drain, state.AcceptingNewSessions);
        Assert.Equal(enabled || maintenance, state.Maintenance);
        if (state.AcceptingNewSessions) state.RequireAdmission();
        else Assert.Equal(503, Assert.Throws<InternalInterviewException>(state.RequireAdmission).StatusCode);
    }

    [Fact]
    public void Existing_install_without_feature_configuration_has_no_new_maintenance_work()
    {
        var state = InternalInterviewRuntimeState.Read(new ConfigurationBuilder().Build());
        Assert.False(state.Enabled); Assert.False(state.Maintenance); Assert.False(state.AcceptingNewSessions);
    }
}
