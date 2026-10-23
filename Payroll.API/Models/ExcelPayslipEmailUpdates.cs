namespace Payroll.API.Models;

public sealed class ExcelPayslipEmailUpdates
{
    public string Month { get; set; } = "";
    public string SourceFileName { get; set; } = "";
    public List<ExcelPayslipEmailUpdate> Updates { get; set; } = [];
}

public sealed class ExcelPayslipEmailUpdate
{
    public string RowId { get; set; } = "";
    public string EmployeeCode { get; set; } = "";
    public string EmployeeName { get; set; } = "";
    public string Location { get; set; } = "";
    public string PreviousEmail { get; set; } = "";
    public string Email { get; set; } = "";
}
