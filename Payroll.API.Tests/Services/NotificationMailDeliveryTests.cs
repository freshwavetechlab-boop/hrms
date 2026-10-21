using MimeKit;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class NotificationMailDeliveryTests
{
    private static NotificationSmtpSetting Settings(bool imap = true) => new()
    {
        ImapEnabled = imap, ImapHost = "imap.example.test", ImapPort = 993,
        UserName = "hr@example.test", Password = "test-only"
    };

    private static MimeMessage Message()
    {
        var message = new MimeMessage { Subject = "Resume request – आवेदन", MessageId = "original@example.test" };
        message.From.Add(new MailboxAddress("Frevo", "hr@example.test"));
        message.To.Add(new MailboxAddress("Candidate", "candidate@example.test"));
        message.Cc.Add(MailboxAddress.Parse("recruiter@example.test"));
        message.Bcc.Add(MailboxAddress.Parse("audit@example.test"));
        var body = new BodyBuilder { TextBody = "Please reply with your resume. धन्यवाद", HtmlBody = "<p>Please reply with your resume. धन्यवाद</p><img src='cid:logo@example.test'>" };
        body.Attachments.Add("resume.pdf", new byte[] { 1, 2, 3, 4 }, new ContentType("application", "pdf"));
        var logo = body.LinkedResources.Add("logo.png", new byte[] { 9, 8, 7 }, new ContentType("image", "png"));
        logo.ContentId = "logo@example.test";
        message.Body = body.ToMessageBody();
        return message;
    }

    [Fact]
    public async Task AcceptedEmailSavesTheSameIdentityRecipientsContentAndAttachmentBytes()
    {
        using var message = Message();
        var mailbox = new MemoryMailbox();
        var sends = 0;
        var result = await NotificationMailDelivery.SendAsync(Settings(), message, async _ =>
        {
            sends++;
            using var transmitted = new MemoryStream(); await message.WriteToAsync(transmitted);
        }, default, _ => Task.FromResult<INotificationSentMailbox>(mailbox));

        Assert.Equal(1, sends);
        Assert.Equal(1, mailbox.Appends);
        Assert.Equal("original@example.test", result.MessageId);
        Assert.Empty(result.SentCopyWarning);
        Assert.Equal("Saved", result.SentCopyStatus);
        using var copy = MimeMessage.Load(new MemoryStream(Assert.Single(mailbox.Messages.Values)));
        Assert.Equal(message.MessageId, copy.MessageId);
        Assert.Equal(message.Subject, copy.Subject);
        Assert.Equal(message.Date.ToUnixTimeSeconds(), copy.Date.ToUnixTimeSeconds());
        Assert.Equal(message.From.ToString(), copy.From.ToString());
        Assert.Equal(message.To.ToString(), copy.To.ToString());
        Assert.Equal(message.Cc.ToString(), copy.Cc.ToString());
        Assert.Equal(message.Bcc.ToString(), copy.Bcc.ToString());
        Assert.Equal(message.TextBody, copy.TextBody);
        Assert.Equal(message.HtmlBody, copy.HtmlBody);
        foreach (var (name, expected) in new[] { ("resume.pdf", new byte[] { 1, 2, 3, 4 }), ("logo.png", new byte[] { 9, 8, 7 }) })
        {
            var part = Assert.Single(copy.BodyParts.OfType<MimePart>(), part => part.FileName == name);
            using var bytes = new MemoryStream(); part.Content!.DecodeTo(bytes);
            Assert.Equal(expected, bytes.ToArray());
        }
    }

    [Fact]
    public async Task ProviderSavedSentCopyIsNotAppendedAgain()
    {
        using var message = Message();
        var mailbox = new MemoryMailbox();
        var result = await NotificationMailDelivery.SendAsync(Settings(), message, async _ =>
        {
            using var bytes = new MemoryStream(); await message.WriteToAsync(bytes);
            mailbox.Messages[message.MessageId!] = bytes.ToArray();
        }, default, _ => Task.FromResult<INotificationSentMailbox>(mailbox));
        Assert.Empty(result.SentCopyWarning);
        Assert.Equal(0, mailbox.Appends);
        Assert.Single(mailbox.Messages);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImapConnectionOrTimeoutFailureReturnsSuccessfulDeliveryWithWarning(bool cancelled)
    {
        using var message = Message(); var sends = 0;
        var result = await NotificationMailDelivery.SendAsync(Settings(), message, _ => { sends++; return Task.CompletedTask; }, default,
            _ => cancelled ? Task.FromCanceled<INotificationSentMailbox>(new CancellationToken(true)) : Task.FromException<INotificationSentMailbox>(new IOException("IMAP unavailable")));
        Assert.Equal(1, sends);
        Assert.Equal(message.MessageId, result.MessageId);
        Assert.Equal(NotificationMailDelivery.SentCopyFailureWarning, result.SentCopyWarning);
        Assert.Equal("Failed", result.SentCopyStatus);
        Assert.NotNull(result.SentCopyError);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AppendOrVerificationFailureCannotTurnAcceptedEmailIntoDeliveryFailure(bool appendThrows)
    {
        using var message = Message(); var sends = 0;
        var mailbox = new MemoryMailbox { AppendThrows = appendThrows, HideAppended = !appendThrows };
        var result = await NotificationMailDelivery.SendAsync(Settings(), message, _ => { sends++; return Task.CompletedTask; }, default, _ => Task.FromResult<INotificationSentMailbox>(mailbox));
        Assert.Equal(1, sends);
        Assert.Equal(1, mailbox.Appends);
        Assert.Equal(message.MessageId, result.MessageId);
        Assert.NotEmpty(result.SentCopyWarning);
    }

    [Fact]
    public async Task SmtpFailureDoesNotSaveAFalselySentCopy()
    {
        using var message = Message(); var opened = false;
        await Assert.ThrowsAsync<IOException>(() => NotificationMailDelivery.SendAsync(Settings(), message,
            _ => Task.FromException(new IOException("SMTP rejected message")), default,
            _ => { opened = true; return Task.FromResult<INotificationSentMailbox>(new MemoryMailbox()); }));
        Assert.False(opened);
    }

    [Fact]
    public async Task DisabledImapKeepsSmtpOnlyDeliveryWithoutOpeningAMailbox()
    {
        using var message = Message(); var sends = 0; var opened = false;
        var result = await NotificationMailDelivery.SendAsync(Settings(false), message, _ => { sends++; return Task.CompletedTask; }, default,
            _ => { opened = true; return Task.FromResult<INotificationSentMailbox>(new MemoryMailbox()); });
        Assert.Equal(1, sends); Assert.False(opened); Assert.Empty(result.SentCopyWarning);
        Assert.Equal("NotConfigured", result.SentCopyStatus);
    }

    [Fact]
    public async Task CancellationAfterSmtpAcceptanceDoesNotCancelArchivalOrCauseAResend()
    {
        using var message = Message(); using var cancellation = new CancellationTokenSource();
        var mailbox = new MemoryMailbox();
        var result = await NotificationMailDelivery.SendAsync(Settings(), message,
            _ => { cancellation.Cancel(); return Task.CompletedTask; }, cancellation.Token,
            token => { Assert.False(token.IsCancellationRequested); return Task.FromResult<INotificationSentMailbox>(mailbox); });
        Assert.Empty(result.SentCopyWarning); Assert.Equal(1, mailbox.Appends);
    }

    [Fact]
    public async Task MissingImapCredentialsWarnWithoutFailingAcceptedDeliveryOrLeakingCredentials()
    {
        using var message = Message(); var settings = Settings(); settings.UserName = ""; var opened = false;
        var result = await NotificationMailDelivery.SendAsync(settings, message, _ => Task.CompletedTask, default,
            _ => { opened = true; return Task.FromResult<INotificationSentMailbox>(new MemoryMailbox()); });
        Assert.False(opened);
        Assert.Equal(NotificationMailDelivery.SentCopyFailureWarning, result.SentCopyWarning);
        Assert.DoesNotContain(settings.Password, result.SentCopyWarning);
    }

    [Fact]
    public async Task GeneratedMessageIdIsSharedBySmtpAndTheSavedCopy()
    {
        using var message = Message(); message.Headers.Remove(HeaderId.MessageId);
        string? submittedId = null; var mailbox = new MemoryMailbox();
        var result = await NotificationMailDelivery.SendAsync(Settings(), message,
            _ => { submittedId = message.MessageId; return Task.CompletedTask; }, default,
            _ => Task.FromResult<INotificationSentMailbox>(mailbox));
        Assert.False(string.IsNullOrWhiteSpace(submittedId));
        Assert.Equal(submittedId, result.MessageId);
        Assert.True(mailbox.Messages.ContainsKey(result.MessageId));
    }

    [Fact]
    public async Task ArchivalTimeoutIsBoundedWithoutCancellingAcceptedDelivery()
    {
        using var message = Message(); var sends = 0;
        var result = await NotificationMailDelivery.SendAsync(Settings(), message,
            _ => { sends++; return Task.CompletedTask; }, default,
            async token => { await Task.Delay(Timeout.Infinite, token); return new MemoryMailbox(); },
            TimeSpan.FromMilliseconds(25));
        Assert.Equal(1, sends);
        Assert.Equal(message.MessageId, result.MessageId);
        Assert.IsAssignableFrom<OperationCanceledException>(result.SentCopyError);
        Assert.NotEmpty(result.SentCopyWarning);
    }

    private sealed class MemoryMailbox : INotificationSentMailbox
    {
        public Dictionary<string, byte[]> Messages { get; } = [];
        public int Appends { get; private set; }
        public bool AppendThrows { get; init; }
        public bool HideAppended { get; init; }
        public Task<bool> ContainsAsync(string messageId, CancellationToken token) => Task.FromResult(!HideAppended && Messages.ContainsKey(messageId));
        public async Task AppendAsync(MimeMessage message, CancellationToken token)
        {
            Appends++;
            if (AppendThrows) throw new IOException("Append unavailable");
            using var bytes = new MemoryStream(); await message.WriteToAsync(bytes, token);
            Messages[message.MessageId!] = bytes.ToArray();
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
