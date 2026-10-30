using Microsoft.Extensions.Configuration;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class DashboardPeriodTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void OmittedMonthKeepsCurrentMonthDefault(string? month) =>
        Assert.Equal("2026-10", DashboardRepository.ResolveMonth(month, new DateTime(2026, 10, 28)));

    [Theory]
    [InlineData("2026-09")]
    [InlineData("2026-10")]
    [InlineData("2027-01")]
    [InlineData("2024-02")]
    public void ExplicitMonthIsNotReplacedByToday(string month) =>
        Assert.Equal(month, DashboardRepository.ResolveMonth(month, new DateTime(2026, 10, 28)));

    [Theory]
    [InlineData("2026-00")]
    [InlineData("2026-13")]
    [InlineData("2026-9")]
    [InlineData("26-09")]
    [InlineData("2026-09-01")]
    [InlineData(" 2026-09")]
    [InlineData("0000-09")]
    [InlineData("2026-09 OR 1=1")]
    public async Task InvalidMonthIsRejectedBeforeOpeningADatabase(string month)
    {
        var repository = new DashboardRepository(new ConfigurationBuilder().Build());
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => repository.GetAsync(11, new AuthUser(), month));
        Assert.Equal("Select a valid dashboard month in yyyy-MM format.", exception.Message);
    }

    [Theory]
    [InlineData(null, null, 0)]
    [InlineData(null, -11, 0)]
    [InlineData(null, 42, 42)]
    [InlineData(11, null, 11)]
    [InlineData(11, 0, 11)]
    [InlineData(11, 42, 11)]
    public void MonthSelectionDoesNotChangeExistingClientScope(int? boundClientId, int? requestedClientId, int expected) =>
        Assert.Equal(expected, DashboardRepository.ResolveClientId(new AuthUser { ClientId = boundClientId }, requestedClientId));

    [Fact]
    public void PermissionVisibilityDoesNotBecomePayrollAccessThroughMonthSelection()
    {
        Assert.False(DashboardAccess.For(new AuthUser { Permissions = ["reports.view"] }).Payroll);
        Assert.True(DashboardAccess.For(new AuthUser { Permissions = ["payroll.run"] }).Payroll);
        Assert.True(DashboardAccess.For(new AuthUser { Permissions = ["security.manage"] }).Payroll);
    }
}
