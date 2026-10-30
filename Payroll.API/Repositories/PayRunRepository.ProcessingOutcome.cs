using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public partial class PayRunRepository
{
    private const string ProcessingResultEvidenceSql = @"
SELECT (SELECT COUNT(*) FROM payrunemployees WHERE PayRunId=@Id)
+(SELECT COUNT(*) FROM payrunemployeelines WHERE PayRunId=@Id)
+(SELECT COUNT(*) FROM tax_computation_snapshots WHERE pay_run_id=@Id)
+(SELECT COUNT(*) FROM payroll_validation_issues WHERE PayRunId=@Id)
+(SELECT COUNT(*) FROM payroll_calculation_traces WHERE PayRunId=@Id)
+(SELECT COUNT(*) FROM payroll_reconciliation_results WHERE PayRunId=@Id)
+(SELECT COUNT(*) FROM payrolladjustments WHERE PayRunId=@Id)
+(SELECT COUNT(*) FROM payrun_step_logs WHERE PayRunId=@Id AND (EmployeeId IS NOT NULL OR Status<>'Success'
 OR COALESCE(JSON_LENGTH(InputJson),0)>0 OR COALESCE(JSON_LENGTH(FormulaJson),0)>0
 OR COALESCE(JSON_LENGTH(OldValueJson),0)>0 OR COALESCE(JSON_LENGTH(NewValueJson),0)>0
 OR COALESCE(JSON_LENGTH(OutputJson),0)<>1 OR COALESCE(JSON_EXTRACT(OutputJson,'$.warningCount'),-1)<>0
 OR ErrorMessage<>'' OR Warning<>''))";

    private static async Task<PayRun> ResolveProcessingExceptionAsync(MySqlConnection connection,
        int payRunId, string performedBy, Exception exception)
    {
        // The same-run advisory lock is still held on this connection. Acquiring
        // the row lock waits for any uncertain calculation commit to resolve.
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        var resolved = await PayRunProcessingOutcome.RecoverAsync(async () =>
        {
            var run = await connection.QuerySingleOrDefaultAsync<PayRun>(
                "SELECT * FROM payruns WHERE Id=@Id FOR UPDATE", new { Id=payRunId }, transaction);
            var evidence = await connection.ExecuteScalarAsync<long>(ProcessingResultEvidenceSql, new { Id=payRunId }, transaction);
            if (run is not null && run.Status != "Processing")
            {
                // Return the committed result without the optional comparison/leave
                // decoration whose post-commit refresh may be the original failure.
                run.Employees = (await connection.QueryAsync<PayRunEmployee>(
                    "SELECT * FROM payrunemployees WHERE PayRunId=@Id ORDER BY EmployeeName", new { Id=payRunId }, transaction)).ToList();
                run.EmployeeCount = run.Employees.Count(employee => !employee.IsSkipped);
            }
            return new(run, evidence);
        }, async run =>
        {
            if (await connection.ExecuteAsync(@"
UPDATE payruns SET Status='Failed', ProcessingCompletedAt=UTC_TIMESTAMP(), ProcessingError=@Error
WHERE Id=@Id AND Status='Processing'", new { Id=payRunId, Error=Trunc(exception.Message,1000) }, transaction) != 1)
                throw new SelectedPayRunConflictException("Payroll outcome changed during failure verification. Existing evidence is retained; refresh this run.");
            var issue = Issue(payRunId, null, "", "Run", "Critical", "Payroll Validation", exception.Message, true);
            issue.IssueType = "Exception";
            issue.DataJson = JsonSerializer.Serialize(new { exception=exception.GetType().Name, stackTrace=Trunc(exception.StackTrace,6000) });
            await WriteValidationIssuesAsync(connection, transaction, [issue]);
            await WritePipelineStepLogsAsync(connection, transaction, payRunId, performedBy, [issue]);
            // Read server timestamps in this same transaction; there is no new
            // post-commit refresh on the failure path either.
            return await connection.QuerySingleAsync<PayRun>("SELECT * FROM payruns WHERE Id=@Id", new { Id=payRunId }, transaction);
        });
        await transaction.CommitAsync();
        return resolved;
    }
}
