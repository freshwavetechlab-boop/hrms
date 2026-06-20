using Dapper;
using MySqlConnector;

namespace Payroll.API.Repositories;

public class SettingsRepository(IConfiguration configuration)
{
    private MySqlConnection Connection() => new(configuration.GetConnectionString("Default"));
    public async Task<string> GetAsync() { await using var db = Connection(); await db.OpenAsync(); await db.ExecuteAsync("USE payroll;"); return await db.ExecuteScalarAsync<string?>("SELECT SetupJson FROM PayrollSetups ORDER BY Id LIMIT 1") ?? "{}"; }
    public async Task SaveAsync(string json) { await using var db = Connection(); await db.OpenAsync(); await db.ExecuteAsync("USE payroll;"); var id = await db.ExecuteScalarAsync<int?>("SELECT Id FROM PayrollSetups ORDER BY Id LIMIT 1"); if (id is null) await db.ExecuteAsync("INSERT INTO PayrollSetups (SetupJson) VALUES (@json)", new { json }); else await db.ExecuteAsync("UPDATE PayrollSetups SET SetupJson=@json WHERE Id=@id", new { json, id }); }
}
