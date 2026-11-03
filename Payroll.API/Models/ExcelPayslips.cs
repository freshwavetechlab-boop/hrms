using System.Text.Json.Serialization;

namespace Payroll.API.Models;

public sealed class ExcelPayslipClient
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool HasSavedBatch { get; set; }
}

public sealed record ExcelPayslipCodeGeneration(bool Enabled, int Digits);
public sealed record ExcelPayslipCodeOptions(string ClientCode, int Digits, string NextSequence);
public sealed record ExcelPayslipCodePreviewRequest(List<string> Codes);
public sealed record ExcelPayslipCodeMatch(string Code, int Records, int ActiveRecords);
public sealed record ExcelPayslipCodePreview(List<ExcelPayslipCodeMatch> Matches, int CheckedCodes);
public sealed record ExcelPayslipMasterComparison(int ClientId, string EmployeeCode, string Status, Dictionary<string, string> Fields);
public sealed record ExcelPayslipIdentityInput(int SourceRow,string EmployeeCode,string EmployeeName,string Email,string Mobile,string Uan,string BankAccount,string WorkLocation,string Department,List<string>? ConflictingFields=null);
public sealed record ExcelPayslipIdentityPreviewRequest(List<ExcelPayslipIdentityInput> Rows);
public sealed record ExcelPayslipIdentityMatch(int SourceRow,string Status,string MasterCode,bool Active,List<string> Basis,List<string> Reasons);
public sealed record ExcelPayslipIdentityPreview(int ClientId,int CheckedRows,List<ExcelPayslipIdentityMatch> Rows);

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
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExcelPayslipMasterDetails? MasterDetails { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExcelPayslipNegativeAmountReview? NegativeAmountReview { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExcelPayslipWarningsReview? WarningReview { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WarningReviewToken { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExcelPayslipEmployment? Employment { get; set; }
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

public sealed record ExcelPayslipMasterDetails(int EmployeeId, string Designation)
{
    public bool Linked { get; init; }
    public string EmployeeCode { get; init; } = "";
    public string EmployeeName { get; init; } = "";
    public string Email { get; init; } = "";
    public decimal AnnualCtc { get; init; }
    public string SalaryStructureId { get; init; } = "";
    public Dictionary<string,string> Fields { get; init; } = [];
    public Dictionary<string,decimal> SalaryInputs { get; set; } = [];
    public ExcelPayslipCurrentSalary? Salary { get; set; }
}
public sealed record ExcelPayslipCurrentSalary(string Status,string Message,List<ExcelPayslipAmount> Earnings,
    List<ExcelPayslipAmount> Deductions,List<ExcelPayslipAmount> EmployerContributions,decimal? Gross,decimal? TotalDeductions,decimal? NetPay);
public sealed record ExcelPayslipMasterLink(string RowId,int EmployeeId,int SourceRow,string IdentityHash,string SalaryHash,
    int PayrollDays,decimal? PresentDays,decimal PayableDays,bool ReviewedExtraDutyUnits)
{
    public string SourceBatchId {get;init;}="";
    public string SourceHash {get;init;}="";
    public string ImportId {get;init;}="";
}

public sealed record ExcelPayslipNegativeAmountReview(string AmountsHash, string ReviewedBy, DateTime ReviewedAtUtc);
public sealed record ExcelPayslipNegativeAmountReviewRequest(string RowId);
public sealed record ExcelPayslipWarningsReview(string AmountsHash, string ReviewedBy, DateTime ReviewedAtUtc, List<string> Warnings);
public sealed record ExcelPayslipWarningReviewSelection(string RowId, string Token);
public sealed class ExcelPayslipWarningReviewRequest
{
    public string Action { get; set; } = "review";
    public List<ExcelPayslipWarningReviewSelection> Rows { get; set; } = [];
}

public sealed record ExcelPayslipEmployment(int? EmployeeId, string EmployeeCode, string Status, string? LastWorkingDate, bool BlockedForMonth, bool IdentityReviewRequired);

// Independent source snapshot: these identifiers never reference employee or pay-run records.
public sealed class ExcelPayslipBatch
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExcelPayslipCodeGeneration? EmployeeCodeGeneration { get; set; }
    // Accepted on save and stored separately; PDF/mail receive it separately to derive the displayed daily rate.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExcelPayslipCalculationSource? CalculationSource { get; set; }
    public string Id { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int LifecycleVersion { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? ParentBatchId { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ExcelPayslipOriginalReference>? OriginalWorkbooks { get; set; }
    public int ClientId { get; set; }
    public string ClientName { get; set; } = "";
    public string SalaryTemplateId { get; set; } = "";
    public string SalaryTemplateName { get; set; } = "";
    public string Month { get; set; } = "";
    public string SourceFileName { get; set; } = "";
    public string SheetName { get; set; } = "";
    public int HeaderRow { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool SimpleLayout { get; set; }
    public List<ExcelPayslipRow> Rows { get; set; } = [];
    public DateTime CreatedAtUtc { get; set; }
    public string CreatedBy { get; set; } = "";
}

public sealed class ExcelPayslipBatchSummary
{
    public bool IsConsolidatedSource { get; set; }
    public bool ConsolidationStale { get; set; }
    public string? CanonicalBatchId { get; set; }
    public string? ConsolidationGroupId { get; set; }
    public List<string> ConsolidatedSourceBatchIds { get; set; } = [];
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

public sealed class ExcelPayslipEmailRequest : ExcelPayslipSelection
{
    public ExcelPayslipEmailRequest() { IncludeSeal = false; }
    public bool AttachPayslips { get; set; }
    public string Email { get; set; } = "";
    public string RequestId { get; set; } = "";
}

public sealed record ExcelPayslipEmailRequestResult(long QueueId, string Email, int EmployeeCount, string Status, bool IncludesPayslips = false);

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
