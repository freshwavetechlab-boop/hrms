using Payroll.API.Models;

namespace Payroll.API.Services;

// This decision runs only after the calculation transaction has unwound. The
// repository supplies a fresh locked outcome, never the pre-calculation snapshot.
internal static class PayRunProcessingOutcome
{
    internal sealed record Snapshot(PayRun? Run, long ResultEvidenceCount);

    internal static async Task<PayRun> RecoverAsync(Func<Task<Snapshot>> readCurrent,
        Func<PayRun, Task<PayRun>> recordRolledBackFailure)
    {
        // If verification itself fails, do not turn an unknown commit outcome into
        // Failed, consume an adjustment again, or start another calculation.
        var current = await readCurrent();
        var run = current.Run ?? throw Uncertain();
        if (run.Status is "Draft" or "Pending Approval" or "Approved" or "Partially Paid" or "Paid" or "Failed")
            return run;
        if (run.Status != "Processing" || current.ResultEvidenceCount != 0 || run.PayrollCost != 0 || run.NetPay != 0)
            throw Uncertain();
        return await recordRolledBackFailure(run);
    }

    private static SelectedPayRunConflictException Uncertain() => new(
        "Payroll processing outcome could not be established as rolled back. Existing status, outputs and adjustments are retained. Refresh this exact run and inspect its persisted result before any retry.");
}
