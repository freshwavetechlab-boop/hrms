using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class SelectedClientDashboardTests
{
    private sealed class Lookup : ISelectedClientOwnershipLookup
    {
        public Task<int?> ClientIdAsync(ScopeOwnershipReference reference, CancellationToken cancellationToken = default) =>
            Task.FromResult<int?>(reference.Value switch { "1" => 20, "2" => 21, "3" => 0, _ => null });
    }

    [Fact]
    public async Task EveryApprovalAggregateReceivesOnlyCurrentClientTaskIds()
    {
        var allowed = await DashboardRepository.FilterApprovalTaskIdsAsync([1, 2, 3, 4, 1, 0], 20, new Lookup());
        Assert.Equal(new long[] { 1 }, allowed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task InvalidClientNeverReturnsAnyPersonalTask(int clientId) =>
        Assert.Empty(await DashboardRepository.FilterApprovalTaskIdsAsync([1, 2], clientId, new Lookup()));
}
