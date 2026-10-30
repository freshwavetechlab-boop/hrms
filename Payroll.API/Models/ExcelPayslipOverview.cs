namespace Payroll.API.Models;

public sealed record ExcelPayslipOverviewScope(int ClientId, string BatchId);
public sealed class ExcelPayslipOverviewRequest
{
    public List<ExcelPayslipOverviewScope> Scopes { get; set; } = [];
    public string Search { get; set; } = "";
    public string Readiness { get; set; } = "All";
    public string MasterStatus { get; set; } = "All";
    public bool OverlapOnly { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;
}
public sealed class ExcelPayslipOverviewBatch
{
    public int ClientId { get; set; }
    public string ClientName { get; set; } = "";
    public string BatchId { get; set; } = "";
    public string Month { get; set; } = "";
    public string FileName { get; set; } = "";
    public string SheetName { get; set; } = "";
    public int RowCount { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public bool HasCalculationSource { get; set; }
}
public sealed class ExcelPayslipOverviewRow
{
    public string Key { get; set; } = "";
    public int ClientId { get; set; }
    public string ClientName { get; set; } = "";
    public string Month { get; set; } = "";
    public string BatchId { get; set; } = "";
    public string FileName { get; set; } = "";
    public string SheetName { get; set; } = "";
    public int SourceRow { get; set; }
    public string EmployeeCode { get; set; } = "";
    public string EmployeeName { get; set; } = "";
    public int? MasterEmployeeId { get; set; }
    public string MasterStatus { get; set; } = "Unmatched";
    public string MasterReason { get; set; } = "";
    public string Readiness { get; set; } = "Review";
    public List<string> Reasons { get; set; } = [];
    public bool ScopeOverlap { get; set; }
    public bool HasCalculationSource { get; set; }
    public string SetupDecision { get; set; } = "Not previewed — review Master Setup";
}
public sealed class ExcelPayslipOverviewTotals
{
    public decimal SourceGrossEarnings { get; set; }
    public decimal SourceDeductions { get; set; }
    public decimal SourceNetPay { get; set; }
    public string MonetaryRoundingBasis { get; set; } = "Each filtered saved payslip total uses its declared total (or component sum when absent), normalized to 15 significant digits and rounded to 2 decimals away from zero before summing. These are source amounts, not payment or bank-disbursement evidence.";
    public int SourceRows { get; set; }
    public int ReadyRows { get; set; }
    public int ReviewRows { get; set; }
    public int BlockedRows { get; set; }
    public int UniqueScopedCodeGroups { get; set; }
    public int UncountedEmployeeRows { get; set; }
    public int MatchedMasterRecords { get; set; }
    public int MatchedRows { get; set; }
    public int UnmatchedRows { get; set; }
    public int ConflictRows { get; set; }
    public int MissingCodeRows { get; set; }
    public int OverlapRows { get; set; }
}
public sealed class ExcelPayslipOverviewResult
{
    public string ScopeLabel { get; set; } = "Explicit selected batches — not a persisted active set";
    public string Snapshot { get; set; } = "";
    public DateTime ObservedAtUtc { get; set; } = DateTime.UtcNow;
    public int ScopeBatchCount { get; set; }
    public int ScopeTotalRows { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public ExcelPayslipOverviewTotals Totals { get; set; } = new();
    public List<ExcelPayslipOverviewRow> Rows { get; set; } = [];
}
