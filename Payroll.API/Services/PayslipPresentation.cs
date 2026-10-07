using System.Text.RegularExpressions;
using Payroll.API.Models;

namespace Payroll.API.Services;

internal static class PayslipPresentation
{
    private static readonly HashSet<string> NonLeaveCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Present", "P", "WO", "Weekly Off", "Week Off", "H", "Holiday", "A", "Absent"
    };

    internal static decimal LeaveDays(IEnumerable<PayRunLeaveBreakdown> rows) =>
        rows.Where(row => !NonLeaveCodes.Contains(row.Code.Trim())).Sum(row => Math.Max(0, row.Days));

    // Account identifiers stay as text: no floating-point conversion or guessed digits.
    internal static string AccountNumber(string? value)
    {
        var text = (value ?? "").Trim();
        var match = Regex.Match(text, @"^\+?(\d+)(?:\.(\d+))?[eE]([+-]?\d{1,3})$");
        if (!match.Success) return text;
        var digits = match.Groups[1].Value + match.Groups[2].Value;
        var point = match.Groups[1].Length + int.Parse(match.Groups[3].Value);
        if (point <= 0 || point > 80 || (point < digits.Length && digits[point..].Any(character => character != '0')))
            return text;
        return point >= digits.Length ? digits.PadRight(point, '0') : digits[..point];
    }
}
