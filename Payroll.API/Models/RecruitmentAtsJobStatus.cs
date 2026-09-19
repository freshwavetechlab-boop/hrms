namespace Payroll.API.Models;

public sealed class RecruitmentAtsJobStatus
{
    public long Id { get; set; }
    public long ApplicationId { get; set; }
    public string Status { get; set; } = "";
    public string LastError { get; set; } = "";
}
