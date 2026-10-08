using System.Text.Json.Serialization;

namespace Payroll.API.Models;

public sealed class ExcelPayslipClient
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class ExcelPayslipTemplate
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<ExcelPayslipTemplateComponent> Components { get; set; } = [];
}

public sealed class ExcelPayslipTemplateComponent
{
    public string Id { get; set; } = "";
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "Information";
}

public sealed class ExcelPayslipText
{
    public string Label { get; set; } = "";
    public string Value { get; set; } = "";
}

public sealed class ExcelPayslipAmount
{
    public string Label { get; set; } = "";
    [JsonRequired] public decimal Amount { get; set; }
}

public sealed class ExcelPayslipRow
{
    public string Id { get; set; } = "";
    public int SourceRow { get; set; }
    public string EmployeeCode { get; set; } = "";
    public string EmployeeName { get; set; } = "";
    public string Email { get; set; } = "";
    public List<ExcelPayslipText> Information { get; set; } = [];
    public List<ExcelPayslipAmount> Earnings { get; set; } = [];
    public List<ExcelPayslipAmount> Deductions { get; set; } = [];
    public List<ExcelPayslipAmount> EmployerContributions { get; set; } = [];
    [JsonRequired] public decimal NetPay { get; set; }
    public decimal? DeclaredGross { get; set; }
    public decimal? DeclaredDeductions { get; set; }
    public List<string> Warnings { get; set; } = [];
}

// Independent source snapshot: these identifiers never reference employee or pay-run records.
public sealed class ExcelPayslipBatch
{
    // Accepted on save, stored separately from the payslip snapshot and never used by PDF/mail rendering.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExcelPayslipCalculationSource? CalculationSource { get; set; }
    public string Id { get; set; } = "";
    public int ClientId { get; set; }
    public string ClientName { get; set; } = "";
    public string SalaryTemplateId { get; set; } = "";
    public string SalaryTemplateName { get; set; } = "";
    public string Month { get; set; } = "";
    public string SourceFileName { get; set; } = "";
    public string SheetName { get; set; } = "";
    public int HeaderRow { get; set; }
    public List<ExcelPayslipRow> Rows { get; set; } = [];
    public DateTime CreatedAtUtc { get; set; }
    public string CreatedBy { get; set; } = "";
}

public sealed class ExcelPayslipBatchSummary
{
    public string Id { get; set; } = "";
    public int ClientId { get; set; }
    public string ClientName { get; set; } = "";
    public string SalaryTemplateId { get; set; } = "";
    public string SalaryTemplateName { get; set; } = "";
    public string Month { get; set; } = "";
    public string SourceFileName { get; set; } = "";
    public string SheetName { get; set; } = "";
    public int RowCount { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string CreatedBy { get; set; } = "";
}

public class ExcelPayslipSelection
{
    public List<string> RowIds { get; set; } = [];
    public bool IncludeSeal { get; set; } = true;
    public int AmountDecimalPlaces { get; set; }
    public bool AcknowledgeWarnings { get; set; }
}

public sealed class SendExcelPayslipsRequest : ExcelPayslipSelection
{
    public string Mode { get; set; } = "Individual";
    public string Email { get; set; } = "";
    public Dictionary<string, string> EmailOverrides { get; set; } = [];
    public string RequestId { get; set; } = "";
}

public sealed class ExcelPayslipDeliveryItem
{
    public string RowId { get; set; } = "";
    public string EmployeeName { get; set; } = "";
    public string Email { get; set; } = "";
    public string Status { get; set; } = "Error";
    public string Message { get; set; } = "";
}

public sealed class ExcelPayslipDeliveryResult
{
    public List<ExcelPayslipDeliveryItem> Items { get; set; } = [];
}
