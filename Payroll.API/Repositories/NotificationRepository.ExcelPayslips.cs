using System.Net;
using System.Globalization;
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
    internal const string ExcelPayslipTemplateCode = "EXCEL_PAYSLIP_SEND_DEFAULT";
    internal const string ExcelPayslipEmailRequestEvent = "EXCEL_PAYSLIP.EMAIL_REQUEST";
    internal const string ExcelPayslipEmailRequestTemplateCode = "EXCEL_PAYSLIP_EMAIL_REQUEST_DEFAULT";
    // Earlier workers on a shared database must not render the old PDF or send data-URL logos.
    internal const string ExcelPayslipPendingStatus = "ExcelPending";
    internal const string ExcelPayslipRetryStatus = "ExcelRetry";
    private const int ExcelPayslipMaximumPdfBytes = 10 * 1024 * 1024;

    internal static async Task<NotificationTemplate> ReadExcelPayslipTemplateAsync(MySqlConnection db, MySqlTransaction tx, string code = ExcelPayslipTemplateCode)
    {
        var template = await db.QuerySingleOrDefaultAsync<NotificationTemplate>(
            "SELECT * FROM notification_templates WHERE Code=@Code AND IsActive=TRUE", new { Code = code }, tx);
        ValidateExcelPayslipTemplate(template, code);
        return template!;
    }

    private static void ValidateExcelPayslipTemplate(NotificationTemplate? template, string code = ExcelPayslipTemplateCode)
    {
        if (template is null || !template.IsActive || string.IsNullOrWhiteSpace(template.SubjectTemplate) || string.IsNullOrWhiteSpace(template.BodyTemplate))
            throw new InvalidOperationException($"Configure the active {code} email template with a subject and body in Settings > Notifications > Automation email templates.");
    }

    internal static (string Subject, string BodyHtml) PrepareExcelPayslipEmailRequest(ExcelPayslipBatch batch,
        IReadOnlyList<ExcelPayslipRow> rows, string email, string actor, NotificationTemplate template)
    {
        ValidateExcelPayslipTemplate(template, ExcelPayslipEmailRequestTemplateCode);
        if (!template.BodyTemplate.Contains("{{employeeTable}}", StringComparison.Ordinal))
            throw new InvalidOperationException("Add {{employeeTable}} to the missing email IDs request template.");
        var marker = Guid.NewGuid().ToString("N");
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["clientName"] = batch.ClientName, ["month"] = batch.Month,
            ["monthLabel"] = DateTime.ParseExact(batch.Month, "yyyy-MM", CultureInfo.InvariantCulture).ToString("MMMM yyyy", CultureInfo.InvariantCulture),
            ["employeeCount"] = rows.Count.ToString(CultureInfo.InvariantCulture), ["recipientEmail"] = email, ["requestedBy"] = actor,
            ["employeeTable"] = marker
        };
        string Encode(string value) => WebUtility.HtmlEncode(value);
        var employeeTable = "<table style=\"width:100%;border-collapse:collapse;font-family:Arial,sans-serif;font-size:14px;\"><thead><tr><th>Employee Code</th><th>Employee Name</th><th>Work Location</th></tr></thead><tbody>"
            + string.Concat(rows.Select(row => "<tr>" + string.Concat(new[] { row.EmployeeCode, row.EmployeeName, ExcelPayslipRepository.EmailRequestLocation(row) }
                .Select(value => "<td style=\"padding:10px;border:1px solid #dbe3ee;\">" + Encode(value) + "</td>")) + "</tr>")) + "</tbody></table>";
        if (!template.IsHtml) employeeTable = string.Join("<br />", rows.Select(row => Encode($"{row.EmployeeCode} | {row.EmployeeName} | {ExcelPayslipRepository.EmailRequestLocation(row)}")));
        var body = template.IsHtml ? Render(template.BodyTemplate, values)
            : "<p>" + Render(Encode(template.BodyTemplate), values).Replace("\r\n", "\n").Replace("\n", "<br />") + "</p>";
        var subject = Regex.Replace(WebUtility.HtmlDecode(Render(template.SubjectTemplate, values)), @"[\r\n\t]+", " ").Trim();
        if (subject.Length == 0) throw new InvalidOperationException("The email request template resolved to an empty subject.");
        return (subject, body.Replace(marker, employeeTable));
    }

    internal async Task<string?> ExcelPayslipDeliveryReadyAsync(MySqlConnection db, MySqlTransaction tx)
    {
        if (DeliverySuppressed) return "Outbound delivery is suppressed for this application instance.";
        var ready = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM notification_smtp_settings
WHERE Id=1 AND IsEnabled=TRUE AND DeliveryPaused=FALSE AND Host<>'' AND FromEmail<>''", transaction: tx);
        return ready == 0 ? "Email delivery is not configured or is currently paused. Review Settings > Notifications." : null;
    }

    // Called only by the explicit Excel batch send action. The document and queue row share its transaction.
    internal async Task<ExcelPayslipMailDocument> QueueExcelPayslipPdfAsync(MySqlConnection db, MySqlTransaction tx, ExcelPayslipBatch batch,
        IReadOnlyList<ExcelPayslipRow> rows, string email, byte[] pdf, string requestId, string actor, bool includeSeal, int amountDecimalPlaces, NotificationTemplate template)
    {
        var document = PrepareExcelPayslipMail(batch, rows, email, pdf, requestId, actor, includeSeal, amountDecimalPlaces, template);
        var queueId = await db.ExecuteScalarAsync<long>(@"INSERT INTO notification_queue
(RuleId,EventCode,ResourceType,ResourceId,ClientId,ToJson,CcJson,BccJson,Subject,BodyHtml,Status)
VALUES(NULL,@EventCode,'ExcelPayslipBatch',@ResourceId,@ClientId,@ToJson,'[]','[]',@Subject,@BodyHtml,@Status);
SELECT LAST_INSERT_ID();", new
        {
            EventCode = ExcelPayslipEvent, ResourceId = batch.Id + ":" + document.Id, batch.ClientId,
            ToJson = JsonSerializer.Serialize(new[] { document.Email }), document.Subject, document.BodyHtml, Status = ExcelPayslipPendingStatus
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
        string email, byte[] pdf, string requestId, string actor, bool includeSeal, int amountDecimalPlaces, NotificationTemplate template)
    {
        if (ValidateExcelPayslipPdf(pdf) is string error) throw new InvalidOperationException(error);
        return PrepareExcelPayslipMailMetadata(batch, rows, email, requestId, actor, includeSeal, amountDecimalPlaces, template);
    }

    internal static ExcelPayslipMailDocument PrepareExcelPayslipMailMetadata(ExcelPayslipBatch batch, IReadOnlyList<ExcelPayslipRow> rows,
        string email, string requestId, string actor, bool includeSeal, int amountDecimalPlaces, NotificationTemplate template, string? batchHash = null)
    {
        ValidateExcelPayslipTemplate(template);
        if (!ExcelPayslipRepository.TryEmail(email, out var recipient)) throw new InvalidOperationException("A single valid email recipient is required.");
        var selection = ExcelPayslipRepository.SelectRows(batch, new() { RowIds = rows.Select(r => r.Id).ToList(), AmountDecimalPlaces = amountDecimalPlaces, AcknowledgeWarnings = true });
        if (selection.Error is not null) throw new InvalidOperationException(selection.Error);
        var individual = rows.Count == 1;
        var clientName = string.IsNullOrWhiteSpace(batch.ClientName) ? "Excel" : Regex.Replace(batch.ClientName, @"[\r\n\t]+", " ").Trim();
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["clientName"] = clientName, ["month"] = batch.Month,
            ["monthLabel"] = DateTime.TryParseExact(batch.Month, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month)
                ? month.ToString("MMMM yyyy", CultureInfo.InvariantCulture) : batch.Month,
            ["recipientName"] = individual ? rows[0].EmployeeName : "Payroll Team",
            ["employeeName"] = individual ? rows[0].EmployeeName : "", ["employeeCode"] = individual ? rows[0].EmployeeCode : "",
            ["recipientEmail"] = recipient, ["payslipCount"] = rows.Count.ToString(CultureInfo.InvariantCulture), ["requestedBy"] = actor
        };
        var subject = Regex.Replace(WebUtility.HtmlDecode(Render(template.SubjectTemplate, values)), @"[\r\n\t]+", " ").Trim();
        if (subject.Length == 0) throw new InvalidOperationException("The Excel payslip email template resolved to an empty subject.");
        var filePrefix = Regex.Replace(clientName, @"[^\p{L}\p{N}._-]+", "-").Trim('-');
        if (filePrefix.Length > 80) filePrefix = filePrefix[..80];
        if (filePrefix.Length == 0) filePrefix = "Excel";
        return new ExcelPayslipMailDocument
        {
            Id = Guid.NewGuid().ToString("N"), ClientId = batch.ClientId, BatchId = batch.Id, RequestId = requestId,
            RowIds = rows.Select(r => r.Id).ToList(), Email = recipient,
            Subject = subject,
            BodyHtml = template.IsHtml ? Render(template.BodyTemplate, values)
                : "<p>" + Render(WebUtility.HtmlEncode(template.BodyTemplate), values).Replace("\r\n", "\n").Replace("\n", "<br />") + "</p>",
            FileName = individual ? $"{filePrefix}-payslip-{batch.Month}-row-{rows[0].SourceRow}.pdf" : $"{filePrefix}-payslips-{batch.Month}-{rows.Count}.pdf",
            IncludeSeal = includeSeal, AmountDecimalPlaces = amountDecimalPlaces, BatchSha256 = batchHash ?? ExcelPayslipBatchHash(batch),
            CreatedAtUtc = DateTime.UtcNow, CreatedBy = actor.Length <= 190 ? actor : actor[..190]
        };
    }

    // All recipients are accepted in one transaction; the existing worker renders PDFs and sends later.
    internal static async Task QueueExcelPayslipDocumentsAsync(MySqlConnection db, MySqlTransaction tx,
        ExcelPayslipBatch batch, IReadOnlyList<ExcelPayslipMailDocument> documents)
    {
        if (documents.Count == 0) return;
        if (documents.Count > 1000 || documents.Any(document => document.ClientId != batch.ClientId || document.BatchId != batch.Id))
            throw new InvalidOperationException("The queued payslip documents do not match this batch.");
        var parameters = new DynamicParameters(new { batch.ClientId, EventCode = ExcelPayslipEvent, Status = ExcelPayslipPendingStatus });
        var values = new List<string>();
        for (var index = 0; index < documents.Count; index++)
        {
            var document = documents[index];
            parameters.Add("Resource" + index, batch.Id + ":" + document.Id);
            parameters.Add("To" + index, JsonSerializer.Serialize(new[] { document.Email }));
            parameters.Add("Subject" + index, document.Subject); parameters.Add("Body" + index, document.BodyHtml);
            values.Add($"(NULL,@EventCode,'ExcelPayslipBatch',@Resource{index},@ClientId,@To{index},'[]','[]',@Subject{index},@Body{index},@Status)");
        }
        var firstId = await db.ExecuteScalarAsync<long>("INSERT INTO notification_queue(RuleId,EventCode,ResourceType,ResourceId,ClientId,ToJson,CcJson,BccJson,Subject,BodyHtml,Status) VALUES "
            + string.Join(',', values) + "; SELECT LAST_INSERT_ID();", parameters, tx);
        // Do not assume contiguous auto-increment IDs (multi-primary servers may use another increment).
        var queued = (await db.QueryAsync<(long Id, string ResourceId)>(@"SELECT Id,ResourceId FROM notification_queue
WHERE Id>=@FirstId AND ClientId=@ClientId AND EventCode=@EventCode AND ResourceType='ExcelPayslipBatch' AND ResourceId IN @Resources",
            new { FirstId = firstId, batch.ClientId, EventCode = ExcelPayslipEvent, Resources = documents.Select(document => batch.Id + ":" + document.Id).ToArray() }, tx))
            .ToDictionary(item => item.ResourceId, item => item.Id, StringComparer.Ordinal);
        foreach (var document in documents) document.QueueId = queued[batch.Id + ":" + document.Id];
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
        EmbedExcelPayslipImages(builder);
    }

    internal static void EmbedExcelPayslipImages(BodyBuilder builder)
    {
        // Rich-text uploads are saved in the template; email clients receive inline MIME images instead of data URLs.
        builder.HtmlBody = Regex.Replace(builder.HtmlBody ?? "", "\\bsrc\\s*=\\s*([\"'])data:(image/(?:png|jpeg|gif));base64,([A-Za-z0-9+/=\\s]+)\\1", match =>
        {
            var bytes = Convert.FromBase64String(match.Groups[3].Value);
            var image = builder.LinkedResources.Add($"payslip-image-{builder.LinkedResources.Count + 1}", bytes, ContentType.Parse(match.Groups[2].Value));
            image.ContentId = Guid.NewGuid().ToString("N") + "@excel-payslips.frevo";
            return $"src=\"cid:{image.ContentId}\"";
        }, RegexOptions.IgnoreCase);
    }

    internal static string ExcelPayslipBatchHash(ExcelPayslipBatch batch) => Convert.ToHexString(SHA256.HashData(
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
