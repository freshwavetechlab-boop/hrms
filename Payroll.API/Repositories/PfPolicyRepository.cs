using System.Data;
using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

// Policies share the existing module document, but never the mutable salary-template rows.
public sealed class PfPolicyRepository(IConfiguration configuration)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private MySqlConnection Db() => new(configuration.GetConnectionString("Default"));

    internal static async Task<List<PfPolicyVersion>> ReadVersionsAsync(MySqlConnection db, IDbTransaction? transaction, int clientId, bool locked = false)
    {
        var json = await db.ExecuteScalarAsync<string?>("SELECT JSON_EXTRACT(SettingsJson,'$.pfPolicyVersions') FROM modulesettings WHERE client_id=@ClientId AND ModuleCode='payroll'" + (locked ? " FOR UPDATE" : ""), new { ClientId = clientId }, transaction);
        return string.IsNullOrWhiteSpace(json) || json == "null" ? [] : JsonSerializer.Deserialize<List<PfPolicyVersion>>(json, JsonOptions) ?? [];
    }

    private static Task WriteAsync(MySqlConnection db, IDbTransaction transaction, int clientId, List<PfPolicyVersion> versions) =>
        db.ExecuteAsync("UPDATE modulesettings SET SettingsJson=JSON_SET(COALESCE(SettingsJson,JSON_OBJECT()),'$.pfPolicyVersions',CAST(@Json AS JSON)) WHERE client_id=@ClientId AND ModuleCode='payroll'", new { ClientId = clientId, Json = JsonSerializer.Serialize(versions, JsonOptions) }, transaction);

    public async Task<IReadOnlyList<PfPolicyVersion>> ListAsync(int clientId)
    {
        await using var db = Db(); await db.OpenAsync();
        return (await ReadVersionsAsync(db, null, clientId)).OrderByDescending(v => v.EffectiveFrom).ThenByDescending(v => v.VersionNumber).ToArray();
    }

    public async Task<(PfPolicyVersion? Item, string? Error)> SaveDraftAsync(int clientId, SavePfPolicyVersionRequest request, string actor)
    {
        var error = PfPolicyCalculator.ValidateDefinition(request);
        if (error is not null) return (null, error);
        await using var db = Db(); await db.OpenAsync(); await using var tx = await db.BeginTransactionAsync();
        if (await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM clients WHERE Id=@ClientId AND IsActive=TRUE", new { ClientId = clientId }, tx) != 1) return (null, "Select an active client.");
        await db.ExecuteAsync("INSERT INTO modulesettings(client_id,ModuleCode,IsEnabled,SettingsJson) VALUES(@ClientId,'payroll',FALSE,JSON_OBJECT()) ON DUPLICATE KEY UPDATE ModuleCode=VALUES(ModuleCode)", new { ClientId = clientId }, tx);
        var versions = await ReadVersionsAsync(db, tx, clientId, true);
        var old = string.IsNullOrWhiteSpace(request.Id) ? null : versions.SingleOrDefault(v => v.Id == request.Id);
        if (!string.IsNullOrWhiteSpace(request.Id) && old is null) return (null, "PF policy draft was not found for this client.");
        if (old is not null && old.Status != "Draft") return (null, "Published PF policy versions are immutable. Create a new draft.");
        if (old is not null && old.SalaryStructureId != request.SalaryStructureId) return (null, "A draft cannot be moved to a different salary template.");
        var setup = await PayrollDataTableStore.GetSetupJsonAsync(db, tx);
        var (rules, templateError) = ValidateTemplate(setup, clientId, request);
        if (templateError is not null) return (null, templateError);
        var version = new PfPolicyVersion
        {
            Id = old?.Id ?? Guid.NewGuid().ToString("N"), VersionNumber = old?.VersionNumber ?? versions.Where(v => v.SalaryStructureId == request.SalaryStructureId).Select(v => v.VersionNumber).DefaultIfEmpty(0).Max() + 1,
            ClientId = clientId, SalaryStructureId = request.SalaryStructureId.Trim(), Name = request.Name.Trim(), EffectiveFrom = request.EffectiveFrom,
            Reason = request.Reason.Trim(), BaseComponentCode = request.BaseComponentCode.Trim().ToUpperInvariant(), CeilingBasis = request.CeilingBasis,
            Contributions = rules!, EdliMonthlyWageCeiling = request.EdliMonthlyWageCeiling, Status = "Draft",
            CreatedAtUtc = old?.CreatedAtUtc ?? DateTime.UtcNow, CreatedBy = old?.CreatedBy ?? actor
        };
        if (old is not null) versions.Remove(old);
        versions.Add(version); await WriteAsync(db, tx, clientId, versions); await tx.CommitAsync(); return (version, null);
    }

    public async Task<(PfPolicyVersion? Item, string? Error)> PublishAsync(int clientId, string id, PublishPfPolicyVersionRequest request, string actor)
    {
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 1000) return (null, "Enter the publication reason (maximum 1000 characters).");
        await using var db = Db(); await db.OpenAsync(); await using var tx = await db.BeginTransactionAsync();
        var versions = await ReadVersionsAsync(db, tx, clientId, true);
        var version = versions.SingleOrDefault(v => v.Id == id);
        if (version is null) return (null, "PF policy draft was not found for this client.");
        if (version.Status != "Draft") return (null, "Only a draft PF policy can be published; published versions are immutable.");
        if (versions.Any(v => v.Id != id && v.Status == "Published" && v.SalaryStructureId == version.SalaryStructureId && v.EffectiveFrom == version.EffectiveFrom)) return (null, "A published PF version already starts on that date for this template.");
        var draft = new SavePfPolicyVersionRequest { Id = version.Id, SalaryStructureId = version.SalaryStructureId, Name = version.Name, EffectiveFrom = version.EffectiveFrom, Reason = request.Reason, BaseComponentCode = version.BaseComponentCode, CeilingBasis = version.CeilingBasis, Contributions = version.Contributions, EdliMonthlyWageCeiling = version.EdliMonthlyWageCeiling };
        var definitionError = PfPolicyCalculator.ValidateDefinition(draft);
        if (definitionError is not null) return (null, definitionError);
        var (rules, templateError) = ValidateTemplate(await PayrollDataTableStore.GetSetupJsonAsync(db, tx), clientId, draft);
        if (templateError is not null) return (null, templateError);
        version.Contributions = rules!; version.Status = "Published"; version.Reason = request.Reason.Trim(); version.PublishedAtUtc = DateTime.UtcNow; version.PublishedBy = actor;
        await WriteAsync(db, tx, clientId, versions); await tx.CommitAsync(); return (version, null);
    }

    internal static (List<PfContributionRule>? Rules, string? Error) ValidateTemplate(string setupJson, int clientId, SavePfPolicyVersionRequest request)
    {
        using var document = JsonDocument.Parse(setupJson);
        var root = document.RootElement;
        if (!root.TryGetProperty("salaryStructures", out var structures) || !root.TryGetProperty("salaryComponents", out var components)) return (null, "Salary template and components are required.");
        var structure = structures.EnumerateArray().FirstOrDefault(s => Text(s, "id") == request.SalaryStructureId && Text(s, "clientId").Split(':')[0] == clientId.ToString() && Active(s));
        if (structure.ValueKind == JsonValueKind.Undefined || !structure.TryGetProperty("lines", out var lines)) return (null, "Select an active salary template belonging to this client.");
        var masters = components.EnumerateArray().Where(Active).ToDictionary(c => Text(c, "id"));
        var templateLines = lines.EnumerateArray().ToDictionary(l => Text(l, "componentId"));
        var baseComponent = masters.Values.FirstOrDefault(c => Text(c, "code").Equals(request.BaseComponentCode.Trim(), StringComparison.OrdinalIgnoreCase) && Text(c, "category") == "Earning" && templateLines.ContainsKey(Text(c, "id")));
        if (baseComponent.ValueKind == JsonValueKind.Undefined) return (null, "The base must be an active earning component in the selected template.");
        var baseId = Text(baseComponent, "id"); var baseLine = templateLines[baseId];
        var baseFormula = FirstText(Text(baseLine, "formula"), Text(baseLine, "value"), Text(baseComponent, "formula"), Text(baseComponent, "value"));
        var baseProRata = FirstText(Text(baseLine, "proRataOverride"), Text(baseComponent, "proRata"));
        if (!baseProRata.Equals("true", StringComparison.OrdinalIgnoreCase) || baseFormula.Contains("PAYABLE_DAYS", StringComparison.OrdinalIgnoreCase) || baseFormula.Contains("PRESENT_DAYS", StringComparison.OrdinalIgnoreCase) || baseFormula.Contains("_EARNED", StringComparison.OrdinalIgnoreCase)) return (null, "PF base must be a monthly earning prorated once by payroll, not an already earned/day-based formula.");
        var basePriority = int.TryParse(Text(baseComponent, "priority"), out var bp) ? bp : 999;
        var mapped = new List<PfContributionRule>();
        foreach (var requested in request.Contributions)
        {
            if (!masters.TryGetValue(requested.ComponentId, out var component) || !templateLines.ContainsKey(requested.ComponentId)) return (null, "Every mapped PF component must be active and present in the selected template.");
            var type = Text(component, "statutoryType"); var category = Text(component, "category");
            if (type is not ("PF Employee" or "PF Employer") || (type == "PF Employee" && category != "Deduction") || (type == "PF Employer" && category != "Benefit")) return (null, "Map PF Employee deductions or PF Employer benefits only. EPS, ESI and other components retain their existing formulas.");
            var priority = int.TryParse(Text(component, "priority"), out var cp) ? cp : 999;
            if (priority <= basePriority) return (null, "The monthly base must have an earlier calculation priority than the mapped PF component.");
            mapped.Add(new() { ComponentId = requested.ComponentId, ComponentCode = Text(component, "code"), StatutoryType = type, RatePercent = requested.RatePercent, MonthlyWageCeiling = requested.MonthlyWageCeiling });
        }
        if (mapped.Count(r => r.StatutoryType == "PF Employee") != 1) return (null, "Map exactly one employee PF component; employer PF mappings are optional.");
        return (mapped, null);
    }

    private static bool Active(JsonElement value) => !value.TryGetProperty("active", out var active) || active.ValueKind != JsonValueKind.False;
    private static string Text(JsonElement value, string property) => value.TryGetProperty(property, out var item) ? item.ToString() : "";
    private static string FirstText(params string[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";
}
