namespace Payroll.API.Models;

public sealed class RecruitmentHiringProgressView
{
    public int RequiredCount { get; set; }
    public List<RecruitmentCandidateStageCount> CandidateStages { get; set; } = [];
    public string PendingReason { get; set; } = "";
}

public sealed record RecruitmentCandidateStageCount(string Stage, int Count);
