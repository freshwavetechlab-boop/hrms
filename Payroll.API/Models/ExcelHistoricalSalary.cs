namespace Payroll.API.Models;

public sealed class HistoricalPeriodSalaryRequest
{
    public ExcelPayrollSetupRequest Setup { get; set; } = new();
    public List<HistoricalPeriodSalaryAssignment> Assignments { get; set; } = [];
    public string SalaryTargetMode { get; set; } = "PreserveEffectiveHistoricalTarget";
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public InitialSourceTargetReview? InitialSourceTargetReview { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public HistoricalLiteralSelfInputReview? LiteralSelfInputReview { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public HistoricalSourceIdentityReview? SourceIdentityReview { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public HistoricalPeriodProfessionalTaxReview? PeriodProfessionalTaxReview { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public HistoricalAmountDifferenceReview? AmountDifferenceReview { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public NamedFormulaInputReview? NamedInputReview { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public HistoricalPayableOnlySeedReview? PayableOnlySeedReview { get; set; }
}

public sealed class HistoricalPeriodProfessionalTaxReview
{
    public int Version { get; set; }
    public bool Reviewed { get; set; }
    public string SourceHash { get; set; } = "";
    public string Reason { get; set; } = "";
    public List<HistoricalPeriodProfessionalTaxRow> Rows { get; set; } = [];
}
public sealed record HistoricalPeriodProfessionalTaxRow(int EmployeeId,string RowId,decimal Amount);
public sealed record HistoricalProfessionalTaxEvidence(int EmployeeId,string RowId,decimal Amount,string SourceHash,
    string PtSourceHash,string NativeBasisHash,string PreviousControlHash,bool ProposedBasisWhatIf);

public sealed class HistoricalLiteralSelfInputReview
{
    public int Version { get; set; }
    public bool Reviewed { get; set; }
    public string SourceHash { get; set; } = "";
    public List<HistoricalLiteralSelfInputBinding> Bindings { get; set; } = [];
}
public sealed record HistoricalLiteralSelfInputBinding(string ComponentKey,string InputKey,int ColumnIndex,List<string> RowIds);
public sealed record HistoricalLiteralSelfInputEvidence(int EmployeeId,string RowId,int SourceRow,string ComponentKey,
    string InputKey,int ColumnIndex,decimal LiteralValue,string SourceHash);

public sealed class InitialSourceTargetReview
{
    public int Version { get; set; }
    public bool Reviewed { get; set; }
    public int? MonthlyCtcColumnIndex { get; set; }
    public string SourceHash { get; set; } = "";
    public string Meaning { get; set; } = "";
    public bool ActualJoiningDateNotEstablished { get; set; }
}
public sealed record HistoricalInitialSourceTargetEvidence(int EmployeeId,int MonthlyCtcColumnIndex,decimal SourceMonthlyBudget,
    decimal AnnualSalaryTarget,string SourceHash,string Meaning,bool ActualJoiningDateNotEstablished);

public sealed record HistoricalPeriodSalaryAssignment(string RowId, int EmployeeId, string TemplateId);
public sealed class HistoricalPeriodSalaryPreview
{
    public int ClientId { get; set; }
    public string BatchId { get; set; } = "";
    public string PayPeriod { get; set; } = "";
    public string EffectiveFrom { get; set; } = "";
    public string EffectiveTo { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string SourceHash { get; set; } = "";
    public string ReviewToken { get; set; } = "";
    public DateTime? ExpiresAtUtc { get; set; }
    public bool CanApply => Blockers.Count == 0 && Rows.Count > 0;
    public List<string> Blockers { get; set; } = [];
    public List<HistoricalPeriodSalaryPreviewRow> Rows { get; set; } = [];
}

public sealed class HistoricalPeriodSalaryPreviewRow
{
    public string RowId { get; set; } = "";
    public int SourceRow { get; set; }
    public int EmployeeId { get; set; }
    public string EmployeeCode { get; set; } = "";
    public string TemplateId { get; set; } = "";
    public decimal RetainedAnnualSalaryTarget { get; set; }
    public string SalaryTargetMeaning { get; set; } = "Existing effective historical annual target; no source Rate or earned pay is declared as CTC.";
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public decimal? InitialSourceAnnualSalaryTarget { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<HistoricalLiteralSelfInputEvidence>? ReviewedLiteralSelfInputs { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public HistoricalSourceIdentityEvidence? ReviewedSourceIdentity { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public HistoricalProfessionalTaxEvidence? ProfessionalTax { get; set; }
    public Dictionary<string, decimal> SourceMonthlyInputs { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public NamedFormulaInputContext? NamedInputs { get; set; }
    public decimal? Gross { get; set; }
    public decimal? TotalDeductions { get; set; }
    public decimal? EmployerCost { get; set; }
    public decimal? NetPay { get; set; }
    public List<ExcelPayrollSetupLine> Lines { get; set; } = [];
    public ExcelPayrollTransferAmountReview? AmountReview { get; set; }
    public List<ExcelPayrollTransferFormula> Formulas { get; set; } = [];
    public List<string> Blockers { get; set; } = [];
}

public sealed class HistoricalPeriodSalaryApplyRequest
{
    public string ReviewToken { get; set; } = "";
    public string RequestId { get; set; } = "";
    public bool Acknowledged { get; set; }
}

public sealed class HistoricalPeriodSalaryReceipt
{
    public int ClientId { get; set; }
    public string BatchId { get; set; } = "";
    public string PayPeriod { get; set; } = "";
    public string RequestId { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public DateTime AppliedAtUtc { get; set; }
    public bool Replayed { get; set; }
    public List<HistoricalPeriodSalarySavedRow> Rows { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<HistoricalInitialSourceTargetEvidence>? InitialSourceTargets { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<HistoricalLiteralSelfInputEvidence>? LiteralSelfInputs { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<HistoricalSourceIdentityEvidence>? SourceIdentities { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<HistoricalProfessionalTaxEvidence>? ProfessionalTax { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<HistoricalAmountDifferenceReceipt>? AmountDifferences { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<HistoricalNamedFormulaInputManifest>? NamedInputs { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<ReportedPeriodHistoricalAssignmentV2>? ReportedInputs { get; set; }
}
public sealed record HistoricalPeriodSalarySavedRow(int EmployeeId, string RowId, long HistoryId, string TemplateId);

internal sealed record HistoricalSalarySimulationAssignment(int ClientId, int EmployeeId, string PayPeriod,
    string TemplateId, decimal AnnualSalaryTarget, IReadOnlyDictionary<string, decimal> SalaryInputs,
    HistoricalConditionalProfessionalTax? ProfessionalTax = null,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    NamedFormulaInputContext? NamedInputs = null)
{
    // Constructed only by the reviewed repository planner. It is a clearly
    // hypothetical pre-insert attendance proposal, never a fabricated receipt.
    [System.Text.Json.Serialization.JsonIgnore]
    internal Payroll.API.Repositories.ExcelPayrollSetupRepository.HistoricalPayableOnlySeedPlan? ReportedAttendanceSeed { get; init; }
}
internal sealed record HistoricalConditionalProfessionalTax(string RowId,string SourceHash,string Reason,int ActorId,string ActorEmail,
    HistoricalPeriodSalaryRequest ReviewedRequest)
{
    internal HistoricalProfessionalTaxEvidence? Evidence { get; set; }
}
internal sealed record HistoricalSalarySimulationResult(PayrollSimulationResult Simulation, string InputFingerprint,
    HistoricalProfessionalTaxEvidence? ProfessionalTax=null);
