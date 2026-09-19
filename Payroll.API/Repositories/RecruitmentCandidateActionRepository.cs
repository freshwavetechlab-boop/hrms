using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using System.Text.RegularExpressions;
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
        await db.ExecuteAsync(@"UPDATE recruitment_candidate_action_sessions action
JOIN recruitment_candidate_applications applicationRow ON applicationRow.Id=action.ApplicationId
LEFT JOIN recruitment_application_pipeline_instances pipeline ON pipeline.ApplicationId=applicationRow.Id
SET action.Status='Revoked',action.RevokedAtUtc=COALESCE(action.RevokedAtUtc,UTC_TIMESTAMP(6))
WHERE action.Status='Open' AND action.RevokedAtUtc IS NULL
  AND (action.PipelineStageInstanceId IS NULL OR pipeline.CurrentStageInstanceId IS NULL
       OR action.PipelineStageInstanceId<>pipeline.CurrentStageInstanceId)");
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
        if (source.CurrentStageInstanceId <= 0) return (null, "The candidate application does not have an active pipeline stage.");
        if (request.PipelineStageInstanceId.HasValue && request.PipelineStageInstanceId.Value != source.CurrentStageInstanceId)
            return (null, "Candidate action must target the application's current pipeline stage.");
        request.PipelineStageInstanceId = source.CurrentStageInstanceId;
        if (!request.FormVersionId.HasValue && source.PipelineStageId > 0)
            request.FormVersionId = await db.ExecuteScalarAsync<long?>("SELECT FormVersionId FROM recruitment_stage_external_form_configurations WHERE PipelineStageId=@Id", new { Id = source.PipelineStageId });
        request.FormVersionId = await ResolveEffectiveFormVersionAsync(db, request.FormVersionId, source.ApplicationId, source.ClientId);
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
            var lockedCurrentStageId = await LockCurrentStageAsync(db, transaction, request.ApplicationId);
            if (!lockedCurrentStageId.HasValue || lockedCurrentStageId.Value != request.PipelineStageInstanceId.Value)
            {
                await transaction.RollbackAsync();
                return (null, "The candidate has moved to another pipeline stage. Generate a new candidate action link.");
            }
            await RevokeSupersededSessionsAsync(db, request.ApplicationId, user.ClientId, transaction);
            var externalSubjectId = await FindOrCreateSubjectAsync(db, transaction, source);
            long? submissionId = null;
            if (request.FormVersionId.HasValue)
            {
                submissionId = await db.ExecuteScalarAsync<long>(@"INSERT INTO form_submissions
(FormVersionId,ClientId,ExternalSubjectId,EntityType,EntityId,CandidateId,ApplicationId,Status)
VALUES (@FormVersionId,@ClientId,@ExternalSubjectId,'CANDIDATE',@CandidateId,@CandidateId,@ApplicationId,'Draft');SELECT LAST_INSERT_ID();",
                    new { request.FormVersionId, source.ClientId, ExternalSubjectId = externalSubjectId, source.CandidateId, source.ApplicationId }, transaction);
                await SeedSubmissionFromCandidateAsync(db, transaction, submissionId.Value, request.FormVersionId.Value, source.CandidateId);
            }

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
        // Client-specific joining pack applies only to document/preboarding stages.
        // Application/profile forms and previously issued links stay unchanged.
        if (purpose == "DOCUMENT_REQUEST")
        {
            var joiningVersion = await db.ExecuteScalarAsync<long?>(@"SELECT definition.CurrentPublishedVersionId
FROM form_definitions definition
JOIN recruitment_candidate_applications application ON application.ClientId=definition.ClientId
JOIN form_versions version ON version.Id=definition.CurrentPublishedVersionId AND version.Status='Published'
WHERE application.Id=@Id AND (@ClientId IS NULL OR application.ClientId=@ClientId)
AND definition.FormCode='UIDAI_JOINING_DOCUMENTS' AND definition.PurposeCode='DOCUMENT_REQUEST'
AND definition.Status='Active' LIMIT 1", new { Id = applicationId, user.ClientId });
            if (joiningVersion.HasValue) stage.FormVersionId = joiningVersion;
        }
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
        await RevokeSupersededSessionsAsync(db, applicationId, user.ClientId);
        var existingId = await db.ExecuteScalarAsync<long?>(@"SELECT s.Id
FROM recruitment_candidate_action_sessions s
JOIN recruitment_candidate_applications a ON a.Id=s.ApplicationId
JOIN recruitment_application_pipeline_instances pipeline ON pipeline.ApplicationId=a.Id
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
        var session = await ValidateAsync(db, token, true, allowCompleted: true);
        if (session is null) return null;
        await RefreshFormAndPrefillAsync(db, session);
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
WHERE fa.SubmissionId=@Id AND a.is_current=TRUE AND a.is_deleted=FALSE
UNION ALL
SELECT fieldRow.Id FieldId,attachment.public_id AttachmentPublicId,attachment.original_file_name OriginalFileName,
attachment.file_size_bytes FileSizeBytes,attachment.uploaded_at_utc UploadedAtUtc
FROM form_submissions submission
JOIN form_fields fieldRow ON fieldRow.FormVersionId=submission.FormVersionId AND fieldRow.IsActive=TRUE
JOIN form_field_types fieldType ON fieldType.Id=fieldRow.FieldTypeId AND fieldType.TypeCode='UPLOAD'
JOIN recruitment_candidate_resumes resume ON resume.CandidateId=submission.CandidateId AND resume.IsPrimary=TRUE
JOIN entity_attachments attachment ON attachment.public_id=resume.AttachmentPublicId
 AND attachment.field_configuration_id=fieldRow.AttachmentFieldConfigurationId
 AND attachment.is_current=TRUE AND attachment.is_deleted=FALSE
WHERE submission.Id=@Id AND NOT EXISTS (
 SELECT 1 FROM form_submission_attachments linked
 WHERE linked.SubmissionId=submission.Id AND linked.FieldId=fieldRow.Id AND linked.AttachmentPublicId=attachment.public_id)
ORDER BY FieldId,UploadedAtUtc", new { Id = session.FormSubmissionId.Value })).ToList();
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
        var session = await ValidateAsync(db, token, false);
        if (session?.FormSubmissionId is null) return (false, "This candidate action does not contain a form.");
        await using var transaction = await db.BeginTransactionAsync();
        try
        {
            await LockCurrentStageAsync(db, transaction, session.ApplicationId);
            var locked = await db.QueryFirstOrDefaultAsync<ActionSessionRow>(SessionSql + " FOR UPDATE", new { TokenHash = Hash(token) }, transaction);
            if (!IsValid(locked) || locked!.FormSubmissionId is null)
            {
                await transaction.RollbackAsync();
                return (false, "Candidate action is invalid or expired.");
            }
            var allowSave = await db.ExecuteScalarAsync<bool>(@"SELECT COALESCE(configuration.AllowSaveDraft,FALSE)
FROM recruitment_application_stage_instances stageInstance
LEFT JOIN recruitment_stage_external_form_configurations configuration ON configuration.PipelineStageId=stageInstance.PipelineStageId
WHERE stageInstance.Id=@Id", new { Id = locked.PipelineStageInstanceId }, transaction);
            if (!allowSave)
            {
                await transaction.RollbackAsync();
                return (false, "Saving a draft is not enabled for this candidate action.");
            }
            var saved = await forms.SaveSubmissionValuesAsync(locked.FormSubmissionId.Value, locked.ClientId, request);
            if (!saved.Ok)
            {
                await transaction.RollbackAsync();
                return saved;
            }
            await db.ExecuteAsync("UPDATE recruitment_candidate_action_sessions SET LastUsedAtUtc=UTC_TIMESTAMP(6) WHERE Id=@Id", new { locked.Id }, transaction);
            await transaction.CommitAsync();
            return saved;
        }
        catch (Exception exception)
        {
            await transaction.RollbackAsync();
            return (false, exception.Message);
        }
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
        var session = await ValidateAsync(db, token, false) ?? throw new InvalidOperationException("Candidate action is invalid or expired.");
        await using var transaction = await db.BeginTransactionAsync();
        try
        {
            await LockCurrentStageAsync(db, transaction, session.ApplicationId);
            var locked = await db.QueryFirstOrDefaultAsync<ActionSessionRow>(SessionSql + " FOR UPDATE", new { TokenHash = Hash(token) }, transaction);
            if (!IsValid(locked)) throw new InvalidOperationException("Candidate action is invalid or expired.");
            if (!locked!.FormSubmissionId.HasValue) throw new InvalidOperationException("Candidate action does not contain an upload form.");
            await db.ExecuteAsync(@"INSERT IGNORE INTO form_submission_attachments (SubmissionId,FieldId,AttachmentId,AttachmentPublicId)
VALUES (@SubmissionId,@FieldId,@AttachmentId,@PublicId);
UPDATE entity_attachments a JOIN form_submissions s ON s.Id=@SubmissionId
SET a.uploaded_by_external_subject_id=s.ExternalSubjectId WHERE a.id=@AttachmentId;",
                new { SubmissionId = locked.FormSubmissionId.Value, FieldId = fieldId, AttachmentId = attachmentId, PublicId = publicId.ToString() }, transaction);
            await db.ExecuteAsync("UPDATE recruitment_candidate_action_sessions SET LastUsedAtUtc=UTC_TIMESTAMP(6) WHERE Id=@Id", new { locked.Id }, transaction);
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task<(PublicCandidateActionResult? Result, string Error)> CompleteAsync(string token, CompletePublicCandidateActionRequest request, string ipAddress, string userAgent)
    {
        await using var db = Db();
        await db.OpenAsync();
        var session = await ValidateAsync(db, token, false);
        if (session is null)
        {
            var completed = string.IsNullOrWhiteSpace(token)
                ? null
                : await db.QueryFirstOrDefaultAsync<ActionSessionRow>(SessionSql, new { TokenHash = Hash(token) });
            if (CanRetryCompletedSubmission(completed, request))
                return (CompletedSubmissionResult(completed!), "");
        }
        if (session is null) return (null, "Candidate action is invalid or expired.");
        var decision = NormalizeDecision(request.Decision);
        if (session.PurposeCode == "OFFER_RESPONSE" && decision.Length == 0)
            return (null, "Select Accept, Reject or Request negotiation.");
        await using var transaction = await db.BeginTransactionAsync();
        try
        {
            await LockCurrentStageAsync(db, transaction, session.ApplicationId);
            var locked = await db.QueryFirstOrDefaultAsync<ActionSessionRow>(SessionSql + " FOR UPDATE", new { TokenHash = Hash(token) }, transaction);
            if (CanRetryCompletedSubmission(locked, request))
            {
                await transaction.RollbackAsync();
                return (CompletedSubmissionResult(locked!), "");
            }
            if (!IsValid(locked))
            {
                await transaction.RollbackAsync();
                return (null, "Candidate action is invalid or expired.");
            }
            if (locked!.FormSubmissionId.HasValue)
            {
                var saved = await forms.SaveSubmissionValuesAsync(locked.FormSubmissionId.Value, locked.ClientId, new SavePublicFormValuesRequest { Values = request.Values });
                if (!saved.Ok)
                {
                    await transaction.RollbackAsync();
                    return (null, saved.Error);
                }
                var required = await forms.ValidateRequiredSubmissionAsync(locked.FormSubmissionId.Value);
                if (!required.Ok)
                {
                    await transaction.RollbackAsync();
                    return (null, required.Error);
                }
            }
            if (locked.PurposeCode == "OFFER_RESPONSE" && locked.OfferId.HasValue)
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
                if (IsBoundToCurrentStage(locked))
                {
                    // A published pipeline owns CurrentStage. Offer response is a
                    // business status used by the stage-exit rule; overwriting the
                    // configured stage here strands the card outside its pipeline.
                    await db.ExecuteAsync(@"UPDATE recruitment_candidate_applications
SET CurrentStatus=@Stage,UpdatedAt=UTC_TIMESTAMP()
WHERE Id=@ApplicationId;", new { locked.ApplicationId, Stage = applicationStage }, transaction);
                }
                else if (!offer.CurrentStage.Equals(applicationStage, StringComparison.OrdinalIgnoreCase))
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
                await ProjectSubmissionToCandidateAsync(db, transaction, locked.FormSubmissionId.Value, locked.CandidateId);
            }
            await db.ExecuteAsync("UPDATE recruitment_candidate_action_sessions SET Status='Completed',CompletedAtUtc=UTC_TIMESTAMP(6),LastUsedAtUtc=UTC_TIMESTAMP(6),UseCount=UseCount+1 WHERE Id=@Id", new { locked.Id }, transaction);
            await transaction.CommitAsync();
            return (new PublicCandidateActionResult
            {
                ApplicationId = locked.ApplicationId,
                PipelineStageInstanceId = locked.PipelineStageInstanceId,
                ShouldResumePipeline = IsBoundToCurrentStage(locked),
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

    private static async Task ProjectSubmissionToCandidateAsync(MySqlConnection db, MySqlTransaction transaction, long submissionId, long candidateId)
    {
        var values = (await db.QueryAsync<SemanticProjectionRow>(@"SELECT COALESCE(
 (SELECT semantic.SemanticCode FROM form_field_semantic_mappings mapping
  JOIN form_semantic_attributes semantic ON semantic.Id=mapping.SemanticAttributeId AND semantic.IsActive=TRUE
  WHERE mapping.FieldId=fieldRow.Id ORDER BY mapping.SemanticAttributeId LIMIT 1),fieldRow.StableFieldCode) SemanticCode,
valueRow.TextValue,valueRow.IntegerValue,valueRow.DecimalValue
FROM form_submission_values valueRow
JOIN form_fields fieldRow ON fieldRow.Id=valueRow.FieldId
WHERE valueRow.SubmissionId=@SubmissionId",
            new { SubmissionId = submissionId }, transaction)).ToList();
        if (values.Count == 0) return;
        SemanticProjectionRow? Value(string code) => values.FirstOrDefault(row => row.SemanticCode.Equals(code, StringComparison.OrdinalIgnoreCase));
        static string Text(SemanticProjectionRow? row) => (row?.TextValue ?? "").Trim();
        static decimal? Number(SemanticProjectionRow? row)
        {
            if (row?.DecimalValue is not null) return row.DecimalValue;
            if (row?.IntegerValue is not null) return row.IntegerValue.Value;
            return decimal.TryParse(row?.TextValue, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
        }

        var firstName = Value("FIRST_NAME");
        var lastName = Value("LAST_NAME");
        var email = Value("EMAIL");
        var phone = Value("PHONE");
        var currentLocation = Value("CURRENT_LOCATION");
        var currentCompany = Value("CURRENT_COMPANY");
        var currentDesignation = Value("CURRENT_DESIGNATION");
        var experience = Value("TOTAL_EXPERIENCE_MONTHS") ?? Value("TOTAL_EXPERIENCE_YEARS");
        var experienceIsYears = experience?.SemanticCode.Equals("TOTAL_EXPERIENCE_YEARS", StringComparison.OrdinalIgnoreCase) == true;
        var qualification = Value("HIGHEST_QUALIFICATION");
        var certifications = Value("CERTIFICATIONS");
        var currentCtc = Value("CURRENT_CTC");
        var expectedCtc = Value("EXPECTED_CTC");
        var noticePeriod = Value("NOTICE_PERIOD_DAYS");
        var emailText = Text(email);
        var phoneText = Text(phone);
        var experienceNumber = Number(experience);
        var currentCtcNumber = Number(currentCtc);
        var expectedCtcNumber = Number(expectedCtc);
        var noticePeriodNumber = Number(noticePeriod);
        await db.ExecuteAsync(@"UPDATE recruitment_candidates SET
FirstName=CASE WHEN @HasFirstName AND @FirstName<>'' THEN @FirstName ELSE FirstName END,
LastName=CASE WHEN @HasLastName THEN @LastName ELSE LastName END,
Email=CASE WHEN @HasEmail AND @Email<>'' THEN @Email ELSE Email END,
NormalizedEmail=CASE WHEN @HasEmail AND @Email<>'' THEN LOWER(TRIM(@Email)) ELSE NormalizedEmail END,
Phone=CASE WHEN @HasPhone AND @Phone<>'' THEN @Phone ELSE Phone END,
NormalizedPhone=CASE WHEN @HasPhone AND @Phone<>'' THEN REGEXP_REPLACE(@Phone,'[^0-9]','') ELSE NormalizedPhone END,
CurrentLocation=CASE WHEN @HasCurrentLocation THEN @CurrentLocation ELSE CurrentLocation END,
CurrentCompany=CASE WHEN @HasCurrentCompany THEN @CurrentCompany ELSE CurrentCompany END,
CurrentTitle=CASE WHEN @HasCurrentDesignation THEN @CurrentDesignation ELSE CurrentTitle END,
TotalExperienceMonths=CASE WHEN @HasExperience AND @ExperienceMonths IS NOT NULL THEN @ExperienceMonths ELSE TotalExperienceMonths END,
HighestQualification=CASE WHEN @HasQualification THEN @Qualification ELSE HighestQualification END,
CurrentCtc=CASE WHEN @HasCurrentCtc THEN @CurrentCtc ELSE CurrentCtc END,
ExpectedCtc=CASE WHEN @HasExpectedCtc THEN @ExpectedCtc ELSE ExpectedCtc END,
NoticePeriodDays=CASE WHEN @HasNoticePeriod THEN @NoticePeriodDays ELSE NoticePeriodDays END,
UpdatedAt=UTC_TIMESTAMP()
WHERE Id=@CandidateId", new
        {
            CandidateId = candidateId,
            HasFirstName = firstName is not null,
            FirstName = Text(firstName),
            HasLastName = lastName is not null,
            LastName = Text(lastName),
            HasEmail = email is not null,
            Email = emailText,
            HasPhone = phone is not null,
            Phone = phoneText,
            HasCurrentLocation = currentLocation is not null,
            CurrentLocation = Text(currentLocation),
            HasCurrentCompany = currentCompany is not null,
            CurrentCompany = Text(currentCompany),
            HasCurrentDesignation = currentDesignation is not null,
            CurrentDesignation = Text(currentDesignation),
            HasExperience = experience is not null,
            ExperienceMonths = experienceNumber.HasValue
                ? Math.Max(0, decimal.ToInt32(decimal.Round(experienceNumber.Value * (experienceIsYears ? 12m : 1m), 0, MidpointRounding.AwayFromZero)))
                : (int?)null,
            HasQualification = qualification is not null,
            Qualification = Text(qualification),
            HasCurrentCtc = currentCtc is not null,
            CurrentCtc = currentCtcNumber.HasValue ? Math.Max(0, currentCtcNumber.Value) : (decimal?)null,
            HasExpectedCtc = expectedCtc is not null,
            ExpectedCtc = expectedCtcNumber.HasValue ? Math.Max(0, expectedCtcNumber.Value) : (decimal?)null,
            HasNoticePeriod = noticePeriod is not null,
            NoticePeriodDays = noticePeriodNumber.HasValue ? Math.Max(0, decimal.ToInt32(decimal.Truncate(noticePeriodNumber.Value))) : (int?)null
        }, transaction);
        if (certifications is not null)
        {
            var names = Text(certifications).Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(100).ToArray();
            await db.ExecuteAsync("DELETE FROM recruitment_candidate_certifications WHERE CandidateId=@CandidateId", new { CandidateId = candidateId }, transaction);
            foreach (var name in names)
                await db.ExecuteAsync(@"INSERT INTO recruitment_candidate_certifications
(CandidateId,CertificationName,Issuer,IssueDate,ExpiryDate,CredentialId)
VALUES (@CandidateId,@CertificationName,'',NULL,NULL,'')", new { CandidateId = candidateId, CertificationName = name[..Math.Min(name.Length, 180)] }, transaction);
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
        if (existing.HasValue)
        {
            await db.ExecuteAsync("UPDATE external_portal_subjects SET CandidateId=COALESCE(CandidateId,@CandidateId) WHERE Id=@Id",
                new { source.CandidateId, Id = existing.Value }, transaction);
            return existing.Value;
        }
        return await db.ExecuteScalarAsync<long>(@"INSERT INTO external_portal_subjects
(ClientId,CandidateId,Email,NormalizedEmail,Phone,NormalizedPhone,ConsentAccepted,ConsentAcceptedAtUtc)
VALUES (@ClientId,@CandidateId,@Email,@NormalizedEmail,@Phone,@NormalizedPhone,TRUE,UTC_TIMESTAMP(6));SELECT LAST_INSERT_ID();",
            new { source.ClientId, source.CandidateId, Email = source.Email ?? "", NormalizedEmail = source.NormalizedEmail ?? "", Phone = source.Phone ?? "", NormalizedPhone = source.NormalizedPhone ?? "" }, transaction);
    }

    private async Task RefreshFormAndPrefillAsync(MySqlConnection db, ActionSessionRow session)
    {
        if (!session.FormVersionId.HasValue || !session.FormSubmissionId.HasValue) return;
        await using var transaction = await db.BeginTransactionAsync();
        try
        {
            var submission = await db.QueryFirstOrDefaultAsync<SubmissionRefreshRow>(@"SELECT submission.Id,submission.FormVersionId,submission.Status,
(SELECT COUNT(*) FROM form_submission_values valueRow WHERE valueRow.SubmissionId=submission.Id)
 +(SELECT COUNT(*) FROM form_submission_selected_options optionRow WHERE optionRow.SubmissionId=submission.Id)
 +(SELECT COUNT(*) FROM form_submission_lookup_values lookupRow WHERE lookupRow.SubmissionId=submission.Id)
 +(SELECT COUNT(*) FROM form_submission_attachments attachmentRow WHERE attachmentRow.SubmissionId=submission.Id) ResponseCount
FROM form_submissions submission
JOIN recruitment_candidate_action_sessions actionRow ON actionRow.FormSubmissionId=submission.Id
WHERE submission.Id=@SubmissionId AND actionRow.Id=@ActionId FOR UPDATE",
                new { SubmissionId = session.FormSubmissionId.Value, ActionId = session.Id }, transaction);
            if (submission is null || !submission.Status.Equals("Draft", StringComparison.OrdinalIgnoreCase))
            {
                await transaction.CommitAsync();
                return;
            }

            var effectiveVersionId = await ResolveEffectiveFormVersionAsync(db, submission.FormVersionId, session.ApplicationId, session.ClientId, transaction);
            if (effectiveVersionId.HasValue && effectiveVersionId.Value != submission.FormVersionId && submission.ResponseCount == 0)
            {
                await db.ExecuteAsync(@"UPDATE form_submissions SET FormVersionId=@FormVersionId,UpdatedAtUtc=UTC_TIMESTAMP(6) WHERE Id=@SubmissionId;
UPDATE recruitment_candidate_action_sessions SET FormVersionId=@FormVersionId WHERE Id=@ActionId;",
                    new { FormVersionId = effectiveVersionId.Value, SubmissionId = submission.Id, ActionId = session.Id }, transaction);
                submission.FormVersionId = effectiveVersionId.Value;
                session.FormVersionId = effectiveVersionId.Value;
            }

            await SeedSubmissionFromCandidateAsync(db, transaction, submission.Id, submission.FormVersionId, session.CandidateId);
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    private static Task<long?> ResolveEffectiveFormVersionAsync(
        MySqlConnection db,
        long? configuredVersionId,
        long applicationId,
        int clientId,
        MySqlTransaction? transaction = null)
    {
        if (!configuredVersionId.HasValue) return Task.FromResult<long?>(null);
        return db.ExecuteScalarAsync<long?>(@"SELECT COALESCE(
 (SELECT publishedVersion.Id FROM form_versions publishedVersion
  JOIN form_versions configuredVersion ON configuredVersion.Id=@ConfiguredVersionId
   AND configuredVersion.FormDefinitionId=publishedVersion.FormDefinitionId
  JOIN form_definitions definitionRow ON definitionRow.Id=publishedVersion.FormDefinitionId
   AND definitionRow.ClientId IN (0,@ClientId) AND definitionRow.Status='Active'
  WHERE publishedVersion.Status='Published'
  ORDER BY publishedVersion.VersionNumber DESC,publishedVersion.Id DESC LIMIT 1),
 (SELECT posting.ApplicationFormVersionId
  FROM recruitment_candidate_applications applicationRow
  JOIN recruitment_job_postings posting ON posting.PositionId=applicationRow.PositionId AND posting.ClientId=applicationRow.ClientId
   AND posting.Status IN ('Published','Closed') AND posting.ApplicationFormVersionId IS NOT NULL
  JOIN form_versions postingVersion ON postingVersion.Id=posting.ApplicationFormVersionId AND postingVersion.Status IN ('Published','Retired')
  JOIN form_versions configuredVersion ON configuredVersion.Id=@ConfiguredVersionId
   AND configuredVersion.FormDefinitionId=postingVersion.FormDefinitionId
  WHERE applicationRow.Id=@ApplicationId AND applicationRow.ClientId=@ClientId
  ORDER BY CASE WHEN posting.Status='Published' THEN 0 ELSE 1 END,posting.PublishedAtUtc DESC,posting.Id DESC LIMIT 1),
 @ConfiguredVersionId)", new { ConfiguredVersionId = configuredVersionId.Value, ApplicationId = applicationId, ClientId = clientId }, transaction);
    }

    private static async Task SeedSubmissionFromCandidateAsync(
        MySqlConnection db,
        MySqlTransaction transaction,
        long submissionId,
        long formVersionId,
        long candidateId)
    {
        var candidate = await db.QueryFirstOrDefaultAsync<CandidatePrefillRow>(@"SELECT c.FirstName,c.LastName,c.Email,c.Phone,
COALESCE(NULLIF(c.CurrentCompany,''),(SELECT experienceRow.Employer FROM recruitment_candidate_experience experienceRow
 WHERE experienceRow.CandidateId=c.Id ORDER BY experienceRow.IsCurrent DESC,experienceRow.DisplayOrder,experienceRow.Id LIMIT 1),'') CurrentCompany,
COALESCE(NULLIF(c.CurrentTitle,''),(SELECT experienceRow.JobTitle FROM recruitment_candidate_experience experienceRow
 WHERE experienceRow.CandidateId=c.Id ORDER BY experienceRow.IsCurrent DESC,experienceRow.DisplayOrder,experienceRow.Id LIMIT 1),'') CurrentTitle,
COALESCE(NULLIF(c.TotalExperienceMonths,0),(SELECT facts.TotalExperienceMonths FROM recruitment_candidate_resumes resume
 JOIN recruitment_resume_parse_facts facts ON facts.ResumeId=resume.Id
 WHERE resume.CandidateId=c.Id AND resume.IsPrimary=TRUE ORDER BY resume.CreatedAt DESC,resume.Id DESC LIMIT 1),0) TotalExperienceMonths,
c.CurrentLocation,c.NoticePeriodDays,c.CurrentCtc,c.ExpectedCtc,
COALESCE(NULLIF(c.HighestQualification,''),(SELECT educationRow.Qualification FROM recruitment_candidate_education educationRow
 WHERE educationRow.CandidateId=c.Id ORDER BY educationRow.DisplayOrder,educationRow.Id LIMIT 1),'') HighestQualification,
COALESCE((SELECT GROUP_CONCAT(certification.CertificationName ORDER BY certification.Id SEPARATOR ', ')
 FROM recruitment_candidate_certifications certification WHERE certification.CandidateId=c.Id),'') Certifications,
COALESCE((SELECT sectionRow.Content FROM recruitment_candidate_resumes resume
 JOIN recruitment_resume_sections sectionRow ON sectionRow.ResumeId=resume.Id AND sectionRow.SectionCode='GENERAL'
 WHERE resume.CandidateId=c.Id AND resume.IsPrimary=TRUE AND resume.ParsingStatus='Parsed'
 ORDER BY resume.CreatedAt DESC,sectionRow.DisplayOrder,sectionRow.Id LIMIT 1),'') ResumeGeneral,
COALESCE((SELECT sectionRow.Content FROM recruitment_candidate_resumes resume
 JOIN recruitment_resume_sections sectionRow ON sectionRow.ResumeId=resume.Id AND sectionRow.SectionCode='SUMMARY'
 WHERE resume.CandidateId=c.Id AND resume.IsPrimary=TRUE AND resume.ParsingStatus='Parsed' AND sectionRow.Confidence>=0.80
 ORDER BY resume.CreatedAt DESC,sectionRow.DisplayOrder,sectionRow.Id LIMIT 1),'') ResumeSummary,
COALESCE((SELECT sectionRow.Content FROM recruitment_candidate_resumes resume
 JOIN recruitment_resume_sections sectionRow ON sectionRow.ResumeId=resume.Id AND sectionRow.SectionCode='EXPERIENCE'
 WHERE resume.CandidateId=c.Id AND resume.IsPrimary=TRUE AND resume.ParsingStatus='Parsed' AND sectionRow.Confidence>=0.80
 ORDER BY resume.CreatedAt DESC,sectionRow.DisplayOrder,sectionRow.Id LIMIT 1),'') ResumeExperience,
COALESCE((SELECT sectionRow.Content FROM recruitment_candidate_resumes resume
 JOIN recruitment_resume_sections sectionRow ON sectionRow.ResumeId=resume.Id AND sectionRow.SectionCode='EDUCATION'
 WHERE resume.CandidateId=c.Id AND resume.IsPrimary=TRUE AND resume.ParsingStatus='Parsed' AND sectionRow.Confidence>=0.80
 ORDER BY resume.CreatedAt DESC,sectionRow.DisplayOrder,sectionRow.Id LIMIT 1),'') ResumeEducation,
COALESCE((SELECT sectionRow.Content FROM recruitment_candidate_resumes resume
 JOIN recruitment_resume_sections sectionRow ON sectionRow.ResumeId=resume.Id AND sectionRow.SectionCode='CERTIFICATIONS'
 WHERE resume.CandidateId=c.Id AND resume.IsPrimary=TRUE AND resume.ParsingStatus='Parsed' AND sectionRow.Confidence>=0.80
 ORDER BY resume.CreatedAt DESC,sectionRow.DisplayOrder,sectionRow.Id LIMIT 1),'') ResumeCertifications
FROM recruitment_candidates c WHERE c.Id=@CandidateId", new { CandidateId = candidateId }, transaction);
        if (candidate is null) return;
        await EnrichPrefillFromSubmittedFormsAsync(db, transaction, candidateId, candidate);
        EnrichPrefillFromResume(candidate);

        var fields = await db.QueryAsync<PrefillFieldRow>(@"SELECT fieldRow.Id FieldId,fieldRow.StableFieldCode,
fieldType.TypeCode FieldTypeCode,COALESCE(
 (SELECT semantic.SemanticCode FROM form_field_semantic_mappings mapping
  JOIN form_semantic_attributes semantic ON semantic.Id=mapping.SemanticAttributeId AND semantic.IsActive=TRUE
  WHERE mapping.FieldId=fieldRow.Id ORDER BY mapping.SemanticAttributeId LIMIT 1),'') SemanticCode
FROM form_fields fieldRow
JOIN form_field_types fieldType ON fieldType.Id=fieldRow.FieldTypeId
WHERE fieldRow.FormVersionId=@FormVersionId AND fieldRow.IsActive=TRUE
ORDER BY fieldRow.DisplayOrder,fieldRow.Id", new { FormVersionId = formVersionId }, transaction);

        foreach (var field in fields.GroupBy(row => row.FieldId).Select(group => group.First()))
        {
            string? textValue = null;
            long? integerValue = null;
            decimal? decimalValue = null;
            var semanticCode = string.IsNullOrWhiteSpace(field.SemanticCode) ? field.StableFieldCode : field.SemanticCode;
            switch (semanticCode.ToUpperInvariant())
            {
                case "FIRST_NAME": textValue = candidate.FirstName; break;
                case "LAST_NAME": textValue = candidate.LastName; break;
                case "EMAIL": textValue = candidate.Email; break;
                case "PHONE": textValue = candidate.Phone; break;
                case "CURRENT_LOCATION": textValue = candidate.CurrentLocation; break;
                case "CURRENT_COMPANY": textValue = candidate.CurrentCompany; break;
                case "CURRENT_DESIGNATION": textValue = candidate.CurrentTitle; break;
                case "HIGHEST_QUALIFICATION": textValue = candidate.HighestQualification; break;
                case "CERTIFICATIONS": textValue = candidate.Certifications; break;
                case "TOTAL_EXPERIENCE_MONTHS" when candidate.TotalExperienceMonths > 0: integerValue = candidate.TotalExperienceMonths; break;
                case "TOTAL_EXPERIENCE_YEARS" when candidate.TotalExperienceMonths > 0: decimalValue = Math.Round(candidate.TotalExperienceMonths / 12m, 1, MidpointRounding.AwayFromZero); break;
                case "NOTICE_PERIOD_DAYS" when candidate.NoticePeriodDays.HasValue: integerValue = candidate.NoticePeriodDays.Value; break;
                case "CURRENT_CTC" when candidate.CurrentCtc is > 0: decimalValue = candidate.CurrentCtc; break;
                case "EXPECTED_CTC" when candidate.ExpectedCtc is > 0: decimalValue = candidate.ExpectedCtc; break;
            }
            textValue = string.IsNullOrWhiteSpace(textValue) ? null : textValue.Trim();
            if (field.FieldTypeCode != "NUMBER" && textValue is null)
            {
                if (integerValue.HasValue) textValue = integerValue.Value.ToString(CultureInfo.InvariantCulture);
                else if (decimalValue.HasValue) textValue = decimalValue.Value.ToString(CultureInfo.InvariantCulture);
            }
            if (textValue is null && !integerValue.HasValue && !decimalValue.HasValue) continue;
            await db.ExecuteAsync(@"INSERT IGNORE INTO form_submission_values
(SubmissionId,FieldId,TextValue,IntegerValue,DecimalValue) VALUES (@SubmissionId,@FieldId,@TextValue,@IntegerValue,@DecimalValue)",
                new { SubmissionId = submissionId, field.FieldId, TextValue = textValue, IntegerValue = integerValue, DecimalValue = decimalValue }, transaction);
        }
    }

    private static async Task EnrichPrefillFromSubmittedFormsAsync(
        MySqlConnection db,
        MySqlTransaction transaction,
        long candidateId,
        CandidatePrefillRow candidate)
    {
        var priorValues = (await db.QueryAsync<SemanticProjectionRow>(@"SELECT COALESCE(
 (SELECT semantic.SemanticCode FROM form_field_semantic_mappings mapping
  JOIN form_semantic_attributes semantic ON semantic.Id=mapping.SemanticAttributeId AND semantic.IsActive=TRUE
  WHERE mapping.FieldId=fieldRow.Id ORDER BY mapping.SemanticAttributeId LIMIT 1),fieldRow.StableFieldCode) SemanticCode,
valueRow.TextValue,valueRow.IntegerValue,valueRow.DecimalValue
FROM form_submissions submission
JOIN form_submission_values valueRow ON valueRow.SubmissionId=submission.Id
JOIN form_fields fieldRow ON fieldRow.Id=valueRow.FieldId
WHERE submission.CandidateId=@CandidateId AND submission.Status='Submitted'
ORDER BY submission.SubmittedAtUtc DESC,submission.Id DESC,valueRow.FieldId",
            new { CandidateId = candidateId }, transaction)).ToList();
        SemanticProjectionRow? Prior(string code) => priorValues.FirstOrDefault(row => row.SemanticCode.Equals(code, StringComparison.OrdinalIgnoreCase));
        static string Text(SemanticProjectionRow? row) => (row?.TextValue ?? "").Trim();
        static decimal? Number(SemanticProjectionRow? row) => row?.DecimalValue ?? (row?.IntegerValue is long integer ? integer : (decimal?)null)
            ?? (decimal.TryParse(row?.TextValue, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : null);

        if (string.IsNullOrWhiteSpace(candidate.CurrentCompany)) candidate.CurrentCompany = Text(Prior("CURRENT_COMPANY"));
        if (string.IsNullOrWhiteSpace(candidate.CurrentTitle)) candidate.CurrentTitle = Text(Prior("CURRENT_DESIGNATION"));
        if (string.IsNullOrWhiteSpace(candidate.CurrentLocation)) candidate.CurrentLocation = Text(Prior("CURRENT_LOCATION"));
        if (string.IsNullOrWhiteSpace(candidate.HighestQualification)) candidate.HighestQualification = Text(Prior("HIGHEST_QUALIFICATION"));
        if (string.IsNullOrWhiteSpace(candidate.Certifications)) candidate.Certifications = Text(Prior("CERTIFICATIONS"));
        if (candidate.TotalExperienceMonths <= 0)
        {
            var months = Number(Prior("TOTAL_EXPERIENCE_MONTHS"));
            var years = Number(Prior("TOTAL_EXPERIENCE_YEARS"));
            var resolvedMonths = months ?? (years.HasValue ? years.Value * 12m : null);
            if (resolvedMonths is >= 0 and <= 1200)
                candidate.TotalExperienceMonths = decimal.ToInt32(Math.Round(resolvedMonths.Value, 0, MidpointRounding.AwayFromZero));
        }
        if (!candidate.CurrentCtc.HasValue || candidate.CurrentCtc <= 0) candidate.CurrentCtc = Number(Prior("CURRENT_CTC"));
        if (!candidate.ExpectedCtc.HasValue || candidate.ExpectedCtc <= 0) candidate.ExpectedCtc = Number(Prior("EXPECTED_CTC"));
        if (!candidate.NoticePeriodDays.HasValue)
        {
            var notice = Number(Prior("NOTICE_PERIOD_DAYS"));
            if (notice is >= 0 and <= 3650)
                candidate.NoticePeriodDays = decimal.ToInt32(Math.Round(notice.Value, 0, MidpointRounding.AwayFromZero));
        }
    }

    private static void EnrichPrefillFromResume(CandidatePrefillRow candidate)
    {
        var experienceLines = ResumeLines(candidate.ResumeExperience);
        if (string.IsNullOrWhiteSpace(candidate.CurrentTitle) || string.IsNullOrWhiteSpace(candidate.CurrentCompany))
        {
            var currentDateIndex = experienceLines.FindIndex(line => Regex.IsMatch(line,
                @"\b(?:19|20)\d{2}\b\s*[-–—]\s*(?:present|current|till\s+date|date)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
            var roleIndex = currentDateIndex <= 0 ? -1 : experienceLines.Take(currentDateIndex).Select((line, index) => (line, index))
                .Where(item => Regex.IsMatch(item.line,
                @"\b(administrator|analyst|architect|consultant|coordinator|designer|developer|engineer|executive|lead|manager|officer|specialist|supervisor)\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                .Select(item => item.index).LastOrDefault(-1);
            var companyIndex = roleIndex <= 0 ? -1 : Enumerable.Range(Math.Max(0, roleIndex - 8), Math.Min(8, roleIndex))
                .Where(index => index < roleIndex && !LooksLikeResumeDate(experienceLines[index]))
                .LastOrDefault(-1);
            if (roleIndex >= 0 && companyIndex >= 0)
            {
                if (string.IsNullOrWhiteSpace(candidate.CurrentTitle)) candidate.CurrentTitle = experienceLines[roleIndex];
                if (string.IsNullOrWhiteSpace(candidate.CurrentCompany)) candidate.CurrentCompany = experienceLines[companyIndex];
            }
        }

        if (candidate.TotalExperienceMonths <= 0)
        {
            var source = candidate.ResumeSummary;
            var years = Regex.Matches(source[..Math.Min(source.Length, 30000)],
                    @"(?<!\d)(?<years>\d{1,2}(?:\.\d{1,2})?)\s*\+?\s*(?:years?|yrs?)\b.{0,80}?\bexperience\b",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)
                .Select(match => decimal.TryParse(match.Groups["years"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : -1m)
                .Where(value => value is > 0 and <= 60)
                .DefaultIfEmpty(-1m)
                .Max();
            if (years > 0) candidate.TotalExperienceMonths = decimal.ToInt32(Math.Round(years * 12m, 0, MidpointRounding.AwayFromZero));
        }

        if (string.IsNullOrWhiteSpace(candidate.HighestQualification))
        {
            var qualification = ResumeLines(candidate.ResumeEducation)
                .FirstOrDefault(line => Regex.IsMatch(line,
                    @"\b(degree|bachelor|master|doctorate|ph\.?d|b\.?\s*tech|m\.?\s*tech|b\.?\s*e\.?|m\.?\s*e\.?|bsc|msc|mba|diploma)\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) ?? "";
            candidate.HighestQualification = Regex.Replace(qualification,
                @"\s*[·|]?\s*\((?:19|20)\d{2}\s*[-–—]\s*(?:(?:19|20)\d{2}|present)\)\s*$", "",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim();
        }

        if (string.IsNullOrWhiteSpace(candidate.CurrentLocation))
        {
            var location = Regex.Match(candidate.ResumeGeneral,
                @"(?<![A-Za-z])(?<location>[A-Z][A-Za-z.' ]{1,50},\s*[A-Z][A-Za-z.' ]{1,50},\s*India)\b",
                RegexOptions.CultureInvariant);
            if (location.Success) candidate.CurrentLocation = location.Groups["location"].Value.Trim();
        }

        if (string.IsNullOrWhiteSpace(candidate.Certifications) && !string.IsNullOrWhiteSpace(candidate.ResumeCertifications))
        {
            var certifications = string.Join(", ", ResumeLines(candidate.ResumeCertifications).Take(12));
            candidate.Certifications = certifications[..Math.Min(certifications.Length, 1000)];
        }
    }

    private static List<string> ResumeLines(string value) => System.Net.WebUtility.HtmlDecode(value ?? "")
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(line => Regex.Replace(line, @"\s+", " ").Trim(' ', '•', '?', '-', '|'))
        .Where(line => line.Length is > 1 and <= 240 && !line.StartsWith("Page ", StringComparison.OrdinalIgnoreCase))
        .Take(100)
        .ToList();

    private static bool LooksLikeResumeDate(string value) => Regex.IsMatch(value,
        @"\b(19|20)\d{2}\b|\b(january|february|march|april|may|june|july|august|september|october|november|december)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

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

    private static async Task<ActionSessionRow?> ValidateAsync(MySqlConnection db, string token, bool touch, bool allowCompleted = false)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var row = await db.QueryFirstOrDefaultAsync<ActionSessionRow>(SessionSql, new { TokenHash = Hash(token) });
        if (allowCompleted && IsAccessibleCompleted(row))
        {
            if (touch)
                await db.ExecuteAsync("UPDATE recruitment_candidate_action_sessions SET LastUsedAtUtc=UTC_TIMESTAMP(6) WHERE Id=@Id", new { row!.Id });
            return row;
        }
        if (IsSuperseded(row))
        {
            await db.ExecuteAsync("UPDATE recruitment_candidate_action_sessions SET Status='Revoked',RevokedAtUtc=COALESCE(RevokedAtUtc,UTC_TIMESTAMP(6)) WHERE Id=@Id AND Status='Open'", new { row!.Id });
            return null;
        }
        if (!IsValid(row)) return null;
        if (touch)
            await db.ExecuteAsync("UPDATE recruitment_candidate_action_sessions SET LastUsedAtUtc=UTC_TIMESTAMP(6) WHERE Id=@Id", new { row!.Id });
        return row;
    }

    private static bool IsBoundToCurrentStage(ActionSessionRow? row) => row?.PipelineStageInstanceId is not null
        && row.CurrentStageInstanceId == row.PipelineStageInstanceId;
    private static bool IsSuperseded(ActionSessionRow? row) => row is not null && !IsBoundToCurrentStage(row);
    private static bool IsValid(ActionSessionRow? row) => IsBoundToCurrentStage(row) && row!.Status == "Open" && row.RevokedAtUtc is null && row.ExpiresAtUtc > DateTime.UtcNow && row.UseCount < row.MaximumUses;
    private static bool IsAccessibleCompleted(ActionSessionRow? row) => row is not null
        && row.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase)
        && row.RevokedAtUtc is null
        && row.ExpiresAtUtc > DateTime.UtcNow;
    private static bool CanRetryCompletedSubmission(ActionSessionRow? row, CompletePublicCandidateActionRequest request) =>
        IsAccessibleCompleted(row)
        && !row!.PurposeCode.Equals("OFFER_RESPONSE", StringComparison.OrdinalIgnoreCase)
        && string.IsNullOrWhiteSpace(request.Decision);
    private static PublicCandidateActionResult CompletedSubmissionResult(ActionSessionRow row) => new()
    {
        ApplicationId = row.ApplicationId,
        PipelineStageInstanceId = row.PipelineStageInstanceId,
        ShouldResumePipeline = IsBoundToCurrentStage(row),
        Status = "COMPLETED",
        Message = "Your documents and information were already submitted successfully. Pipeline processing was retried."
    };
    private static Task<long?> LockCurrentStageAsync(MySqlConnection db, MySqlTransaction transaction, long applicationId) =>
        db.ExecuteScalarAsync<long?>("SELECT CurrentStageInstanceId FROM recruitment_application_pipeline_instances WHERE ApplicationId=@ApplicationId FOR UPDATE", new { ApplicationId = applicationId }, transaction);
    private static Task<int> RevokeSupersededSessionsAsync(MySqlConnection db, long applicationId, int? clientId, MySqlTransaction? transaction = null) =>
        db.ExecuteAsync(@"UPDATE recruitment_candidate_action_sessions action
JOIN recruitment_candidate_applications applicationRow ON applicationRow.Id=action.ApplicationId
LEFT JOIN recruitment_application_pipeline_instances pipeline ON pipeline.ApplicationId=applicationRow.Id
SET action.Status='Revoked',action.RevokedAtUtc=COALESCE(action.RevokedAtUtc,UTC_TIMESTAMP(6))
WHERE action.ApplicationId=@ApplicationId AND action.Status='Open' AND action.RevokedAtUtc IS NULL
  AND (@ClientId IS NULL OR applicationRow.ClientId=@ClientId)
  AND (action.PipelineStageInstanceId IS NULL OR pipeline.CurrentStageInstanceId IS NULL
       OR action.PipelineStageInstanceId<>pipeline.CurrentStageInstanceId)", new { ApplicationId = applicationId, ClientId = clientId }, transaction);
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
    private const string SessionSql = @"SELECT action.Id,action.ClientId,action.ApplicationId,action.CandidateId,action.PipelineStageInstanceId,action.FormVersionId,action.FormSubmissionId,action.OfferId,action.PurposeCode,action.Status,action.MaximumUses,action.UseCount,action.ExpiresAtUtc,action.RevokedAtUtc,pipeline.CurrentStageInstanceId
FROM recruitment_candidate_action_sessions action
LEFT JOIN recruitment_application_pipeline_instances pipeline ON pipeline.ApplicationId=action.ApplicationId
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

    private sealed class SubmissionRefreshRow
    {
        public long Id { get; set; }
        public long FormVersionId { get; set; }
        public string Status { get; set; } = "";
        public int ResponseCount { get; set; }
    }

    private sealed class CandidatePrefillRow
    {
        public string FirstName { get; set; } = "";
        public string LastName { get; set; } = "";
        public string Email { get; set; } = "";
        public string Phone { get; set; } = "";
        public string CurrentCompany { get; set; } = "";
        public string CurrentTitle { get; set; } = "";
        public int TotalExperienceMonths { get; set; }
        public string CurrentLocation { get; set; } = "";
        public int? NoticePeriodDays { get; set; }
        public decimal? CurrentCtc { get; set; }
        public decimal? ExpectedCtc { get; set; }
        public string HighestQualification { get; set; } = "";
        public string Certifications { get; set; } = "";
        public string ResumeGeneral { get; set; } = "";
        public string ResumeSummary { get; set; } = "";
        public string ResumeExperience { get; set; } = "";
        public string ResumeEducation { get; set; } = "";
        public string ResumeCertifications { get; set; } = "";
    }

    private sealed class PrefillFieldRow
    {
        public long FieldId { get; set; }
        public string FieldTypeCode { get; set; } = "";
        public string StableFieldCode { get; set; } = "";
        public string SemanticCode { get; set; } = "";
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
        public long? CurrentStageInstanceId { get; set; }
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

    private sealed class SemanticProjectionRow
    {
        public string SemanticCode { get; set; } = "";
        public string? TextValue { get; set; }
        public long? IntegerValue { get; set; }
        public decimal? DecimalValue { get; set; }
    }
}
