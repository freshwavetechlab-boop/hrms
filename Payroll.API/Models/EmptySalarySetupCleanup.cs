namespace Payroll.API.Models;

public sealed record EmptySalarySetupCleanupRequest(int ClientId, string PayPeriod, string ExpectedPlanHash,
    string RequestId, string Reason, bool Acknowledged);
public sealed record EmptySalarySetupCleanupPreview(int EmployeeId, int ClientId, string EmployeeCode,
    string PayPeriod, string PlanHash, string TemplateId, decimal AnnualCtc,
    IReadOnlyList<string> EmptyEffectiveDates, IReadOnlyList<string> Blockers)
{
    public bool CanApply => EmptyEffectiveDates.Count > 0 && Blockers.Count == 0;
}
public sealed record EmptySalarySetupCleanupResult(int EmployeeId, int ClientId, string PayPeriod,
    string RequestId, int RepairedMarkers, bool Replayed)
{
    public string PlanHash { get; init; } = "";
    public string Reason { get; init; } = "";
}
