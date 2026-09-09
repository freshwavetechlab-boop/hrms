using System.Text.RegularExpressions;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Services;

public static partial class RecruitmentAccessScope
{
    public static bool IsRestricted(AuthUser user) =>
        !string.Equals(user.RecruitmentScopeMode, "Client", StringComparison.OrdinalIgnoreCase);

    public static async Task<bool> CanAccessLocationAsync(MySqlConnection db, AuthUser user, int clientId, string? location)
    {
        if (user.ClientId.HasValue && user.ClientId.Value != clientId) return false;
        if (!IsRestricted(user)) return true;

        var locationIds = user.RecruitmentScopeMode.Equals("EmployeeLocation", StringComparison.OrdinalIgnoreCase)
            ? user.EmployeeId is > 0
                ? [await db.ExecuteScalarAsync<int>("SELECT COALESCE(WorkLocationId,0) FROM employees WHERE Id=@EmployeeId AND IsActive=TRUE", new { user.EmployeeId })]
                : []
            : user.RecruitmentLocationIds.Distinct().Where(id => id > 0).ToArray();
        if (locationIds.Length == 0 || string.IsNullOrWhiteSpace(location)) return false;

        var allowed = await db.QueryAsync<ScopeLocation>(@"SELECT Id,Name,City,State
FROM worklocations
WHERE Id IN @LocationIds AND ClientId=@ClientId AND IsActive=TRUE", new { LocationIds = locationIds, ClientId = clientId });
        var target = Normalize(location);
        return allowed.Any(item => Matches(target, item.Name) || Matches(target, item.City) || Matches(target, item.State));
    }

    public static async Task<List<T>> FilterAsync<T>(MySqlConnection db, AuthUser user, IEnumerable<T> rows, Func<T, int> clientId, Func<T, string?> location)
    {
        var source = rows.ToList();
        if (!IsRestricted(user)) return source;
        var result = new List<T>(source.Count);
        foreach (var row in source)
            if (await CanAccessLocationAsync(db, user, clientId(row), location(row))) result.Add(row);
        return result;
    }

    private static bool Matches(string target, string? candidate)
    {
        var value = Normalize(candidate);
        return value.Length >= 3 && (target.Contains(value, StringComparison.Ordinal) || value.Contains(target, StringComparison.Ordinal));
    }

    private static string Normalize(string? value) => NonAlphaNumeric().Replace((value ?? "").Trim().ToLowerInvariant(), "");

    [GeneratedRegex("[^a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonAlphaNumeric();

    private sealed class ScopeLocation
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string City { get; set; } = "";
        public string State { get; set; } = "";
    }
}
