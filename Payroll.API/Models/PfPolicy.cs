namespace Payroll.API.Models;

public sealed class PfContributionRule
{
    public string ComponentId { get; set; } = "";
    [System.Text.Json.Serialization.JsonRequired]
    public decimal RatePercent { get; set; }
    public decimal? MonthlyWageCeiling { get; set; }
    // Captured from the selected template when publishing, never inferred from its formula.
    public string ComponentCode { get; set; } = "";
    public string StatutoryType { get; set; } = "";
}

public sealed class SavePfPolicyVersionRequest
{
    public string? Id { get; set; }
    public string SalaryStructureId { get; set; } = "";
    public string Name { get; set; } = "";
    public DateOnly EffectiveFrom { get; set; }
    public string Reason { get; set; } = "";
    public string BaseComponentCode { get; set; } = "";
    public string CeilingBasis { get; set; } = "";
    public List<PfContributionRule> Contributions { get; set; } = [];
    public decimal? EdliMonthlyWageCeiling { get; set; }
}

public sealed class PublishPfPolicyVersionRequest { public string Reason { get; set; } = ""; }

public sealed class PfPolicyVersion
{
    public string Id { get; set; } = "";
    public int VersionNumber { get; set; }
    public int ClientId { get; set; }
    public string SalaryStructureId { get; set; } = "";
    public string Name { get; set; } = "";
    public DateOnly EffectiveFrom { get; set; }
    public string Reason { get; set; } = "";
    public string BaseComponentCode { get; set; } = "";
    public string CeilingBasis { get; set; } = "";
    public List<PfContributionRule> Contributions { get; set; } = [];
    public decimal? EdliMonthlyWageCeiling { get; set; }
    public string Status { get; set; } = "Draft";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? PublishedAtUtc { get; set; }
    public string CreatedBy { get; set; } = "";
    public string PublishedBy { get; set; } = "";
}

public sealed record PfPolicyAttendanceDay(DateOnly Date, decimal PayableValue, string Status, string? AttendanceRuleVersionId = null);
public sealed record PfPolicyPeriod(DateOnly Start, DateOnly End, IReadOnlyList<PfPolicyVersion> Versions, IReadOnlyList<PfPolicyAttendanceDay> Days);
public sealed record PfPolicySegmentSnapshot(string VersionId, int VersionNumber, DateOnly From, DateOnly To,
    string BaseComponentCode, decimal MonthlyBase, int CalendarDays, decimal PayableDays, string CeilingBasis,
    decimal? MonthlyWageCeiling, decimal WageBase, decimal RatePercent, decimal UnroundedContribution,
    decimal? EdliWageBase, IReadOnlyList<PfPolicyAttendanceDay> Attendance);
public sealed record PfComponentSnapshot(int SchemaVersion, string SalaryStructureId, string ComponentId,
    string ComponentCode, string StatutoryType, DateOnly PeriodStart, DateOnly PeriodEnd, int PayrollDays,
    decimal PayableDays, decimal Contribution, decimal? EpfWages, decimal? EdliWages,
    IReadOnlyList<PfPolicySegmentSnapshot> Segments);

public sealed record PayrollAttendanceDaySnapshot(DateOnly Date, string Status, decimal PayableValue,
    string? RuleVersionId, int? RuleVersionNumber, string Reason, string WorkWeek, string WorkWeekConfig,
    DateOnly? PreviousWorkingDate, DateOnly? NextWorkingDate);
public sealed record PayrollAttendanceSnapshot(int SchemaVersion, DateOnly PeriodStart, DateOnly PeriodEnd,
    string Source, IReadOnlyList<WeeklyOffRuleVersion> Versions, IReadOnlyList<PayrollAttendanceDaySnapshot> Days);
