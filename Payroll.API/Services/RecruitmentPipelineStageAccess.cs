using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Services;

// Access is explicit and separate from the displayed owner colour. Legacy
// stages without a policy retain their existing recruitment permissions.
public static class RecruitmentPipelineStageAccess
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static string Key(long versionId) => $"recruitment_pipeline_access_{versionId}";
    internal static bool IsSystem(AuthUser user) => user.Id == 0 && user.IsActive && user.DisplayName is
        "Recruitment automation" or "Public recruitment portal" or "External candidate portal" or "Public recruitment resume worker" or "External candidate";
    internal static bool IsAdministrator(AuthUser user) => RecruitmentPermissions.IsAdmin(user);

    internal static bool Allows(AuthUser user, int clientId, RecruitmentStageAccessPolicy? policy)
    {
        if (!IsSystem(user) && (user.Id <= 0 || !user.IsActive || user.MustChangePassword)) return false;
        if (!SelectedClientAccess.Allows(user, clientId) || user.ClientId.HasValue && user.ClientId != clientId) return false;
        if (IsSystem(user) || IsAdministrator(user)) return true;
        return policy is not { Restricted: true } || policy.AllowedUserIds?.Contains(user.Id) == true
            || policy.AllowedRoleCodes?.Any(role => user.Roles.Contains(role, StringComparer.OrdinalIgnoreCase)) == true;
    }

    internal static async Task<Dictionary<string, RecruitmentStageAccessPolicy>> ReadAsync(MySqlConnection db, int clientId, long versionId, MySqlTransaction? tx = null)
    {
        string? json;
        try { json = await db.ExecuteScalarAsync<string?>("SELECT SettingsJson FROM modulesettings WHERE client_id=@ClientId AND ModuleCode=@Key AND IsEnabled=TRUE", new { ClientId = clientId, Key = Key(versionId) }, tx); }
        catch (MySqlException error) when (error.Number == 1146) { return new(StringComparer.OrdinalIgnoreCase); }
        if (string.IsNullOrWhiteSpace(json)) return new(StringComparer.OrdinalIgnoreCase);
        // Invalid saved policy never falls back to unrestricted access.
        return new(JsonSerializer.Deserialize<Dictionary<string, RecruitmentStageAccessPolicy>>(json, Json) ?? throw new InvalidOperationException("Review the saved pipeline stage access policy."), StringComparer.OrdinalIgnoreCase);
    }

    internal static RecruitmentStageAccessPolicy Normalize(RecruitmentStageAccessPolicy? policy)
    {
        policy ??= new();
        if (!new[] { "Unassigned", "GAD", "Client", "Shared" }.Contains(policy.OwnerType)) throw new InvalidOperationException("Select a valid stage owner: Unassigned, GAD, Client or Shared.");
        return new() { OwnerType = policy.OwnerType, DepartmentLabel = (policy.DepartmentLabel ?? "").Trim(), Restricted = policy.Restricted,
            AllowedRoleCodes = (policy.AllowedRoleCodes ?? []).Select(x => (x ?? "").Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList(),
            AllowedUserIds = (policy.AllowedUserIds ?? []).Distinct().Order().ToList() };
    }

    internal static async Task<string> ValidateSaveAsync(MySqlConnection db, int clientId, long versionId, IReadOnlyList<RecruitmentPipelineStage> stages, AuthUser user)
    {
        var previous = versionId > 0 ? await ReadAsync(db, clientId, versionId) : [];
        var policies = stages.Select(s => Normalize(s.AccessPolicy)).ToArray();
        if (!IsAdministrator(user) && (previous.Values.Any(p => p.Restricted) || policies.Any(p => p.Restricted)))
            return "A platform administrator must configure or revise restricted stage access.";
        if (policies.Any(p => p.DepartmentLabel.Length > 120 || p.AllowedRoleCodes.Count > 100 || p.AllowedUserIds.Count > 1000 || p.AllowedUserIds.Any(id => id <= 0) || p.AllowedRoleCodes.Any(r => r.Length > 100)))
            return "Stage access has an invalid department, role or user selection.";
        var ids = policies.SelectMany(p => p.AllowedUserIds).Distinct().ToArray();
        if (ids.Length > 0 && await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM authusers WHERE Id IN @Ids AND IsActive=TRUE AND (ClientId IS NULL OR ClientId=@ClientId)", new { Ids = ids, ClientId = clientId }) != ids.Length)
            return "One or more stage users are inactive or belong to another client.";
        var roles = policies.SelectMany(p => p.AllowedRoleCodes).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (roles.Length > 0 && await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM authroles WHERE Code IN @Roles", new { Roles = roles }) != roles.Length)
            return "One or more selected stage roles no longer exist.";
        return "";
    }

    internal static async Task SaveAsync(MySqlConnection db, MySqlTransaction tx, int clientId, long versionId, IEnumerable<RecruitmentPipelineStage> stages)
    {
        var policies = stages.ToDictionary(s => s.StageCode.Trim().ToUpperInvariant(), s => Normalize(s.AccessPolicy), StringComparer.OrdinalIgnoreCase);
        await db.ExecuteAsync(@"INSERT INTO modulesettings(client_id,ModuleCode,IsEnabled,SettingsJson) VALUES(@ClientId,@Key,TRUE,@Json)
ON DUPLICATE KEY UPDATE IsEnabled=TRUE,SettingsJson=@Json", new { ClientId = clientId, Key = Key(versionId), Json = JsonSerializer.Serialize(policies, Json) }, tx);
    }

    internal sealed class StageReference { public long Id { get; set; } public int ClientId { get; set; } public long PipelineVersionId { get; set; } public string StageCode { get; set; } = ""; }
    internal static async Task<HashSet<long>> AllowedStageIdsAsync(MySqlConnection db, IEnumerable<long> stageIds, AuthUser user, MySqlTransaction? tx = null)
    {
        var ids = stageIds.Where(id => id > 0).Distinct().ToArray(); if (ids.Length == 0) return [];
        var stages = (await db.QueryAsync<StageReference>(@"SELECT s.Id,d.ClientId,s.PipelineVersionId,s.StageCode FROM recruitment_pipeline_stages s
JOIN recruitment_pipeline_versions v ON v.Id=s.PipelineVersionId JOIN recruitment_pipeline_definitions d ON d.Id=v.PipelineDefinitionId WHERE s.Id IN @Ids", new { Ids = ids }, tx)).ToList();
        var allowed = new HashSet<long>();
        foreach (var group in stages.GroupBy(s => (s.ClientId, s.PipelineVersionId)))
        {
            var policies = await ReadAsync(db, group.Key.ClientId, group.Key.PipelineVersionId, tx);
            foreach (var stage in group) if (Allows(user, stage.ClientId, policies.GetValueOrDefault(stage.StageCode))) allowed.Add(stage.Id);
        }
        return allowed;
    }

    internal static async Task<bool> AllowsStageAsync(MySqlConnection db, long stageId, AuthUser user, MySqlTransaction? tx = null) => (await AllowedStageIdsAsync(db, [stageId], user, tx)).Contains(stageId);
    internal sealed class ApplicationReference { public long Id { get; set; } public int ClientId { get; set; } public long? StageId { get; set; } }
    internal static async Task<bool> HasPoliciesAsync(MySqlConnection db, MySqlTransaction? tx = null)
    {
        try { return await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM modulesettings WHERE ModuleCode LIKE 'recruitment_pipeline_access_%' AND IsEnabled=TRUE)", transaction: tx); }
        catch (MySqlException error) when (error.Number == 1146) { return false; }
    }
    internal static async Task<HashSet<long>> AllowedApplicationIdsAsync(MySqlConnection db, IEnumerable<long> applicationIds, AuthUser user, MySqlTransaction? tx = null)
    {
        var ids = applicationIds.Where(id => id > 0).Distinct().ToArray(); if (ids.Length == 0) return [];
        if (!IsSystem(user) && (user.Id <= 0 || !user.IsActive || user.MustChangePassword)) return [];
        if (IsSystem(user) || IsAdministrator(user) || !await HasPoliciesAsync(db, tx))
        {
            var legacy = await db.QueryAsync<ApplicationReference>("SELECT Id,ClientId FROM recruitment_candidate_applications WHERE Id IN @Ids", new { Ids = ids }, tx);
            return legacy.Where(r => SelectedClientAccess.Allows(user, r.ClientId) && (!user.ClientId.HasValue || user.ClientId == r.ClientId)).Select(r => r.Id).ToHashSet();
        }
        var rows = (await db.QueryAsync<ApplicationReference>(@"SELECT a.Id,a.ClientId,s.PipelineStageId StageId FROM recruitment_candidate_applications a
LEFT JOIN recruitment_application_pipeline_instances p ON p.ApplicationId=a.Id LEFT JOIN recruitment_application_stage_instances s ON s.Id=p.CurrentStageInstanceId WHERE a.Id IN @Ids", new { Ids = ids }, tx)).ToList();
        var allowed = await AllowedStageIdsAsync(db, rows.Where(r => r.StageId > 0).Select(r => r.StageId!.Value), user, tx);
        return rows.Where(r => SelectedClientAccess.Allows(user, r.ClientId) && (!user.ClientId.HasValue || user.ClientId == r.ClientId) && (r.StageId is not > 0 || allowed.Contains(r.StageId.Value))).Select(r => r.Id).ToHashSet();
    }
    internal static async Task<bool> AllowsApplicationAsync(MySqlConnection db, long id, AuthUser user, MySqlTransaction? tx = null) => (await AllowedApplicationIdsAsync(db, [id], user, tx)).Contains(id);
    internal static async Task<bool> AllowsCaseAsync(MySqlConnection db, long id, AuthUser user, MySqlTransaction? tx = null)
    {
        var stage = await db.ExecuteScalarAsync<long?>("SELECT s.PipelineStageId FROM recruitment_position_pipeline_instances p JOIN recruitment_position_stage_instances s ON s.Id=p.CurrentStageInstanceId WHERE p.Id=@Id", new { Id = id }, tx);
        return stage is > 0 && await AllowsStageAsync(db, stage.Value, user, tx);
    }
    internal static async Task<HashSet<long>> AllowedCaseIdsAsync(MySqlConnection db, IEnumerable<long> ids, AuthUser user)
    {
        var input = ids.Distinct().ToArray(); if (input.Length == 0) return [];
        var rows = (await db.QueryAsync<ApplicationReference>("SELECT p.Id,p.ClientId,s.PipelineStageId StageId FROM recruitment_position_pipeline_instances p LEFT JOIN recruitment_position_stage_instances s ON s.Id=p.CurrentStageInstanceId WHERE p.Id IN @Ids", new { Ids = input })).ToArray();
        var allowed = await AllowedStageIdsAsync(db, rows.Where(r => r.StageId > 0).Select(r => r.StageId!.Value), user);
        return rows.Where(r => SelectedClientAccess.Allows(user, r.ClientId) && (r.StageId is not > 0 || allowed.Contains(r.StageId.Value))).Select(r => r.Id).ToHashSet();
    }
    internal static async Task<bool> AllowsCandidateAsync(MySqlConnection db, long id, AuthUser user)
    {
        if (!IsSystem(user) && (user.Id <= 0 || !user.IsActive || user.MustChangePassword)) return false;
        var apps = (await db.QueryAsync<long>("SELECT Id FROM recruitment_candidate_applications WHERE CandidateId=@Id AND (@ClientId IS NULL OR ClientId=@ClientId)", new { Id = id, user.ClientId })).ToArray();
        return apps.Length == 0 || (await AllowedApplicationIdsAsync(db, apps, user)).Count > 0;
    }
    internal sealed class CandidateApplication { public long CandidateId { get; set; } public long Id { get; set; } }
    internal static async Task<HashSet<long>> AllowedCandidateIdsAsync(MySqlConnection db, IEnumerable<long> ids, AuthUser user)
    {
        var input = ids.Distinct().ToArray(); if (input.Length == 0) return [];
        if (!IsSystem(user) && (user.Id <= 0 || !user.IsActive || user.MustChangePassword)) return [];
        if (IsAdministrator(user) || IsSystem(user) || !await HasPoliciesAsync(db)) return input.ToHashSet();
        var apps = (await db.QueryAsync<CandidateApplication>("SELECT Id,CandidateId FROM recruitment_candidate_applications WHERE CandidateId IN @Ids AND (@ClientId IS NULL OR ClientId=@ClientId)", new { Ids = input, user.ClientId })).ToArray();
        var permitted = await AllowedApplicationIdsAsync(db, apps.Select(a => a.Id), user);
        var withApps = apps.Select(a => a.CandidateId).ToHashSet();
        return input.Where(id => !withApps.Contains(id)).Concat(apps.Where(a => permitted.Contains(a.Id)).Select(a => a.CandidateId)).ToHashSet();
    }
    private sealed class DocumentReference { public long? ApplicationId { get; set; } public long? HiringCaseId { get; set; } public long? PipelineStageId { get; set; } }
    internal static async Task<bool> AllowsDocumentAsync(MySqlConnection db, long documentId, AuthUser user)
    {
        var doc = await db.QueryFirstOrDefaultAsync<DocumentReference>("SELECT ApplicationId,HiringCaseId,PipelineStageId FROM recruitment_process_documents WHERE Id=@Id", new { Id = documentId });
        if (doc is null) return false;
        if (doc.PipelineStageId > 0 && !await AllowsStageAsync(db, doc.PipelineStageId.Value, user)) return false;
        if (doc.HiringCaseId > 0) return await AllowsCaseAsync(db, doc.HiringCaseId.Value, user);
        return doc.ApplicationId is not > 0 || await AllowsApplicationAsync(db, doc.ApplicationId.Value, user);
    }
}
