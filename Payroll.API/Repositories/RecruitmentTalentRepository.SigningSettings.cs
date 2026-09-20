using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public sealed partial class RecruitmentTalentRepository
{
    // Client suffix also supports older installations with a unique ModuleCode index.
    private static string SigningSettingsKey(int clientId) => $"recruitment_final_offer_signing:{clientId}";
    private static bool CanConfigureSigning(AuthUser user) => user.ClientId is null
        && user.Roles.Contains("super_admin", StringComparer.OrdinalIgnoreCase);

    private async Task<RecruitmentFinalOfferSettings> ResolveSigningSettingsAsync(MySqlConnection db, int clientId, MySqlTransaction? tx = null)
    {
        var saved = await db.QueryFirstOrDefaultAsync<RecruitmentFinalOfferSettings>(@"SELECT client_id ClientId,IsEnabled Enabled,
CAST(NULLIF(JSON_UNQUOTE(JSON_EXTRACT(SettingsJson,'$.FinalApproverUserId')),'null') AS UNSIGNED) FinalApproverUserId,
'Portal' Source FROM modulesettings WHERE client_id=@ClientId AND ModuleCode=@Code",
            new { ClientId = clientId, Code = SigningSettingsKey(clientId) }, tx);
        if (saved is not null) return saved; // Explicit off overrides the legacy server setting.
        var legacyClient = configuration.GetValue<int>("OfferSigning:Uidai:ClientId") == clientId;
        return new RecruitmentFinalOfferSettings { ClientId = clientId,
            Enabled = legacyClient && configuration.GetValue<bool>("OfferSigning:Uidai:PostAcceptanceEnabled"),
            FinalApproverUserId = legacyClient ? configuration.GetValue<int?>("OfferSigning:Uidai:FinalApproverUserId") : null,
            Source = legacyClient ? "Server configuration" : "Not configured" };
    }

    public async Task<RecruitmentFinalOfferSettings?> GetFinalOfferSettingsAsync(int clientId, AuthUser user)
    {
        if (!CanConfigureSigning(user) || clientId <= 0) return null;
        await using var db = Db(); await db.OpenAsync();
        if (await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM clients WHERE Id=@Id", new { Id = clientId }) != 1) return null;
        return await ResolveSigningSettingsAsync(db, clientId);
    }

    public async Task<(RecruitmentFinalOfferSettings? Row, string Error)> SaveFinalOfferSettingsAsync(int clientId,
        SaveRecruitmentFinalOfferSettings request, AuthUser user)
    {
        if (!CanConfigureSigning(user)) return (null, "Only a global super admin can configure signing authority.");
        if (clientId <= 0) return (null, "Select a client first.");
        if (request.Enabled && request.FinalApproverUserId is not > 0) return (null, "Select the authorized final signatory.");
        await using var db = Db(); await db.OpenAsync();
        await using var tx = await db.BeginTransactionAsync();
        if (await db.ExecuteScalarAsync<int?>("SELECT Id FROM clients WHERE Id=@Id FOR UPDATE", new { Id = clientId }, tx) is null)
            return (null, "Client was not found.");
        if (request.FinalApproverUserId.HasValue && await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM authusers
WHERE Id=@Id AND IsActive=TRUE AND (ClientId IS NULL OR ClientId=@ClientId)",
            new { Id = request.FinalApproverUserId, ClientId = clientId }, tx) != 1)
            return (null, "Select an active user belonging to this client or the global organization.");
        var before = await ResolveSigningSettingsAsync(db, clientId, tx);
        if (before.FinalApproverUserId != request.FinalApproverUserId && await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*)
FROM workflowinstances w JOIN recruitment_offers o ON w.ResourceId=CAST(o.Id AS CHAR)
WHERE w.ResourceType='RecruitmentFinalOffer' AND o.ClientId=@ClientId
AND (w.Status='Pending' OR (w.Status='Approved' AND NOT EXISTS
 (SELECT 1 FROM workflowhistory h WHERE h.InstanceId=w.Id AND h.Action='FinalDocumentGenerated')))",
            new { ClientId = clientId }, tx) > 0)
            return (null, "Complete or reject pending final-letter approvals and finish document retries before changing the signatory.");
        var json = JsonSerializer.Serialize(new { request.FinalApproverUserId });
        await db.ExecuteAsync(@"INSERT INTO modulesettings(client_id,ModuleCode,IsEnabled,SettingsJson)
VALUES(@ClientId,@Code,@Enabled,@Json)
ON DUPLICATE KEY UPDATE IsEnabled=@Enabled,SettingsJson=@Json,UpdatedAt=UTC_TIMESTAMP()",
            new { ClientId = clientId, Code = SigningSettingsKey(clientId), request.Enabled, Json = json }, tx);
        await db.ExecuteAsync(@"INSERT INTO recruitment_audit(EntityType,EntityId,Action,OldValueJson,NewValueJson,ChangedByUserId)
VALUES('RecruitmentFinalOfferSettings',@ClientId,'Configure final signatory',@Before,@After,@UserId)",
            new { ClientId = clientId, Before = JsonSerializer.Serialize(before), After = JsonSerializer.Serialize(request), UserId = user.Id }, tx);
        await tx.CommitAsync();
        return (await ResolveSigningSettingsAsync(db, clientId), "");
    }
}
