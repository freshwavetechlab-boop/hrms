namespace Payroll.API.Models;

// Additive review metadata in the existing setup request/result JSON receipts; no second import engine or table.
public sealed class ExcelPayrollTransferTemplateDecision
{
    public string VariantKey { get; set; } = "";
    public string ExistingTemplateId { get; set; } = "";
    public string ScopeAction { get; set; } = "";
    public string ScopeReason { get; set; } = "";
    public bool KeepExisting { get; set; }
    public bool CreateVariant { get; set; }
}
public sealed record ExcelPayrollTransferComponent(string Code, string Category, string Action, string MasterId);
public sealed record ExcelPayrollTransferFormula(string RowId, string Code, int SourceRow, int? SourceColumn, string SourceHash, string Status, string Reason);
public sealed record ExcelPayrollTransferDifference(string Component, string Field, string Existing, string ExcelDerived);
public sealed record ExcelPayrollTransferTemplateCandidate(string Id, string Name, bool Exact, IReadOnlyList<string> Differences, IReadOnlyList<ExcelPayrollTransferDifference> Details) { public List<ExcelPayrollRecordedTemplateScope> RecordedScopes { get; init; } = []; public bool ConfigurationExact { get; init; } public string ScopeStatus { get; init; } = "Legacy comparison"; public string ScopeReason { get; init; } = ""; }
public sealed record ExcelPayrollTransferExistingResult(string RowId,string TemplateId,bool Complete,decimal? Net,decimal SourceNet,decimal? Variance,string Coverage,IReadOnlyList<ExcelPayrollSetupLine> Lines);
public sealed record ExcelPayrollTransferKeptRow(string RowId,int EmployeeId,string TemplateId,string Reason);
public sealed class ExcelPayrollTransferTemplate
{
    public string VariantKey { get; set; } = "";
    public string Name { get; set; } = "";
    public string Action { get; set; } = "Review required";
    public string TemplateId { get; set; } = "";
    public List<string> RowIds { get; set; } = [];
    public ExcelPayrollRecordedTemplateScope? Applicability { get; set; }
    public List<ExcelPayrollTransferExistingResult> ExistingResults { get; set; } = [];
    public List<ExcelPayrollTransferKeptRow> KeptAssignments { get; set; } = [];
    public List<ExcelPayrollTransferTemplateCandidate> Candidates { get; set; } = [];
}
public sealed class ExcelPayrollTransferReview
{
    public string SourceMonth { get; set; } = "";
    public List<ExcelPayrollAttendanceProof> AttendanceProofs { get; set; } = [];
    public List<ExcelPayrollTransferAmountReview> AmountComparisons { get; set; } = [];
    public int Version { get; set; } = 1;
    public string SourceBatchId { get; set; } = "";
    public string SourceHash { get; set; } = "";
    public string EffectiveFrom { get; set; } = "";
    public string PreparationStatus { get; set; } = "Blocked";
    public string ParityStatus { get; set; } = "Not verified — run saved Employee simulation with reviewed source-month attendance after setup.";
    public decimal Tolerance { get; set; } = 0.000001m;
    public List<ExcelPayrollTransferComponent> Components { get; set; } = [];
    public List<ExcelPayrollTransferFormula> Formulas { get; set; } = [];
    public List<ExcelPayrollTransferTemplate> Templates { get; set; } = [];
}
public sealed record ExcelPayrollTransferRowReceipt(string RowId, int EmployeeId, int PayrollDays, decimal? PresentDays, decimal PayableDays,
    decimal SourceNet, IReadOnlyList<ExcelPayrollSetupLine> Lines);

public sealed record ExcelPayrollTransferAmountLine(string Code, string Category, decimal SourceRaw,
    decimal SourceDisplay, decimal CoreUnrounded, decimal Calculated, decimal RawDelta, decimal DisplayDelta);
public sealed class ExcelPayrollTransferAmountReview
{
    public string Version { get; set; } = "source-core-currency-v1";
    public string RowId { get; set; } = "";
    public int SourceRow { get; set; }
    public string Status { get; set; } = "Unverified";
    public string Reason { get; set; } = "";
    public string ProofHash { get; set; } = "";
    public bool Acknowledged { get; set; }
    public decimal SourceNetRaw { get; set; }
    public decimal SourceNetDisplay { get; set; }
    public decimal? CoreNet { get; set; }
    public decimal? RawNetDelta { get; set; }
    public decimal? DisplayNetDelta { get; set; }
    public List<ExcelPayrollTransferAmountLine> Lines { get; set; } = [];
}
