using System.Globalization;
using Dapper;
using MySqlConnector;

namespace Payroll.API.Repositories;

public static partial class PayrollDataTableStore
{
    private const string DeletedTemplatePrefix = "deleted-salary-template:";
    private static string DeletedTemplateKey(long id) => DeletedTemplatePrefix + id.ToString(CultureInfo.InvariantCulture);

    internal static Task ReserveDeletedTemplateIdAsync(MySqlConnection db, MySqlTransaction tx, int clientId, long templateId) =>
        db.ExecuteAsync("INSERT INTO modulesettings(client_id,ModuleCode,IsEnabled,SettingsJson) VALUES(@clientId,@code,FALSE,JSON_OBJECT())",
            new { clientId, code = DeletedTemplateKey(templateId) }, tx);

    // Bulk settings forms carry whole snapshots and can still contain a deliberately removed ID.
    // Reserve the identifier only; the component formulas and template data are physically removed.
    internal static async Task RequireAvailableTemplateIdsAsync(MySqlConnection db, MySqlTransaction? tx, IEnumerable<long> proposedIds)
    {
        var keys = proposedIds.Distinct().Select(DeletedTemplateKey).ToArray();
        if (keys.Length == 0 || !await db.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name='modulesettings')", transaction: tx)) return;
        var found = await db.ExecuteScalarAsync<string?>("SELECT ModuleCode FROM modulesettings WHERE ModuleCode IN @keys LIMIT 1"
            + (tx is null ? "" : " LOCK IN SHARE MODE"), new { keys }, tx);
        if (found is not null) throw new InvalidOperationException("This salary template was deleted. Refresh Salary Templates and create a new template to use a new ID.");
    }

    internal static async Task<long> NextSalaryTemplateIdAsync(MySqlConnection db, MySqlTransaction tx, long highestExisting)
    {
        const long maxExactId = 9007199254740991L;
        if (highestExisting >= maxExactId) throw new InvalidOperationException("A new salary template ID is unavailable.");
        var candidate = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), highestExisting + 1);
        var reservedCodes = await db.QueryAsync<string>(
            "SELECT ModuleCode FROM modulesettings WHERE ModuleCode LIKE @pattern LOCK IN SHARE MODE", new { pattern = DeletedTemplatePrefix + "%" }, tx);
        var reserved = reservedCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        while (reserved.Contains(DeletedTemplateKey(candidate))) candidate++;
        return candidate <= maxExactId ? candidate : throw new InvalidOperationException("A new salary template ID is unavailable.");
    }
}
