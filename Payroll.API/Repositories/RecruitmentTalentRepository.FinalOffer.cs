using System.Globalization;
using System.Text.Json;
using Dapper;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class RecruitmentTalentRepository
{
    internal const string FinalOfferResource = "RecruitmentFinalOffer";
    private const string FinalDocumentEvent = "FinalDocumentGenerated";

    // Reuse workflow instances/history as approval versions and immutable document receipts.
    // Never reset Accepted, replace the original letter, or infer approval from acceptance.
    public async Task<(RecruitmentOffer? Row, string Error)> RequestFinalOfferAsync(long offerId, AuthUser user,
        CancellationToken cancellationToken = default)
    {
        await using var db = Db();
        await db.OpenAsync(cancellationToken);
        await using var tx = await db.BeginTransactionAsync(cancellationToken);
        var offer = await db.QueryFirstOrDefaultAsync<RecruitmentOffer>(
            @"SELECT o.*,TRIM(CONCAT(COALESCE(c.FirstName,''),' ',COALESCE(c.LastName,''))) CandidateName,p.PositionTitle
FROM recruitment_offers o JOIN recruitment_candidate_applications a ON a.Id=o.ApplicationId
JOIN recruitment_candidates c ON c.Id=a.CandidateId JOIN recruitment_open_positions p ON p.Id=a.PositionId
WHERE o.Id=@Id AND (@ClientId IS NULL OR o.ClientId=@ClientId) FOR UPDATE",
            new { Id = offerId, user.ClientId }, tx);
        if (offer is null) return (null, "Offer was not found.");
        if (offer.Status != "Accepted") return (null, "Candidate acceptance is required before final departmental approval.");
        var clientId = offer.ClientId;
        await db.ExecuteScalarAsync<int>("SELECT Id FROM clients WHERE Id=@Id FOR UPDATE", new { Id = clientId }, tx);
        var signing = await ResolveSigningSettingsAsync(db, clientId, tx);
        var approverId = signing.FinalApproverUserId ?? 0;
        if (!signing.Enabled || approverId <= 0)
            return (null, "Configure and enable this client's post-acceptance final signatory first.");
        var valid = await db.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM authusers WHERE Id=@Id AND IsActive=TRUE AND (ClientId IS NULL OR ClientId=@ClientId)",
            new { Id = approverId, ClientId = clientId }, tx);
        if (valid != 1) return (null, "The configured final signatory is inactive or outside this client.");
        var latest = await db.QueryFirstOrDefaultAsync<WorkflowInstance>(
            "SELECT * FROM workflowinstances WHERE ResourceType=@Type AND ResourceId=@Id ORDER BY Id DESC LIMIT 1",
            new { Type = FinalOfferResource, Id = offerId.ToString(CultureInfo.InvariantCulture) }, tx);
        if (latest?.Status is "Pending" or "Approved")
        {
            await tx.CommitAsync(cancellationToken);
            if (latest.Status == "Approved") return await GenerateFinalOfferAsync(offerId, latest.Id, user, cancellationToken);
            return ((await OfferRowsAsync(db, user, offer.ApplicationId, null)).First(row => row.Id == offerId), "");
        }
        var code = $"FINAL_OFFER_{clientId}_{approverId}";
        await db.ExecuteAsync(@"INSERT INTO workflowmasters(ClientId,Code,Name,ResourceType,IsActive)
VALUES(@ClientId,@Code,'Final offer: departmental approval',@Type,TRUE) ON DUPLICATE KEY UPDATE Id=Id",
            new { ClientId = clientId, Code = code, Type = FinalOfferResource }, tx);
        var workflowId = await db.ExecuteScalarAsync<int>(
            "SELECT Id FROM workflowmasters WHERE ClientId=@ClientId AND Code=@Code AND IsActive=TRUE FOR UPDATE",
            new { ClientId = clientId, Code = code }, tx);
        if (workflowId <= 0) return (null, "The final offer approval workflow is inactive.");
        await db.ExecuteAsync(@"INSERT IGNORE INTO workflowstages(WorkflowId,StageOrder,Name,ApproverType,ApproverUserId)
VALUES(@Id,1,'Approve final signed offer','Specific User',@ApproverId)", new { Id = workflowId, ApproverId = approverId }, tx);
        var stages = (await db.QueryAsync<WorkflowStage>("SELECT * FROM workflowstages WHERE WorkflowId=@Id", new { Id = workflowId }, tx)).ToList();
        if (stages.Count != 1 || stages[0].ApproverType != "Specific User" || stages[0].ApproverUserId != approverId)
            return (null, "Final offer workflow must match the configured authorized signatory.");
        // Snapshot the bundled reference wording; existing generic/issued templates remain untouched.
        var body = BrandedOfferPdfService.DefaultTemplate;
        var instance = await workflows.StartInTransactionAsync(db, tx, new StartWorkflowRequest
        {
            WorkflowId = workflowId, ResourceType = FinalOfferResource, ResourceId = offerId.ToString(CultureInfo.InvariantCulture),
            PayloadJson = JsonSerializer.Serialize(new { OfferId = offerId, offer.OfferNumber, offer.ApplicationId,
                offer.ClientId, offer.CandidateName, offer.PositionTitle, offer.OfferedCtc, offer.Currency, offer.ProposedJoiningDate, OfferTemplateId = 0L,
                OfferLetterTemplateHash = BrandedOfferPdfService.TemplateHash(body), FinalOfferBody = body,
                OriginalOfferAttachment = offer.OfferLetterAttachmentPublicId, FinalApproverUserId = approverId })
        }, user.Id);
        if (instance is null) return (null, "The departmental approval task could not be created.");
        await tx.CommitAsync(cancellationToken);
        return ((await OfferRowsAsync(db, user, offer.ApplicationId, null)).First(row => row.Id == offerId), "");
    }

    public async Task TryRequestAcceptedFinalOfferAsync(long offerId, AuthUser user)
    {
        try
        {
            await using var db = Db(); await db.OpenAsync();
            var clientId = await db.ExecuteScalarAsync<int?>("SELECT ClientId FROM recruitment_offers WHERE Id=@Id AND (@ClientId IS NULL OR ClientId=@ClientId)", new { Id = offerId, user.ClientId });
            if (!clientId.HasValue || !(await ResolveSigningSettingsAsync(db, clientId.Value)).Enabled) return;
            var result = await RequestFinalOfferAsync(offerId, user);
            if (result.Row is null) logger.LogWarning("Offer {OfferId} final approval needs attention: {Reason}", offerId, result.Error);
        }
        catch (Exception exception)
        {
            // Acceptance is already committed. A document/configuration outage must not report a failed acceptance.
            logger.LogError(exception, "Accepted offer {OfferId} final approval needs retry from Offers", offerId);
        }
    }

    public async Task TryRequestAcceptedFinalOfferForApplicationAsync(long applicationId, AuthUser user)
    {
        try
        {
            await using var db = Db();
            var id = await db.ExecuteScalarAsync<long?>(@"SELECT Id FROM recruitment_offers
WHERE ApplicationId=@ApplicationId AND Status='Accepted' AND (@ClientId IS NULL OR ClientId=@ClientId) ORDER BY Id DESC LIMIT 1",
                new { ApplicationId = applicationId, user.ClientId });
            if (id.HasValue) await TryRequestAcceptedFinalOfferAsync(id.Value,user);
        }
        catch (Exception exception) { logger.LogError(exception, "Accepted application {ApplicationId} final approval needs retry", applicationId); }
    }

    public async Task<(RecruitmentOffer? Row, string Error)> GenerateFinalOfferAsync(long offerId, long instanceId,
        AuthUser user, CancellationToken cancellationToken = default)
    {
        await using var db = Db();
        await db.OpenAsync(cancellationToken);
        var key = $"final-offer:{db.Database}:{offerId}";
        if (await db.ExecuteScalarAsync<int>("SELECT GET_LOCK(@Key,0)", new { Key = key }) != 1)
            return (null, "This offer's final letter is already being processed. Refresh shortly.");
        try
        {
            var result = await GenerateOfferLetterAsync(offerId, user, "", "Final departmental approval", cancellationToken, instanceId);
            if (result.Row is null)
                await db.ExecuteAsync(@"INSERT INTO workflowhistory(InstanceId,Action,ActorUserId,Comment)
SELECT Id,'FinalDocumentFailed',@UserId,@Error FROM workflowinstances WHERE Id=@Id AND ResourceType=@Type AND ResourceId=@OfferId",
                    new { Id = instanceId, Type = FinalOfferResource, OfferId = offerId.ToString(CultureInfo.InvariantCulture), UserId = user.Id, Error = result.Error[..Math.Min(450, result.Error.Length)] });
            return result;
        }
        finally { await db.ExecuteScalarAsync<int>("SELECT RELEASE_LOCK(@Key)", new { Key = key }); }
    }

    private sealed class FinalOfferApproval
    {
        public long Id { get; set; }
        public string Status { get; set; } = "";
        public string PayloadJson { get; set; } = "{}";
        public string? AttachmentId { get; set; }
    }
}
