using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public partial class AttendanceIntegrationRepository
{
    internal sealed class StoredApiAdapter
    {
        public AttendanceApiAdapter Configuration { get; set; } = new();
        public string ProtectedCredentials { get; set; } = "";
    }

    private static async Task<List<StoredApiAdapter>> ReadAdaptersAsync(MySqlConnection db,
        System.Data.IDbTransaction? tx, int clientId, bool locked = false)
    {
        var json = await db.ExecuteScalarAsync<string?>("SELECT JSON_EXTRACT(SettingsJson,'$.attendanceApiAdapters') FROM modulesettings WHERE client_id=@ClientId AND ModuleCode='leave_attendance'" + (locked ? " FOR UPDATE" : ""), new { ClientId = clientId }, tx);
        return string.IsNullOrWhiteSpace(json) || json == "null" ? [] : JsonSerializer.Deserialize<List<StoredApiAdapter>>(json, JsonOptions) ?? [];
    }

    private static Task WriteAdaptersAsync(MySqlConnection db, System.Data.IDbTransaction tx, int clientId, List<StoredApiAdapter> rows) =>
        db.ExecuteAsync("UPDATE modulesettings SET SettingsJson=JSON_SET(COALESCE(SettingsJson,JSON_OBJECT()),'$.attendanceApiAdapters',CAST(@Json AS JSON)) WHERE client_id=@ClientId AND ModuleCode='leave_attendance'", new { ClientId = clientId, Json = JsonSerializer.Serialize(rows) }, tx);

    internal async Task<List<StoredApiAdapter>> GetAdaptersAsync(int clientId)
    {
        await using var db = Db(); await db.OpenAsync();
        return await ReadAdaptersAsync(db, null, clientId);
    }

    internal async Task<AttendanceApiAdapter> SaveAdapterAsync(AttendanceApiAdapter config,
        Func<StoredApiAdapter?, string> credentials)
    {
        await using var db = Db(); await db.OpenAsync(); await using var tx = await db.BeginTransactionAsync();
        if (await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM clients WHERE Id=@ClientId AND IsActive=TRUE", new { config.ClientId }, tx) != 1)
            throw new InvalidOperationException("Select an active client.");
        await EnsureRowAsync(db, tx, config.ClientId);
        var rows = await ReadAdaptersAsync(db, tx, config.ClientId, true);
        var existing = rows.FirstOrDefault(r => r.Configuration.Id == config.Id);
        if (config.Id.Length > 0 && existing is null) throw new InvalidOperationException("Adapter was deleted. Reload the list.");
        if (existing is not null && existing.Configuration.Revision != config.Revision) throw new InvalidOperationException("Adapter changed. Reload before saving.");
        if (rows.Count >= 50 && existing is null) throw new InvalidOperationException("A client can have up to 50 adapters.");
        var integration = await ReadAsync(db, tx, config.ClientId);
        if (!integration.Devices.Any(d => d.DeviceId == config.DeviceId && d.IsActive)) throw new InvalidOperationException("Select an active registered punch machine for this client.");
        var protectedCredentials = credentials(existing);
        config.Id = existing?.Configuration.Id ?? Guid.NewGuid().ToString("N");
        config.Revision = Guid.NewGuid().ToString("N");
        config.LastAttemptAt = existing?.Configuration.LastAttemptAt;
        config.LastSuccessAt = existing?.Configuration.LastSuccessAt;
        config.LastRun = existing?.Configuration.LastRun;
        rows.RemoveAll(r => r.Configuration.Id == config.Id);
        rows.Add(new() { Configuration = config, ProtectedCredentials = protectedCredentials });
        await WriteAdaptersAsync(db, tx, config.ClientId, rows); await tx.CommitAsync();
        return config;
    }

    internal async Task DeleteAdapterAsync(int clientId, string id)
    {
        await using var db = Db(); await db.OpenAsync(); await using var tx = await db.BeginTransactionAsync();
        var rows = await ReadAdaptersAsync(db, tx, clientId, true);
        rows.RemoveAll(r => r.Configuration.Id == id);
        await WriteAdaptersAsync(db, tx, clientId, rows); await tx.CommitAsync();
    }

    internal async Task RecordAdapterRunAsync(int clientId, string id, string revision, AttendanceAdapterRun run)
    {
        await using var db = Db(); await db.OpenAsync(); await using var tx = await db.BeginTransactionAsync();
        var rows = await ReadAdaptersAsync(db, tx, clientId, true);
        var row = rows.FirstOrDefault(r => r.Configuration.Id == id && r.Configuration.Revision == revision);
        if (row is null) return;
        // Keep only operational summaries; preview employee data is never persisted here.
        row.Configuration.LastRun = JsonSerializer.Deserialize<AttendanceAdapterRun>(JsonSerializer.Serialize(run));
        row.Configuration.LastRun!.Samples = [];
        if (!run.Preview)
        {
            row.Configuration.LastAttemptAt = run.StartedAt;
            if (run.Success) row.Configuration.LastSuccessAt = run.StartedAt;
        }
        await WriteAdaptersAsync(db, tx, clientId, rows); await tx.CommitAsync();
    }

    internal async Task<int[]> AdapterClientIdsAsync()
    {
        await using var db = Db();
        return (await db.QueryAsync<int>("SELECT m.client_id FROM modulesettings m JOIN clients c ON c.Id=m.client_id AND c.IsActive=TRUE WHERE m.ModuleCode='leave_attendance' AND JSON_LENGTH(JSON_EXTRACT(m.SettingsJson,'$.attendanceApiAdapters'))>0")).ToArray();
    }

    // A database advisory lock serializes manual and scheduled runs across API instances.
    internal async Task<MySqlConnection?> AcquireAdapterLockAsync(int clientId, string id)
    {
        var db = Db();
        try
        {
            await db.OpenAsync();
            var name = "attendance-adapter:" + Hash(clientId + ":" + id)[..40];
            if (await db.ExecuteScalarAsync<int>("SELECT GET_LOCK(@Name,0)", new { Name = name }) == 1) return db;
            await db.DisposeAsync(); return null;
        }
        catch { await db.DisposeAsync(); throw; }
    }

    internal static Task ReleaseAdapterLockAsync(MySqlConnection db, int clientId, string id) =>
        db.ExecuteAsync("SELECT RELEASE_LOCK(@Name)", new { Name = "attendance-adapter:" + Hash(clientId + ":" + id)[..40] });
}
