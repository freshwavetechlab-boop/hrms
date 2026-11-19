using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using MimeKit;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public partial class NotificationRepository
{
    internal const string RegisterPdfEvent = "PAYSLIP.PDF_SEND";
    internal const string RegisterPdfResource = "PayRunPayslip";
    internal const string RegisterPdfPending = "PayslipPdfPending";
    internal const string RegisterPdfRetry = "PayslipPdfRetry";
    private const string RegisterMailPrefix = "payslip_mail:";
    private static readonly JsonSerializerOptions RegisterMailJson = new(JsonSerializerDefaults.Web);

    internal sealed class RegisterMailRecord
    {
        public string Id { get; set; } = "";
        public int ClientId { get; set; }
        public int PayRunId { get; set; }
        public string ClientName { get; set; } = "";
        public string Month { get; set; } = "";
        public int ActorUserId { get; set; }
        public string Fingerprint { get; set; } = "";
        public DateTime CreatedAtUtc { get; set; }
        public bool Resend { get; set; }
        public List<PayslipDeliveryItem> Items { get; set; } = [];
        public Dictionary<int, RegisterMailDocument> Documents { get; set; } = [];
    }

    internal sealed class RegisterMailDocument
    {
        public int EmployeeId { get; set; }
        public int PayRunEmployeeId { get; set; }
        public string PayrollHash { get; set; } = "";
        public string Email { get; set; } = "";
        public long QueueId { get; set; }
        public List<ExcelPayslipRow> Rows { get; set; } = [];
    }

    internal sealed class RegisterMailQueue : NotificationQueueItem
    {
        public int EmployeeId { get; set; }
        public string Recipient { get; set; } = "";
        public string DeliveryStatus { get; set; } = "";
        public string SentCopyStatus { get; set; } = "Unknown";
        public DateTime? LastSentAt { get; set; }
        public string State { get; set; } = "Unknown";
    }

    private static string RegisterMailKey(int runId, string requestId) => RegisterMailPrefix + runId + ":" + requestId;
    internal static string RegisterMailFingerprint(int clientId, int runId, int actorId, PayslipMailJobRequest request) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        { clientId, runId, actorId, ids = request.EmployeeIds.Order().ToArray(), request.Resend, request.AcknowledgeResend }, RegisterMailJson))));

    internal static string RegisterPayrollHash(PayRunEmployee row) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new { row.Id, row.PayRunId, row.ClientId, row.EmployeeId, row.EmployeeCode, row.EmployeeName,
            row.IsSkipped, row.PresentDays, row.PayableDays, row.GrossPay, row.StatutoryDeductions,
            row.OneTimeEarnings, row.OneTimeDeductions, row.NetPay, row.DetailsJson }, RegisterMailJson))));

    internal static string? ValidateRegisterMailRequest(PayslipMailJobRequest request) =>
        !Guid.TryParseExact(request.RequestId, "N", out _) ? "A valid email request ID is required. Refresh and retry."
        : request.EmployeeIds is null || request.EmployeeIds.Count is < 1 or > 1000 || request.EmployeeIds.Any(id => id <= 0)
            || request.EmployeeIds.Distinct().Count() != request.EmployeeIds.Count ? "Select between 1 and 1000 distinct employees."
        : request.Resend != request.AcknowledgeResend ? "Confirm the separate resend action before sending a payslip again." : null;

    private static async Task<List<RegisterMailQueue>> ReadRegisterQueuesAsync(MySqlConnection db, PayRun run, MySqlTransaction? tx = null)
    {
        var rows = (await db.QueryAsync<RegisterMailQueue>("""
SELECT q.*,p.EmployeeId,JSON_UNQUOTE(JSON_EXTRACT(q.ToJson,'$[0]')) Recipient
FROM notification_queue q JOIN payrunemployees p ON p.ClientId=q.ClientId AND p.PayRunId=@RunId
 AND ((q.EventCode='PAYSLIP.SEND' AND q.ResourceType='PayRunEmployee' AND BINARY q.ResourceId=BINARY CAST(p.Id AS CHAR))
 OR (q.EventCode=@Event AND q.ResourceType=@Resource AND q.ResourceId LIKE @Prefix
     AND BINARY SUBSTRING_INDEX(SUBSTRING_INDEX(q.ResourceId,':',2),':',-1)=BINARY CAST(p.Id AS CHAR)))
WHERE q.ClientId=@ClientId ORDER BY q.Id DESC
""", new { RunId = run.Id, run.ClientId, Event = RegisterPdfEvent, Resource = RegisterPdfResource, Prefix = run.Id + ":%" }, tx)).ToList();
        if (rows.Count == 0) return rows;
        var logs = (await db.QueryAsync<NotificationLog>("SELECT * FROM notification_logs WHERE QueueId IN @Ids ORDER BY CreatedAt DESC,Id DESC",
            new { Ids = rows.Select(row => row.Id).ToArray() }, tx)).ToLookup(log => log.QueueId);
        foreach (var row in rows)
        {
            var history = logs[row.Id].Where(log => log.EventCode == row.EventCode && log.ResourceType == row.ResourceType
                && log.ResourceId == row.ResourceId && log.Recipient.Equals(row.Recipient, StringComparison.OrdinalIgnoreCase)).ToArray();
            var sent = history.FirstOrDefault(log => log.Status == "Sent");
            row.LastSentAt = sent?.CreatedAt;
            var report = history.FirstOrDefault(log => log.Status is "Bounced" or "Deferred" or "Delivered"
                && sent is not null && log.CreatedAt >= sent.CreatedAt && log.ProviderMessageId == sent.ProviderMessageId);
            row.DeliveryStatus = report?.Status ?? "";
            var archive = history.FirstOrDefault(log => log.Status.StartsWith("SentCopy", StringComparison.Ordinal)
                && sent is not null && log.CreatedAt >= sent.CreatedAt && log.ProviderMessageId == sent.ProviderMessageId);
            row.SentCopyStatus = archive?.Status["SentCopy".Length..] ?? "Unknown";
            row.State = RegisterQueueState(row);
        }
        return rows;
    }

    internal static string RegisterQueueState(RegisterMailQueue row) => row.DeliveryStatus is "Bounced" or "Deferred" or "Delivered" ? row.DeliveryStatus
        : row.SentAt.HasValue || row.LastSentAt.HasValue || row.Status == "Sent" ? "Sent"
        : row.Status is "Pending" or "Retry" or RegisterPdfPending or RegisterPdfRetry or "Processing" or "Sending" ? "Queued"
        : row.Status == "Failed" ? "Failed" : "Unknown";

    internal static PayslipEmailStatus RegisterEmployeeStatus(PayRunEmployee employee, string email, IEnumerable<RegisterMailQueue> queues)
    {
        var history = queues.Where(q => q.EmployeeId == employee.EmployeeId).OrderByDescending(q => q.Id).ToArray();
        var latest = history.FirstOrDefault();
        var valid = ExcelPayslipRepository.TryEmail(email, out var recipient);
        var blocked = history.Any(q => q.State is "Queued" or "Unknown" or "Deferred");
        return new() { EmployeeId = employee.EmployeeId, EmployeeCode = employee.EmployeeCode, Email = valid ? recipient : email,
            LastRecipient = latest?.Recipient ?? "", Status = latest?.State ?? (valid ? "Not sent" : "Email missing"),
            SentCopyStatus = latest?.SentCopyStatus ?? "Unknown", SentAtUtc = Utc(latest?.SentAt ?? latest?.LastSentAt),
            CanSend = valid && history.Length == 0, CanResend = valid && history.Length > 0 && !blocked,
            Message = !valid ? "Add a valid Work Email in Employee Master."
                : blocked ? "An email is queued, delayed or its outcome is uncertain. Review its existing delivery before resending."
                : latest is null ? "No email has been queued for this employee in this pay run."
                : latest.State is "Sent" or "Delivered" ? "Normal sending skips this employee. Use the separate acknowledged resend action for another copy."
                : "Review the failed delivery, correct the email if needed, then use Resend." };
    }

    private static DateTime? Utc(DateTime? value) => value.HasValue ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc) : null;

    internal static PayslipMailJob RegisterJobSummary(RegisterMailRecord record, IEnumerable<RegisterMailQueue> queueRows)
    {
        var queues = queueRows.ToDictionary(q => q.Id);
        var job = new PayslipMailJob { Id = record.Id, ClientId = record.ClientId, PayRunId = record.PayRunId, Month = record.Month,
            CreatedAtUtc = DateTime.SpecifyKind(record.CreatedAtUtc, DateTimeKind.Utc), Resend = record.Resend, Total = record.Items.Count };
        foreach (var item in record.Items)
        {
            if (item.QueueId == 0) { if (item.Reason == "MissingEmail") job.MissingEmail++; else job.Skipped++; continue; }
            job.TotalEmails++;
            if (!queues.TryGetValue(item.QueueId, out var queue) || queue.ClientId != record.ClientId || queue.EmployeeId != item.EmployeeId
                || queue.EventCode != RegisterPdfEvent || queue.ResourceType != RegisterPdfResource
                || queue.ResourceId != record.PayRunId + ":" + record.Documents.GetValueOrDefault(item.EmployeeId)?.PayRunEmployeeId + ":" + record.Id)
            { job.Unknown++; continue; }
            if (queue.SentAt.HasValue || queue.LastSentAt.HasValue || queue.Status == "Sent")
            {
                job.AcceptedEmails++;
                if (queue.SentCopyStatus == "Saved") job.SentCopySavedEmails++; else job.SentCopyUnverifiedEmails++;
            }
            if (queue.State == "Queued") job.Queued++;
            else if (queue.State is "Failed" or "Bounced") job.Failed++;
            else if (queue.State is "Unknown" or "Deferred") job.Unknown++;
            if (queue.State == "Delivered") job.DeliveredEmails++;
        }
        job.Completed = job.Total - job.Queued;
        job.PercentComplete = job.Total == 0 ? 100 : (int)(100m * job.Completed / job.Total);
        job.IsComplete = job.Queued == 0;
        job.Status = job.Failed + job.MissingEmail + job.Unknown + job.SentCopyUnverifiedEmails > 0 ? "Needs attention" : job.IsComplete ? "Completed" : "Processing";
        job.Message = job.SentCopyUnverifiedEmails > 0 ? "SMTP accepted the email, but its Sent-folder copy is not verified. Do not resend to repair the Sent folder."
            : job.Unknown > 0 ? "Review uncertain or delayed deliveries before resending."
            : job.Failed + job.MissingEmail > 0 ? "Review failed deliveries and missing email addresses."
            : job.IsComplete ? "Email processing finished. SMTP acceptance and Sent copies are shown separately."
            : "Delivery continues in the existing background queue. You may leave this page.";
        return job;
    }

    public async Task<PayslipMailOverview> GetRegisterMailAsync(PayRun run, IReadOnlyDictionary<int, string> emails)
    {
        await using var db = Db(); await db.OpenAsync();
        var queues = await ReadRegisterQueuesAsync(db, run);
        // Poll compact metadata, never the frozen PDF rows or payroll component details.
        var json = await db.QueryAsync<string>("""
SELECT CAST(JSON_REMOVE(SettingsJson,'$.documents') AS CHAR) FROM modulesettings
WHERE client_id=@ClientId AND ModuleCode LIKE @Prefix ORDER BY Id DESC LIMIT 20
""", new { run.ClientId, Prefix = RegisterMailKey(run.Id, "") + "%" });
        var jobs = new List<PayslipMailJob>();
        foreach (var value in json)
        {
            var record = JsonSerializer.Deserialize<RegisterMailRecord>(value, RegisterMailJson);
            if (record is null || record.ClientId != run.ClientId || record.PayRunId != run.Id) continue;
            // Restore just the immutable employee/queue identity needed by the shared progress contract.
            foreach (var item in record.Items.Where(item => item.QueueId > 0))
            {
                var saved = run.Employees.SingleOrDefault(e => e.EmployeeId == item.EmployeeId);
                if (saved is not null) record.Documents[item.EmployeeId] = new() { PayRunEmployeeId = saved.Id };
            }
            jobs.Add(RegisterJobSummary(record, queues));
        }
        return new() { ClientId = run.ClientId, PayRunId = run.Id,
            Items = run.Employees.Where(e => !e.IsSkipped).Select(e => RegisterEmployeeStatus(e, emails.GetValueOrDefault(e.EmployeeId) ?? "", queues)).ToArray(), Jobs = jobs };
    }

    public async Task<(PayslipMailJobSubmission? Item, string? Error)> QueueRegisterMailAsync(PayRun run, PayslipMailJobRequest request,
        IReadOnlyDictionary<int, string> emails, IReadOnlyList<PayrollLocationPayslipDocument> slips, AuthUser actor)
    {
        if (ValidateRegisterMailRequest(request) is { } validation) return (null, validation);
        if (DeliverySuppressed) return (null, "Outbound delivery is suppressed for this application instance.");
        var selected = run.Employees.Where(e => !e.IsSkipped && request.EmployeeIds.Contains(e.EmployeeId)).ToArray();
        if (selected.Length != request.EmployeeIds.Count) return (null, "Every selected employee must belong to this approved pay run.");
        await using var db = Db(); await db.OpenAsync();
        var rule = await db.QueryFirstOrDefaultAsync<NotificationRule>("""
SELECT r.* FROM notification_rules r JOIN notification_templates t ON t.Id=r.TemplateId AND t.IsActive=TRUE
WHERE r.EventCode='PAYSLIP.SEND' AND r.IsEnabled=TRUE AND (r.ClientId=@ClientId OR r.ClientId IS NULL)
ORDER BY r.ClientId IS NULL,r.Id DESC LIMIT 1
""", new { run.ClientId });
        if (rule is null) return (null, "Configure an enabled PAYSLIP.SEND rule in Security > Apps > Notifications.");
        var template = await db.QuerySingleAsync<NotificationTemplate>("SELECT * FROM notification_templates WHERE Id=@Id", new { Id = rule.TemplateId });
        if (!Regex.IsMatch(template.BodyTemplate, @"\{\{\s*payslipHtml\s*\}\}", RegexOptions.IgnoreCase))
            return (null, "The payslip email template must include {{payslipHtml}}.");
        rule.Recipients = (await db.QueryAsync<NotificationRecipient>("SELECT * FROM notification_recipients WHERE RuleId=@Id AND IsActive=TRUE", new { rule.Id })).ToList();
        rule.Parameters = (await db.QueryAsync<NotificationParameterMapping>("SELECT * FROM notification_parameter_mappings WHERE RuleId=@Id AND IsActive=TRUE", new { rule.Id })).ToList();
        var envelopes = new Dictionary<int, (string Email, string To, string Cc, string Bcc, string Subject, string Body)>();
        foreach (var employee in selected)
        {
            if (!ExcelPayslipRepository.TryEmail(emails.GetValueOrDefault(employee.EmployeeId) ?? "", out var email)) continue;
            var documents = slips.Where(s => s.EmployeeId == employee.EmployeeId).ToArray();
            if (documents.Length == 0 || documents.Sum(s => s.NetPay) != employee.NetPay
                || documents.Sum(s => s.GrossPay) != employee.GrossPay + employee.OneTimeEarnings
                || documents.Sum(s => s.TotalDeductions) != employee.StatutoryDeductions + employee.OneTimeDeductions)
                return (null, "Saved location slips do not match the selected payroll. Refresh and review before sending.");
            var evt = new NotificationEvent { ClientId = run.ClientId, ResourceId = employee.Id.ToString(CultureInfo.InvariantCulture),
                ResourceType = "PayRunEmployee", EventCode = "PAYSLIP.SEND", ActorUserId = actor.Id, ActorName = actor.DisplayName, ActorEmail = actor.Email,
                PayloadJson = JsonSerializer.Serialize(new { employeeId = employee.EmployeeId, employeeEmail = email, employee.EmployeeName,
                    employee.EmployeeCode, run.ClientName, run.PayPeriod, payRunId = run.Id }) };
            var values = BuildBaseValues(evt);
            foreach (var mapping in rule.Parameters) values[mapping.ParameterName] = await ResolveParameterAsync(db, mapping, evt, values);
            var to = await ResolveRecipientsAsync(db, rule.Recipients.Where(r => r.RecipientType == "To"), evt, values);
            var cc = await ResolveRecipientsAsync(db, rule.Recipients.Where(r => r.RecipientType == "Cc"), evt, values);
            var bcc = await ResolveRecipientsAsync(db, rule.Recipients.Where(r => r.RecipientType == "Bcc"), evt, values);
            if (to.Count != 1 || !to[0].Equals(email, StringComparison.OrdinalIgnoreCase)
                || cc.Concat(bcc).Any(address => !MailboxAddress.TryParse(address, out _)))
                return (null, "The payslip rule must resolve each employee's Work Email as its single To recipient. Review Notification settings.");
            var subject = Render(template.SubjectTemplate, values);
            if (string.IsNullOrWhiteSpace(subject)) return (null, "Configure the payslip email subject in Notification settings.");
            var bodyTemplate = template.Code == "PAYSLIP_SEND_DEFAULT"
                ? template.BodyTemplate.Replace("printable HTML document", "PDF document", StringComparison.Ordinal) : template.BodyTemplate;
            envelopes[employee.EmployeeId] = (email, JsonSerializer.Serialize(to), JsonSerializer.Serialize(cc), JsonSerializer.Serialize(bcc), subject,
                RenderPayslipBody(bodyTemplate, values, string.Join("<hr />", documents.Select(d => d.Html))));
        }
        await using var tx = await db.BeginTransactionAsync();
        await db.ExecuteScalarAsync<int>("SELECT Id FROM clients WHERE Id=@ClientId FOR UPDATE", new { run.ClientId }, tx);
        var status = await db.ExecuteScalarAsync<string>("SELECT Status FROM payruns WHERE Id=@Id AND ClientId=@ClientId FOR UPDATE", new { run.Id, run.ClientId }, tx);
        if (status is not ("Approved" or "Partially Paid" or "Paid")) return (null, "Only approved payroll can be emailed.");
        var fingerprint = RegisterMailFingerprint(run.ClientId, run.Id, actor.Id, request);
        var key = RegisterMailKey(run.Id, request.RequestId);
        var previousJson = await db.ExecuteScalarAsync<string?>("SELECT CAST(SettingsJson AS CHAR) FROM modulesettings WHERE client_id=@ClientId AND ModuleCode=@Key FOR UPDATE", new { run.ClientId, Key = key }, tx);
        if (previousJson is not null)
        {
            var previous = JsonSerializer.Deserialize<RegisterMailRecord>(previousJson, RegisterMailJson)!;
            if (previous.Fingerprint != fingerprint || previous.ActorUserId != actor.Id) return (null, "This request ID was already used for a different selection. Refresh email progress.");
            return (new() { Job = RegisterJobSummary(previous, await ReadRegisterQueuesAsync(db, run, tx)), Items = previous.Items }, null);
        }
        var current = (await db.QueryAsync<PayRunEmployee>("SELECT * FROM payrunemployees WHERE PayRunId=@Id AND ClientId=@ClientId FOR UPDATE", new { run.Id, run.ClientId }, tx)).ToDictionary(e => e.EmployeeId);
        if (selected.Any(e => !current.TryGetValue(e.EmployeeId, out var saved) || RegisterPayrollHash(saved) != RegisterPayrollHash(e)))
            return (null, "Payroll changed after loading this page. Refresh before sending; nothing was queued.");
        var currentEmails = (await db.QueryAsync<(int Id, string WorkEmail)>("SELECT Id,WorkEmail FROM employees WHERE ClientId=@ClientId AND Id IN @Ids", new { run.ClientId, Ids = request.EmployeeIds }, tx)).ToDictionary(e => e.Id, e => e.WorkEmail ?? "");
        if (selected.Any(e => (currentEmails.GetValueOrDefault(e.EmployeeId) ?? "").Trim() != (emails.GetValueOrDefault(e.EmployeeId) ?? "").Trim()))
            return (null, "Employee emails changed. Refresh email status before sending.");
        if (await ExcelPayslipDeliveryReadyAsync(db, tx) is { } readyError) return (null, readyError);
        var queues = await ReadRegisterQueuesAsync(db, run, tx);
        var record = new RegisterMailRecord { Id = request.RequestId, ClientId = run.ClientId, PayRunId = run.Id, ClientName = run.ClientName,
            Month = run.PayPeriod, ActorUserId = actor.Id, Fingerprint = fingerprint, CreatedAtUtc = DateTime.UtcNow, Resend = request.Resend };
        foreach (var employee in selected)
        {
            var email = emails.GetValueOrDefault(employee.EmployeeId) ?? "";
            var review = RegisterEmployeeStatus(employee, email, queues);
            var item = new PayslipDeliveryItem { EmployeeId = employee.EmployeeId, EmployeeCode = employee.EmployeeCode, Email = email };
            record.Items.Add(item);
            if (!envelopes.TryGetValue(employee.EmployeeId, out var envelope)) { item.Reason = "MissingEmail"; item.Message = review.Message; continue; }
            if (!(request.Resend ? review.CanResend : review.CanSend))
            { item.Status = "Already queued"; item.Reason = "AlreadyHandled"; item.Message = review.Message; continue; }
            var resourceId = run.Id + ":" + employee.Id + ":" + record.Id;
            var queueId = await db.ExecuteScalarAsync<long>("""
INSERT INTO notification_queue(RuleId,EventCode,ResourceType,ResourceId,ClientId,ToJson,CcJson,BccJson,Subject,BodyHtml,Status)
VALUES(@RuleId,@Event,@Resource,@ResourceId,@ClientId,@To,@Cc,@Bcc,@Subject,@Body,@Status); SELECT LAST_INSERT_ID();
""", new { RuleId = rule.Id, Event = RegisterPdfEvent, Resource = RegisterPdfResource, ResourceId = resourceId,
                run.ClientId, envelope.To, envelope.Cc, envelope.Bcc, envelope.Subject, envelope.Body, Status = RegisterPdfPending }, tx);
            item.QueueId = queueId; item.Status = "Queued"; item.Message = "Individual PDF queued through the existing notification service.";
            record.Documents[employee.EmployeeId] = new() { EmployeeId = employee.EmployeeId, PayRunEmployeeId = employee.Id,
                PayrollHash = RegisterPayrollHash(employee), Email = envelope.Email, QueueId = queueId,
                Rows = slips.Where(s => s.EmployeeId == employee.EmployeeId).Select(s => s.PrintSnapshot).ToList() };
        }
        var json = JsonSerializer.Serialize(record, RegisterMailJson);
        if (Encoding.UTF8.GetByteCount(json) > 16 * 1024 * 1024) return (null, "The email selection is too large. Select fewer employees.");
        // Delivery metadata only: never another Excel batch, employee, salary record or payroll run.
        await db.ExecuteAsync("INSERT INTO modulesettings(client_id,ModuleCode,IsEnabled,SettingsJson) VALUES(@ClientId,@Key,TRUE,CAST(@Json AS JSON))",
            new { run.ClientId, Key = key, Json = json }, tx);
        var summary = RegisterJobSummary(record, await ReadRegisterQueuesAsync(db, run, tx));
        await tx.CommitAsync();
        return (new() { Job = summary, Items = record.Items }, null);
    }

    internal async Task AttachRegisterPayslipPdfAsync(MySqlConnection db, NotificationQueueItem queue, BodyBuilder builder)
    {
        var parts = queue.ResourceId.Split(':');
        if (queue.ResourceType != RegisterPdfResource || parts.Length != 3 || !int.TryParse(parts[0], out var runId)
            || !int.TryParse(parts[1], out var rowId) || !Guid.TryParseExact(parts[2], "N", out _))
            throw new InvalidOperationException("The payroll PDF delivery reference is invalid.");
        var json = await db.ExecuteScalarAsync<string?>("SELECT CAST(SettingsJson AS CHAR) FROM modulesettings WHERE client_id=@ClientId AND ModuleCode=@Key",
            new { queue.ClientId, Key = RegisterMailKey(runId, parts[2]) });
        var record = json is null ? null : JsonSerializer.Deserialize<RegisterMailRecord>(json, RegisterMailJson);
        var document = record?.Documents.Values.SingleOrDefault(d => d.PayRunEmployeeId == rowId && d.QueueId == queue.Id);
        if (record is null || record.Id != parts[2] || record.ClientId != queue.ClientId || record.PayRunId != runId || document is null
            || !ReadEmailArray(queue.ToJson).SequenceEqual(new[] { document.Email }, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("The payroll PDF recipient or saved delivery proof is unavailable.");
        var saved = await db.QuerySingleOrDefaultAsync<PayRunEmployee>("""
SELECT p.* FROM payrunemployees p JOIN payruns r ON r.Id=p.PayRunId AND r.ClientId=p.ClientId
WHERE p.Id=@RowId AND p.PayRunId=@RunId AND p.ClientId=@ClientId AND p.IsSkipped=FALSE
AND r.Status IN ('Approved','Partially Paid','Paid')
""", new { RowId = rowId, RunId = runId, queue.ClientId });
        if (saved is null || RegisterPayrollHash(saved) != document.PayrollHash)
            throw new InvalidOperationException("This payroll changed or is no longer approved. Review the queued payslip before retrying.");
        var batch = new ExcelPayslipBatch { ClientId = record.ClientId, ClientName = record.ClientName, Month = record.Month, Rows = document.Rows };
        var pdf = new ExcelPayslipPdfService().Create(batch, batch.Rows, includeSeal: false, amountDecimalPlaces: 2);
        builder.Attachments.Add($"payslip-{saved.EmployeeCode}-{record.Month}.pdf", pdf, new ContentType("application", "pdf"));
    }
}
