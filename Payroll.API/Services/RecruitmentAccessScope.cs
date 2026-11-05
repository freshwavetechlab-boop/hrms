using System.Text.RegularExpressions;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Services;

public static partial class RecruitmentAccessScope
{
    private static bool HasClientContext(AuthUser user, int clientId) => SelectedClientAccess.Allows(user, clientId)
        && (!user.ClientId.HasValue || user.ClientId == clientId);
    // Institute/posting visibility uses the existing Work Location ID. Keep the
    // older text-based matching below for its existing callers only.
    public static async Task<List<WorkLocation>> WorkLocationsAsync(MySqlConnection db, AuthUser user, int clientId)
    {
        if (!HasClientContext(user, clientId)) return [];
        var rows = (await db.QueryAsync<WorkLocation>("SELECT * FROM worklocations WHERE ClientId=@ClientId AND IsActive=TRUE ORDER BY Name,Id", new { ClientId = clientId })).ToList();
        int? employeeLocation = null;
        if (user.RecruitmentScopeMode.Equals("EmployeeLocation", StringComparison.OrdinalIgnoreCase) && user.EmployeeId is > 0)
            employeeLocation = await db.ExecuteScalarAsync<int?>("SELECT NULLIF(WorkLocationId,0) FROM employees WHERE Id=@EmployeeId AND ClientId=@ClientId AND IsActive=TRUE", new { user.EmployeeId, ClientId = clientId });
        return rows.Where(row => AllowsKnownWorkLocation(user, clientId, row.Id, employeeLocation)).ToList();
    }

    public static async Task<bool> CanAccessWorkLocationIdAsync(MySqlConnection db, AuthUser user, int clientId, int? workLocationId)
    {
        if (!HasClientContext(user, clientId)) return false;
        if (workLocationId is null or <= 0) return !IsRestricted(user);
        return (await WorkLocationsAsync(db, user, clientId)).Any(row => row.Id == workLocationId);
    }

    public static async Task<bool> CanAccessApplicationWorkLocationAsync(MySqlConnection db, AuthUser user, long applicationId)
    {
        var row = await db.QuerySingleOrDefaultAsync<ApplicationLocation>(@"SELECT a.ClientId,NULLIF(p.BranchId,0) WorkLocationId
FROM recruitment_candidate_applications a JOIN recruitment_open_positions p ON p.Id=a.PositionId AND p.ClientId=a.ClientId WHERE a.Id=@Id", new { Id = applicationId });
        return row is not null && HasClientContext(user, row.ClientId)
            && (!IsRestricted(user) || await CanAccessWorkLocationIdAsync(db, user, row.ClientId, row.WorkLocationId));
    }

    private sealed class ApplicationLocation { public int ClientId { get; set; } public int? WorkLocationId { get; set; } }

    internal static bool AllowsKnownWorkLocation(AuthUser user, int clientId, int? workLocationId, int? employeeWorkLocationId = null)
    {
        if (!HasClientContext(user, clientId)) return false;
        if (!IsRestricted(user)) return true;
        if (workLocationId is null or <= 0) return false;
        if (user.RecruitmentScopeMode.Equals("EmployeeLocation", StringComparison.OrdinalIgnoreCase))
            return employeeWorkLocationId is > 0 && employeeWorkLocationId == workLocationId;
        return user.RecruitmentScopeMode.Equals("SelectedLocations", StringComparison.OrdinalIgnoreCase)
            && user.RecruitmentLocationIds.Contains(workLocationId.Value);
    }

    public static async Task<List<T>> FilterWorkLocationsAsync<T>(MySqlConnection db, AuthUser user, IEnumerable<T> rows, Func<T, int> clientId, Func<T, int?> workLocationId)
    {
        var source = rows.Where(row => HasClientContext(user, clientId(row))).ToList();
        if (!IsRestricted(user)) return source;
        var allowed = new Dictionary<int, HashSet<int>>();
        foreach (var client in source.Select(clientId).Distinct())
            allowed[client] = (await WorkLocationsAsync(db, user, client)).Select(row => row.Id).ToHashSet();
        return source.Where(row => workLocationId(row) is > 0 && allowed[clientId(row)].Contains(workLocationId(row)!.Value)).ToList();
    }

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
