using System.Data;
using System.Text.Json;
using Dapper;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayslipRepository
{
    private sealed class OverviewSnapshot { public int ClientId { get; set; } public string BatchId { get; set; } = ""; public string Json { get; set; } = ""; public bool HasCalculationSource { get; set; } }
    private sealed class DuplicateOverviewCode { public int ClientId { get; set; } public string Code { get; set; } = ""; }
    public async Task<IReadOnlyList<ExcelPayslipOverviewBatch>> GetOverviewBatchesAsync(IReadOnlyCollection<int> allowedClientIds)
    {
        if (allowedClientIds.Count == 0) return [];
        await using var db = Db(); await db.OpenAsync();
        await using var tx = await db.BeginTransactionAsync(IsolationLevel.RepeatableRead, isReadOnly: true);
        var rows = (await db.QueryAsync<ExcelPayslipOverviewBatch>(@"SELECT b.client_id AS ClientId,c.Name AS ClientName,b.id AS BatchId,
JSON_UNQUOTE(JSON_EXTRACT(b.batch_json,'$.month')) AS Month,JSON_UNQUOTE(JSON_EXTRACT(b.batch_json,'$.sourceFileName')) AS FileName,
JSON_UNQUOTE(JSON_EXTRACT(b.batch_json,'$.sheetName')) AS SheetName,JSON_LENGTH(JSON_EXTRACT(b.batch_json,'$.rows')) AS RowCount,b.created_at AS CreatedAtUtc,
EXISTS(SELECT 1 FROM excel_payslip_calculation_sources s WHERE s.client_id=b.client_id AND s.batch_id=b.id) AS HasCalculationSource
FROM excel_payslip_batches b JOIN clients c ON c.Id=b.client_id AND c.IsActive=TRUE WHERE b.client_id IN @ClientIds ORDER BY c.Name,b.client_id,b.created_at DESC,b.id DESC",
            new { ClientIds = allowedClientIds }, tx)).ToArray();
        foreach (var row in rows) { row.ClientName = ExcelPayslipOverviewService.SafeText(row.ClientName); row.FileName = ExcelPayslipOverviewService.SafeText(row.FileName); row.SheetName = ExcelPayslipOverviewService.SafeText(row.SheetName); }
        var kept = await FilterCatalogConsolidatedSummariesAsync(db, tx,
            rows.Select(row => new ExcelPayslipBatchSummary { Id = row.BatchId, ClientId = row.ClientId }).ToArray());
        var visible = kept.Select(batch => (batch.ClientId, batch.Id)).ToHashSet();
        await tx.RollbackAsync();
        return rows.Where(row=>visible.Contains((row.ClientId,row.BatchId))).ToArray();
    }
    public async Task<ExcelPayslipOverviewResult> GetOverviewAsync(ExcelPayslipOverviewRequest request)
    {
        ExcelPayslipOverviewService.Validate(request);
        await using var db = Db(); await db.OpenAsync();
        await using var tx = await db.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        foreach(var scope in request.Scopes)await RequireCurrentConsolidationSourceAsync(db,tx,scope.ClientId,scope.BatchId);
        var args = new DynamicParameters();
        var terms = request.Scopes.Select((scope, index) => { args.Add("Client" + index, scope.ClientId); args.Add("Batch" + index, scope.BatchId); return $"(b.client_id=@Client{index} AND b.id=@Batch{index})"; });
        var where = string.Join(" OR ", terms);
        var total = await db.ExecuteScalarAsync<long>($@"SELECT COALESCE(SUM(JSON_LENGTH(JSON_EXTRACT(b.batch_json,'$.rows'))),0)
FROM excel_payslip_batches b JOIN clients c ON c.Id=b.client_id AND c.IsActive=TRUE WHERE {where}", args, tx);
        if (total > 50000) throw new InvalidOperationException("Select a smaller scope (maximum 50,000 source rows).");
        var saved = (await db.QueryAsync<OverviewSnapshot>($@"SELECT b.client_id AS ClientId,b.id AS BatchId,CAST(b.batch_json AS CHAR) AS Json,
EXISTS(SELECT 1 FROM excel_payslip_calculation_sources s WHERE s.client_id=b.client_id AND s.batch_id=b.id) AS HasCalculationSource
FROM excel_payslip_batches b JOIN clients c ON c.Id=b.client_id AND c.IsActive=TRUE WHERE {where} ORDER BY b.client_id,b.id", args, tx)).ToArray();
        if (saved.Length != request.Scopes.Count) throw new InvalidOperationException("One selected batch is missing or its client is inactive. Refresh the scope list.");
        var input = new List<ExcelPayslipOverviewInput>();
        foreach (var savedBatch in saved)
        {
            var batch = JsonSerializer.Deserialize<ExcelPayslipBatch>(savedBatch.Json, JsonOptions);
            if (batch is null || batch.ClientId != savedBatch.ClientId || batch.Id != savedBatch.BatchId || ValidateBatch(batch) is not null)
                throw new InvalidOperationException("A selected source snapshot is invalid. Review that saved batch before aggregating it.");
            // Reuse existing client-scoped identity/month eligibility checks. Protected inputs stay inside the repository, never in overview DTOs.
            await AnnotateEmploymentAsync(db, batch, tx);
            input.Add(new(batch, savedBatch.HasCalculationSource));
        }
        var duplicates = await db.QueryAsync<DuplicateOverviewCode>(@"SELECT ClientId,UPPER(TRIM(EmployeeCode)) AS Code FROM employees
WHERE ClientId IN @ClientIds AND TRIM(EmployeeCode)<>'' GROUP BY ClientId,UPPER(TRIM(EmployeeCode)) HAVING COUNT(*)>1",
            new { ClientIds = request.Scopes.Select(s => s.ClientId).Distinct().ToArray() }, tx);
        return ExcelPayslipOverviewService.Build(input, request, duplicates.Select(d => (d.ClientId, d.Code)).ToHashSet());
    }
}
