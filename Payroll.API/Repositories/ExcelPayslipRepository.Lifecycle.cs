using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayslipRepository
{
    // These operations share the client/batch lock order used by save, send and master setup.
    public async Task<ExcelPayslipDeletePreview?> PreviewDeleteAsync(int clientId, string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) return null;
        await using var db = Db(); await db.OpenAsync(); await using var tx = await db.BeginTransactionAsync();
        await db.ExecuteScalarAsync<int>("SELECT Id FROM clients WHERE Id=@ClientId FOR UPDATE", new { ClientId = clientId }, tx);
        return await BuildDeletePreviewAsync(db, tx, clientId, id);
    }

    public async Task<ExcelPayslipDeleteResult> DeleteBatchAsync(int clientId, string id, ExcelPayslipDeleteRequest request, string actor)
    {
        if (clientId <= 0 || !Guid.TryParseExact(id, "N", out _) || request.ConfirmBatchId != id || string.IsNullOrWhiteSpace(request.Token)
            || request.Token.Length != 64 || request.Token.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidOperationException("Confirm the exact batch ID from a fresh dependency preview.");
        await using var db = Db(); await db.OpenAsync(); await using var tx = await db.BeginTransactionAsync();
        await db.ExecuteScalarAsync<int>("SELECT Id FROM clients WHERE Id=@ClientId FOR UPDATE", new { ClientId = clientId }, tx);
        var receipt = await db.ExecuteScalarAsync<string?>("SELECT preview_token FROM excel_payslip_deletions WHERE client_id=@ClientId AND batch_id=@Id", new { ClientId = clientId, Id = id }, tx);
        if (receipt is not null)
        {
            if (receipt != request.Token) throw new InvalidOperationException("This batch was already deleted by another confirmed action.");
            return new(true, true);
        }
        var preview = await BuildDeletePreviewAsync(db, tx, clientId, id)
            ?? throw new InvalidOperationException("Batch not found for this client.");
        if (!preview.CanDelete) throw new InvalidOperationException(string.Join(" ", preview.Blockers));
        if (preview.Token != request.Token) throw new InvalidOperationException("Batch dependencies changed. Review a fresh deletion preview.");
        // Keep only a bounded deletion receipt, never the financial/employee snapshot.
        await db.ExecuteAsync(@"INSERT INTO excel_payslip_deletions(client_id,batch_id,preview_token,actor,summary_json)
VALUES(@ClientId,@Id,@Token,@Actor,CAST(@Summary AS JSON));
DELETE FROM excel_payslip_batch_originals WHERE client_id=@ClientId AND batch_id=@Id;
DELETE FROM excel_payslip_calculation_sources WHERE client_id=@ClientId AND batch_id=@Id;
DELETE FROM excel_payslip_batches WHERE client_id=@ClientId AND id=@Id;",
            new { ClientId = clientId, Id = id, preview.Token, Actor = Limit(actor, 190), Summary = JsonSerializer.Serialize(preview, JsonOptions) }, tx);
        await tx.CommitAsync();
        return new(true, false);
    }

    private static async Task<ExcelPayslipDeletePreview?> BuildDeletePreviewAsync(MySqlConnection db, MySqlTransaction tx, int clientId, string id)
    {
        var row = await db.QuerySingleOrDefaultAsync<DeleteSnapshot>(@"SELECT CAST(batch_json AS CHAR) AS BatchJson,CAST(send_state_json AS CHAR) AS StateJson
FROM excel_payslip_batches WHERE client_id=@ClientId AND id=@Id FOR UPDATE", new { ClientId = clientId, Id = id }, tx);
        if (row is null) return null;
        var batch = JsonSerializer.Deserialize<ExcelPayslipBatch>(row.BatchJson, JsonOptions)
            ?? throw new InvalidOperationException("Invalid batch snapshot. Deletion is blocked.");
        using var state = JsonDocument.Parse(row.StateJson);
        var preview = new ExcelPayslipDeletePreview { BatchId = id, PayslipRows = batch.Rows.Count };
        static int Count(JsonElement state, string name) => state.ValueKind == JsonValueKind.Object && state.TryGetProperty(name, out var value)
            ? value.ValueKind == JsonValueKind.Object ? value.EnumerateObject().Count() : 1 : 0;
        preview.DeliveryRecords = Count(state.RootElement, "deliveries");
        preview.MailJobs = Count(state.RootElement, "jobs");
        preview.RequestReceipts = Count(state.RootElement, "requests");
        // Fail closed for unknown send-state fields (including legacy coordinator request receipts).
        var unknownState = state.RootElement.ValueKind != JsonValueKind.Object || state.RootElement.EnumerateObject().Any(p =>
            p.Name is not ("deliveries" or "jobs" or "requests"));
        var parameters = new { ClientId = clientId, Id = id, Prefix = id + ":%" };
        preview.Notifications = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM notification_queue WHERE ClientId=@ClientId
AND ResourceType IN ('ExcelPayslipBatch','ExcelPayslipEmailRequest') AND (ResourceId=@Id OR ResourceId LIKE @Prefix)", parameters, tx);
        preview.NotificationLogs = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM notification_logs l LEFT JOIN notification_queue q ON q.Id=l.QueueId
WHERE (q.ClientId=@ClientId OR q.Id IS NULL) AND l.ResourceType IN ('ExcelPayslipBatch','ExcelPayslipEmailRequest')
AND (l.ResourceId=@Id OR l.ResourceId LIKE @Prefix)", parameters, tx);
        var receiptTable = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name='excel_payroll_setup_imports'", transaction: tx);
        if (receiptTable > 0)
            preview.MasterSetupReceipts = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM excel_payroll_setup_imports WHERE client_id=@ClientId AND batch_id=@Id", parameters, tx);
        preview.DerivedBatches = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM excel_payslip_batches b
WHERE b.client_id=@ClientId AND b.id<>@Id AND (JSON_UNQUOTE(JSON_EXTRACT(b.batch_json,'$.parentBatchId'))=@Id
OR EXISTS(SELECT 1 FROM excel_payslip_calculation_sources s WHERE s.client_id=b.client_id AND s.batch_id=b.id
AND JSON_UNQUOTE(JSON_EXTRACT(s.source_json,'$.sourceBatchId'))=@Id))", parameters, tx);
        preview.CalculationSources = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM excel_payslip_calculation_sources WHERE client_id=@ClientId AND batch_id=@Id", parameters, tx);
        var originals = (await db.QueryAsync<string>("SELECT original_id FROM excel_payslip_batch_originals WHERE client_id=@ClientId AND batch_id=@Id ORDER BY original_id", parameters, tx)).ToArray();
        preview.OriginalLinks = originals.Length;
        preview.OtherBatchOriginalLinks = originals.Length == 0 ? 0 : await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM excel_payslip_batch_originals
WHERE client_id=@ClientId AND batch_id<>@Id AND original_id IN @Originals", new { ClientId = clientId, Id = id, Originals = originals }, tx);
        await AddCleanupDetailsAsync(db, tx, clientId, id, row, batch, preview, receiptTable > 0);
        preview.Blockers = ExcelPayslipDeletionPolicy.Blockers(preview, batch.LifecycleVersion, unknownState);
        preview.Token = DeletePreviewToken(row.BatchJson, row.StateJson, originals, preview);
        return preview;
    }
    internal static string DeletePreviewToken(string batchJson, string stateJson, IEnumerable<string> originals, ExcelPayslipDeletePreview preview)
    {
        var review = JsonSerializer.SerializeToNode(preview, JsonOptions)!.AsObject();
        review.Remove("token");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            batchJson, stateJson, originals = originals.OrderBy(id => id, StringComparer.Ordinal).ToArray(), review
        }, JsonOptions)))).ToLowerInvariant();
    }
    private sealed class DeleteSnapshot { public string BatchJson { get; set; } = ""; public string StateJson { get; set; } = ""; }

    public async Task<IReadOnlyList<ExcelPayslipOriginal>> GetOriginalsAsync(int clientId, string batchId)
    {
        await using var db = Db(); await db.OpenAsync();
        return (await db.QueryAsync<ExcelPayslipOriginal>(@"SELECT o.id AS Id,l.original_file_name AS FileName,a.file_size_bytes AS SizeBytes,
a.sha256_hash AS Sha256,a.uploaded_at_utc AS UploadedAtUtc FROM excel_payslip_batch_originals l
JOIN excel_payslip_originals o ON o.client_id=l.client_id AND o.id=l.original_id
JOIN entity_attachments a ON a.id=o.attachment_id AND a.is_deleted=FALSE
WHERE l.client_id=@ClientId AND l.batch_id=@BatchId ORDER BY l.original_id", new { ClientId = clientId, BatchId = batchId })).ToArray();
    }
}
