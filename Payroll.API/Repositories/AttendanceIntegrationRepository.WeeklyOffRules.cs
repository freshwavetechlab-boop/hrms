using MySqlConnector;
using Dapper;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public partial class AttendanceIntegrationRepository
{
    public async Task<IReadOnlyList<WeeklyOffRuleVersion>> GetWeeklyOffRulesAsync(int clientId)
    {
        await using var db = Db();
        await db.OpenAsync();
        return (await ReadAsync(db, null, clientId)).WeeklyOffRuleVersions.OrderByDescending(v => v.VersionNumber).ToArray();
    }

    public async Task<(IReadOnlyList<WeeklyOffRuleVersion>? Versions, string? Error)> PublishWeeklyOffRuleAsync(PublishWeeklyOffRuleRequest request, string publishedBy)
    {
        if (request.ClientId <= 0) return (null, "Select a client.");
        if (request.EffectiveFrom == default) return (null, "Effective date is required.");
        if (!WeeklyOffPayableResolver.Modes.Contains(request.Mode)) return (null, "Select a valid weekly-off mode.");
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length > 500) return (null, "Enter a change reason of up to 500 characters.");
        await using var db = Db();
        await db.OpenAsync();
        await using var tx = await db.BeginTransactionAsync();
        if (await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM clients WHERE Id=@ClientId AND IsActive=TRUE", new { request.ClientId }, tx) == 0)
            return (null, "Select an active client.");
        await EnsureRowAsync(db, tx, request.ClientId);
        var settings = await ReadAsync(db, tx, request.ClientId, true);
        var error = WeeklyOffPayableResolver.ValidatePublication(settings.WeeklyOffRuleVersions, request);
        if (error is not null) return (null, error);
        var workWeeks = (await db.QueryAsync<(string Value, string ConfigJson)>("SELECT Value,COALESCE(CAST(ConfigJson AS CHAR),'') ConfigJson FROM dropdownmasters WHERE Type='Work Week' AND IsActive=TRUE AND ClientId IN (0,@ClientId) ORDER BY ClientId DESC,Id", new { request.ClientId }, tx))
            .GroupBy(w => w.Value, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().ConfigJson, StringComparer.OrdinalIgnoreCase);
        settings.WeeklyOffRuleVersions.Add(new()
        {
            VersionId = Guid.NewGuid().ToString("N"), VersionNumber = request.ExpectedLatestVersion + 1,
            EffectiveFrom = request.EffectiveFrom.Date, Mode = request.Mode, Reason = request.Reason.Trim(),
            PublishedBy = publishedBy, PublishedAt = DateTime.UtcNow, WorkWeekConfigs = workWeeks
        });
        await WriteAsync(db, tx, request.ClientId, settings);
        await tx.CommitAsync();
        // Publishing deliberately does not recalculate any attendance or payroll.
        return (settings.WeeklyOffRuleVersions.OrderByDescending(v => v.VersionNumber).ToArray(), null);
    }
}
