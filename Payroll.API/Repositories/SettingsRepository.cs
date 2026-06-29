using Dapper;
using MySqlConnector;

namespace Payroll.API.Repositories;

public class SettingsRepository(IConfiguration configuration)
{
    private MySqlConnection Connection() => new(configuration.GetConnectionString("Default"));
    public async Task<string> GetAsync() { await using var db = Connection(); await db.OpenAsync(); return await PayrollDataTableStore.GetSetupJsonAsync(db); }
    public async Task SaveAsync(string json) { await using var db = Connection(); await db.OpenAsync(); await PayrollDataTableStore.SaveSetupJsonAsync(db, json); }
}
