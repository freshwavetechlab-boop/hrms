namespace Payroll.API.Models;

// Closed-month source operand assignment. This is distinct from an ordinary
// Applied source import and from every existing version-one named-input record.
public sealed record ReportedPeriodHistoricalAssignmentV2(int Version,
    ReportedPeriodInputManifestV2 Inputs, string EffectiveFrom, string EffectiveTo,
    decimal AnnualSalaryTarget, string SalaryInputsHash, long MonthlyAttendanceId,
    int ExistingAttendanceGroupId, string GroupStateHash, string ProvenanceHash,
    string AssignmentHash);
