namespace Payroll.API.Models;

public class TravelExpenseSetup
{
    public IEnumerable<TravelPolicy> Policies { get; set; } = [];
    public IEnumerable<TravelPolicyAssignment> Assignments { get; set; } = [];
    public IEnumerable<TravelPolicyRule> Rules { get; set; } = [];
    public IEnumerable<TravelExpenseCategory> Categories { get; set; } = [];
    public IEnumerable<TravelPolicyAudit> Audit { get; set; } = [];
}

public class TravelPolicy
{
    public long Id { get; set; }
    public string PolicyCode { get; set; } = string.Empty;
    public string PolicyName { get; set; } = string.Empty;
    public int CompanyId { get; set; }
    public string CompanyName { get; set; } = string.Empty;
    public string BusinessUnit { get; set; } = string.Empty;
    public DateTime EffectiveFrom { get; set; } = DateTime.Today;
    public DateTime? EffectiveTo { get; set; }
    public string Status { get; set; } = "Draft";
    public string Description { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class TravelPolicyAssignment
{
    public long Id { get; set; }
    public long PolicyId { get; set; }
    public string PolicyName { get; set; } = string.Empty;
    public int CompanyId { get; set; }
    public string CompanyName { get; set; } = string.Empty;
    public int? BranchId { get; set; }
    public string BranchName { get; set; } = string.Empty;
    public string Department { get; set; } = string.Empty;
    public string Grade { get; set; } = string.Empty;
    public string Designation { get; set; } = string.Empty;
    public string EmployeeCategory { get; set; } = string.Empty;
    public string EmploymentType { get; set; } = string.Empty;
    public int? EmployeeId { get; set; }
    public string EmployeeName { get; set; } = string.Empty;
    public int Priority { get; set; } = 100;
    public DateTime EffectiveFrom { get; set; } = DateTime.Today;
    public DateTime? EffectiveTo { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class TravelPolicyRule
{
    public long Id { get; set; }
    public long PolicyId { get; set; }
    public string PolicyName { get; set; } = string.Empty;
    public string RuleType { get; set; } = string.Empty;
    public string RuleName { get; set; } = string.Empty;
    public string AppliesTo { get; set; } = string.Empty;
    public bool IsAllowed { get; set; } = true;
    public string EligibilityJson { get; set; } = "{}";
    public decimal? LimitAmount { get; set; }
    public string LimitCurrency { get; set; } = "INR";
    public bool ReceiptMandatory { get; set; }
    public bool ApprovalRequired { get; set; }
    public long? WorkflowId { get; set; }
    public string WorkflowName { get; set; } = string.Empty;
    public string ExceptionHandling { get; set; } = "Warning";
    public string ConfigJson { get; set; } = "{}";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class TravelExpenseCategory
{
    public long Id { get; set; }
    public int ClientId { get; set; }
    public string ClientName { get; set; } = string.Empty;
    public long? ParentId { get; set; }
    public string ParentName { get; set; } = string.Empty;
    public string ExpenseType { get; set; } = string.Empty;
    public bool IsClaimHeader { get; set; }
    public string CategoryCode { get; set; } = string.Empty;
    public string CategoryName { get; set; } = string.Empty;
    public bool ReceiptMandatory { get; set; }
    public bool GstApplicable { get; set; }
    public decimal? DailyLimit { get; set; }
    public decimal? MaximumClaim { get; set; }
    public bool RequiresFinanceApproval { get; set; }
    public bool RequiresManagerApproval { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class TravelPolicyAudit
{
    public long Id { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public long EntityId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string OldValueJson { get; set; } = string.Empty;
    public string NewValueJson { get; set; } = string.Empty;
    public string ChangedBy { get; set; } = string.Empty;
    public DateTime ChangedOn { get; set; }
}
