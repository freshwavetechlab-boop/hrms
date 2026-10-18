using System.Globalization;
using System.Text.RegularExpressions;
using Payroll.API.Models;

namespace Payroll.API.Services;

// One employee may have separately calculated salary rows after a location transfer.
// Row IDs remain the payslip/delivery identity; a shared code needs stronger evidence.
public static class ExcelPayslipRowIdentity
{
    public static bool IsVerifiedLocationSplit(IEnumerable<ExcelPayslipRow> source, string? month = null)
    {
        var rows = source.ToArray();
        if (rows.Length < 2 || rows.Select(row => NormalizeName(row.EmployeeName)).Distinct().Count() != 1
            || string.IsNullOrWhiteSpace(rows[0].EmployeeName)) return false;
        var locations = rows.Select(row => row.Information.Where(item => IsLocation(item.Label))
            .Select(item => Normalize(item.Value)).Where(value => value.Length > 0).Distinct().ToArray()).ToArray();
        if (locations.Any(values => values.Length != 1)) return false;
        var commonIdentifier = false;
        foreach (var kind in new[] { "UAN", "PAN", "AADHAAR" })
        {
            var values = rows.Select(row => row.Information.Where(item => IdentifierKind(item.Label) == kind)
                .Select(item => Identifier(kind, item.Value)).Where(value => value.Length > 0).Distinct().ToArray()).ToArray();
            if (values.Any(items => items.Length > 1) || values.SelectMany(items => items).Distinct().Count() > 1) return false;
            commonIdentifier |= values.All(items => items.Length == 1);
        }
        if (!commonIdentifier) return false;
        if (locations.Select(values => values[0]).Distinct().Count() == rows.Length) return true;
        // A transfer workbook can retain the old work-location text. Keep it intact;
        // accept separate source payroll units only after an explicit import review.
        if (!DateTime.TryParseExact(month, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var period)) return false;
        string Single(ExcelPayslipRow row, string label)
        {
            var values = row.Information.Where(i => Label(i.Label) == label).Select(i => Normalize(i.Value)).Where(v => v.Length > 0).Distinct().ToArray();
            return values.Length == 1 ? values[0] : "";
        }
        var units = rows.Select(r => Single(r, "PAYROLLUNIT")).ToArray();
        var workbooks = rows.Select(r => Single(r, "SOURCEWORKBOOK")).ToArray();
        if (units.Any(v => v.Length == 0) || units.Distinct().Count() != rows.Length
            || workbooks.Any(v => v.Length == 0) || workbooks.Distinct().Count() != rows.Length
            || rows.Any(r => Single(r, "REVIEWEDSOURCESPLIT") != "CONFIRMED"
                || !int.TryParse(Single(r, "SOURCEEXCELROW"), out var sourceRow) || sourceRow < 1)) return false;
        var days = rows.Select(r => Single(r, "ATTENDANCE")).ToArray();
        var payable = new List<decimal>();
        foreach (var value in days)
        {
            if (!decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var day) || day <= 0) return false;
            payable.Add(day);
        }
        return payable.Sum() <= DateTime.DaysInMonth(period.Year, period.Month);
    }

    private static string Normalize(string value) => ExcelPayslipVarianceService.NormalizeText(value);
    public static string NormalizeName(string value) => Regex.Replace(Normalize(value), @"^(?:(?:SHRI|SMT|MR|MRS|MS|DR)\.?\s+)+", "");
    private static string Label(string value) => Regex.Replace(Normalize(value), "[^A-Z0-9]", "");
    private static bool IsLocation(string label) => Label(label) is "WORKLOCATION" or "WORKLOCATIONNAME" or "LOCATION" or "LOCATIONNAME" or "LOCATIONNAMETEHSILSUBTEHSIL" or "TEHSILSUBTEHSIL";
    private static string IdentifierKind(string label) => Label(label) switch
    {
        "UAN" or "UANNO" or "UANNUMBER" => "UAN",
        "PAN" or "PANNO" or "PANNUMBER" => "PAN",
        "AADHAAR" or "AADHAARNO" or "AADHAARNUMBER" or "AADHAR" or "AADHARNO" or "AADHARNUMBER"
            or "ADHAAR" or "ADHAARNO" or "ADHAARNUMBER" => "AADHAAR",
        _ => ""
    };
    private static string Identifier(string kind, string value)
    {
        var normalized = Regex.Replace(Normalize(value), @"[\s-]", "");
        return Regex.IsMatch(normalized, kind == "PAN" ? "^[A-Z]{5}[0-9]{4}[A-Z]$" : "^[0-9]{12}$")
            && normalized != "000000000000" ? normalized : "";
    }
}
