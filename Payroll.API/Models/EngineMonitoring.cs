namespace Payroll.API.Models;

public sealed class EngineMonitoringSnapshot
{
    public DateTime GeneratedAtUtc { get; set; }
    public int RefreshAfterSeconds { get; set; } = 5;
    public bool IsReadOnly { get; set; } = true;
    public string AccessPolicy { get; set; } = "Super admin only";
    public EngineProcessMetric Process { get; set; } = new();
    public List<EngineRuntimeMetric> Engines { get; set; } = [];
}

public sealed class EngineProcessMetric
{
    public decimal CpuPercent { get; set; }
    public decimal WorkingSetMb { get; set; }
    public decimal ManagedMemoryMb { get; set; }
    public int ThreadCount { get; set; }
    public long UptimeSeconds { get; set; }
}

public sealed class EngineRuntimeMetric
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Coverage { get; set; } = "Request traffic";
    public string State { get; set; } = "Idle";
    public int LoadPercent { get; set; }
    public int ActiveRequests { get; set; }
    public int RequestsLastFiveMinutes { get; set; }
    public decimal SuccessRate { get; set; } = 100;
    public decimal AverageDurationMs { get; set; }
    public decimal P95DurationMs { get; set; }
    public DateTime? LastActivityUtc { get; set; }
    public string LastError { get; set; } = string.Empty;
    public List<EngineTrendPoint> Trend { get; set; } = [];
}

public sealed class EngineTrendPoint
{
    public DateTime TimeUtc { get; set; }
    public int LoadPercent { get; set; }
    public int Requests { get; set; }
    public int Failures { get; set; }
    public decimal AverageDurationMs { get; set; }
}
