namespace Payroll.API.Models;

public class DirectInterviewApplication
{
    public long PositionId { get; set; }
    public string Email { get; set; } = "";
    public string FullName { get; set; } = "";
    public string Phone { get; set; } = "";
}

public class RecruitmentNegotiation
{
    public long ApplicationId { get; set; }
    public decimal? ExpectedCtc { get; set; }
    public decimal ApprovedBudget { get; set; }
    public string BudgetBasis { get; set; } = "";
    public string Currency { get; set; } = "INR";
    public bool? NegotiationOverride { get; set; }
    public bool Required => NegotiationOverride ?? (ExpectedCtc > ApprovedBudget && ApprovedBudget > 0);
    public decimal? AgreedCtc { get; set; }
    public DateTime? TermsConfirmedAtUtc { get; set; }
    public int TermsVersion { get; set; }
    public string ApprovalStatus { get; set; } = "";
    public string ReviewComment { get; set; } = "";
}

public class SaveRecruitmentNegotiation
{
    public bool? NegotiationOverride { get; set; }
    public decimal? AgreedCtc { get; set; }
    public bool ConfirmTerms { get; set; }
}

public class SignCandidateMom
{
    public string SignerName { get; set; } = "";
    public int TermsVersion { get; set; }
    public bool Consent { get; set; }
}

public class CandidateMom
{
    public long Id { get; set; }
    public long ApplicationId { get; set; }
    public int VersionNumber { get; set; }
    public int TermsVersion { get; set; }
    public string BodySnapshot { get; set; } = "";
    public string Status { get; set; } = "";
    public DateTime? SignedAtUtc { get; set; }
    public string ApprovalStatus { get; set; } = "";
    public string? ReviewComment { get; set; }
}
