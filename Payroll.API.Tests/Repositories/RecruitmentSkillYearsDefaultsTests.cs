using System.Reflection;
using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Repositories;

public sealed class RecruitmentSkillYearsDefaultsTests
{
    [Theory]
    [InlineData("Parsed", "Completed", true, "Completed")]
    [InlineData("Parsed", "NeedsReview", true, "NeedsReview")]
    [InlineData("Parsed", "Queued", true, "Processing")]
    [InlineData("Parsed", "", true, "Processing")]
    [InlineData("Parsed", "", false, "Completed")]
    [InlineData("Failed", "Completed", true, "NeedsReview")]
    public void Public_tracking_reports_real_processing_state(string resume, string ats, bool auto, string expected) =>
        Assert.Equal(expected, RecruitmentFormRepository.PublicProcessingSummary(resume, ats, auto).Status);

    [Fact]
    public void Automatic_job_keeps_required_skills_without_copying_career_years_to_each_skill()
    {
        var request = new RecruitmentRequisition { Id = 1, PositionTitle = "Software Architect", ExperienceRange = "5+ years",
            RequiredSkills = "Kubernetes, C#", PreferredSkills = "Terraform" };
        var jd = RecruitmentPipelineRepository.BuildJobDescriptionFromRequisition(request);
        Assert.Equal(3, jd.Skills.Count);
        Assert.All(jd.Skills, skill => Assert.Equal(0, skill.MinimumYears));
        Assert.True(jd.Skills.Single(skill => skill.SkillName == "Kubernetes").IsRequired);
        Assert.False(jd.Skills.Single(skill => skill.SkillName == "Terraform").IsRequired);
        Assert.Equal("5+ years", request.ExperienceRange);
    }

    [Theory]
    [InlineData(true, 0, "Worked with Kubernetes", "Matched")]
    [InlineData(false, 0, "Worked with Linux", "Missing")]
    [InlineData(true, 3, "Worked with Kubernetes", "NeedsReview")]
    [InlineData(true, 3, "Kubernetes: 2 years", "InsufficientExperience")]
    [InlineData(true, 3, "Kubernetes: 4 years", "Matched")]
    public void Existing_scoring_gate_respects_presence_only_or_explicit_manual_duration(bool hasSkill, decimal years, string resume, string expected)
    {
        // Exercise the existing private scoring evaluator without widening its runtime API.
        var type = typeof(RecruitmentTalentRepository).GetNestedType("CalculatedSkillMatch", BindingFlags.NonPublic)!;
        var skill = JsonSerializer.Deserialize(JsonSerializer.Serialize(new { HasSkillEvidence = hasSkill,
            SkillName = "Kubernetes", SkillType = "Required", MinimumYears = years, DurationTerms = new[] { "Kubernetes" }, EvidenceExcerpt = "" }), type);
        var method = typeof(RecruitmentTalentRepository).GetMethod("EvaluateSkillRequirement", BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = method.Invoke(null, [resume, skill]);
        Assert.Equal(expected, type.GetProperty("MatchStatus")!.GetValue(result));
    }
}
