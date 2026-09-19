using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Payroll.API.Services;

// Stores query plans only. The caller must validate every use and promote a
// host-held candidate only after the guarded, scoped database query succeeds.
public sealed class FrevoPilotPlanMemory
{
    internal const int MaximumBytes = 2 * 1024 * 1024;
    // Reserve room for authenticated-encryption and base64 expansion on disk.
    internal const int MaximumMemoryBytes = MaximumBytes / 2;
    private const int MaximumEntries = 128;
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);
    private readonly object sync = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly string? filePath;
    private readonly Func<string, string>? protect;
    private readonly Func<string, string?>? unprotect;
    private sealed record Entry(JsonElement Payload, DateTimeOffset CreatedUtc, DateTimeOffset AccessedUtc, int Bytes);

    public FrevoPilotPlanMemory(string? filePath = null, Func<string, string>? protect = null, Func<string, string?>? unprotect = null)
    {
        // A path alone must never cause plaintext persistence.
        if (string.IsNullOrWhiteSpace(filePath) || protect is null || unprotect is null) return;
        try { this.filePath = Path.GetFullPath(filePath); }
        catch { return; }
        this.protect = protect;
        this.unprotect = unprotect;
        Load();
    }

    public FrevoPilotPlanSession CreateSession(string scopeFingerprint) => new(this, scopeFingerprint);

    internal bool TryGet(string key, out JsonElement payload)
    {
        lock (sync)
        {
            Prune();
            if (entries.TryGetValue(key, out var entry))
            {
                entries[key] = entry with { AccessedUtc = DateTimeOffset.UtcNow };
                payload = entry.Payload.Clone();
                return true;
            }
            payload = default;
            return false;
        }
    }

    internal void Store(string key, JsonElement payload)
    {
        lock (sync)
        {
            var now = DateTimeOffset.UtcNow;
            entries[key] = new(payload.Clone(), now, now, Encoding.UTF8.GetByteCount(payload.GetRawText()) + 256);
            Prune();
            Persist();
        }
    }

    internal void Invalidate(string key)
    {
        lock (sync)
        {
            if (entries.Remove(key)) Persist();
        }
    }

    private void Prune()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var key in entries.Where(pair => pair.Value.CreatedUtc > now || now - pair.Value.CreatedUtc >= Lifetime).Select(pair => pair.Key).ToArray())
            entries.Remove(key);
        while (entries.Count > MaximumEntries || entries.Values.Sum(entry => entry.Bytes) > MaximumMemoryBytes)
            entries.Remove(entries.MinBy(pair => pair.Value.AccessedUtc).Key);
    }

    private void Load()
    {
        if (filePath is null || unprotect is null) return;
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaximumBytes) return;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var encrypted = reader.ReadToEnd();
            var plaintext = unprotect(encrypted);
            if (plaintext is null || plaintext == encrypted || Encoding.UTF8.GetByteCount(plaintext) > MaximumMemoryBytes) return;
            using var document = JsonDocument.Parse(plaintext, new JsonDocumentOptions { MaxDepth = 24 });
            var root = document.RootElement;
            if (root.GetProperty("version").GetInt32() != 1) return;
            var saved = root.GetProperty("entries");
            if (saved.ValueKind != JsonValueKind.Array || saved.GetArrayLength() > MaximumEntries) return;
            var loaded = new Dictionary<string, Entry>(StringComparer.Ordinal);
            foreach (var item in saved.EnumerateArray())
            {
                var key = item.GetProperty("key").GetString();
                var created = item.GetProperty("createdUtc").GetDateTimeOffset();
                if (key is null || key.Length != 64 || key.Any(character => !Uri.IsHexDigit(character))
                    || !TryExtractPayload(item.GetProperty("payload"), out var payload)) continue;
                loaded[key] = new(payload, created, created, Encoding.UTF8.GetByteCount(payload.GetRawText()) + 256);
            }
            foreach (var pair in loaded) entries[pair.Key] = pair.Value;
            Prune();
        }
        catch { /* Corruption, unavailable keys and storage failure are cache misses. */ }
    }

    private void Persist()
    {
        if (filePath is null || protect is null) return;
        string? temporary = null;
        try
        {
            var plaintext = JsonSerializer.Serialize(new
            {
                version = 1,
                entries = entries.Select(pair => new { key = pair.Key, createdUtc = pair.Value.CreatedUtc, payload = pair.Value.Payload })
            });
            if (Encoding.UTF8.GetByteCount(plaintext) > MaximumMemoryBytes) return;
            var encrypted = protect(plaintext);
            if (string.IsNullOrEmpty(encrypted) || encrypted == plaintext || Encoding.UTF8.GetByteCount(encrypted) > MaximumBytes) return;
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            temporary = filePath + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporary, encrypted, new UTF8Encoding(false));
            File.Move(temporary, filePath, overwrite: true);
        }
        catch { /* Cache persistence must not fail a dashboard. */ }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); } catch { }
        }
    }

    internal static bool TryExtractPayload(JsonElement input, out JsonElement payload)
    {
        payload = default;
        try
        {
            if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("sql", out var sqlField)
                || sqlField.ValueKind != JsonValueKind.String || !input.TryGetProperty("parameters", out var parameters)
                || parameters.ValueKind != JsonValueKind.Array || parameters.GetArrayLength() > 20) return false;
            if (input.EnumerateObject().GroupBy(property => property.Name, StringComparer.Ordinal).Any(group => group.Count() > 1)) return false;
            var sql = sqlField.GetString()!;
            if (sql.Length is < 7 or > 20_000 || !sql.TrimStart().StartsWith("SELECT ", StringComparison.OrdinalIgnoreCase)) return false;
            var values = new List<string>();
            foreach (var parameter in parameters.EnumerateArray())
            {
                if (parameter.ValueKind != JsonValueKind.String || parameter.GetString()!.Length > 2_000) return false;
                values.Add(parameter.GetString()!);
            }
            var candidate = JsonSerializer.SerializeToElement(new { sql, parameters = values });
            if (Encoding.UTF8.GetByteCount(candidate.GetRawText()) > 64 * 1024) return false;
            payload = candidate;
            return true;
        }
        catch { return false; }
    }

    internal static bool TryCanonicalContract(JsonElement contract, out string canonical)
    {
        canonical = "";
        try
        {
            static bool Name(JsonElement parent, string property) => parent.TryGetProperty(property, out var value)
                && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 and <= 128 } name
                && (char.IsAsciiLetter(name[0]) || name[0] == '_') && name.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
            if (contract.ValueKind != JsonValueKind.Object || contract.GetRawText().Length > 16 * 1024
                || contract.GetProperty("version").GetString() != "simple-count-v1"
                || !Name(contract, "recordTable") || contract.GetProperty("noOtherFiltersRequested").ValueKind != JsonValueKind.True) return false;
            var measure = contract.GetProperty("measure").GetString();
            if (measure is not ("count_records" or "sum_values" or "average_values")) return false;
            var grouping = contract.GetProperty("grouping");
            if (!Name(grouping, "table") || !Name(grouping, "column")) return false;
            var allowed = contract.GetProperty("allowedTables");
            if (allowed.ValueKind != JsonValueKind.Array || allowed.GetArrayLength() is < 1 or > 3
                || allowed.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String)) return false;
            foreach (var property in new[] { "requestedFilters", "recordDefinitionFilters", "requiredRelationships" })
            {
                var array = contract.GetProperty(property);
                if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > (property == "requiredRelationships" ? 2 : 3)) return false;
            }
            if (measure != "count_records")
            {
                var column = contract.GetProperty("measureColumn");
                if (!Name(column, "table") || !Name(column, "column")
                    || !string.Equals(column.GetProperty("table").GetString(), contract.GetProperty("recordTable").GetString(), StringComparison.OrdinalIgnoreCase)) return false;
            }
            if (contract.TryGetProperty("roundDigits", out var rounding)
                && (measure != "average_values" || !rounding.TryGetInt32(out var digits) || digits != 2)) return false;
            using var bytes = new MemoryStream();
            using (var writer = new Utf8JsonWriter(bytes)) WriteCanonical(writer, contract, 0);
            if (bytes.Length > 16 * 1024) return false;
            canonical = Encoding.UTF8.GetString(bytes.ToArray());
            return true;
        }
        catch { return false; }
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value, int depth)
    {
        if (depth > 16) throw new InvalidOperationException("Contract depth exceeds the plan-memory limit.");
        if (value.ValueKind == JsonValueKind.Object)
        {
            var properties = value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal).ToArray();
            if (properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length)
                throw new InvalidOperationException("Duplicate contract properties are not supported.");
            writer.WriteStartObject();
            foreach (var property in properties) { writer.WritePropertyName(property.Name); WriteCanonical(writer, property.Value, depth + 1); }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item, depth + 1);
            writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }
}

public sealed class FrevoPilotPlanSession
{
    private readonly FrevoPilotPlanMemory memory;
    private readonly string scopeFingerprint;
    private readonly object sync = new();
    private readonly Dictionary<string, JsonElement> pending = new(StringComparer.Ordinal);
    private string? key;
    private JsonElement? contract;
    private string? providerFingerprint;
    internal FrevoPilotPlanSession(FrevoPilotPlanMemory memory, string scopeFingerprint)
    {
        this.memory = memory;
        this.scopeFingerprint = scopeFingerprint;
    }

    public JsonElement? Contract { get { lock (sync) return contract?.Clone(); } }
    public string? ProviderFingerprint { get { lock (sync) return providerFingerprint; } }

    public void Prepare(string providerFingerprint, JsonElement contract)
    {
        lock (sync)
        {
            if (string.IsNullOrWhiteSpace(scopeFingerprint) || scopeFingerprint.Length > 4096
                || string.IsNullOrWhiteSpace(providerFingerprint) || providerFingerprint.Length > 4096
                || !FrevoPilotPlanMemory.TryCanonicalContract(contract, out var canonical))
            {
                key = null; this.contract = null; this.providerFingerprint = null; pending.Clear();
                return;
            }
            var identity = JsonSerializer.Serialize(new[] { scopeFingerprint, providerFingerprint, canonical });
            var next = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
            if (next != key) pending.Clear();
            key = next;
            this.contract = contract.Clone();
            this.providerFingerprint = providerFingerprint;
        }
    }

    public bool TryGet(out JsonElement payload)
    {
        lock (sync)
        {
            payload = default;
            return key is not null && memory.TryGet(key, out payload);
        }
    }

    public string? RememberCandidate(JsonElement payload)
    {
        lock (sync)
        {
            if (key is null || pending.Count >= 4 || !FrevoPilotPlanMemory.TryExtractPayload(payload, out var candidate)) return null;
            var receipt = Guid.NewGuid().ToString("N");
            pending.Add(receipt, candidate);
            return receipt;
        }
    }

    public bool Promote(string receipt)
    {
        lock (sync)
        {
            if (key is null || string.IsNullOrEmpty(receipt) || !pending.Remove(receipt, out var candidate)) return false;
            memory.Store(key, candidate);
            return true;
        }
    }

    public void Invalidate()
    {
        lock (sync)
        {
            pending.Clear();
            if (key is not null) memory.Invalidate(key);
        }
    }
}
