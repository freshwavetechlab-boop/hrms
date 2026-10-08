namespace Payroll.API.Models;

public sealed class ExcelPayslipVarianceResult
{
    public string CurrentBatchId { get; set; } = "";
    public string PreviousBatchId { get; set; } = "";
    public string CurrentMonth { get; set; } = "";
    public string PreviousMonth { get; set; } = "";
    public int MatchedEmployees { get; set; }
    public int NewEmployees { get; set; }
    public int MissingEmployees { get; set; }
    public int ReviewEmployees { get; set; }
    public List<ExcelPayslipVarianceRow> Rows { get; set; } = [];
}

public sealed class ExcelPayslipVarianceRow
{
    public string Id { get; set; } = "";
    public string EmployeeName { get; set; } = "";
    public string EmployeeCode { get; set; } = "";
    public string Location { get; set; } = "";
    public string Status { get; set; } = "";
    public string Category { get; set; } = "";
    public string Component { get; set; } = "";
    public decimal? PreviousAmount { get; set; }
    public decimal? CurrentAmount { get; set; }
    public decimal? Difference { get; set; }
    public string? PreviousRowId { get; set; }
    public string? CurrentRowId { get; set; }
    public string Message { get; set; } = "";
}
