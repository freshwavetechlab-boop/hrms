using System.Globalization;
using System.Text.Json.Nodes;
using Dapper;

namespace Payroll.API.Repositories;

public partial class SettingsRepository
{
    public async Task<string> GetForSelectedClientAsync(int clientId)
    {
        await using var db = Connection(); await db.OpenAsync();
        var current = JsonNode.Parse(await PayrollDataTableStore.GetSetupJsonAsync(db))?.AsObject() ?? new JsonObject();
        return ProjectSelectedClientSetup(current, clientId).ToJsonString();
    }

    public async Task SaveForSelectedClientAsync(int clientId, string json)
    {
        if (clientId <= 0) throw new InvalidOperationException("Select an assigned client before saving settings.");
        var incoming = JsonNode.Parse(json)?.AsObject() ?? throw new InvalidOperationException("Submit valid client settings.");
        await using var db = Connection(); await db.OpenAsync(); await using var tx = await db.BeginTransactionAsync();
        // Hold rows/gaps while ownership is checked so a conflicting inserted ID
        // or concurrent reparenting cannot turn an owned update into a foreign one.
        await db.QueryAsync<long>("SELECT Id FROM payrollsetups ORDER BY Id FOR UPDATE", transaction: tx);
        await db.QueryAsync<long>("SELECT Id FROM salarystructures ORDER BY Id FOR UPDATE", transaction: tx);
        await db.QueryAsync<long>("SELECT Id FROM paysliptemplates ORDER BY Id FOR UPDATE", transaction: tx);
        var current = JsonNode.Parse(await PayrollDataTableStore.GetSetupJsonAsync(db, tx))?.AsObject() ?? new JsonObject();
        var merged = MergeSelectedClientSetup(current, incoming, clientId);
        await PayrollDataTableStore.SaveSelectedClientSetupJsonAsync(db, tx, clientId, current, merged);
        await tx.CommitAsync();
    }

    internal static JsonObject ProjectSelectedClientSetup(JsonObject current, int clientId)
    {
        if (clientId <= 0) throw new InvalidOperationException("Select an assigned client.");
        var result = current.DeepClone().AsObject();
        foreach (var property in new[] { "salaryStructures", "payslipTemplates" })
        {
            var source = result[property] as JsonArray ?? [];
            result[property] = new JsonArray(source.OfType<JsonObject>().Where(row => ReadClientRefs(row) is { } ids && (ids.Count == 0 || ids.Contains(clientId))).Select(row => {
                var copy = row.DeepClone().AsObject(); SetCanonical(copy, "clientId", JsonValue.Create(clientId.ToString(CultureInfo.InvariantCulture))); return (JsonNode)copy;
            }).ToArray());
        }
        if (result["tax"] is JsonObject tax)
        {
            var source = tax["clientSettings"] as JsonArray ?? [];
            tax["clientSettings"] = new JsonArray(source.OfType<JsonObject>().Where(row => IsPureClientRow(row, clientId)).Select(row => row.DeepClone()).ToArray());
        }
        return result;
    }

    internal static JsonObject MergeSelectedClientSetup(JsonObject current, JsonObject incoming, int clientId)
    {
        if (clientId <= 0) throw new InvalidOperationException("Select an assigned client.");
        var result = current.DeepClone().AsObject();
        foreach (var property in new[] { "salaryStructures", "payslipTemplates" })
            MergeSelectedArray(result, incoming, property, clientId, requireId: true);
        if (incoming["tax"] is JsonObject incomingTax && incomingTax.ContainsKey("clientSettings"))
        {
            var tax = result["tax"] as JsonObject;
            if (tax is null) { tax = new JsonObject(); result["tax"] = tax; }
            MergeSelectedArray(tax, incomingTax, "clientSettings", clientId, requireId: false);
        }
        // All other incoming fields are deliberately ignored: components,
        // statutory rules and organization schedules remain shared definitions.
        return result;
    }

    private static void MergeSelectedArray(JsonObject current, JsonObject incoming, string property, int clientId, bool requireId)
    {
        if (!incoming.ContainsKey(property)) return;
        if (incoming[property] is not JsonArray submitted) throw new InvalidOperationException($"Submit a valid {property} list.");
        var existing = (current[property] as JsonArray ?? []).OfType<JsonObject>().ToArray();
        var byId = existing.Where(row => ReadRowId(row) > 0).ToDictionary(ReadRowId);
        var protectedRows = existing.Where(row => !IsPureClientRow(row, clientId)).ToArray();
        var merged = protectedRows.Select(row => row.DeepClone()).ToList();
        var seen = new HashSet<long>();
        foreach (var node in submitted)
        {
            if (node is not JsonObject row || !IsPureClientRow(row, clientId))
                throw new InvalidOperationException($"Every submitted {property} record must belong only to the active assigned client.");
            var id = ReadRowId(row);
            if (requireId && id <= 0) throw new InvalidOperationException($"Every {property} record needs a positive ID.");
            if (id > 0 && !seen.Add(id)) throw new InvalidOperationException($"A {property} record ID is repeated.");
            var copy = row.DeepClone().AsObject();
            SetCanonical(copy, "clientId", JsonValue.Create(clientId.ToString(CultureInfo.InvariantCulture)));
            if (id > 0) SetCanonical(copy, "id", JsonValue.Create(id));
            if (id > 0 && byId.TryGetValue(id, out var old))
            {
                var refs = ReadClientRefs(old);
                if (refs is null || refs.Count > 0 && !refs.Contains(clientId))
                    throw new InvalidOperationException($"A submitted {property} ID belongs to another client.");
                if (!IsPureClientRow(old, clientId))
                {
                    if (!SameExceptClientRef(old, copy))
                        throw new InvalidOperationException($"This {property} record is shared. An unrestricted administrator must change shared definitions.");
                    continue; // retain the original shared row, including every client reference
                }
                merged.Add(SameExceptClientRef(old, copy) ? old.DeepClone() : copy);
            }
            else merged.Add(copy);
        }
        if (requireId && protectedRows.Any(row => ReadClientRefs(row) is { } refs && (refs.Count == 0 || refs.Contains(clientId)) && ReadRowId(row) > 0 && !seen.Contains(ReadRowId(row))))
            throw new InvalidOperationException($"Shared {property} records cannot be deleted from Selected-client settings.");
        var ordered = new List<JsonNode?>();
        foreach (var old in existing)
        {
            var id = ReadRowId(old);
            var match = merged.FindIndex(node => node is JsonObject item && (id > 0 ? ReadRowId(item) == id : JsonNode.DeepEquals(old, item)));
            if (match >= 0) { ordered.Add(merged[match]); merged.RemoveAt(match); }
        }
        ordered.AddRange(merged);
        current[property] = new JsonArray(ordered.ToArray());
    }

    internal static bool IsPureClientRow(JsonObject row, int clientId) => ReadClientRefs(row) is { Count: 1 } ids && ids[0] == clientId;
    internal static long ReadRowId(JsonObject row) => long.TryParse(ReadProperty(row, "id")?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0;
    internal static bool SameExceptClientRef(JsonObject left, JsonObject right)
    {
        var a = left.DeepClone().AsObject(); var b = right.DeepClone().AsObject();
        foreach (var key in a.Select(p => p.Key).Where(k => k.Equals("clientId", StringComparison.OrdinalIgnoreCase)).ToArray()) a.Remove(key);
        foreach (var key in b.Select(p => p.Key).Where(k => k.Equals("clientId", StringComparison.OrdinalIgnoreCase)).ToArray()) b.Remove(key);
        return JsonNode.DeepEquals(a, b);
    }
    private static List<int>? ReadClientRefs(JsonObject row)
    {
        var raw = ReadProperty(row, "clientId")?.ToString().Trim() ?? "";
        if (raw.Length == 0 || raw == "0") return [];
        var result = new List<int>();
        foreach (var token in raw.Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(token.Split(':', 2)[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id <= 0) return null;
            if (!result.Contains(id)) result.Add(id);
        }
        return result.Count == 0 ? null : result;
    }
    private static JsonNode? ReadProperty(JsonObject row, string key)
    {
        var matches = row.Where(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length > 1) throw new InvalidOperationException($"Ambiguous {key} property.");
        return matches.FirstOrDefault().Value;
    }
    private static void SetCanonical(JsonObject row, string key, JsonNode? value)
    {
        foreach (var alias in row.Select(p => p.Key).Where(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)).ToArray()) row.Remove(alias);
        row[key] = value;
    }
}
