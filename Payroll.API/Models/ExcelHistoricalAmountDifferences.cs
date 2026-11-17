namespace Payroll.API.Models;

// Explicit closed-period review metadata only. It never supplies a salary amount.
public sealed class HistoricalAmountDifferenceReview
{
    public int Version { get; set; }
    public bool Reviewed { get; set; }
    public string SourceHash { get; set; } = "";
    public decimal MaxAbsoluteDifference { get; set; }
    public string Reason { get; set; } = "";
}
public sealed record HistoricalAmountDifferenceTotal(string Kind,decimal SourceRaw,decimal SourceDisplay,
    decimal Calculated,decimal RawDelta,decimal DisplayDelta);
public sealed record HistoricalSourceAggregateDifference(string Kind,decimal Stored,decimal ComponentAggregate,
    decimal RawDelta,decimal DisplayDelta);
public sealed record HistoricalAmountDifferenceEvidence(int Version,int ClientId,int EmployeeId,string RowId,
    string TemplateId,string SourceHash,decimal MaximumAbsoluteDifference,string Reason,string NativeInputFingerprint,
    string NativeTraceHash,string FormulaEvidenceHash,IReadOnlyList<HistoricalAmountDifferenceTotal> Totals,
    IReadOnlyList<HistoricalSourceAggregateDifference> SourceAggregates);
public sealed record HistoricalAmountDifferenceReceipt(int EmployeeId,string RowId,string TemplateId,
    ExcelPayrollTransferAmountReview Review);
