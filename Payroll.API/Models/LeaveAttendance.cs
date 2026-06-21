namespace Payroll.API.Models;

public class LeaveAttendanceSetup
{
    public bool IsEnabled { get; set; }
    public List<LeaveAttendanceSetupStep> Steps { get; set; } = [];
}

public class LeaveAttendanceSetupStep
{
    public string Code { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Status { get; set; } = "Not Started";
    public bool IsMandatory { get; set; }
    public bool CanDisable { get; set; } = true;
    public DateTime? UpdatedAt { get; set; }
}

public class UpdateLeaveAttendanceModuleRequest
{
    public bool IsEnabled { get; set; }
}

public class UpdateLeaveAttendanceStepRequest
{
    public string Status { get; set; } = string.Empty;
}

public class LeaveAttendancePreferences
{
    public int Id { get; set; }
    public int AttendanceCycleStartDay { get; set; } = 1;
    public int AttendanceCycleEndDay { get; set; } = 25;
    public int PayrollReportGenerationDay { get; set; } = 28;
    public bool IncludeLeaveEncashmentInPayRun { get; set; }
    public int? LeaveEncashmentSalaryComponentId { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public class SaveLeaveAttendancePreferencesRequest
{
    public int AttendanceCycleStartDay { get; set; }
    public int AttendanceCycleEndDay { get; set; }
    public int PayrollReportGenerationDay { get; set; }
    public bool IncludeLeaveEncashmentInPayRun { get; set; }
    public int? LeaveEncashmentSalaryComponentId { get; set; }
}

public class AttendanceSettings
{
    public int Id { get; set; }
    public TimeSpan CheckInTime { get; set; } = new(9, 0, 0);
    public TimeSpan CheckOutTime { get; set; } = new(18, 0, 0);
    public string WorkingHoursCalculation { get; set; } = "First check-in and last check-out";
    public decimal MinimumHoursForHalfDay { get; set; } = 4;
    public decimal MinimumHoursForFullDay { get; set; } = 8;
    public decimal MaximumHoursAllowedForFullDay { get; set; } = 12;
    public bool AllowRegularizationRequests { get; set; } = true;
    public string RegularizationWindow { get; set; } = "Anytime";
    public int PastDaysAllowed { get; set; } = 7;
    public bool RestrictRegularizationRequestsPerMonth { get; set; }
    public int MaxRegularizationRequestsPerMonth { get; set; } = 3;
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public class SaveAttendanceSettingsRequest : AttendanceSettings { }

public class LeaveType
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Type { get; set; } = "Paid";
    public string Description { get; set; } = string.Empty;
    public decimal Entitlement { get; set; }
    public string EntitlementPeriod { get; set; } = "Yearly";
    public bool ProRateForNewJoinees { get; set; }
    public bool ResetEnabled { get; set; }
    public string ResetFrequency { get; set; } = "Yearly";
    public bool CarryForwardUnusedLeaves { get; set; }
    public decimal? MaxCarryForwardLimit { get; set; }
    public bool EncashUnusedLeaves { get; set; }
    public decimal? MaxEncashmentLimit { get; set; }
    public bool AllowNegativeLeaveBalance { get; set; }
    public string NegativeBalanceHandling { get; set; } = "Mark as LOP";
    public bool AllowPastDates { get; set; }
    public string PastDateLimitType { get; set; } = "No limit";
    public int? PastDateLimitDays { get; set; }
    public bool AllowFutureDates { get; set; }
    public string FutureDateLimitType { get; set; } = "No limit";
    public int? FutureDateLimitDays { get; set; }
    public string ApplicabilityMode { get; set; } = "All employees";
    public string WorkLocation { get; set; } = string.Empty;
    public string Department { get; set; } = string.Empty;
    public string Designation { get; set; } = string.Empty;
    public string Gender { get; set; } = string.Empty;
    public DateTime EffectiveFrom { get; set; } = DateTime.Today;
    public DateTime? ExpiresOn { get; set; }
    public bool PostponeCreditsForNewEmployees { get; set; }
    public int? PostponeCreditValue { get; set; }
    public string PostponeCreditUnit { get; set; } = "Days";
    public bool IsActive { get; set; } = true;
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public class SaveLeaveTypeRequest : LeaveType { }

public class Holiday
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime StartDate { get; set; } = DateTime.Today;
    public DateTime EndDate { get; set; } = DateTime.Today;
    public string Description { get; set; } = string.Empty;
    public bool AllLocations { get; set; } = true;
    public List<int> WorkLocationIds { get; set; } = [];
    public string WorkLocations { get; set; } = "All locations";
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public class SaveHolidayRequest : Holiday { }

public class LeaveBalanceImportMapping
{
    public string EmployeeNumber { get; set; } = string.Empty;
    public string LeaveType { get; set; } = string.Empty;
    public string Date { get; set; } = string.Empty;
    public string Count { get; set; } = string.Empty;
}

public class LeaveBalanceImportRow
{
    public int RowNumber { get; set; }
    public string EmployeeNumber { get; set; } = string.Empty;
    public string LeaveType { get; set; } = string.Empty;
    public string Date { get; set; } = string.Empty;
    public string Count { get; set; } = string.Empty;
    public bool IsValid { get; set; }
    public List<string> Errors { get; set; } = [];
}

public class LeaveBalanceImportPreview
{
    public string FileName { get; set; } = string.Empty;
    public List<string> Columns { get; set; } = [];
    public LeaveBalanceImportMapping Mapping { get; set; } = new();
    public List<string> UnmappedFields { get; set; } = [];
    public List<LeaveBalanceImportRow> ValidRecords { get; set; } = [];
    public List<LeaveBalanceImportRow> ErrorRecords { get; set; } = [];
}

public class FinalizeLeaveBalanceImportRequest
{
    public string FileName { get; set; } = string.Empty;
    public string Encoding { get; set; } = "UTF-8";
    public LeaveBalanceImportMapping Mapping { get; set; } = new();
    public List<LeaveBalanceImportRow> ValidRecords { get; set; } = [];
    public List<LeaveBalanceImportRow> ErrorRecords { get; set; } = [];
}

public class LeaveBalanceImportResult
{
    public int LogId { get; set; }
    public int ImportedCount { get; set; }
    public int SkippedCount { get; set; }
}
