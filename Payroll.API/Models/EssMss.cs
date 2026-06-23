namespace Payroll.API.Models;

public class EssLeaveBalance
{
    public string LeaveCode { get; set; } = string.Empty;
    public string LeaveType { get; set; } = string.Empty;
    public decimal Balance { get; set; }
    public DateTime BalanceDate { get; set; }
}

public class EssProfile
{
    public string EmployeeCode { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string WorkEmail { get; set; } = string.Empty;
    public string Department { get; set; } = string.Empty;
    public string Designation { get; set; } = string.Empty;
    public string DateOfJoining { get; set; } = string.Empty;
    public string WorkLocation { get; set; } = string.Empty;
    public string ReportingManager { get; set; } = string.Empty;
}

public class CreateEssLeaveRequest { public string LeaveCode { get; set; } = ""; public string FromDate { get; set; } = ""; public string ToDate { get; set; } = ""; public string Reason { get; set; } = ""; }
public class EssLeaveRequest { public long Id { get; set; } public string LeaveCode { get; set; } = ""; public string LeaveType { get; set; } = ""; public DateTime FromDate { get; set; } public DateTime ToDate { get; set; } public decimal Days { get; set; } public string Reason { get; set; } = ""; public string Status { get; set; } = ""; public DateTime CreatedAt { get; set; } }
public class EssPayslip { public int PayRunId { get; set; } public string PayPeriod { get; set; } = ""; public DateTime PayDate { get; set; } public string RunStatus { get; set; } = ""; public decimal GrossPay { get; set; } public decimal StatutoryDeductions { get; set; } public decimal OneTimeDeductions { get; set; } public decimal NetPay { get; set; } public string PaymentStatus { get; set; } = ""; public DateTime? PaymentDate { get; set; } }
public class EssAttendanceSummary { public string Month { get; set; } = ""; public int PresentDays { get; set; } public int PayableDays { get; set; } public int TotalWorkingDays { get; set; } }
public class EssHoliday { public string Name { get; set; } = ""; public DateTime StartDate { get; set; } public DateTime EndDate { get; set; } }
public class EssBirthday { public string Name { get; set; } = ""; public string Department { get; set; } = ""; }

public class AttendanceFacialVerification
{
    public bool Passed { get; set; }
    public decimal? FaceMatchScore { get; set; }
    public decimal? LivenessScore { get; set; }
    public string Provider { get; set; } = "";
    public string ReferenceId { get; set; } = "";
}

public class ValidateAttendancePunchRequest
{
    public string Action { get; set; } = "CheckIn";
    public decimal Latitude { get; set; }
    public decimal Longitude { get; set; }
    public int AccuracyMeters { get; set; }
    public DateTime? CapturedAt { get; set; }
    public string Reason { get; set; } = "";
    public AttendanceFacialVerification? Facial { get; set; }
}

public class AttendancePunchRuleSummary
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string ScopeType { get; set; } = "";
    public string Strictness { get; set; } = "";
}

public class AttendancePunchValidationResponse
{
    public bool Allowed { get; set; }
    public bool RequiresReason { get; set; }
    public bool RequiresApproval { get; set; }
    public bool PunchRecorded { get; set; }
    public long? PunchId { get; set; }
    public string Status { get; set; } = "";
    public string Message { get; set; } = "";
    public string NextAction { get; set; } = "";
    public decimal? DistanceMeters { get; set; }
    public int? AllowedRadiusMeters { get; set; }
    public int? GpsToleranceMeters { get; set; }
    public int DeviceAccuracyMeters { get; set; }
    public int? EffectiveRadiusMeters { get; set; }
    public decimal? OutsideByMeters { get; set; }
    public bool FacialRequired { get; set; } = true;
    public bool FacialPassed { get; set; }
    public AttendancePunchRuleSummary? Rule { get; set; }
}
