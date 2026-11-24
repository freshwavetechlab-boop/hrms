using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class RecruitmentDocumentRepository
{
    // This deletes one offer source, not the person. Candidate/Employee Master,
    // payroll, shared JDs, templates and other batches have independent owners.
    public async Task<bool> HardDeleteRowAsync(int clientId, long id, AuthUser user)
    {
        if (!RecruitmentPermissions.CanHardDeleteOffers(user)) throw new UnauthorizedAccessException();
        if (clientId <= 0 || id <= 0) throw new InvalidOperationException("Select a valid client and employee-list offer.");
        await using var db = Db(); await db.OpenAsync();
        if (!await SimpleJdTableExistsAsync(db, "recruitment_document_roster")) return false;
        await using var tx = await db.BeginTransactionAsync();
        // Intake, corrected uploads, release and mail submission take this same
        // client lock before changing roster rows. Never run DDL in this action.
        await db.ExecuteScalarAsync<int?>("SELECT Id FROM clients WHERE Id=@ClientId FOR UPDATE", new { ClientId = clientId }, tx);
        var row = await db.QuerySingleOrDefaultAsync<DeletedRosterIdentity>(
            "SELECT Id,ClientId,CandidateId,EmployeeId FROM recruitment_document_roster WHERE Id=@Id AND ClientId=@ClientId FOR UPDATE",
            new { Id = id, ClientId = clientId }, tx);
        if (row is null) return false;

        if (await SimpleJdTableExistsAsync(db, "recruitment_document_mail_deliveries", tx)
            && await SimpleJdTableExistsAsync(db, "notification_queue", tx))
        {
            var queues = (await db.QueryAsync<DeletionQueue>("""
SELECT q.Id,q.Status FROM notification_queue q
JOIN recruitment_document_mail_deliveries d ON d.QueueId=q.Id AND d.ClientId=q.ClientId
WHERE d.RosterId=@Id AND d.ClientId=@ClientId AND q.EventCode=@EventCode AND q.ResourceType='RecruitmentRosterOffer'
ORDER BY q.Id FOR UPDATE
""", new { Id = id, ClientId = clientId, EventCode = NotificationRepository.RosterOfferEvent }, tx)).ToArray();
            if (queues.Any(q => q.Status is "Processing" or "Sending"))
                throw new InvalidOperationException("An email for this offer is currently being sent. Wait for it to finish, then retry Hard delete.");
            // A worker's conditional claim cannot take a cancelled queue row.
            // Retain sent-mail receipts; deleting an offer cannot recall email.
            var pending = queues.Where(q => q.Status is "Pending" or "Retry" or NotificationRepository.RosterOfferPending or NotificationRepository.RosterOfferRetry).Select(q => q.Id).ToArray();
            if (pending.Length > 0)
                await db.ExecuteAsync("UPDATE notification_queue SET Status='Cancelled',ErrorMessage='Offer permanently deleted by Superadmin.' WHERE Id IN @Ids AND ClientId=@ClientId",
                    new { Ids = pending, ClientId = clientId }, tx);
        }

        if (await SimpleJdTableExistsAsync(db, "recruitment_document_collection_requests", tx))
            // Completed submissions stay completed. Expire only an open link
            // whose sole source is this offer; a subsequent offer can renew it.
            await db.ExecuteAsync("""
UPDATE recruitment_document_collection_requests SET
 ExpiresAtUtc=CASE WHEN Status='Open' AND OfferId IS NULL AND ApplicationId IS NULL THEN LEAST(ExpiresAtUtc,UTC_TIMESTAMP(6)) ELSE ExpiresAtUtc END,
 RosterId=NULL,UpdatedAtUtc=UTC_TIMESTAMP(6)
WHERE RosterId=@Id AND ClientId=@ClientId
""", new { Id = id, ClientId = clientId }, tx);
        if (await SimpleJdTableExistsAsync(db, "recruitment_document_roster_jds", tx))
            await db.ExecuteAsync("DELETE FROM recruitment_document_roster_jds WHERE RosterId=@Id AND ClientId=@ClientId", new { Id = id, ClientId = clientId }, tx);
        var releases = await db.ExecuteAsync("DELETE FROM recruitment_document_roster_releases WHERE RosterId=@Id AND ClientId=@ClientId", new { Id = id, ClientId = clientId }, tx);
        await db.ExecuteAsync("DELETE FROM recruitment_document_roster_audit WHERE RosterId=@Id AND ClientId=@ClientId", new { Id = id, ClientId = clientId }, tx);
        await db.ExecuteAsync("DELETE FROM recruitment_document_roster WHERE Id=@Id AND ClientId=@ClientId", new { Id = id, ClientId = clientId }, tx);
        // Keep a minimal actor receipt, without the deleted workbook inputs/PDF.
        await db.ExecuteAsync("""
INSERT INTO recruitment_document_roster_audit(RosterId,ClientId,Action,NewValuesJson,ActorUserId)
VALUES(@Id,@ClientId,'Hard delete',@Receipt,@Actor)
""", new { Id = id, ClientId = clientId, Actor = user.Id, Receipt = JsonSerializer.Serialize(new { row.CandidateId, row.EmployeeId, DeletedReleases = releases }, Json) }, tx);
        await tx.CommitAsync();
        return true;
    }

    private sealed class DeletedRosterIdentity { public long Id { get; set; } public int ClientId { get; set; } public long CandidateId { get; set; } public int? EmployeeId { get; set; } }
    private sealed class DeletionQueue { public long Id { get; set; } public string Status { get; set; } = ""; }
}
