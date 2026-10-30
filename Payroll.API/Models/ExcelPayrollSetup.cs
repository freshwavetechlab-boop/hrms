using System.Text.Json.Nodes;

namespace Payroll.API.Models;

public sealed class ExcelPayrollSetupRequest
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? CorrectionOfImportId { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ExcelPayrollIdentityColumnReview? IdentityColumnReview { get; set; }
    public ExcelPayrollLocationAllocationReview? LocationAllocations { get; set; }
    public ExcelPayrollAttendanceReview? AttendanceReview { get; set; }
    public int TransferReviewVersion { get; set; }
    public int TemplateScopeReviewVersion { get; set; }
    public ExcelPayrollTemplateScope? TemplateScope { get; set; }
    public bool ReviewedFixedInputs { get; set; }
    public bool ReviewedPolicyScope { get; set; }
    public Dictionary<string, string> RoundingReviews { get; set; } = [];
    public List<ExcelPayrollTransferTemplateDecision> TemplateDecisions { get; set; } = [];
    public string BatchId { get; set; } = "";
    public List<string> RowIds { get; set; } = [];
    public string TemplateName { get; set; } = "";
    public string EffectiveFrom { get; set; } = "";
    public int PayrollDaysColumnIndex { get; set; } = -1;
    public int? FixedPayrollDays { get; set; }
    public string PayrollDaysCell { get; set; } = "";
    public Dictionary<int, string> SourceColumnRoles { get; set; } = [];
    public int PayableDaysColumnIndex { get; set; } = -1;
    public int? PresentDaysColumnIndex { get; set; }
    public string WorkWeek { get; set; } = "";
    public int AttendanceCycleStartDay { get; set; } = 1;
    public int AttendanceCycleEndDay { get; set; } = 30;
    public int PayrollReportGenerationDay { get; set; } = 3;
    public string DefaultDepartment { get; set; } = "";
    public string DefaultDesignation { get; set; } = "";
    public string DefaultGrade { get; set; } = "";
    public int? DefaultWorkLocationId { get; set; }
    public string DefaultWorkLocationName { get; set; } = "";
    public string DefaultWorkLocationState { get; set; } = "";
    public int? SourceWorkLocationColumnIndex { get; set; }
    public bool CreateMissingWorkLocations { get; set; }
    public bool CreateMissingDropdownValues { get; set; } = true;
    public bool PreserveExistingAttendancePolicies { get; set; } = true;
    public bool ImportAttendance { get; set; } = true;
    // Opt-in source payable units never certify actual presence. Omit the default to preserve legacy request hashes.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool ImportPayableOnlyAttendance { get; set; }
    // Explicit month-scoped salary payable override; does not replace dated presence.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool OverrideSourcePayableAttendance { get; set; }
    public List<ExcelPayrollSetupComponent> Components { get; set; } = [];
    public List<ExcelPayrollSetupEmployeeDecision> EmployeeDecisions { get; set; } = [];
    public JsonObject EditableDefaults { get; set; } = new();
}

public sealed class ExcelPayrollIdentityColumnReview
{
    public int Version { get; set; } = 1;
    public string Role { get; set; } = "UAN";
    public int ColumnIndex { get; set; }
    public string SourceHash { get; set; } = "";
    public string SelectionFingerprint { get; set; } = "";
    public string Reason { get; set; } = "";
    public bool Reviewed { get; set; }
}

public sealed record ExcelPayrollIdentityColumnCandidate(int ColumnIndex, string ColumnLetter, string SourceHeader,
    int SelectedRows, int CanonicalLiteralRows, int InvalidOrMissingRows, string SourceHash, string SelectionFingerprint);

public sealed class ExcelPayrollSetupComponent
{
    public string Key { get; set; } = "";
    public string? ExistingComponentId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "Earning";
    public string ComponentRole { get; set; } = "Regular Earning";
    public string StatutoryType { get; set; } = "None";
    public string CalculationType { get; set; } = "Fixed Amount";
    public string? PayType { get; set; }
    public string Value { get; set; } = "0";
    public string Formula { get; set; } = "";
    public string BaseComponent { get; set; } = "";
    public bool ProRata { get; set; } = true;
    public bool Taxable { get; set; } = true;
    public int Priority { get; set; } = 100;
    public int? SourceColumnIndex { get; set; }
    public int? ExpectedColumnIndex { get; set; }
    public bool IsPeriodAdjustment { get; set; }
    public bool ExemptWhenSourceZero { get; set; }
    public bool BlankInputAsZero { get; set; }
    public Dictionary<string,string> RowFormulas { get; set; } = [];
}

public sealed class ExcelPayrollSetupEmployeeDecision
{
    public string RowId { get; set; } = "";
    public int? MatchedEmployeeId { get; set; }
    public bool AllowCreate { get; set; }
}

public sealed class ExcelPayrollSetupApplyRequest
{
    public ExcelPayrollSetupRequest Request { get; set; } = new();
    public string PlanHash { get; set; } = "";
    public string RequestId { get; set; } = "";
}

public sealed class ExcelPayrollSetupDraftRequest
{
    public ExcelPayrollSetupRequest Request { get; set; } = new();
    public string RequestId { get; set; } = "";
}

public sealed class ExcelPayrollDefaultsDraftRequest
{
    public string BatchId { get; set; } = "";
    public string ImportId { get; set; } = "";
    public int ExpectedRevision { get; set; }
    public JsonObject EditableDefaults { get; set; } = new();
}

public sealed record ExcelPayrollSetupIssue(string Severity, string Message, string? RowId = null, string? Code = null);
public sealed record ExcelPayrollSetupLine(string Code, decimal? SourceAmount, decimal CalculatedAmount, decimal? Variance)
{
    public decimal? UnroundedEarned { get; init; }
    public string? Category { get; init; }
}
public sealed record ExcelPayrollPresentDaysEvidence(int? SourceColumnIndex, string? SourceCell,
    decimal? SourceValue, decimal CalculationValue, bool ActualWorkedReviewed, string Status, string Message);

public sealed class ExcelPayrollSetupPreviewRow
{
    // A calculation-only copy of the resolved row. Never accepted by Apply as a source or parity proof.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public PayrollSimulationRequest? WhatIfSimulation { get; set; }
    public string RowId { get; set; } = "";
    public int? EmployeeId { get; set; }
    public string EmployeeCode { get; set; } = "";
    public string EmployeeName { get; set; } = "";
    public string Action { get; set; } = "";
    public string TemplateId { get; set; } = "";
    public int PayrollDays { get; set; }
    public decimal PresentDays { get; set; }
    public ExcelPayrollPresentDaysEvidence? PresentDaysEvidence { get; set; }
    public decimal PayableDays { get; set; }
    public decimal AnnualCtc { get; set; }
    public decimal? Gross { get; set; }
    public decimal? Deductions { get; set; }
    public decimal? Benefits { get; set; }
    public decimal? Net { get; set; }
    public List<ExcelPayrollSetupLine> Lines { get; set; } = [];
}

public sealed record ExcelPayrollSelectedSourceRow(string RowId, int? SourceRow, bool SourceFound);

public sealed class ExcelPayrollSetupPreview
{
    public ExcelPayrollLocationAllocationPreview? LocationAllocations { get; set; }
    public List<ExcelPayrollSelectedSourceRow> SelectedSourceRows { get; set; } = [];
    public ExcelPayrollTransferReview? Transfer { get; set; }
    public string PlanHash { get; set; } = "";
    public bool CanApply => Issues.All(i => i.Severity != "Error");
    public bool MigrationRequired { get; set; }
    public string TemplateId { get; set; } = "";
    public List<string> TemplateIds { get; set; } = [];
    public string Coverage { get; set; } = "Draft template arithmetic and reviewed source inputs. Effective PF, professional tax, projected TDS and existing period adjustments require Employee simulation after setup. No payroll or masters are saved by preview.";
    public List<ExcelPayrollSetupIssue> Issues { get; set; } = [];
    public List<ExcelPayrollSetupPreviewRow> Rows { get; set; } = [];
}

public sealed class ExcelPayrollSetupResult
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? CorrectionOfImportId { get; set; }
    public List<ExcelPayrollReusedAdjustment> ReusedAdjustments { get; set; } = [];
    public List<ExcelPayrollPayableOverrideReceipt> PayableOverrides { get; set; } = [];
    public ExcelPayrollLocationAllocationPreview? LocationAllocations { get; set; }
    public List<ExcelPayrollLocationAssignmentReceipt> LocationAssignmentReceipts { get; set; } = [];
    public List<ExcelPayrollSetupIssue> DataQualityReviews { get; set; } = [];
    public ExcelPayrollTransferReview? Transfer { get; set; }
    public List<ExcelPayrollTransferRowReceipt> TransferRows { get; set; } = [];
    public List<ExcelPayrollTransferKeptRow> KeptRows { get; set; } = [];
    public List<ExcelPayrollAttendanceAssignment> AttendanceAssignments { get; set; } = [];
    public string ImportId { get; set; } = "";
    public string TemplateId { get; set; } = "";
    public List<string> TemplateIds { get; set; } = [];
    public int AttendanceGroupId { get; set; }
    public List<int> EmployeeIds { get; set; } = [];
    public int CreatedCount { get; set; }
    public int UpdatedCount { get; set; }
    public bool Replayed { get; set; }
}

public sealed record ExcelPayrollReusedAdjustment(int AdjustmentId, int EmployeeId, string RowId, int SourceRow,
    string ComponentCode, decimal Amount, string OriginalImportId, string BusinessFingerprint, string ReceiptFingerprint);

public sealed record ExcelPayrollPayableOverrideReceipt(int EmployeeId, string RowId, string Month,
    decimal PreviousWorkingDays, decimal? PreviousPresentDays, decimal PreviousPayableDays, decimal PreviousLopDays,
    string PreviousSourceType, string PreviousRemarks, int PayrollDays, decimal PayableDays);

public sealed record ExcelPayrollAttendanceAssignment(int EmployeeId, int GroupId, bool IsSourceCohort);

public sealed record ExcelPayrollRosterRow(string RowId, int SourceRow, string EmployeeName, string EmployeeCode,
    string Status, int? EmployeeId, string Message);
public sealed record ExcelPayrollMissingEmployee(int EmployeeId, string EmployeeCode, string EmployeeName, string WorkLocation);
public sealed record ExcelPayrollRoster(IReadOnlyList<ExcelPayrollRosterRow> Rows, IReadOnlyList<ExcelPayrollMissingEmployee> MissingEmployees);
