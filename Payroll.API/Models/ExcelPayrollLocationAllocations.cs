namespace Payroll.API.Models;

// Reviewed source-month attendance allocations. Configuration dates describe the
// import/version, never invented calendar ranges for source location parts.
public sealed class ExcelPayrollLocationAllocationReview
{
    public int Version { get; set; } = 1;
    public string PayrollPeriod { get; set; } = "";
    public string ConfigurationEffectiveFrom { get; set; } = "";
    public bool Reviewed { get; set; }
    public bool ReviewedSettingsApplicability { get; set; }
    public List<ExcelPayrollLocationAllocationGroup> Groups { get; set; } = [];
}

public sealed class ExcelPayrollLocationAllocationGroup
{
    public int EmployeeId { get; set; }
    public string? ReferenceAssignmentId { get; set; }
    public List<ExcelPayrollLocationAllocationPart> Parts { get; set; } = [];
}

public sealed class ExcelPayrollLocationAllocationPart
{
    public string RowId { get; set; } = "";
    public int WorkLocationId { get; set; }
    public string TemplateId { get; set; } = "";
    public Dictionary<string, string> ComponentScopes { get; set; } = [];
}

public sealed class ExcelPayrollLocationAllocationPreview
{
    public int Version { get; set; } = 1;
    public string PayrollPeriod { get; set; } = "";
    public string ConfigurationEffectiveFrom { get; set; } = "";
    public List<ExcelPayrollLocationAllocationGroupPreview> Groups { get; set; } = [];
}

public sealed class ExcelPayrollLocationAllocationGroupPreview
{
    public int EmployeeId { get; set; }
    public string? ReferenceAssignmentId { get; set; }
    public List<ExcelPayrollLocationAllocationPartPreview> Parts { get; set; } = [];
}

public sealed class ExcelPayrollLocationAllocationPartPreview
{
    public string RowId { get; set; } = "";
    public int SourceRow { get; set; }
    public string PartKey { get; set; } = "";
    public string AssignmentId { get; set; } = "";
    public int WorkLocationId { get; set; }
    public string WorkLocationName { get; set; } = "";
    public string WorkState { get; set; } = "";
    public string TemplateId { get; set; } = "";
    public int PayrollDays { get; set; }
    public decimal PresentDays { get; set; }
    public decimal PayableDays { get; set; }
    public decimal AnnualCtc { get; set; }
    public Dictionary<string, decimal> SalaryInputs { get; set; } = [];
    public Dictionary<string, string> ComponentScopes { get; set; } = [];
    public string RulesHash { get; set; } = "";
    public string SourceHash { get; set; } = "";
}

public sealed record ExcelPayrollLocationAssignmentReceipt(string RowId, int EmployeeId, string PartKey,
    string AssignmentId, int WorkLocationId, string TemplateId, string PayPeriod,
    string ConfigurationEffectiveFrom, string RulesHash, string SourceHash);
