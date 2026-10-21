namespace Payroll.API.Models;

public sealed class ExcelPayslipDeliveryStatuses
{
    public IReadOnlyList<ExcelPayslipRowDeliveryStatus> Items { get; set; } = [];
}

public sealed class ExcelPayslipRowDeliveryStatus
{
    public string RowId { get; set; } = "";
    public string Status { get; set; } = "Pending";
    public string Email { get; set; } = "";
    public DateTime? QueuedAtUtc { get; set; }
    public DateTime? SentAtUtc { get; set; }
    public string Message { get; set; } = "";
    public bool CanSend { get; set; } = true;
    public string SentCopyStatus { get; set; } = "Unknown";
    public List<ExcelPayslipRecipientDelivery> RecipientDeliveries { get; set; } = [];
}

public sealed class ExcelPayslipRecipientDelivery
{
    public string Mode { get; set; } = "Individual";
    public string Email { get; set; } = "";
    public string Status { get; set; } = "Unknown";
    public string SentCopyStatus { get; set; } = "Unknown";
    public DateTime? QueuedAtUtc { get; set; }
    public DateTime? SentAtUtc { get; set; }
    public string Message { get; set; } = "";
}
