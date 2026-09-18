namespace Payroll.API.Models;

public sealed class RecruitmentLifecycleNotificationRule
{
    public long Id { get; set; }
    public int ClientId { get; set; }
    public string ClientName { get; set; } = "";
    public string TriggerCode { get; set; } = "";
    public string TriggerName { get; set; } = "";
    public string Description { get; set; } = "";
    public string SubjectTemplate { get; set; } = "";
    public string CandidateBodyTemplate { get; set; } = "";
    public string InternalBodyTemplate { get; set; } = "";
    public bool SendToCandidate { get; set; }
    public bool SendToPanel { get; set; }
    public bool SendToRequester { get; set; }
    public bool SendToRecruiter { get; set; }
    public bool SendToApprovers { get; set; }
    public bool IsEnabled { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class SaveRecruitmentLifecycleNotificationRule
{
    public long Id { get; set; }
    public int ClientId { get; set; }
    public string TriggerCode { get; set; } = "";
    public string TriggerName { get; set; } = "";
    public string Description { get; set; } = "";
    public string SubjectTemplate { get; set; } = "";
    public string CandidateBodyTemplate { get; set; } = "";
    public string InternalBodyTemplate { get; set; } = "";
    public bool SendToCandidate { get; set; }
    public bool SendToPanel { get; set; }
    public bool SendToRequester { get; set; }
    public bool SendToRecruiter { get; set; }
    public bool SendToApprovers { get; set; }
    public bool IsEnabled { get; set; }
}
