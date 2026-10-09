using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

// Lives only in the existing batch send_state_json; no payroll/employee records are changed.
internal sealed class ExcelPayslipMailJobRecord
{
    public string Id { get; set; } = "";
    public int ActorUserId { get; set; }
    public int ClientId { get; set; }
    public string ClientName { get; set; } = "";
    public string BatchId { get; set; } = "";
    public string Month { get; set; } = "";
    public string SourceFileName { get; set; } = "";
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? DismissedAtUtc { get; set; }
    public List<string> RowIds { get; set; } = [];
    public int MissingEmail { get; set; }
    public int Skipped { get; set; }
    public List<ExcelPayslipMailJobDelivery> Deliveries { get; set; } = [];
}

internal sealed class ExcelPayslipMailJobDelivery
{
    public string Id { get; set; } = "";
    public long QueueId { get; set; }
    public int RowCount { get; set; }
}

public sealed partial class ExcelPayslipRepository
{
    public async Task<(ExcelPayslipMailJobSubmission? Item, string? Error)> SubmitMailJobAsync(int clientId, string batchId,
        SendExcelPayslipsRequest request, int actorUserId, string actor)
    {
        if (actorUserId <= 0) return (null, "A signed-in user is required to send payslips.");
        if (ValidateSendRequest(batchId, request, 1000) is string requestError) return (null, requestError);
        var requestId = Guid.Parse(request.RequestId).ToString("N");
        await using var db = Db(); await db.OpenAsync(); await using var tx = await db.BeginTransactionAsync();
        if (!await SupportedAsync(db, clientId, tx)) return (null, "Select an active client.");
        // Old /send and this entire-job path use the same locks and delivery history.
        await db.ExecuteScalarAsync<int>("SELECT Id FROM clients WHERE Id=@ClientId FOR UPDATE", new { ClientId = clientId }, tx);
        var stateJson = await db.ExecuteScalarAsync<string?>("SELECT CAST(send_state_json AS CHAR) FROM excel_payslip_batches WHERE client_id=@ClientId AND id=@Id FOR UPDATE", new { ClientId = clientId, Id = batchId }, tx);
        var batch = await ReadBatchAsync(db, clientId, batchId, tx);
        if (batch is null) return (null, "The saved batch was not found for this client.");
        var state = JsonSerializer.Deserialize<ExcelPayslipSendState>(stateJson!, JsonOptions) ?? new();
        var (selected, error) = SelectRows(batch, request); if (error is not null) return (null, error);
        var selectedIds = selected.Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
        if (request.EmailOverrides.Keys.Any(id => !selectedIds.Contains(id))) return (null, "Email overrides must refer only to selected source rows.");
        if (request.Mode == "Combined" && !TryEmail(request.Email, out _)) return (null, "Enter one valid recipient email for the combined PDF.");
        var fingerprint = SendFingerprint(batchId, request);
        var previousJson = state.Requests.TryGetValue(requestId, out var receipt) ? receipt.GetRawText() :
            await db.ExecuteScalarAsync<string?>("SELECT CAST(JSON_EXTRACT(send_state_json,@Path) AS CHAR) FROM excel_payslip_batches WHERE client_id=@ClientId AND id<>@Id AND JSON_CONTAINS_PATH(send_state_json,'one',@Path) LIMIT 1 FOR UPDATE",
                new { ClientId = clientId, Id = batchId, Path = "$.requests.\"" + requestId + "\"" }, tx);
        var existing = RestoreSendRequest(previousJson, fingerprint);
        if (existing.Error is not null) return (null, existing.Error);
        if (existing.Item is not null)
        {
            if (!state.Jobs.TryGetValue(requestId, out var previous) || !OwnsMailJob(previous, actorUserId, clientId, batchId))
                return (null, "This request ID is already in use. Refresh delivery status before starting another send action.");
            return (new() { Job = BuildMailJobSummary(previous, await ReadJobQueuesAsync(db, [previous], tx)), Items = existing.Item.Items }, null);
        }
        var statuses = BuildDeliveryStatuses(batch, state, await ReadDeliveryQueueAsync(db, clientId, state, tx));
        var reviewed = ReviewDeliverySelection(selected, request, statuses);
        if (reviewed.Items.Any(item => item.Status == "Ready") && await notifications.ExcelPayslipDeliveryReadyAsync(db, tx) is string readyError)
            return (null, readyError);
        var mailTemplate = reviewed.Items.Any(item => item.Status == "Ready")
            ? await NotificationRepository.ReadExcelPayslipTemplateAsync(db, tx) : null;
        // PDF rendering is deliberately absent from submission; the existing durable worker performs it.
        var documents = PrepareMailJobDocuments(batch, selected, request, reviewed, requestId, actor, mailTemplate);
        await NotificationRepository.QueueExcelPayslipDocumentsAsync(db, tx, batch, documents);
        foreach (var document in documents) state.Deliveries.Add(document.Id, document);
        foreach (var item in reviewed.Items.Where(item => item.Status == "Ready"))
        {
            item.Status = "Queued";
            item.Message = request.Mode == "Combined" ? "Combined PDF queued to the specified recipient." : "Individual PDF queued to this recipient.";
        }
        var job = CreateMailJobRecord(batch, requestId, actorUserId, actor, selected, reviewed, documents);
        state.Jobs.Add(requestId, job);
        state.Requests.Add(requestId, JsonSerializer.SerializeToElement(new SendManifest
            { Fingerprint = fingerprint, CreatedBy = Limit(actor, 190), CreatedAtUtc = job.CreatedAtUtc, Result = reviewed }, JsonOptions));
        await db.ExecuteAsync("UPDATE excel_payslip_batches SET send_state_json=CAST(@Json AS JSON) WHERE client_id=@ClientId AND id=@Id",
            new { ClientId = clientId, Id = batchId, Json = JsonSerializer.Serialize(state, JsonOptions) }, tx);
        await tx.CommitAsync();
        var queued = documents.Select(document => new ExcelPayslipQueueStatus { Id = document.QueueId, ClientId = clientId,
            EventCode = NotificationRepository.ExcelPayslipEvent, ResourceType = "ExcelPayslipBatch", ResourceId = batchId + ":" + document.Id,
            Status = NotificationRepository.ExcelPayslipPendingStatus, CreatedAt = job.CreatedAtUtc });
        return (new() { Job = BuildMailJobSummary(job, queued), Items = reviewed.Items }, null);
    }

    internal static string? ValidateSendRequest(string batchId, SendExcelPayslipsRequest request, int maximumIndividualRows)
    {
        if (!Guid.TryParse(request.RequestId, out _)) return "Supply a unique request ID for this send action.";
        if (request.Mode is not ("Individual" or "Combined")) return "Choose individual or combined delivery.";
        if (request.EmailOverrides is null || request.EmailOverrides.Count > 1000) return "Email overrides are invalid.";
        if (!TextValid(request.Email ?? "", 254) || request.EmailOverrides.Values.Any(v => !TextValid(v ?? "", 254))) return "Recipient emails exceed their allowed lengths.";
        if (request.Mode == "Individual" && request.RowIds?.Count > maximumIndividualRows) return $"Send individual payslips in groups of up to {maximumIndividualRows} rows.";
        return !Guid.TryParseExact(batchId, "N", out _) ? "The saved batch was not found." : null;
    }

    internal static List<NotificationRepository.ExcelPayslipMailDocument> PrepareMailJobDocuments(ExcelPayslipBatch batch,
        IReadOnlyList<ExcelPayslipRow> selected, SendExcelPayslipsRequest request, ExcelPayslipDeliveryResult reviewed, string requestId, string actor, NotificationTemplate? template)
    {
        var ready = reviewed.Items.Where(item => item.Status == "Ready").ToDictionary(item => item.RowId, StringComparer.Ordinal);
        var pending = selected.Where(row => ready.ContainsKey(row.Id)).ToArray();
        if (pending.Length == 0) return [];
        var hash = NotificationRepository.ExcelPayslipBatchHash(batch);
        var groups = request.Mode == "Combined" ? new[] { pending } : pending.Select(row => new[] { row });
        return groups.Select(rows => NotificationRepository.PrepareExcelPayslipMailMetadata(batch, rows,
            request.Mode == "Combined" ? request.Email : ready[rows[0].Id].Email, requestId, actor,
            request.IncludeSeal, request.AmountDecimalPlaces, template!, hash)).ToList();
    }

    internal static ExcelPayslipMailJobRecord CreateMailJobRecord(ExcelPayslipBatch batch, string id, int actorUserId, string actor,
        IReadOnlyList<ExcelPayslipRow> selected, ExcelPayslipDeliveryResult result, IReadOnlyList<NotificationRepository.ExcelPayslipMailDocument> documents) => new()
    {
        Id = id, ActorUserId = actorUserId, ClientId = batch.ClientId, ClientName = batch.ClientName, BatchId = batch.Id,
        Month = batch.Month, SourceFileName = batch.SourceFileName, CreatedBy = Limit(actor, 190), CreatedAtUtc = DateTime.UtcNow,
        RowIds = selected.Select(row => row.Id).ToList(), MissingEmail = result.Items.Count(item => item.Status == "Error"),
        Skipped = result.Items.Count(item => item.Status == "Skipped"),
        Deliveries = documents.Select(document => new ExcelPayslipMailJobDelivery { Id = document.Id, QueueId = document.QueueId, RowCount = document.RowIds.Count }).ToList()
    };

    internal static bool OwnsMailJob(ExcelPayslipMailJobRecord job, int actorUserId, int clientId, string batchId) =>
        actorUserId > 0 && job.ActorUserId == actorUserId && job.ClientId == clientId && job.BatchId == batchId;

    internal static ExcelPayslipMailJobSummary BuildMailJobSummary(ExcelPayslipMailJobRecord job,
        IEnumerable<ExcelPayslipQueueStatus> rows, DateTime? nowUtc = null)
    {
        var queues = rows.ToDictionary(row => row.Id);
        var summary = new ExcelPayslipMailJobSummary { Id = job.Id, ClientId = job.ClientId, ClientName = job.ClientName,
            BatchId = job.BatchId, Month = job.Month, SourceFileName = job.SourceFileName, CreatedAtUtc = job.CreatedAtUtc,
            CreatedBy = job.CreatedBy, Total = job.RowIds.Count, MissingEmail = job.MissingEmail, Skipped = job.Skipped, DismissedAtUtc = job.DismissedAtUtc };
        var processing = false; var needsReview = false;
        foreach (var delivery in job.Deliveries)
        {
            var queue = queues.GetValueOrDefault(delivery.QueueId);
            if (queue is null || queue.ClientId != job.ClientId || queue.EventCode != NotificationRepository.ExcelPayslipEvent
                || queue.ResourceType != "ExcelPayslipBatch" || queue.ResourceId != job.BatchId + ":" + delivery.Id)
            { summary.Unknown += delivery.RowCount; continue; }
            if (queue.Status.Equals("Sent", StringComparison.OrdinalIgnoreCase) || queue.SentAt.HasValue || queue.LastSentAt.HasValue)
            { summary.Sent += delivery.RowCount; continue; }
            switch (queue.Status.ToUpperInvariant())
            {
                case "PENDING": case "RETRY": case "EXCELPENDING": case "EXCELRETRY": summary.Queued += delivery.RowCount; break;
                case "PROCESSING": case "SENDING":
                    summary.Queued += delivery.RowCount; processing = true;
                    // There is no processing lease timestamp. This is a review signal, never permission to resend.
                    needsReview |= queue.CreatedAt < (nowUtc ?? DateTime.UtcNow).AddMinutes(-30); break;
                case "FAILED": summary.Failed += delivery.RowCount; break;
                default: summary.Unknown += delivery.RowCount; break;
            }
        }
        // Missing/corrupt coverage is not a successful send.
        summary.Unknown += Math.Max(0, summary.Total - summary.Sent - summary.Queued - summary.Failed - summary.MissingEmail - summary.Skipped - summary.Unknown);
        summary.Completed = summary.Total - summary.Queued;
        summary.PercentComplete = summary.Total == 0 ? 100 : (int)Math.Floor(100m * summary.Completed / summary.Total);
        summary.IsComplete = summary.Queued == 0;
        var attention = needsReview || summary.Failed + summary.MissingEmail + summary.Unknown > 0;
        summary.Status = attention ? "Needs attention" : summary.IsComplete ? "Completed" : processing ? "Processing" : "Queued";
        summary.Message = needsReview ? "An older queue item is still processing. Review Settings > Notifications before retrying; delivery may already have occurred."
            : summary.Unknown > 0 ? "Some delivery states cannot be verified. Review Settings > Notifications before retrying."
            : summary.Failed > 0 ? "Some deliveries failed. Review or retry their existing items in Settings > Notifications."
            : summary.MissingEmail > 0 ? "Rows without a valid recipient were not queued. Add their email addresses and start a new send action."
            : summary.IsComplete ? "Email processing finished. Sent and previously handled rows are shown separately."
            : "Email delivery continues in the background. You may leave this page.";
        return summary;
    }

    private static Task<IReadOnlyList<ExcelPayslipQueueStatus>> ReadJobQueuesAsync(MySqlConnection db,
        IReadOnlyList<ExcelPayslipMailJobRecord> jobs, MySqlTransaction? tx = null) => ReadDeliveryQueueAsync(db,
        jobs.Select(job => job.ClientId).Distinct().ToArray(), jobs.SelectMany(job => job.Deliveries).Select(delivery => delivery.QueueId).Where(id => id > 0).Distinct().ToArray(), tx);

    public async Task<ExcelPayslipMailJobs?> GetBatchMailJobsAsync(int clientId, string batchId)
    {
        if (!Guid.TryParseExact(batchId, "N", out _)) return null;
        await using var db = Db(); await db.OpenAsync();
        var json = await db.ExecuteScalarAsync<string?>("SELECT CAST(COALESCE(JSON_EXTRACT(send_state_json,'$.jobs'),JSON_OBJECT()) AS CHAR) FROM excel_payslip_batches WHERE client_id=@ClientId AND id=@Id", new { ClientId = clientId, Id = batchId });
        if (json is null) return null;
        var jobs = (JsonSerializer.Deserialize<Dictionary<string, ExcelPayslipMailJobRecord>>(json, JsonOptions) ?? [])
            .Where(pair => pair.Key == pair.Value.Id && pair.Value.ClientId == clientId && pair.Value.BatchId == batchId).Select(pair => pair.Value).ToArray();
        var queues = await ReadJobQueuesAsync(db, jobs);
        return new() { Items = jobs.OrderByDescending(job => job.CreatedAtUtc).Select(job => BuildMailJobSummary(job, queues)).ToArray() };
    }

    public async Task<ExcelPayslipMailJobs> GetOwnMailJobsAsync(int actorUserId, int? scopedClientId, Func<int, bool> canAccessClient)
    {
        if (actorUserId <= 0) return new();
        await using var db = Db(); await db.OpenAsync();
        // Poll compact job metadata only: never transfer batch_json or the complete worksheet/send descriptors.
        var stored = await db.QueryAsync<StoredMailJob>("""
SELECT batch.client_id AS ClientId,batch.id AS BatchId,jobs.job_id AS Id,
CAST(JSON_EXTRACT(batch.send_state_json,CONCAT('$.jobs."',jobs.job_id,'"')) AS CHAR) AS Json
FROM excel_payslip_batches batch INNER JOIN clients client ON client.Id=batch.client_id AND client.IsActive=TRUE
CROSS JOIN JSON_TABLE(COALESCE(JSON_KEYS(batch.send_state_json,'$.jobs'),JSON_ARRAY()),'$[*]' COLUMNS(job_id VARCHAR(32) PATH '$')) jobs
WHERE (@ClientId IS NULL OR batch.client_id=@ClientId)
AND JSON_EXTRACT(batch.send_state_json,CONCAT('$.jobs."',jobs.job_id,'".actorUserId'))=@ActorUserId
AND COALESCE(JSON_UNQUOTE(JSON_EXTRACT(batch.send_state_json,CONCAT('$.jobs."',jobs.job_id,'".dismissedAtUtc'))),'null')='null'
""",
            new { ActorUserId = actorUserId, ClientId = scopedClientId });
        var jobs = stored.Where(row => canAccessClient(row.ClientId)).Select(row => (row, job: JsonSerializer.Deserialize<ExcelPayslipMailJobRecord>(row.Json, JsonOptions)))
            .Where(item => item.job is not null && item.row.Id == item.job.Id && OwnsMailJob(item.job, actorUserId, item.row.ClientId, item.row.BatchId) && item.job.DismissedAtUtc is null)
            .Select(item => item.job!).ToArray();
        var queues = await ReadJobQueuesAsync(db, jobs);
        return new() { Items = jobs.OrderByDescending(job => job.CreatedAtUtc).Select(job => BuildMailJobSummary(job, queues)).ToArray() };
    }

    public async Task<string?> DismissMailJobAsync(int clientId, string batchId, string jobId, int actorUserId)
    {
        if (!Guid.TryParseExact(batchId, "N", out _) || !Guid.TryParseExact(jobId, "N", out _)) return "Email job not found.";
        await using var db = Db(); await db.OpenAsync(); await using var tx = await db.BeginTransactionAsync();
        await db.ExecuteScalarAsync<int>("SELECT Id FROM clients WHERE Id=@ClientId FOR UPDATE", new { ClientId = clientId }, tx);
        var json = await db.ExecuteScalarAsync<string?>("SELECT CAST(JSON_EXTRACT(send_state_json,@Path) AS CHAR) FROM excel_payslip_batches WHERE client_id=@ClientId AND id=@Id FOR UPDATE",
            new { ClientId = clientId, Id = batchId, Path = "$.jobs.\"" + jobId + "\"" }, tx);
        var job = json is null ? null : JsonSerializer.Deserialize<ExcelPayslipMailJobRecord>(json, JsonOptions);
        if (job is null || job.Id != jobId || !OwnsMailJob(job, actorUserId, clientId, batchId)) return "Email job not found for this user.";
        if (!BuildMailJobSummary(job, await ReadJobQueuesAsync(db, [job], tx)).IsComplete) return "An ongoing email job cannot be dismissed.";
        await db.ExecuteAsync("UPDATE excel_payslip_batches SET send_state_json=JSON_SET(send_state_json,@Path,@Dismissed) WHERE client_id=@ClientId AND id=@Id",
            new { ClientId = clientId, Id = batchId, Path = "$.jobs.\"" + jobId + "\".dismissedAtUtc", Dismissed = DateTime.UtcNow.ToString("O") }, tx);
        await tx.CommitAsync(); return null;
    }

    private sealed class StoredMailJob
    {
        public int ClientId { get; set; }
        public string BatchId { get; set; } = "";
        public string Id { get; set; } = "";
        public string Json { get; set; } = "";
    }
}
