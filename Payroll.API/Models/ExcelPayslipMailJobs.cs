namespace Payroll.API.Models;

public sealed class ExcelPayslipMailJobSummary
{
    public string Id { get; set; } = "";
    public int ClientId { get; set; }
    public string ClientName { get; set; } = "";
    public string BatchId { get; set; } = "";
    public string Month { get; set; } = "";
    public string SourceFileName { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public string CreatedBy { get; set; } = "";
    public string Status { get; set; } = "Queued";
    public int Total { get; set; }
    public int Sent { get; set; }
    public int Queued { get; set; }
    public int Failed { get; set; }
    public int MissingEmail { get; set; }
    public int Skipped { get; set; }
    public int Unknown { get; set; }
    public int Completed { get; set; }
    public int PercentComplete { get; set; }
    public bool IsComplete { get; set; }
    public string Message { get; set; } = "";
    public DateTime? DismissedAtUtc { get; set; }
}

public sealed class ExcelPayslipMailJobSubmission
{
    public ExcelPayslipMailJobSummary Job { get; set; } = new();
    public IReadOnlyList<ExcelPayslipDeliveryItem> Items { get; set; } = [];
}

public sealed class ExcelPayslipMailJobs
{
    public IReadOnlyList<ExcelPayslipMailJobSummary> Items { get; set; } = [];
}
