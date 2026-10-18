namespace Payroll.API.Models;

public sealed class RecruitmentResumeRecoveryRequest
{
    public long ExpectedResumeId { get; set; }
    public long FieldConfigurationId { get; set; }
    public string ApplicationCode { get; set; } = "";
    public string SenderEmail { get; set; } = "";
    public string SourceMessageId { get; set; } = "";
    public DateTimeOffset ReceivedAt { get; set; }
    public string Sha256 { get; set; } = "";
    public bool Reassess { get; set; } = true;
    public IFormFile? File { get; set; }
}

public sealed record RecruitmentResumeRecoveryResult(long ApplicationId, string ApplicationCode,
    long ResumeId, Guid AttachmentPublicId, bool AlreadyAvailable, string ParsingStatus,
    string EligibilityStatus = "", string ReassessmentError = "");

internal static class RecruitmentResumeRecoveryIdentity
{
    internal static bool SameMailbox(string candidateEmail, string senderEmail) => Canonical(candidateEmail) == Canonical(senderEmail)
        && !string.IsNullOrWhiteSpace(candidateEmail) && !string.IsNullOrWhiteSpace(senderEmail);

    private static string Canonical(string email)
    {
        var value = email.Trim().ToLowerInvariant();
        var parts = value.Split('@');
        // Only consumer Gmail guarantees dot and plus aliases. Keep corporate mailbox names exact.
        return parts.Length == 2 && parts[1] == "gmail.com"
            ? parts[0].Split('+')[0].Replace(".", "") + "@gmail.com" : value;
    }
}
