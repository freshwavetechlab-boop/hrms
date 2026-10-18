namespace Payroll.API.Models;

public sealed class BulkEmployeeResignationRequest
{
    public int ClientId { get; set; }
    public List<int> EmployeeIds { get; set; } = [];
    public string LastWorkingDate { get; set; } = "";
    public Dictionary<int, string> LastWorkingDates { get; set; } = [];
    public string Reason { get; set; } = "";
}

public sealed record BulkEmployeeResignationResult(int Marked, int AlreadyMarked, string LastWorkingDate, IReadOnlyList<int> EmployeeIds);
public sealed class EmployeeResignationDefaultsRequest
{
    public int ClientId { get; set; }
    public List<int> EmployeeIds { get; set; } = [];
}
public sealed record EmployeeResignationDefault(int EmployeeId, string EmployeeCode, string Name, string? LastPayrollMonth, string? LastWorkingDate);
