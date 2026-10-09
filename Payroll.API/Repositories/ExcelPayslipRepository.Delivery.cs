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
    }

    private static async Task<IReadOnlyList<ExcelPayslipQueueStatus>> ReadDeliveryQueueAsync(MySqlConnection db, int clientId,
        ExcelPayslipSendState state, MySqlTransaction? tx = null)
    {
        var ids = state.Deliveries.Values.Where(document => document is not null && document.QueueId > 0).Select(document => document.QueueId).Distinct().ToArray();
        return await ReadDeliveryQueueAsync(db, [clientId], ids, tx);
    }

    private static async Task<IReadOnlyList<ExcelPayslipQueueStatus>> ReadDeliveryQueueAsync(MySqlConnection db, int[] clientIds,
        long[] ids, MySqlTransaction? tx = null)
    {
        if (ids.Length == 0) return [];
        return (await db.QueryAsync<ExcelPayslipQueueStatus>(@"SELECT queue.Id,queue.ClientId,queue.EventCode,queue.ResourceType,queue.ResourceId,
queue.Status,queue.CreatedAt,queue.SentAt,
(SELECT MAX(log.CreatedAt) FROM notification_logs log WHERE log.QueueId=queue.Id AND log.Status='Sent'
 AND log.EventCode=queue.EventCode AND log.ResourceType=queue.ResourceType AND log.ResourceId=queue.ResourceId) AS LastSentAt
FROM notification_queue queue WHERE queue.ClientId IN @ClientIds AND queue.Id IN @Ids
 AND queue.EventCode='EXCEL_PAYSLIP.SEND' AND queue.ResourceType='ExcelPayslipBatch'",
            new { ClientIds = clientIds, Ids = ids }, tx)).ToArray();
    }

    internal static ExcelPayslipDeliveryStatuses BuildDeliveryStatuses(ExcelPayslipBatch batch, ExcelPayslipSendState state,
        IEnumerable<ExcelPayslipQueueStatus> queueRows)
    {
        var queues = queueRows.ToDictionary(row => row.Id);
        var history = new Dictionary<string, List<ExcelPayslipRowDeliveryStatus>>(StringComparer.Ordinal);
        var rowIds = batch.Rows.Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
        static DateTime? Utc(DateTime? value) => value is null || value == default(DateTime) ? null : DateTime.SpecifyKind(value.Value, DateTimeKind.Utc);
        foreach (var (key, document) in state.Deliveries)
        {
            if (document is null) continue;
            var valid = document.Id == key && document.ClientId == batch.ClientId && document.BatchId == batch.Id;
            var queue = valid ? queues.GetValueOrDefault(document.QueueId) : null;
            if (queue is not null && (queue.ClientId != batch.ClientId || queue.EventCode != NotificationRepository.ExcelPayslipEvent
                || queue.ResourceType != "ExcelPayslipBatch" || queue.ResourceId != batch.Id + ":" + document.Id)) queue = null;
            var status = queue is null ? "Unknown" : queue.Status.Equals("Sent", StringComparison.OrdinalIgnoreCase) || queue.SentAt.HasValue || queue.LastSentAt.HasValue ? "Sent"
                : queue.Status.ToUpperInvariant() switch { "PENDING" or "RETRY" or "EXCELPENDING" or "EXCELRETRY" or "PROCESSING" or "SENDING" => "Queued", "FAILED" => "Failed", _ => "Unknown" };
            var message = status switch
            {
                "Sent" => "Already sent to the shown recipient. A new send will skip this payslip.",
                "Queued" => "Already queued or being retried. A new send will skip this payslip.",
                "Failed" => "Delivery failed. Review or retry the existing item in Settings > Notifications; a new email will not be created.",
                _ => "A previous send is recorded, but its delivery status cannot be verified. Review Settings > Notifications before retrying."
            };
            foreach (var rowId in document.RowIds.Where(rowIds.Contains).Distinct(StringComparer.Ordinal))
            {
                if (!history.TryGetValue(rowId, out var items)) history[rowId] = items = [];
                items.Add(new() { RowId = rowId, Status = status, Email = valid ? document.Email : "", CanSend = false,
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
                if (!rowIds.Contains(rowId) || history.ContainsKey(rowId)) continue;
                history[rowId] = [new() { RowId = rowId, Status = "Unknown", CanSend = false,
                    Message = "A previous send is recorded, but delivery details are unavailable. Review Settings > Notifications before retrying." }];
            }
        }
        static int Priority(string status) => status switch { "Sent" => 4, "Queued" => 3, "Unknown" => 2, _ => 1 };
        return new() { Items = batch.Rows.Select(row => history.TryGetValue(row.Id, out var items)
            ? items.OrderByDescending(item => Priority(item.Status)).ThenByDescending(item => item.QueuedAtUtc).First()
            : new ExcelPayslipRowDeliveryStatus { RowId = row.Id, Email = row.Email,
                Message = TryEmail(row.Email, out _) ? "No email has been queued for this payslip." : "No email has been queued. Add a valid recipient before sending." }).ToArray() };
    }

    internal static ExcelPayslipDeliveryResult ReviewDeliverySelection(IReadOnlyList<ExcelPayslipRow> selected,
        SendExcelPayslipsRequest request, ExcelPayslipDeliveryStatuses statuses)
    {
        var lookup = statuses.Items.ToDictionary(item => item.RowId, StringComparer.Ordinal);
        var recipients = request.Mode == "Individual" ? ReviewIndividualRecipients(selected, request.EmailOverrides)
            : new ExcelPayslipDeliveryResult { Items = selected.Select(row => DeliveryItem(row, request.Email.Trim(), "Ready", "")).ToList() };
        var recipientLookup = recipients.Items.ToDictionary(item => item.RowId, StringComparer.Ordinal);
        foreach (var row in selected)
        {
            if (!lookup.TryGetValue(row.Id, out var previous) || !previous.CanSend)
            {
                var item = recipientLookup[row.Id];
                item.Status = "Skipped"; item.Email = previous?.Email ?? "";
                item.Message = previous?.Message ?? "Delivery status is unavailable. Refresh before sending this payslip.";
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
