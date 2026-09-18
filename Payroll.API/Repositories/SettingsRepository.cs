using Dapper;
using MySqlConnector;
using System.Text.Json.Nodes;

namespace Payroll.API.Repositories;

public class SettingsRepository(IConfiguration configuration)
{
    private MySqlConnection Connection() => new(configuration.GetConnectionString("Default"));
    public async Task<string> GetAsync() { await using var db = Connection(); await db.OpenAsync(); return await PayrollDataTableStore.GetSetupJsonAsync(db); }
    public async Task SaveAsync(string json) { await using var db = Connection(); await db.OpenAsync(); await PayrollDataTableStore.SaveSetupJsonAsync(db, json); }

    public async Task<string> GetForClientAsync(int clientId)
    {
        await using var db = Connection();
        await db.OpenAsync();
        var root = JsonNode.Parse(await PayrollDataTableStore.GetSetupJsonAsync(db))?.AsObject() ?? new JsonObject();
        FilterClientArray(root, "salaryStructures", clientId);
        FilterClientArray(root, "payslipTemplates", clientId);
        if (root["tax"] is JsonObject tax) FilterClientArray(tax, "clientSettings", clientId);
        return root.ToJsonString();
    }

    public async Task SaveForClientAsync(int clientId, string json)
    {
        await using var db = Connection();
        await db.OpenAsync();
        var current = JsonNode.Parse(await PayrollDataTableStore.GetSetupJsonAsync(db))?.AsObject() ?? new JsonObject();
        var incoming = JsonNode.Parse(json)?.AsObject() ?? new JsonObject();
        MergeClientArray(current, incoming, "salaryStructures", clientId);
        MergeClientArray(current, incoming, "payslipTemplates", clientId);
        if (incoming["tax"] is JsonObject incomingTax)
        {
            var currentTax = current["tax"] as JsonObject ?? new JsonObject();
            current["tax"] = currentTax;
            MergeClientArray(currentTax, incomingTax, "clientSettings", clientId);
        }
        await PayrollDataTableStore.SaveSetupJsonAsync(db, current.ToJsonString());
    }

    private static void FilterClientArray(JsonObject owner, string property, int clientId)
    {
        var source = owner[property] as JsonArray ?? [];
        owner[property] = new JsonArray(source.Where(item => BelongsToClient(item, clientId)).Select(item => item?.DeepClone()).ToArray());
    }

    private static void MergeClientArray(JsonObject current, JsonObject incoming, string property, int clientId)
    {
        var existing = current[property] as JsonArray ?? [];
        var submitted = incoming[property] as JsonArray ?? [];
        var merged = existing.Where(item => !BelongsToClient(item, clientId)).Select(item => item?.DeepClone()).ToList();
        merged.AddRange(submitted.Where(item => BelongsToClient(item, clientId)).Select(item => item?.DeepClone()));
        current[property] = new JsonArray(merged.ToArray());
    }

    private static bool BelongsToClient(JsonNode? node, int clientId)
    {
        if (node is not JsonObject item || item["clientId"] is null) return false;
        var raw = item["clientId"]!.ToString();
        return raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(value => int.TryParse(value, out var parsed) && parsed == clientId);
    }
}
