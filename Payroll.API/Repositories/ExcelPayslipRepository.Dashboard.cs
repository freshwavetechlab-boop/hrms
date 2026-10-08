using Dapper;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayslipRepository
{
    public async Task<ExcelPayslipDashboard?> GetDashboardAsync(int clientId, string? batchId = null)
    {
        if (batchId is not null && !Guid.TryParseExact(batchId, "N", out _))
            throw new InvalidOperationException("Select a valid saved batch.");
        await using var db = Db(); await db.OpenAsync();
        if (!await SupportedAsync(db, clientId)) return null;
        var selection = SelectDashboardBatches(clientId, await ReadBatchSummariesAsync(db, clientId), batchId);
        if (selection is null) return null;
        var result = new ExcelPayslipDashboard { ClientId = clientId, SelectedBatch = selection.SelectedBatch, History = selection.History };
        if (selection.SelectedBatch is null) return result;

        // Only the selected snapshot plus the latest snapshot for each of 12 months is aggregated.
        // Independent component subqueries avoid multiplying earnings by deduction/employer rows.
        var ids = selection.TrendBatches.Select(batch => batch.Id).Append(selection.SelectedBatch.Id).Distinct().ToArray();
        var totals = (await db.QueryAsync<ExcelPayslipDashboardTotals>(@"
SELECT batches.id AS BatchId,
JSON_UNQUOTE(JSON_EXTRACT(batches.batch_json,'$.month')) AS Month,
JSON_LENGTH(JSON_EXTRACT(batches.batch_json,'$.rows')) AS RowCount,
COALESCE((SELECT SUM(component.amount) FROM JSON_TABLE(batches.batch_json,'$.rows[*].earnings[*]'
    COLUMNS(amount DECIMAL(65,28) PATH '$.amount')) component),0) AS Earnings,
COALESCE((SELECT SUM(component.amount) FROM JSON_TABLE(batches.batch_json,'$.rows[*].deductions[*]'
    COLUMNS(amount DECIMAL(65,28) PATH '$.amount')) component),0) AS Deductions,
COALESCE((SELECT SUM(component.amount) FROM JSON_TABLE(batches.batch_json,'$.rows[*].employerContributions[*]'
    COLUMNS(amount DECIMAL(65,28) PATH '$.amount')) component),0) AS EmployerContributions,
COALESCE((SELECT SUM(employee.net_pay) FROM JSON_TABLE(batches.batch_json,'$.rows[*]'
    COLUMNS(net_pay DECIMAL(65,28) PATH '$.netPay')) employee),0) AS NetPay,
(SELECT COUNT(*) FROM JSON_TABLE(batches.batch_json,'$.rows[*]' COLUMNS(row_ordinal FOR ORDINALITY)) employee
    WHERE JSON_LENGTH(JSON_EXTRACT(batches.batch_json,CONCAT('$.rows[',employee.row_ordinal-1,'].warnings')))>0) AS ReviewCount,
EXISTS(SELECT 1 FROM excel_payslip_calculation_sources source
    WHERE source.client_id=batches.client_id AND source.batch_id=batches.id) AS HasCalculationSource
FROM excel_payslip_batches batches WHERE batches.client_id=@ClientId AND batches.id IN @Ids",
            new { ClientId = clientId, Ids = ids })).ToDictionary(total => total.BatchId, StringComparer.Ordinal);
        result.Summary = totals.GetValueOrDefault(selection.SelectedBatch.Id);
        result.Trend = selection.TrendBatches.Where(batch => totals.ContainsKey(batch.Id)).Select(batch => totals[batch.Id]).ToArray();
        return result;
    }

    internal sealed record DashboardBatchSelection(IReadOnlyList<ExcelPayslipBatchSummary> History,
        ExcelPayslipBatchSummary? SelectedBatch, IReadOnlyList<ExcelPayslipBatchSummary> TrendBatches);

    internal static DashboardBatchSelection? SelectDashboardBatches(int clientId, IEnumerable<ExcelPayslipBatchSummary> batches, string? batchId)
    {
        var history = batches.Where(batch => batch.ClientId == clientId)
            .OrderByDescending(batch => batch.CreatedAtUtc).ThenByDescending(batch => batch.Id, StringComparer.Ordinal).ToArray();
        var selected = batchId is null ? history.FirstOrDefault() : history.SingleOrDefault(batch => batch.Id == batchId);
        if (batchId is not null && selected is null) return null;
        var trend = history.GroupBy(batch => batch.Month).Select(month => month.First())
            .OrderByDescending(batch => batch.Month, StringComparer.Ordinal).Take(12)
            .OrderBy(batch => batch.Month, StringComparer.Ordinal).ToArray();
        return new(history, selected, trend);
    }
}
