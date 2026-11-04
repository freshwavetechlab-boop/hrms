using System.Text.Json.Serialization;

namespace Payroll.API.Models;

public sealed record PayrollLocationPayslipDocument(int EmployeeId, string EmployeeCode, string EmployeeName,
    string PartKey, int WorkLocationId, string WorkLocationName, decimal GrossPay, decimal TotalDeductions,
    decimal NetPay, decimal? PresentDays, decimal PayableDays, string FileName, string Html)
{
    [JsonIgnore] public ExcelPayslipRow PrintSnapshot { get; init; } = new();
}
