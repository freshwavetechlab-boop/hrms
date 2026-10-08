using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;
using System.Text.RegularExpressions;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayslipRepository
{
    // Read identity only. Salary, bank details and employee records are never imported or changed.
    private static async Task<MasterEmployeeIdentity[]> ReadEmployeeCodeMatchesAsync(MySqlConnection db, int clientId, MySqlTransaction tx) =>
        (await db.QueryAsync<MasterEmployeeIdentity>(@"
SELECT e.Id,e.ClientId,e.EmployeeCode,
TRIM(CONCAT(COALESCE(e.FirstName,''),' ',COALESCE(e.LastName,''))) AS EmployeeName,
COALESCE(w.Name,'') AS WorkLocation,
COALESCE(NULLIF(TRIM(p.PanNumber),''),NULLIF(JSON_UNQUOTE(JSON_EXTRACT(IF(JSON_VALID(e.PersonalJson),e.PersonalJson,'{}'),'$.pan')),''),
    JSON_UNQUOTE(JSON_EXTRACT(IF(JSON_VALID(e.PersonalJson),e.PersonalJson,'{}'),'$.panNumber')),'') AS Pan,
COALESCE(NULLIF(TRIM(p.AadhaarNumber),''),NULLIF(JSON_UNQUOTE(JSON_EXTRACT(IF(JSON_VALID(e.PersonalJson),e.PersonalJson,'{}'),'$.aadhaar')),''),
    JSON_UNQUOTE(JSON_EXTRACT(IF(JSON_VALID(e.PersonalJson),e.PersonalJson,'{}'),'$.aadhaarNumber')),'') AS Aadhaar,
COALESCE(NULLIF(TRIM(p.UanNumber),''),NULLIF(JSON_UNQUOTE(JSON_EXTRACT(IF(JSON_VALID(e.PersonalJson),e.PersonalJson,'{}'),'$.uan')),''),
    JSON_UNQUOTE(JSON_EXTRACT(IF(JSON_VALID(e.PersonalJson),e.PersonalJson,'{}'),'$.uanNumber')),'') AS Uan
FROM employees e
LEFT JOIN worklocations w ON w.Id=e.WorkLocationId AND w.ClientId=e.ClientId
LEFT JOIN employeepersonaldetails p ON p.EmployeeId=e.Id
WHERE e.ClientId=@ClientId", new { ClientId = clientId }, tx)).ToArray();

    internal sealed class MasterEmployeeIdentity
    {
        public int Id { get; set; }
        public int ClientId { get; set; }
        public string EmployeeCode { get; set; } = "";
        public string EmployeeName { get; set; } = "";
        public string WorkLocation { get; set; } = "";
        public string Pan { get; set; } = "";
        public string Aadhaar { get; set; } = "";
        public string Uan { get; set; } = "";
    }

    private static readonly HashSet<string> EmployeeLocationLabels =
        ["WORKLOCATION", "WORKLOCATIONNAME", "LOCATION", "LOCATIONNAME", "LOCATIONNAMETEHSILSUBTEHSIL", "TEHSILSUBTEHSIL"];

    private static string IdentityLabel(string value) => new(ExcelPayslipVarianceService.NormalizeText(value).Where(char.IsLetterOrDigit).ToArray());
    private static string NameLocation(string name, string location) => string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(location) ? ""
        : ExcelPayslipVarianceService.NormalizeText(name) + "\u001f" + ExcelPayslipVarianceService.NormalizeText(location);
    private static string IdentifierKind(string label) => IdentityLabel(label) switch
    {
        "PAN" or "PANNO" or "PANNUMBER" => "PAN",
        "AADHAAR" or "AADHAARNO" or "AADHAARNUMBER" or "AADHAR" or "AADHARNO" or "AADHARNUMBER"
            or "ADHAAR" or "ADHAARNO" or "ADHAARNUMBER" => "AADHAAR",
        "UAN" or "UANNO" or "UANNUMBER" => "UAN",
        _ => ""
    };
    private static string Identifier(string kind, string value)
    {
        var normalized = Regex.Replace(ExcelPayslipVarianceService.NormalizeText(value), @"[\s-]", "");
        return Regex.IsMatch(normalized, kind == "PAN" ? @"^[A-Z]{5}[0-9]{4}[A-Z]$" : @"^[0-9]{12}$", RegexOptions.CultureInvariant)
            && normalized != "000000000000" ? normalized : "";
    }

    internal static void ReuseMasterEmployeeCodes(ExcelPayslipBatch batch, IEnumerable<MasterEmployeeIdentity> employees)
    {
        var master = employees.Where(employee => employee.ClientId == batch.ClientId && !string.IsNullOrWhiteSpace(employee.EmployeeCode)).ToArray();
        var locations = master.Where(employee => NameLocation(employee.EmployeeName, employee.WorkLocation).Length > 0)
            .ToLookup(employee => NameLocation(employee.EmployeeName, employee.WorkLocation));
        var identifiers = master.SelectMany(employee => new[] { ("PAN", employee.Pan), ("AADHAAR", employee.Aadhaar), ("UAN", employee.Uan) }
            .Select(item => (Key: item.Item1 + ":" + Identifier(item.Item1, item.Item2), Value: Identifier(item.Item1, item.Item2), Employee: employee)))
            .Where(item => item.Value.Length > 0).ToLookup(item => item.Key, item => item.Employee);

        foreach (var row in batch.Rows.Where(row => string.IsNullOrWhiteSpace(row.EmployeeCode)))
        {
            InvalidOperationException Review(string reason) => new($"Excel row {row.SourceRow}: {reason} Review the employee and map the correct Employee Code before saving.");
            var rowLocations = row.Information.Where(item => EmployeeLocationLabels.Contains(IdentityLabel(item.Label)) && !string.IsNullOrWhiteSpace(item.Value))
                .Select(item => ExcelPayslipVarianceService.NormalizeText(item.Value)).Distinct().ToArray();
            if (rowLocations.Length > 1) throw Review("Conflicting work locations.");
            var rowIdentifiers = row.Information.Select(item => (Kind: IdentifierKind(item.Label), item.Value))
                .Where(item => item.Kind.Length > 0).Select(item => (item.Kind, Value: Identifier(item.Kind, item.Value)))
                .Where(item => item.Value.Length > 0).Distinct().ToArray();
            if (rowIdentifiers.GroupBy(item => item.Kind).Any(group => group.Count() > 1)) throw Review("Conflicting identity fields.");

            var locationMatches = locations[NameLocation(row.EmployeeName, rowLocations.SingleOrDefault() ?? "")].ToArray();
            var identifierMatches = rowIdentifiers.Select(item => identifiers[item.Kind + ":" + item.Value].ToArray()).ToArray();
            if (identifierMatches.Any(matches => matches.Length > 1)) throw Review("An identity field matches multiple Employee Master records.");
            var matched = identifierMatches.SelectMany(matches => matches).Distinct().ToArray();
            if (matched.Length > 1) throw Review("Identity fields point to different employees.");
            MasterEmployeeIdentity? employee = matched.SingleOrDefault();
            if (employee is null)
            {
                if (locationMatches.Length > 1) continue; // Let the allocator generate a code instead of guessing a master identity.
                employee = locationMatches.SingleOrDefault();
            }
            if (employee is null) continue; // Name alone never claims a master employee's code.
            if (ExcelPayslipVarianceService.NormalizeText(row.EmployeeName) != ExcelPayslipVarianceService.NormalizeText(employee.EmployeeName)
                || locationMatches.Length > 0 && !locationMatches.Contains(employee)) throw Review("Name/location and identity fields point to different employees.");
            foreach (var item in rowIdentifiers)
            {
                var existing = Identifier(item.Kind, item.Kind switch { "PAN" => employee.Pan, "AADHAAR" => employee.Aadhaar, _ => employee.Uan });
                if (existing.Length > 0 && existing != item.Value) throw Review("An identity field conflicts with the matched Employee Master record.");
            }
            row.EmployeeCode = employee.EmployeeCode;
        }
    }
}
