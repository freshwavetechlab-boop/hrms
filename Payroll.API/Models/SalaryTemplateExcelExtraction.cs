namespace Payroll.API.Models;

// A transient, client-scoped worksheet review. It is never an Excel payslip batch.
public sealed class SalaryTemplateExcelExtractionRequest
{
    public int ClientId { get; set; }
    public string TemplateName { get; set; } = "";
    public string Month { get; set; } = "";
    public ExcelPayslipCalculationSource Source { get; set; } = new();
    public int SampleRow { get; set; }
    public List<SalaryTemplateExcelColumn> SelectedColumns { get; set; } = [];
    public int? FixedPayrollDays { get; set; }
    public string PayrollDaysCell { get; set; } = "";
}

public sealed class SalaryTemplateExcelColumn
{
    public int ColumnIndex { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "ignore";
    public string? ExistingComponentId { get; set; }
    public bool ReviewedFixedValue { get; set; }
    public string InputMode { get; set; } = "period";
}

public sealed class SalaryTemplateExcelCandidate
{
    public string Id { get; set; } = "";
    public string? ExistingComponentId { get; set; }
    public int ColumnIndex { get; set; }
    public int? InputColumnIndex { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "Earning";
    public string ComponentRole { get; set; } = "Regular Earning";
    public string StatutoryType { get; set; } = "None";
    public string CalculationType { get; set; } = "Formula";
    public string Value { get; set; } = "";
    public string Formula { get; set; } = "";
    public string BaseComponent { get; set; } = "";
    public bool ProRata { get; set; }
    public bool Taxable { get; set; } = true;
    public bool Active { get; set; } = true;
    public int Priority { get; set; }
    public string PayType { get; set; } = "Fixed Pay";
    public bool RequiresEmployeeInput { get; set; }
}

public sealed record SalaryTemplateExcelIssue(string Severity, string Code, int? ColumnIndex, string Message);
public sealed record SalaryTemplateExcelComparison(int ColumnIndex, string Code, decimal? SourceAmount,
    decimal? CalculatedAmount, decimal? Variance, string Status);
public sealed class SalaryTemplateExcelExtractionResult
{
    public bool CanCreateDraft => Issues.All(issue => issue.Severity != "Error");
    public string TemplateName { get; set; } = "";
    public List<SalaryTemplateExcelCandidate> Components { get; set; } = [];
    public List<PayrollReviewLine> Lines { get; set; } = [];
    public List<SalaryTemplateExcelIssue> Issues { get; set; } = [];
    public List<SalaryTemplateExcelComparison> Comparisons { get; set; } = [];
    public PayrollSimulationRequest? Scenario { get; set; }
    public PayrollSimulationResult? Simulation { get; set; }
}
