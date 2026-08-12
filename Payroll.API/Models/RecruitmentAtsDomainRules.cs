using System.Globalization;
using System.Text.RegularExpressions;

namespace Payroll.API.Models;

public enum RecruitmentAtsEligibilityStatus
{
    Eligible,
    NeedsReview,
    Ineligible
}

public sealed record RecruitmentSkillDurationEvidence(bool IsEstablished, decimal Years, string Evidence)
{
    public static RecruitmentSkillDurationEvidence NotEstablished { get; } = new(false, 0, "");
}

public static class RecruitmentAtsDomainRules
{
    public const string Matched = "Matched";
    public const string Missing = "Missing";
    public const string NeedsReview = "NeedsReview";
    public const string InsufficientExperience = "InsufficientExperience";
    public const string Ineligible = "Ineligible";

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);
    private const string DurationPattern = @"(?<amount>\d{1,2}(?:\.\d{1,2})?)(?:\s*(?:-|\u2013|\u2014)\s*\d{1,2}(?:\.\d{1,2})?)?\s*\+?\s*(?:-\s*)?(?<unit>years?|yrs?|months?|mos?)(?:\s+(?<extraMonths>\d{1,2})\s*(?:months?|mos?))?";

    public static RecruitmentAtsEligibilityStatus EvaluateMustHaveGate(IEnumerable<string> matchStatuses)
    {
        var statuses = matchStatuses.Select(value => (value ?? "").Trim()).ToArray();
        if (statuses.Any(value => value.Equals(Missing, StringComparison.OrdinalIgnoreCase)
            || value.Equals(InsufficientExperience, StringComparison.OrdinalIgnoreCase)))
            return RecruitmentAtsEligibilityStatus.Ineligible;
        if (statuses.Any(value => !value.Equals(Matched, StringComparison.OrdinalIgnoreCase)))
            return RecruitmentAtsEligibilityStatus.NeedsReview;
        return RecruitmentAtsEligibilityStatus.Eligible;
    }

    public static IReadOnlyList<decimal> NormalizeBucketWeights(IEnumerable<decimal> configuredWeights)
    {
        var weights = configuredWeights.Select(value => Math.Max(0, value)).ToArray();
        if (weights.Length == 0) return [];
        var total = weights.Sum();
        if (total <= 0)
        {
            var equalWeight = 1m / weights.Length;
            return weights.Select(_ => equalWeight).ToArray();
        }
        return weights.Select(value => value / total).ToArray();
    }

    public static RecruitmentSkillDurationEvidence FindSkillSpecificDuration(string resumeText, IEnumerable<string> skillTerms)
    {
        if (string.IsNullOrWhiteSpace(resumeText)) return RecruitmentSkillDurationEvidence.NotEstablished;
        var best = RecruitmentSkillDurationEvidence.NotEstablished;
        foreach (var term in skillTerms.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var boundedTerm = $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(term)}(?![\p{{L}}\p{{N}}])";
            var patterns = new[]
            {
                $@"{boundedTerm}\s*(?:(?:hands[- ]on|professional|relevant|commercial)\s+)?experience\s*(?::|-|\u2013|\u2014|\bof\b|\bfor\b)?\s*{DurationPattern}",
                $@"{boundedTerm}\s*(?::|-|\u2013|\u2014)\s*{DurationPattern}",
                $@"{boundedTerm}\s+(?:for|over)\s+{DurationPattern}",
                $@"{DurationPattern}\s+(?:of\s+)?{boundedTerm}",
                $@"{DurationPattern}\s+(?:of\s+)?(?:(?:hands[- ]on|professional|relevant|commercial)\s+)?experience\s+(?:in|with|using)\s+{boundedTerm}",
                $@"experience\s+(?:in|with|using)\s+{boundedTerm}\s*(?:of|for|:|-|\u2013|\u2014)\s*{DurationPattern}"
            };
            foreach (var pattern in patterns)
            {
                try
                {
                    foreach (Match match in Regex.Matches(resumeText, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout))
                    {
                        if (!TryReadYears(match, out var years) || years < best.Years) continue;
                        best = new RecruitmentSkillDurationEvidence(true, years, CompactEvidence(match.Value));
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    // Treat an unbounded/hostile resume fragment as unverified evidence, never as a pass or fail.
                }
            }
        }
        return best;
    }

    private static bool TryReadYears(Match match, out decimal years)
    {
        years = 0;
        if (!decimal.TryParse(match.Groups["amount"].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount)) return false;
        var unit = match.Groups["unit"].Value;
        years = unit.StartsWith("month", StringComparison.OrdinalIgnoreCase) || unit.StartsWith("mo", StringComparison.OrdinalIgnoreCase)
            ? amount / 12m
            : amount;
        if (match.Groups["extraMonths"].Success
            && decimal.TryParse(match.Groups["extraMonths"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var extraMonths))
            years += extraMonths / 12m;
        return years >= 0 && years <= 80;
    }

    private static string CompactEvidence(string value)
    {
        var compact = Regex.Replace(value, @"\s+", " ").Trim();
        return compact.Length <= 500 ? compact : compact[..500];
    }
}
