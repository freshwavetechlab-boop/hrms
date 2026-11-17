using System.Text.Json.Serialization;
using Payroll.API.Services;

namespace Payroll.API.Models;

public sealed record NamedFormulaInputDefinition(string Key, string Label, string Unit, int Precision,
    decimal Minimum, decimal Maximum, bool Required = true, string Purpose = "FormulaOperandOnly");
public sealed record NamedFormulaInputBinding(string Key, int ColumnIndex,int HeaderRow=0,string SourceHeader="");
public sealed class NamedFormulaInputReview
{
    public string TemplateId { get; set; } = "";
    public string DeclarationHash { get; set; } = "";
    public List<NamedFormulaInputBinding> Bindings { get; set; } = [];
}
public sealed class NamedFormulaInputContext
{
    public List<NamedFormulaInputDefinition> Definitions { get; set; } = [];
    public Dictionary<string, decimal> Values { get; set; } = [];
    public string DeclarationHash { get; set; } = "";
    public string ManifestHash { get; set; } = "";
    // Server-only proof. JSON supplied to simulation cannot create a trusted v2
    // context by adding a version flag or by changing the public definitions.
    [JsonIgnore] internal ReportedPeriodCalculationProof? ReportedPeriodProof { get; set; }
}
public sealed record NamedFormulaInputCell(string Key, int SourceRow, int ColumnIndex, string Header,
    string Kind, string Value, string CellHash,int HeaderRow=0,string HumanHeader="");
public sealed record NamedFormulaInputProfile(int Version, int ClientId, string TemplateId, string RulesHash,
    List<NamedFormulaInputDefinition> Definitions, string DeclarationHash);
public sealed record NamedFormulaInputManifest(int Version,int ClientId,int EmployeeId,string Month,
    string AssignmentId,string TemplateId,int WorkLocationId,string SourcePartKey,string BatchId,string RowId,int SourceRow,
    string SourceHash,string RawBatchHash,string RawSourceHash,string IdentityHash,string DeclarationHash,
    List<NamedFormulaInputDefinition> Definitions,Dictionary<string,decimal> Values,List<NamedFormulaInputCell> Cells,
    string RequestId,int ReviewerId,DateTime ReviewedAtUtc,string ManifestHash)
{
    public List<string> CompleteSourcePartKeys {get;init;}=[];
    public string? ReferenceAssignmentId {get;init;}
    public string NativeRulesHash {get;init;}="";
    public string ComponentScopesHash {get;init;}="";
}
public sealed class NamedFormulaTemplateRequest
{
    public int ClientId { get; set; }
    public string ExistingTemplateId { get; set; } = "";
    public string Name { get; set; } = "";
    public string SourceRowId { get; set; } = "";
    public string SourceHash { get; set; } = "";
    public ExcelPayrollSetupRequest SourceReview { get; set; } = new();
    public List<NamedFormulaInputDefinition> Definitions { get; set; } = [];
    public List<NamedFormulaInputBinding> Bindings { get; set; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HistoricalAmountDifferenceReview? AmountDifferenceReview { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool HistoricalSalaryUse { get; set; }
}
public sealed record NamedFormulaTemplatePreview(string PlanHash,bool CanSave,List<string> Blockers,
    string SourceHash,int FinancialComponents,List<ExcelPayrollSetupLine> Lines)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public NamedFormulaTemplateAmountDifferenceEvidence? AmountDifferenceEvidence { get; init; }
}
// Configuration proof only. Actual historical salary assignment still requires
// its independent native Employee, policy, attendance and source ownership gates.
public sealed record NamedFormulaTemplateAmountDifferenceEvidence(int Version,int ClientId,int EmployeeId,
    string ExistingTemplateId,string BatchId,string RowId,int SourceRow,HistoricalAmountDifferenceReview Review,
    IReadOnlyList<ExcelPayrollTransferAmountLine> Lines,IReadOnlyList<HistoricalAmountDifferenceTotal> Totals,
    IReadOnlyList<HistoricalSourceAggregateDifference> SourceAggregates,string FormulaEvidenceHash,
    string NamedContextHash,string CalculationHash,string ProofHash);
public sealed record NamedFormulaTemplateSaveRequest(NamedFormulaTemplateRequest Request,string PlanHash,string RequestId,bool Acknowledged);
public sealed record NamedFormulaTemplateResult(string TemplateId,string DeclarationHash,string RequestId,bool Replayed);
