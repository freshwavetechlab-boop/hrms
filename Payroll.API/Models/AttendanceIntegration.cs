namespace Payroll.API.Models;

public class AttendanceDevice
{
    public string DeviceId { get; set; } = "";
    public string Name { get; set; } = "";
    public int ClientId { get; set; }
    public int WorkLocationId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime? TokenExpiresAt { get; set; }
    public bool HasToken { get; set; }
    public string TokenHint { get; set; } = "";
}

public class AttendanceDeviceRegistration : AttendanceDevice { }
public record AttendanceTokenRequest(int ClientId, int ValidDays = 90);
public record MachineAttendanceRequest(string DeviceId, List<MachineAttendancePunch> Punches);
public record MachineAttendancePunch(string PunchId, string EmployeeCode, string Action, string CapturedAt);
public record MachinePunchResult(string PunchId, string Status, string? Error = null, DateTime? AttendanceDate = null, decimal? TotalHours = null, decimal? PayableValue = null, string? Remarks = null);
public record AttendanceConfigurationGap(string Key, int ClientId, string ClientName, string Title, string Detail, string Route, string[] RequiredPermissions);

public class AttendanceRules
{
    public bool AllowManualHalfDay { get; set; }
    public int? LateGraceMinutes { get; set; }
    public int? EarlyGraceMinutes { get; set; }
    // null means not configured; zero explicitly disables requests of that kind.
    public int? MonthlyMissPunchLimit { get; set; }
    public int? MonthlyOdLimit { get; set; }
}
