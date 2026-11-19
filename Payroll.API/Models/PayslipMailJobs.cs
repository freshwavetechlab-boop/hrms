namespace Payroll.API.Models;

public sealed class PayslipMailJobRequest : SendPayslipsRequest
{
    public string RequestId { get; set; } = "";
    public bool Resend { get; set; }
    public bool AcknowledgeResend { get; set; }
}

public sealed class PayslipEmailStatus
{
    public int EmployeeId { get; set; }
    public string EmployeeCode { get; set; } = "";
    public string Email { get; set; } = "";
    public string LastRecipient { get; set; } = "";
    public string Status { get; set; } = "Not sent";
    public string SentCopyStatus { get; set; } = "Unknown";
    public DateTime? SentAtUtc { get; set; }
    public bool CanSend { get; set; }
    public bool CanResend { get; set; }
    public string Message { get; set; } = "";
}

// Same progress contract consumed by the existing BulkMailProgress component.
public sealed class PayslipMailJob
{
    public string Id { get; set; } = "";
    public int ClientId { get; set; }
    public int PayRunId { get; set; }
    public string Month { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public bool Resend { get; set; }
    public string Status { get; set; } = "Queued";
    public string Message { get; set; } = "";
    public int Total { get; set; }
    public int Completed { get; set; }
    public int Queued { get; set; }
    public int Failed { get; set; }
    public int MissingEmail { get; set; }
    public int Skipped { get; set; }
    public int Unknown { get; set; }
    public int TotalEmails { get; set; }
    public int AcceptedEmails { get; set; }
    public int SentCopySavedEmails { get; set; }
    public int SentCopyUnverifiedEmails { get; set; }
    public int DeliveredEmails { get; set; }
    public int PercentComplete { get; set; }
    public bool IsComplete { get; set; }
}

public sealed class PayslipMailOverview
{
    public int ClientId { get; set; }
    public int PayRunId { get; set; }
    public IReadOnlyList<PayslipEmailStatus> Items { get; set; } = [];
    public IReadOnlyList<PayslipMailJob> Jobs { get; set; } = [];
}

public sealed class PayslipMailJobSubmission
{
    public PayslipMailJob Job { get; set; } = new();
    public IReadOnlyList<PayslipDeliveryItem> Items { get; set; } = [];
}
