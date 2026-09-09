namespace Payroll.API.Models;

public class NotificationAutomationEvent
{
    public string EventCode { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string ModuleCode { get; set; } = "";
    public string ResourceType { get; set; } = "";
    public string Description { get; set; } = "";
    public string HttpMethod { get; set; } = "";
    public string PathPattern { get; set; } = "";
    public string Lifecycle { get; set; } = "Action";
}

public class NotificationAutomationCatalog
{
    public List<NotificationAutomationEvent> Events { get; set; } = [];
}

public class NotificationStakeholderPreviewRequest
{
    public string EventCode { get; set; } = "";
    public string ResourceType { get; set; } = "";
    public string ResourceId { get; set; } = "";
    public int? ClientId { get; set; }
}

public class NotificationStakeholderOption
{
    public string Code { get; set; } = "";
    public string Label { get; set; } = "";
    public string Description { get; set; } = "";
    public bool IsRelevant { get; set; } = true;
    public List<NotificationStakeholderPerson> People { get; set; } = [];
}

public class NotificationStakeholderPerson
{
    public int? UserId { get; set; }
    public string DisplayName { get; set; } = "";
    public string Email { get; set; } = "";
}

public class NotificationStakeholderPreview
{
    public string EventCode { get; set; } = "";
    public string ResourceType { get; set; } = "";
    public string ResourceId { get; set; } = "";
    public int? ClientId { get; set; }
    public List<NotificationStakeholderOption> Stakeholders { get; set; } = [];
}
