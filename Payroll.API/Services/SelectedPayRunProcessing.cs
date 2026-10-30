using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Payroll.API.Models;

namespace Payroll.API.Services;

// Endpoint metadata: configured workflows are a blocker, never bypassed to run a draft.
public sealed class RequireIsolatedPayrollProcessing { }

// Same global-super-admin + payroll.run pattern as the existing endpoints,
// with an explicit live active-user check. Scoped administrators retain their
// normal workflow; this restricted operator action grants no new access.
internal static class SelectedPayRunAuthorization
{
    internal static bool CanRequest(AuthUser? user) => user is { Id: > 0, IsActive: true, ClientId: null }
        && user.Roles?.Contains("super_admin", StringComparer.OrdinalIgnoreCase) == true
        && user.Permissions?.Contains("payroll.run", StringComparer.OrdinalIgnoreCase) == true;

    internal static bool CanAccess(AuthUser? user, int clientId) => clientId > 0 && CanRequest(user);
}


public sealed class PayRunAttemptConflictException(int payRunId, string status)
    : InvalidOperationException($"Existing payroll run {payRunId} ({status}) is retained. Open and review it; creating a new request cannot delete or replace it.")
{
    public int PayRunId { get; } = payRunId;
    public string RunStatus { get; } = status;
}

public sealed class SelectedPayRunConflictException(string message) : InvalidOperationException(message);

internal sealed record SelectedRunRuntime(bool MigrationsDisabled, bool WorkersDisabled, bool OutboundSuppressed)
{
    internal static SelectedRunRuntime Read(IConfiguration configuration) => new(
        !configuration.GetValue<bool>("Database:AutoMigrate"),
        !configuration.GetValue("BackgroundWorkers:Enabled", true),
        configuration.GetValue<bool>("OutboundDelivery:Suppressed"));
    internal bool Safe => MigrationsDisabled && WorkersDisabled && OutboundSuppressed;
}

internal sealed class SelectedRunState
{
    public required PayRun Run { get; init; }
    public CreatePayRunRequest? Request { get; init; }
    public bool ClientActive { get; init; }
    public int[] EligibleEmployeeIds { get; init; } = [];
    public List<SelectedPayRunEmployee> Employees { get; init; } = [];
    public int[] AlreadyCoveredEmployeeIds { get; init; } = [];
    public int[] OtherRunningRunIds { get; init; } = [];
    public long FinancialEvidenceCount { get; init; }
    public long AdjustmentCount { get; init; }
    public List<SelectedPayRunAdjustmentReview> AdjustmentReviews { get; init; } = [];
    public List<string> AdjustmentIssues { get; init; } = [];
}

internal static class SelectedPayRunPolicy
{
    internal static SelectedPayRunProcessingPreview Preview(SelectedRunState state, SelectedRunRuntime runtime)
    {
        var run = state.Run;
        var request = state.Request;
        var ids = request?.IncludedEmployeeIds ?? [];
        var preview = new SelectedPayRunProcessingPreview
        {
            PayRunId = run.Id, ClientId = run.ClientId, ClientName = run.ClientName,
            PayPeriod = run.PayPeriod, RunCode = run.RunCode, Status = run.Status,
            EmployeeCount = ids.Distinct().Count(), Fingerprint = Fingerprint(state, runtime),
            Employees = state.Employees.Where(employee => ids.Contains(employee.EmployeeId)).OrderBy(employee => employee.EmployeeCode).ToList(),
            Adjustments=state.AdjustmentReviews.OrderBy(a=>a.AdjustmentId).ToList()
        };
        var blockers = preview.Blockers;
        if (!runtime.Safe) blockers.Add("Restricted processing requires migrations and global background workers disabled and outbound delivery suppressed. Ask the operator to use the reviewed local runtime; no settings are changed here.");
        if (!state.ClientActive) blockers.Add("The client is missing or inactive.");
        if (!SelectedPayRunDraft.IsProcessableStatus(run.Status)) blockers.Add(run.Status switch
        {
            "Draft" => "This run already has a Draft result. Review or compare it; it will not be recalculated or replaced.",
            "Failed" => "This failed attempt and its diagnostics are retained. Review the failure before a separately scoped correction; automatic retry is unavailable.",
            "Processing" => "This run is already processing. Refresh its state; do not submit another calculation.",
            _ => "Only an empty held or Queued regular run can use restricted processing. Existing approval or payment history is preserved."
        });
        if (request is null || request.IncludedEmployeeIds is null || request.ExcludedEmployeeIds is null || request.AdjustmentIds is null)
            blockers.Add("The saved queue request is missing or invalid; it cannot be reconstructed from assumptions.");
        else
        {
            if (request.ClientId != run.ClientId || request.PayPeriod != run.PayPeriod)
                blockers.Add("The saved request does not match this run's client and pay period.");
            if (run.RunType != "Regular" || request.RunType != "Regular")
                blockers.Add("This restricted path supports regular payroll only; off-cycle adjustments require their normal reviewed workflow.");
            if (!Regex.IsMatch(run.PayPeriod, @"^\d{4}-(0[1-9]|1[0-2])$") || request.TotalWorkingDays is < 1 or > 31)
                blockers.Add("Review the saved pay period and working-day divisor.");
            if (ids.Count == 0 || ids.Any(id => id <= 0) || ids.Distinct().Count() != ids.Count || request.ExcludedEmployeeIds.Count > 0)
                blockers.Add("An explicit, unique employee selection is required; implicit all-employees or mixed inclusion/exclusion is blocked.");
            if (ids.Except(state.EligibleEmployeeIds).Any()) blockers.Add("Selected employees are missing, outside this client or ineligible in this period. Review the selection; no partial scope will be processed.");
            if (request.AdjustmentIds.Count > 0 || state.AdjustmentCount > 0 || request.SelectedAdjustmentReviews is { Count: > 0 })
            {
                if(state.AdjustmentIssues.Count>0) blockers.AddRange(state.AdjustmentIssues);
                else
                {
                    try
                    {
                        if(state.AdjustmentCount!=request.AdjustmentIds.Count)throw new SelectedPayRunConflictException("The applicable adjustment set changed; refresh source review.");
                        SelectedPayRunAdjustments.RequireFrozen(request,state.AdjustmentReviews);
                    }
                    catch(SelectedPayRunConflictException error){blockers.Add(error.Message);}
                }
            }
        }
        if (state.AlreadyCoveredEmployeeIds.Length > 0) blockers.Add($"{state.AlreadyCoveredEmployeeIds.Length} selected employees are already covered by another regular run. Review existing results; this request will not silently remove employees.");
        if (state.OtherRunningRunIds.Length > 0) blockers.Add("Another regular run for this client and month is queued or processing. Review it before processing this scope.");
        if (state.FinancialEvidenceCount > 0 || run.PayrollCost != 0 || run.NetPay != 0 || run.ProcessingStartedAt.HasValue || run.ProcessingCompletedAt.HasValue)
            blockers.Add("This run contains calculation, adjustment, validation or processing evidence. Existing evidence cannot be cleared through restricted processing.");
        preview.CanProcess = blockers.Count == 0;
        foreach(var adjustment in preview.Adjustments)
            preview.Effects.Add($"Apply only reviewed EXCEL_SETUP adjustment {adjustment.AdjustmentId}: {adjustment.ComponentCode}, {adjustment.Category}, {adjustment.Amount:0.00}, taxable={adjustment.Taxable}; retain its existing amount/classification and bind it once to this Draft. No new adjustment is created.");
        return preview;
    }

    internal static void RequireReviewed(SelectedRunState state, SelectedRunRuntime runtime, SelectedPayRunProcessingRequest command)
    {
        if (!command.Confirmed) throw new SelectedPayRunConflictException("Review and explicitly confirm this exact run before processing.");
        if (command.ClientId != state.Run.ClientId || command.PayPeriod != state.Run.PayPeriod)
            throw new SelectedPayRunConflictException("Client or period does not match this run. Refresh the selected run.");
        var preview = Preview(state, runtime);
        if (string.IsNullOrEmpty(command.ExpectedFingerprint) || command.ExpectedFingerprint != preview.Fingerprint)
            throw new SelectedPayRunConflictException("Run scope or evidence changed after review. Refresh the preview and confirm again.");
        if (!preview.CanProcess) throw new SelectedPayRunConflictException(string.Join(" ", preview.Blockers));
    }

    private static string Fingerprint(SelectedRunState state, SelectedRunRuntime runtime) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    {
        state.Run.Id, state.Run.ClientId, state.Run.PayPeriod, state.Run.Status, state.Run.RunCode,
        state.Run.UpdatedAt, state.Run.RequestJson, state.Run.PayrollCost, state.Run.NetPay,
        state.Run.ProcessingStartedAt, state.Run.ProcessingCompletedAt,
        state.ClientActive, state.FinancialEvidenceCount, state.AdjustmentCount,
        adjustmentReviews=state.AdjustmentReviews.OrderBy(a=>a.AdjustmentId).ToArray(),state.AdjustmentIssues,
        eligible = state.EligibleEmployeeIds.Order().ToArray(), covered = state.AlreadyCoveredEmployeeIds.Order().ToArray(),
        running = state.OtherRunningRunIds.Order().ToArray(), runtime
    }))));
}

internal static class SelectedPayRunGate
{
    // Bounded memory. The existing database connection lock additionally covers worker/other-process races.
    private static readonly SemaphoreSlim[] Gates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    internal static async Task<T> RunAsync<T>(int id, Func<Task<T>> action)
    {
        if (id <= 0) throw new SelectedPayRunConflictException("Select a valid payroll run.");
        var gate = Gates[id % Gates.Length];
        if (!await gate.WaitAsync(0)) throw new SelectedPayRunConflictException("A processing request is already in progress. Refresh this run before any retry.");
        try { return await action(); }
        finally { gate.Release(); }
    }
}
