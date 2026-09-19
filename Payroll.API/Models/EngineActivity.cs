namespace Payroll.API.Models;

// References only: never persist names, prompts, resumes, addresses or credentials.
public sealed record EngineActivityContext(string Operation, string ReferenceType = "", string ReferenceId = "",
    long? ApplicationId = null, long? CandidateId = null, long? PositionId = null, int? ClientId = null,
    double? QueueWaitMs = null, int? Attempt = null);

public sealed record EngineActivityRow
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string EngineCode { get; init; } = "";
    public string Operation { get; init; } = "";
    public string ReferenceType { get; init; } = "";
    public string ReferenceId { get; init; } = "";
    public long? ApplicationId { get; init; }
    public long? CandidateId { get; init; }
    public long? PositionId { get; init; }
    public int? ClientId { get; init; }
    public int? Attempt { get; init; }
    public DateTime StartedAtUtc { get; init; }
    public DateTime? CompletedAtUtc { get; init; }
    public DateTime UpdatedAtUtc { get; init; }
    public double? DurationMs { get; init; }
    public double? QueueWaitMs { get; init; }
    public double? AiDurationMs { get; init; }
    public string AiStatus { get; init; } = "";
    public string Status { get; init; } = "Running";
    public int? HttpStatus { get; init; }
    public string FailureCode { get; init; } = "";
    public long Revision { get; init; }
    // Read-time display joins, not stored in telemetry.
    public string CandidateName { get; init; } = "";
    public string ApplicationCode { get; init; } = "";
    public string PositionTitle { get; init; } = "";
}

public sealed class EngineActivityPage
{
    public string Deployment { get; set; } = "";
    public bool RecordingEnabled { get; set; }
    public int RetentionDays { get; set; }
    public long DroppedRecords { get; set; }
    public string Warning { get; set; } = "";
    public bool HasMore { get; set; }
    public List<EngineActivityRow> Items { get; set; } = [];
}
