using System.Text.RegularExpressions;

namespace Payroll.API.Models;

// Missing/ambiguous text evidence goes to a recruiter; it must never become an automatic rejection.
public static class RecruitmentMandatoryEvidence
{
    public static IReadOnlyList<string> ReviewReasons(string resumeText, string qualificationEvidence,
        IReadOnlyList<(string Name, string Specialization)> qualifications, IReadOnlyList<string> certifications)
    {
        var reasons = new List<string>();
        var text = Normalize(resumeText + " " + qualificationEvidence);
        if (qualifications.Count > 1)
            reasons.Add("Mandatory qualification alternatives need recruiter verification: " + string.Join(" / ", qualifications.Select(x => x.Name)) + ".");
        else if (qualifications.Count == 1)
        {
            var qualification = qualifications[0];
            var name = Normalize(qualification.Name);
            var pattern = name switch
            {
                "llb" or "l l b" => @"\b(?:llb|l l b|bachelor of laws)\b",
                "be" or "b e" => @"\b(?:be|b e|bachelor of engineering)\b",
                "b tech" or "btech" => @"\b(?:btech|b tech|bachelor of technology)\b",
                _ when name.StartsWith("bachelor") => @"\b(?:bachelor|bachelors|btech|b tech|b e|bsc|b sc|bcom|b com|bba|bca|llb|l l b)\b",
                _ => @"\b" + Regex.Escape(name) + @"\b"
            };
            if (!Matches(text, pattern)) reasons.Add($"Mandatory qualification '{qualification.Name}' is not established by the resume/profile.");
            else if (!string.IsNullOrWhiteSpace(qualification.Specialization)
                && !Normalize(qualification.Specialization).StartsWith("relevant discipline")
                && !Matches(text, @"\b" + Regex.Escape(Normalize(qualification.Specialization)) + @"\b"))
                reasons.Add($"Qualification mode/discipline '{qualification.Specialization}' needs recruiter verification.");
        }
        foreach (var certification in certifications.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            var alternatives = Regex.Split(certification, @"\s*/\s*|\s+or\s+", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100))
                .Select(Normalize).Where(x => x.Length > 0).ToArray();
            var confirmed = alternatives.Any(alternative => Matches(text, @"\b" + Regex.Escape(alternative) + @"\b")
                && !Matches(text, @"\b(?:pursuing|preparing|planned|expired|not certified)\b.{0,35}\b" + Regex.Escape(alternative) + @"\b")
                && !Matches(text, @"\b" + Regex.Escape(alternative) + @"\b.{0,20}\b(?:expired|pursuing|planned)\b"));
            if (!confirmed) reasons.Add($"Mandatory certification '{certification}' needs evidence of a completed, valid credential.");
        }
        return reasons;
    }

    private static string Normalize(string value) => Regex.Replace(value.ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ", RegexOptions.None, TimeSpan.FromMilliseconds(100)).Trim();
    private static bool Matches(string text, string pattern) => Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
}
