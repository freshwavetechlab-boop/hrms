using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.AspNetCore.DataProtection;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public sealed class RecruitmentCandidateActionRepository(
    IConfiguration configuration,
    RecruitmentFormRepository forms,
    IDataProtectionProvider dataProtectionProvider)
{
    private static readonly HashSet<string> Purposes = new(StringComparer.OrdinalIgnoreCase)
    {
        "DOCUMENT_REQUEST", "OFFER_RESPONSE", "PROFILE_UPDATE"
    };
    private readonly IDataProtector tokenProtector = dataProtectionProvider.CreateProtector("Payroll.API.RecruitmentCandidateActionToken.v1");

    private MySqlConnection Db() => new(configuration.GetConnectionString("Default"));

    public async Task InitializeAsync()
    {
        await using var db = Db();
        await db.OpenAsync();
        await db.ExecuteAsync(@"
CREATE TABLE IF NOT EXISTS recruitment_candidate_action_sessions (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    ClientId INT NOT NULL,
    ApplicationId BIGINT NOT NULL,
    CandidateId BIGINT NOT NULL,
    PipelineStageInstanceId BIGINT NULL,
    FormVersionId BIGINT NULL,
    FormSubmissionId BIGINT NULL,
    OfferId BIGINT NULL,
    PurposeCode VARCHAR(60) NOT NULL,
    TokenHash CHAR(64) NOT NULL,
    TokenCipherText TEXT NOT NULL,
    Status VARCHAR(30) NOT NULL DEFAULT 'Open',
    Instructions VARCHAR(1500) NOT NULL DEFAULT '',
    MaximumUses INT NOT NULL DEFAULT 100,
    UseCount INT NOT NULL DEFAULT 0,
    ExpiresAtUtc DATETIME(6) NOT NULL,
    LastUsedAtUtc DATETIME(6) NULL,
    CreatedByUserId INT NOT NULL,
    CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    CompletedAtUtc DATETIME(6) NULL,
    RevokedAtUtc DATETIME(6) NULL,
    UNIQUE KEY UX_recruitment_candidate_action_token (TokenHash),
    INDEX IX_recruitment_candidate_action_application (ApplicationId,PurposeCode,Status),
    INDEX IX_recruitment_candidate_action_expiry (ExpiresAtUtc,Status,RevokedAtUtc),
    INDEX IX_recruitment_candidate_action_submission (FormSubmissionId),
    CONSTRAINT FK_recruitment_candidate_action_application FOREIGN KEY (ApplicationId) REFERENCES recruitment_candidate_applications(Id),
    CONSTRAINT FK_recruitment_candidate_action_candidate FOREIGN KEY (CandidateId) REFERENCES recruitment_candidates(Id),
    CONSTRAINT FK_recruitment_candidate_action_stage FOREIGN KEY (PipelineStageInstanceId) REFERENCES recruitment_application_stage_instances(Id),
    CONSTRAINT FK_recruitment_candidate_action_form_version FOREIGN KEY (FormVersionId) REFERENCES form_versions(Id),
    CONSTRAINT FK_recruitment_candidate_action_submission FOREIGN KEY (FormSubmissionId) REFERENCES form_submissions(Id)
);
CREATE TABLE IF NOT EXISTS recruitment_candidate_action_decisions (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    CandidateActionSessionId BIGINT NOT NULL,
    ApplicationId BIGINT NOT NULL,
    OfferId BIGINT NULL,
    DecisionCode VARCHAR(40) NOT NULL,
    Remarks VARCHAR(1000) NOT NULL DEFAULT '',
    DecidedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    IpAddress VARCHAR(80) NOT NULL DEFAULT '',
    UserAgent VARCHAR(500) NOT NULL DEFAULT '',
    UNIQUE KEY UX_recruitment_candidate_action_decision (CandidateActionSessionId),
    INDEX IX_recruitment_candidate_decision_application (ApplicationId,DecidedAtUtc),
    CONSTRAINT FK_recruitment_candidate_decision_session FOREIGN KEY (CandidateActionSessionId) REFERENCES recruitment_candidate_action_sessions(Id) ON DELETE CASCADE
);");
    }

    public async Task<IEnumerable<RecruitmentCandidateActionSession>> ListAsync(long applicationId, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        var rows = (await db.QueryAsync<RecruitmentCandidateActionSession>(SessionSelect + @"
WHERE s.ApplicationId=@ApplicationId AND (@ClientId IS NULL OR s.ClientId=@ClientId)
ORDER BY s.CreatedAtUtc DESC,s.Id DESC", new { ApplicationId = applicationId, user.ClientId })).ToList();
        foreach (var row in rows)
            row.ActionToken = UnprotectToken(row.TokenCipherText);
        return rows;
    }

    public async Task<(RecruitmentCandidateActionSession? Session, string Error)> CreateAsync(CreateRecruitmentCandidateActionRequest request, AuthUser user)
    {
        request.PurposeCode = NormalizeCode(request.PurposeCode);
        if (!Purposes.Contains(request.PurposeCode)) return (null, "Unsupported candidate action purpose.");
        if (request.ApplicationId <= 0) return (null, "Candidate application is required.");
        request.ValidForMinutes = Math.Clamp(request.ValidForMinutes, 5, 60 * 24 * 365);
        request.MaximumUses = Math.Clamp(request.MaximumUses, 1, 1000);

        await using var db = Db();
        await db.OpenAsync();
        var source = await db.QueryFirstOrDefaultAsync<ActionSourceRow>(@"SELECT a.Id ApplicationId,a.ClientId,a.CandidateId,c.Email,c.NormalizedEmail,c.Phone,c.NormalizedPhone,
COALESCE(si.Id,0) CurrentStageInstanceId,COALESCE(si.PipelineStageId,0) PipelineStageId
FROM recruitment_candidate_applications a
JOIN recruitment_candidates c ON c.Id=a.CandidateId
LEFT JOIN recruitment_application_pipeline_instances pi ON pi.ApplicationId=a.Id
LEFT JOIN recruitment_application_stage_instances si ON si.Id=pi.CurrentStageInstanceId
WHERE a.Id=@Id", new { Id = request.ApplicationId });
        if (source is null || (user.ClientId is not null && user.ClientId != source.ClientId)) return (null, "Candidate application was not found.");
        var portalEnabled = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM recruitment_settings
WHERE ClientId=@ClientId AND RecruitmentEnabled=TRUE AND EnableCandidatePortal=TRUE AND IsActive=TRUE", new { source.ClientId });
        if (portalEnabled == 0) return (null, "Enable the candidate portal in Recruitment Settings before creating an external candidate link.");
        if (request.PipelineStageInstanceId.HasValue)
        {
            var stageBelongs = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_application_stage_instances WHERE Id=@Id AND ApplicationId=@ApplicationId", new { Id = request.PipelineStageInstanceId.Value, request.ApplicationId });
            if (stageBelongs == 0) return (null, "Pipeline stage does not belong to this application.");
        }
        else if (source.CurrentStageInstanceId > 0)
        {
            request.PipelineStageInstanceId = source.CurrentStageInstanceId;
        }

        if (!request.FormVersionId.HasValue && source.PipelineStageId > 0)
            request.FormVersionId = await db.ExecuteScalarAsync<long?>("SELECT FormVersionId FROM recruitment_stage_external_form_configurations WHERE PipelineStageId=@Id", new { Id = source.PipelineStageId });
        if (request.FormVersionId.HasValue)
        {
            var published = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM form_versions v JOIN form_definitions d ON d.Id=v.FormDefinitionId
WHERE v.Id=@Id AND v.Status IN ('Published','Retired') AND d.ClientId IN (0,@ClientId) AND d.Status='Active'", new { Id = request.FormVersionId.Value, source.ClientId });
            if (published == 0) return (null, "Select a published form version for this client.");
        }
        if (request.PurposeCode == "OFFER_RESPONSE" && !request.OfferId.HasValue)
            request.OfferId = await db.ExecuteScalarAsync<long?>("SELECT Id FROM recruitment_offers WHERE ApplicationId=@Id AND Status IN ('Released','Pending Candidate') ORDER BY UpdatedAt DESC,Id DESC LIMIT 1", new { Id = request.ApplicationId });
        if (request.OfferId.HasValue)
        {
            var offerBelongs = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_offers WHERE Id=@Id AND ApplicationId=@ApplicationId", new { Id = request.OfferId.Value, request.ApplicationId });
            if (offerBelongs == 0) return (null, "Offer does not belong to this application.");
        }
        if (request.PurposeCode == "OFFER_RESPONSE" && !request.OfferId.HasValue) return (null, "Release an offer before creating a candidate response link.");
        if (request.PurposeCode != "OFFER_RESPONSE" && !request.FormVersionId.HasValue) return (null, "A published external form is required for this candidate action.");

        await using var transaction = await db.BeginTransactionAsync();
        try
        {
            var externalSubjectId = await FindOrCreateSubjectAsync(db, transaction, source);
            long? submissionId = null;
            if (request.FormVersionId.HasValue)
                submissionId = await db.ExecuteScalarAsync<long>(@"INSERT INTO form_submissions
(FormVersionId,ClientId,ExternalSubjectId,EntityType,EntityId,CandidateId,ApplicationId,Status)
VALUES (@FormVersionId,@ClientId,@ExternalSubjectId,'CANDIDATE',@CandidateId,@CandidateId,@ApplicationId,'Draft');SELECT LAST_INSERT_ID();",
                    new { request.FormVersionId, source.ClientId, ExternalSubjectId = externalSubjectId, source.CandidateId, source.ApplicationId }, transaction);

            await db.ExecuteAsync(@"UPDATE recruitment_candidate_action_sessions SET Status='Revoked',RevokedAtUtc=UTC_TIMESTAMP(6)
WHERE ApplicationId=@ApplicationId AND PurposeCode=@PurposeCode AND Status='Open' AND RevokedAtUtc IS NULL", request, transaction);
            var rawToken = RandomToken();
            var id = await db.ExecuteScalarAsync<long>(@"INSERT INTO recruitment_candidate_action_sessions
(ClientId,ApplicationId,CandidateId,PipelineStageInstanceId,FormVersionId,FormSubmissionId,OfferId,PurposeCode,TokenHash,TokenCipherText,Status,Instructions,MaximumUses,ExpiresAtUtc,CreatedByUserId)
VALUES (@ClientId,@ApplicationId,@CandidateId,@PipelineStageInstanceId,@FormVersionId,@FormSubmissionId,@OfferId,@PurposeCode,@TokenHash,@TokenCipherText,'Open',@Instructions,@MaximumUses,@ExpiresAtUtc,@UserId);SELECT LAST_INSERT_ID();",
                new
                {
                    source.ClientId,
                    source.ApplicationId,
                    source.CandidateId,
                    request.PipelineStageInstanceId,
                    request.FormVersionId,
                    FormSubmissionId = submissionId,
                    request.OfferId,
                    request.PurposeCode,
                    TokenHash = Hash(rawToken),
                    TokenCipherText = tokenProtector.Protect(rawToken),
                    Instructions = Truncate(request.Instructions, 1500),
                    request.MaximumUses,
                    ExpiresAtUtc = DateTime.UtcNow.AddMinutes(request.ValidForMinutes),
                    UserId = user.Id
                }, transaction);
            await transaction.CommitAsync();
            var session = await GetInternalAsync(db, id, user.ClientId);
            if (session is not null) session.ActionToken = rawToken;
            return (session, "");
        }
        catch (Exception exception)
        {
            await transaction.RollbackAsync();
            return (null, exception.Message);
        }
    }

    public async Task<(RecruitmentCandidateActionSession? Session, string Error)> CreateForCurrentStageAsync(long applicationId, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        var stage = await db.QueryFirstOrDefaultAsync<StageActionSource>(@"SELECT si.Id StageInstanceId,s.Id StageId,s.StageType,
ef.FormVersionId,COALESCE(ef.ActionTokenValidityMinutes,10080) TokenValidityMinutes,COALESCE(ef.ActionTokenMaximumUses,100) TokenMaximumUses,
oc.CandidateResponseValidityDays
FROM recruitment_candidate_applications a
JOIN recruitment_application_pipeline_instances pi ON pi.ApplicationId=a.Id
JOIN recruitment_application_stage_instances si ON si.Id=pi.CurrentStageInstanceId
JOIN recruitment_pipeline_stages s ON s.Id=si.PipelineStageId
LEFT JOIN recruitment_stage_external_form_configurations ef ON ef.PipelineStageId=s.Id
LEFT JOIN recruitment_stage_offer_configurations oc ON oc.PipelineStageId=s.Id
WHERE a.Id=@Id AND (@ClientId IS NULL OR a.ClientId=@ClientId)", new { Id = applicationId, user.ClientId });
        if (stage is null) return (null, "Active pipeline stage was not found.");
        var candidateFacingStage = stage.StageType.Equals("ExternalForm", StringComparison.OrdinalIgnoreCase)
            || stage.StageType.Equals("Documents", StringComparison.OrdinalIgnoreCase)
            || stage.StageType.Equals("PreOnboarding", StringComparison.OrdinalIgnoreCase);
        var purpose = stage.StageType.Equals("Offer", StringComparison.OrdinalIgnoreCase) ? "OFFER_RESPONSE"
            : stage.StageType.Equals("ExternalForm", StringComparison.OrdinalIgnoreCase) ? "PROFILE_UPDATE"
            : candidateFacingStage ? "DOCUMENT_REQUEST" : "";
        if (purpose.Length == 0) return (null, "The current stage does not require a candidate action link.");
        return await CreateAsync(new CreateRecruitmentCandidateActionRequest
        {
            ApplicationId = applicationId,
            PipelineStageInstanceId = stage.StageInstanceId,
            FormVersionId = stage.FormVersionId,
            PurposeCode = purpose,
            ValidForMinutes = purpose == "OFFER_RESPONSE" ? Math.Max(1, stage.CandidateResponseValidityDays) * 1440 : stage.TokenValidityMinutes,
            MaximumUses = stage.TokenMaximumUses,
            Instructions = purpose == "OFFER_RESPONSE" ? "Review and respond to your offer."
                : purpose == "PROFILE_UPDATE" ? "Complete the requested candidate information."
                : "Complete the requested information and upload the configured documents."
        }, user);
    }

    public async Task<(RecruitmentCandidateActionSession? Session, string Error)> EnsureForCurrentStageAsync(long applicationId, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        var existingId = await db.ExecuteScalarAsync<long?>(@"SELECT s.Id
FROM recruitment_candidate_action_sessions s
JOIN recruitment_candidate_applications a ON a.Id=s.ApplicationId
JOIN recruitment_application_pipeline_instances pipeline ON pipeline.ApplicationId=a.Id
JOIN recruitment_settings settings ON settings.ClientId=s.ClientId AND settings.RecruitmentEnabled=TRUE AND settings.EnableCandidatePortal=TRUE AND settings.IsActive=TRUE
WHERE s.ApplicationId=@ApplicationId AND s.PipelineStageInstanceId=pipeline.CurrentStageInstanceId
  AND s.Status='Open' AND s.RevokedAtUtc IS NULL AND s.ExpiresAtUtc>UTC_TIMESTAMP(6)
  AND s.UseCount<s.MaximumUses AND (@ClientId IS NULL OR s.ClientId=@ClientId)
ORDER BY s.Id DESC LIMIT 1", new { ApplicationId = applicationId, user.ClientId });
        if (existingId.HasValue)
        {
            var existing = await GetInternalAsync(db, existingId.Value, user.ClientId);
            if (existing is not null)
            {
                existing.ActionToken = UnprotectToken(existing.TokenCipherText);
                return (existing, "");
            }
        }
        return await CreateForCurrentStageAsync(applicationId, user);
    }

    public async Task<PublicRecruitmentCandidateActionContext?> GetPublicAsync(string token)
    {
        await using var db = Db();
        await db.OpenAsync();
        var session = await ValidateAsync(db, token, true);
        if (session is null) return null;
        var context = await db.QueryFirstAsync<PublicRecruitmentCandidateActionContext>(@"SELECT s.PurposeCode,CONCAT(c.FirstName,' ',c.LastName) CandidateName,
p.PositionTitle,cl.Name OrganizationName,s.ExpiresAtUtc,s.Status,s.Instructions,
COALESCE(externalForm.AllowSaveDraft,FALSE) AllowSaveDraft
FROM recruitment_candidate_action_sessions s
JOIN recruitment_candidates c ON c.Id=s.CandidateId
JOIN recruitment_candidate_applications a ON a.Id=s.ApplicationId
JOIN recruitment_open_positions p ON p.Id=a.PositionId
LEFT JOIN recruitment_application_stage_instances stageInstance ON stageInstance.Id=s.PipelineStageInstanceId
LEFT JOIN recruitment_stage_external_form_configurations externalForm ON externalForm.PipelineStageId=stageInstance.PipelineStageId
JOIN clients cl ON cl.Id=s.ClientId WHERE s.Id=@Id", new { session.Id });
        context.Message = context.Instructions;
        if (session.FormVersionId.HasValue)
            context.Form = await forms.GetPublishedVersionAsync(session.FormVersionId.Value);
        if (session.FormSubmissionId.HasValue)
        {
            context.ExistingValues = await LoadValuesAsync(db, session.FormSubmissionId.Value);
            context.UploadedFiles = (await db.QueryAsync<PublicCandidateActionFile>(@"SELECT fa.FieldId,a.public_id AttachmentPublicId,a.original_file_name OriginalFileName,a.file_size_bytes FileSizeBytes,a.uploaded_at_utc UploadedAtUtc
FROM form_submission_attachments fa JOIN entity_attachments a ON a.id=fa.AttachmentId
WHERE fa.SubmissionId=@Id AND a.is_current=TRUE AND a.is_deleted=FALSE ORDER BY fa.FieldId,a.uploaded_at_utc", new { Id = session.FormSubmissionId.Value })).ToList();
        }
        if (session.OfferId.HasValue)
        {
            context.Offer = await db.QueryFirstOrDefaultAsync<PublicCandidateOffer>(@"SELECT Id,OfferNumber,OfferedCtc,Currency,ProposedJoiningDate,ExpiryDate,OfferLetterAttachmentPublicId,Status
FROM recruitment_offers WHERE Id=@Id AND ApplicationId=@ApplicationId", new { Id = session.OfferId.Value, session.ApplicationId });
            if (context.Offer?.OfferLetterAttachmentPublicId is not null)
                context.Offer.DocumentUrl = $"/api/public/recruitment/actions/{Uri.EscapeDataString(token)}/offer-document";
            if (context.Offer is not null && (context.Offer.Status is not ("Pending Candidate" or "Released")
                || (context.Offer.ExpiryDate.HasValue && context.Offer.ExpiryDate.Value.Date < DateTime.UtcNow.Date)))
            {
                context.Status = "Unavailable";
                context.Message = context.Offer.ExpiryDate.HasValue && context.Offer.ExpiryDate.Value.Date < DateTime.UtcNow.Date
                    ? "This offer has expired. Contact HR for a revised offer."
                    : $"This offer is already {context.Offer.Status}.";
            }
        }
        return context;
    }

    public async Task<(bool Ok, string Error)> SaveValuesAsync(string token, SavePublicFormValuesRequest request)
    {
        await using var db = Db();
        await db.OpenAsync();
        var session = await ValidateAsync(db, token, true);
        if (session?.FormSubmissionId is null) return (false, "This candidate action does not contain a form.");
        var allowSave = await db.ExecuteScalarAsync<bool>(@"SELECT COALESCE(configuration.AllowSaveDraft,FALSE)
FROM recruitment_application_stage_instances stageInstance
LEFT JOIN recruitment_stage_external_form_configurations configuration ON configuration.PipelineStageId=stageInstance.PipelineStageId
WHERE stageInstance.Id=@Id", new { Id = session.PipelineStageInstanceId });
        if (!allowSave) return (false, "Saving a draft is not enabled for this candidate action.");
        return await forms.SaveSubmissionValuesAsync(session.FormSubmissionId.Value, session.ClientId, request);
    }

    public async Task<(PublicUploadAuthorization? Authorization, string Error)> AuthorizeUploadAsync(string token, long fieldId)
    {
        await using var db = Db();
        await db.OpenAsync();
        var session = await ValidateAsync(db, token, false);
        if (session?.FormSubmissionId is null) return (null, "Candidate action is invalid or expired.");
        var row = await db.QueryFirstOrDefaultAsync<PublicUploadAuthorization>(@"SELECT s.Id SubmissionId,COALESCE(s.ExternalSubjectId,0) ExternalSubjectId,s.ClientId,f.Id FieldId,f.AttachmentFieldConfigurationId
FROM form_submissions s JOIN form_fields f ON f.FormVersionId=s.FormVersionId JOIN form_field_types t ON t.Id=f.FieldTypeId
WHERE s.Id=@SubmissionId AND f.Id=@FieldId AND f.IsActive=TRUE AND t.TypeCode='UPLOAD' AND f.AttachmentFieldConfigurationId IS NOT NULL", new { SubmissionId = session.FormSubmissionId.Value, FieldId = fieldId });
        return row is null ? (null, "This field is not an active upload field.") : (row, "");
    }

    public async Task<IEnumerable<DynamicLookupOption>> ResolveLookupAsync(string token, long fieldId, string search)
    {
        await using var db = Db();
        await db.OpenAsync();
        var session = await ValidateAsync(db, token, false);
        if (session?.FormSubmissionId is null) return [];
        var (items, _) = await forms.ResolveSubmissionLookupAsync(session.FormSubmissionId.Value, session.ClientId, fieldId, search ?? "");
        return items;
    }

    public async Task LinkAttachmentAsync(string token, long fieldId, long attachmentId, Guid publicId)
    {
        await using var db = Db();
        await db.OpenAsync();
        var session = await ValidateAsync(db, token, true) ?? throw new InvalidOperationException("Candidate action is invalid or expired.");
        if (!session.FormSubmissionId.HasValue) throw new InvalidOperationException("Candidate action does not contain an upload form.");
        await db.ExecuteAsync(@"INSERT IGNORE INTO form_submission_attachments (SubmissionId,FieldId,AttachmentId,AttachmentPublicId)
VALUES (@SubmissionId,@FieldId,@AttachmentId,@PublicId);
UPDATE entity_attachments a JOIN form_submissions s ON s.Id=@SubmissionId
SET a.uploaded_by_external_subject_id=s.ExternalSubjectId WHERE a.id=@AttachmentId;",
            new { SubmissionId = session.FormSubmissionId.Value, FieldId = fieldId, AttachmentId = attachmentId, PublicId = publicId.ToString() });
    }

    public async Task<(PublicCandidateActionResult? Result, string Error)> CompleteAsync(string token, CompletePublicCandidateActionRequest request, string ipAddress, string userAgent)
    {
        await using var db = Db();
        await db.OpenAsync();
        var session = await ValidateAsync(db, token, false);
        if (session is null) return (null, "Candidate action is invalid or expired.");
        if (session.FormSubmissionId.HasValue)
        {
            var saved = await forms.SaveSubmissionValuesAsync(session.FormSubmissionId.Value, session.ClientId, new SavePublicFormValuesRequest { Values = request.Values });
            if (!saved.Ok) return (null, saved.Error);
            var required = await forms.ValidateRequiredSubmissionAsync(session.FormSubmissionId.Value);
            if (!required.Ok) return (null, required.Error);
        }

        var decision = NormalizeDecision(request.Decision);
        if (session.PurposeCode == "OFFER_RESPONSE" && decision.Length == 0)
            return (null, "Select Accept, Reject or Request negotiation.");
        await using var transaction = await db.BeginTransactionAsync();
        try
        {
            var locked = await db.QueryFirstOrDefaultAsync<ActionSessionRow>(SessionSql + " FOR UPDATE", new { TokenHash = Hash(token) }, transaction);
            if (!IsValid(locked)) return (null, "Candidate action is invalid or expired.");
            if (locked!.PurposeCode == "OFFER_RESPONSE" && locked.OfferId.HasValue)
            {
                var offer = await db.QueryFirstOrDefaultAsync<OfferResponseRow>(@"SELECT offer.Id,offer.Status,offer.ExpiryDate,
applicationRow.ClientId,applicationRow.CandidateId,applicationRow.PositionId,applicationRow.CurrentStage
FROM recruitment_offers offer
JOIN recruitment_candidate_applications applicationRow ON applicationRow.Id=offer.ApplicationId
WHERE offer.Id=@Id AND offer.ApplicationId=@ApplicationId FOR UPDATE",
                    new { Id = locked.OfferId.Value, locked.ApplicationId }, transaction);
                if (offer is null) return (null, "The offer linked to this action is no longer available.");
                if (offer.Status is not ("Pending Candidate" or "Released"))
                    return (null, $"This offer can no longer be changed because it is {offer.Status}.");
                if (offer.ExpiryDate.HasValue && offer.ExpiryDate.Value.Date < DateTime.UtcNow.Date)
                    return (null, "This offer has expired. Contact HR for a revised offer.");

                var offerStatus = decision == "ACCEPTED" ? "Accepted" : decision == "REJECTED" ? "Rejected" : "Negotiation";
                var remarks = Truncate(request.Remarks, 1000);
                await db.ExecuteAsync(@"UPDATE recruitment_offers
SET Status=@Status,Remarks=CASE WHEN @Remarks='' THEN Remarks ELSE @Remarks END,UpdatedAt=UTC_TIMESTAMP()
WHERE Id=@Id AND ApplicationId=@ApplicationId", new { Id = offer.Id, locked.ApplicationId, Status = offerStatus, Remarks = remarks }, transaction);

                var applicationStage = offerStatus == "Accepted" ? "Offer Accepted"
                    : offerStatus == "Rejected" ? "Rejected" : "Offer Negotiation";
                if (!offer.CurrentStage.Equals(applicationStage, StringComparison.OrdinalIgnoreCase))
                {
                    await db.ExecuteAsync(@"UPDATE recruitment_candidate_applications
SET CurrentStage=@Stage,CurrentStatus=@Stage,LastStageChangedAt=UTC_TIMESTAMP(),UpdatedAt=UTC_TIMESTAMP()
WHERE Id=@ApplicationId;
INSERT INTO recruitment_application_stage_history (ApplicationId,FromStage,ToStage,Reason,ChangedByUserId)
VALUES (@ApplicationId,@FromStage,@Stage,@Reason,0);", new
                    {
                        locked.ApplicationId,
                        FromStage = offer.CurrentStage,
                        Stage = applicationStage,
                        Reason = $"Candidate offer response: {offerStatus}. {remarks}".Trim()
                    }, transaction);
                }

                if (offerStatus == "Accepted")
                {
                    var checklist = (await db.QueryAsync<CandidateChecklistConfigurationRow>(@"SELECT d.Id,d.DocumentName,d.Stage,d.Mandatory,
d.AttachmentAttributeId,d.RequiresVerification,d.DueOffsetDays,d.DisplayOrder
FROM recruitment_document_checklist d
JOIN recruitment_open_positions positionRow ON positionRow.Id=@PositionId
WHERE d.IsActive=TRUE AND d.ClientId IN (0,@ClientId)
AND (d.HiringType='' OR d.HiringType=positionRow.HiringType)
ORDER BY d.ClientId DESC,d.Mandatory DESC,d.DisplayOrder,d.DocumentName",
                        new { offer.PositionId, offer.ClientId }, transaction)).ToList();
                    foreach (var item in checklist
                        .GroupBy(value => $"{value.Stage}|{value.DocumentName}", StringComparer.OrdinalIgnoreCase)
                        .Select(group => group.First()))
                        await db.ExecuteAsync(@"INSERT INTO recruitment_candidate_checklist_items
(ApplicationId,CandidateId,ChecklistConfigurationId,ChecklistName,Stage,Mandatory,AttachmentAttributeId,RequiresVerification,DueDate,Status,DisplayOrder)
VALUES (@ApplicationId,@CandidateId,@Id,@DocumentName,@Stage,@Mandatory,@AttachmentAttributeId,@RequiresVerification,
DATE_ADD(UTC_DATE(),INTERVAL @DueOffsetDays DAY),'Pending',@DisplayOrder)
ON DUPLICATE KEY UPDATE Mandatory=VALUES(Mandatory),AttachmentAttributeId=VALUES(AttachmentAttributeId),
RequiresVerification=VALUES(RequiresVerification),DueDate=VALUES(DueDate),DisplayOrder=VALUES(DisplayOrder)", new
                        {
                            locked.ApplicationId,
                            offer.CandidateId,
                            item.Id,
                            item.DocumentName,
                            item.Stage,
                            item.Mandatory,
                            item.AttachmentAttributeId,
                            item.RequiresVerification,
                            item.DueOffsetDays,
                            item.DisplayOrder
                        }, transaction);
                }

                await db.ExecuteAsync(@"INSERT INTO recruitment_position_timeline
(PositionId,EventType,EventTitle,EventDetails,ActorUserId)
VALUES (@PositionId,'Offer Status',@Title,@Details,NULL);
INSERT INTO person_activity_events
(ClientId,CandidateId,EmployeeId,ModuleCode,EventType,EventTitle,EventSummary,ResourceType,ResourceId,ActorUserId,Visibility,IsSensitive,MetadataJson,OccurredAt)
VALUES (@ClientId,@CandidateId,NULL,'RECRUITMENT','OFFER_STATUS_CHANGED',@Title,@Details,'RecruitmentOffer',@ResourceId,NULL,'HR',FALSE,JSON_OBJECT(),UTC_TIMESTAMP());",
                    new
                    {
                        offer.PositionId,
                        offer.ClientId,
                        offer.CandidateId,
                        Title = $"Candidate offer response: {offerStatus}",
                        Details = remarks,
                        ResourceId = offer.Id.ToString()
                    }, transaction);
            }
            if (decision.Length > 0)
                await db.ExecuteAsync(@"INSERT INTO recruitment_candidate_action_decisions
(CandidateActionSessionId,ApplicationId,OfferId,DecisionCode,Remarks,IpAddress,UserAgent)
VALUES (@SessionId,@ApplicationId,@OfferId,@Decision,@Remarks,@Ip,@Agent)", new { SessionId = locked.Id, locked.ApplicationId, locked.OfferId, Decision = decision, Remarks = Truncate(request.Remarks, 1000), Ip = ipAddress ?? "", Agent = Truncate(userAgent, 500) }, transaction);
            if (locked.FormSubmissionId.HasValue)
            {
                // Files are uploaded against the temporary form submission so the
                // public endpoint never needs direct candidate access. Once the
                // candidate completes the action, promote the same secured global
                // attachments to the candidate profile; no file copy is performed.
                await db.ExecuteAsync(@"UPDATE entity_attachments a
JOIN form_submission_attachments fa ON fa.AttachmentId=a.id AND fa.SubmissionId=@SubmissionId
JOIN form_submissions fs ON fs.Id=fa.SubmissionId
SET a.entity_type='CANDIDATE',a.entity_id=@CandidateId,a.uploaded_by_external_subject_id=fs.ExternalSubjectId
WHERE a.entity_type='FORM_SUBMISSION' AND a.entity_id=@SubmissionId AND a.is_deleted=FALSE;
UPDATE form_submissions SET CandidateId=@CandidateId,ApplicationId=@ApplicationId,EntityType='CANDIDATE',EntityId=@CandidateId,
Status='Submitted',SubmittedAtUtc=UTC_TIMESTAMP(6),UpdatedAtUtc=UTC_TIMESTAMP(6) WHERE Id=@SubmissionId",
                    new { SubmissionId = locked.FormSubmissionId.Value, locked.CandidateId, locked.ApplicationId }, transaction);
            }
            await db.ExecuteAsync("UPDATE recruitment_candidate_action_sessions SET Status='Completed',CompletedAtUtc=UTC_TIMESTAMP(6),LastUsedAtUtc=UTC_TIMESTAMP(6),UseCount=UseCount+1 WHERE Id=@Id", new { locked.Id }, transaction);
            await transaction.CommitAsync();
            return (new PublicCandidateActionResult
            {
                ApplicationId = locked.ApplicationId,
                PipelineStageInstanceId = locked.PipelineStageInstanceId,
                Status = decision.Length > 0 ? decision : "COMPLETED",
                Message = locked.PurposeCode == "OFFER_RESPONSE" ? "Your offer response was submitted successfully." : "Your documents and information were submitted successfully."
            }, "");
        }
        catch (Exception exception)
        {
            await transaction.RollbackAsync();
            return (null, exception.Message);
        }
    }

    public async Task<(Guid? PublicId, int ClientId, long CandidateId)> GetOfferDocumentAsync(string token)
    {
        await using var db = Db();
        await db.OpenAsync();
        var session = await ValidateAsync(db, token, true);
        if (session?.OfferId is null) return (null, 0, 0);
        var publicId = await db.ExecuteScalarAsync<string>("SELECT OfferLetterAttachmentPublicId FROM recruitment_offers WHERE Id=@Id AND ApplicationId=@ApplicationId", new { Id = session.OfferId.Value, session.ApplicationId });
        return Guid.TryParse(publicId, out var parsed) ? (parsed, session.ClientId, session.CandidateId) : (null, 0, 0);
    }

    public async Task<bool> RevokeAsync(long id, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        return await db.ExecuteAsync("UPDATE recruitment_candidate_action_sessions SET Status='Revoked',RevokedAtUtc=UTC_TIMESTAMP(6) WHERE Id=@Id AND Status='Open' AND (@ClientId IS NULL OR ClientId=@ClientId)", new { Id = id, user.ClientId }) > 0;
    }

    private static async Task<long> FindOrCreateSubjectAsync(MySqlConnection db, MySqlTransaction transaction, ActionSourceRow source)
    {
        var existing = await db.ExecuteScalarAsync<long?>(@"SELECT Id FROM external_portal_subjects WHERE ClientId=@ClientId
AND ((@Email<>'' AND NormalizedEmail=@Email) OR (@Phone<>'' AND NormalizedPhone=@Phone)) ORDER BY Id LIMIT 1",
            new { source.ClientId, Email = source.NormalizedEmail ?? "", Phone = source.NormalizedPhone ?? "" }, transaction);
        if (existing.HasValue) return existing.Value;
        return await db.ExecuteScalarAsync<long>(@"INSERT INTO external_portal_subjects
(ClientId,Email,NormalizedEmail,Phone,NormalizedPhone,ConsentAccepted,ConsentAcceptedAtUtc)
VALUES (@ClientId,@Email,@NormalizedEmail,@Phone,@NormalizedPhone,TRUE,UTC_TIMESTAMP(6));SELECT LAST_INSERT_ID();",
            new { source.ClientId, Email = source.Email ?? "", NormalizedEmail = source.NormalizedEmail ?? "", Phone = source.Phone ?? "", NormalizedPhone = source.NormalizedPhone ?? "" }, transaction);
    }

    private static async Task<List<PublicFormValue>> LoadValuesAsync(MySqlConnection db, long submissionId)
    {
        var values = (await db.QueryAsync<PublicFormValue>(@"SELECT FieldId,TextValue,IntegerValue,DecimalValue,DateValue,DateTimeValue,BooleanValue
FROM form_submission_values WHERE SubmissionId=@Id ORDER BY FieldId", new { Id = submissionId })).ToList();
        var staticOptions = await db.QueryAsync<(long FieldId, long OptionId)>("SELECT FieldId,OptionId FROM form_submission_selected_options WHERE SubmissionId=@Id ORDER BY FieldId,OptionId", new { Id = submissionId });
        var lookupOptions = await db.QueryAsync<(long FieldId, string OptionValue)>("SELECT FieldId,SelectedValue OptionValue FROM form_submission_lookup_values WHERE SubmissionId=@Id ORDER BY FieldId,DisplayOrder", new { Id = submissionId });
        foreach (var fieldId in staticOptions.Select(row => row.FieldId).Concat(lookupOptions.Select(row => row.FieldId)).Distinct())
        {
            var value = values.FirstOrDefault(row => row.FieldId == fieldId);
            if (value is null) { value = new PublicFormValue { FieldId = fieldId }; values.Add(value); }
            value.SelectedOptionIds = staticOptions.Where(row => row.FieldId == fieldId).Select(row => row.OptionId).ToList();
            value.SelectedOptionValues = lookupOptions.Where(row => row.FieldId == fieldId).Select(row => row.OptionValue).ToList();
        }
        return values;
    }

    private static async Task<RecruitmentCandidateActionSession?> GetInternalAsync(MySqlConnection db, long id, int? clientId) =>
        await db.QueryFirstOrDefaultAsync<RecruitmentCandidateActionSession>(SessionSelect + " WHERE s.Id=@Id AND (@ClientId IS NULL OR s.ClientId=@ClientId)", new { Id = id, ClientId = clientId });

    private string UnprotectToken(string cipherText)
    {
        if (string.IsNullOrWhiteSpace(cipherText)) return "";
        try { return tokenProtector.Unprotect(cipherText); }
        catch { return ""; }
    }

    private static async Task<ActionSessionRow?> ValidateAsync(MySqlConnection db, string token, bool touch)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var row = await db.QueryFirstOrDefaultAsync<ActionSessionRow>(SessionSql, new { TokenHash = Hash(token) });
        if (!IsValid(row)) return null;
        if (touch)
            await db.ExecuteAsync("UPDATE recruitment_candidate_action_sessions SET LastUsedAtUtc=UTC_TIMESTAMP(6) WHERE Id=@Id", new { row!.Id });
        return row;
    }

    private static bool IsValid(ActionSessionRow? row) => row is not null && row.Status == "Open" && row.RevokedAtUtc is null && row.ExpiresAtUtc > DateTime.UtcNow && row.UseCount < row.MaximumUses;
    private static string NormalizeCode(string value) => string.Join("_", (value ?? "").Trim().ToUpperInvariant().Split([' ', '-', '/'], StringSplitOptions.RemoveEmptyEntries));
    private static string NormalizeDecision(string value) => (value ?? "").Trim().ToUpperInvariant() switch { "ACCEPT" or "ACCEPTED" => "ACCEPTED", "REJECT" or "REJECTED" => "REJECTED", "NEGOTIATION" or "NEGOTIATION_REQUESTED" or "REQUEST_NEGOTIATION" => "NEGOTIATION_REQUESTED", _ => "" };
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string RandomToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string Truncate(string value, int maximum) => string.IsNullOrEmpty(value) ? "" : value.Length <= maximum ? value : value[..maximum];

    private const string SessionSelect = @"SELECT s.*,CONCAT(c.FirstName,' ',c.LastName) CandidateName,p.PositionTitle
FROM recruitment_candidate_action_sessions s
JOIN recruitment_candidates c ON c.Id=s.CandidateId
JOIN recruitment_candidate_applications a ON a.Id=s.ApplicationId
JOIN recruitment_open_positions p ON p.Id=a.PositionId ";
    private const string SessionSql = @"SELECT action.Id,action.ClientId,action.ApplicationId,action.CandidateId,action.PipelineStageInstanceId,action.FormVersionId,action.FormSubmissionId,action.OfferId,action.PurposeCode,action.Status,action.MaximumUses,action.UseCount,action.ExpiresAtUtc,action.RevokedAtUtc
FROM recruitment_candidate_action_sessions action
JOIN recruitment_settings settings ON settings.ClientId=action.ClientId AND settings.RecruitmentEnabled=TRUE AND settings.EnableCandidatePortal=TRUE AND settings.IsActive=TRUE
WHERE action.TokenHash=@TokenHash";

    private sealed class ActionSourceRow
    {
        public long ApplicationId { get; set; }
        public int ClientId { get; set; }
        public long CandidateId { get; set; }
        public string Email { get; set; } = "";
        public string NormalizedEmail { get; set; } = "";
        public string Phone { get; set; } = "";
        public string NormalizedPhone { get; set; } = "";
        public long CurrentStageInstanceId { get; set; }
        public long PipelineStageId { get; set; }
    }

    private sealed class StageActionSource
    {
        public long StageInstanceId { get; set; }
        public long StageId { get; set; }
        public string StageType { get; set; } = "";
        public long? FormVersionId { get; set; }
        public int TokenValidityMinutes { get; set; } = 10080;
        public int TokenMaximumUses { get; set; } = 100;
        public int CandidateResponseValidityDays { get; set; } = 7;
    }

    private sealed class OfferResponseRow
    {
        public long Id { get; set; }
        public string Status { get; set; } = "";
        public DateTime? ExpiryDate { get; set; }
        public int ClientId { get; set; }
        public long CandidateId { get; set; }
        public long PositionId { get; set; }
        public string CurrentStage { get; set; } = "";
    }

    private sealed class CandidateChecklistConfigurationRow
    {
        public int Id { get; set; }
        public string DocumentName { get; set; } = "";
        public string Stage { get; set; } = "Pre-Onboarding";
        public bool Mandatory { get; set; }
        public long? AttachmentAttributeId { get; set; }
        public bool RequiresVerification { get; set; }
        public int DueOffsetDays { get; set; }
        public int DisplayOrder { get; set; }
    }

    private sealed class ActionSessionRow
    {
        public long Id { get; set; }
        public int ClientId { get; set; }
        public long ApplicationId { get; set; }
        public long CandidateId { get; set; }
        public long? PipelineStageInstanceId { get; set; }
        public long? FormVersionId { get; set; }
        public long? FormSubmissionId { get; set; }
        public long? OfferId { get; set; }
        public string PurposeCode { get; set; } = "";
        public string Status { get; set; } = "";
        public int MaximumUses { get; set; }
        public int UseCount { get; set; }
        public DateTime ExpiresAtUtc { get; set; }
        public DateTime? RevokedAtUtc { get; set; }
    }
}
