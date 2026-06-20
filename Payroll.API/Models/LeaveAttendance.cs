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
