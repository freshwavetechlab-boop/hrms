namespace Payroll.API.Models;

public sealed class ExcelPayrollLocationCorrectionRequest
{
    public int PayRunId { get; set; }
    public string BatchId { get; set; } = "";
    public List<int> EmployeeIds { get; set; } = [];
    public bool RecoverOrphanedLineage { get; set; }
    public string RecoverySourceHash { get; set; } = "";
    public ExcelPayrollSetupRequest? SourceReview { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ExcelPayrollLocationRowRebindReview? RowRebindReview { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public NamedFormulaInputReview? NamedInputReview { get; set; }
}

public sealed class ExcelPayrollLocationRowRebindReview
{
    public int Version { get; set; } = 1;
    public bool Reviewed { get; set; }
    public List<ExcelPayrollLocationRowBinding> Rows { get; set; } = [];
}
public sealed record ExcelPayrollLocationRowBinding(int EmployeeId,string AssignmentId,string SourcePartKey,
    string StoredSourceRowId,string CurrentRowId,int SourceRow,string SourceIdentityHash);
public sealed record ExcelPayrollLocationRowRebindEvidence(int EmployeeId,string AssignmentId,string SourcePartKey,
    string StoredSourceRowId,string CurrentRowId,int SourceRow,string SourceIdentityHash,string OldFinancialProofHash,
    decimal ActualPresentDays,decimal PreviousPayableDays);

public sealed class ExcelPayrollLocationCorrectionApplyRequest
{
    public ExcelPayrollLocationCorrectionRequest Request { get; set; } = new();
    public string PlanHash { get; set; } = "";
    public string RequestId { get; set; } = "";
    public bool Acknowledged { get; set; }
}

public sealed class ExcelPayrollLocationCorrectionPreview
{
    public int ClientId { get; set; }
    public int PayRunId { get; set; }
    public string BatchId { get; set; } = "";
    public string Month { get; set; } = "";
    public string PlanHash { get; set; } = "";
    public string SourceHash { get; set; } = "";
    public string RawBatchSha256 { get; set; } = "";
    public string RawSourceSha256 { get; set; } = "";
    public bool CanApply => Blockers.Count == 0;
    public string Coverage { get; set; } = "Calculation only: complete saved source parts, exact existing Settings rules and native Employee payroll. Source divisor/payable dependencies are evaluated; known native actual presence is retained separately. No leave/off entitlement is inferred and preview saves no payroll, Master, source or delivery data.";
    public List<string> Blockers { get; set; } = [];
    public List<ExcelPayrollLocationCorrectionEmployee> Employees { get; set; } = [];
    public List<ExcelPayrollLocationRecoveryEvidence> RecoveryEvidence { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<ExcelPayrollLocationRowRebindEvidence>? RowRebindings { get; set; }
    public List<ExcelPayrollLocationCorrectionField> Changes { get; set; } = [];
    public List<ExcelPayrollLocationCorrectionAggregate> Aggregates { get; set; } = [];
}

public sealed record ExcelPayrollLocationRecoveryEvidence(int EmployeeId, string MissingParentImportId,
    string ParentStatus, string LegacyAllocationSourceHash, string CurrentSourceHash, bool SourceHashDiffers, bool DurableProfileLinkVerified);
public sealed record ExcelPayrollLocationCorrectionField(int EmployeeId, string SourceRowId, string Field, string Previous, string Proposed);
public sealed record ExcelPayrollLocationCorrectionAggregate(string SourceRowId,int ColumnIndex,string Formula,
    IReadOnlyList<int> OriginalOutputColumns,decimal Amount,string ProofHash);

public sealed record ExcelPayrollLocationCorrectionEmployee(int EmployeeId, int PayRunEmployeeId,
    decimal PreviousPresentDays, decimal PreviousPayableDays, decimal PresentDays, decimal PayableDays,
    decimal PreviousGross, decimal PreviousDeductions, decimal PreviousNet,
    decimal Gross, decimal Deductions, decimal Employer, decimal Net,
    IReadOnlyList<ExcelPayrollSetupPreviewRow> SourceParts);

public sealed record ExcelPayrollLocationCorrectionResult(string ReceiptId, int PayRunId,
    IReadOnlyList<int> EmployeeIds, string PlanHash, bool Replayed);
