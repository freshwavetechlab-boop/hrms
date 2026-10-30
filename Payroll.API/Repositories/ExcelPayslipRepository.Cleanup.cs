using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayslipRepository
{
    private sealed class CleanupSnapshot
    {
        public string BatchJson { get; set; } = "";
        public string? SourceJson { get; set; }
        public ExcelPayslipBatch Batch => JsonSerializer.Deserialize<ExcelPayslipBatch>(BatchJson, JsonOptions)!;
        public ExcelPayslipCalculationSource? Source => SourceJson is null ? null : JsonSerializer.Deserialize<ExcelPayslipCalculationSource>(SourceJson, JsonOptions);
        public string Hash => ExcelPayslipLegacyReview.SnapshotHash(BatchJson, SourceJson);
    }
    private sealed class CleanupAudit
    {
        public string Action { get; set; } = "";
        public string RequestHash { get; set; } = "";
        public string BatchHash { get; set; } = "";
        public string KeeperId { get; set; } = "";
        public string KeeperHash { get; set; } = "";
    }
    private sealed class CleanupObjectRow
    {
        public string Id { get; set; } = "";
        public string Status { get; set; } = "";
        public string Label { get; set; } = "";
        public bool Discardable { get; set; }
        public int ReferenceCount { get; set; }
    }
    private sealed class ParentEdge { public string Id { get; set; } = ""; public string? ParentId { get; set; } public string? SourceParentId { get; set; } }
    private sealed class DraftResult { public string Status { get; set; } = ""; public string ResultJson { get; set; } = ""; }
    private static async Task<IReadOnlyDictionary<string, string[]>> ReadParentGraphAsync(MySqlConnection db, MySqlTransaction tx, int clientId)
    {
        var edges = await db.QueryAsync<ParentEdge>(@"SELECT b.id AS Id,JSON_UNQUOTE(JSON_EXTRACT(b.batch_json,'$.parentBatchId')) AS ParentId,
JSON_UNQUOTE(JSON_EXTRACT(s.source_json,'$.sourceBatchId')) AS SourceParentId FROM excel_payslip_batches b
LEFT JOIN excel_payslip_calculation_sources s ON s.client_id=b.client_id AND s.batch_id=b.id WHERE b.client_id=@ClientId", new { ClientId = clientId }, tx);
        return edges.ToDictionary(e => e.Id, e => new[] { e.ParentId, e.SourceParentId }.Where(p => !string.IsNullOrWhiteSpace(p) && p != "null").Select(p => p!).Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
    }
    private static Task<CleanupSnapshot?> ReadCleanupSnapshotAsync(MySqlConnection db, MySqlTransaction tx, int clientId, string id) =>
        db.QuerySingleOrDefaultAsync<CleanupSnapshot>(@"SELECT CAST(b.batch_json AS CHAR) AS BatchJson,CAST(s.source_json AS CHAR) AS SourceJson
FROM excel_payslip_batches b LEFT JOIN excel_payslip_calculation_sources s ON s.client_id=b.client_id AND s.batch_id=b.id
WHERE b.client_id=@ClientId AND b.id=@Id FOR UPDATE", new { ClientId = clientId, Id = id }, tx);

    private static async Task AddCleanupDetailsAsync(MySqlConnection db, MySqlTransaction tx, int clientId, string id,
        DeleteSnapshot row, ExcelPayslipBatch batch, ExcelPayslipDeletePreview preview, bool hasReceiptTable)
    {
        var p = new { ClientId = clientId, Id = id, Prefix = id + ":%" };
        var sourceJson = await db.ExecuteScalarAsync<string?>("SELECT CAST(source_json AS CHAR) FROM excel_payslip_calculation_sources WHERE client_id=@ClientId AND batch_id=@Id", p, tx);
        var source = sourceJson is null ? null : JsonSerializer.Deserialize<ExcelPayslipCalculationSource>(sourceJson, JsonOptions);
        preview.IsLegacy = batch.LifecycleVersion != 1;
        preview.RecordedParentId = !string.IsNullOrWhiteSpace(batch.ParentBatchId) ? batch.ParentBatchId : source?.SourceBatchId ?? "";
        preview.CleanupAuditRecords = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM excel_payslip_cleanup_actions WHERE client_id=@ClientId AND batch_id=@Id", p, tx);
        if (preview.IsLegacy)
        {
            preview.LegacyReviewStatus = "Review required: missing creation ancestry is not proof of an orphan.";
            var review = await db.QueryFirstOrDefaultAsync<CleanupAudit>(@"SELECT action_code AS Action,request_hash AS RequestHash,batch_hash AS BatchHash,
keeper_id AS KeeperId,keeper_hash AS KeeperHash FROM excel_payslip_cleanup_actions
WHERE client_id=@ClientId AND batch_id=@Id AND action_code IN ('supersede-legacy','verify-recorded-parent') ORDER BY created_at_utc DESC,request_id DESC LIMIT 1", p, tx);
            if (review is not null)
            {
                var keeper = await ReadCleanupSnapshotAsync(db, tx, clientId, review.KeeperId);
                preview.LegacyReviewValid = review.BatchHash == ExcelPayslipLegacyReview.SnapshotHash(row.BatchJson, sourceJson)
                    && keeper is not null && review.KeeperHash == keeper.Hash
                    && (review.Action == "verify-recorded-parent" ? preview.RecordedParentId == review.KeeperId
                        : ExcelPayslipLegacyReview.Compare(batch, source, keeper.Batch, keeper.Source).Eligible);
                preview.LegacyReviewStatus = preview.LegacyReviewValid
                    ? $"Reviewed {review.Action}; retained reference {review.KeeperId}. Creation ancestry is not invented."
                    : "Previous review is stale or its retained source is unavailable. Review fresh evidence again.";
            }
            if (!string.IsNullOrEmpty(preview.RecordedParentId))
            {
                var parent = await ReadCleanupSnapshotAsync(db, tx, clientId, preview.RecordedParentId);
                var graphIssue = ExcelPayslipLegacyReview.ValidateParentGraph(id, preview.RecordedParentId, await ReadParentGraphAsync(db, tx, clientId));
                var validParent = parent is not null && graphIssue is null && parent.Batch.CreatedAtUtc <= batch.CreatedAtUtc;
                if (!validParent) { preview.InvalidParentReferences++; preview.LegacyReviewValid = false; preview.LegacyReviewStatus = graphIssue ?? "Recorded parent is missing or has an invalid creation date."; }
                preview.Objects.Add(new("Recorded parent", preview.RecordedParentId, validParent ? "Available" : "Missing/invalid",
                    !validParent ? graphIssue ?? "Restore/reconcile the referenced source or verified historical evidence. Future parent references cannot be accepted."
                        : "This persisted source reference can be verified without altering either financial snapshot.",
                    validParent ? "verify-recorded-parent" : ""));
            }
            // Keep JSON out of MySQL filesort: even an extracted JSON predicate can make the
            // server carry the entire captured workbook through a sort. Read small keys without
            // ORDER BY, order them in memory, then load metadata for only the first 100 IDs.
            var candidateKeys = (await db.QueryAsync<ExcelPayslipLegacyCandidate>(@"SELECT id AS Id,created_at AS CreatedAtUtc FROM excel_payslip_batches
WHERE client_id=@ClientId AND id<>@Id AND JSON_UNQUOTE(JSON_EXTRACT(batch_json,'$.month'))=@Month
AND (created_at>@CreatedAtUtc OR (created_at=@CreatedAtUtc AND id>@Id))", new { ClientId = clientId, Id = id, batch.Month, batch.CreatedAtUtc }, tx)).ToArray();
            preview.CandidateCount = candidateKeys.Length;
            var orderedKeys = candidateKeys.OrderByDescending(candidate => candidate.CreatedAtUtc)
                .ThenByDescending(candidate => candidate.Id, StringComparer.Ordinal).Take(100).ToArray();
            if (orderedKeys.Length > 0)
            {
                var candidates = (await db.QueryAsync<ExcelPayslipLegacyCandidate>(@"SELECT id AS Id,JSON_UNQUOTE(JSON_EXTRACT(batch_json,'$.month')) AS Month,
JSON_UNQUOTE(JSON_EXTRACT(batch_json,'$.sourceFileName')) AS FileName,JSON_UNQUOTE(JSON_EXTRACT(batch_json,'$.sheetName')) AS SheetName,
JSON_LENGTH(JSON_EXTRACT(batch_json,'$.rows')) AS RowCount,created_at AS CreatedAtUtc FROM excel_payslip_batches
WHERE client_id=@ClientId AND id IN @Ids", new { ClientId = clientId, Ids = orderedKeys.Select(candidate => candidate.Id).ToArray() }, tx))
                    .ToDictionary(candidate => candidate.Id, StringComparer.Ordinal);
                preview.Candidates = orderedKeys.Select(candidate => candidates[candidate.Id]).ToList();
            }
        }
        var queues = (await db.QueryAsync<CleanupObjectRow>(@"SELECT CAST(Id AS CHAR) AS Id,Status,EventCode AS Label FROM notification_queue
WHERE ClientId=@ClientId AND ResourceType IN ('ExcelPayslipBatch','ExcelPayslipEmailRequest') AND (ResourceId=@Id OR ResourceId LIKE @Prefix) ORDER BY Id", p, tx)).ToArray();
        foreach (var queue in queues)
            preview.Objects.Add(new("Notification", queue.Id, queue.Status,
                queue.Status is "Pending" or "Processing" or "Retry" ? "Active/retry dispatch: allow it to finish or use a separately supported dispatch administration workflow. Cleanup never cancels it."
                    : "Delivery/attempt evidence and generated-PDF inputs are retained. No supported evidence archive/purge workflow exists here.", Label: queue.Label));
        // Logs have no ClientId column: scope through their queue. Orphan logs matching the exact opaque batch resource fail closed.
        var logs = await db.QueryAsync<CleanupObjectRow>(@"SELECT CAST(l.Id AS CHAR) AS Id,l.Status,l.EventCode AS Label FROM notification_logs l
LEFT JOIN notification_queue q ON q.Id=l.QueueId
WHERE (q.ClientId=@ClientId OR q.Id IS NULL) AND l.ResourceType IN ('ExcelPayslipBatch','ExcelPayslipEmailRequest')
AND (l.ResourceId=@Id OR l.ResourceId LIKE @Prefix) ORDER BY l.Id", p, tx);
        foreach (var log in logs) preview.Objects.Add(new("Notification audit", log.Id, log.Status, "Delivery audit is retained. It cannot be erased to make batch deletion succeed.", Label: log.Label));
        var state = JsonNode.Parse(row.StateJson) as JsonObject;
        if (state?["jobs"] is JsonObject jobs)
            foreach (var job in jobs)
            {
                var discardable = CanDiscardUnqueuedJob(row.StateJson, job.Key, preview.Notifications, preview.NotificationLogs, clientId, id);
                preview.Objects.Add(new("Mail job", job.Key, discardable ? "Never queued" : "Retained",
                    discardable ? "No delivery documents, queue or logs exist. Its unqueued attempt and paired request receipt may be discarded with separate confirmation."
                        : "Job/recipient coverage is retained. Dismissal only hides a completed job; it does not remove dispatch evidence.", discardable ? "discard-unqueued-job" : ""));
            }
        if (state?["deliveries"] is JsonObject deliveries)
            foreach (var delivery in deliveries) preview.Objects.Add(new("Delivery descriptor", delivery.Key, "Retained", "The worker and delivery history require this batch and descriptor."));
        if (state?["requests"] is JsonObject requests)
            foreach (var request in requests) preview.Objects.Add(new("Request receipt", request.Key, "Retained", "Idempotency/coordinator delivery evidence. Only a proven never-queued paired job action can remove its own receipt."));
        if (hasReceiptTable)
        {
            // The JSON-derived discard flag must not share a MySQL filesort with a large
            // receipt payload. Keep the complete scoped read; order only its small DTOs.
            var receipts = await db.QueryAsync<CleanupObjectRow>(@"SELECT id AS Id,status AS Status,
 (BINARY status='Draft' AND JSON_TYPE(result_json)='OBJECT' AND JSON_LENGTH(result_json)=0) AS Discardable
FROM excel_payroll_setup_imports WHERE client_id=@ClientId AND batch_id=@Id", p, tx);
            foreach (var receipt in receipts.OrderBy(receipt => receipt.Id, StringComparer.Ordinal)) preview.Objects.Add(new("Master setup", receipt.Id, receipt.Status,
                receipt.Discardable ? "Unapplied empty-result draft. Discarding removes this draft only; no employee/component/template is touched."
                    : "Applied or non-empty setup evidence must stay linked to master/payroll history. Review it in Master setup; no automatic rollback/unlink is offered.", receipt.Discardable ? "discard-draft" : ""));
        }
        var children = await db.QueryAsync<CleanupObjectRow>(@"SELECT b.id AS Id,JSON_UNQUOTE(JSON_EXTRACT(b.batch_json,'$.month')) AS Status,
JSON_UNQUOTE(JSON_EXTRACT(b.batch_json,'$.sourceFileName')) AS Label FROM excel_payslip_batches b WHERE b.client_id=@ClientId AND b.id<>@Id
AND (JSON_UNQUOTE(JSON_EXTRACT(b.batch_json,'$.parentBatchId'))=@Id OR EXISTS(SELECT 1 FROM excel_payslip_calculation_sources s
WHERE s.client_id=b.client_id AND s.batch_id=b.id AND JSON_UNQUOTE(JSON_EXTRACT(s.source_json,'$.sourceBatchId'))=@Id))", p, tx);
        foreach (var child in children.OrderBy(child => child.Id, StringComparer.Ordinal)) preview.Objects.Add(new("Dependent batch", child.Id, child.Status, "Inspect and independently resolve this dependent version first. No automatic reparenting or cascade is performed.", "inspect-batch", Label: child.Label));
        var originals = await db.QueryAsync<CleanupObjectRow>(@"SELECT o.id AS Id,l.original_file_name AS Label,CASE WHEN a.is_deleted THEN 'Unavailable' ELSE 'Retained' END AS Status,
(SELECT COUNT(*) FROM excel_payslip_batch_originals other WHERE other.client_id=l.client_id AND other.original_id=l.original_id AND other.batch_id<>l.batch_id) AS ReferenceCount
FROM excel_payslip_batch_originals l JOIN excel_payslip_originals o ON o.client_id=l.client_id AND o.id=l.original_id
JOIN entity_attachments a ON a.id=o.attachment_id WHERE l.client_id=@ClientId AND l.batch_id=@Id ORDER BY o.id", p, tx);
        foreach (var original in originals) preview.Objects.Add(new("Original attachment", original.Id, original.Status,
            $"Original bytes are retained for source provenance; {original.ReferenceCount} other batch link(s) remain. This cleanup removes this batch link only. No physical purge is offered.", Count: original.ReferenceCount, Label: original.Label));
        var legacy = (await db.QueryAsync<string>(@"SELECT ModuleCode FROM modulesettings WHERE client_id=@ClientId AND
(ModuleCode=CONCAT('excel_payslip:',@Id) OR LEFT(ModuleCode,19) IN ('excel_payslip_send:','excel_payslip_mail:')) ORDER BY ModuleCode", p, tx)).ToArray();
        preview.LegacyStorageRecords = legacy.Length;
        foreach (var code in legacy) preview.Objects.Add(new("Legacy storage", code, "Unconverted", "Use the reviewed explicit storage migration/conversion first. Old mail keys cannot be safely attributed or erased here."));
        preview.Objects.Add(new("Cleanup audit", id, "Retained", $"{preview.CleanupAuditRecords} resolution receipt(s), plus a minimal deletion receipt after purge. No deleted employee rows are copied into audit.", Count: preview.CleanupAuditRecords));
    }

    internal static bool CanDiscardUnqueuedJob(string stateJson, string jobId, int notifications, int logs, int clientId, string batchId)
    {
        if (notifications != 0 || logs != 0) return false;
        try
        {
            var root = JsonNode.Parse(stateJson) as JsonObject;
            if (root is null || root.Any(p => p.Key is not ("jobs" or "deliveries" or "requests"))) return false;
            var state = JsonSerializer.Deserialize<ExcelPayslipSendState>(stateJson, JsonOptions);
            if (state?.Deliveries is null || state.Jobs is null || state.Requests is null || state.Deliveries.Count != 0 || !state.Jobs.TryGetValue(jobId, out var job)
                || job is null || job.Id != jobId || job.BatchId != batchId || job.ClientId != clientId || job.Deliveries is null || job.Deliveries.Count != 0) return false;
            if (!state.Requests.TryGetValue(jobId, out var receipt)) return false;
            var manifest = JsonSerializer.Deserialize<SendManifest>(receipt.GetRawText(), JsonOptions);
            return manifest?.Result?.Items is not null && !string.IsNullOrWhiteSpace(manifest.Fingerprint) && manifest.Result.Items.All(item => item.Status is "Error" or "Missing" or "Skipped");
        }
        catch (JsonException) { return false; }
    }

    public async Task<ExcelPayslipSupersessionPreview> PreviewSupersessionAsync(int clientId, string id, string keeperId)
    {
        if (!Guid.TryParseExact(id, "N", out _) || !Guid.TryParseExact(keeperId, "N", out _)) throw new InvalidOperationException("Select saved client batch IDs.");
        await using var db = Db(); await db.OpenAsync(); await using var tx = await db.BeginTransactionAsync();
        await db.ExecuteScalarAsync<int>("SELECT Id FROM clients WHERE Id=@ClientId FOR UPDATE", new { ClientId = clientId }, tx);
        var preview = await BuildDeletePreviewAsync(db, tx, clientId, id) ?? throw new InvalidOperationException("Batch not found.");
        return await BuildSupersessionAsync(db, tx, clientId, id, keeperId, preview);
    }
    private static async Task<ExcelPayslipSupersessionPreview> BuildSupersessionAsync(MySqlConnection db, MySqlTransaction tx, int clientId, string id, string keeperId, ExcelPayslipDeletePreview preview)
    {
        var old = await ReadCleanupSnapshotAsync(db, tx, clientId, id) ?? throw new InvalidOperationException("Batch not found for this client.");
        var keeper = await ReadCleanupSnapshotAsync(db, tx, clientId, keeperId) ?? throw new InvalidOperationException("Retained version not found for this client.");
        var result = ExcelPayslipLegacyReview.Compare(old.Batch, old.Source, keeper.Batch, keeper.Source);
        if (preview.InvalidParentReferences > 0) result.Blockers.Add("Reconcile this batch's invalid recorded ancestry before any supersession action.");
        var keeperParent = !string.IsNullOrWhiteSpace(keeper.Batch.ParentBatchId) ? keeper.Batch.ParentBatchId : keeper.Source?.SourceBatchId;
        if (!string.IsNullOrWhiteSpace(keeperParent))
        {
            var issue = ExcelPayslipLegacyReview.ValidateParentGraph(keeperId, keeperParent, await ReadParentGraphAsync(db, tx, clientId));
            if (issue is not null) result.Blockers.Add("Retained version ancestry is unsafe: " + issue);
        }
        result.Token = ExcelPayslipLegacyReview.Hash(JsonSerializer.Serialize(new { preview.Token, oldHash = old.Hash, keeperHash = keeper.Hash, result }, JsonOptions));
        return result;
    }

    public async Task<ExcelPayslipCleanupActionResult> ResolveCleanupAsync(int clientId, string id, ExcelPayslipCleanupActionRequest request, string actor)
    {
        if (clientId <= 0 || !Guid.TryParseExact(id, "N", out _) || !Guid.TryParse(request.RequestId, out var parsedRequestId)
            || request.ConfirmBatchId != id || !request.Acknowledged || request.Token?.Length != 64 || request.Token.Any(c => !Uri.IsHexDigit(c))
            || request.Action is not ("supersede-legacy" or "verify-recorded-parent" or "discard-draft" or "discard-unqueued-job"))
            throw new InvalidOperationException("Review and explicitly confirm this exact cleanup action.");
        var requestId = parsedRequestId.ToString("N");
        var requestHash = ExcelPayslipLegacyReview.Hash(JsonSerializer.Serialize(request, JsonOptions));
        await using var db = Db(); await db.OpenAsync(); await using var tx = await db.BeginTransactionAsync();
        await db.ExecuteScalarAsync<int>("SELECT Id FROM clients WHERE Id=@ClientId FOR UPDATE", new { ClientId = clientId }, tx);
        var previous = await db.QuerySingleOrDefaultAsync<CleanupAudit>(@"SELECT action_code AS Action,request_hash AS RequestHash,batch_hash AS BatchHash,keeper_id AS KeeperId,keeper_hash AS KeeperHash
FROM excel_payslip_cleanup_actions WHERE client_id=@ClientId AND request_id=@RequestId", new { ClientId = clientId, RequestId = requestId }, tx);
        if (previous is not null)
        {
            if (previous.RequestHash != requestHash) throw new InvalidOperationException("This cleanup request ID already belongs to a different confirmed action.");
            await tx.CommitAsync(); return new(true, true, await PreviewDeleteAsync(clientId, id));
        }
        var preview = await BuildDeletePreviewAsync(db, tx, clientId, id) ?? throw new InvalidOperationException("Batch not found for this client.");
        var snapshot = await ReadCleanupSnapshotAsync(db, tx, clientId, id) ?? throw new InvalidOperationException("Batch not found.");
        string keeperHash = ""; object proof = new { action = request.Action, objectId = request.ObjectId };
        if (request.Action == "supersede-legacy")
        {
            if (!preview.IsLegacy || request.ConfirmObjectId != id) throw new InvalidOperationException("Confirm the exact legacy version being superseded.");
            var review = await BuildSupersessionAsync(db, tx, clientId, id, request.KeeperBatchId, preview);
            if (!review.Eligible) throw new InvalidOperationException(string.Join(" ", review.Blockers));
            if (request.Token != review.Token) throw new InvalidOperationException("Version comparison or dependencies changed. Review a fresh comparison.");
            keeperHash = (await ReadCleanupSnapshotAsync(db, tx, clientId, request.KeeperBatchId))!.Hash; proof = review;
        }
        else
        {
            if (request.Token != preview.Token) throw new InvalidOperationException("Cleanup dependencies changed. Refresh the preview before confirming.");
            if (request.Action == "verify-recorded-parent")
            {
                if (!preview.IsLegacy || request.KeeperBatchId != preview.RecordedParentId || request.ConfirmObjectId != id
                    || !preview.Objects.Any(o => o.Action == "verify-recorded-parent" && o.Id == request.KeeperBatchId))
                    throw new InvalidOperationException("Only the exact persisted parent in this client's current snapshot can be verified.");
                keeperHash = (await ReadCleanupSnapshotAsync(db, tx, clientId, request.KeeperBatchId))!.Hash;
                proof = new { recordedParentId = request.KeeperBatchId, evidence = "Verified existing persisted source reference; no financial snapshot was changed." };
            }
            else
            {
                if (request.ObjectId != request.ConfirmObjectId || !preview.Objects.Any(o => o.Action == request.Action && o.Id == request.ObjectId))
                    throw new InvalidOperationException("This object is no longer safely discardable. Review its exact blocker.");
                if (request.Action == "discard-draft")
                {
                    var draft = await db.QuerySingleOrDefaultAsync<DraftResult>(@"SELECT status AS Status,CAST(result_json AS CHAR) AS ResultJson
FROM excel_payroll_setup_imports WHERE client_id=@ClientId AND batch_id=@Id AND id=@ObjectId FOR UPDATE", new { ClientId = clientId, Id = id, request.ObjectId }, tx);
                    if (draft is null || !ExcelPayslipLegacyReview.IsEmptyUnappliedDraft(draft.Status, draft.ResultJson))
                        throw new InvalidOperationException("This setup is applied or contains result metadata. No master/setup evidence was discarded.");
                    var affected = await db.ExecuteAsync(@"DELETE FROM excel_payroll_setup_imports WHERE client_id=@ClientId AND batch_id=@Id AND id=@ObjectId
AND BINARY status='Draft' AND JSON_TYPE(result_json)='OBJECT' AND JSON_LENGTH(result_json)=0", new { ClientId = clientId, Id = id, request.ObjectId }, tx);
                    if (affected != 1) throw new InvalidOperationException("The draft changed or was applied. Nothing was discarded.");
                }
                else
                {
                    var stateJson = await db.ExecuteScalarAsync<string?>("SELECT CAST(send_state_json AS CHAR) FROM excel_payslip_batches WHERE client_id=@ClientId AND id=@Id FOR UPDATE", new { ClientId = clientId, Id = id }, tx)
                        ?? throw new InvalidOperationException("The batch send state is no longer available. Nothing was discarded.");
                    if (!CanDiscardUnqueuedJob(stateJson, request.ObjectId, preview.Notifications, preview.NotificationLogs, clientId, id))
                        throw new InvalidOperationException("The job now has dispatch or delivery evidence; it was not discarded.");
                    var state = JsonNode.Parse(stateJson)!.AsObject(); state["jobs"]!.AsObject().Remove(request.ObjectId); state["requests"]!.AsObject().Remove(request.ObjectId);
                    await db.ExecuteAsync("UPDATE excel_payslip_batches SET send_state_json=CAST(@Json AS JSON) WHERE client_id=@ClientId AND id=@Id", new { ClientId = clientId, Id = id, Json = state.ToJsonString() }, tx);
                }
            }
        }
        await db.ExecuteAsync(@"INSERT INTO excel_payslip_cleanup_actions(client_id,batch_id,request_id,request_hash,action_code,object_id,batch_hash,keeper_id,keeper_hash,proof_json,actor)
VALUES(@ClientId,@Id,@RequestId,@RequestHash,@Action,@ObjectId,@BatchHash,@KeeperId,@KeeperHash,CAST(@Proof AS JSON),@Actor)",
            new { ClientId = clientId, Id = id, RequestId = requestId, RequestHash = requestHash, request.Action, request.ObjectId, BatchHash = snapshot.Hash,
                KeeperId = request.KeeperBatchId ?? "", KeeperHash = keeperHash, Proof = JsonSerializer.Serialize(proof, JsonOptions), Actor = Limit(actor, 190) }, tx);
        await tx.CommitAsync();
        return new(true, false, await PreviewDeleteAsync(clientId, id));
    }
}
