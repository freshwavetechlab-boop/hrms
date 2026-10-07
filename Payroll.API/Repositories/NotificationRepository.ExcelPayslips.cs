using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Dapper;
using MimeKit;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public partial class NotificationRepository
{
    internal const string ExcelPayslipEvent = "EXCEL_PAYSLIP.SEND";
    private const int ExcelPayslipMaximumPdfBytes = 10 * 1024 * 1024;

    internal async Task<string?> ExcelPayslipDeliveryReadyAsync(MySqlConnection db, MySqlTransaction tx)
    {
        if (DeliverySuppressed) return "Outbound delivery is suppressed for this application instance.";
        var ready = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM notification_smtp_settings
WHERE Id=1 AND IsEnabled=TRUE AND DeliveryPaused=FALSE AND Host<>'' AND FromEmail<>''", transaction: tx);
        return ready == 0 ? "Email delivery is not configured or is currently paused. Review Settings > Notifications." : null;
    }

    // Called only by the explicit Excel batch send action. The document and queue row share its transaction.
    internal async Task<ExcelPayslipMailDocument> QueueExcelPayslipPdfAsync(MySqlConnection db, MySqlTransaction tx, ExcelPayslipBatch batch,
        IReadOnlyList<ExcelPayslipRow> rows, string email, byte[] pdf, string requestId, string actor, bool includeSeal, int amountDecimalPlaces)
    {
        var document = PrepareExcelPayslipMail(batch, rows, email, pdf, requestId, actor, includeSeal, amountDecimalPlaces);
        var queueId = await db.ExecuteScalarAsync<long>(@"INSERT INTO notification_queue
(RuleId,EventCode,ResourceType,ResourceId,ClientId,ToJson,CcJson,BccJson,Subject,BodyHtml,Status)
VALUES(NULL,@EventCode,'ExcelPayslipBatch',@ResourceId,@ClientId,@ToJson,'[]','[]',@Subject,@BodyHtml,'Pending');
SELECT LAST_INSERT_ID();", new
        {
            EventCode = ExcelPayslipEvent, ResourceId = batch.Id + ":" + document.Id, batch.ClientId,
            ToJson = JsonSerializer.Serialize(new[] { document.Email }), document.Subject, document.BodyHtml
        }, tx);
        document.QueueId = queueId;
        return document; // The caller saves this descriptor beside the batch in the same transaction.
    }

    internal static string? ValidateExcelPayslipPdf(byte[]? pdf)
    {
        if (pdf is null || pdf.Length < 8 || !pdf.AsSpan(0, 5).SequenceEqual("%PDF-"u8)) return "The payslip PDF could not be prepared.";
        return pdf.Length > ExcelPayslipMaximumPdfBytes ? "The PDF exceeds the 10 MiB email attachment limit. Select fewer source rows." : null;
    }

    internal static ExcelPayslipMailDocument PrepareExcelPayslipMail(ExcelPayslipBatch batch, IReadOnlyList<ExcelPayslipRow> rows,
        string email, byte[] pdf, string requestId, string actor, bool includeSeal, int amountDecimalPlaces)
    {
        if (!ExcelPayslipRepository.TryEmail(email, out var recipient)) throw new InvalidOperationException("A single valid email recipient is required.");
        if (ValidateExcelPayslipPdf(pdf) is string error) throw new InvalidOperationException(error);
        var selection = ExcelPayslipRepository.SelectRows(batch, new() { RowIds = rows.Select(r => r.Id).ToList(), AmountDecimalPlaces = amountDecimalPlaces, AcknowledgeWarnings = true });
        if (selection.Error is not null) throw new InvalidOperationException(selection.Error);
        var individual = rows.Count == 1;
        var description = individual ? $"Payslip for {rows[0].EmployeeName}" : $"{rows.Count} payslips";
        var clientName = string.IsNullOrWhiteSpace(batch.ClientName) ? "Excel" : Regex.Replace(batch.ClientName, @"[\r\n\t]+", " ").Trim();
        var filePrefix = Regex.Replace(clientName, @"[^\p{L}\p{N}._-]+", "-").Trim('-');
        if (filePrefix.Length > 80) filePrefix = filePrefix[..80];
        if (filePrefix.Length == 0) filePrefix = "Excel";
        return new ExcelPayslipMailDocument
        {
            Id = Guid.NewGuid().ToString("N"), ClientId = batch.ClientId, BatchId = batch.Id, RequestId = requestId,
            RowIds = rows.Select(r => r.Id).ToList(), Email = recipient,
            Subject = $"{clientName} payslip{(individual ? "" : "s")} - {batch.Month}",
            BodyHtml = $"<p>{WebUtility.HtmlEncode(description)} for {WebUtility.HtmlEncode(batch.Month)} {(individual ? "is" : "are")} attached as a PDF.</p><p>GA DIGITAL WEB WORD PVT. LTD.</p>",
            FileName = individual ? $"{filePrefix}-payslip-{batch.Month}-row-{rows[0].SourceRow}.pdf" : $"{filePrefix}-payslips-{batch.Month}-{rows.Count}.pdf",
            IncludeSeal = includeSeal, AmountDecimalPlaces = amountDecimalPlaces, BatchSha256 = ExcelPayslipBatchHash(batch),
            CreatedAtUtc = DateTime.UtcNow, CreatedBy = actor.Length <= 190 ? actor : actor[..190]
        };
    }

    private async Task AttachExcelPayslipPdfAsync(MySqlConnection db, NotificationQueueItem row, BodyBuilder builder)
    {
        var ids = row.ResourceId?.Split(':');
        if (row.ClientId is not int clientId || ids is not { Length: 2 } || !Guid.TryParseExact(ids[0], "N", out _) || !Guid.TryParseExact(ids[1], "N", out _))
            throw new InvalidOperationException("The queued Excel payslip reference is invalid.");
        var stored = await db.QuerySingleOrDefaultAsync<ExcelPayslipMailSource>(@"SELECT CAST(batch_json AS CHAR) AS BatchJson,
CAST(send_state_json AS CHAR) AS SendStateJson FROM excel_payslip_batches WHERE client_id=@ClientId AND id=@BatchId",
            new { ClientId = clientId, BatchId = ids[0] });
        var batch = stored is null ? null : JsonSerializer.Deserialize<ExcelPayslipBatch>(stored.BatchJson, ExcelPayslipRepository.JsonOptions);
        var state = stored is null ? null : JsonSerializer.Deserialize<ExcelPayslipSendState>(stored.SendStateJson, ExcelPayslipRepository.JsonOptions);
        if (batch is null || state?.Deliveries is null || !state.Deliveries.TryGetValue(ids[1], out var document) || document is null)
            throw new InvalidOperationException("The queued Excel payslip batch or delivery information is unavailable.");
        AttachExcelPayslipPdf(builder, row, document, batch);
    }

    internal static void AttachExcelPayslipPdf(BodyBuilder builder, NotificationQueueItem row, ExcelPayslipMailDocument document, ExcelPayslipBatch batch)
    {
        var recipients = ReadEmailArray(row.ToJson);
        if (row.EventCode != ExcelPayslipEvent || row.ResourceType != "ExcelPayslipBatch" || document.QueueId != row.Id || document.ClientId != row.ClientId
            || batch.ClientId != document.ClientId || batch.Id != document.BatchId || row.ResourceId != batch.Id + ":" + document.Id
            || !ExcelPayslipRepository.TryEmail(document.Email, out _) || recipients.Count != 1 || !string.Equals(recipients[0], document.Email, StringComparison.OrdinalIgnoreCase)
            || ReadEmailArray(row.CcJson).Count != 0 || ReadEmailArray(row.BccJson).Count != 0)
            throw new InvalidOperationException("The queued Excel payslip recipient or source does not match its saved delivery information.");
        if (!ExcelPayslipBatchHash(batch).Equals(document.BatchSha256, StringComparison.Ordinal)) throw new InvalidOperationException("The saved payslip batch failed its integrity check.");
        var (selected, selectionError) = ExcelPayslipRepository.SelectRows(batch, new() { RowIds = document.RowIds, AmountDecimalPlaces = document.AmountDecimalPlaces, AcknowledgeWarnings = true });
        if (selectionError is not null) throw new InvalidOperationException(selectionError);
        var pdf = new ExcelPayslipPdfService().Create(batch, selected, document.IncludeSeal, document.AmountDecimalPlaces);
        if (ValidateExcelPayslipPdf(pdf) is string error) throw new InvalidOperationException(error);
        builder.Attachments.Add(document.FileName, pdf, new ContentType("application", "pdf"));
    }

    private static string ExcelPayslipBatchHash(ExcelPayslipBatch batch) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(batch, ExcelPayslipRepository.JsonOptions))));

    private sealed class ExcelPayslipMailSource
    {
        public string BatchJson { get; set; } = "";
        public string SendStateJson { get; set; } = "";
    }

    internal sealed class ExcelPayslipMailDocument
    {
        public string Id { get; set; } = "";
        public long QueueId { get; set; }
        public int ClientId { get; set; }
        public string BatchId { get; set; } = "";
        public string RequestId { get; set; } = "";
        public List<string> RowIds { get; set; } = [];
        public string Email { get; set; } = "";
        public string Subject { get; set; } = "";
        public string BodyHtml { get; set; } = "";
        public string FileName { get; set; } = "";
        [JsonRequired] public bool IncludeSeal { get; set; }
        [JsonRequired] public int AmountDecimalPlaces { get; set; }
        public string BatchSha256 { get; set; } = "";
        public string CreatedBy { get; set; } = "";
        public DateTime CreatedAtUtc { get; set; }
    }
}
