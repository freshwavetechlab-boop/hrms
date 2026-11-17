namespace Payroll.API.Models;

// Presentation metadata only. No source employee, attendance or monetary values are persisted here.
public sealed class PayslipDesignField
{
    public int SourceColumn { get; set; }
    public string Label { get; set; } = "";
    public string Kind { get; set; } = "info";
    public string NativeCode { get; set; } = "";
    public string InfoToken { get; set; } = "";
    public bool Visible { get; set; } = true;
}
public sealed class PayslipDesignTemplate
{
    public long Id { get; set; }
    public int ClientId { get; set; }
    public string Name { get; set; } = "Excel Payslip";
    public string Theme { get; set; } = "Excel Payslip";
    public bool ShowLogo { get; set; } = true;
    public bool ShowClient { get; set; } = true;
    public bool ShowYtd { get; set; }
    public bool ShowBank { get; set; } = true;
    public string Note { get; set; } = "This is a system-generated payslip and does not require a signature.";
    public bool Active { get; set; } = true;
}
public sealed class PayslipDesignPreviewRequest { public int ClientId { get; set; } public string SourceBatchId { get; set; } = ""; }
public sealed class PayslipDesignSaveRequest
{
    public string RequestId { get; set; } = "";
    public PayslipDesignTemplate Template { get; set; } = new();
    public string SourceBatchId { get; set; } = "";
    public string SourceHash { get; set; } = "";
    public string ExistingProfileHash { get; set; } = "";
    public int AmountDecimalPlaces { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? PayPeriods { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool AllPayPeriods { get; set; }
    public List<PayslipDesignField> Fields { get; set; } = [];
    public bool Reviewed { get; set; }
}
public sealed record PayslipDesignSource(string Id, string Month, string Name);
public sealed record PayslipDesignCode(string Code, string Name, string Category);
public sealed record PayslipDesignPreview(int ClientId, string SourceBatchId, string SourceHash, bool SimpleLayout,
    IReadOnlyList<PayslipDesignField> Fields, IReadOnlyList<PayslipDesignCode> NativeCodes, IReadOnlyList<string> InfoTokens)
{
    public bool SupportsPayrollMonthScope { get; init; } = true;
}
public sealed class PayslipDesignProfile
{
    public int Version { get; set; } = 1;
    public int ClientId { get; set; }
    public long TemplateId { get; set; }
    public string SourceBatchId { get; set; } = "";
    public string SourceHash { get; set; } = "";
    public bool SimpleLayout { get; set; }
    // Old protected profiles deliberately retain their previous zero-row display.
    // A newly reviewed source derives this setting from its actual layout.
    public bool KeepMappedZeroRows { get; set; } = true;
    public int AmountDecimalPlaces { get; set; }
    // Absent/null protected legacy profiles keep their existing all-month scope.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? PayPeriods { get; set; }
    // Server-retained original client Classic settings only. Not a save-request
    // field and never exposed by the editor API as an editable fallback.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public PayslipDesignTemplate? PreviousClassicTemplate { get; set; }
    public List<PayslipDesignField> Fields { get; set; } = [];
    public string RequestId { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public int ReviewedBy { get; set; }
}
public sealed record NativePayslipDesignLine(string Code, string Category, decimal MonthlyAmount, decimal Amount);
public sealed class NativePayslipDesignData
{
    internal int NativeRowId { get; set; }
    public int ClientId { get; set; }
    public int EmployeeId { get; set; }
    public string EmployeeCode { get; set; } = "";
    public string EmployeeName { get; set; } = "";
    public string ClientName { get; set; } = "";
    public string Month { get; set; } = "";
    public string Status { get; set; } = "";
    public decimal Gross { get; set; }
    public decimal Deductions { get; set; }
    public decimal NetPay { get; set; }
    public Dictionary<string,string> Information { get; set; } = new(StringComparer.Ordinal);
    public List<NativePayslipDesignLine> Lines { get; set; } = [];
}
public sealed record PayslipDesignPdf(byte[] Bytes, string FileName);

// Transaction-local authenticated configuration; never an HTTP request/response.
internal sealed record NativeSourceDesignBinding(PayslipDesignTemplate Template, PayslipDesignProfile Profile,
    ExcelPayslipBatch Batch, ExcelPayslipCalculationSource Source, string DesignMetadataHash,
    string RawBatchJson, string RawSourceJson);
