using System.Text.Json.Serialization;

namespace Payroll.API.Models;

public class RecruitmentAiScoringSettings
{
    public long Id { get; set; }
    public int ClientId { get; set; }
    public string ClientName { get; set; } = "";
    public bool EnableAiScoring { get; set; }
    public string ProviderCode { get; set; } = "Gemini";
    public string ModelName { get; set; } = "gemini-3.5-flash";
    public decimal AiBlendWeight { get; set; } = 20m;
    public decimal MinimumConfidence { get; set; } = .65m;
    public int MaximumResumeCharacters { get; set; } = 40_000;
    public int RequestTimeoutSeconds { get; set; } = 45;
    public bool HasApiKey { get; set; }
    public string HealthStatus { get; set; } = "NotTested";
    public string LastHealthMessage { get; set; } = "";
    public DateTime? LastTestedAt { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class SaveRecruitmentAiScoringSettings : RecruitmentAiScoringSettings
{
    public string ApiKey { get; set; } = "";
}

public sealed class RecruitmentAiAnalysis
{
    public bool Applied { get; set; }
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public decimal OverallFit { get; set; }
    public decimal Confidence { get; set; }
    public string Summary { get; set; } = "";
    public string Status { get; set; } = "NotEnabled";
    public string Error { get; set; } = "";
    public Dictionary<string, decimal> Criteria { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<RecruitmentAiSkillAssessment> Skills { get; set; } = [];
    public List<string> ReviewFlags { get; set; } = [];
}

public sealed class RecruitmentAiSkillAssessment
{
    public string Skill { get; set; } = "";
    public decimal Confidence { get; set; }
    public string Evidence { get; set; } = "";
    public bool Matched { get; set; }
}

internal sealed class RecruitmentAiScoringSecretRow : RecruitmentAiScoringSettings
{
    [JsonIgnore] public string ApiKeyCipherText { get; set; } = "";
}

public sealed class RecruitmentAiAnalysisRequest
{
    public string PositionTitle { get; set; } = "";
    public string PositionCategory { get; set; } = "";
    public string ExperienceRange { get; set; } = "";
    public string Qualification { get; set; } = "";
    public string Certifications { get; set; } = "";
    public string Location { get; set; } = "";
    public List<string> RequiredSkills { get; set; } = [];
    public List<string> PreferredSkills { get; set; } = [];
    public string ResumeText { get; set; } = "";
}
