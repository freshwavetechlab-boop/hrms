using System.Text.Json;

namespace Payroll.API.Models;

// Deliberately separate session lifecycle from the existing interview/hiring result.
public sealed class InternalInterviewOptions
{
    public bool Enabled { get; set; }
    public bool DrainMode { get; set; }
    public bool MaintenanceEnabled { get; set; }
    public string PublicPortalBaseUrl { get; set; } = "";
    public string LiveKitUrl { get; set; } = "";
    public string LiveKitApiUrl { get; set; } = "";
    public string LiveKitApiKey { get; set; } = "";
    public string LiveKitApiSecret { get; set; } = "";
    public bool LiveKitWebhooksEnabled { get; set; }
    public string RecordingDirectory { get; set; } = "";
    public string EgressRecordingDirectory { get; set; } = "/recordings";
    public long LocalModelId { get; set; }
    public string OllamaBaseUrl { get; set; } = "";
    public string OllamaModel { get; set; } = "";
    public string SpeechBaseUrl { get; set; } = "";
    public string SpeechApiKey { get; set; } = "";
    public int RetentionDays { get; set; } = 30;
    public int ProviderTimeoutSeconds { get; set; } = 120;
    public long MaxRecordingBytes { get; set; } = 512L * 1024 * 1024;
}

public sealed class InternalInterviewConfiguration
{
    public string Mode { get; set; } = "Human";
    public string Language { get; set; } = "en";
    public string Difficulty { get; set; } = "Intermediate";
    public int MaxQuestions { get; set; } = 8;
    public int AnswerSeconds { get; set; } = 120;
    public int FollowUpLimit { get; set; } = 1;
    public bool RecordingEnabled { get; set; }
    public bool TranscriptionEnabled { get; set; }
    public List<long> QuestionIds { get; set; } = [];
}

public sealed class InterviewQuestion
{
    public long Id { get; set; }
    public int ClientId { get; set; }
    public long PositionId { get; set; }
    public string Skill { get; set; } = "";
    public string Difficulty { get; set; } = "Intermediate";
    public string Language { get; set; } = "en";
    public string Question { get; set; } = "";
    public string EvaluationCriteria { get; set; } = "";
    public string FollowUpInstructions { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

public sealed class InternalInterviewContext
{
    public long InterviewId { get; set; }
    public long ApplicationId { get; set; }
    public long CandidateId { get; set; }
    public int ClientId { get; set; }
    public long PositionId { get; set; }
    public string CandidateName { get; set; } = "";
    public string PositionTitle { get; set; } = "";
    public string JobLocation { get; set; } = "";
    public string RoundCode { get; set; } = "";
    public string InterviewStatus { get; set; } = "";
    public string Result { get; set; } = "";
    public string TimeZoneId { get; set; } = "Asia/Kolkata";
    public DateTime ScheduledStart { get; set; }
    public DateTime ScheduledEnd { get; set; }
    public List<int> PanelUserIds { get; set; } = [];
}

public sealed class InternalInterviewSession
{
    public long InterviewId { get; set; }
    public string RoomId { get; set; } = "";
    public string LinkVersion { get; set; } = "";
    public DateTime LinkExpiresAtUtc { get; set; }
    public string Status { get; set; } = "Scheduled";
    public string Control { get; set; } = "Human";
    public string ConfigurationJson { get; set; } = "{}";
    public string QuestionsJson { get; set; } = "[]";
    public DateTime? ConsentAtUtc { get; set; }
    public bool RecordingConsent { get; set; }
    public bool TranscriptionConsent { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? EndedAtUtc { get; set; }
    public DateTime? RetainUntilUtc { get; set; }
    public DateTime ScheduledStartUtc { get; set; }
    public DateTime ScheduledEndUtc { get; set; }
    public string MediaState { get; set; } = "None";
    public string MediaError { get; set; } = "";
    public long Revision { get; set; }
    public int CreatedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public InternalInterviewConfiguration Configuration => JsonSerializer.Deserialize<InternalInterviewConfiguration>(ConfigurationJson, Json) ?? new();
    public List<InterviewQuestion> Questions => JsonSerializer.Deserialize<List<InterviewQuestion>>(QuestionsJson, Json) ?? [];
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

public sealed class InternalInterviewRecording
{
    public string Id { get; set; } = "";
    public long InterviewId { get; set; }
    public string Status { get; set; } = "Pending";
    public string StorageKey { get; set; } = "";
    public string ContentType { get; set; } = "video/mp4";
    public string EgressId { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime? EndedAtUtc { get; set; }
    public int CreatedByUserId { get; set; }
}

public sealed class InternalInterviewEvent
{
    public long Id { get; set; }
    public long InterviewId { get; set; }
    public string EventKey { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Actor { get; set; } = "";
    public string Source { get; set; } = "";
    public string Text { get; set; } = "";
    public long? QuestionEventId { get; set; }
    public long? OffsetMs { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public sealed record InterviewConsent(bool Recording, bool Transcription, string NoticeVersion);
public sealed record InterviewEventInput(string EventKey, string Kind, string Text, long? QuestionEventId = null, long? OffsetMs = null);
public sealed record InterviewCommand(string Action, long Revision, string? SectionSkill = null);
public sealed record InterviewScheduleReset(long Revision);
public sealed record InterviewCandidateTicket(long InterviewId, string LinkVersion, DateTime ExpiresAtUtc);
public sealed record InterviewLink(string Url, DateTime ExpiresAtUtc);
public sealed record InterviewAccess(InternalInterviewContext Context, InternalInterviewSession Session, AuthUser? User)
{
    public bool IsCandidate => User is null;
    public string Actor => IsCandidate ? "candidate" : $"panel:{User!.Id}";
}

public sealed class InternalInterviewException(int status, string message) : Exception(message)
{
    public int StatusCode { get; } = status;
}
