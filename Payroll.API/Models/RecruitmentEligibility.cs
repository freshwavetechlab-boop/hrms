using System.Globalization;
using System.Text.RegularExpressions;

namespace Payroll.API.Models;

public sealed record RecruitmentEligibilityAssessment(string Status, IReadOnlyList<string> Reasons, int? MinimumExperienceMonths)
{
    public bool IsEligible => Status == "Eligible";
}

public static class RecruitmentEligibility
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(100);

    public static string InterviewSchedulingBlockReason(RecruitmentCandidateApplication application)
    {
        if (application.EligibilityStatus != "Eligible")
            return "Interview scheduling requires verified eligibility. " + (application.EligibilityReasons.Count > 0
                ? string.Join(" ", application.EligibilityReasons) : "Review the JD requirements and candidate evidence first.");
        if (!application.AtsScore.HasValue || application.AtsScore < (application.AtsShortlistThreshold > 0 ? application.AtsShortlistThreshold : 60))
            return "Interview scheduling requires an ATS score meeting this application's shortlist cutoff.";
        return "";
    }

    public static int? MinimumExperienceMonths(string? requirement)
    {
        if (string.IsNullOrWhiteSpace(requirement)) return 0;
        var value = requirement.Trim();
        if (Regex.IsMatch(value, @"^(?:fresher(?:s)?|no experience(?: required)?|not required|any|n/?a)$", RegexOptions.IgnoreCase, Timeout)) return 0;
        // Conditional qualification/experience alternatives need a recruiter, not a guessed minimum.
        if (Regex.IsMatch(value, @"\b(?:or|depending|if|otherwise)\b", RegexOptions.IgnoreCase, Timeout)) return null;
        var match = Regex.Match(value, @"(?<![\d.\-])(?<amount>\d{1,2}(?:\.\d{1,2})?)\s*(?:\+|(?:-|–|—|\bto\b)\s*\d{1,2}(?:\.\d{1,2})?)?\s*(?<unit>years?|yrs?|months?|mos?)\b(?:\s*(?:and\s*)?(?<extra>\d{1,2})\s*months?\b)?", RegexOptions.IgnoreCase, Timeout);
        if (!match.Success || value.TrimStart().StartsWith('-')) return null;
        var amount = decimal.Parse(match.Groups["amount"].Value, CultureInfo.InvariantCulture);
        var months = match.Groups["unit"].Value.StartsWith("m", StringComparison.OrdinalIgnoreCase) ? amount : amount * 12;
        if (match.Groups["extra"].Success) months += int.Parse(match.Groups["extra"].Value, CultureInfo.InvariantCulture);
        return months <= 80 * 12 ? decimal.ToInt32(decimal.Ceiling(months)) : null;
    }

    public static RecruitmentEligibilityAssessment Assess(int? experienceMonths, string? experienceRequirement,
        IEnumerable<string>? mustHaveStatuses = null, string scoreStatus = "", bool hasAssessment = true,
        IEnumerable<string>? additionalReviewReasons = null)
    {
        var reasons = new List<string>();
        var review = new List<string>();
        var minimum = MinimumExperienceMonths(experienceRequirement);
        if (minimum is null) review.Add($"Experience requirement '{experienceRequirement}' needs recruiter verification.");
        else if (minimum > 0)
        {
            if (!experienceMonths.HasValue || experienceMonths < 0) review.Add($"Minimum total experience is {Duration(minimum.Value)}; resume experience is not established.");
            else if (experienceMonths < minimum) reasons.Add($"Minimum total experience is {Duration(minimum.Value)}; resume shows {Duration(experienceMonths.Value)}.");
        }

        var statuses = (mustHaveStatuses ?? []).ToArray();
        // No keyword match is missing evidence, not proof that the candidate lacks the skill.
        var gate = RecruitmentAtsDomainRules.EvaluateMustHaveGate(statuses.Select(status =>
            status.Equals(RecruitmentAtsDomainRules.Missing, StringComparison.OrdinalIgnoreCase) ? RecruitmentAtsDomainRules.NeedsReview : status));
        if (gate == RecruitmentAtsEligibilityStatus.Ineligible)
            reasons.Add(statuses.Contains(RecruitmentEligibilityReviewPolicy.ConfirmedNotMet)
                ? "A recruiter-confirmed mandatory requirement is not met." : "One or more mandatory skill requirements are not met. Review the supporting evidence.");
        else if (scoreStatus.Equals(RecruitmentAtsDomainRules.Ineligible, StringComparison.OrdinalIgnoreCase) && reasons.Count == 0
            && !statuses.Any(status => status.Equals(RecruitmentAtsDomainRules.Missing, StringComparison.OrdinalIgnoreCase)))
            reasons.Add("The recorded assessment contains an unmet mandatory requirement. Review the supporting evidence.");
        else if (gate == RecruitmentAtsEligibilityStatus.NeedsReview || scoreStatus.Equals(RecruitmentAtsDomainRules.NeedsReview, StringComparison.OrdinalIgnoreCase))
            review.Add("Mandatory eligibility evidence needs recruiter verification.");
        review.AddRange((additionalReviewReasons ?? []).Where(x => !string.IsNullOrWhiteSpace(x)));
        var status = reasons.Count > 0 ? "Ineligible" : review.Count > 0 ? "NeedsReview" : hasAssessment ? "Eligible" : "NotAssessed";
        if (status == "Eligible" && minimum > 0 && experienceMonths.HasValue)
            reasons.Add($"Minimum total experience is {Duration(minimum.Value)}; resume shows {Duration(experienceMonths.Value)} and meets the minimum. Mandatory skill checks passed.");
        return new(status, reasons.Concat(review).Distinct().ToArray(), minimum);
    }

    public static string Duration(int months) => months % 12 == 0
        ? $"{months / 12} {(months == 12 ? "year" : "years")}" : $"{months / 12} years {months % 12} months";
}
