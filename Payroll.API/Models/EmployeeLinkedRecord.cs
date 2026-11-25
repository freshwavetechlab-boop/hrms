namespace Payroll.API.Models;

public sealed record EmployeeLinkedLogin(int Id, string DisplayName, string Email, bool IsActive);
public sealed record EmployeeLinkedRecord(string Key, string Label, int Count, string Route);
