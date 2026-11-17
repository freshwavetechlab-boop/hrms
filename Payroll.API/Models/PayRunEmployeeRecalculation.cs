namespace Payroll.API.Models;

public sealed record PayRunEmployeeRecalculationTotals(decimal Gross, decimal Deductions, decimal Net,
    decimal? PresentDays, decimal PayableDays);
public sealed record PayRunEmployeeRecalculationChange(string Code, string Name, string Category, decimal Before, decimal After);
public sealed record RecalculatePayRunEmployeeRequest(string ExpectedFingerprint, bool Confirmed, string Reason);
public sealed class PayRunEmployeeRecalculationPreview
{
    public int PayRunId { get; set; }
    public int ClientId { get; set; }
    public string PayPeriod { get; set; } = "";
    public int EmployeeId { get; set; }
    public string EmployeeCode { get; set; } = "";
    public string EmployeeName { get; set; } = "";
    public string Status { get; set; } = "";
    public string ExpectedFingerprint { get; set; } = "";
    public bool CanApply { get; set; }
    public List<string> Blockers { get; set; } = [];
    public PayRunEmployeeRecalculationTotals? Before { get; set; }
    public PayRunEmployeeRecalculationTotals? After { get; set; }
    public List<PayRunEmployeeRecalculationChange> Changes { get; set; } = [];
}
