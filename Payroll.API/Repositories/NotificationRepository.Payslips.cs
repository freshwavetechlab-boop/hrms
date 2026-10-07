using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using MimeKit;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public partial class NotificationRepository
{
    public async Task<(string Status, string Message)> QueuePayslipAsync(NotificationEvent evt, string payslipHtml)
    {
        if (DeliverySuppressed) return ("Excluded", "Outbound delivery is suppressed for this application instance.");
        await using var db = Db();
        await db.OpenAsync();
        if (await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM notification_smtp_settings
WHERE Id=1 AND IsEnabled=TRUE AND DeliveryPaused=FALSE AND Host<>'' AND FromEmail<>''") == 0)
            return ("Excluded", "Enable email delivery in Settings > Notifications; delivery is disabled, paused or incomplete.");

        // A client rule takes precedence over the global fallback, avoiding duplicate payslips.
        var rule = await db.QueryFirstOrDefaultAsync<NotificationRule>(@"SELECT r.* FROM notification_rules r
JOIN notification_templates t ON t.Id=r.TemplateId AND t.IsActive=TRUE
WHERE r.EventCode='PAYSLIP.SEND' AND r.IsEnabled=TRUE AND (r.ClientId=@ClientId OR r.ClientId IS NULL)
ORDER BY r.ClientId IS NULL,r.Id DESC LIMIT 1", new { evt.ClientId });
        if (rule is null) return ("Excluded", "Configure an enabled PAYSLIP.SEND rule and email template in Settings > Notifications.");
        var template = await db.QuerySingleAsync<NotificationTemplate>("SELECT * FROM notification_templates WHERE Id=@Id", new { Id = rule.TemplateId });
        if (!Regex.IsMatch(template.BodyTemplate, @"\{\{\s*payslipHtml\s*\}\}", RegexOptions.IgnoreCase))
            return ("Excluded", "The payslip email template must contain {{payslipHtml}} so the payslip is included.");
        rule.Recipients = (await db.QueryAsync<NotificationRecipient>("SELECT * FROM notification_recipients WHERE RuleId=@Id AND IsActive=TRUE", new { rule.Id })).ToList();
        rule.Parameters = (await db.QueryAsync<NotificationParameterMapping>("SELECT * FROM notification_parameter_mappings WHERE RuleId=@Id AND IsActive=TRUE", new { rule.Id })).ToList();
        var values = BuildBaseValues(evt);
        foreach (var mapping in rule.Parameters)
            values[mapping.ParameterName] = await ResolveParameterAsync(db, mapping, evt, values);
        var to = await ResolveRecipientsAsync(db, rule.Recipients.Where(item => item.RecipientType == "To"), evt, values);
        var cc = await ResolveRecipientsAsync(db, rule.Recipients.Where(item => item.RecipientType == "Cc"), evt, values);
        var bcc = await ResolveRecipientsAsync(db, rule.Recipients.Where(item => item.RecipientType == "Bcc"), evt, values);
        if (to.Count == 0 || to.Concat(cc).Concat(bcc).Any(email => !MailboxAddress.TryParse(email, out _)))
            return ("Excluded", "The notification rule did not resolve valid email recipients.");
        var subject = Render(template.SubjectTemplate, values);
        if (string.IsNullOrWhiteSpace(subject)) return ("Excluded", "Configure the payslip email subject in Settings > Notifications.");
        var body = RenderPayslipBody(template.BodyTemplate, values, payslipHtml);

        await using var tx = await db.BeginTransactionAsync();
        var rowId = await db.ExecuteScalarAsync<int?>(@"SELECT p.Id FROM payrunemployees p JOIN payruns r ON r.Id=p.PayRunId
WHERE p.Id=@ResourceId AND p.ClientId=@ClientId AND r.ClientId=@ClientId AND p.IsSkipped=FALSE
AND r.Status IN ('Approved','Partially Paid','Paid') FOR UPDATE", new { evt.ResourceId, evt.ClientId }, tx);
        if (rowId is null) return ("Excluded", "The payslip is no longer eligible for delivery.");
        var existing = await db.ExecuteScalarAsync<long?>(@"SELECT Id FROM notification_queue
WHERE EventCode='PAYSLIP.SEND' AND ResourceType='PayRunEmployee' AND ResourceId=@ResourceId
AND ClientId=@ClientId AND Status IN ('Pending','Processing','Sent') ORDER BY Id DESC LIMIT 1", new { evt.ResourceId, evt.ClientId }, tx);
        if (existing.HasValue) return ("Already queued", "This payslip is already queued or sent. Check Settings > Notifications > Delivery Monitor.");
        await db.ExecuteAsync(@"INSERT INTO notification_queue
(RuleId,EventCode,ResourceType,ResourceId,ClientId,ToJson,CcJson,BccJson,Subject,BodyHtml,Status)
VALUES (@RuleId,'PAYSLIP.SEND','PayRunEmployee',@ResourceId,@ClientId,@ToJson,@CcJson,@BccJson,@Subject,@BodyHtml,'Pending')",
            new { RuleId = rule.Id, evt.ResourceId, evt.ClientId, ToJson = JsonSerializer.Serialize(to), CcJson = JsonSerializer.Serialize(cc), BccJson = JsonSerializer.Serialize(bcc), Subject = subject, BodyHtml = body }, tx);
        await tx.CommitAsync();
        return ("Queued", "Queued through the configured notification email service.");
    }

    internal static string RenderPayslipBody(string template, Dictionary<string, string> values, string payslipHtml)
    {
        var marker = Guid.NewGuid().ToString("N");
        var wrapper = Regex.Replace(template, @"\{\{\s*payslipHtml\s*\}\}", marker, RegexOptions.IgnoreCase);
        var content = Regex.Match(payslipHtml, @"<body[^>]*>(.*)</body>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var style = Regex.Match(payslipHtml, @"<style[^>]*>.*?</style>", RegexOptions.Singleline | RegexOptions.IgnoreCase).Value;
        return style + Render(wrapper, values).Replace(marker, content.Success ? content.Groups[1].Value : payslipHtml);
    }
}
