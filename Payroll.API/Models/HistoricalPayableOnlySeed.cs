namespace Payroll.API.Models;

// Optional opt-in for a closed salary transaction. This is not actual presence,
// a normal source-import receipt, or permission to replace recorded attendance.
public sealed class HistoricalPayableOnlySeedReview
{
    public int Version { get; set; } = 1;
    public bool Reviewed { get; set; }
    public string QuantityBasis { get; set; } = "ReportedPeriodInputsV2";
    public string Reason { get; set; } = "";
}
