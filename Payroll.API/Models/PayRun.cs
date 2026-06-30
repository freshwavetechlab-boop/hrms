namespace Payroll.API.Models;

public class PayRun
{
    public int Id { get; set; }
    public int ClientId { get; set; }
    public string ClientName { get; set; } = string.Empty;
    public string PayPeriod { get; set; } = string.Empty;
    public string RunCode { get; set; } = string.Empty;
    public string RunType { get; set; } = "Regular";
    public string RunName { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public DateTime PayDate { get; set; }
    public int TotalWorkingDays { get; set; }
    public string Status { get; set; } = "Draft";
    public string RequestJson { get; set; } = "{}";
    public DateTime? ProcessingStartedAt { get; set; }
    public DateTime? ProcessingCompletedAt { get; set; }
    public string ProcessingError { get; set; } = string.Empty;
    public decimal PayrollCost { get; set; }
    public decimal NetPay { get; set; }
    public int EmployeeCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<PayRunEmployee> Employees { get; set; } = [];
}

public class PayRunDiagnostics
{
    public int PayRunId { get; set; }
    public List<PayRunStepLog> StepLogs { get; set; } = [];
    public List<PayrollValidationIssue> ValidationIssues { get; set; } = [];
    public List<PayrollValidationIssue> Exceptions { get; set; } = [];
    public List<PayrollCalculationTrace> CalculationTraces { get; set; } = [];
    public List<PayrollReconciliationResult> ReconciliationResults { get; set; } = [];
}

public class PayRunStepLog
{
    public long Id { get; set; }
    public int PayRunId { get; set; }
    public int? EmployeeId { get; set; }
    public int StepNumber { get; set; }
    public string StepName { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public int DurationMs { get; set; }
    public string InputJson { get; set; } = "{}";
    public string RuleJson { get; set; } = "{}";
    public string FormulaJson { get; set; } = "{}";
    public string OldValueJson { get; set; } = "{}";
    public string NewValueJson { get; set; } = "{}";
    public string OutputJson { get; set; } = "{}";
    public string Status { get; set; } = "Success";
    public string Warning { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public string PerformedBy { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0";
    public DateTime CreatedAt { get; set; }
}

public class PayrollValidationIssue
{
    public long Id { get; set; }
    public int PayRunId { get; set; }
    public int? EmployeeId { get; set; }
    public string EmployeeCode { get; set; } = string.Empty;
    public string Scope { get; set; } = "Employee";
    public string IssueType { get; set; } = "Validation";
    public string Severity { get; set; } = "Warning";
    public string StepName { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string DataJson { get; set; } = "{}";
    public bool IsBlocking { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class PayrollCalculationTrace
{
    public long Id { get; set; }
    public int PayRunId { get; set; }
    public int EmployeeId { get; set; }
    public string EmployeeCode { get; set; } = string.Empty;
    public string ComponentCode { get; set; } = string.Empty;
    public string ComponentName { get; set; } = string.Empty;
    public string ParentComponentCode { get; set; } = string.Empty;
    public int TraceOrder { get; set; }
    public string RuleUsed { get; set; } = string.Empty;
    public string FormulaUsed { get; set; } = string.Empty;
    public decimal BaseAmount { get; set; }
    public decimal Factor { get; set; }
    public decimal CalculatedAmount { get; set; }
    public string InputJson { get; set; } = "{}";
    public string OutputJson { get; set; } = "{}";
    public DateTime CreatedAt { get; set; }
}

public class PayrollReconciliationResult
{
    public long Id { get; set; }
    public int PayRunId { get; set; }
    public string CheckName { get; set; } = string.Empty;
    public decimal ExpectedAmount { get; set; }
    public decimal ActualAmount { get; set; }
    public decimal DifferenceAmount { get; set; }
    public string Status { get; set; } = "Passed";
    public string DetailsJson { get; set; } = "{}";
    public DateTime CreatedAt { get; set; }
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
    public decimal PresentDays { get; set; }
    public decimal PayableDays { get; set; }
    public decimal MonthlyGross { get; set; }
    public decimal GrossPay { get; set; }
    public decimal StatutoryDeductions { get; set; }
    public decimal OneTimeEarnings { get; set; }
    public decimal OneTimeDeductions { get; set; }
    public decimal ManualTds { get; set; }
    public decimal NetPay { get; set; }
    public bool IsSkipped { get; set; }
    public string PaymentStatus { get; set; } = "Pending";
    public DateTime? PaymentDate { get; set; }
    public string DetailsJson { get; set; } = "[]";
    public List<PayRunLeaveBreakdown> LeaveBreakdown { get; set; } = [];
    public decimal? PreviousNetPay { get; set; }
    public decimal? PreviousSecondNetPay { get; set; }
    public decimal? PreviousTwoMonthAverageNetPay { get; set; }
    public decimal? NetPayVariance { get; set; }
    public decimal? TwoMonthAverageVariance { get; set; }
    public decimal? VariancePercent { get; set; }
    public decimal? TwoMonthAverageVariancePercent { get; set; }
    public string VarianceReason { get; set; } = string.Empty;
}

public class PayRunLeaveBreakdown
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public decimal Days { get; set; }
    public decimal PayableDays { get; set; }
}

public class CreatePayRunRequest
{
    public int ClientId { get; set; }
    public string PayPeriod { get; set; } = string.Empty;
    public DateOnly PayDate { get; set; }
    public int TotalWorkingDays { get; set; }
    public string RunType { get; set; } = "Regular";
    public string RunName { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public List<int> ExcludedEmployeeIds { get; set; } = [];
    public List<int> IncludedEmployeeIds { get; set; } = [];
    public List<int> AdjustmentIds { get; set; } = [];
}

public class UpdatePayRunEmployeeRequest
{
    public decimal PresentDays { get; set; }
    public decimal OneTimeEarnings { get; set; }
    public decimal OneTimeDeductions { get; set; }
    public decimal ManualTds { get; set; }
    public bool IsSkipped { get; set; }
}

public class RecordPaymentRequest
{
    public List<int> EmployeeIds { get; set; } = [];
    public DateOnly PaymentDate { get; set; }
}

public class PayrollAdjustment
{
    public int Id { get; set; }
    public int ClientId { get; set; }
    public int EmployeeId { get; set; }
    public string EmployeeName { get; set; } = string.Empty;
    public string EmployeeCode { get; set; } = string.Empty;
    public int ComponentId { get; set; }
    public string ComponentCode { get; set; } = string.Empty;
    public string ComponentName { get; set; } = string.Empty;
    public string AdjustmentType { get; set; } = "Earning";
    public decimal Amount { get; set; }
    public string PayPeriod { get; set; } = string.Empty;
    public string PayRunType { get; set; } = "Regular";
    public string ReasonCode { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public bool Taxable { get; set; }
    public string Status { get; set; } = "Approved";
    public int? PayRunId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
