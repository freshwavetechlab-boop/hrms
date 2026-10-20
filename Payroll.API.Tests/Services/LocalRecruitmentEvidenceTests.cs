using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public class LocalRecruitmentEvidenceTests
{
    [Fact]
    public void InventedDegreeAndEmployerClaimsCannotBecomeAcceptedAiEvidence()
    {
        var job = new RecruitmentAiAnalysisRequest { Qualification = "LLB Regular", ResumeText = "LL.B. 2024 60%. Legal drafting experience.", RequiredSkills = ["Legal drafting"] };
        LocalLlmProtocol.GroundScore(new() { Summary = "Legal drafting experience.", ReviewFlags = ["qualification"] }, job);
        Assert.Throws<LocalLlmException>(() => LocalLlmProtocol.GroundScore(new() { Summary = "Incomplete LLB degree (60%)" }, job));
        Assert.Throws<LocalLlmException>(() => LocalLlmProtocol.GroundScore(new() { Summary = "Legal drafting experience.", ReviewFlags = ["No employer names listed"] }, job));
    }

    [Fact]
    public void FlatLocalAnswerIsNormalizedWithoutInventingMissingRatios()
    {
        const string flat = """{"overallFit":0.8,"confidence":0.9,"requiredSkills":0.7,"preferredSkills":1,"experience":1,"qualification":0.6,"certifications":1,"roleSimilarity":0.8,"summary":"Evidence reviewed","reviewFlags":[]}""";
        var normalized = LocalLlmProtocol.NormalizeScore(flat);
        LocalLlmProtocol.ValidateScore(normalized);
        Assert.Contains("\"requiredSkills\":0.7", normalized);
        Assert.Throws<LocalLlmException>(() => LocalLlmProtocol.NormalizeScore(flat.Replace("\"experience\":1,", "")));
        Assert.Throws<LocalLlmException>(() => LocalLlmProtocol.NormalizeScore(flat.Replace("\"requiredSkills\":0.7", "\"requiredSkills\":{\"Ceph\":0.7}")));
    }

    [Fact]
    public void RelevantFinalPageEvidenceIsKeptWithoutInventingOrTruncatingIt()
    {
        const string evidence = "Built and operated a Ceph cluster with 80 OSDs and 400 TB usable capacity.";
        var source = string.Join('\n', Enumerable.Range(0, 150).Select(i => $"Unrelated project narrative {i} with administrative tasks and recurring procedures."))
            + "\nPROFESSIONAL EXPERIENCE (12 Years)\n" + evidence;
        var packet = LocalRecruitmentEvidence.ScoringResume(new RecruitmentAiAnalysisRequest { RequiredSkills = ["Ceph"] }, source);
        Assert.Contains(evidence, packet);
        Assert.Contains("PROFESSIONAL EXPERIENCE (12 Years)", packet);
        Assert.Contains("excerpts, not a full copy", packet);
        Assert.DoesNotContain("Unrelated project narrative 1", packet);
        Assert.DoesNotContain("Kubernetes", packet);
        Assert.True(packet.Length < source.Length);
    }

    [Fact]
    public void ToolsListDoesNotCrowdOutProjectAndNegativeEvidence()
    {
        var filler = string.Join('\n', Enumerable.Range(0, 150).Select(i => $"General administrative duties and project narrative {i}."));
        var source = "SKILLS: Ceph, RADOS, CRUSH\n" + filler + "\nNever operated Ceph in production; laboratory familiarity only.";
        var packet = LocalRecruitmentEvidence.ScoringResume(new() { RequiredSkills = ["Ceph"] }, source);
        Assert.Contains("SKILLS: Ceph, RADOS, CRUSH", packet);
        Assert.Contains("Never operated Ceph in production; laboratory familiarity only.", packet);
    }

    [Fact]
    public void ApprovedRoleResponsibilitiesAreIncludedInModelContextAndEvidenceSelection()
    {
        var job = new RecruitmentAiAnalysisRequest { RoleRequirements = ["Architecture review board governance"] };
        var source = string.Join('\n', Enumerable.Range(0, 150).Select(i => $"General implementation and operational notes {i} without named responsibilities."))
            + "\nChaired the architecture review board and documented governance decisions.";
        var prompt = LocalLlmProtocol.ScoringPrompt(job, source);
        Assert.Contains("Architecture review board governance", prompt);
        Assert.Contains("Chaired the architecture review board", prompt);
    }

    [Fact]
    public void ShortSourceIsPreservedAndScoringTemplateDoesNotOfferZeroAnswers()
    {
        const string resume = "LL.B. Regular. Litigation experience: 5 years.";
        Assert.Equal(resume, LocalRecruitmentEvidence.ScoringResume(new(), resume));
        var prompt = LocalLlmProtocol.ScoringPrompt(new(), resume);
        Assert.Contains(resume, prompt);
        Assert.DoesNotContain("\"confidence\":0.0", prompt);
        Assert.Contains("Do not invent requirements", prompt);
        Assert.Contains("no requirement", prompt);
    }

    [Fact]
    public void UntrustedRagTerminologyCannotSupplyCandidateFacts()
    {
        var context = new RecruitmentDocumentRagContext("local", 20, "Resume", [],
            new Dictionary<string, IReadOnlyList<string>> { ["Skill"] = ["InventedSkill", "Five years", "CISSP2026"], ["Work Location"] = ["InventedCity"] }, new Dictionary<string, string>());
        var prompt = LocalLlmProtocol.ResumePrompt("Synthetic Candidate\nActualSkill", context);
        Assert.Contains("TERMINOLOGY ONLY", prompt);
        Assert.DoesNotContain("InventedCity", prompt);
        var result = new RecruitmentAiResumeDocumentSuggestion { Confidence = .9m, FullName = "Synthetic Candidate", Sections = [new() { SectionCode = "SKILLS", Content = "InventedSkill", Confidence = .9m }] };
        LocalLlmProtocol.GroundResume(result, "Synthetic Candidate\nActualSkill");
        Assert.Empty(result.Sections);
    }
}
