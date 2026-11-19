using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class BeforeJoiningPayrollErrorTests
{
    [Fact]
    public void Earlier_month_has_an_explicit_English_joining_date_error()
    {
        Assert.Equal("Payroll cannot be generated for September 2026. Employee SYNTH-71 joined on 1 October 2026.",
            EmployeeEmploymentRules.BeforeJoiningPayrollError("2026-10-01","2026-09","SYNTH-71"));
    }
    [Theory]
    [InlineData("2026-10-01","2026-10")][InlineData("2026-10-31","2026-10")]
    [InlineData("2026-09-01","2026-10")][InlineData("","2026-09")]
    public void Joined_in_or_before_month_and_unknown_dates_do_not_acquire_a_false_error(string joining,string month)
        => Assert.Null(EmployeeEmploymentRules.BeforeJoiningPayrollError(joining,month,"SYNTH-71"));
}
