namespace Payroll.API.Models;

public sealed class CreateSimilarRecruitmentTemplate
{
    public int TargetClientId { get; set; }
    public string TemplateName { get; set; } = "";
    public string TemplateCode { get; set; } = "";
}

public sealed class CreateSimilarRecruitmentForm
{
    public long? SourceVersionId { get; set; }
    public int TargetClientId { get; set; }
    public string FormName { get; set; } = "";
    public string FormCode { get; set; } = "";
}

public sealed class SimilarRecruitmentFormResult
{
    public DynamicFormDefinition Definition { get; set; } = new();
    public DynamicFormVersion Version { get; set; } = new();
    public List<string> Warnings { get; set; } = [];
}
