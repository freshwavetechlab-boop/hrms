using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayslipRepository
{
    // Requests use the existing notification queue, never the payslip delivery history.
    public async Task<ExcelPayslipEmailRequestResult> RequestEmailIdsAsync(int clientId, string batchId, ExcelPayslipEmailRequest request, string actor)
    {
        if (!Guid.TryParseExact(batchId, "N", out _) || !Guid.TryParse(request.RequestId, out var requestId))
            throw new InvalidOperationException("A saved batch and unique request ID are required.");
        if (!TryEmail(request.Email, out var email)) throw new InvalidOperationException("Enter one valid recipient email.");
        await using var db = Db(); await db.OpenAsync(); await using var tx = await db.BeginTransactionAsync();
        if (!await SupportedAsync(db, clientId, tx)) throw new InvalidOperationException("Select an active client.");
        await db.ExecuteScalarAsync<string?>("SELECT id FROM excel_payslip_batches WHERE client_id=@ClientId AND id=@Id FOR UPDATE", new { ClientId = clientId, Id = batchId }, tx);
        var batch = await ReadBatchAsync(db, clientId, batchId, tx) ?? throw new InvalidOperationException("The saved batch was not found for this client.");
        var selected = SelectEmailRequestRows(batch, request.RowIds);
        var prefix = batchId + ":" + requestId.ToString("N") + ":";
        var resourceId = prefix + EmailRequestFingerprint(email, selected);
        var existing = await db.QuerySingleOrDefaultAsync<NotificationQueueItem>(@"SELECT * FROM notification_queue
WHERE ClientId=@ClientId AND EventCode=@EventCode AND ResourceType='ExcelPayslipEmailRequest' AND ResourceId LIKE @Prefix",
            new { ClientId = clientId, EventCode = NotificationRepository.ExcelPayslipEmailRequestEvent, Prefix = prefix + "%" }, tx);
        if (existing is not null)
        {
            if (existing.ResourceId != resourceId) throw new InvalidOperationException("This request ID is already used for another selection or recipient.");
            return new(existing.Id, email, selected.Count, existing.Status == "Sent" ? "Sent" : "Queued");
        }
        if (await notifications.ExcelPayslipDeliveryReadyAsync(db, tx) is string readyError) throw new InvalidOperationException(readyError);
        var template = await NotificationRepository.ReadExcelPayslipTemplateAsync(db, tx, NotificationRepository.ExcelPayslipEmailRequestTemplateCode);
        var content = NotificationRepository.PrepareExcelPayslipEmailRequest(batch, selected, email, actor, template);
        var queueId = await db.ExecuteScalarAsync<long>(@"INSERT INTO notification_queue
(RuleId,EventCode,ResourceType,ResourceId,ClientId,ToJson,CcJson,BccJson,Subject,BodyHtml,Status)
VALUES(NULL,@EventCode,'ExcelPayslipEmailRequest',@ResourceId,@ClientId,@ToJson,'[]','[]',@Subject,@BodyHtml,@Status); SELECT LAST_INSERT_ID();",
            new { EventCode = NotificationRepository.ExcelPayslipEmailRequestEvent, ResourceId = resourceId, batch.ClientId,
                ToJson = JsonSerializer.Serialize(new[] { email }), content.Subject, content.BodyHtml, Status = NotificationRepository.ExcelPayslipPendingStatus }, tx);
        await tx.CommitAsync();
        return new(queueId, email, selected.Count, "Queued");
    }

    internal static IReadOnlyList<ExcelPayslipRow> SelectEmailRequestRows(ExcelPayslipBatch batch, List<string>? rowIds)
    {
        if (rowIds is null || rowIds.Count is < 1 or > 1000) throw new InvalidOperationException("Select up to 1,000 employees with missing email IDs.");
        var (rows, error) = SelectRows(batch, new() { RowIds = rowIds, AcknowledgeWarnings = true });
        if (error is not null) throw new InvalidOperationException(error);
        if (rows.Any(row => TryEmail(row.Email, out _))) throw new InvalidOperationException("Request email IDs only for employees whose saved email is missing or invalid.");
        return rows;
    }

    internal static string EmailRequestLocation(ExcelPayslipRow row) => row.Information
        .FirstOrDefault(item => EmployeeLocationLabels.Contains(IdentityLabel(item.Label)))?.Value ?? "";

    // A 128-bit digest keeps batch + request + fingerprint within the existing VARCHAR(120) reference.
    internal static string EmailRequestFingerprint(string email, IReadOnlyList<ExcelPayslipRow> rows) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { Email = email.ToLowerInvariant(), Rows = rows.Select(row => row.Id).Order(StringComparer.Ordinal) }))))[..32];
}
