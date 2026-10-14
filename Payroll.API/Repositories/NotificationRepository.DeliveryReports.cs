using Dapper;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public partial class NotificationRepository
{
    private static Task<IEnumerable<NotificationDeliveryCandidate>> ReadDeliveryCandidatesAsync(MySqlConnection db, NotificationSmtpSetting smtp,
        DateTime since, MySqlTransaction? tx = null) => db.QueryAsync<NotificationDeliveryCandidate>(@"SELECT log.Id,log.QueueId,log.Recipient,
log.ProviderMessageId MessageId,queue.Subject,@Sender Sender,log.CreatedAt SentAt,queue.EventCode,queue.ResourceType,queue.ResourceId,queue.ClientId
FROM notification_logs log JOIN notification_queue queue ON queue.Id=log.QueueId
WHERE log.Status='Sent' AND log.CreatedAt>=@since AND queue.EventCode='EXCEL_PAYSLIP.SEND' AND queue.ResourceType='ExcelPayslipBatch'",
            new { since, Sender = smtp.FromEmail }, tx);

    private static async Task<int> RecordDeliveryReportsAsync(MySqlConnection db, MySqlTransaction tx, string mailbox, string key,
        IReadOnlyList<NotificationDeliveryReport> reports, IReadOnlyList<NotificationDeliveryCandidate> candidates, DateTime receivedAt)
    {
        var matched = 0;
        foreach (var report in reports)
        {
            var original = NotificationDeliveryReport.Match(report, candidates);
            if (original is null) continue;
            if (original.MessageId.Length == 0 && report.OriginalMessageId.Length is > 0 and <= 220)
            {
                await db.ExecuteAsync("UPDATE notification_logs SET ProviderMessageId=@MessageId WHERE Id=@Id AND ProviderMessageId=''",
                    new { original.Id, MessageId = report.OriginalMessageId }, tx);
                original.MessageId = report.OriginalMessageId;
            }
            await db.ExecuteAsync(@"INSERT INTO notification_logs
(QueueId,EventCode,ResourceType,ResourceId,Recipient,Status,ProviderMessageId,ErrorMessage,CreatedAt)
SELECT @QueueId,@EventCode,@ResourceType,@ResourceId,@Recipient,@Status,@MessageId,@Reason,@receivedAt
WHERE NOT EXISTS (SELECT 1 FROM notification_logs WHERE QueueId=@QueueId AND Recipient=@Recipient
 AND Status=@Status AND ProviderMessageId=@MessageId AND ErrorMessage=@Reason)",
                new { original.QueueId, original.EventCode, original.ResourceType, original.ResourceId, original.Recipient,
                    Status = report.DeliveryStatus, MessageId = Truncate(report.OriginalMessageId, 220), Reason = Truncate(report.Reason, 4000), receivedAt }, tx);
            await db.ExecuteAsync(@"UPDATE notification_incoming_mail SET QueueId=@QueueId,ClientId=@ClientId WHERE MailboxKey=@mailbox AND MessageKey=@key",
                new { original.QueueId, original.ClientId, mailbox, key }, tx);
            matched++;
        }
        if (reports.Count > 0)
            await db.ExecuteAsync(@"UPDATE notification_incoming_mail SET Status=@Status,BodyText=@Details WHERE MailboxKey=@mailbox AND MessageKey=@key",
                new { mailbox, key, Status = matched == reports.Count ? reports.All(report => report.DeliveryStatus == "Bounced") ? "Delivery failed" : "Delivery report" : "Delivery needs review",
                    Details = string.Join("\n", reports.Select(report => $"{report.Recipient}: {report.DeliveryStatus} — {report.Reason}")) }, tx);
        return matched;
    }

    public async Task<object> ReconcileDeliveryReportsAsync(DateTime since, CancellationToken token, bool apply = true)
    {
        if (since > DateTime.UtcNow || since < DateTime.UtcNow.AddYears(-2)) throw new InvalidOperationException("Choose a valid delivery-report start date within the last two years.");
        await using var db = Db(); await db.OpenAsync(token);
        var lockName = "notification-imap-" + NotificationImapIdentity.Hash(db.Database)[..32];
        if (await db.ExecuteScalarAsync<int>("SELECT GET_LOCK(@lockName,0)", new { lockName }) != 1) throw new InvalidOperationException("Incoming mail sync is already running. Retry shortly.");
        try
        {
            var smtp = await db.QuerySingleAsync<NotificationSmtpSetting>("SELECT * FROM notification_smtp_settings WHERE Id=1");
            using var client = new ImapClient(); var folder = await OpenInboxAsync(client, smtp, token);
            var mailbox = NotificationImapIdentity.Mailbox(smtp.ImapHost, smtp.ImapPort, smtp.UserName, smtp.ImapFolder);
            var candidates = (await ReadDeliveryCandidatesAsync(db, smtp, since.AddDays(-30))).ToArray();
            var uids = await folder.SearchAsync(SearchQuery.DeliveredAfter(since.Date), token);
            var examined = 0; var updated = 0; var unmatched = 0; var skipped = 0;
            foreach (var batch in uids.Chunk(100))
            {
                var summaries = await folder.FetchAsync(batch, MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.BodyStructure, token);
                foreach (var summary in summaries)
                {
                    if (!NotificationDeliveryReport.Parts(summary.Body).Any(part => part.ContentType.IsMimeType("message", "delivery-status"))) continue;
                    var messageId = summary.Envelope.MessageId ?? "";
                    var key = NotificationImapIdentity.MessageKey(messageId, folder.UidValidity, summary.UniqueId.Id);
                    var previous = await db.QueryFirstOrDefaultAsync<NotificationIncomingMail>("SELECT * FROM notification_incoming_mail WHERE MailboxKey=@mailbox AND MessageKey=@key", new { mailbox, key });
                    if (previous?.Status is "Delivery failed" or "Delivery report") { skipped++; continue; }
                    var reports = await NotificationDeliveryReport.ReadAsync(folder, summary, token);
                    if (reports.Count == 0) continue;
                    if (!apply)
                    {
                        var matchCount = 0;
                        foreach (var report in reports)
                        {
                            var original = NotificationDeliveryReport.Match(report, candidates);
                            if (original is null) continue;
                            matchCount++;
                            if (original.MessageId.Length == 0) original.MessageId = report.OriginalMessageId;
                        }
                        examined += reports.Count; updated += matchCount; unmatched += reports.Count - matchCount;
                        continue;
                    }
                    await using var tx = await db.BeginTransactionAsync(token);
                    await db.ExecuteAsync(@"INSERT INTO notification_incoming_mail (MailboxKey,MessageKey,MessageId,Sender,Subject,BodyText,ReceivedAt)
VALUES (@mailbox,@key,@messageId,@Sender,@Subject,'',@receivedAt) ON DUPLICATE KEY UPDATE Id=Id",
                        new { mailbox, key, messageId, Sender = Truncate(summary.Envelope.From.Mailboxes.FirstOrDefault()?.Address ?? "", 320),
                            Subject = Truncate(summary.Envelope.Subject ?? "", 500), receivedAt = (summary.Envelope.Date ?? DateTimeOffset.UtcNow).UtcDateTime }, tx);
                    var matches = await RecordDeliveryReportsAsync(db, tx, mailbox, key, reports, candidates, (summary.Envelope.Date ?? DateTimeOffset.UtcNow).UtcDateTime);
                    await tx.CommitAsync(token); examined += reports.Count; updated += matches; unmatched += reports.Count - matches;
                }
            }
            await client.DisconnectAsync(true, token);
            return new { examined, updated, unmatched, skipped };
        }
        finally { await db.ExecuteAsync("SELECT RELEASE_LOCK(@lockName)", new { lockName }); }
    }
}
