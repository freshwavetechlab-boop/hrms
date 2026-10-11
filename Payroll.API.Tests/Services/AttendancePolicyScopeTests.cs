using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public class AttendancePolicyScopeTests
{
    [Theory]
    [InlineData(0, null, null)]
    [InlineData(999, " Engineering ", " Developer ")]
    public void AllScopeIncludesUnassignedLocationsAndBlankMasters(int location, string? department, string? designation)
    {
        Assert.True(LeaveAttendanceRepository.MatchesAttendancePolicyScope(location, department, designation, [], [], []));
    }

    [Fact]
    public void ExplicitFiltersMatchTrimmedValuesIgnoringCase()
    {
        Assert.True(LeaveAttendanceRepository.MatchesAttendancePolicyScope(2, " Engineering ", " Developer ", [1, 2], ["engineering"], ["DEVELOPER"]));
    }

    [Theory]
    [InlineData(0, "Engineering", "Developer")]
    [InlineData(2, "Finance", "Developer")]
    [InlineData(2, "Engineering", "Manager")]
    public void ExplicitFiltersRejectEmployeesOutsideScope(int location, string department, string designation)
    {
        Assert.False(LeaveAttendanceRepository.MatchesAttendancePolicyScope(location, department, designation, [2], ["Engineering"], ["Developer"]));
    }
}
