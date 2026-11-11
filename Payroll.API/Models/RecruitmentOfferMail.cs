namespace Payroll.API.Models;

public sealed class RecruitmentOfferMailRequest
{
    public string RequestId { get; set; } = "";
    public List<long> Ids { get; set; } = [];
    public bool RetryFailed { get; set; }
    public Dictionary<string,long> ReleaseIds { get; set; } = [];
}

public sealed class RecruitmentOfferMailItem
{
    public long RosterId { get; set; }
    public int? WorkLocationId { get; set; }
    public string CandidateCode { get; set; } = "";
    public string CandidateName { get; set; } = "";
    public string Email { get; set; } = "";
    public long? ReleaseId { get; set; }
    public long? QueueId { get; set; }
    public string Status { get; set; } = "";
    public string Message { get; set; } = "";
    public string SentCopyStatus { get; set; } = "Unknown";
    public string DeliveryStatus { get; set; } = "";
}

public sealed class RecruitmentOfferMailJob
{
    public string Id { get; set; } = "";
    public int ClientId { get; set; }
    public string ClientName { get; set; } = "";
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? DismissedAtUtc { get; set; }
    public string Status { get; set; } = "Queued";
    public string Message { get; set; } = "";
    public int Total { get; set; }
    public int Sent { get; set; }
    public int Queued { get; set; }
    public int Failed { get; set; }
    public int MissingEmail { get; set; }
    public int NotReleased { get; set; }
    public int Skipped { get; set; }
    public int Unknown { get; set; }
    public int Completed { get; set; }
    public int PercentComplete { get; set; }
    public bool IsComplete { get; set; }
    public int TotalEmails { get; set; }
    public int AcceptedEmails { get; set; }
    public int SentCopySavedEmails { get; set; }
    public int SentCopyUnverifiedEmails { get; set; }
    public int DeliveredEmails { get; set; }
    public List<RecruitmentOfferMailItem> Items { get; set; } = [];
}
