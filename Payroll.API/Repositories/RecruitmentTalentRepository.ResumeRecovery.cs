using System.Security.Cryptography;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class RecruitmentTalentRepository
{
    public async Task<(RecruitmentResumeRecoveryResult? Row, string Error)> RecoverApplicationResumeAsync(
        long applicationId, RecruitmentResumeRecoveryRequest request, AuthUser user,
        string ipAddress, string userAgent, CancellationToken cancellationToken)
    {
        if (request.File is null || request.File.Length <= 0 || request.File.Length > 10 * 1024 * 1024)
            return (null, "Select a resume up to 10 MB.");
        if (request.ExpectedResumeId <= 0 || request.FieldConfigurationId <= 0
            || string.IsNullOrWhiteSpace(request.SourceMessageId) || request.SourceMessageId.Length > 500
            || request.ReceivedAt == default || request.ReceivedAt > DateTimeOffset.UtcNow.AddMinutes(5))
            return (null, "Original resume, attachment field and source email evidence are required.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(request.Sha256 ?? "", "^[a-fA-F0-9]{64}$"))
            return (null, "The verified reply attachment SHA-256 is required.");

        var connection = new MySqlConnectionStringBuilder(configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("The recruitment database connection is not configured.")) { Pooling = false };
        await using var db = new MySqlConnection(connection.ConnectionString);
        await db.OpenAsync(cancellationToken);
        if (await RecruitmentApplicationResumeGuard.AcquireAsync(db, applicationId, cancellationToken) != 1)
            return (null, "This application is being assessed or recovered. Retry after it finishes.");
        try
        {
            return await RecoverApplicationResumeLockedAsync(db, applicationId, request, user, ipAddress, userAgent, cancellationToken);
        }
        finally
        {
            try { await RecruitmentApplicationResumeGuard.ReleaseAsync(db, applicationId); }
            catch (Exception exception) { logger.LogWarning("Resume recovery lock release failed ({Type}); its dedicated session will close.", exception.GetType().Name); }
        }
    }

    private async Task<(RecruitmentResumeRecoveryResult? Row, string Error)> RecoverApplicationResumeLockedAsync(
        MySqlConnection db, long applicationId, RecruitmentResumeRecoveryRequest request, AuthUser user,
        string ipAddress, string userAgent, CancellationToken cancellationToken)
    {
        var application = await ApplicationByIdAsync(db, applicationId, user);
        if (application is null || application.ApplicationCode != request.ApplicationCode) return (null, "Application was not found.");
        var candidate = await CandidateByIdAsync(db, application.CandidateId);
        if (candidate is null || !await CanAccessCandidateAsync(db, user, candidate)) return (null, "Candidate was not found.");
        if (!RecruitmentResumeRecoveryIdentity.SameMailbox(candidate.Email, request.SenderEmail))
            return (null, "Reply sender does not match the application's candidate email.");
        if (candidate.ConsentStatus.Equals("Revoked", StringComparison.OrdinalIgnoreCase)
            || candidate.RetentionUntil.HasValue && candidate.RetentionUntil < DateTime.UtcNow)
            return (null, "Review candidate consent/retention before recovering a resume.");
        var validResumeField = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM attachment_field_configurations fieldRow
JOIN attachment_attributes attributeRow ON attributeRow.id=fieldRow.attachment_attribute_id
WHERE fieldRow.id=@Id AND fieldRow.is_active=TRUE AND attributeRow.is_active=TRUE
 AND attributeRow.attribute_code='RESUME' AND fieldRow.module_code='RECRUITMENT'
 AND fieldRow.form_code IN ('CANDIDATE_APPLICATION','EMPLOYEE_REFERRAL')", new { Id = request.FieldConfigurationId });
        if (validResumeField != 1) return (null, "Select an active recruitment Resume attachment field.");

        var recoveryFile = request.File!; // Validated before opening the dedicated session.
        await using (var input = recoveryFile.OpenReadStream())
        {
            var digest = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken));
            if (!digest.Equals(request.Sha256, StringComparison.OrdinalIgnoreCase)) return (null, "Reply attachment hash did not match.");
        }
        var original = await db.QuerySingleOrDefaultAsync<ResumeRecoveryAttachment>(@"SELECT resume.Id ResumeId,
attachment.public_id PublicId,attachment.storage_key StorageKey,attachment.sha256_hash Sha256Hash,attachment.is_deleted IsDeleted
FROM recruitment_candidate_resumes resume JOIN entity_attachments attachment ON attachment.public_id=resume.AttachmentPublicId
WHERE resume.Id=@ResumeId AND resume.CandidateId=@CandidateId", new { ResumeId = application.ResumeId, application.CandidateId });
        if (original is null) return (null, "The application's recorded resume attachment was not found.");
        if (!original.IsDeleted)
        {
            var (_, originalServer, accessError) = await attachments.GetForContentAsync(original.PublicId, user, "PREVIEW", ipAddress, userAgent);
            if (originalServer is null) return (null, accessError ?? "The original storage could not be checked.");
            try
            {
                await using var file = await attachmentStorage.OpenReadAsync(originalServer, original.StorageKey, cancellationToken);
                if (!original.Sha256Hash.Equals(request.Sha256, StringComparison.OrdinalIgnoreCase))
                    return (null, "An original is already available. Recovery will not replace it.");
                var storedDigest = Convert.ToHexString(await SHA256.HashDataAsync(file.Stream, cancellationToken));
                if (!storedDigest.Equals(request.Sha256, StringComparison.OrdinalIgnoreCase))
                    return (null, "Stored content differs from its recorded hash. Review storage before recovery.");
                return (new(application.Id, application.ApplicationCode, original.ResumeId, original.PublicId, true, "Existing"), "");
            }
            catch (FileNotFoundException) { /* Only a missing original may be recovered. */ }
        }
        if (application.ResumeId != request.ExpectedResumeId) return (null, "The application's resume changed. Refresh before recovery.");

        // Preserve all prior versions and do not run pipeline automation.
        var parse = await resumeParser.ParseAsync(recoveryFile, cancellationToken);
        var (attachment, uploadError) = await attachments.UploadAsync(new AttachmentUploadMetadata
        {
            FieldConfigurationId = request.FieldConfigurationId,
            EntityType = "CANDIDATE", EntityId = candidate.Id,
            DocumentNumber = "EMAIL-RECOVERY:" + application.ApplicationCode
        }, recoveryFile, user, ipAddress, userAgent, cancellationToken, restoreMissingContent: true);
        if (attachment is null) return (null, uploadError ?? "Resume upload failed.");
        var resume = await RegisterResumeAsync(db, candidate, attachment, parse, user, suppressAutoScoring: true,
            recoveryApplicationId: application.Id, expectedResumeId: request.ExpectedResumeId);
        await WriteActivityAsync(db, application.ClientId, candidate.Id, candidate.EmployeeId, "RECRUITMENT",
            "RESUME_RECOVERED_FROM_EMAIL", "Missing resume recovered from candidate reply",
            $"{application.ApplicationCode}; source Message-ID {request.SourceMessageId}; sender {request.SenderEmail}; received {request.ReceivedAt.ToUniversalTime():O}; SHA-256 {request.Sha256}; previous resume {request.ExpectedResumeId}; new resume {resume.Id}. Previous assessment archived; pipeline stage preserved.",
            "RecruitmentApplication", application.Id.ToString(), user);
        var reassessmentError = "";
        var eligibilityStatus = "";
        if (request.Reassess)
        {
            // Generate an assessment directly: do not enqueue a job whose worker can run stage actions.
            using var observation = engineMonitor.Observe("ats-scoring", new EngineActivityContext(
                "Assess recovered resume; preserve pipeline stage", "Recruitment application", application.Id.ToString(), ApplicationId: application.Id));
            try
            {
                var (score, error) = await ScoreApplicationWithResumeLockAsync(db, application.Id, user, true, observation);
                eligibilityStatus = score?.EligibilityStatus ?? "";
                reassessmentError = error;
                observation.Succeeded = score is not null;
            }
            catch (Exception exception)
            {
                logger.LogWarning("Recovered resume assessment failed for application {Id} ({Type}).", application.Id, exception.GetType().Name);
                reassessmentError = "Resume restored. ATS reassessment needs recruiter review.";
            }
        }
        return (new(application.Id, application.ApplicationCode, resume.Id, attachment.PublicId, false, parse.Status,
            eligibilityStatus, reassessmentError), "");
    }

    private sealed class ResumeRecoveryAttachment
    {
        public long ResumeId { get; set; }
        public Guid PublicId { get; set; }
        public string StorageKey { get; set; } = "";
        public string Sha256Hash { get; set; } = "";
        public bool IsDeleted { get; set; }
    }
}
