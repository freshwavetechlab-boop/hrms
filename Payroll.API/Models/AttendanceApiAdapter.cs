namespace Payroll.API.Models;

public sealed class AttendanceApiAdapter
{
    public string Id { get; set; } = "";
    public int ClientId { get; set; }
    public string Revision { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsActive { get; set; }
    public string DeviceId { get; set; } = "";
    public string SourceDeviceId { get; set; } = "";
    public string Url { get; set; } = "";
    public string Method { get; set; } = "POST";
    public string AuthType { get; set; } = "ApiKey";
    public string AuthHeader { get; set; } = "X-Api-Key";
    public string Username { get; set; } = "";
    public bool HasSecret { get; set; }
    public bool HasDeviceToken { get; set; }
    public bool AllowPrivateNetwork { get; set; }
    public Dictionary<string, string> Headers { get; set; } = new();
    public Dictionary<string, string> Query { get; set; } = new();
    public string BodyTemplate { get; set; } = "{}";
    public string RequestDateFormat { get; set; } = "yyyy-MM-dd";
    public string RecordsPath { get; set; } = "Data";
    public string SuccessPath { get; set; } = "Success";
    public string SuccessValue { get; set; } = "true";
    public string EmployeePath { get; set; } = "UserID";
    public string DevicePath { get; set; } = "DeviceID";
    public string TimePath { get; set; } = "PunchTime";
    public string ActionPath { get; set; } = "InOutMode";
    public string PunchIdPath { get; set; } = "";
    public string PunchTimeFormat { get; set; } = "";
    public string TimeZoneId { get; set; } = "Asia/Kolkata";
    public string[] InValues { get; set; } = ["IN"];
    public string[] OutValues { get; set; } = ["OUT"];
    public Dictionary<string, string> EmployeeMap { get; set; } = new();
    public string Pagination { get; set; } = "None";
    public int FirstPage { get; set; } = 1;
    public int PageSize { get; set; } = 500;
    public int MaxPages { get; set; } = 20;
    public int TimeoutSeconds { get; set; } = 20;
    public int SyncIntervalMinutes { get; set; }
    public int LookbackDays { get; set; } = 2;
    public DateTime? LastAttemptAt { get; set; }
    public DateTime? LastSuccessAt { get; set; }
    public AttendanceAdapterRun? LastRun { get; set; }
}

public sealed record SaveAttendanceApiAdapter(AttendanceApiAdapter Configuration, string? Secret, string? DeviceToken,
    bool ClearSecret = false, bool ClearDeviceToken = false);
public sealed record RunAttendanceApiAdapter(string FromDate, string ToDate);
public sealed class AttendanceAdapterRun
{
    public bool Success { get; set; }
    public bool Preview { get; set; }
    public int? HttpStatus { get; set; }
    public int Fetched { get; set; }
    public int Mapped { get; set; }
    public int Skipped { get; set; }
    public int Accepted { get; set; }
    public int Duplicates { get; set; }
    public int Rejected { get; set; }
    public string Message { get; set; } = "";
    public List<string> Errors { get; set; } = [];
    public List<MachineAttendancePunch> Samples { get; set; } = [];
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public long DurationMs { get; set; }
}
