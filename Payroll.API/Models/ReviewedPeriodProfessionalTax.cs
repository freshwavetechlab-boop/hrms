namespace Payroll.API.Models;

public sealed class ReviewedProfessionalTaxRow
{
    public int EmployeeId { get; set; }
    public string SourceRowId { get; set; } = "";
    public string Mode { get; set; } = "";
    public decimal? Amount { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, int>? SourceZeroStatutoryColumns { get; set; }
}
public sealed class ReviewedProfessionalTaxRequest
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public int SourcePeriodReviewVersion { get; set; }
    public int ClientId { get; set; }
    public string PayPeriod { get; set; } = "";
    public string SourceBatchId { get; set; } = "";
    public string SourceHash { get; set; } = "";
    public string Reason { get; set; } = "";
    public List<ReviewedProfessionalTaxRow> Rows { get; set; } = [];
}
public sealed class ReviewedProfessionalTaxControl
{
    public int Version { get; set; } = 1;
    public string Kind { get; set; } = "ReviewedPeriodProfessionalTax";
    public int ClientId { get; set; }
    public int EmployeeId { get; set; }
    public string PayPeriod { get; set; } = "";
    public string Mode { get; set; } = "";
    public decimal? Amount { get; set; }
    public string SourceBatchId { get; set; } = "";
    public string SourceRowId { get; set; } = "";
    public string SourceHash { get; set; } = "";
    public string NativeBasisHash { get; set; } = "";
    public string Reason { get; set; } = "";
    public int ReviewedById { get; set; }
    public string ReviewedBy { get; set; } = "";
    public DateTime ReviewedAtUtc { get; set; }
    public string RequestId { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? HistoricalReceiptId { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, int>? SourceZeroStatutoryColumns { get; set; }
}
public sealed record ReviewedProfessionalTaxPreviewRow(int EmployeeId, string EmployeeCode, string EmployeeName,
    string SourceRowId, string CurrentMode, decimal? CurrentAmount, string Mode, decimal? Amount,
    string NativeBasisHash, IReadOnlyList<string> Blockers);
public sealed class ReviewedProfessionalTaxPreview
{
    public string PlanHash { get; set; } = "";
    public string SourceHash { get; set; } = "";
    public ReviewedProfessionalTaxRequest Request { get; set; } = new();
    public List<ReviewedProfessionalTaxPreviewRow> Rows { get; set; } = [];
    public List<string> Blockers { get; set; } = [];
    public bool CanSave => Blockers.Count == 0 && Rows.Count > 0 && Rows.All(row => row.Blockers.Count == 0);
}
public sealed record SaveReviewedProfessionalTaxRequest(ReviewedProfessionalTaxRequest Request,
    string ExpectedPlanHash, string RequestId, bool Acknowledged);
public sealed record ReviewedProfessionalTaxSaveResult(string RequestId, int SavedEmployees,
    IReadOnlyList<ReviewedProfessionalTaxControl> Controls, bool Replayed);
