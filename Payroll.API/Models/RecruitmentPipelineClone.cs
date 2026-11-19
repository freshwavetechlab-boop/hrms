namespace Payroll.API.Models;

public sealed class CreateSimilarRecruitmentPipelineRequest
{
    public long SourceVersionId { get; set; }
    public int TargetClientId { get; set; }
    public string PipelineCode { get; set; } = "";
    public string PipelineName { get; set; } = "";
    public string? Description { get; set; }
}

public sealed record RecruitmentPipelineCloneResult(RecruitmentPipelineDefinition Definition,
    RecruitmentPipelineVersion Version, IReadOnlyList<string> Warnings);
