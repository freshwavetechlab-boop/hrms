namespace Payroll.API.Models;

public class FrevoPilotChatThreadSummary
{
    public Guid ThreadId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string ClientCode { get; set; } = string.Empty;
    public string ClientName { get; set; } = string.Empty;
    public string JourneyId { get; set; } = string.Empty;
    public string Status { get; set; } = "Draft";
    public string RunId { get; set; } = string.Empty;
    public int MessageCount { get; set; }
    public int Revision { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class FrevoPilotChatThread : FrevoPilotChatThreadSummary
{
    public List<string> SelectedJourneyIds { get; set; } = [];
    public List<string> ConfirmedFieldIds { get; set; } = [];
    public List<FrevoPilotChatMessage> Messages { get; set; } = [];
    public List<FrevoPilotChatAnswer> Answers { get; set; } = [];
}

public sealed class FrevoPilotChatMessage
{
    public int Sequence { get; set; }
    public string Role { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string Meta { get; set; } = string.Empty;
    public DateTime OccurredAtUtc { get; set; }
}

public sealed class FrevoPilotChatAnswer
{
    public string FieldKey { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public bool IsConfirmed { get; set; }
}

public sealed class SaveFrevoPilotChatThreadRequest
{
    public string Title { get; set; } = string.Empty;
    public string ClientCode { get; set; } = string.Empty;
    public string ClientName { get; set; } = string.Empty;
    public string JourneyId { get; set; } = string.Empty;
    public string Status { get; set; } = "Draft";
    public string RunId { get; set; } = string.Empty;
    public List<string> SelectedJourneyIds { get; set; } = [];
    public List<string> ConfirmedFieldIds { get; set; } = [];
    public List<FrevoPilotChatMessage> Messages { get; set; } = [];
    public List<FrevoPilotChatAnswer> Answers { get; set; } = [];
}

public sealed class FrevoPilotChatStorageStatus
{
    public bool Available { get; set; }
    public string Message { get; set; } = string.Empty;
    public string ActiveStorageName { get; set; } = string.Empty;
    public string ActiveStorageType { get; set; } = string.Empty;
    public string Folder { get; set; } = "FrevoPilot/Threads";
}
