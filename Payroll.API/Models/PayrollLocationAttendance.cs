namespace Payroll.API.Models;

public sealed class PayrollLocationAttendanceContext
{
    public int ClientId { get; set; }
    public string Month { get; set; } = "";
    public string PolicyBatchId { get; set; } = "";
    public List<int> GroupIds { get; set; } = [];
    public string Version { get; set; } = "";
    public bool StorageAvailable { get; set; }
    public List<PayrollLocationAttendanceRow> Rows { get; set; } = [];
}

public sealed class PayrollLocationAttendanceRow
{
    public string Key { get; set; } = "";
    public string AssignmentId { get; set; } = "";
    public string? ReferenceAssignmentId { get; set; }
    public int EmployeeId { get; set; }
    public string EmployeeCode { get; set; } = "";
    public string EmployeeName { get; set; } = "";
    public int WorkLocationId { get; set; }
    public string WorkLocationName { get; set; } = "";
    public string TemplateId { get; set; } = "";
    public string TemplateName { get; set; } = "";
    public string RulesHash { get; set; } = "";
    public string InputVersion { get; set; } = "";
    public bool NativePart { get; set; }
    public int PayrollDays { get; set; }
    public int MonthlyPayrollDays { get; set; }
    public decimal? PresentDays { get; set; }
    public decimal? PayableDays { get; set; }
    public int? LockedPayRunId { get; set; }
    public string Issue { get; set; } = "";
    public bool Recorded => PresentDays.HasValue && PayableDays.HasValue;
}

public sealed class PayrollLocationAttendanceRequest
{
    public string RequestId { get; set; } = "";
    public int ClientId { get; set; }
    public string Month { get; set; } = "";
    public string PolicyBatchId { get; set; } = "";
    public List<int> GroupIds { get; set; } = [];
    public int WorkLocationId { get; set; }
    public string ContextVersion { get; set; } = "";
    public List<PayrollLocationAttendanceInput> Rows { get; set; } = [];
}

public sealed record PayrollLocationAttendanceInput(string Key, int EmployeeId, int WorkLocationId,
    [property: System.Text.Json.Serialization.JsonRequired] int PayrollDays,
    [property: System.Text.Json.Serialization.JsonRequired] decimal PresentDays,
    [property: System.Text.Json.Serialization.JsonRequired] decimal PayableDays,
    string InputVersion);

public sealed record PayrollLocationAttendanceResult(string RequestId, int SavedParts, int PublishedEmployees,
    int PendingEmployees, PayrollLocationAttendanceContext Context);
