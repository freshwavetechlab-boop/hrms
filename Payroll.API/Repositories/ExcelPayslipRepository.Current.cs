using System.Text.Json;
using System.Text.Json.Nodes;
using System.Globalization;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayslipRepository
{
    // A saved payslip is the working record for a client/month, not a revision.
    // Older records remain addressable by ID for delivery/source references.
    internal static IReadOnlyList<ExcelPayslipBatchSummary> CurrentMonths(IEnumerable<ExcelPayslipBatchSummary> rows) =>
        rows.GroupBy(row => (row.ClientId, row.Month)).Select(group => group.First()).OrderByDescending(row => row.Month).ToArray();

    internal static bool SameSavedContent(ExcelPayslipBatch batch,ExcelPayslipBatch existing)
    {
        JsonNode Snapshot(ExcelPayslipBatch value)
        {
            var node=JsonSerializer.SerializeToNode(value,JsonOptions)!;
            foreach(var row in node["rows"]!.AsArray()) { row!.AsObject().Remove("employment"); row.AsObject().Remove("masterDetails"); }
            return node;
        }
        return JsonNode.DeepEquals(Snapshot(batch),Snapshot(existing));
    }

    internal static void ReusePayslipIdentity(ExcelPayslipBatch batch, ExcelPayslipBatch existing)
    {
        if (batch.Month != existing.Month) throw new InvalidOperationException("An update must keep the saved salary month.");
        batch.Id = existing.Id; batch.CreatedAtUtc = existing.CreatedAtUtc; batch.CreatedBy = existing.CreatedBy;
        batch.ParentBatchId = existing.ParentBatchId;
        foreach (var row in batch.Rows)
        {
            if(string.IsNullOrWhiteSpace(row.EmployeeCode))
            {
                var generated=existing.Rows.Where(old=>old.SourceRow==row.SourceRow
                    && string.Equals(old.EmployeeName.Trim(),row.EmployeeName.Trim(),StringComparison.OrdinalIgnoreCase)
                    && JsonSerializer.Serialize(old.Information,JsonOptions)==JsonSerializer.Serialize(row.Information,JsonOptions)).ToArray();
                if(generated.Length==1)row.EmployeeCode=generated[0].EmployeeCode;
            }
            var matches = existing.Rows.Where(old => old.SourceRow == row.SourceRow
                && string.Equals(old.EmployeeCode.Trim(), row.EmployeeCode.Trim(), StringComparison.OrdinalIgnoreCase)
                && string.Equals(old.EmployeeName.Trim(), row.EmployeeName.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 1)
            {
                // Browser number round-trips can move an unchanged decimal cache
                // by a few trillionths. Keep the authoritative saved values rather
                // than creating new financial/delivery identities for that noise.
                RetainUnchangedDecimalCaches(row,matches[0]);
                var suppliedId = row.Id;
                row.Id = matches[0].Id;
                if (NegativeAmountHash(row) == NegativeAmountHash(matches[0])) row.NegativeAmountReview = matches[0].NegativeAmountReview;
                else row.Id = suppliedId == matches[0].Id ? Guid.NewGuid().ToString("N") : suppliedId;
                row.Warnings = RowWarnings(row);
            }
        }
    }

    private static void RetainUnchangedDecimalCaches(ExcelPayslipRow row,ExcelPayslipRow old)
    {
        static bool Equal(decimal a,decimal b)=>Math.Abs(a-b)<=0.00000001m;
        static decimal? Optional(decimal? a,decimal? b)=>a.HasValue && b.HasValue && Equal(a.Value,b.Value)?b:a;
        static void Lines(List<ExcelPayslipAmount> a,List<ExcelPayslipAmount> b)
        {
            if(a.Count!=b.Count)return;
            foreach(var pair in a.Zip(b))if(pair.First.Label==pair.Second.Label && Equal(pair.First.Amount,pair.Second.Amount))pair.First.Amount=pair.Second.Amount;
        }
        if(Equal(row.NetPay,old.NetPay))row.NetPay=old.NetPay;
        row.DeclaredGross=Optional(row.DeclaredGross,old.DeclaredGross);row.DeclaredDeductions=Optional(row.DeclaredDeductions,old.DeclaredDeductions);
        Lines(row.Earnings,old.Earnings);Lines(row.Deductions,old.Deductions);Lines(row.EmployerContributions,old.EmployerContributions);
    }

    internal static void RetainCalculationDecimalCaches(ExcelPayslipCalculationSource source,ExcelPayslipCalculationSource old)
    {
        // Preserve the same authoritative cache text in both the mapped and raw
        // source snapshots. Exact source validation continues to run afterwards.
        static void Rows(IEnumerable<ExcelPayslipCalculationSourceRow> current,IEnumerable<ExcelPayslipCalculationSourceRow> previous,HashSet<int>? columns=null)
        {
            var byRow=previous.ToDictionary(row=>row.SourceRow);
            foreach(var row in current)
            {
                if(!byRow.TryGetValue(row.SourceRow,out var before))continue;
                for(var i=0;i<Math.Min(row.Cells.Count,before.Cells.Count);i++)
                {
                    if(columns is not null && !columns.Contains(i))continue;
                    var cell=row.Cells[i];var prior=before.Cells[i];
                    if(cell is null || prior is null || cell.FormulaText!=prior.FormulaText
                        || cell.Error!=prior.Error || cell.CalculationError!=prior.CalculationError || cell.MissingCachedValue!=prior.MissingCachedValue)continue;
                    if(decimal.TryParse(cell.Value,NumberStyles.Float,CultureInfo.InvariantCulture,out var value)
                        && decimal.TryParse(prior.Value,NumberStyles.Float,CultureInfo.InvariantCulture,out var earlier)
                        && Math.Abs(value-earlier)<=0.00000001m)cell.Value=prior.Value;
                }
            }
        }
        if(source.SheetName!=old.SheetName || source.HeaderRow!=old.HeaderRow)return;
        var columns=source.Columns.Where(column=>column.Kind is "earning" or "deduction" or "employer" or "netPay" or "grossTotal" or "deductionTotal")
            .Where(column=>old.Columns.Any(prior=>prior.ColumnIndex==column.ColumnIndex && prior.Kind==column.Kind && prior.Label==column.Label)).Select(column=>column.ColumnIndex).ToHashSet();
        Rows(source.Rows,old.Rows,columns);
        foreach(var sheet in source.DependencySheets)
        {
            var before=old.DependencySheets.SingleOrDefault(item=>item.WorkbookKey==sheet.WorkbookKey && item.SheetName==sheet.SheetName);
            if(before is not null)Rows(sheet.Rows,before.Rows);
        }
    }

    private async Task<ExcelPayslipBatch?> CurrentSaveTargetAsync(MySqlConnection db, MySqlTransaction tx, int clientId, ExcelPayslipBatch request)
    {
        if (!string.IsNullOrWhiteSpace(request.Id))
        {
            if (!Guid.TryParseExact(request.Id, "N", out _)) throw new InvalidOperationException("The saved payslip ID is invalid.");
            var explicitTarget = await ReadBatchAsync(db, clientId, request.Id, tx)
                ?? throw new InvalidOperationException("The saved payslip no longer belongs to this client. Refresh before saving.");
            if (explicitTarget.Month != request.Month) throw new InvalidOperationException("Use Save for the selected month; an existing record cannot be moved to another month.");
            return explicitTarget;
        }
        var id = await db.ExecuteScalarAsync<string?>(@"SELECT id FROM excel_payslip_batches
WHERE client_id=@ClientId AND JSON_UNQUOTE(JSON_EXTRACT(batch_json,'$.month'))=@Month
ORDER BY created_at DESC,id DESC LIMIT 1 FOR UPDATE",new {ClientId=clientId,request.Month},tx);
        return id is null ? null : await ReadBatchAsync(db,clientId,id,tx);
    }

    private static async Task RequireIdlePayslipUpdateAsync(MySqlConnection db, MySqlTransaction tx, int clientId, string id)
    {
        if(await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM notification_queue WHERE ClientId=@ClientId
AND ResourceType IN ('ExcelPayslipBatch','ExcelPayslipEmailRequest') AND (ResourceId=@Id OR ResourceId LIKE @Prefix)
AND Status IN ('Pending','Queued','Processing')",new{ClientId=clientId,Id=id,Prefix=id+":%"},tx)>0)
            throw new InvalidOperationException("Payslip delivery is in progress. Let it finish before updating this month's saved payslips.");
        // Workers hold their own saved message payloads. Do not change their source
        // while a delivery is being prepared, and never erase send/audit state.
        var state = await db.ExecuteScalarAsync<string?>("SELECT CAST(send_state_json AS CHAR) FROM excel_payslip_batches WHERE client_id=@ClientId AND id=@Id FOR UPDATE",new {ClientId=clientId,Id=id},tx);
        if (!string.IsNullOrWhiteSpace(state))
        {
            using var json=JsonDocument.Parse(state);
            if(json.RootElement.TryGetProperty("jobs",out var jobs) && jobs.ValueKind==JsonValueKind.Object
                && jobs.EnumerateObject().Any(job=>job.Value.TryGetProperty("status",out var status) && status.GetString() is "Queued" or "Processing"))
                throw new InvalidOperationException("Payslip delivery is in progress. Let it finish before updating this month's saved payslips.");
        }
    }
}
