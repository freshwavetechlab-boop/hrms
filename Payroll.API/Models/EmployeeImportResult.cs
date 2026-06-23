namespace Payroll.API.Models;

public class EmployeeImportResult
{
    public int ImportedCount { get; set; }
    public int UpdatedCount { get; set; }
    public int SkippedCount { get; set; }
    public List<string> Errors { get; set; } = [];
}
