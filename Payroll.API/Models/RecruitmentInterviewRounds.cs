namespace Payroll.API.Models;

public sealed class RecruitmentInterviewRoundDefinition
{
    public string Name { get; set; } = "";
    public string InterviewType { get; set; } = "Technical";
}

public sealed class SaveRecruitmentInterviewRounds
{
    public int ExpectedRevision { get; set; }
    public List<RecruitmentInterviewRoundDefinition> Rounds { get; set; } = [];
}

public sealed class RecruitmentInterviewRoundState
{
    public string Name { get; set; } = "";
    public string InterviewType { get; set; } = "";
    public long? InterviewId { get; set; }
    public string Status { get; set; } = "Pending";
    public string Result { get; set; } = "Pending";
    public bool Passed { get; set; }
    public bool CanSchedule { get; set; }
}

public sealed class RecruitmentInterviewRoundPlan
{
    public long ApplicationId { get; set; }
    public long PositionId { get; set; }
    public string StageName { get; set; } = "";
    public int Revision { get; set; }
    public bool IsConfigured { get; set; }
    public bool CanConfigure { get; set; }
    public bool Complete => Rounds.Count > 0 && Rounds.All(r => r.Passed);
    public int CompletedCount => Rounds.Count(r => r.Passed);
    public List<RecruitmentInterviewRoundState> Rounds { get; set; } = [];
}
