using System.Text.RegularExpressions;

namespace Payroll.API.Models;

// Missing/ambiguous text evidence goes to a recruiter; it must never become an automatic rejection.
public static class RecruitmentMandatoryEvidence
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(100);
    public static IReadOnlyList<string> ReviewReasons(string resumeText, string qualificationEvidence,
        IReadOnlyList<(string Name, string Specialization)> qualifications, IReadOnlyList<string> certifications)
    {
        var reasons = new List<string>();
        // "be" and "me" in ordinary prose are not engineering degrees. Their
        // compact forms are accepted in the explicit qualification field only.
        var text = Regex.Replace(Normalize(resumeText), @"\b(?:be|me)\b", "", RegexOptions.IgnoreCase, Timeout)
            + " " + Normalize(qualificationEvidence);
        if (qualifications.Count > 1)
            reasons.Add("Mandatory qualification alternatives need recruiter verification: " + string.Join(" / ", qualifications.Select(x => x.Name)) + ".");
        else if (qualifications.Count == 1)
        {
            var qualification = qualifications[0];
            var name = Normalize(qualification.Name);
            var pattern = QualificationPattern(name);
            var alternatives = DegreePatterns.Where(row => Matches(name, row.Name)).Select(row => row.Evidence).Distinct().ToArray();
            // Slash/or within one saved requirement is explicit. Separate mandatory
            // rows remain ambiguous; do not silently reinterpret AND as OR.
            if (Regex.IsMatch(name, @"\b(?:and|plus)\b", RegexOptions.IgnoreCase, Timeout) && alternatives.Length > 1)
            {
                reasons.Add($"Combined qualification '{qualification.Name}' needs recruiter verification.");
                pattern = @"(?!)";
            }
            if (!Matches(text, pattern)) reasons.Add($"Mandatory qualification '{qualification.Name}{(string.IsNullOrWhiteSpace(qualification.Specialization) ? "" : " (" + qualification.Specialization + ")")}' is not established by the resume/profile.");
            else if (!string.IsNullOrWhiteSpace(qualification.Specialization)
                && !Normalize(qualification.Specialization).StartsWith("relevant discipline")
                && !SpecializationMatches(resumeText, qualificationEvidence, pattern, qualification.Specialization))
                reasons.Add($"Qualification mode/discipline '{qualification.Specialization}' needs recruiter verification.");
        }
        foreach (var certification in certifications.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            var alternatives = Regex.Split(certification, @"\s*/\s*|\s+or\s+", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100))
                .Select(Normalize).Where(x => x.Length > 0).ToArray();
            var confirmed = alternatives.Any(alternative => Matches(text, @"\b" + Regex.Escape(alternative) + @"\b")
                && !Matches(text, @"\b(?:pursuing|preparing|planned|expired|not certified|not yet certified|no certification|interested in)\b.{0,35}\b" + Regex.Escape(alternative) + @"\b")
                && !Matches(text, @"\b" + Regex.Escape(alternative) + @"\b.{0,20}\b(?:expired|pursuing|planned)\b"));
            if (!confirmed) reasons.Add($"Mandatory certification '{certification}' needs evidence of a completed, valid credential.");
        }
        return reasons;
    }

    private static readonly (string Name, string Evidence)[] DegreePatterns =
    [
        (@"\b(?:llb|ll b|l l b|bachelor of laws)\b", @"\b(?:llb|ll b|l l b|bachelor of laws)\b"),
        (@"\b(?:be|b e|bachelor of engineering)\b", @"\b(?:be|b e|bachelor of engineering)\b"),
        (@"\b(?:btech|b tech|bachelor of technology)\b", @"\b(?:btech|b tech|bachelor of technology)\b"),
        (@"\b(?:mtech|m tech|master of technology)\b", @"\b(?:mtech|m tech|master of technology)\b"),
        (@"\b(?:me|m e|master of engineering)\b", @"\b(?:me|m e|master of engineering)\b"),
        (@"\b(?:mca|master of computer applications)\b", @"\b(?:mca|master of computer applications)\b"),
        (@"\b(?:msc|m sc|master of science)\b", @"\b(?:msc|m sc|master of science)\b")
    ];

    private static string QualificationPattern(string name)
    {
        var alternatives = DegreePatterns.Where(row => Matches(name, row.Name)).Select(row => row.Evidence).Distinct().ToArray();
        if (alternatives.Length > 0) return "(?:" + string.Join("|", alternatives) + ")";
        return name switch
            {
                _ when name.StartsWith("bachelor") => @"\b(?:bachelor|bachelors|btech|b tech|b e|bsc|b sc|bcom|b com|bba|bca|llb|l l b)\b",
                _ => @"\b" + Regex.Escape(name) + @"\b"
            };
    }

    private static bool SpecializationMatches(string resume, string qualification, string degreePattern, string expected)
    {
        var normalized = Normalize(resume);
        var windows = Regex.Matches(normalized, degreePattern, RegexOptions.IgnoreCase, Timeout)
            .Select(match => normalized.Substring(Math.Max(0, match.Index - 80), Math.Min(normalized.Length, match.Index + match.Length + 180) - Math.Max(0, match.Index - 80)))
            .Append(Normalize(qualification)).ToArray();
        var value = Normalize(expected);
        if (value == "regular")
        {
            return windows.Any(window => Matches(window, @"\b(?:regular|full time)\b")
                && !Matches(window, @"\b(?:regular workouts?|regularly|not regular|part time|distance learning|correspondence)\b"));
        }
        var options = Regex.Split(expected, @",|/|\bor\b", RegexOptions.IgnoreCase, Timeout).Select(Normalize)
            .Where(option => option.Length > 2 && !Matches(option, @"\b(?:related|relevant|allied)\b")).ToArray();
        return windows.Any(window => Matches(window, @"\b" + Regex.Escape(value) + @"\b")
            || options.Any(option => Matches(window, @"\b" + Regex.Escape(option) + @"\b")));
    }

    private static string Normalize(string value) => Regex.Replace(value.ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ", RegexOptions.None, TimeSpan.FromMilliseconds(100)).Trim();
    private static bool Matches(string text, string pattern) => Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
}
