namespace Payroll.API.Models;

public class NotificationSmtpSetting
{
    public int Id { get; set; } = 1;
    public bool IsEnabled { get; set; }
    public bool DeliveryPaused { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public string UserName { get; set; } = "";
    public string Password { get; set; } = "";
    public bool EnableSsl { get; set; } = true;
    public string FromEmail { get; set; } = "";
    public string FromName { get; set; } = "";
}

public class NotificationTemplate
{
    public long Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string SubjectTemplate { get; set; } = "";
    public string BodyTemplate { get; set; } = "";
    public bool IsHtml { get; set; } = true;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class NotificationRule
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string EventCode { get; set; } = "";
    public int? ClientId { get; set; }
    public string ClientName { get; set; } = "";
    public long TemplateId { get; set; }
    public string TemplateName { get; set; } = "";
    public bool IsEnabled { get; set; } = true;
    public string ConditionJson { get; set; } = "{}";
    public List<NotificationRecipient> Recipients { get; set; } = [];
    public List<NotificationParameterMapping> Parameters { get; set; } = [];
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class NotificationRecipient
{
    public long Id { get; set; }
    public long RuleId { get; set; }
    public string RecipientType { get; set; } = "To";
    public string SourceType { get; set; } = "StaticEmail";
    public string SourceValue { get; set; } = "";
    public string TableName { get; set; } = "";
    public string MatchColumn { get; set; } = "";
    public string MatchValueSource { get; set; } = "resourceId";
    public string EmailColumn { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

public class NotificationParameterMapping
{
    public long Id { get; set; }
    public long RuleId { get; set; }
    public string ParameterName { get; set; } = "";
    public string SourceType { get; set; } = "Payload";
    public string PayloadPath { get; set; } = "";
    public string TableName { get; set; } = "";
    public string MatchColumn { get; set; } = "";
    public string MatchValueSource { get; set; } = "resourceId";
    public string ValueColumn { get; set; } = "";
    public string DefaultValue { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

public class NotificationQueueItem
{
    public long Id { get; set; }
    public long? RuleId { get; set; }
    public string EventCode { get; set; } = "";
    public string ResourceType { get; set; } = "";
    public string ResourceId { get; set; } = "";
    public int? ClientId { get; set; }
    public string ToJson { get; set; } = "[]";
    public string CcJson { get; set; } = "[]";
    public string BccJson { get; set; } = "[]";
    public string Subject { get; set; } = "";
    public string BodyHtml { get; set; } = "";
    public string Status { get; set; } = "Pending";
    public int RetryCount { get; set; }
    public string ErrorMessage { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? SentAt { get; set; }
}

public class NotificationLog
{
    public long Id { get; set; }
    public long QueueId { get; set; }
    public string EventCode { get; set; } = "";
    public string ResourceType { get; set; } = "";
    public string ResourceId { get; set; } = "";
    public string Recipient { get; set; } = "";
    public string Status { get; set; } = "";
    public string ProviderMessageId { get; set; } = "";
    public string ErrorMessage { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public class NotificationSetup
{
    public NotificationSmtpSetting Smtp { get; set; } = new();
    public List<NotificationTemplate> Templates { get; set; } = [];
    public List<NotificationRule> Rules { get; set; } = [];
    public List<NotificationQueueItem> Queue { get; set; } = [];
    public List<NotificationLog> Logs { get; set; } = [];
}

public class NotificationEvent
{
    public string EventCode { get; set; } = "";
    public string ResourceType { get; set; } = "";
    public string ResourceId { get; set; } = "";
    public int? ClientId { get; set; }
    public int ActorUserId { get; set; }
    public string ActorName { get; set; } = "";
    public string ActorEmail { get; set; } = "";
    public string PayloadJson { get; set; } = "{}";
}

public class NotificationTestRequest
{
    public long RuleId { get; set; }
    public string ToEmail { get; set; } = "";
}
