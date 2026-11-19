namespace Payroll.API.Models;

public sealed class SaveRecruitmentPipelineConfiguration
{
    public SaveRecruitmentPipelineDefinition Definition { get; set; } = new();
    public SaveRecruitmentPipelineVersion Configuration { get; set; } = new();
    public string? ExpectedConfigurationHash { get; set; }
}

public sealed record RecruitmentPipelineConfigurationResult(RecruitmentPipelineDefinition Definition, RecruitmentPipelineVersion Version);
