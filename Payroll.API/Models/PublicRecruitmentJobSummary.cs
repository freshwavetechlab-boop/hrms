namespace Payroll.API.Models;

// Deliberate public projection. Never return an internal posting/requisition or
// candidate form from the all-client directory.
public sealed class PublicRecruitmentJobSummary
{
    public string PublicSlug { get; set; } = "";
    public string PublicUrl { get; set; } = "";
    public string PublicTitle { get; set; } = "";
    public string PositionCode { get; set; } = "";
    public string PositionTitle { get; set; } = "";
    public string ClientName { get; set; } = "";
    public string Department { get; set; } = "";
    public string JobLocation { get; set; } = "";
    public string EmploymentType { get; set; } = "";
    public string WorkMode { get; set; } = "";
    public string ExperienceRange { get; set; } = "";
    // These are the same advertised raw amounts/currency as Jobs cards.
    // The source has no salary-period field: never invent annual/monthly/LPA.
    public decimal SalaryMin { get; set; }
    public decimal SalaryMax { get; set; }
    public string Currency { get; set; } = "INR";
    public int NumberOfPositions { get; set; }
    public int RemainingPositions { get; set; }
    public DateTime? PublishedAtUtc { get; set; }
    public DateTime? OpensAtUtc { get; set; }
    public DateTime? ClosesAtUtc { get; set; }
    public string Summary { get; set; } = "";
    public string RolePurpose { get; set; } = "";
    public List<string> Responsibilities { get; set; } = [];
    public List<string> Skills { get; set; } = [];
    public List<string> Qualifications { get; set; } = [];
    public List<string> Certifications { get; set; } = [];
    public List<string> Languages { get; set; } = [];
    public List<string> Benefits { get; set; } = [];
}
