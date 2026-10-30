using System.Text.Json.Serialization;

namespace Payroll.API.Models;

// This is an independent Excel snapshot, never an employee or payroll-master record.
public sealed partial class ExcelPayslipCalculationSource
{
    public string SourceBatchId { get; set; } = "";
    public int? AttendanceColumnIndex { get; set; }
    public string? MonthDaysCell { get; set; }
    public string SheetName { get; set; } = "";
    public int HeaderRow { get; set; }
    public int ColumnCount { get; set; }
    public string DateSystem { get; set; } = "1900";
    public List<ExcelPayslipCalculationColumn> Columns { get; set; } = [];
    public List<ExcelPayslipCalculationSourceRow> Rows { get; set; } = [];
    public List<int> ExcludedSourceRows { get; set; } = [];
}

public sealed class ExcelPayslipCalculationColumn
{
    public int ColumnIndex { get; set; }
    public string SourceHeader { get; set; } = "";
    public string Label { get; set; } = "";
    public string Kind { get; set; } = "ignore";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ComponentId { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool BlankAsZero { get; set; }
}

public sealed partial class ExcelPayslipCalculationSourceRow
{
    public int SourceRow { get; set; }
    public List<ExcelPayslipCalculationCell?> Cells { get; set; } = [];
}

public sealed class ExcelPayslipCalculationCell
{
    public string DataType { get; set; } = "";
    public string Value { get; set; } = "";
    public string? FormulaText { get; set; }
    public string? CalculationError { get; set; }
    public string? Error { get; set; }
    public bool MissingCachedValue { get; set; }
}

public sealed class ExcelPayslipCalculateRequest
{
    public string Month { get; set; } = "";
    public int AttendanceColumnIndex { get; set; }
    public string? MonthDaysCell { get; set; }
    public decimal? MonthDays { get; set; }
    public List<ExcelPayslipAttendanceInput> Attendance { get; set; } = [];
    public Dictionary<string, decimal> Overrides { get; set; } = [];
}

public sealed class ExcelPayslipAttendanceInput
{
    public string RowId { get; set; } = "";
    public decimal Days { get; set; }
}

public sealed class ExcelPayslipCorrectionPreview
{
    public ExcelPayslipBatch Batch { get; set; } = new();
    public List<ExcelPayslipCellCorrection> Edits { get; set; } = [];
}

public sealed class ExcelPayslipCellCorrection
{
    public int SourceRow { get; set; }
    public int ColumnIndex { get; set; }
    public decimal Amount { get; set; }
}
