namespace Payroll.API.Models;

public class ReportFilter
{
    public int ClientId { get; set; }
    public string? Department { get; set; }
    public int? WorkLocationId { get; set; }
    public string? FromDate { get; set; }
    public string? ToDate { get; set; }
    public string? Month { get; set; }
}

public class ReportResult
{
    public string Title { get; set; } = string.Empty;
    public List<string> Columns { get; set; } = [];
    public List<Dictionary<string, object?>> Rows { get; set; } = [];
}
