namespace Payroll.API.Models;

public sealed record RecruitmentRosterSimpleJdLink(long RosterId, long JobDescriptionId, string Source, string Title);

public sealed class RecruitmentRosterSimpleJd
{
    public long Id { get; set; }
    public int ClientId { get; set; }
    public string Source { get; set; } = "Simple";
    public string Status { get; set; } = "Ready";
    public string Title { get; set; } = "";
    public string Summary { get; set; } = "";
    public string RolePurpose { get; set; } = "";
    public List<string> Responsibilities { get; set; } = [];
    public List<string> Skills { get; set; } = [];
    public List<string> Qualifications { get; set; } = [];
    public List<string> Certifications { get; set; } = [];
    public List<string> Languages { get; set; } = [];
    public List<string> Benefits { get; set; } = [];
}

public sealed record CreateRecruitmentRosterSimpleJd(string PositionTitle, string Revision);
public sealed record CreateRecruitmentRosterSimpleJds(List<long> Ids, Dictionary<string, string> Revisions);
public sealed record RecruitmentRosterSimpleJdCreated(RecruitmentRosterSimpleJd JobDescription, List<long> LinkedRosterIds, bool Created);
public sealed record RecruitmentRosterSimpleJdsCreated(int Created, int Reused, int LinkedRows);
