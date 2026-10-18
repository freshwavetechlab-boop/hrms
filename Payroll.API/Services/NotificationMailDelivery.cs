using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
using MimeKit.Utils;
using Payroll.API.Models;

namespace Payroll.API.Services;

internal sealed record NotificationMailDeliveryResult(string MessageId, string SentCopyWarning = "", Exception? SentCopyError = null);

internal interface INotificationSentMailbox : IAsyncDisposable
{
    Task<bool> ContainsAsync(string messageId, CancellationToken token);
    Task AppendAsync(MimeMessage message, CancellationToken token);
}

internal static class NotificationMailDelivery
{
    internal const string SentCopyFailureWarning = "Email was sent, but its mailbox Sent copy could not be saved. Do not resend the email.";

    internal static async Task<NotificationMailDeliveryResult> SendAsync(
        NotificationSmtpSetting setting,
        MimeMessage message,
        Func<CancellationToken, Task> submit,
        CancellationToken deliveryToken,
        Func<CancellationToken, Task<INotificationSentMailbox>>? openSentMailbox = null,
        TimeSpan? sentCopyTimeout = null)
    {
        if (string.IsNullOrWhiteSpace(message.MessageId)) message.MessageId = MimeUtils.GenerateMessageId();
        message.Date = DateTimeOffset.Now;
        await submit(deliveryToken);

        // SMTP acceptance is final for delivery. Archival errors must never trigger a delivery retry.
        var messageId = message.MessageId!;
        if (!setting.ImapEnabled) return new(messageId);
        try
        {
            if (string.IsNullOrWhiteSpace(setting.ImapHost) || setting.ImapPort is < 1 or > 65535
                || string.IsNullOrWhiteSpace(setting.UserName) || string.IsNullOrWhiteSpace(setting.Password))
                throw new InvalidOperationException("Sent-copy IMAP settings are incomplete.");

            // Finish saving an accepted email even if the original HTTP request is cancelled.
            using var timeout = new CancellationTokenSource(sentCopyTimeout ?? TimeSpan.FromSeconds(30));
            await using var mailbox = await (openSentMailbox is null
                ? OpenSentMailboxAsync(setting, timeout.Token)
                : openSentMailbox(timeout.Token));
            if (!await mailbox.ContainsAsync(messageId, timeout.Token))
            {
                await mailbox.AppendAsync(message, timeout.Token);
                if (!await mailbox.ContainsAsync(messageId, timeout.Token))
                    throw new IOException("The appended Sent copy could not be verified by Message-ID.");
            }
            return new(messageId);
        }
        catch (Exception exception)
        {
            return new(messageId, SentCopyFailureWarning, exception);
        }
    }

    private static async Task<INotificationSentMailbox> OpenSentMailboxAsync(NotificationSmtpSetting setting, CancellationToken token)
    {
        var client = new ImapClient { Timeout = 30000 };
        try
        {
            await client.ConnectAsync(setting.ImapHost.Trim(), setting.ImapPort,
                setting.ImapPort == 143 ? SecureSocketOptions.StartTls : SecureSocketOptions.SslOnConnect, token);
            await client.AuthenticateAsync(setting.UserName, setting.Password, token);
            IMailFolder? sent = null;
            try { sent = client.GetFolder(SpecialFolder.Sent); }
            catch (FolderNotFoundException) { }
            catch (NotSupportedException) { }
            if (sent is null)
            {
                var folders = new List<IMailFolder>();
                foreach (var ns in client.PersonalNamespaces)
                    folders.AddRange(await client.GetFoldersAsync(ns, false, token));
                sent = folders.FirstOrDefault(folder => (folder.Attributes & FolderAttributes.Sent) != 0);
                if (sent is null)
                {
                    foreach (var name in new[] { "Sent", "INBOX.Sent", "Sent Items", "Sent Messages" })
                    {
                        sent = folders.FirstOrDefault(folder => folder.FullName.Equals(name, StringComparison.OrdinalIgnoreCase));
                        if (sent is not null) break;
                    }
                }
            }
            if (sent is null) throw new FolderNotFoundException("An existing mailbox Sent folder was not found.");
            await sent.OpenAsync(FolderAccess.ReadWrite, token);
            return new SentMailbox(client, sent, token);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private sealed class SentMailbox(ImapClient client, IMailFolder folder, CancellationToken archiveToken) : INotificationSentMailbox
    {
        public async Task<bool> ContainsAsync(string messageId, CancellationToken token) =>
            (await folder.SearchAsync(SearchQuery.HeaderContains("Message-Id", messageId), token)).Count > 0;

        public async Task AppendAsync(MimeMessage message, CancellationToken token) =>
            await folder.AppendAsync(message, MessageFlags.Seen, token);

        public async ValueTask DisposeAsync()
        {
            try { if (client.IsConnected) await client.DisconnectAsync(true, archiveToken); }
            catch { /* A logout failure does not invalidate a saved copy. */ }
            finally { client.Dispose(); }
        }
    }
}
