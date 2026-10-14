using System.Security.Cryptography;
using System.Text;
using MimeKit.Utils;

namespace Payroll.API.Services;

public static class NotificationImapIdentity
{
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string Mailbox(string host, int port, string user, string folder) =>
        Hash($"{host.Trim().ToLowerInvariant()}\n{port}\n{user.Trim().ToLowerInvariant()}\n{folder.Trim()}");
    public static string MessageKey(string? messageId, uint validity, uint uid) =>
        Hash(string.IsNullOrWhiteSpace(messageId) ? $"uid:{validity}:{uid}" : $"id:{messageId.Trim().Trim('<', '>')}");
    public static uint ResumeUid(string savedMailbox, string mailbox, uint savedValidity, uint validity, uint savedUid) =>
        savedMailbox == mailbox && savedValidity == validity ? savedUid : 0;
    public static string[] ReplyIds(string? inReplyTo, string? references) =>
        MimeUtils.EnumerateReferences(inReplyTo ?? "").Concat(MimeUtils.EnumerateReferences(references ?? "")).Distinct(StringComparer.Ordinal).ToArray();
}
