namespace Payroll.API.Models;

public class Organization
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string LegalName { get; set; } = string.Empty;
    public string BusinessType { get; set; } = string.Empty;
    public string BusinessLocation { get; set; } = "India";
    public string Industry { get; set; } = string.Empty;
    public bool HasRunPayrollThisYear { get; set; }
    public bool SetupCompleted { get; set; }
    public string LogoDataUrl { get; set; } = string.Empty;
    public string Pan { get; set; } = string.Empty;
    public string Gstin { get; set; } = string.Empty;
    public string FiscalYearStart { get; set; } = string.Empty;
    public string AddressLine1 { get; set; } = string.Empty;
    public string AddressLine2 { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string PostalCode { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
    public string BankName { get; set; } = string.Empty;
    public string AccountNumber { get; set; } = string.Empty;
    public string IfscCode { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
