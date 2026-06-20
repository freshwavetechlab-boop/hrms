namespace Payroll.API.Models;

public class PayRun
{
    public int Id { get; set; }
    public int ClientId { get; set; }
    public string ClientName { get; set; } = string.Empty;
    public string PayPeriod { get; set; } = string.Empty;
    public DateTime PayDate { get; set; }
    public int TotalWorkingDays { get; set; }
    public string Status { get; set; } = "Draft";
    public decimal PayrollCost { get; set; }
    public decimal NetPay { get; set; }
    public int EmployeeCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<PayRunEmployee> Employees { get; set; } = [];
}

public class PayRunEmployee
{
    public int Id { get; set; }
    public int PayRunId { get; set; }
    public int EmployeeId { get; set; }
    public int ClientId { get; set; }
    public string ClientName { get; set; } = string.Empty;
    public string EmployeeCode { get; set; } = string.Empty;
    public string EmployeeName { get; set; } = string.Empty;
    public string Department { get; set; } = string.Empty;
    public int PresentDays { get; set; }
    public int PayableDays { get; set; }
    public decimal MonthlyGross { get; set; }
    public decimal GrossPay { get; set; }
    public decimal StatutoryDeductions { get; set; }
    public decimal OneTimeEarnings { get; set; }
    public decimal OneTimeDeductions { get; set; }
    public decimal NetPay { get; set; }
    public bool IsSkipped { get; set; }
    public string PaymentStatus { get; set; } = "Pending";
    public DateTime? PaymentDate { get; set; }
    public string DetailsJson { get; set; } = "[]";
    public decimal? PreviousNetPay { get; set; }
    public decimal? NetPayVariance { get; set; }
    public decimal? VariancePercent { get; set; }
    public string VarianceReason { get; set; } = string.Empty;
}

public class CreatePayRunRequest
{
    public int ClientId { get; set; }
    public string PayPeriod { get; set; } = string.Empty;
    public DateOnly PayDate { get; set; }
    public int TotalWorkingDays { get; set; }
    public List<int> ExcludedEmployeeIds { get; set; } = [];
}

public class UpdatePayRunEmployeeRequest
{
    public int PresentDays { get; set; }
    public decimal OneTimeEarnings { get; set; }
    public decimal OneTimeDeductions { get; set; }
    public bool IsSkipped { get; set; }
}

public class RecordPaymentRequest
{
    public List<int> EmployeeIds { get; set; } = [];
    public DateOnly PaymentDate { get; set; }
}
