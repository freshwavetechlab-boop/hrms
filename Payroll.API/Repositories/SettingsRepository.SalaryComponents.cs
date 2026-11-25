using System.Text.Json.Nodes;

namespace Payroll.API.Repositories;

public partial class SettingsRepository
{
    public async Task<JsonObject> SaveSalaryComponentAsync(JsonObject component, JsonObject? expected)
    {
        await using var db = Connection();
        await db.OpenAsync();
        return await PayrollDataTableStore.SaveSalaryComponentAsync(db, component, expected);
    }
}
