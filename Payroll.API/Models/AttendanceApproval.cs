namespace Payroll.API.Models;

public sealed class AttendanceApprovalPolicy
{
    public bool Enabled { get; set; }
    public int WorkflowId { get; set; }
    public string EffectiveFromMonth { get; set; } = "";
}

public sealed class SubmitAttendanceApproval
{
    public int ClientId { get; set; }
    public string Month { get; set; } = "";
    public List<int> EmployeeIds { get; set; } = [];
    public string ProjectReference { get; set; } = "";
}

public sealed class ConfigureAttendanceApproval
{
    public int ClientId { get; set; }
    public bool Enabled { get; set; } = true;
    public int WorkflowId { get; set; }
    public string EffectiveFromMonth { get; set; } = "";
    public int? DefaultPiUserId { get; set; }
    public bool AssignUnassignedEmployees { get; set; }
}

public sealed class AttendanceApprovalRow
{
    public int EmployeeId { get; set; }
    public string EmployeeCode { get; set; } = "";
    public string EmployeeName { get; set; } = "";
    public string WorkLocation { get; set; } = "";
    public string Designation { get; set; } = "";
    public int? PiUserId { get; set; }
    public string PiName { get; set; } = "";
    public string Status { get; set; } = "Not submitted";
    public string Detail { get; set; } = "";
    public string ProjectReference { get; set; } = "";
    public long? InstanceId { get; set; }
    public long? TaskId { get; set; }
    public bool CanSubmit { get; set; }
    public bool CanApprove { get; set; }
    public bool RequiresRollVerification { get; set; }
    public decimal? PresentDays { get; set; }
    public decimal? PayableDays { get; set; }
}

public sealed class AttendanceApprovalContext
{
    public bool Enabled { get; set; }
    public string Month { get; set; } = "";
    public string Message { get; set; } = "";
    public List<AttendanceApprovalRow> Rows { get; set; } = [];
}
