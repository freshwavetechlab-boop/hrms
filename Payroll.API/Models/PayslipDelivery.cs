namespace Payroll.API.Models;

public sealed class SendPayslipsRequest
{
    public List<int> EmployeeIds { get; set; } = [];
}

public sealed class PayslipDeliveryItem
{
    public int EmployeeId { get; set; }
    public string EmployeeCode { get; set; } = "";
    public string Status { get; set; } = "Excluded";
    public string Message { get; set; } = "";
}

public sealed class PayslipDeliveryResult
{
    public List<PayslipDeliveryItem> Items { get; set; } = [];
    public int QueuedCount => Items.Count(item => item.Status == "Queued");
    public int AlreadyQueuedCount => Items.Count(item => item.Status == "Already queued");
    public int ExcludedCount => Items.Count(item => item.Status == "Excluded");
}
