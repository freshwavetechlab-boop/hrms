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
    public Dictionary<string, decimal> SalaryComponents { get; set; } = [];
    public EmployeePersonalDetails PersonalDetails { get; set; } = new();
    public EmployeePaymentDetails PaymentDetails { get; set; } = new();
    public bool IsActive { get; set; } = true;
}

public class EmployeePersonalDetails
{
    public string DateOfBirth { get; set; } = string.Empty;
    public string Mobile { get; set; } = string.Empty;
    public string PanNumber { get; set; } = string.Empty;
    public string AadhaarNumber { get; set; } = string.Empty;
    public string UanNumber { get; set; } = string.Empty;
    public string EsicNumber { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string SourceLocation { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string District { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string RawDesignation { get; set; } = string.Empty;
    public string OriginalEmployeeCode { get; set; } = string.Empty;
    public string DuplicateResolution { get; set; } = string.Empty;
    public int ExcelRow { get; set; }
    public decimal EsicEmployee { get; set; }
    public decimal PtLwfWorkmenComp { get; set; }
    public decimal Tds { get; set; }
    public decimal Recovery { get; set; }
}

public class EmployeePaymentDetails
{
    public string BankName { get; set; } = string.Empty;
    public string BankAccountNo { get; set; } = string.Empty;
    public string IfscCode { get; set; } = string.Empty;
    public string PaymentMode { get; set; } = string.Empty;
}
