namespace Payroll.API.Models;

// Structural evidence only. No employee rows, cell values, workbook paths or raw formulas.
public sealed class ExcelPayslipMappingRequest
{
    public string SalaryTemplateId { get; set; } = "";
    public List<ExcelPayslipMappingColumn> Columns { get; set; } = [];
}

public sealed class ExcelPayslipMappingColumn
{
    public int ColumnIndex { get; set; }
    public string SourceHeader { get; set; } = "";
    public string Label { get; set; } = "";
    public string Kind { get; set; } = "ignore";
    public string? ComponentId { get; set; }
    public int[] FormulaDependencies { get; set; } = [];
    public string[] FormulaFunctions { get; set; } = [];
    public List<ExcelPayslipMappingDependency> CrossSheetDependencies { get; set; } = [];
    public bool DependencyEvidenceIncomplete { get; set; }
    public bool HasFormula { get; set; }
    public bool HasNumbers { get; set; }
    public bool HasText { get; set; }
    public bool HasBlanks { get; set; }
}

// Sheet ordinals and column indexes only: no worksheet names, paths, cell values or formulas.
public sealed class ExcelPayslipMappingDependency
{
    public int SheetIndex { get; set; } = -1;
    public string SourceType { get; set; } = "unknown";
    public string Status { get; set; } = "missing";
    public int[] ColumnIndexes { get; set; } = [];
}

public sealed record ExcelPayslipMappingReference(string Source, string Title);
public sealed record ExcelPayslipMappingResult(
    IReadOnlyList<ExcelPayslipMappingColumn> Columns, string Method, string Model,
    string KnowledgeVersion, IReadOnlyList<ExcelPayslipMappingReference> References,
    IReadOnlyList<int> ReviewColumnIndexes, string Message);
