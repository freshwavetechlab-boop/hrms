using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Services;

public sealed class LocalLlmRecoverySettings
{
    public bool Enabled { get; set; }
    public string ControlEndpointUrl { get; set; } = "";
    public string InferenceEndpointUrl { get; set; } = "";
    public string Source { get; set; } = "Not configured";
    public string Version { get; set; } = "";
    public string CredentialStatus { get; set; } = "Missing";
    [JsonIgnore] public string ApiKey { get; set; } = "";
}

public sealed class SaveLocalLlmRecoverySettings
{
    public bool Enabled { get; set; }
    public string ControlEndpointUrl { get; set; } = "";
    public string InferenceEndpointUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string Version { get; set; } = "";
}

public interface ILocalLlmRecoverySettingsStore
{
    Task<LocalLlmRecoverySettings> GetAsync(CancellationToken ct = default);
    Task<(LocalLlmRecoverySettings? Settings, string Error)> SaveAsync(SaveLocalLlmRecoverySettings request,
        RecruitmentAiScoringSettings model, AuthUser user, CancellationToken ct = default);
}

// Reuse the existing global settings table, not client setup JSON or model inference credentials.
public sealed class LocalLlmRecoverySettingsStore(IConfiguration configuration,
    PortableIntegrationCredentialProtector protector) : ILocalLlmRecoverySettingsStore
{
    internal const string ModuleCode = "local_llm_recovery:0";
    private MySqlConnection Db() => new(configuration.GetConnectionString("Default"));
    private sealed class Row
    {
        public bool IsEnabled { get; set; }
        public string SettingsJson { get; set; } = "{}";
    }
    internal sealed class Stored
    {
        public string ControlEndpointUrl { get; set; } = "";
        public string InferenceEndpointUrl { get; set; } = "";
        public string ApiKeyCipherText { get; set; } = "";
        public string Version { get; set; } = "";
    }

    public async Task<LocalLlmRecoverySettings> GetAsync(CancellationToken ct = default)
    {
        await using var db = Db(); await db.OpenAsync(ct);
        var row = await db.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            "SELECT IsEnabled,SettingsJson FROM modulesettings WHERE client_id=0 AND ModuleCode=@Code",
            new { Code = ModuleCode }, cancellationToken: ct));
        // Only a genuinely absent record uses legacy configuration. Explicit DB off,
        // corrupt data, unreadable ciphertext or a DB outage never fall back to env.
        return row is null ? LegacySettings(configuration) : Decode(row.IsEnabled, row.SettingsJson);
    }

    internal static LocalLlmRecoverySettings LegacySettings(IConfiguration configuration)
    {
        var key = configuration["LocalLlmRecovery:ApiKey"] ?? "";
        return new() { Enabled = configuration.GetValue<bool>("LocalLlmRecovery:Enabled"),
            ControlEndpointUrl = configuration["LocalLlmRecovery:ControlEndpointUrl"] ?? "",
            InferenceEndpointUrl = configuration["LocalLlmRecovery:InferenceEndpointUrl"] ?? "",
            ApiKey = key, CredentialStatus = ValidKey(key) ? "Ready" : "Missing",
            Source = configuration.GetSection("LocalLlmRecovery").Exists() ? "Server configuration" : "Not configured" };
    }

    internal LocalLlmRecoverySettings Decode(bool enabled, string json)
    {
        var stored = JsonSerializer.Deserialize<Stored>(json) ?? throw new JsonException();
        var key = protector.UnprotectRecovery(stored.ApiKeyCipherText);
        return new() { Enabled = enabled, ControlEndpointUrl = stored.ControlEndpointUrl,
            InferenceEndpointUrl = stored.InferenceEndpointUrl, Version = stored.Version, Source = "Database",
            ApiKey = key ?? "", CredentialStatus = string.IsNullOrEmpty(stored.ApiKeyCipherText) ? "Missing" : ValidKey(key) ? "Ready" : "Unreadable" };
    }

    internal static bool ValidKey(string? key) => key is not null && Regex.IsMatch(key, "\\A[a-fA-F0-9]{64}\\z");
    internal static bool ValidEndpoints(string control, string inference) =>
        SecureUri(control, out var c) && SecureUri(inference, out var i) && c!.Authority == i!.Authority && c != i;
    internal static bool SecureUri(string? value, out Uri? uri)
    {
        uri = null;
        return value?.Length <= 2048 && Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.Scheme == Uri.UriSchemeHttps
            && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);
    }

    internal string ValidateSave(SaveLocalLlmRecoverySettings request, RecruitmentAiScoringSettings model,
        LocalLlmRecoverySettings before)
    {
        if (model.ClientId != 0 || model.ProviderCode != "LocalOpenAICompatible") return "Select a global Local LLM model.";
        if (request.Version != before.Version) return "Recovery settings changed. Reopen settings before saving again.";
        if (!ValidEndpoints(request.ControlEndpointUrl, request.InferenceEndpointUrl)
            || !SecureUri(model.EndpointUrl, out var saved) || saved != new Uri(request.InferenceEndpointUrl))
            return "Use separate HTTPS control/inference URLs on the same host; inference must match this saved model.";
        var changedEndpoint = before.ControlEndpointUrl != request.ControlEndpointUrl || before.InferenceEndpointUrl != request.InferenceEndpointUrl;
        if (request.ApiKey.Length > 0 && !ValidKey(request.ApiKey)) return "Enter the separate 64-character control key, without Bearer or quotes.";
        if (changedEndpoint && request.ApiKey.Length == 0 && before.CredentialStatus != "Missing")
            return "Re-enter the control key when changing recovery endpoints.";
        if (request.Enabled && request.ApiKey.Length == 0 && before.CredentialStatus != "Ready")
            return "Re-enter the control key. The saved key is missing or cannot be decrypted by this API instance.";
        return "";
    }

    public async Task<(LocalLlmRecoverySettings? Settings, string Error)> SaveAsync(SaveLocalLlmRecoverySettings request,
        RecruitmentAiScoringSettings model, AuthUser user, CancellationToken ct = default)
    {
        if (!LocalLlmRecoveryService.IsAllowed(user)) throw new UnauthorizedAccessException();
        request.ControlEndpointUrl = (request.ControlEndpointUrl ?? "").Trim();
        request.InferenceEndpointUrl = (request.InferenceEndpointUrl ?? "").Trim();
        request.ApiKey = (request.ApiKey ?? "").Trim();
        await using var db = Db(); await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        // Serialize first-time saves too; no last-writer-wins secret replacement.
        var inserted = await db.ExecuteAsync(new CommandDefinition(
            "INSERT IGNORE INTO modulesettings(client_id,ModuleCode,IsEnabled,SettingsJson) VALUES(0,@Code,FALSE,'{}')",
            new { Code = ModuleCode }, tx, cancellationToken: ct));
        var row = await db.QuerySingleAsync<Row>(new CommandDefinition(
            "SELECT IsEnabled,SettingsJson FROM modulesettings WHERE client_id=0 AND ModuleCode=@Code FOR UPDATE",
            new { Code = ModuleCode }, tx, cancellationToken: ct));
        var before = inserted == 1 ? LegacySettings(configuration) : Decode(row.IsEnabled, row.SettingsJson);
        var error = ValidateSave(request, model, before);
        if (error.Length > 0) return (null, error); // rollback also removes the placeholder
        var stored = inserted == 1 ? new Stored() : JsonSerializer.Deserialize<Stored>(row.SettingsJson)!;
        stored.ControlEndpointUrl = request.ControlEndpointUrl;
        stored.InferenceEndpointUrl = request.InferenceEndpointUrl;
        stored.Version = Guid.NewGuid().ToString("N");
        if (request.ApiKey.Length > 0) stored.ApiKeyCipherText = protector.ProtectRecovery(request.ApiKey);
        else if (inserted == 1) stored.ApiKeyCipherText = protector.ProtectRecovery(before.ApiKey);
        var json = JsonSerializer.Serialize(stored);
        var after = Decode(request.Enabled, json);
        await db.ExecuteAsync(new CommandDefinition(
            "UPDATE modulesettings SET IsEnabled=@Enabled,SettingsJson=@Json,UpdatedAt=UTC_TIMESTAMP() WHERE client_id=0 AND ModuleCode=@Code",
            new { request.Enabled, Json = json, Code = ModuleCode }, tx, cancellationToken: ct));
        // Safe view only: neither plaintext nor ciphertext ever enters the audit.
        await db.ExecuteAsync(new CommandDefinition(@"INSERT INTO auditlogs
(UserId,UserEmail,Action,Resource,Method,Path,StatusCode,DetailsJson)
VALUES(@Id,@Email,'engine.local-llm.recovery.configure','LocalLlmRecoverySettings','PUT',@Path,200,@Details)",
            new { user.Id, user.Email, Path = $"/api/integrations/ai/{model.Id}/recovery-settings",
                Details = JsonSerializer.Serialize(new { before, after, keyChanged = request.ApiKey.Length > 0,
                    reason = "Global super-admin recovery configuration", scope = "Global" }) }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return (after, "");
    }
}
