using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public class AttendanceReconciliationGuardTests
{
    [Fact]
    public async Task ClosedHistoricalMismatchDoesNotStopRemainingReconciliation()
    {
        var completed = new List<string>();
        await EssMssRepository.ReconcileOpenLeaveWorkflowRowsAsync([("closed", "Approved"), ("open", "Approved")], (id, status) =>
        {
            if (id == "closed") throw new AttendancePeriodLockedException("Closed payroll period");
            completed.Add(id);
            return Task.CompletedTask;
        });
        Assert.Equal(new[] { "open" }, completed);
    }

    [Fact]
    public async Task UnexpectedFailureIsNotSilentlyTreatedAsAClosedPeriod()
    {
        var failure = new InvalidOperationException("Unexpected reconciliation failure");
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => EssMssRepository.ReconcileOpenLeaveWorkflowRowsAsync(
            [("open", "Approved")], (_, _) => Task.FromException(failure)));
        Assert.Same(failure, actual);
    }
}
