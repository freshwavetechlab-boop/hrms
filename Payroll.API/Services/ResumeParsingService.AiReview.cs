using System.Text.RegularExpressions;
using Payroll.API.Models;

namespace Payroll.API.Services;

public sealed partial class ResumeParsingService
{
    private static (ResumeParsedFacts Facts, IReadOnlyList<ResumeFieldReviewIssue> Issues) ReviewAiProfessionalFacts(
        ResumeParseResult local, RecruitmentAiResumeDocumentSuggestion ai)
    {
        var issues = new List<ResumeFieldReviewIssue>();
        var facts = local.Facts;
        string Review(string field, string key, string existing, string proposed, string evidence, bool groundedContext)
        {
            if (string.IsNullOrWhiteSpace(proposed))
            {
                if (!string.IsNullOrWhiteSpace(existing))
                    issues.Add(new(field, "AiFieldUnverified", "AI did not establish " + field + " from source evidence; the local value still needs review."));
                return existing;
            }
            if (ai.Status == "Completed" && ai.FieldMetadata.TryGetValue(key, out var trace)
                && trace.SourceType.Equals("exact", StringComparison.OrdinalIgnoreCase) && trace.Confidence >= .80m
                && ResumeLearningMemory.Supported(local.Text, field, proposed.Trim())
                && GroundedSnippet(local.Text, evidence, proposed) && groundedContext)
                return proposed.Trim();
            issues.Add(new(field, "AiFieldConflict", "AI could not verify its " + field + " proposal against sufficient source evidence. Review the original resume."));
            return existing;
        }
        var employmentGrounded = IsLatestEmploymentEvidence(local.Text, ai.CurrentEmploymentEvidence);
        facts = facts with
        {
            CurrentCompany = Review("CurrentCompany", "currentCompany", facts.CurrentCompany, ai.CurrentCompany,
                ai.CurrentEmploymentEvidence, employmentGrounded && IsEmployerHeader(ai.CurrentCompany)),
            CurrentTitle = Review("CurrentTitle", "currentTitle", facts.CurrentTitle, ai.CurrentTitle,
                ai.CurrentEmploymentEvidence, employmentGrounded && IsRoleHeader(ai.CurrentTitle)),
            HighestQualification = Review("HighestQualification", "highestQualification", facts.HighestQualification,
                ai.HighestQualification, ai.QualificationEvidence, IsCompletedQualificationEvidence(local.Text, ai.QualificationEvidence))
        };
        if (ai.Status == "Completed" && ai.FieldMetadata.TryGetValue("skills", out var skillsTrace)
            && skillsTrace.SourceType.Equals("exact", StringComparison.OrdinalIgnoreCase) && skillsTrace.Confidence >= .80m)
        {
            var professional = local.Sections.Where(section => section.SectionCode is "SKILLS" or "SUMMARY" or "EXPERIENCE" or "PROJECTS")
                .SelectMany(section => section.Content.Split('\n')).Where(line => !IsUnverifiedSkillClaim(line)).ToArray();
            var skills = (ai.Skills ?? []).Where(skill => ResumeLearningMemory.Supported(local.Text, "Skills", skill)
                && professional.Any(line => ContainsNormalizedEvidence(line, skill)));
            facts = facts with { Skills = facts.Skills.Concat(skills).Distinct(StringComparer.OrdinalIgnoreCase).Take(100).ToList() };
        }
        return (facts, issues);
    }

    private static bool GroundedSnippet(string source, string evidence, string value) => evidence.Length is >= 2 and <= 1000
        && ContainsNormalizedEvidence(source, evidence) && ContainsNormalizedEvidence(evidence, value)
        && !Regex.IsMatch(evidence, @"\b(?:ignore\s+(?:all\s+)?instructions?|system\s+prompt|references?\s+for)\b", RegexOptions.IgnoreCase);

    private static bool IsLatestEmploymentEvidence(string source, string evidence)
    {
        if (string.IsNullOrWhiteSpace(evidence) || evidence.Length > 1000 || !ContainsNormalizedEvidence(source, evidence)) return false;
        var proposed = FlexibleEmploymentDateRangePattern.Matches(evidence).Cast<Match>().ToList();
        if (proposed.Count == 0)
            return Regex.IsMatch(evidence, @"\b(?:current|present)\s+(?:company|employer|organisation|organization|designation|role|position)\s*[:|]", RegexOptions.IgnoreCase);
        var all = FlexibleEmploymentDateRangePattern.Matches(source).Cast<Match>().ToList();
        if (all.Any(match => match.Groups["present"].Success)) return proposed.Any(match => match.Groups["present"].Success);
        DateOnly End(Match match) => DatePart(match.Groups["endMonth"].Value, match.Groups["endYear"].Value, true) ?? DateOnly.MinValue;
        return proposed.Max(End) >= all.Max(End);
    }

    private static bool IsCompletedQualificationEvidence(string source, string evidence) => !string.IsNullOrWhiteSpace(evidence)
        && evidence.Length <= 1000 && ContainsNormalizedEvidence(source, evidence) && IsVerifiedDegree(evidence)
        && !Regex.IsMatch(evidence, @"\b(?:pursuing|pursuance|expected|in\s+progress|ongoing|enrolled|not\s+completed)\b", RegexOptions.IgnoreCase)
        && (BuildSections(source).Any(section => section.SectionCode == "EDUCATION" && ContainsNormalizedEvidence(section.Content, evidence))
            || Regex.IsMatch(evidence, @"\b(?:university|college|graduated|completed|degree|highest\s+qualification)\b", RegexOptions.IgnoreCase));
}
