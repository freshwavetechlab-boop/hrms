namespace Payroll.API.Models;

public sealed class RecruitmentRequesterOption
{
    public int UserId { get; set; }
    public string DisplayName { get; set; } = "";
    public string Email { get; set; } = "";
    public int ClientId { get; set; }
}
