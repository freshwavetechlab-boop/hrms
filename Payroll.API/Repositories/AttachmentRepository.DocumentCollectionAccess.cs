using Dapper;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public partial class AttachmentRepository
{
    private volatile bool documentCollectionMembershipReady;
    private async Task<bool> HasDocumentCollectionMembershipAsync(MySqlConnector.MySqlConnection db)
    {
        if (documentCollectionMembershipReady) return true;
        documentCollectionMembershipReady = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES
WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME IN ('recruitment_document_collection_requests','recruitment_document_collection_files')") == 2;
        return documentCollectionMembershipReady;
    }
    // A shared talent-pool candidate can have applications for several clients.
    // Joining documents belong to the client/request that collected them, never
    // to every client that can see the candidate's resume.
    private async Task<bool?> DocumentCollectionAccessAsync(EntityAttachment attachment, AuthUser user, string action = "Read")
    {
        if (attachment.EntityType != "CANDIDATE") return null;
        await using var db = Connection(); await db.OpenAsync();
        if (!await HasDocumentCollectionMembershipAsync(db)) return null;
        var request = await db.QuerySingleOrDefaultAsync<DocumentCollectionAccessRow>(@"SELECT r.ApplicationId,r.RosterId,r.ClientId,r.CandidateId,r.Status
FROM recruitment_document_collection_files f JOIN recruitment_document_collection_requests r ON r.Id=f.RequestId
WHERE f.AttachmentId=@Id", attachment);
        if (request is null) return null;
        if (request.ClientId != attachment.ClientId || request.CandidateId != attachment.EntityId || request.Status != "Completed") return false;
        if (user.Id <= 0 || !user.IsActive || user.MustChangePassword || !SelectedClientAccess.Allows(user, attachment.ClientId)
            || user.ClientId.HasValue && user.ClientId != attachment.ClientId) return false;
        var permissions = action == "Manage"
            ? new[] { "attachment.recruitment.upload", "recruitment.manage", "settings.manage", "security.manage" }
            : action == "Verify" ? new[] { "attachment.recruitment.verify", "recruitment.manage", "security.manage" }
            : new[] { "attachment.recruitment.view", "recruitment.manage", "security.manage" };
        if (!HasAnyPermission(user, permissions)) return false;
        var scopedUser = SelectedClientAccess.ForRequest(user, attachment.ClientId);
        if (request.ApplicationId is > 0)
            return await RecruitmentPipelineStageAccess.AllowsApplicationAsync(db, request.ApplicationId.Value, scopedUser)
                && await RecruitmentAccessScope.CanAccessApplicationWorkLocationAsync(db, scopedUser, request.ApplicationId.Value);
        if (request.RosterId is not > 0) return false;
        var roster = await db.QuerySingleOrDefaultAsync<DocumentCollectionLocation>("SELECT WorkLocationId FROM recruitment_document_roster WHERE Id=@Id AND ClientId=@ClientId AND CandidateId=@CandidateId", new { Id = request.RosterId, attachment.ClientId, CandidateId = attachment.EntityId });
        return roster is not null && await RecruitmentAccessScope.CanAccessWorkLocationIdAsync(db, scopedUser, attachment.ClientId, roster.WorkLocationId);
    }

    private async Task<List<EntityAttachment>> CandidateDocumentCollectionAttachmentsAsync(long candidateId, AuthUser user)
    {
        await using var db = Connection(); await db.OpenAsync();
        if (!await HasDocumentCollectionMembershipAsync(db)) return [];
        var rows = await db.QueryAsync<EntityAttachment>(AttachmentSelect + @"
WHERE a.entity_type='CANDIDATE' AND a.entity_id=@Id AND a.is_current=TRUE AND a.is_deleted=FALSE
AND EXISTS (SELECT 1 FROM recruitment_document_collection_files collected WHERE collected.AttachmentId=a.id)
ORDER BY f.display_order,a.uploaded_at_utc DESC", new { Id = candidateId });
        var result = new List<EntityAttachment>();
        foreach (var row in rows)
            if (await DocumentCollectionAccessAsync(row, user) == true) result.Add(row);
        return result;
    }

    private sealed class DocumentCollectionAccessRow { public long? ApplicationId { get; set; } public long? RosterId { get; set; } public int ClientId { get; set; } public long CandidateId { get; set; } public string Status { get; set; } = ""; }
    private sealed class DocumentCollectionLocation { public int? WorkLocationId { get; set; } }
}
