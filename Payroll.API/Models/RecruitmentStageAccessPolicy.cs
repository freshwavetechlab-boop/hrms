namespace Payroll.API.Models;

public sealed class RecruitmentStageAccessPolicy
{
    public string OwnerType { get; set; } = "Unassigned";
    public string DepartmentLabel { get; set; } = "";
    public bool Restricted { get; set; }
    public List<string> AllowedRoleCodes { get; set; } = [];
    public List<int> AllowedUserIds { get; set; } = [];
}
