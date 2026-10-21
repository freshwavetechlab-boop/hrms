using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayslipRepository
{
    public async Task<ExcelPayslipDeliveryStatuses?> GetDeliveryStatusAsync(int clientId, string batchId)
    {
        if (!Guid.TryParseExact(batchId, "N", out _)) return null;
        await using var db = Db(); await db.OpenAsync();
        if (!await SupportedAsync(db, clientId)) return null;
        // Polling needs row IDs/recipients, not salary amounts or the complete saved worksheet.
        using var result = await db.QueryMultipleAsync(@"SELECT CAST(send_state_json AS CHAR) FROM excel_payslip_batches WHERE client_id=@ClientId AND id=@Id;
SELECT employee.row_id AS Id,COALESCE(employee.email,'') AS Email FROM excel_payslip_batches batch
CROSS JOIN JSON_TABLE(batch.batch_json,'$.rows[*]' COLUMNS(row_id VARCHAR(100) PATH '$.id',email VARCHAR(254) PATH '$.email' NULL ON EMPTY)) employee
WHERE batch.client_id=@ClientId AND batch.id=@Id", new { ClientId = clientId, Id = batchId });
        var json = await result.ReadFirstOrDefaultAsync<string?>();
        if (json is null) return null;
        var batch = new ExcelPayslipBatch { Id = batchId, ClientId = clientId, Rows = (await result.ReadAsync<ExcelPayslipRow>()).ToList() };
        var state = JsonSerializer.Deserialize<ExcelPayslipSendState>(json ?? "{}", JsonOptions) ?? new();
        return BuildDeliveryStatuses(batch, state, await ReadDeliveryQueueAsync(db, clientId, state));
    }

    internal sealed class ExcelPayslipQueueStatus
    {
        public long Id { get; set; }
        public int ClientId { get; set; }
        public string EventCode { get; set; } = "";
        public string ResourceType { get; set; } = "";
        public string ResourceId { get; set; } = "";
        public string Status { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public DateTime? SentAt { get; set; }
        public DateTime? LastSentAt { get; set; }
        public string DeliveryStatus { get; set; } = "";
        public string DeliveryError { get; set; } = "";
        public string SentCopyStatus { get; set; } = "Unknown";
        public string Recipient { get; set; } = "";
    }

    private static async Task<IReadOnlyList<ExcelPayslipQueueStatus>> ReadDeliveryQueueAsync(MySqlConnection db, int clientId,
        ExcelPayslipSendState state, MySqlTransaction? tx = null)
    {
        var ids = MailDocuments(state).Where(item => item.Document.QueueId > 0).Select(item => item.Document.QueueId).Distinct().ToArray();
        return await ReadDeliveryQueueAsync(db, [clientId], ids, tx);
    }

    private static async Task<IReadOnlyList<ExcelPayslipQueueStatus>> ReadDeliveryQueueAsync(MySqlConnection db, int[] clientIds,
        long[] ids, MySqlTransaction? tx = null)
    {
        if (ids.Length == 0) return [];
        var rows = (await db.QueryAsync<ExcelPayslipQueueStatus>(@"SELECT queue.Id,queue.ClientId,queue.EventCode,queue.ResourceType,queue.ResourceId,
queue.Status,queue.CreatedAt,queue.SentAt,JSON_UNQUOTE(JSON_EXTRACT(queue.ToJson,'$[0]')) Recipient,
(SELECT MAX(log.CreatedAt) FROM notification_logs log WHERE log.QueueId=queue.Id AND log.Status='Sent'
 AND log.EventCode=queue.EventCode AND log.ResourceType=queue.ResourceType AND log.ResourceId=queue.ResourceId) AS LastSentAt
FROM notification_queue queue WHERE queue.ClientId IN @ClientIds AND queue.Id IN @Ids
 AND queue.EventCode IN ('EXCEL_PAYSLIP.SEND','EXCEL_PAYSLIP.EMAIL_REQUEST_PDF') AND queue.ResourceType IN ('ExcelPayslipBatch','ExcelPayslipEmailRequest')",
            new { ClientIds = clientIds, Ids = ids }, tx)).ToArray();
        var logs = (await db.QueryAsync<NotificationLog>(@"SELECT * FROM notification_logs WHERE QueueId IN @Ids
 AND EventCode IN ('EXCEL_PAYSLIP.SEND','EXCEL_PAYSLIP.EMAIL_REQUEST_PDF') AND Status IN ('Sent','Bounced','Deferred','Delivered','SentCopySaved','SentCopyFailed','SentCopyNotConfigured') ORDER BY CreatedAt DESC,Id DESC", new { Ids = ids }, tx))
            .GroupBy(log => log.QueueId).ToDictionary(group => group.Key, group => group.ToArray());
        foreach (var row in rows)
        {
            if (!logs.TryGetValue(row.Id, out var history)) continue;
            var lastSent = history.FirstOrDefault(log => log.Status == "Sent");
            var outcome = history.FirstOrDefault(log => (log.Status is "Bounced" or "Deferred" or "Delivered") && log.ResourceId == row.ResourceId
                && log.ResourceType == row.ResourceType && lastSent is not null && log.CreatedAt >= lastSent.CreatedAt
                && (lastSent.ProviderMessageId.Length == 0 || log.ProviderMessageId == lastSent.ProviderMessageId));
            if (outcome is not null) { row.DeliveryStatus = outcome.Status; row.DeliveryError = outcome.ErrorMessage; }
            var archive = history.FirstOrDefault(log => log.Status.StartsWith("SentCopy", StringComparison.Ordinal) && log.ResourceId == row.ResourceId
                && log.ResourceType == row.ResourceType && lastSent is not null && log.CreatedAt >= lastSent.CreatedAt
                && log.ProviderMessageId == lastSent.ProviderMessageId);
            if (archive is not null) row.SentCopyStatus = archive.Status["SentCopy".Length..];
        }
        return rows;
    }

    internal static ExcelPayslipDeliveryStatuses BuildDeliveryStatuses(ExcelPayslipBatch batch, ExcelPayslipSendState state,
        IEnumerable<ExcelPayslipQueueStatus> queueRows)
    {
        var queues = queueRows.ToDictionary(row => row.Id);
        var history = new Dictionary<string, List<ExcelPayslipRecipientDelivery>>(StringComparer.Ordinal);
        var rowIds = batch.Rows.Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
        static DateTime? Utc(DateTime? value) => value is null || value == default(DateTime) ? null : DateTime.SpecifyKind(value.Value, DateTimeKind.Utc);
        foreach (var (key, document, requestCopy) in MailDocuments(state))
        {
            if (document is null) continue;
            var valid = document.Id == key && document.ClientId == batch.ClientId && document.BatchId == batch.Id;
            var queue = valid ? queues.GetValueOrDefault(document.QueueId) : null;
            if (queue is not null && (queue.ClientId != batch.ClientId
                || queue.EventCode != (requestCopy ? NotificationRepository.ExcelPayslipEmailRequestPdfEvent : NotificationRepository.ExcelPayslipEvent)
                || queue.ResourceType != (requestCopy ? "ExcelPayslipEmailRequest" : "ExcelPayslipBatch")
                || (requestCopy ? !queue.ResourceId.StartsWith(batch.Id + ":" + document.Id + ":", StringComparison.Ordinal) : queue.ResourceId != batch.Id + ":" + document.Id)
                || queue.Recipient.Length > 0 && !queue.Recipient.Equals(document.Email, StringComparison.OrdinalIgnoreCase))) queue = null;
            var status = queue is null ? "Unknown" : queue.DeliveryStatus is "Bounced" or "Deferred" or "Delivered" ? queue.DeliveryStatus
                : queue.Status.Equals("Sent", StringComparison.OrdinalIgnoreCase) || queue.SentAt.HasValue || queue.LastSentAt.HasValue ? "Sent"
                : queue.Status.ToUpperInvariant() switch { "PENDING" or "RETRY" or "EXCELPENDING" or "EXCELRETRY" or "PROCESSING" or "SENDING" => "Queued", "FAILED" => "Failed", _ => "Unknown" };
            var message = status switch
            {
                "Sent" => "SMTP accepted the message. Recipient delivery is not confirmed; no delivery report has been received.",
                "Bounced" => "Not delivered: " + queue!.DeliveryError,
                "Deferred" => "Delivery delayed: " + queue!.DeliveryError,
                "Delivered" => "Recipient server confirmed delivery. " + queue!.DeliveryError,
                "Queued" => "Already queued or being retried. A new send will skip this payslip.",
                "Failed" => "Delivery failed. Review the existing item in Security > Apps > Notifications; a new email will not be created.",
                _ => "A previous send is recorded, but its delivery status cannot be verified. Review Security > Apps > Notifications before retrying."
            };
            foreach (var rowId in document.RowIds.Where(rowIds.Contains).Distinct(StringComparer.Ordinal))
            {
                if (!history.TryGetValue(rowId, out var items)) history[rowId] = items = [];
                items.Add(new() { Mode = valid ? requestCopy ? "Combined" : DeliveryMode(document) : "Unknown", Status = status, Email = valid ? document.Email : "",
                    SentCopyStatus = queue?.SentCopyStatus ?? "Unknown",
                    QueuedAtUtc = valid ? Utc(document.CreatedAtUtc) ?? Utc(queue?.CreatedAt) : null,
                    SentAtUtc = Utc(queue?.SentAt ?? queue?.LastSentAt), Message = message });
            }
        }
        // Older receipts may survive a removed queue/descriptor. Never interpret their stored "Queued" as delivered.
        foreach (var receipt in state.Requests.Values)
        {
            if (receipt.ValueKind != JsonValueKind.Object || !receipt.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object
                || !result.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("rowId", out var id) || id.ValueKind != JsonValueKind.String
                    || !item.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String
                    || status.GetString() is not ("Queued" or "Already queued" or "Sent" or "Skipped")) continue;
                var rowId = id.GetString()!;
                if (!rowIds.Contains(rowId)) continue;
                var email = item.TryGetProperty("email", out var address) && address.ValueKind == JsonValueKind.String ? address.GetString() ?? "" : "";
                if (history.TryGetValue(rowId, out var known) && known.Any(x => x.Email.Equals(email, StringComparison.OrdinalIgnoreCase))) continue;
                if (!history.TryGetValue(rowId, out var old)) history[rowId] = old = [];
                old.Add(new() { Mode = "Unknown", Status = "Unknown", Email = email,
                    Message = "A previous send is recorded, but delivery details are unavailable. Review Security > Apps > Notifications before retrying." });
            }
        }
        static int Priority(string status) => status switch { "Delivered" => 6, "Bounced" or "Deferred" => 5, "Sent" => 4, "Queued" => 3, "Unknown" => 2, _ => 1 };
        return new() { Items = batch.Rows.Select(row =>
        {
            var items = history.GetValueOrDefault(row.Id) ?? [];
            var personal = items.Where(x => x.Mode != "Combined").OrderByDescending(x => Priority(x.Status)).ThenByDescending(x => x.QueuedAtUtc).FirstOrDefault();
            return new ExcelPayslipRowDeliveryStatus { RowId = row.Id, Email = personal?.Email ?? row.Email,
                Status = personal?.Status ?? "Pending", CanSend = personal is null,
                SentCopyStatus = personal?.SentCopyStatus ?? "Unknown", QueuedAtUtc = personal?.QueuedAtUtc, SentAtUtc = personal?.SentAtUtc,
                Message = personal?.Message ?? (TryEmail(row.Email, out _) ? "No individual employee email has been queued for this payslip." : "Employee email is missing or invalid. PDF export and a coordinator copy remain available."),
                RecipientDeliveries = items };
        }).ToArray() };
    }

    internal static string DeliveryMode(NotificationRepository.ExcelPayslipMailDocument document) => document.DeliveryMode is "Individual" or "Combined"
        ? document.DeliveryMode : document.RowIds.Count > 1 ? "Combined" : "Individual";

    private static IEnumerable<(string Key, NotificationRepository.ExcelPayslipMailDocument Document, bool RequestCopy)> MailDocuments(ExcelPayslipSendState state)
    {
        foreach (var item in state.Deliveries.Where(x => x.Value is not null)) yield return (item.Key, item.Value, false);
        foreach (var item in state.Requests.Where(x => x.Key.StartsWith("email-request:", StringComparison.Ordinal)))
        {
            NotificationRepository.ExcelPayslipMailDocument? document = null;
            try { document = item.Value.Deserialize<NotificationRepository.ExcelPayslipMailDocument>(JsonOptions); } catch (JsonException) { }
            if (document is not null) yield return (item.Key["email-request:".Length..], document, true);
        }
    }

    internal static bool CanQueueDelivery(ExcelPayslipRowDeliveryStatus previous, string mode, string email) =>
        (mode != "Individual" || previous.CanSend)
        && !previous.RecipientDeliveries.Any(x => x.Mode == "Unknown" || x.Status == "Unknown" || x.Email.Equals(email.Trim(), StringComparison.OrdinalIgnoreCase))
        && (previous.RecipientDeliveries.Count > 0 || previous.CanSend);

    internal static ExcelPayslipDeliveryResult ReviewDeliverySelection(IReadOnlyList<ExcelPayslipRow> selected,
        SendExcelPayslipsRequest request, ExcelPayslipDeliveryStatuses statuses)
    {
        var lookup = statuses.Items.ToDictionary(item => item.RowId, StringComparer.Ordinal);
        var recipients = request.Mode == "Individual" ? ReviewIndividualRecipients(selected, request.EmailOverrides)
            : new ExcelPayslipDeliveryResult { Items = selected.Select(row => DeliveryItem(row, request.Email.Trim(), "Ready", "")).ToList() };
        var recipientLookup = recipients.Items.ToDictionary(item => item.RowId, StringComparer.Ordinal);
        foreach (var row in selected)
        {
            if (!lookup.TryGetValue(row.Id, out var previous) || !CanQueueDelivery(previous, request.Mode,
                request.Mode == "Individual" ? recipientLookup[row.Id].Email : request.Email))
            {
                var item = recipientLookup[row.Id];
                item.Status = "Skipped"; item.Email = request.Mode == "Combined" ? request.Email.Trim() : previous?.Email ?? "";
                item.Message = request.Mode == "Combined" && previous is not null
                    ? "A coordinator copy to this recipient already has delivery activity. Review that existing email; no duplicate was queued."
                    : previous?.Message ?? "Delivery status is unavailable. Refresh before sending this payslip.";
            }
        }
        return recipients;
    }

    // The same planner serves individual and combined sends; an all-skipped selection never touches SMTP/PDF generation.
    internal static async Task<(ExcelPayslipDeliveryResult? Item, string? Error)> DispatchDeliverySelectionAsync(
        IReadOnlyList<ExcelPayslipRow> selected, SendExcelPayslipsRequest request, ExcelPayslipDeliveryResult reviewed,
        Func<Task<string?>> ready, Func<IReadOnlyList<ExcelPayslipRow>, string, Task<string?>> queue)
    {
        var recipients = reviewed.Items.Where(item => item.Status == "Ready").ToDictionary(item => item.RowId);
        var pending = selected.Where(row => recipients.ContainsKey(row.Id)).ToArray();
        if (pending.Length == 0) return (reviewed, null);
        var error = await ready(); if (error is not null) return (null, error);
        var groups = request.Mode == "Combined" ? new[] { pending } : pending.Select(row => new[] { row });
        foreach (var rows in groups)
        {
            var email = request.Mode == "Combined" ? request.Email.Trim() : recipients[rows[0].Id].Email;
            error = await queue(rows, email); if (error is not null) return (null, error);
            foreach (var row in rows)
            {
                var item = recipients[row.Id]; item.Status = "Queued";
                item.Message = request.Mode == "Combined" ? "Combined PDF queued to the specified recipient." : "Individual PDF queued to this recipient.";
            }
        }
        return (reviewed, null);
    }
}
