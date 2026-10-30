namespace Payroll.API.Models;

// Period attendance allocations, not inferred calendar transfer intervals. Salary
// formulas remain in Settings; these inputs contain only reviewed recurring bases.
public sealed class PayrollLocationPartPeriod
{
    public int Version { get; set; } = 1;
    public int ClientId { get; set; }
    public int EmployeeId { get; set; }
    public string PayPeriod { get; set; } = "";
    public string? ReferenceAssignmentId { get; set; }
    public List<PayrollLocationPart> Parts { get; set; } = [];
}

public sealed class PayrollLocationPart
{
    public string PartKey { get; set; } = "";
    public string AssignmentId { get; set; } = "";
    public int WorkLocationId { get; set; }
    public string WorkState { get; set; } = "";
    public string SalaryStructureId { get; set; } = "";
    public decimal AnnualCtc { get; set; }
    public Dictionary<string, decimal> SalaryInputs { get; set; } = [];
    public Dictionary<string, string> ComponentScopes { get; set; } = [];
    public int PayrollDays { get; set; }
    public decimal PresentDays { get; set; }
    public decimal PayableDays { get; set; }
}

public sealed record PayrollLocationComponentPart(string PartKey, string AssignmentId, int WorkLocationId,
    string SalaryStructureId, int PayrollDays, decimal PresentDays, decimal PayableDays,
    string Scope, string Formula, decimal Monthly, decimal Earned, decimal? UnroundedEarned = null);
