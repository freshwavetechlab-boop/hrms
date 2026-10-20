using System.Text;
using System.Text.RegularExpressions;
using Payroll.API.Models;

namespace Payroll.API.Services;

// Select verbatim evidence across the complete resume, rather than clipping its
// first page. This changes only the optional model input; deterministic ATS still
// assesses the full original document and owns mandatory eligibility.
internal static class LocalRecruitmentEvidence
{
    internal static string ScoringResume(RecruitmentAiAnalysisRequest job, string resume)
    {
        if (Encoding.UTF8.GetByteCount(resume) <= 5_000) return resume;
        var paragraphs = Regex.Split(resume, @"[\r\n]+|(?<=[.!?])\s+")
            .Select(line => Regex.Replace(line, @"\s+", " ").Trim()).Where(line => line.Length > 0).ToArray();
        var selected = new SortedSet<int>();
        var queries = job.RequiredSkills.Concat(job.PreferredSkills).Concat(job.RoleRequirements)
            .Concat([job.PositionTitle, job.Qualification, job.Certifications]).Where(value => !string.IsNullOrWhiteSpace(value));
        foreach (var query in queries)
        {
            var terms = Regex.Matches(query, @"[\p{L}\p{N}+#.][\p{L}\p{N}+#.-]{1,}").Select(match => match.Value)
                .Where(term => !StopWords.Contains(term)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var matches = paragraphs.Select((text, index) => new { text, index, hits = terms.Count(term =>
                Regex.IsMatch(text, @"(?<![\p{L}\p{N}])" + Regex.Escape(term) + @"(?![\p{L}\p{N}])", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100))) })
                .Where(row => row.hits > 0).OrderByDescending(row => row.hits).ThenBy(row => row.index).ToArray();
            if (matches.FirstOrDefault() is { } best) selected.Add(best.index);
            // A dense tools list must not crowd out an actual project (or a
            // negative claim) later in the document for the same required skill.
            if (job.RequiredSkills.Contains(query, StringComparer.OrdinalIgnoreCase))
            {
                var work = matches.FirstOrDefault(row => Regex.IsMatch(row.text,
                    @"\b(?:built|designed|implemented|operated|managed|developed|drafted|represented|deployed|configured|never|no experience)\b", RegexOptions.IgnoreCase));
                if (work is not null) selected.Add(work.index);
            }
        }
        foreach (var row in paragraphs.Select((text, index) => new { text, index }).Where(row =>
                     Regex.IsMatch(row.text, @"\b(?:experience|years?|yrs?|months?|certified|certification|bachelor|master|llb|ll\.b|btech|b\.tech|mca|regular|full.time)\b", RegexOptions.IgnoreCase)).Take(12))
            selected.Add(row.index);
        var lines = selected.Select(index => paragraphs[index]).ToArray();
        // Never shorten a chosen evidence statement or silently drop a requirement
        // to meet the gateway cap. An oversized packet retains the existing explicit
        // InputTooLarge/fallback behaviour.
        return "SELECTED VERBATIM EXCERPTS FROM THE COMPLETE RESUME. These are excerpts, not a full copy. "
            + "Absence here is an evidence gap, not proof of ineligibility. HRMS checks the full source separately.\n"
            + string.Join("\n", lines.Select((line, index) => $"[{index + 1}] {line}"));
    }

    internal static string Terminology(RecruitmentDocumentRagContext? context)
    {
        if (context is null) return "";
        var words = context.Vocabulary.Where(row => row.Key is "Skill" or "Qualification" or "Certification")
            .SelectMany(row => row.Value).Where(value => value.Length is > 1 and <= 60 && value.Any(char.IsLetter))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(8);
        return "\nUNTRUSTED TERMINOLOGY ONLY; never candidate/job facts. Accept a value only when copied from SOURCE: " + string.Join("; ", words);
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "or", "with", "for", "from", "have", "has", "such", "ability", "understanding", "knowledge", "experience",
        "required", "preferred", "relevant", "equivalent", "discipline", "degree", "minimum", "years", "year", "familiarity", "strong", "skills"
    };
}
