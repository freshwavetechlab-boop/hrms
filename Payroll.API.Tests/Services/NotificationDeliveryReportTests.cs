using System.Text;
using MimeKit;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class NotificationDeliveryReportTests
{
    private static readonly DateTime Sent = new(2026, 10, 13, 7, 48, 0, DateTimeKind.Utc);
    private static NotificationDeliveryReport Report(string action = "failed", string id = "original@example.test") =>
        new("employee@example.test", action, "4.2.2", "smtp; mailbox full", id, "Salary Slip | September 2026", "hr@example.test", Sent, Sent);
    private static NotificationDeliveryCandidate Candidate(long id = 1, string messageId = "") => new()
        { Id = id, QueueId = id, MessageId = messageId, Recipient = "employee@example.test", Subject = "Salary Slip | September 2026", Sender = "hr@example.test", SentAt = Sent.AddSeconds(10) };

    [Theory]
    [InlineData("failed", "Bounced")]
    [InlineData("delayed", "Deferred")]
    [InlineData("delivered", "Delivered")]
    [InlineData("relayed", "")]
    public void DsnActionDeterminesOutcomeEvenWhenAFailedReportContainsATemporaryCode(string action, string expected) =>
        Assert.Equal(expected, Report(action).DeliveryStatus);

    [Fact]
    public void RecipientAloneCannotChangeAnEmployeePayslipStatus()
    {
        Assert.Null(NotificationDeliveryReport.Match(Report() with { OriginalMessageId = "", OriginalSubject = "" }, [Candidate()]));
        Assert.Null(NotificationDeliveryReport.Match(Report() with { OriginalSender = "other@example.test" }, [Candidate()]));
        Assert.Null(NotificationDeliveryReport.Match(Report() with { OriginalDate = Sent.AddHours(1) }, [Candidate()]));
    }

    [Fact]
    public void AUniqueLegacySendRequiresExactSubjectSenderRecipientAndCloseSendTime() =>
        Assert.Equal(1, NotificationDeliveryReport.Match(Report(), [Candidate()])!.QueueId);

    [Fact]
    public void AmbiguousLegacyRecipientsAreHeldForReview() =>
        Assert.Null(NotificationDeliveryReport.Match(Report(), [Candidate(1), Candidate(2)]));

    [Fact]
    public void AMessageCannotMatchAnEarlierCompletedSend()
    {
        var earlier = Candidate(); earlier.SentAt = Sent.AddSeconds(-3);
        Assert.Null(NotificationDeliveryReport.Match(Report(), [earlier]));
    }

    [Fact]
    public void KnownLaterMessageDisambiguatesAnEarlierLegacySend()
    {
        var earlier = Candidate(1); earlier.SentAt = Sent.AddSeconds(1);
        var later = Candidate(2); later.SentAt = Sent.AddSeconds(5);
        var laterReport = Report() with { OriginalDate = Sent.AddSeconds(4), OriginalMessageId = "later@example.test" };
        Assert.Equal(2, NotificationDeliveryReport.Match(laterReport, [earlier, later])!.QueueId);
        later.MessageId = laterReport.OriginalMessageId;
        Assert.Equal(1, NotificationDeliveryReport.Match(Report(), [earlier, later])!.QueueId);
    }

    [Fact]
    public async Task TextRfc822HeadersExposeOriginalMessageMetadata()
    {
        const string raw = "Content-Type: text/rfc822-headers\r\n\r\nFrom: HR <hr@example.test>\r\nTo: employee@example.test\r\nSubject: Salary Slip | September 2026\r\nDate: Tue, 13 Oct 2026 07:48:00 +0000\r\nMessage-Id: <original@example.test>\r\n\r\n";
        using var entity = MimeEntity.Load(new MemoryStream(Encoding.ASCII.GetBytes(raw)));
        using var original = await NotificationDeliveryReport.DecodeOriginalHeadersAsync(entity);
        Assert.Equal("original@example.test", original!.MessageId);
        Assert.Equal("Salary Slip | September 2026", original.Subject);
        Assert.Equal(Sent, original.Date.UtcDateTime);
    }

    [Fact]
    public void RecordedMessageIdDisambiguatesRepeatedSends()
    {
        Assert.Equal(2, NotificationDeliveryReport.Match(Report(), [Candidate(1, "older@example.test"), Candidate(2, "original@example.test")])!.QueueId);
        Assert.Null(NotificationDeliveryReport.Match(Report(), [Candidate(1, "original@example.test"), Candidate(2, "original@example.test")]));
    }

    [Fact]
    public void MimeDeliveryStatusParsesFoldedDiagnosticsAndBothRecipientFields()
    {
        const string raw = "Content-Type: message/delivery-status\r\n\r\nReporting-MTA: dns; relay.example.test\r\nArrival-Date: Tue, 13 Oct 2026 07:48:01 +0000 (UTC)\r\n\r\nFinal-Recipient: rfc822; employee@example.test\r\nAction: failed\r\nStatus: 4.2.2\r\nDiagnostic-Code: smtp; 552 mailbox full\r\n retry after freeing space\r\n";
        using var entity = MimeEntity.Load(new MemoryStream(Encoding.ASCII.GetBytes(raw)));
        var report = Assert.Single(NotificationDeliveryReport.Parse((MessageDeliveryStatus)entity, "original@example.test", "Salary", "hr@example.test", Sent));
        Assert.Equal("employee@example.test", report.Recipient);
        Assert.Equal("Bounced", report.DeliveryStatus);
        Assert.Contains("mailbox full", report.Reason);
        Assert.Equal(Sent.AddSeconds(1), report.ArrivalDate);
    }
}
