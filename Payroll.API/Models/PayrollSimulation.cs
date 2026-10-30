namespace Payroll.API.Models;

public sealed record PayrollValueDefinition(string Token, string Meaning, string Source, string Unit, string Example, string Availability, bool Legacy = false, string? ComponentId = null);
public sealed record PayrollCatalogueComponent(string Id, string Code, string Name, bool Active);
public sealed record PayrollCatalogueRequest(List<PayrollCatalogueComponent> Components, List<string>? IncludedComponentIds = null, string? ExcludeComponentId = null);
public sealed class PayrollSimulationRequest
{
    public string Mode { get; set; } = "Template";
    public int ClientId { get; set; }
    public int? EmployeeId { get; set; }
    public string TemplateId { get; set; } = "";
    public string PayPeriod { get; set; } = "";
    public PayrollReviewSnapshot? Snapshot { get; set; }
    public int? PayrollDays { get; set; }
    public decimal? PresentDays { get; set; }
    public decimal? PayableDays { get; set; }
    public decimal? AnnualCtc { get; set; }
    public Dictionary<string, decimal> AmountOverrides { get; set; } = [];
}
public sealed record PayrollSimulationInput(string Name, decimal? Value, string Source, string Unit);
public sealed record PayrollAttendanceInputEvidence(string SourceKind, string PayPeriod, decimal? WorkingDays,
    decimal? PresentDays, decimal? PayableDays, decimal? WeeklyOffPayableUnits, decimal? HolidayPayableUnits,
    string? CycleStart, string? CycleEnd, bool TemporaryDayOverrides, string PolicyValidationError, string Explanation)
{
    public bool ActualPresenceKnown => PresentDays.HasValue;
}
public sealed record PayrollSimulationIssue(string Severity, string Component, string Message, string Action);
public sealed record PayrollSimulationLine(string Code, string Name, string Category, string Formula, string Substituted, decimal Raw, decimal Monthly, decimal Earned, bool ProRata, string Source, string Rounding, Dictionary<string, decimal> Inputs) { public decimal? UnroundedEarned { get; init; } }
public sealed class PayrollSimulationResult
{
    public int ClientId { get; set; }
    public int? EmployeeId { get; set; }
    public string PayPeriod { get; set; } = "";
    public PayrollLocationPartPeriod? LocationAllocations { get; set; }
    public string Mode { get; set; } = "";
    public PayrollAttendanceInputEvidence? AttendanceEvidence { get; set; }
    public string TemplateId { get; set; } = "";
    public string Employee { get; set; } = "";
    public bool Complete => Issues.All(i => i.Severity != "Error");
    public List<PayrollSimulationIssue> Issues { get; set; } = [];
    public List<PayrollSimulationInput> Inputs { get; set; } = [];
    public List<PayrollSimulationLine> Lines { get; set; } = [];
    public decimal? Gross { get; set; }
    public decimal? Deductions { get; set; }
    public decimal? Benefits { get; set; }
    public decimal? Net { get; set; }
    public string Coverage { get; set; } = "";
}
