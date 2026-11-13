namespace Payroll.API.Models;

public sealed class ExcelPayslipUpdateCatalog
{
    public int ClientId { get; set; }
    public string BatchId { get; set; } = "";
    public string Month { get; set; } = "";
    public List<ExcelPayslipUpdateField> Fields { get; set; } = [];
    public List<ExcelPayslipUpdateCatalogRow> Rows { get; set; } = [];
    public List<string> Blockers { get; set; } = [];
}

public sealed class ExcelPayslipUpdateField
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string Type { get; set; } = "text";
    public string Destination { get; set; } = "Batch only";
    public bool Editable { get; set; }
    public string Reason { get; set; } = "";
    public int? SourceColumnIndex { get; set; }
    public string? MasterField { get; set; }
}

public sealed class ExcelPayslipUpdateCatalogRow
{
    public string EmployeeCode { get; set; } = "";
    public string EmployeeName { get; set; } = "";
    public Dictionary<string, string> Values { get; set; } = [];
}

public sealed class ExcelPayslipUpdateRowInput
{
    public string EmployeeCode { get; set; } = "";
    public Dictionary<string, string> Values { get; set; } = [];
}

public sealed class ExcelPayslipUpdatePreviewRequest
{
    public string Month { get; set; } = "";
    public string SourceFileName { get; set; } = "";
    public List<ExcelPayslipUpdateRowInput> Rows { get; set; } = [];
}

public sealed class ExcelPayslipUpdateChange
{
    public string Id { get; set; } = "";
    public string EmployeeCode { get; set; } = "";
    public string EmployeeName { get; set; } = "";
    public string FieldId { get; set; } = "";
    public string Label { get; set; } = "";
    public string Type { get; set; } = "text";
    public string OldValue { get; set; } = "";
    public string NewValue { get; set; } = "";
    public string Destination { get; set; } = "";
    public bool CanApply { get; set; }
    public bool Computed { get; set; }
    public string Reason { get; set; } = "";
}

public sealed class ExcelPayslipUpdatePreview
{
    public string Token { get; set; } = "";
    public List<ExcelPayslipUpdateChange> Changes { get; set; } = [];
    public List<string> Blockers { get; set; } = [];
}

public sealed class ExcelPayslipUpdateApplyRequest
{
    public string Token { get; set; } = "";
    public List<string> SelectedChangeIds { get; set; } = [];
    public string RequestId { get; set; } = "";
    public bool Acknowledged { get; set; }
    public bool AcknowledgeZeroChanges { get; set; }
}

public sealed class ExcelPayslipUpdateApplyResult
{
    public ExcelPayslipBatch Batch { get; set; } = new();
    public int AppliedChanges { get; set; }
    public int UpdatedEmployees { get; set; }
    public bool Replayed { get; set; }
}
