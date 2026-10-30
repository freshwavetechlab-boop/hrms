using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public partial class PayRunRepository
{
    public async Task<SelectedPayRunProcessingPreview?> GetSelectedProcessingPreviewAsync(int id, int clientId, string payPeriod)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await using var transaction=await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,isReadOnly:true);
        var state = await ReadSelectedRunStateAsync(connection, transaction, id, clientId, payPeriod, false);
        await transaction.RollbackAsync();
        return state is null ? null : SelectedPayRunPolicy.Preview(state, SelectedRunRuntime.Read(configuration));
    }

    public Task<PayRun?> ProcessSelectedRunAsync(int id, SelectedPayRunProcessingRequest request, string actor) =>
        SelectedPayRunGate.RunAsync(id, () => ProcessQueuedRunCoreAsync(id, actor, request));

    private async Task<SelectedRunState?> ReadSelectedRunStateAsync(MySqlConnection connection, MySqlTransaction? transaction,
        int id, int clientId, string payPeriod, bool lockRows)
    {
        // GET preview performs SELECTs only. POST repeats this under the existing same-run advisory lock
        // and a transaction, locks the client then the run, and changes only this held/Queued run.
        var active = await connection.ExecuteScalarAsync<int?>("SELECT IsActive FROM clients WHERE Id=@ClientId" + (lockRows ? " FOR UPDATE" : ""), new { ClientId = clientId }, transaction);
        var run = await connection.QueryFirstOrDefaultAsync<PayRun>("SELECT * FROM payruns WHERE Id=@Id AND ClientId=@ClientId AND PayPeriod=@PayPeriod" + (lockRows ? " FOR UPDATE" : ""), new { Id = id, ClientId = clientId, PayPeriod = payPeriod }, transaction);
        if (run is null) return null;
        CreatePayRunRequest? request = null;
        try { request = JsonSerializer.Deserialize<CreatePayRunRequest>(run.RequestJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch (JsonException) { /* Retain invalid requests and return an actionable blocker. */ }
        if (request?.IncludedEmployeeIds is null || request.ExcludedEmployeeIds is null || request.AdjustmentIds is null) request = null;
        var ids = request?.IncludedEmployeeIds?.Where(id => id > 0).Distinct().ToArray() ?? [];
        var employees = ids.Length == 0 ? [] : (await connection.QueryAsync<SelectedPayRunEmployee>(
            "SELECT e.Id AS EmployeeId,e.EmployeeCode,CONCAT_WS(' ',e.FirstName,e.LastName) AS EmployeeName FROM employees e WHERE e.ClientId=@ClientId AND e.Id IN @Ids AND " + EmployeeEmploymentRules.EligibleInMonth("e", "@PayPeriod") + (lockRows ? " FOR UPDATE" : ""),
            new { ClientId = clientId, PayPeriod = payPeriod, Ids = ids }, transaction)).ToList();
        var covered = ids.Length == 0 ? [] : (await connection.QueryAsync<int>(@"
SELECT DISTINCT pe.EmployeeId FROM payrunemployees pe JOIN payruns r ON r.Id=pe.PayRunId
WHERE r.Id<>@Id AND r.ClientId=@ClientId AND r.PayPeriod=@PayPeriod AND r.RunType='Regular'
AND r.Status IN ('Draft','Pending Approval','Approved','Partially Paid','Paid') AND pe.IsSkipped=FALSE AND pe.EmployeeId IN @Ids",
            new { Id = id, ClientId = clientId, PayPeriod = payPeriod, Ids = ids }, transaction)).ToArray();
        var otherRuns = (await connection.QueryAsync<int>(@"SELECT Id FROM payruns WHERE Id<>@Id AND ClientId=@ClientId AND PayPeriod=@PayPeriod AND RunType='Regular' AND Status IN ('AwaitingProcessing','Queued','Processing')",
            new { Id = id, ClientId = clientId, PayPeriod = payPeriod }, transaction)).ToArray();
        var evidence = await connection.ExecuteScalarAsync<long>(ProcessingResultEvidenceSql,
            new { Id = id }, transaction);
        var adjustmentState = request is null || ids.Length==0 ? new SelectedAdjustmentState()
            : await ReadSelectedAdjustmentsAsync(connection,transaction,request,lockRows,true);
        return new SelectedRunState
        {
            Run = run, Request = request, ClientActive = active == 1, EligibleEmployeeIds = employees.Select(employee => employee.EmployeeId).ToArray(), Employees = employees,
            AlreadyCoveredEmployeeIds = covered, OtherRunningRunIds = otherRuns,
            FinancialEvidenceCount = evidence, AdjustmentCount = adjustmentState.ApplicableCount,
            AdjustmentReviews=adjustmentState.Reviews,AdjustmentIssues=adjustmentState.Issues
        };
    }

    private static void RequireUnchangedSelectedScope(CreatePayRunRequest request, HashSet<int> actualIds, IReadOnlyCollection<int> reviewedIds)
    {
        if (!actualIds.SetEquals(reviewedIds))
            throw new SelectedPayRunConflictException("Employee coverage changed before calculation. No partial calculation was saved; review the failed attempt and current payroll scope.");
    }
}
