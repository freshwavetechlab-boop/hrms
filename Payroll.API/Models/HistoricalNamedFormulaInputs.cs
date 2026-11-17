namespace Payroll.API.Models;

// Protected period operands, never salary components, money overrides, location
// allocations or a replacement for the separately reviewed annual salary target.
public sealed record HistoricalNamedFormulaInputManifest(int Version,int ClientId,int EmployeeId,string PayPeriod,
    long HistoryId,string TemplateId,string EffectiveFrom,string EffectiveTo,decimal AnnualSalaryTarget,string SalaryJsonHash,
    string BatchId,string RowId,int SourceRow,string SourceHash,string RawBatchHash,string RawSourceHash,string IdentityHash,
    string DeclarationHash,List<NamedFormulaInputDefinition> Definitions,Dictionary<string,decimal> Values,
    List<NamedFormulaInputCell> Cells,string RequestId,int ReviewerId,DateTime ReviewedAtUtc,string ManifestHash);
