using Dapper;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public partial class NotificationRepository
{
    public async Task InitializeImapAsync()
    {
        await using var db = Db();
        await db.OpenAsync();
        foreach (var (name, definition) in new[] {
            ("ImapEnabled", "BOOLEAN NOT NULL DEFAULT FALSE"),
            ("ImapHost", "VARCHAR(180) NOT NULL DEFAULT 'imap.hostinger.com'"),
            ("ImapPort", "INT NOT NULL DEFAULT 993"),
            ("ImapFolder", "VARCHAR(180) NOT NULL DEFAULT 'INBOX'"),
            ("ImapMailboxKey", "VARCHAR(64) NOT NULL DEFAULT ''"),
            ("ImapUidValidity", "BIGINT UNSIGNED NOT NULL DEFAULT 0"),
            ("ImapLastUid", "BIGINT UNSIGNED NOT NULL DEFAULT 0"),
            ("ImapLastSyncAt", "DATETIME NULL"),
            ("ImapLastError", "VARCHAR(500) NOT NULL DEFAULT ''") })
            await EnsureColumnAsync(db, "notification_smtp_settings", name, definition);
        await db.ExecuteAsync(@"CREATE TABLE IF NOT EXISTS notification_incoming_mail (
Id BIGINT PRIMARY KEY AUTO_INCREMENT,
MailboxKey CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
MessageKey CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
MessageId TEXT NOT NULL, Sender VARCHAR(320) NOT NULL, Subject VARCHAR(500) NOT NULL,
BodyText MEDIUMTEXT NOT NULL, ReceivedAt DATETIME NOT NULL, ProcessedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
QueueId BIGINT NULL, ClientId INT NULL, Status VARCHAR(40) NOT NULL DEFAULT 'Received',
UNIQUE KEY UX_IncomingMail_Message (MailboxKey,MessageKey),
INDEX IX_IncomingMail_Received (ReceivedAt,Id), INDEX IX_IncomingMail_Queue (QueueId)
);");
        if (await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM information_schema.statistics
WHERE table_schema=DATABASE() AND table_name='notification_logs' AND index_name='IX_NotificationLog_Message'") == 0)
            await db.ExecuteAsync("CREATE INDEX IX_NotificationLog_Message ON notification_logs (ProviderMessageId,Recipient)");
    }

    public async Task<List<NotificationIncomingMail>> ListIncomingMailAsync()
    {
        await using var db = Db();
        return (await db.QueryAsync<NotificationIncomingMail>(@"SELECT Id,MessageId,Sender,Subject,ReceivedAt,ProcessedAt,QueueId,ClientId,Status
FROM notification_incoming_mail ORDER BY ReceivedAt DESC,Id DESC LIMIT 200")).ToList();
    }

    public async Task<NotificationIncomingMail?> GetIncomingMailAsync(long id)
    {
        await using var db = Db();
        return await db.QuerySingleOrDefaultAsync<NotificationIncomingMail>("SELECT * FROM notification_incoming_mail WHERE Id=@id", new { id });
    }

    private static void ValidateImap(NotificationSmtpSetting setting)
    {
        if (string.IsNullOrWhiteSpace(setting.ImapHost) || setting.ImapPort is < 1 or > 65535 || string.IsNullOrWhiteSpace(setting.ImapFolder))
            throw new InvalidOperationException("IMAP host, valid port and folder are required.");
        if (string.IsNullOrWhiteSpace(setting.UserName) || string.IsNullOrWhiteSpace(setting.Password))
            throw new InvalidOperationException("Save the SMTP username and password before configuring incoming mail.");
    }

    private static async Task<IMailFolder> OpenInboxAsync(ImapClient client, NotificationSmtpSetting setting, CancellationToken token)
    {
        ValidateImap(setting);
        client.Timeout = 30000;
        await client.ConnectAsync(setting.ImapHost.Trim(), setting.ImapPort, SecureSocketOptions.SslOnConnect, token);
        await client.AuthenticateAsync(setting.UserName, setting.Password, token);
        var folder = await client.GetFolderAsync(setting.ImapFolder.Trim(), token);
        await folder.OpenAsync(FolderAccess.ReadOnly, token);
        return folder;
    }

    public async Task<int> TestImapAsync(CancellationToken token)
    {
        await using var db = Db();
        var setting = await db.QuerySingleAsync<NotificationSmtpSetting>("SELECT * FROM notification_smtp_settings WHERE Id=1");
        using var client = new ImapClient();
        var folder = await OpenInboxAsync(client, setting, token);
        var count = folder.Count;
        await client.DisconnectAsync(true, token);
        return count;
    }

    public async Task<int> SyncIncomingMailAsync(CancellationToken token, bool force = false)
    {
        await using var db = Db();
        await db.OpenAsync(token);
        // A connection-scoped database lock protects the cursor across API instances.
        var lockName = "notification-imap-" + NotificationImapIdentity.Hash(db.Database)[..32];
        if (await db.ExecuteScalarAsync<int>("SELECT GET_LOCK(@lockName,0)", new { lockName }) != 1) return 0;
        try
        {
            var setting = await db.QuerySingleAsync<NotificationSmtpSetting>("SELECT * FROM notification_smtp_settings WHERE Id=1");
            if (!setting.ImapEnabled || (!force && setting.ImapLastSyncAt > DateTime.UtcNow.AddSeconds(-60))) return 0;
            await db.ExecuteAsync("UPDATE notification_smtp_settings SET ImapLastSyncAt=UTC_TIMESTAMP() WHERE Id=1");
            using var client = new ImapClient();
            var folder = await OpenInboxAsync(client, setting, token);
            var mailbox = NotificationImapIdentity.Mailbox(setting.ImapHost, setting.ImapPort, setting.UserName, setting.ImapFolder);
            var lastUid = NotificationImapIdentity.ResumeUid(setting.ImapMailboxKey, mailbox, setting.ImapUidValidity, folder.UidValidity, setting.ImapLastUid);
            if (lastUid == uint.MaxValue) return 0;
            var uids = (await folder.SearchAsync(SearchQuery.Uids(new UniqueIdRange(new UniqueId(lastUid + 1), UniqueId.MaxValue)), token))
                .OrderBy(uid => uid.Id).Take(50).ToList();
            var count = 0;
            if (uids.Count > 0)
            {
                var summaries = await folder.FetchAsync(uids, new FetchRequest(MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.BodyStructure,
                    new[] { HeaderId.InReplyTo, HeaderId.References }), token);
                var requested = uids.Select(uid => uid.Id).ToHashSet();
                foreach (var summary in summaries.Where(item => requested.Contains(item.UniqueId.Id)).OrderBy(item => item.UniqueId.Id))
                {
                    token.ThrowIfCancellationRequested();
                    var messageId = summary.Envelope.MessageId ?? "";
                    var key = NotificationImapIdentity.MessageKey(messageId, folder.UidValidity, summary.UniqueId.Id);
                    var exists = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM notification_incoming_mail WHERE MailboxKey=@mailbox AND MessageKey=@key", new { mailbox, key });
                    var body = "";
                    IReadOnlyList<NotificationDeliveryReport> deliveryReports = [];
                    IReadOnlyList<NotificationDeliveryCandidate> deliveryCandidates = [];
                    if (exists == 0)
                    {
                        var part = summary.TextBody ?? summary.HtmlBody;
                        // Attachments are not downloaded; oversized bodies retain the envelope.
                        if (part is not null && part.Octets <= 1024 * 1024)
                        {
                            using var entity = await folder.GetBodyPartAsync(summary.UniqueId, part, token);
                            if (entity is TextPart text) body = text.IsHtml ? StripHtml(text.Text) : text.Text;
                        }
                        deliveryReports = await NotificationDeliveryReport.ReadAsync(folder, summary, token);
                        if (deliveryReports.Count > 0)
                        {
                            var since = deliveryReports.Select(report => report.OriginalDate ?? report.ArrivalDate ?? DateTime.UtcNow)
                                .Where(date => date > DateTime.UtcNow.AddYears(-2)).DefaultIfEmpty(DateTime.UtcNow).Min().AddDays(-1);
                            deliveryCandidates = (await ReadDeliveryCandidatesAsync(db, setting, since)).ToArray();
                        }
                    }
                    await using var tx = await db.BeginTransactionAsync(token);
                    if (exists == 0)
                    {
                        var sender = summary.Envelope.From.Mailboxes.FirstOrDefault()?.Address ?? "";
                        var replyIds = NotificationImapIdentity.ReplyIds(summary.Headers?[HeaderId.InReplyTo], summary.Headers?[HeaderId.References]);
                        var original = replyIds.Length == 0 ? null : await db.QueryFirstOrDefaultAsync<NotificationLog>(@"SELECT * FROM notification_logs
WHERE ProviderMessageId IN @replyIds AND Recipient=@sender AND Status='Sent' ORDER BY Id DESC LIMIT 1", new { replyIds, sender }, tx);
                        var clientId = original is null ? (int?)null : await db.ExecuteScalarAsync<int?>("SELECT ClientId FROM notification_queue WHERE Id=@Id", new { Id = original.QueueId }, tx);
                        await db.ExecuteAsync(@"INSERT INTO notification_incoming_mail
(MailboxKey,MessageKey,MessageId,Sender,Subject,BodyText,ReceivedAt,QueueId,ClientId,Status)
VALUES (@mailbox,@key,@messageId,@sender,@subject,@body,@receivedAt,@queueId,@clientId,@status)",
                            new { mailbox, key, messageId, sender = Truncate(sender, 320), subject = Truncate(summary.Envelope.Subject ?? "", 500), body = Truncate(body, 100000),
                                receivedAt = (summary.Envelope.Date ?? DateTimeOffset.UtcNow).UtcDateTime, queueId = original?.QueueId, clientId,
                                status = original is null ? "Received" : "Reply received" }, tx);
                        if (original is not null)
                        {
                            await db.ExecuteAsync(@"INSERT INTO notification_logs (QueueId,EventCode,ResourceType,ResourceId,Recipient,Status,ProviderMessageId,ErrorMessage)
VALUES (@QueueId,@EventCode,@ResourceType,@ResourceId,@Recipient,'Reply received',@messageId,'')", new { original.QueueId, original.EventCode, original.ResourceType, original.ResourceId, original.Recipient, messageId = Truncate(messageId, 220) }, tx);
                        }
                        await RecordDeliveryReportsAsync(db, tx, mailbox, key, deliveryReports, deliveryCandidates,
                            (summary.Envelope.Date ?? DateTimeOffset.UtcNow).UtcDateTime);
                        count++;
                    }
                    await db.ExecuteAsync(@"UPDATE notification_smtp_settings SET ImapMailboxKey=@mailbox,ImapUidValidity=@validity,ImapLastUid=@uid WHERE Id=1",
                        new { mailbox, validity = folder.UidValidity, uid = summary.UniqueId.Id }, tx);
                    await tx.CommitAsync(token);
                }
            }
            await client.DisconnectAsync(true, token);
            await db.ExecuteAsync("UPDATE notification_smtp_settings SET ImapLastError='' WHERE Id=1");
            return count;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await db.ExecuteAsync("UPDATE notification_smtp_settings SET ImapLastError=@error WHERE Id=1", new { error = Truncate(exception.Message, 500) });
            throw;
        }
        finally { await db.ExecuteAsync("SELECT RELEASE_LOCK(@lockName)", new { lockName }); }
    }

    private static string Truncate(string text, int limit) => text.Length <= limit ? text : text[..limit];
}
