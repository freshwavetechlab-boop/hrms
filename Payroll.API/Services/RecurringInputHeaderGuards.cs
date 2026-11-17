using System.Text.RegularExpressions;

namespace Payroll.API.Services;

internal static class RecurringInputHeaderGuards
{
    // Captured header, display label and reviewed header may repeat. Only the
    // complete monetary label qualifies; mixed/extra identity words still block.
    internal static bool HasContactIdentifier(string header)
    {
        var knownMonetaryLabel = Regex.IsMatch(header.Trim(),
            @"\A(?:(?:telephone|mobile|phone)[_\s-]*(?:allowance|reimbursement)(?:\s*\([A-Z]{1,3}\))?\s*){1,3}\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
            TimeSpan.FromMilliseconds(100));
        return !knownMonetaryLabel && Regex.IsMatch(header, "phone|mobile", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
