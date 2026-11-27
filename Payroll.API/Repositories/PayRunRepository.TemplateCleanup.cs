using System.Globalization;
using Dapper;
using MySqlConnector;

namespace Payroll.API.Repositories;

public sealed record EmployeeTestPayslip(int RunId, string PayPeriod, bool Paid, bool Legacy);

public partial class PayRunRepository
{
    internal sealed record TemplateCleanupReview(string Fingerprint, List<EmployeeTestPayslip> Payslips, string? Blocker = null);

    private sealed class TemplateCleanupRow
    {
        public int Id { get; init; }
        public int EmployeeId { get; init; }
        public int RunId { get; init; }
        public string PayPeriod { get; init; } = "";
        public bool Paid { get; init; }
        public DateTime? PaymentDate { get; init; }
        public decimal NetPay { get; init; }
        public decimal GrossPay { get; init; }
        public string CalculationHash { get; init; } = "";
        public string? RecordedTemplates { get; init; }
        public bool HasSharedCapture { get; init; }
        public bool Legacy => RecordedTemplates is null;
    }

    private static async Task<List<TemplateCleanupRow>> ReadTemplateCleanupRowsAsync(MySqlConnection db, MySqlTransaction? tx,
        int clientId, int[] employeeIds, bool lockRows)
    {
        if (employeeIds.Length == 0 || !await TableExistsAsync(db, tx, "payruns")) return [];
        // Explicit test cleanup reviews saved results, including tests generated with
        // the selected template. The confirmed row fingerprints protect new runs.
        const string sql = """
            SELECT p.Id,p.EmployeeId,r.Id RunId,r.PayPeriod,
                (p.PaymentStatus='Paid' OR p.PaymentDate IS NOT NULL OR r.Status='Paid') Paid,
                p.PaymentDate,p.NetPay,p.GrossPay,
                SHA2(CONCAT_WS('|',p.MonthlyGross,p.StatutoryDeductions,p.OneTimeEarnings,p.OneTimeDeductions,
                    p.PayableDays,COALESCE(p.PresentDays,-1),p.DetailsJson),256) CalculationHash,
                CAST(JSON_EXTRACT(p.DetailsJson,'$[*].salaryTemplateId','$[*].pfPolicy.SalaryStructureId',
                    '$[*].locationParts[*].SalaryStructureId') AS CHAR) RecordedTemplates,
                EXISTS(SELECT 1 FROM modulesettings m WHERE m.client_id=p.ClientId
                    AND m.ModuleCode IN (CONCAT('pcst.active:',r.Id),CONCAT('prfi:run:',r.Id))) HasSharedCapture
            FROM payrunemployees p
            JOIN payruns r ON r.Id=p.PayRunId AND r.ClientId=p.ClientId
            JOIN employees e ON e.Id=p.EmployeeId AND e.ClientId=p.ClientId
            WHERE p.ClientId=@clientId AND p.EmployeeId IN @employeeIds AND p.IsSkipped=FALSE
                AND r.RunType='Regular' AND r.Status IN ('Approved','Partially Paid','Paid')
            ORDER BY r.Id,p.Id
            """;
        return (await db.QueryAsync<TemplateCleanupRow>(sql + (lockRows ? " FOR UPDATE" : ""), new { clientId, employeeIds }, tx)).ToList();
    }

    private static TemplateCleanupReview DescribeCleanup(IEnumerable<TemplateCleanupRow> rows)
    {
        var payslips = rows.Select(r => new EmployeeTestPayslip(r.RunId, r.PayPeriod, r.Paid, r.Legacy)).ToList();
        return new(RecalculationHash(rows), payslips);
    }

    internal static async Task<Dictionary<int, TemplateCleanupReview>> ReviewTemplateCleanupAsync(MySqlConnection db,
        MySqlTransaction? tx, int clientId, int[] employeeIds)
    {
        var rows = await ReadTemplateCleanupRowsAsync(db, tx, clientId, employeeIds, false);
        var groups = rows.ToLookup(row => row.EmployeeId);
        var reviews = employeeIds.ToDictionary(id => id, id => DescribeCleanup(groups[id]));
        foreach (var (runId, message) in await IncompleteSharedRunsAsync(db, tx, rows))
            foreach (var row in rows.Where(row => row.RunId == runId)) reviews[row.EmployeeId] = reviews[row.EmployeeId] with { Blocker = message };
        return reviews;
    }

    private static async Task<Dictionary<int, string>> IncompleteSharedRunsAsync(MySqlConnection db, MySqlTransaction? tx,
        List<TemplateCleanupRow> candidates)
    {
        var groups = candidates.Where(row => row.HasSharedCapture).ToLookup(row => row.RunId);
        var ids = groups.Select(group => group.Key).ToArray();
        if (ids.Length == 0) return [];
        var totals = await db.QueryAsync<(int RunId, int Count)>("SELECT PayRunId,COUNT(*) FROM payrunemployees WHERE PayRunId IN @ids AND IsSkipped=FALSE GROUP BY PayRunId", new { ids }, tx);
        return totals.Where(run => groups[run.RunId].Count() != run.Count).ToDictionary(run => run.RunId,
            run => $"Run #{run.RunId} ({groups[run.RunId].First().PayPeriod}) has a signed payslip record for the whole roster. Partial removal would invalidate the remaining payslips.");
    }

    internal static async Task<HashSet<int>> RemoveReviewedTestPayslipsAsync(MySqlConnection db, MySqlTransaction tx,
        int clientId, int[] employeeIds, string templateId, string actor, IReadOnlyDictionary<int, TemplateCleanupReview> reviews)
    {
        var rows = await ReadTemplateCleanupRowsAsync(db, tx, clientId, employeeIds, true);
        var candidates = rows.ToLookup(row => row.EmployeeId);
        foreach (var id in employeeIds)
            if (!reviews.TryGetValue(id, out var review) || DescribeCleanup(candidates[id]).Fingerprint != review.Fingerprint)
                throw new InvalidOperationException($"Payroll for employee #{id} changed after the cleanup preview. Review the current paid and unpaid records again.");
        var blockers = await IncompleteSharedRunsAsync(db, tx, rows);
        if (blockers.Count > 0) throw new InvalidOperationException(string.Join(" ", blockers.Values));
        foreach (var row in rows)
        {
            var employeeId = row.EmployeeId;
            var key = new { clientId, employeeId, row.Id, row.RunId, templateId, actor };
            await CancelTestPayslipDeliveriesAsync(db, tx, clientId, row.RunId, row.Id);
            if (!row.Paid)
            {
                await SyncExpenseClaimPayrollStatusAsync(db, tx, row.RunId, "Pending Payroll", [employeeId]);
                await db.ExecuteAsync("UPDATE payrolladjustments SET Status='Approved',PayRunId=NULL WHERE ClientId=@clientId AND EmployeeId=@employeeId AND PayRunId=@RunId AND Status='Applied'", key, tx);
            }
            // Paid expense claims and their applied adjustments stay consumed. A new
            // regular payroll must never issue an already paid reimbursement again.
            await db.ExecuteAsync("""
                DELETE FROM tax_computation_snapshots WHERE pay_run_id=@RunId AND employee_id=@employeeId;
                DELETE FROM payrunemployeelines WHERE PayRunId=@RunId AND PayRunEmployeeId=@Id;
                DELETE FROM payroll_calculation_traces WHERE PayRunId=@RunId AND EmployeeId=@employeeId;
                DELETE FROM payroll_validation_issues WHERE PayRunId=@RunId AND EmployeeId=@employeeId;
                """, key, tx);
            if (await db.ExecuteAsync("DELETE FROM payrunemployees WHERE Id=@Id AND PayRunId=@RunId AND ClientId=@clientId AND EmployeeId=@employeeId", key, tx) != 1)
                throw new InvalidOperationException("The reviewed employee payroll row is no longer available.");
            await db.ExecuteAsync("""
                INSERT INTO payrun_step_logs(PayRunId,EmployeeId,StepNumber,StepName,StartTime,EndTime,Status,PerformedBy,OldValueJson,NewValueJson)
                VALUES(@RunId,@employeeId,62,'Removed test payslip',UTC_TIMESTAMP(),UTC_TIMESTAMP(),'Success',@actor,
                    JSON_OBJECT('employeePayrollRowId',@Id,'paid',@Paid,'netPay',@NetPay,'period',@PayPeriod),
                    JSON_OBJECT('assignedTemplateId',@templateId))
                """, new { row.RunId, employeeId, actor, row.Id, row.Paid, row.NetPay, row.PayPeriod, templateId }, tx);
        }
        foreach (var runId in rows.Select(row => row.RunId).Distinct())
        {
            var key = new { runId, clientId };
            var remaining = await db.QuerySingleAsync<(int Total, int Paid)>("""
                SELECT COUNT(*),COALESCE(SUM(PaymentStatus='Paid' OR PaymentDate IS NOT NULL),0)
                FROM payrunemployees WHERE PayRunId=@RunId AND IsSkipped=FALSE
                """, key, tx);
            if (remaining.Total == 0)
            {
                await db.ExecuteAsync("""
                    UPDATE payruns SET Status='Superseded',RunCode=CONCAT('CLEANED-',Id),PayrollCost=0,NetPay=0
                    WHERE Id=@RunId AND ClientId=@clientId;
                    DELETE FROM payroll_reconciliation_results WHERE PayRunId=@RunId;
                    DELETE FROM modulesettings WHERE client_id=@clientId
                        AND ModuleCode IN (CONCAT('pcst.active:',@RunId),CONCAT('prfi:run:',@RunId));
                    """, key, tx);
            }
            else
            {
                var status = remaining.Paid == 0 ? "Approved" : remaining.Paid == remaining.Total ? "Paid" : "Partially Paid";
                await db.ExecuteAsync("UPDATE payruns SET Status=@status WHERE Id=@runId AND ClientId=@clientId", new { status, runId, clientId }, tx);
                await RefreshTotalsAsync(db, tx, runId);
                await WriteReconciliationResultsAsync(db, tx, runId);
            }
        }
        return candidates.Select(group => group.Key).ToHashSet();
    }

    private static async Task CancelTestPayslipDeliveriesAsync(MySqlConnection db, MySqlTransaction tx, int clientId, int runId, int rowId)
    {
        if (!await TableExistsAsync(db, tx, "notification_queue")) return;
        const string deliveries = """
            ClientId=@clientId AND ((EventCode='PAYSLIP.SEND' AND ResourceType='PayRunEmployee' AND ResourceId=@rowKey)
            OR (EventCode=@pdfEvent AND ResourceType=@pdfResource AND ResourceId LIKE @pdfPrefix))
            """;
        var args = new { clientId, rowKey = rowId.ToString(CultureInfo.InvariantCulture), pdfPrefix = $"{runId}:{rowId}:%",
            pdfEvent = NotificationRepository.RegisterPdfEvent, pdfResource = NotificationRepository.RegisterPdfResource };
        var states = await db.QueryAsync<string>("SELECT Status FROM notification_queue WHERE " + deliveries + " FOR UPDATE", args, tx);
        if (states.Any(state => state is "Processing" or "Sending"))
            throw new InvalidOperationException($"A payslip email from run #{runId} is being sent. Retry the cleanup after delivery finishes.");
        await db.ExecuteAsync("UPDATE notification_queue SET Status='Cancelled',ErrorMessage='Test payslip removed from payroll.' WHERE " +
            deliveries + " AND SentAt IS NULL AND Status IN ('Pending','Retry','Failed','PayslipPdfPending','PayslipPdfRetry')", args, tx);
    }
}
