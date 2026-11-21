using System.Globalization;
using System.Text.RegularExpressions;

namespace Payroll.API.Services;

internal static class EmployeeImportDate
{
    internal static bool TryNormalize(string value, out string date)
    {
        date = "";
        var text = value.Trim().TrimStart('\'').Replace('\u00a0', ' ').Trim();
        if (text.Length == 0) return true;
        var timestamp = Regex.Match(text, @"^(.+?)[T ]([0-9]{1,2}):([0-9]{2})(?::([0-9]{2})(?:\.[0-9]{1,7})?)?(?:Z|[+-](?:0[0-9]|1[0-4]):[0-5][0-9])?$");
        if (timestamp.Success)
        {
            if (int.Parse(timestamp.Groups[2].Value) > 23 || int.Parse(timestamp.Groups[3].Value) > 59 || timestamp.Groups[4].Success && int.Parse(timestamp.Groups[4].Value) > 59) return false;
            text = timestamp.Groups[1].Value.Trim();
        }
        static int Year(string raw) => raw.Length == 2 ? int.Parse(raw, CultureInfo.InvariantCulture) + (int.Parse(raw, CultureInfo.InvariantCulture) <= 29 ? 2000 : 1900) : int.Parse(raw, CultureInfo.InvariantCulture);
        static bool Date(int year, int month, int day, out string result)
        {
            result = "";
            if (year is < 1900 or > 9999 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month)) return false;
            result = new DateTime(year, month, day).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); return true;
        }
        if (Regex.IsMatch(text, @"^[0-9]+(?:\.[0-9]+)?$"))
        {
            if (!double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number)) return false;
            var serial = Math.Floor(number);
            if (serial is < 1 or > 2958465 or 60) return false;
            var parsed = new DateTime(1899, 12, serial < 60 ? 31 : 30).AddDays(serial);
            return Date(parsed.Year, parsed.Month, parsed.Day, out date);
        }
        var iso = Regex.Match(text, @"^([0-9]{4})\s*[-/.]\s*([0-9]{1,2})\s*[-/.]\s*([0-9]{1,2})$");
        if (iso.Success) return Date(Year(iso.Groups[1].Value), int.Parse(iso.Groups[2].Value), int.Parse(iso.Groups[3].Value), out date);
        var numeric = Regex.Match(text, @"^([0-9]{1,2})\s*[-/.]\s*([0-9]{1,2})\s*[-/.]\s*([0-9]{2}|[0-9]{4})$");
        if (numeric.Success)
        {
            var first = int.Parse(numeric.Groups[1].Value); var second = int.Parse(numeric.Groups[2].Value);
            return second > 12 && first <= 12 ? Date(Year(numeric.Groups[3].Value), first, second, out date) : Date(Year(numeric.Groups[3].Value), second, first, out date);
        }
        var months = new[] { "january", "february", "march", "april", "may", "june", "july", "august", "september", "october", "november", "december" };
        var named = Regex.Replace(Regex.Replace(text.ToLowerInvariant(), @"[,./-]+", " "), @"\s+", " ").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (named.Length != 3) return false;
        var dayFirst = char.IsDigit(named[0][0]); var monthToken = named[dayFirst ? 1 : 0]; var dayToken = named[dayFirst ? 0 : 1];
        var month = Array.FindIndex(months, m => monthToken == m || monthToken == m[..3] || m == "september" && monthToken == "sept") + 1;
        return Regex.IsMatch(dayToken, @"^[0-9]{1,2}$") && Regex.IsMatch(named[2], @"^([0-9]{2}|[0-9]{4})$") && Date(Year(named[2]), month, int.Parse(dayToken), out date);
    }
}
