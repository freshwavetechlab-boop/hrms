using Microsoft.Extensions.Configuration;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Repositories;

public sealed class RecruitmentJobDescriptionFieldLengthTests
{
    [Theory]
    [InlineData("title", 180, "Public role title")]
    [InlineData("skill", 180, "Skill row 1")]
    [InlineData("reviewer", 80, "reviewer level")]
    [InlineData("language", 120, "language name")]
    [InlineData("proficiency", 80, "proficiency")]
    [InlineData("benefit", 180, "benefit name")]
    [InlineData("description", 1000, "description")]
    public async Task Oversized_fixed_fields_are_rejected_before_a_database_connection_without_changing_the_request(string field, int maximum, string label)
    {
        var request = Request();
        Set(request, field, new string('x', maximum + 1));
        var before = System.Text.Json.JsonSerializer.Serialize(request);
        var repository = new RecruitmentPipelineRepository(new ConfigurationBuilder().Build(), null!);
        var result = await repository.SaveJobDescriptionVersionAsync(request, new AuthUser());
        Assert.Null(result.Row);
        Assert.Contains(label, result.Error);
        Assert.Contains(maximum.ToString(), result.Error);
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(request));
    }

    [Theory]
    [InlineData("title", 180)]
    [InlineData("skill", 180)]
    [InlineData("reviewer", 80)]
    [InlineData("language", 120)]
    [InlineData("proficiency", 80)]
    [InlineData("benefit", 180)]
    [InlineData("description", 1000)]
    public void Fixed_field_capacity_counts_unicode_codepoints_after_trimming(string field, int maximum)
    {
        var request = Request();
        Set(request, field, "  " + string.Concat(Enumerable.Repeat("😀", maximum)) + "  ");
        Assert.Equal("", RecruitmentPipelineRepository.ValidateJobDescriptionFieldLengths(request));
    }

    [Theory]
    [InlineData("summary")]
    [InlineData("purpose")]
    [InlineData("responsibility")]
    [InlineData("certification")]
    public void Text_fields_accept_exact_utf8_capacity_and_reject_one_extra_multibyte_character(string field)
    {
        var request = Request();
        var exact = string.Concat(Enumerable.Repeat("अ", 21845));
        Set(request, field, exact);
        Assert.Equal("", RecruitmentPipelineRepository.ValidateJobDescriptionFieldLengths(request));
        Set(request, field, exact + "अ");
        Assert.Contains("65535 UTF-8 bytes", RecruitmentPipelineRepository.ValidateJobDescriptionFieldLengths(request));
    }

    private static SaveRecruitmentJobDescriptionVersion Request() => new()
    {
        RequisitionId = 1, Title = "Synthetic role", Summary = "Synthetic summary",
        Responsibilities = [new() { ResponsibilityText = "Synthetic responsibility" }],
        Skills = [new() { SkillName = "Research", IsRequired = true, MinimumProficiency = "Intermediate" }],
        Languages = [new() { LanguageName = "English", Proficiency = "Professional" }],
        Benefits = [new() { BenefitName = "Synthetic benefit", Description = "Synthetic detail" }],
        Certifications = [new() { CertificationName = "Synthetic certification" }]
    };

    private static void Set(SaveRecruitmentJobDescriptionVersion request, string field, string value)
    {
        switch (field)
        {
            case "title": request.Title = value; break;
            case "summary": request.Summary = value; break;
            case "purpose": request.RolePurpose = value; break;
            case "responsibility": request.Responsibilities[0].ResponsibilityText = value; break;
            case "skill": request.Skills[0].SkillName = value; break;
            case "reviewer": request.Skills[0].MinimumProficiency = value; break;
            case "certification": request.Certifications[0].CertificationName = value; break;
            case "language": request.Languages[0].LanguageName = value; break;
            case "proficiency": request.Languages[0].Proficiency = value; break;
            case "benefit": request.Benefits[0].BenefitName = value; break;
            case "description": request.Benefits[0].Description = value; break;
            default: throw new ArgumentException(field);
        }
    }
}
