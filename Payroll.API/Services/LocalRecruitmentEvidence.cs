using System.Text;
using System.Text.RegularExpressions;
using Payroll.API.Models;

namespace Payroll.API.Services;

// Select verbatim evidence across the complete resume, rather than clipping its
// first page. This changes only the optional model input; deterministic ATS still
// assesses the full original document and owns mandatory eligibility.
internal static class LocalRecruitmentEvidence
{
    internal static string ResumeDocument(string source)
    {
        const int budget = 7_500;
        if (Encoding.UTF8.GetByteCount(source) <= budget) return source;
        var lines = source.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToArray();
        var chosen = new SortedSet<int>();
        var used = 0;
        void Add(int index, int groupLimit, ref int groupBytes)
        {
            if (index < 0 || index >= lines.Length || chosen.Contains(index)) return;
            var size = Encoding.UTF8.GetByteCount(lines[index]) + 1;
            if (used + size > budget || groupBytes + size > groupLimit) return;
            chosen.Add(index); used += size; groupBytes += size;
        }
        var header = 0;
        for (var i = 0; i < Math.Min(lines.Length, 12); i++) Add(i, 1_200, ref header);
        var career = 0;
        foreach (var index in Enumerable.Range(0, lines.Length).Where(i => Regex.IsMatch(lines[i],
                     @"\b(?:present|current|onwards|still working|till date)\b", RegexOptions.IgnoreCase)).Take(4))
            for (var i = index - 2; i <= index + 3; i++) Add(i, 2_500, ref career);
        var education = 0;
        foreach (var index in Enumerable.Range(0, lines.Length).Where(i => Regex.IsMatch(lines[i],
                     @"^(?:education|academic|qualifications?)\b|\b(?:ph\.?d|b\.?tech|m\.?tech|mba|bachelor|master(?:s|'s)? of)\b", RegexOptions.IgnoreCase)).Take(8))
            for (var i = index; i <= index + 2; i++) Add(i, 1_800, ref education);
        var skills = 0;
        foreach (var index in Enumerable.Range(0, lines.Length).Where(i => Regex.IsMatch(lines[i],
                     @"^(?:(?:technical|core|key|professional)\s+)?(?:skills|competencies|strengths|technologies|expertise)\b", RegexOptions.IgnoreCase)).Take(3))
            for (var i = index; i <= index + 6; i++) Add(i, 2_000, ref skills);
        var other = 0;
        for (var i = 0; i < lines.Length; i++) Add(i, budget, ref other);
        return "SELECTED VERBATIM EXCERPTS FROM THE COMPLETE RESUME. Omitted text is an evidence gap, not proof of absence. HRMS validates against the full source.\n"
            + string.Join('\n', chosen.Select(i => lines[i]));
    }

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
