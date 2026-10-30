namespace Payroll.API.Models;

public sealed partial class ExcelPayslipCalculationSource
{
    public int CaptureVersion { get; set; }
    public string DependencyReviewStatus { get; set; } = "Legacy";
    public List<ExcelPayslipWorkbookBinding> WorkbookFiles { get; set; } = [];
    public string WorkbookKey { get; set; } = "current";
    public bool CompleteSheetCapture { get; set; }
    public List<ExcelPayslipDependencySheet> DependencySheets { get; set; } = [];
    public List<ExcelPayslipExternalMapping> ExternalMappings { get; set; } = [];
    public List<ExcelPayslipLookupTrace> LookupTraces { get; set; } = [];
}
public sealed class ExcelPayslipDependencySheet
{
    public string WorkbookKey { get; set; } = "current";
    public string SheetName { get; set; } = "";
    public string SourceHash { get; set; } = "";
    public int ColumnCount { get; set; }
    public bool CompleteSheetCapture { get; set; }
    public List<ExcelPayslipCalculationSourceRow> Rows { get; set; } = [];
}
public sealed class ExcelPayslipExternalMapping
{
    public string FromWorkbookKey { get; set; } = "current";
    public string Reference { get; set; } = ""; // Excel slot/name e.g. [1]; never a file/UNC/network path.
    public string ToWorkbookKey { get; set; } = "";
    public bool Reviewed { get; set; }
}
public sealed record ExcelPayslipLookupTrace(string FormulaCell,string TableRange,string MatchMode,int? SelectedRow,int MatchingRows,string Status);

public sealed class ExcelPayslipWorkbookBinding
{
    public string WorkbookKey { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string OriginalId { get; set; } = "";
}