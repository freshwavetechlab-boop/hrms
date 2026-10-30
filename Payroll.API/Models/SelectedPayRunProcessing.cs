namespace Payroll.API.Models;

public sealed class SelectedPayRunProcessingRequest
{
    public int ClientId { get; set; }
    public string PayPeriod { get; set; } = "";
    public string ExpectedFingerprint { get; set; } = "";
    public bool Confirmed { get; set; }
}

public sealed class SelectedPayRunProcessingPreview
{
    public int PayRunId { get; set; }
    public int ClientId { get; set; }
    public string ClientName { get; set; } = "";
    public string PayPeriod { get; set; } = "";
    public string RunCode { get; set; } = "";
    public string Status { get; set; } = "";
    public int EmployeeCount { get; set; }
    public List<SelectedPayRunEmployee> Employees { get; set; } = [];
    public List<SelectedPayRunAdjustmentReview> Adjustments { get; set; } = [];
    public string Fingerprint { get; set; } = "";
    public bool CanProcess { get; set; }
    public List<string> Blockers { get; set; } = [];
    public List<string> Effects { get; set; } =
    [
        "Calculate only this held or queued run and save its Draft result or Failed diagnostics using the regular payroll calculator.",
        "Use current effective employee, salary, policy and attendance inputs. This scope preview is not an amount or parity certification.",
        "Retain existing runs and diagnostics. Do not submit, approve, pay, publish or send payslips; do not process the global queue."
    ];
}

// Server-generated provenance/business snapshots, without personal or bank identifiers.
public sealed record SelectedPayRunAdjustmentReview(int AdjustmentId, int EmployeeId, string ComponentCode,
    string Category, decimal Amount, bool Taxable, string SourceBatchId, string SourceImportId,
    string SourceRowId, int SourceRow, string SourceHash, string ReceiptFingerprint, string BusinessFingerprint)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public SelectedAppliedSourceWitness? SourceWitness { get; init; }
}

public sealed record SelectedAppliedSourceWitness(string Kind, int HistoricalTransferVersion,
    string OriginalReceiptFingerprint, string SourceHash, string MappingFingerprint, string IdentityFingerprint,
    string SourceFormulaFingerprint, string NativeInputFingerprint, string SalaryPolicyFingerprint,
    string CoreCalculationFingerprint, string ComparisonFingerprint, string WitnessFingerprint);
public sealed record SelectedSourceReviewAcknowledgement(string ExpectedFingerprint, bool Confirmed);
public sealed record SelectedSourceReviewAttestation(int ConfirmedByUserId, DateTime ConfirmedAtUtc, string WitnessFingerprint);
public sealed record SelectedSourceFinancialLine(string Code, string Category, decimal SourceRaw,
    decimal SourceDisplay, decimal CoreAmount, decimal? CoreUnrounded, string FormulaStatus)
{
    public string ComponentRole { get; init; } = "";
    public string StatutoryType { get; init; } = "None";
    public bool Taxable { get; init; }
    public bool ProRata { get; init; }
    public string SourceFormula { get; init; } = "";
    public string NativeFormula { get; init; } = "";
    public int SourceColumnIndex { get; init; }
    public string SourceLabel { get; init; } = "";
    public string SourceHeader { get; init; } = "";
}
public sealed class SelectedDraftSourceReviewPreview
{
    public string Kind { get; set; } = "ReviewedAppliedSourceV1";
    public int HistoricalTransferVersion { get; set; }
    public bool CanConfirm => Blockers.Count == 0 && Reviews.Count == 1;
    public string Fingerprint { get; set; } = "";
    public List<string> Blockers { get; set; } = [];
    public List<SelectedPayRunAdjustmentReview> Reviews { get; set; } = [];
    public List<SelectedSourceFinancialLine> Lines { get; set; } = [];
    public PayrollAttendanceInputEvidence? NativeAttendance { get; set; }
    public string SourceActualWorkedMeaning { get; set; } = "NotMapped";
    public decimal? SourceNet { get; set; }
    public decimal? CoreNet { get; set; }
}

public sealed class SelectedPayRunEmployee
{
    public int EmployeeId { get; set; }
    public string EmployeeCode { get; set; } = "";
    public string EmployeeName { get; set; } = "";
}
