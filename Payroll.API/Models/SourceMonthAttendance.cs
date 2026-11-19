namespace Payroll.API.Models;

public sealed record SourceMonthAttendanceEmployee(int EmployeeId, string SourceRowId);
public sealed class SourceMonthAttendanceRequest
{
    public int Version { get; set; } = 1;
    public int ClientId { get; set; }
    public string PayPeriod { get; set; } = "";
    public string SourceBatchId { get; set; } = "";
    public string SourceHash { get; set; } = "";
    public int DivisorColumn { get; set; }
    public int PayableColumn { get; set; }
    public int CycleStartDay { get; set; } = 1;
    public int CycleEndDay { get; set; } = 31;
    public bool ReviewedExtraPaidUnits { get; set; }
    public string Reason { get; set; } = "";
    public List<SourceMonthAttendanceEmployee> Rows { get; set; } = [];
}
public sealed record SourceMonthAttendancePreviewRow(int EmployeeId, string EmployeeCode, string SourceRowId,
    int Divisor, decimal PayableUnits, IReadOnlyList<string> Blockers);
public sealed class SourceMonthAttendancePreview
{
    public SourceMonthAttendanceRequest Request { get; set; } = new();
    public string PlanHash { get; set; } = "";
    public List<string> Blockers { get; set; } = [];
    public List<SourceMonthAttendancePreviewRow> Rows { get; set; } = [];
    public bool CanSave => Blockers.Count == 0 && Rows.Count == Request.Rows.Count && Rows.Count > 0 && Rows.All(row=>row.Blockers.Count==0);
}
public sealed record SaveSourceMonthAttendanceRequest(SourceMonthAttendanceRequest Request, string ExpectedPlanHash,
    string RequestId, bool Acknowledged);
public sealed record SourceMonthAttendanceResult(string RequestId, int SavedEmployees, bool Replayed);
