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
    public string EndpointUrl { get; set; } = "";
    public decimal AiBlendWeight { get; set; } = 20m;
    public decimal MinimumConfidence { get; set; } = .65m;
    public int MaximumResumeCharacters { get; set; } = 40_000;
    public int RequestTimeoutSeconds { get; set; } = 45;
    public bool HasApiKey { get; set; }
    public string HealthStatus { get; set; } = "NotTested";
    public string LastHealthMessage { get; set; } = "";
    public DateTime? LastTestedAt { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsPrimary { get; set; }
    public int Priority { get; set; } = 100;
    public int MonthlyRequestLimit { get; set; } = 1000;
    public string UsagePeriod { get; set; } = "";
    public int UsageRequestCount { get; set; }
    public long UsageInputTokens { get; set; }
    public long UsageOutputTokens { get; set; }
    public decimal UsagePercent { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public int ConsecutiveFailureCount { get; set; }
    public DateTime? LastFailureAt { get; set; }
    public long? ProviderRequestLimit { get; set; }
    public long? ProviderRequestsRemaining { get; set; }
    public long? ProviderTokenLimit { get; set; }
    public long? ProviderTokensRemaining { get; set; }
    public string ProviderRequestReset { get; set; } = "";
    public string ProviderTokenReset { get; set; } = "";
    public DateTime? ProviderQuotaObservedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class SaveRecruitmentAiScoringSettings : RecruitmentAiScoringSettings
{
    public string ApiKey { get; set; } = "";
}

public sealed class RecruitmentAiProviderPool
{
    public bool AutoSwitchEnabled { get; set; }
    public List<RecruitmentAiScoringSettings> Models { get; set; } = [];
}

public sealed class SaveRecruitmentAiRuntimeSettings
{
    public bool AutoSwitchEnabled { get; set; }
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

public sealed class RecruitmentAiHiringDocumentSuggestion
{
    public string Status { get; set; } = "NotEnabled";
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public decimal Confidence { get; set; }
    public string Error { get; set; } = "";
    public string PositionTitle { get; set; } = "";
    public string ClientName { get; set; } = "";
    public string Department { get; set; } = "";
    public string BusinessUnit { get; set; } = "";
    public string CostCenter { get; set; } = "";
    public string JobLocation { get; set; } = "";
    public string WorkMode { get; set; } = "";
    public string Project { get; set; } = "";
    public string ExternalPositionCode { get; set; } = "";
    public string SourceReference { get; set; } = "";
    public string SourceAuthority { get; set; } = "";
    public string ExperienceRange { get; set; } = "";
    public string Qualification { get; set; } = "";
    public string HiringType { get; set; } = "";
    public string EmploymentType { get; set; } = "";
    public string PositionCategory { get; set; } = "";
    public string HiringPriority { get; set; } = "";
    public int? NumberOfOpenings { get; set; }
    public bool? IsReplacement { get; set; }
    public bool? BudgetAvailable { get; set; }
    public decimal? BudgetAmount { get; set; }
    public decimal? SalaryMin { get; set; }
    public decimal? SalaryMax { get; set; }
    public string Currency { get; set; } = "";
    public string TargetJoiningDate { get; set; } = "";
    public string BusinessJustification { get; set; } = "";
    public string HiringNotes { get; set; } = "";
    public string RoleSummary { get; set; } = "";
    public string RolePurpose { get; set; } = "";
    public List<string> Responsibilities { get; set; } = [];
    public List<string> RequiredSkills { get; set; } = [];
    public List<string> PreferredSkills { get; set; } = [];
    public List<string> Qualifications { get; set; } = [];
    public List<string> Certifications { get; set; } = [];
    public List<string> Languages { get; set; } = [];
    public List<string> Benefits { get; set; } = [];
    public List<RecruitmentAiHiringSkillSuggestion> SkillRequirements { get; set; } = [];
    public List<RecruitmentAiHiringQualificationSuggestion> QualificationRequirements { get; set; } = [];
    public List<RecruitmentAiHiringCertificationSuggestion> CertificationRequirements { get; set; } = [];
    public List<RecruitmentAiHiringLanguageSuggestion> LanguageRequirements { get; set; } = [];
    public Dictionary<string, RecruitmentAiHiringFieldTrace> FieldMetadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class RecruitmentAiHiringSkillSuggestion
{
    public string SkillName { get; set; } = "";
    public bool IsRequired { get; set; } = true;
    public decimal MinimumYears { get; set; }
    public string Proficiency { get; set; } = "";
    public decimal WeightPercent { get; set; }
    public string SourceType { get; set; } = "inferred";
    public decimal Confidence { get; set; }
}

public sealed class RecruitmentAiHiringQualificationSuggestion
{
    public string QualificationName { get; set; } = "";
    public string Specialization { get; set; } = "";
    public bool IsMandatory { get; set; } = true;
    public string SourceType { get; set; } = "exact";
    public decimal Confidence { get; set; }
}

public sealed class RecruitmentAiHiringCertificationSuggestion
{
    public string CertificationName { get; set; } = "";
    public bool IsMandatory { get; set; }
    public bool ProofRequired { get; set; }
    public string SourceType { get; set; } = "exact";
    public decimal Confidence { get; set; }
}

public sealed class RecruitmentAiHiringLanguageSuggestion
{
    public string LanguageName { get; set; } = "";
    public string Proficiency { get; set; } = "";
    public bool IsMandatory { get; set; }
    public string SourceType { get; set; } = "exact";
    public decimal Confidence { get; set; }
}

public sealed class RecruitmentAiHiringFieldTrace
{
    public string SourceType { get; set; } = "manual";
    public decimal Confidence { get; set; }
}
