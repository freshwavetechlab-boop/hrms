namespace Payroll.API.Models;

public class ScheduledJob
{
    public int Id { get; set; }
    public string JobCode { get; set; } = string.Empty;
    public string JobName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string HandlerKey { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public string ScheduleType { get; set; } = "Interval";
    public int IntervalMinutes { get; set; } = 60;
    public string DailyRunTime { get; set; } = "01:00";
    public int MonthlyRunDay { get; set; } = 1;
    public string ConfigJson { get; set; } = "{}";
    public DateTime? LastRunAt { get; set; }
    public DateTime? NextRunAt { get; set; }
    public string LastStatus { get; set; } = "Never Run";
    public string LastMessage { get; set; } = string.Empty;
    public bool IsRunning { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public class ScheduledJobRun
{
    public long Id { get; set; }
    public int JobId { get; set; }
    public string JobCode { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string Status { get; set; } = "Running";
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
    public string Message { get; set; } = string.Empty;
    public string ErrorDetails { get; set; } = string.Empty;
    public string TriggeredBy { get; set; } = "Scheduler";
    public long DurationMs { get; set; }
}

public class ScheduledJobSaveRequest : ScheduledJob { }

public class ScheduledJobAction
{
    public int Id { get; set; }
    public string ActionCode { get; set; } = string.Empty;
    public string ActionName { get; set; } = string.Empty;
    public string ActionType { get; set; } = "Notification Event";
    public string Description { get; set; } = string.Empty;
    public string ConfigJson { get; set; } = "{}";
    public bool IsActive { get; set; } = true;
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public class ScheduledJobActionSaveRequest : ScheduledJobAction { }

public class ScheduledJobRunResult
{
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class ScheduledJobHandlerOption
{
    public string HandlerKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}
