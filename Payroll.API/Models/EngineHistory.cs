namespace Payroll.API.Models;

// Numeric aggregates only: no request, user, document, prompt or error content.
public sealed record EngineHistoryBucket(DateTime BucketUtc, string EngineCode, long Completed, long Failed,
    double DurationMs, double MaxDurationMs, double BusyMs, double ObservedMs, long Revision);

public sealed class EngineHistoryResult
{
    public string Deployment { get; set; } = "";
    public DateTime FromUtc { get; set; }
    public DateTime UntilUtc { get; set; }
    public int BucketMinutes { get; set; }
    public bool RecordingEnabled { get; set; }
    public DateTime? LastSavedAtUtc { get; set; }
    public long DroppedBuckets { get; set; }
    public string Warning { get; set; } = "";
    public List<EngineHistorySeries> Engines { get; set; } = [];
}
public sealed class EngineHistorySeries
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public List<EngineHistoryPoint> Trend { get; set; } = [];
}
public sealed class EngineHistoryPoint
{
    public DateTime TimeUtc { get; set; }
    public long? Requests { get; set; }
    public long? Failures { get; set; }
    public double? LoadPercent { get; set; }
    public double? AverageDurationMs { get; set; }
    public double? MaxDurationMs { get; set; }
    public double ObservedInstanceSeconds { get; set; }
}
