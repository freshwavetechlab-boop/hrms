namespace Payroll.API.Models;

public sealed record WeeklyOffRuleVersion
{
    public string VersionId { get; init; } = "";
    public int VersionNumber { get; init; }
    public DateTime EffectiveFrom { get; init; }
    public string Mode { get; init; } = "Paid";
    public string Reason { get; init; } = "";
    public string PublishedBy { get; init; } = "";
    public DateTime PublishedAt { get; init; }
    public Dictionary<string, string> WorkWeekConfigs { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class PublishWeeklyOffRuleRequest
{
    public int ClientId { get; set; }
    public DateTime EffectiveFrom { get; set; }
    public string Mode { get; set; } = "Paid";
    public string Reason { get; set; } = "";
    [System.Text.Json.Serialization.JsonRequired]
    public int ExpectedLatestVersion { get; set; }
}
