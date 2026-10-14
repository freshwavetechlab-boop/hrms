using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class NotificationImapIdentityTests
{
    [Fact]
    public void AProcessedMessageRetainsIdentityAfterMailboxUidReset()
    {
        Assert.Equal(NotificationImapIdentity.MessageKey("<reply@example.test>", 10, 4),
            NotificationImapIdentity.MessageKey("reply@example.test", 11, 900));
        Assert.NotEqual(NotificationImapIdentity.MessageKey("first@example.test", 10, 4),
            NotificationImapIdentity.MessageKey("second@example.test", 10, 4));
    }

    [Fact]
    public void MissingMessageIdsUseTheUidValidityAndUidWithoutCollapsingDifferentMessages()
    {
        var key = NotificationImapIdentity.MessageKey(null, 10, 4);
        Assert.Equal(key, NotificationImapIdentity.MessageKey("", 10, 4));
        Assert.NotEqual(key, NotificationImapIdentity.MessageKey(null, 10, 5));
        Assert.NotEqual(key, NotificationImapIdentity.MessageKey(null, 11, 4));
    }

    [Fact]
    public void CursorResumesAfterRestartButResetsForANewMailboxOrValidity()
    {
        Assert.Equal(42u, NotificationImapIdentity.ResumeUid("mailbox", "mailbox", 10, 10, 42));
        Assert.Equal(0u, NotificationImapIdentity.ResumeUid("old", "new", 10, 10, 42));
        Assert.Equal(0u, NotificationImapIdentity.ResumeUid("mailbox", "mailbox", 10, 11, 42));
    }

    [Fact]
    public void ReplyHeadersExtractOriginalRfcMessageIdsAndIgnoreDuplicateReferences()
    {
        Assert.Equal(new[] { "recent@example.test", "first@example.test" },
            NotificationImapIdentity.ReplyIds("<recent@example.test>", "<first@example.test>\r\n <recent@example.test>"));
        Assert.Empty(NotificationImapIdentity.ReplyIds(null, ""));
    }

    [Fact]
    public void MailboxIdentityIsStableForHostAndUsernameCaseButIsolatedByAccountAndFolder()
    {
        var key = NotificationImapIdentity.Mailbox("IMAP.HOSTINGER.COM", 993, "HR@EXAMPLE.TEST", "INBOX");
        Assert.Equal(key, NotificationImapIdentity.Mailbox("imap.hostinger.com", 993, "hr@example.test", "INBOX"));
        Assert.NotEqual(key, NotificationImapIdentity.Mailbox("imap.hostinger.com", 993, "other@example.test", "INBOX"));
        Assert.NotEqual(key, NotificationImapIdentity.Mailbox("imap.hostinger.com", 993, "hr@example.test", "Other"));
    }
}
