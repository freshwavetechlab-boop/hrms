namespace Payroll.API.Models;

// Configuration arithmetic only. This never certifies saved Employee payroll,
// actual presence, a statutory policy or a native payment/approval action.
public sealed class ExcelPayrollConfigurationDifferenceReview
{
    public int Version { get; set; }
    public bool Reviewed { get; set; }
    public string SourceHash { get; set; } = "";
    public decimal MaxAbsoluteDifference { get; set; }
    public string Reason { get; set; } = "";
}

public sealed record ExcelPayrollConfigurationDifferenceEvidence(int Version,int ClientId,int EmployeeId,
    string RowId,string TemplateId,string SourceHash,decimal MaximumAbsoluteDifference,string Reason,
    string TemplateInputFingerprint,string TemplateTraceHash,string FormulaEvidenceHash,
    IReadOnlyList<HistoricalAmountDifferenceTotal> Totals,IReadOnlyList<HistoricalSourceAggregateDifference> SourceAggregates)
{
    public bool EmployeePayrollParityVerified => false;
    public bool ActualPresenceCertified => false;
}
