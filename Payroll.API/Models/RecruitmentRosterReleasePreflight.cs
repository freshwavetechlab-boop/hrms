namespace Payroll.API.Models;

public sealed class RecruitmentRosterReleasePreflight
{
    public DateTime CheckedAtUtc { get; set; }
    public List<string> Issues { get; set; } = [];
    public List<RecruitmentRosterReleaseCheck> Items { get; set; } = [];
    public bool CanRelease => Issues.Count == 0 && Items.Count > 0 && Items.All(item => item.Ready);
}

public sealed class RecruitmentRosterReleaseCheck
{
    public long RosterId { get; set; }
    public string ProfileCode { get; set; } = "";
    public int TemplateId { get; set; }
    public string TemplateName { get; set; } = "";
    public string Revision { get; set; } = "";
    public List<string> Issues { get; set; } = [];
    public bool Ready => Issues.Count == 0;
}
