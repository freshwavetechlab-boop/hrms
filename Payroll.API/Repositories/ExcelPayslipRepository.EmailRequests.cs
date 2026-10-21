using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayslipRepository
{
    // Requests use the existing queue; attached PDFs share recipient coverage with ordinary coordinator copies.
    public async Task<ExcelPayslipEmailRequestResult> RequestEmailIdsAsync(int clientId, string batchId, ExcelPayslipEmailRequest request, string actor)
    {
        if (!Guid.TryParseExact(batchId, "N", out _) || !Guid.TryParse(request.RequestId, out var requestId))
            throw new InvalidOperationException("A saved batch and unique request ID are required.");
        if (!TryEmail(request.Email, out var email)) throw new InvalidOperationException("Enter one valid recipient email.");
        await using var db = Db(); await db.OpenAsync(); await using var tx = await db.BeginTransactionAsync();
        if (!await SupportedAsync(db, clientId, tx)) throw new InvalidOperationException("Select an active client.");
        var stateJson = await db.ExecuteScalarAsync<string?>("SELECT CAST(send_state_json AS CHAR) FROM excel_payslip_batches WHERE client_id=@ClientId AND id=@Id FOR UPDATE", new { ClientId = clientId, Id = batchId }, tx);
        var batch = await ReadBatchAsync(db, clientId, batchId, tx) ?? throw new InvalidOperationException("The saved batch was not found for this client.");
        await AnnotateEmploymentAsync(db, batch, tx);
        var selected = SelectEmailRequestRows(batch, request.RowIds, request);
        var prefix = batchId + ":" + requestId.ToString("N") + ":";
        var fingerprint = EmailRequestFingerprint(email, selected, request, request.AttachPayslips ? NotificationRepository.ExcelPayslipBatchHash(batch) : null);
        var resourceId = prefix + fingerprint;
        var existing = await db.QuerySingleOrDefaultAsync<NotificationQueueItem>(@"SELECT * FROM notification_queue
WHERE ClientId=@ClientId AND EventCode IN (@EventCode,@PdfEventCode) AND ResourceType='ExcelPayslipEmailRequest'
 AND (ResourceId LIKE @Prefix OR (ResourceId LIKE @BatchPrefix AND RIGHT(ResourceId,32)=@Fingerprint)) ORDER BY Id DESC LIMIT 1",
            new { ClientId = clientId, EventCode = NotificationRepository.ExcelPayslipEmailRequestEvent,
                PdfEventCode = NotificationRepository.ExcelPayslipEmailRequestPdfEvent, Prefix = prefix + "%", BatchPrefix = batchId + ":%", Fingerprint = fingerprint }, tx);
        if (existing is not null)
        {
            if (!existing.ResourceId.EndsWith(":" + fingerprint, StringComparison.Ordinal)) throw new InvalidOperationException("This request ID is already used for another selection or recipient.");
            if (existing.Status == "Failed") throw new InvalidOperationException($"The existing email request #{existing.Id} failed. Retry that notification instead of creating a duplicate.");
            return new(existing.Id, email, selected.Count, existing.Status == "Sent" ? "Sent" : "Queued", request.AttachPayslips);
        }
        if (request.AttachPayslips)
        {
            var sendState = JsonSerializer.Deserialize<ExcelPayslipSendState>(stateJson ?? "{}", JsonOptions) ?? new();
            var statuses = BuildDeliveryStatuses(batch, sendState, await ReadDeliveryQueueAsync(db, clientId, sendState, tx));
            if (selected.Any(row => statuses.Items.Any(item => item.RowId == row.Id && !CanQueueDelivery(item, "Combined", email))))
                throw new InvalidOperationException("A selected payslip is already queued or has prior delivery activity. Refresh email status and select only remaining unsent employees.");
        }
        if (await notifications.ExcelPayslipDeliveryReadyAsync(db, tx) is string readyError) throw new InvalidOperationException(readyError);
        var template = await NotificationRepository.ReadExcelPayslipTemplateAsync(db, tx, NotificationRepository.ExcelPayslipEmailRequestTemplateCode);
        var content = NotificationRepository.PrepareExcelPayslipEmailRequest(batch, selected, email, actor, template);
        var queueId = await db.ExecuteScalarAsync<long>(@"INSERT INTO notification_queue
(RuleId,EventCode,ResourceType,ResourceId,ClientId,ToJson,CcJson,BccJson,Subject,BodyHtml,Status)
VALUES(NULL,@EventCode,'ExcelPayslipEmailRequest',@ResourceId,@ClientId,@ToJson,'[]','[]',@Subject,@BodyHtml,@Status); SELECT LAST_INSERT_ID();",
            new { EventCode = request.AttachPayslips ? NotificationRepository.ExcelPayslipEmailRequestPdfEvent : NotificationRepository.ExcelPayslipEmailRequestEvent, ResourceId = resourceId, batch.ClientId,
                ToJson = JsonSerializer.Serialize(new[] { email }), content.Subject, content.BodyHtml, Status = NotificationRepository.ExcelPayslipPendingStatus }, tx);
        if (request.AttachPayslips)
        {
            var document = NotificationRepository.PrepareExcelPayslipEmailRequestAttachment(batch, selected, email, actor, template, request);
            document.Id = requestId.ToString("N"); document.QueueId = queueId;
            // The namespaced receipt preserves coordinator coverage without marking employee email delivery.
            var state = JsonNode.Parse(stateJson ?? "{}") as JsonObject ?? throw new InvalidOperationException("The saved send state is invalid.");
            if (state["requests"] is null) state["requests"] = new JsonObject();
            if (state["requests"] is not JsonObject receipts) throw new InvalidOperationException("The saved request state is invalid.");
            receipts[EmailRequestAttachmentKey(document.Id)] = JsonSerializer.SerializeToNode(document, JsonOptions);
            await db.ExecuteAsync("UPDATE excel_payslip_batches SET send_state_json=CAST(@Json AS JSON) WHERE client_id=@ClientId AND id=@Id",
                new { ClientId = clientId, Id = batchId, Json = state.ToJsonString() }, tx);
        }
        await tx.CommitAsync();
        return new(queueId, email, selected.Count, "Queued", request.AttachPayslips);
    }

    internal static IReadOnlyList<ExcelPayslipRow> SelectEmailRequestRows(ExcelPayslipBatch batch, List<string>? rowIds, ExcelPayslipEmailRequest? request = null)
    {
        if (rowIds is null || rowIds.Count is < 1 or > 1000) throw new InvalidOperationException("Select up to 1,000 employees with missing email IDs.");
        var (rows, error) = SelectRows(batch, new() { RowIds = rowIds,
            AcknowledgeWarnings = request?.AttachPayslips != true || request.AcknowledgeWarnings,
            AmountDecimalPlaces = request?.AttachPayslips == true ? request.AmountDecimalPlaces : 0 });
        if (error is not null) throw new InvalidOperationException(error);
        if (rows.Any(row => TryEmail(row.Email, out _))) throw new InvalidOperationException("Request email IDs only for employees whose saved email is missing or invalid.");
        return rows;
    }

    internal static string EmailRequestLocation(ExcelPayslipRow row) => row.Information
        .FirstOrDefault(item => EmployeeLocationLabels.Contains(IdentityLabel(item.Label)))?.Value ?? "";

    // A 128-bit digest keeps batch + request + fingerprint within the existing VARCHAR(120) reference.
    internal static string EmailRequestFingerprint(string email, IReadOnlyList<ExcelPayslipRow> rows, ExcelPayslipEmailRequest? request = null, string? batchHash = null)
    {
        // Retain existing info-only fingerprints for safe retries of requests queued before this option existed.
        var json = request?.AttachPayslips == true
            ? JsonSerializer.Serialize(new { Email = email.ToLowerInvariant(), Rows = rows.Select(row => row.Id).Order(StringComparer.Ordinal),
                request.AttachPayslips, request.IncludeSeal, request.AmountDecimalPlaces, BatchHash = batchHash })
            : JsonSerializer.Serialize(new { Email = email.ToLowerInvariant(), Rows = rows.Select(row => row.Id).Order(StringComparer.Ordinal) });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..32];
    }

    internal static string EmailRequestAttachmentKey(string requestId) => "email-request:" + requestId;
}
