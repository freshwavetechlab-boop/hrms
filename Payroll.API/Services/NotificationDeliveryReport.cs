using MailKit;
using MailKit.Net.Imap;
using MimeKit;

namespace Payroll.API.Services;

public sealed record NotificationDeliveryReport(string Recipient, string Action, string StatusCode, string Diagnostic,
    string OriginalMessageId, string OriginalSubject, string OriginalSender, DateTime? OriginalDate, DateTime? ArrivalDate)
{
    public string DeliveryStatus => Action.ToLowerInvariant() switch { "failed" => "Bounced", "delayed" => "Deferred", "delivered" => "Delivered", _ => "" };
    public string Reason => $"{StatusCode}: {Diagnostic}".Trim(':', ' ');

    public static IEnumerable<BodyPart> Parts(BodyPart? part)
    {
        if (part is null) yield break;
        yield return part;
        if (part is BodyPartMultipart multipart)
            foreach (var child in multipart.BodyParts) foreach (var nested in Parts(child)) yield return nested;
    }

    public static async Task<IReadOnlyList<NotificationDeliveryReport>> ReadAsync(IMailFolder folder, IMessageSummary summary, CancellationToken token)
    {
        var parts = Parts(summary.Body).ToArray();
        var delivery = parts.OfType<BodyPartBasic>().FirstOrDefault(part => part.ContentType.IsMimeType("message", "delivery-status"));
        if (delivery is null || delivery.Octets > 1024 * 1024) return [];
        using var entity = await folder.GetBodyPartAsync(summary.UniqueId, delivery, token);
        if (entity is not MessageDeliveryStatus status) return [];
        var envelope = parts.OfType<BodyPartMessage>().FirstOrDefault()?.Envelope;
        MimeMessage? original = null;
        var headerPart = parts.OfType<BodyPartText>().FirstOrDefault(part => part.ContentType.IsMimeType("text", "rfc822-headers"));
        if (headerPart is not null && headerPart.Octets <= 128 * 1024)
        {
            using var headers = await folder.GetBodyPartAsync(summary.UniqueId, headerPart, token);
            original = await DecodeOriginalHeadersAsync(headers, token);
        }
        using (original)
            return Parse(status, original?.MessageId ?? envelope?.MessageId ?? "", original?.Subject ?? envelope?.Subject ?? "",
                original?.From.Mailboxes.FirstOrDefault()?.Address ?? envelope?.From.Mailboxes.FirstOrDefault()?.Address ?? "",
                original?.Date.UtcDateTime ?? envelope?.Date?.UtcDateTime);
    }

    public static IReadOnlyList<NotificationDeliveryReport> Parse(MessageDeliveryStatus delivery, string messageId, string subject, string sender, DateTime? date)
    {
        var groups = delivery.StatusGroups;
        DateTime? arrival = groups.Count > 0 && MimeKit.Utils.DateUtils.TryParse(groups[0]["Arrival-Date"] ?? "", out var parsed) ? parsed.UtcDateTime : null;
        return groups.Skip(1).Select(group => new NotificationDeliveryReport(
            Address(group["Original-Recipient"] ?? group["Final-Recipient"] ?? ""), group["Action"] ?? "", group["Status"] ?? "",
            group["Diagnostic-Code"] ?? "", messageId, subject, sender, date, arrival))
            .Where(report => report.Recipient.Length > 0 && report.DeliveryStatus.Length > 0).ToArray();
    }

    private static string Address(string value) => value[(value.IndexOf(';') + 1)..].Trim();

    public static async Task<MimeMessage?> DecodeOriginalHeadersAsync(MimeEntity headers, CancellationToken token = default)
    {
        if (headers is MessagePart messagePart) return messagePart.Message;
        if (headers is not MimePart mimePart) return null;
        using var stream = new MemoryStream();
        await mimePart.Content.DecodeToAsync(stream, token);
        stream.Position = 0;
        return await MimeMessage.LoadAsync(stream, token);
    }

    public static NotificationDeliveryCandidate? Match(NotificationDeliveryReport report, IEnumerable<NotificationDeliveryCandidate> candidates)
    {
        var recipient = candidates.Where(item => item.Recipient.Equals(report.Recipient, StringComparison.OrdinalIgnoreCase)).ToArray();
        var exact = recipient.Where(item => !string.IsNullOrWhiteSpace(report.OriginalMessageId) && item.MessageId == report.OriginalMessageId).ToArray();
        if (exact.Length > 0) return exact.Select(item => item.QueueId).Distinct().Count() == 1 ? exact.OrderByDescending(item => item.Id).First() : null;
        if (report.OriginalDate is null || string.IsNullOrWhiteSpace(report.OriginalSubject) || string.IsNullOrWhiteSpace(report.OriginalSender)) return null;
        var legacy = recipient.Where(item => item.MessageId.Length == 0 && item.Subject == report.OriginalSubject
            && item.Sender.Equals(report.OriginalSender, StringComparison.OrdinalIgnoreCase)
            && item.SentAt >= report.OriginalDate.Value.AddSeconds(-1)
            && Math.Abs((item.SentAt - report.OriginalDate.Value).TotalMinutes) <= 2).ToArray();
        return legacy.Length == 1 ? legacy[0] : null;
    }
}

public sealed class NotificationDeliveryCandidate
{
    public long Id { get; set; }
    public long QueueId { get; set; }
    public string Recipient { get; set; } = "";
    public string MessageId { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Sender { get; set; } = "";
    public DateTime SentAt { get; set; }
    public string EventCode { get; set; } = "";
    public string ResourceType { get; set; } = "";
    public string ResourceId { get; set; } = "";
    public int? ClientId { get; set; }
}
