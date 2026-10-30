namespace Payroll.API.Models;

public sealed record ExcelPayslipConsolidationRequest(string CanonicalBatchId, List<string> SourceBatchIds);
public sealed record ExcelPayslipConsolidationApplyRequest(string RequestId, string Token, string CanonicalBatchId, List<string> SourceBatchIds, bool Acknowledged);
public sealed record ExcelPayslipConsolidationUndoRequest(string RequestId, string Token);
public sealed class ExcelPayslipConsolidationPreview
{
    public string CanonicalBatchId { get; set; } = "";
    public List<string> SourceBatchIds { get; set; } = [];
    public string Token { get; set; } = "";
    public string CanonicalSnapshotHash { get; set; } = "";
    public bool CanConsolidate => Blockers.Count == 0;
    public List<string> Blockers { get; set; } = [];
    public int CanonicalRows { get; set; }
    public int MatchedRows { get; set; }
    public List<ExcelPayslipConsolidationSource> Sources { get; set; } = [];
    public List<ExcelPayslipConsolidationDifference> Differences { get; set; } = [];
    public string Evidence { get; set; } = "";
}
public sealed class ExcelPayslipConsolidationSource
{
    public string BatchId { get; set; } = "";
    public int Rows { get; set; }
    public int MatchedRows { get; set; }
    public bool FinancialMatch { get; set; }
    public bool IdentityMatch { get; set; }
    public bool FormulaSourcePresent { get; set; }
    public bool FormulaSourceUnchanged { get; set; }
    public string BatchHash { get; set; } = "";
    public string SourceHash { get; set; } = "";
}
public sealed record ExcelPayslipConsolidationDifference(string SourceBatchId, int SourceRow, string Field, string Reason);
public sealed class ExcelPayslipConsolidationStatus
{
    public string GroupId { get; set; } = "";
    public string CanonicalBatchId { get; set; } = "";
    public List<string> SourceBatchIds { get; set; } = [];
    public bool Active { get; set; }
    public bool CanUndo { get; set; }
    public string Token { get; set; } = "";
    public int CanonicalRows { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public List<ExcelPayslipHistoricalDelivery> HistoricalDelivery { get; set; } = [];
    public List<ExcelPayslipConsolidationHistory> History { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}
public sealed record ExcelPayslipHistoricalDelivery(string RowId, string SourceRowId, string SourceBatchId,
    bool FromConsolidatedVersion, string Status, string Mode, DateTime? QueuedAtUtc, DateTime? SentAtUtc, string Message);
public sealed record ExcelPayslipConsolidationHistory(string Action, string RequestId, DateTime AtUtc);
