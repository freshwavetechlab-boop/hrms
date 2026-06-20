namespace Payroll.API.Models;

public class Employee
{
    public int Id { get; set; }
    public int ClientId { get; set; }
    public string EmployeeCode { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Gender { get; set; } = string.Empty;
    public string DateOfJoining { get; set; } = string.Empty;
    public string WorkEmail { get; set; } = string.Empty;
    public string Department { get; set; } = string.Empty;
    public string Designation { get; set; } = string.Empty;
    public int WorkLocationId { get; set; }
    public int ReportingManagerId { get; set; }
    public bool PortalAccess { get; set; }
    public string SalaryStructureId { get; set; } = string.Empty;
    public decimal AnnualCtc { get; set; }
    public string SalaryJson { get; set; } = "{}";
    public string PersonalJson { get; set; } = "{}";
    public string PaymentJson { get; set; } = "{}";
    public bool IsActive { get; set; } = true;
}
