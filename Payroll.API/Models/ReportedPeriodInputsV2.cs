namespace Payroll.API.Models;

// This review declares operator-reported salary-period facts. It never changes
// the captured workbook's dependency status or certifies dated worked days.
public sealed class ReportedPeriodInputReview
{
    public int Version { get; set; } = 2;
    public bool Reviewed { get; set; }
    public string Meaning { get; set; } = "ReportedSalaryPeriodFacts";
    public int ClientId { get; set; }
    public string BatchId { get; set; } = "";
    public string PayPeriod { get; set; } = "";
    public string SourceHash { get; set; } = "";
    public string RawBatchHash { get; set; } = "";
    public string RawSourceHash { get; set; } = "";
    public string PrimaryWorkbookSha256 { get; set; } = "";
    public string CycleStart { get; set; } = "";
    public string CycleEnd { get; set; } = "";
    public string SharedPayrollDivisorCell { get; set; } = "";
    public List<string> TotalPayableKeys { get; set; } = [];
    public List<ReportedPeriodInputBinding> Bindings { get; set; } = [];
    public List<ReportedPeriodInputRowReview> Rows { get; set; } = [];
}

public sealed record ReportedPeriodInputBinding(string Key, int ColumnIndex, int HeaderRow,
    string SourceHeader, string SourceMode, int? MaximumColumnIndex = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? OriginalHeaderText = null);

public sealed class ReportedPeriodInputRowReview
{
    public string RowId { get; set; } = "";
    public int SourceRow { get; set; }
    public string SourceIdentityHash { get; set; } = "";
    public Dictionary<string, decimal> ExpectedValues { get; set; } = [];
    public Dictionary<string, string> CellHashes { get; set; } = [];
}

public sealed record ReportedPeriodInputProfileV2(int Version, int ClientId, string TemplateId,
    string RulesHash, List<NamedFormulaInputDefinition> Definitions, List<string> TotalPayableKeys,
    string DeclarationHash);

public sealed record ReportedPeriodInputCellV2(string Key, int SourceRow, int ColumnIndex,
    string Header, string SourceMode, decimal Value, string CellHash, string? OriginalFormula,
    string? IndependentExpression, int HeaderRow, string OriginalHeaderText, string HeaderCellHash, string EvidenceHash);

public sealed record ReportedPeriodBoundRowV2(int Version, int ClientId, string BatchId,
    string PayPeriod, string RowId, int SourceRow, string SourceIdentityHash, string SourceHash,
    string RawBatchHash, string RawSourceHash, string PrimaryWorkbookSha256,
    string DeclarationHash, string ReviewHash, int PayrollDivisor, decimal PayableDays,
    Dictionary<string, decimal> Values, List<ReportedPeriodInputCellV2> Cells, string ProofHash)
{
    public string ProofKind => "ReportedSalaryPeriodFacts";
    public bool ActualPresenceKnown => false;
    public bool ExternalLookupsVerified => false;
    public bool DependencyCompletenessClaimed => false;
}

// Protected sidecar only. An actual existing-Master ownership receipt and a
// matching native monthly snapshot are required by the future integration.
public sealed record ReportedPeriodInputManifestV2(int Version, int ClientId, int EmployeeId,
    string PayPeriod, long HistoryId, string TemplateId, string NativeIdentityOwnershipHash,
    string NativeAttendanceSnapshotHash, string RequestId, int ReviewerId, DateTime ReviewedAtUtc,
    ReportedPeriodBoundRowV2 BoundInputs, string ManifestHash);

public sealed record ReportedPeriodCashCellV2(int ColumnIndex, string SourceCell, string Kind,
    string? OriginalFormula, string NativeExpression, string StructureHash,
    decimal Calculated, decimal SavedAmount, decimal Difference);

public sealed record ReportedPeriodCashClosureV2(string RowId, int SourceRow, string SourceHash,
    string BoundInputsHash, int DeclaredDayBoundaries, string SharedDivisorCell,
    List<ReportedPeriodCashCellV2> Cells, List<string> RequiredIntegrationGates, string ProofHash)
{
    public bool ExternalLookupsVerified => false;
    public bool DependencyCompletenessClaimed => false;
    public bool NativePayrollParityCertified => false;
}
