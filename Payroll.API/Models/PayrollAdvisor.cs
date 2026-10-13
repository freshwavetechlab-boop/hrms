namespace Payroll.API.Models;

// Deliberately excludes employee records, identifiers, bank details and free-form documents.
public sealed record PayrollReviewComponent(string Id, string Code, string Category, string ComponentRole,
    string StatutoryType, string CalculationType, string Value, string Formula, string BaseComponent,
    bool ProRata, bool Taxable, bool Active, int Priority, string PayType);
public sealed record PayrollReviewLine(string ComponentId, string Value = "", string Formula = "",
    string CalculationType = "", string BaseComponent = "", string ProRataOverride = "");
public sealed record PayrollReviewSnapshot(string Mode, int ClientId, string FocusCode,
    decimal AnnualCtc, List<PayrollReviewComponent> Components, List<PayrollReviewLine> Lines);
public sealed record PayrollReviewRequest(PayrollReviewSnapshot Snapshot, bool Consent);
public sealed record PayrollReviewDecision(PayrollReviewSnapshot Snapshot, string FindingId, string Decision);
public sealed record PayrollReviewFinding(string Id, string Source, string Severity, string Code, string Field,
    string Title, string Explanation, string Example, string Suggestion, bool CanDisableProration = false);
public sealed record PayrollReviewAmount(string Code, string Category, decimal FullPeriod, decimal Earned);
public sealed record PayrollReviewScenario(string Label, int PayrollDays, decimal PayableDays,
    List<PayrollReviewAmount> Amounts, string? Error);
public sealed record PayrollReviewReport(string Status, string Coverage, List<PayrollReviewFinding> Findings,
    List<PayrollReviewScenario> Scenarios, string? Model = null, string? Provider = null, string? AiError = null);
public sealed class PayrollReviewJob
{
    public string Id { get; set; } = "";
    public int UserId { get; set; }
    public int ClientId { get; set; }
    public string SnapshotHash { get; set; } = "";
    public string SnapshotJson { get; set; } = "";
    public string Status { get; set; } = "Queued";
    public string? ResultJson { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
}
