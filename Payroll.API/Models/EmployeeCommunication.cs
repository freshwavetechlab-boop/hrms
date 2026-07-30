using System.Text.Json.Serialization;

namespace Payroll.API.Models;

public static class CommunicationChannels
{
    public const string Email = "Email";
    public const string Sms = "Sms";
    public const string WhatsApp = "WhatsApp";

    public static string Normalize(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "email" => Email,
        "sms" => Sms,
        "whatsapp" or "whats_app" or "whats-app" => WhatsApp,
        _ => string.Empty
    };
}

public class CommunicationProviderAccount
{
    public long Id { get; set; }
    public int? ClientId { get; set; }
    public string ClientName { get; set; } = "All clients";
    public string Channel { get; set; } = CommunicationChannels.Sms;
    public string ProviderCode { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string ApiVersion { get; set; } = string.Empty;
    public string SenderId { get; set; } = string.Empty;
    public string PhoneNumberId { get; set; } = string.Empty;
    public string BusinessAccountId { get; set; } = string.Empty;
    public string DefaultCountryCode { get; set; } = "+91";
    public string DefaultLanguageCode { get; set; } = "en";
    public int RequestTimeoutSeconds { get; set; } = 30;
    public int MaximumMessagesPerMinute { get; set; } = 60;
    public bool IsEnabled { get; set; }
    public bool DeliveryPaused { get; set; }
    public string HealthStatus { get; set; } = "NotConfigured";
    public string LastHealthMessage { get; set; } = string.Empty;
    public DateTime? LastTestedAtUtc { get; set; }
    public bool HasApiKey { get; set; }
    public bool HasAccessToken { get; set; }
    public bool HasWebhookSecret { get; set; }
    public string WebhookPath => Id <= 0 || string.IsNullOrWhiteSpace(ProviderCode)
        ? string.Empty
        : $"/api/public/employee-communications/webhooks/{Uri.EscapeDataString(ProviderCode)}?accountId={Id}";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public class SaveCommunicationProviderAccountRequest
{
    public long Id { get; set; }
    public int? ClientId { get; set; }
    public string Channel { get; set; } = CommunicationChannels.Sms;
    public string ProviderCode { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string ApiVersion { get; set; } = string.Empty;
    public string SenderId { get; set; } = string.Empty;
    public string PhoneNumberId { get; set; } = string.Empty;
    public string BusinessAccountId { get; set; } = string.Empty;
    public string DefaultCountryCode { get; set; } = "+91";
    public string DefaultLanguageCode { get; set; } = "en";
    public int RequestTimeoutSeconds { get; set; } = 30;
    public int MaximumMessagesPerMinute { get; set; } = 60;
    public bool IsEnabled { get; set; }
    public bool DeliveryPaused { get; set; }
    public string ApiKey { get; set; } = string.Empty;
    public string AccessToken { get; set; } = string.Empty;
    public string WebhookSecret { get; set; } = string.Empty;
}

public class CommunicationProviderTestResult
{
    public bool Success { get; set; }
    public string Status { get; set; } = "Unavailable";
    public string Message { get; set; } = string.Empty;
    public DateTime TestedAtUtc { get; set; } = DateTime.UtcNow;
}

public class CommunicationTemplate
{
    public long Id { get; set; }
    public int? ClientId { get; set; }
    public string ClientName { get; set; } = "All clients";
    public string Channel { get; set; } = CommunicationChannels.Email;
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string SubjectTemplate { get; set; } = string.Empty;
    public string BodyTemplate { get; set; } = string.Empty;
    public string ProviderTemplateCode { get; set; } = string.Empty;
    public string LanguageCode { get; set; } = "en";
    public bool IsHtml { get; set; }
    public bool IsActive { get; set; } = true;
    public int CreatedByUserId { get; set; }
    public int UpdatedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public List<CommunicationTemplateVariable> Variables { get; set; } = [];
}

public class CommunicationTemplateVariable
{
    public long Id { get; set; }
    public long TemplateId { get; set; }
    public int Position { get; set; }
    public string VariableKey { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string SourceCode { get; set; } = string.Empty;
    public bool IsRequired { get; set; }
    public string FallbackValue { get; set; } = string.Empty;
}

public class CommunicationRecipientOption
{
    public int EmployeeId { get; set; }
    public int ClientId { get; set; }
    public string ClientName { get; set; } = string.Empty;
    public string EmployeeCode { get; set; } = string.Empty;
    public string EmployeeName { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string WorkEmail { get; set; } = string.Empty;
    public string Mobile { get; set; } = string.Empty;
    public string Department { get; set; } = string.Empty;
    public string Designation { get; set; } = string.Empty;
    public int WorkLocationId { get; set; }
    public string WorkLocationName { get; set; } = string.Empty;
    public bool IsActive { get; set; }
}

public class CommunicationRecipientSearchResult
{
    public List<CommunicationRecipientOption> Items { get; set; } = [];
    public int Total { get; set; }
    public int EmailReadyCount { get; set; }
    public int MobileReadyCount { get; set; }
}

public class CommunicationSelectionRequest
{
    public int ClientId { get; set; }
    public long? DraftId { get; set; }
    public string Channel { get; set; } = CommunicationChannels.Email;
    public long? TemplateId { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string SelectionMode { get; set; } = "SelectedEmployees";
    public List<int> EmployeeIds { get; set; } = [];
    public List<int> ToEmployeeIds { get; set; } = [];
    public List<int> CcEmployeeIds { get; set; } = [];
    public List<int> BccEmployeeIds { get; set; } = [];
    public List<int> ExcludedEmployeeIds { get; set; } = [];
    public string Search { get; set; } = string.Empty;
    public List<int> WorkLocationIds { get; set; } = [];
    public List<string> Departments { get; set; } = [];
    public List<string> Designations { get; set; } = [];
}

public class SendEmployeeCommunicationRequest : CommunicationSelectionRequest
{
    public string IdempotencyKey { get; set; } = string.Empty;
}

public class CommunicationPreviewRecipient
{
    public int EmployeeId { get; set; }
    public string RecipientType { get; set; } = "To";
    public string EmployeeCode { get; set; } = string.Empty;
    public string EmployeeName { get; set; } = string.Empty;
    public string Destination { get; set; } = string.Empty;
    public bool IsEligible { get; set; }
    public string ExclusionReason { get; set; } = string.Empty;
    public string SubjectPreview { get; set; } = string.Empty;
    public string BodyPreview { get; set; } = string.Empty;
}

public class CommunicationPreviewResult
{
    public int SelectedCount { get; set; }
    public int EligibleCount { get; set; }
    public int ExcludedCount { get; set; }
    public int MissingDestinationCount { get; set; }
    public int DuplicateDestinationCount { get; set; }
    public string SampleSubject { get; set; } = string.Empty;
    public string SampleBody { get; set; } = string.Empty;
    public List<CommunicationPreviewRecipient> Recipients { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public bool CanSend { get; set; }
}

public class EmployeeCommunicationCampaign
{
    public long Id { get; set; }
    public int ClientId { get; set; }
    public string ClientName { get; set; } = string.Empty;
    public string Channel { get; set; } = CommunicationChannels.Email;
    public long? TemplateId { get; set; }
    public string TemplateName { get; set; } = string.Empty;
    public string SelectionMode { get; set; } = "SelectedEmployees";
    public string SubjectSnapshot { get; set; } = string.Empty;
    public string BodySnapshot { get; set; } = string.Empty;
    public int TotalSelected { get; set; }
    public int TotalEligible { get; set; }
    public int TotalExcluded { get; set; }
    public int TotalQueued { get; set; }
    public int TotalSent { get; set; }
    public int TotalDelivered { get; set; }
    public int TotalRead { get; set; }
    public int TotalFailed { get; set; }
    public string Status { get; set; } = "Draft";
    public string IdempotencyKey { get; set; } = string.Empty;
    public int CreatedByUserId { get; set; }
    public string CreatedByName { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public List<EmployeeCommunicationCampaignFilter> Filters { get; set; } = [];
    public List<EmployeeCommunicationRecipient> Recipients { get; set; } = [];
}

public class EmployeeCommunicationCampaignFilter
{
    public long Id { get; set; }
    public long CampaignId { get; set; }
    public string FilterType { get; set; } = string.Empty;
    public int? IntegerValue { get; set; }
    public string TextValue { get; set; } = string.Empty;
}

public class EmployeeCommunicationRecipient
{
    public long Id { get; set; }
    public long CampaignId { get; set; }
    public int EmployeeId { get; set; }
    public string RecipientType { get; set; } = "To";
    public string EmployeeCode { get; set; } = string.Empty;
    public string EmployeeName { get; set; } = string.Empty;
    public string Destination { get; set; } = string.Empty;
    public string RenderedSubject { get; set; } = string.Empty;
    public string RenderedBody { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";
    public string ExclusionReason { get; set; } = string.Empty;
    public int RetryCount { get; set; }
    public string ProviderMessageId { get; set; } = string.Empty;
    public string ErrorCode { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public long? EmailQueueId { get; set; }
    public long? MessageId { get; set; }
    public DateTime? QueuedAtUtc { get; set; }
    public DateTime? SentAtUtc { get; set; }
    public DateTime? DeliveredAtUtc { get; set; }
    public DateTime? ReadAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public List<CommunicationDeliveryAttempt> Attempts { get; set; } = [];
    public List<CommunicationDeliveryEvent> Events { get; set; } = [];
}

public class EmployeeCommunicationCampaignPage
{
    public List<EmployeeCommunicationCampaign> Items { get; set; } = [];
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}

public class CommunicationDeliveryAttempt
{
    public long Id { get; set; }
    public long MessageId { get; set; }
    public long? CampaignRecipientId { get; set; }
    public int AttemptNumber { get; set; }
    public long? ProviderAccountId { get; set; }
    public string ProviderRequestId { get; set; } = string.Empty;
    public int? HttpStatusCode { get; set; }
    public bool IsSuccess { get; set; }
    public string ErrorCode { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public int SegmentCount { get; set; }
    public decimal Cost { get; set; }
    public string Currency { get; set; } = string.Empty;
    public DateTime AttemptedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
}

public class CommunicationDeliveryEvent
{
    public long Id { get; set; }
    public long MessageId { get; set; }
    public long? CampaignRecipientId { get; set; }
    public long? ProviderAccountId { get; set; }
    public string ProviderEventId { get; set; } = string.Empty;
    public string EventStatus { get; set; } = string.Empty;
    public DateTime OccurredAtUtc { get; set; }
    public DateTime ReceivedAtUtc { get; set; }
}

public class EmployeeCommunicationConversation
{
    public long Id { get; set; }
    public int ClientId { get; set; }
    public int EmployeeId { get; set; }
    public string EmployeeCode { get; set; } = string.Empty;
    public string EmployeeName { get; set; } = string.Empty;
    public string Channel { get; set; } = CommunicationChannels.Email;
    public string Destination { get; set; } = string.Empty;
    public string Status { get; set; } = "Open";
    public int? AssignedUserId { get; set; }
    public string AssignedUserName { get; set; } = string.Empty;
    public string LastMessagePreview { get; set; } = string.Empty;
    public DateTime? LastMessageAtUtc { get; set; }
    public int UnreadCount { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public List<EmployeeCommunicationMessage> Messages { get; set; } = [];
}

public class EmployeeCommunicationMessage
{
    public long Id { get; set; }
    public long ConversationId { get; set; }
    public long? CampaignRecipientId { get; set; }
    public long? ProviderAccountId { get; set; }
    public string Direction { get; set; } = "Outbound";
    public string MessageType { get; set; } = "Text";
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";
    public string ProviderMessageId { get; set; } = string.Empty;
    public long? EmailQueueId { get; set; }
    public int RetryCount { get; set; }
    public string ErrorCode { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public DateTime? SentAtUtc { get; set; }
    public DateTime? DeliveredAtUtc { get; set; }
    public DateTime? ReadAtUtc { get; set; }
    public DateTime? ReceivedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public List<CommunicationMessageAttachment> Attachments { get; set; } = [];
}

public class CommunicationConversationReplyRequest
{
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public long? TemplateId { get; set; }
    public long? DraftId { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
}

public class EmployeeCommunicationDraft
{
    public long Id { get; set; }
    public int ClientId { get; set; }
    public string Channel { get; set; } = CommunicationChannels.Email;
    public string Status { get; set; } = "Draft";
    public int CreatedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? ConsumedAtUtc { get; set; }
}

public class CreateEmployeeCommunicationDraftRequest
{
    public int ClientId { get; set; }
    public string Channel { get; set; } = CommunicationChannels.Email;
}

public class CommunicationMessageAttachment
{
    public long Id { get; set; }
    public long MessageId { get; set; }
    public long EntityAttachmentId { get; set; }
    public Guid PublicId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
}

public class CommunicationWebhookRequest
{
    public string ProviderEventId { get; set; } = string.Empty;
    public string ProviderMessageId { get; set; } = string.Empty;
    public string Direction { get; set; } = "Status";
    public string Channel { get; set; } = string.Empty;
    public string Sender { get; set; } = string.Empty;
    public string Recipient { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public DateTime? OccurredAtUtc { get; set; }
}

public class CommunicationWebhookResult
{
    public bool Accepted { get; set; }
    public bool Duplicate { get; set; }
    public long? ConversationId { get; set; }
    public long? MessageId { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class CommunicationProviderContext
{
    public CommunicationProviderAccount Account { get; set; } = new();
    [JsonIgnore] public string ApiKey { get; set; } = string.Empty;
    [JsonIgnore] public string AccessToken { get; set; } = string.Empty;
    [JsonIgnore] public string WebhookSecret { get; set; } = string.Empty;
}

public record CommunicationSendCommand(string Destination, string Subject, string Body, string? ProviderTemplateCode, string LanguageCode);
public record CommunicationSendResult(bool Success, string ProviderMessageId, string ProviderRequestId, int? HttpStatusCode, string ErrorCode, string ErrorMessage, int SegmentCount = 0, decimal Cost = 0, string Currency = "");

public interface ICommunicationChannelSender
{
    string Channel { get; }
    string ProviderCode { get; }
    Task<CommunicationProviderTestResult> TestAsync(CommunicationProviderContext context, CancellationToken cancellationToken);
    Task<CommunicationSendResult> SendAsync(CommunicationProviderContext context, CommunicationSendCommand command, CancellationToken cancellationToken);
}
