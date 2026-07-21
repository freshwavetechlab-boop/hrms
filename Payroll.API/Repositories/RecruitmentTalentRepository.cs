using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.AspNetCore.Http;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed class RecruitmentTalentRepository(
    IConfiguration configuration,
    AttachmentRepository attachments,
    AttachmentStorageService attachmentStorage,
    ResumeParsingService resumeParser,
    TemplatePdfService templatePdf,
    EmployeeRepository employees,
    WorkflowRepository workflows)
{
    private MySqlConnection Db() => new(configuration.GetConnectionString("Default"));
    private static readonly AtsCriterionDefinition[] AtsCriterionDefinitions =
    [
        new("requiredSkills", "Required skills", "SkillMatch", 35m, 10),
        new("preferredSkills", "Preferred skills", "SkillMatch", 10m, 20),
        new("experience", "Relevant experience", "ExperienceRange", 20m, 30),
        new("qualification", "Qualification", "TextMatch", 10m, 40),
        new("certifications", "Certifications", "TextMatch", 5m, 50),
        new("roleSimilarity", "Role similarity", "TokenSimilarity", 10m, 60),
        new("location", "Location", "LocationMatch", 5m, 70),
        new("noticePeriod", "Notice period", "NoticePeriod", 5m, 80)
    ];
    private static readonly string[] AtsWeightKeys = AtsCriterionDefinitions.Select(row => row.Code).ToArray();
    private static readonly string[] InterviewStatuses = ["Scheduled", "Rescheduled", "Completed", "Cancelled", "No Show"];
    private static readonly string[] InterviewResults = ["Pending", "Selected", "Rejected", "On Hold", "No Show", "Reschedule"];
    private static readonly string[] InterviewRecommendations = ["Strong Hire", "Hire", "On Hold", "No Hire", "Strong No Hire"];
    private static readonly string[] OfferStatuses = ["Draft", "Pending Approval", "Approved", "Pending Candidate", "Released", "Negotiation", "Accepted", "Rejected", "Expired", "Withdrawn"];
    private static readonly string[] CandidateProfileStatuses = ["Active", "Inactive", "Joined", "Archived"];
    private static readonly string[] CandidateConsentStatuses = ["Pending", "Granted", "Revoked"];

    public async Task InitializeAsync()
    {
        await using var db = Db();
        await db.OpenAsync();
        await EnsureTablesAsync(db);
        await EnsureExistingColumnsAsync(db);
        await EnsureOfferIndexesAsync(db);
        await SeedAttachmentConfigurationsAsync(db);
        await MigrateLegacyRecruitmentIntelligenceAsync(db);
        await DropObsoleteAtsConfigurationColumnsAsync(db);
        await SeedScoringProfilesAsync(db);
        await SeedMissingScoringProfileCriteriaAsync(db);
        await EnsureApplicationResumeIntegrityAsync(db);
        await EnsureNormalizedAtsForeignKeysAsync(db);
    }

    public async Task<RecruitmentTalentDashboard> DashboardAsync(AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        var clientId = user.ClientId;
        return new RecruitmentTalentDashboard
        {
            TalentProfiles = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_candidates c WHERE (@ClientId IS NULL OR c.ClientId=@ClientId OR EXISTS (SELECT 1 FROM recruitment_candidate_applications a WHERE a.CandidateId=c.Id AND a.ClientId=@ClientId)) AND c.ProfileStatus<>'Archived'", new { ClientId = clientId }),
            ActiveApplications = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_candidate_applications WHERE (@ClientId IS NULL OR ClientId=@ClientId) AND CurrentStage NOT IN ('Rejected','Withdrawn','Joined')", new { ClientId = clientId }),
            InterviewsScheduled = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM recruitment_interviews i JOIN recruitment_candidate_applications a ON a.Id=i.ApplicationId WHERE (@ClientId IS NULL OR a.ClientId=@ClientId) AND i.Status IN ('Scheduled','Rescheduled')", new { ClientId = clientId }),
            OffersPending = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM recruitment_offers o JOIN recruitment_candidate_applications a ON a.Id=o.ApplicationId WHERE (@ClientId IS NULL OR a.ClientId=@ClientId) AND o.Status IN ('Draft','Pending Approval','Approved','Pending Candidate','Released','Negotiation')", new { ClientId = clientId }),
            PreOnboardingPending = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM recruitment_candidate_checklist_items c JOIN recruitment_candidate_applications a ON a.Id=c.ApplicationId WHERE (@ClientId IS NULL OR a.ClientId=@ClientId) AND c.Status<>'Completed'", new { ClientId = clientId }),
            Joined = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_candidate_applications WHERE (@ClientId IS NULL OR ClientId=@ClientId) AND CurrentStage='Joined'", new { ClientId = clientId })
        };
    }

    public async Task<IEnumerable<RecruitmentCandidate>> SearchCandidatesAsync(AuthUser user, int? clientId, string query, string status)
    {
        await using var db = Db();
        await db.OpenAsync();
        var scopeClientId = user.ClientId ?? clientId;
        return await db.QueryAsync<RecruitmentCandidate>($@"{CandidateSelect}
WHERE (@ClientId IS NULL OR c.ClientId=@ClientId OR EXISTS (SELECT 1 FROM recruitment_candidate_applications ca WHERE ca.CandidateId=c.Id AND ca.ClientId=@ClientId))
  AND (@Status='' OR c.ProfileStatus=@Status)
  AND (@Query='' OR CONCAT(c.CandidateCode,' ',c.FirstName,' ',c.LastName,' ',c.Email,' ',c.Phone,' ',c.CurrentCompany,' ',c.CurrentTitle) LIKE CONCAT('%',@Query,'%') OR EXISTS (SELECT 1 FROM recruitment_candidate_skills cs WHERE cs.CandidateId=c.Id AND cs.SkillName LIKE CONCAT('%',@Query,'%')))
ORDER BY c.UpdatedAt DESC LIMIT 500", new { ClientId = scopeClientId, ScopeClientId = scopeClientId, Query = query?.Trim() ?? "", Status = status?.Trim() ?? "" });
    }

    public async Task<RecruitmentCandidateDetail?> GetCandidateDetailAsync(long id, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        var candidate = await db.QueryFirstOrDefaultAsync<RecruitmentCandidate>($@"{CandidateSelect}
WHERE c.Id=@Id AND (@ClientId IS NULL OR c.ClientId=@ClientId OR EXISTS (SELECT 1 FROM recruitment_candidate_applications ca WHERE ca.CandidateId=c.Id AND ca.ClientId=@ClientId))", new { Id = id, ClientId = user.ClientId, ScopeClientId = user.ClientId });
        if (candidate is null) return null;
        var applications = (await ApplicationsAsync(db, user, candidateId: id)).ToList();
        var applicationIds = applications.Select(row => row.Id).ToArray();
        var resumes = (await db.QueryAsync<RecruitmentCandidateResume>($@"{ResumeSummarySelect} WHERE r.CandidateId=@Id ORDER BY r.IsPrimary DESC,r.CreatedAt DESC", new { Id = id })).ToList();
        await HydrateResumeIntelligenceAsync(db, resumes);
        var scores = applicationIds.Length == 0
            ? new List<RecruitmentApplicationScore>()
            : (await db.QueryAsync<RecruitmentApplicationScore>("SELECT * FROM recruitment_application_scores WHERE ApplicationId IN @Ids ORDER BY IsCurrent DESC,ScoredAt DESC", new { Ids = applicationIds })).ToList();
        await HydrateScoresAsync(db, scores);
        return new RecruitmentCandidateDetail
        {
            Candidate = candidate,
            Resumes = resumes,
            Applications = applications,
            Skills = await db.QueryAsync<RecruitmentCandidateSkill>("SELECT * FROM recruitment_candidate_skills WHERE CandidateId=@Id ORDER BY Confidence DESC,SkillName", new { Id = id }),
            Experience = await db.QueryAsync<RecruitmentCandidateExperience>("SELECT * FROM recruitment_candidate_experience WHERE CandidateId=@Id ORDER BY DisplayOrder,StartDate DESC", new { Id = id }),
            Education = await db.QueryAsync<RecruitmentCandidateEducation>("SELECT * FROM recruitment_candidate_education WHERE CandidateId=@Id ORDER BY DisplayOrder,CompletionYear DESC", new { Id = id }),
            Certifications = await db.QueryAsync<RecruitmentCandidateCertification>("SELECT * FROM recruitment_candidate_certifications WHERE CandidateId=@Id ORDER BY IssueDate DESC", new { Id = id }),
            Scores = scores,
            Interviews = applicationIds.Length == 0 ? [] : await InterviewRowsAsync(db, user, null, applicationIds),
            Offers = applicationIds.Length == 0 ? [] : await OfferRowsAsync(db, user, null, applicationIds),
            Checklist = applicationIds.Length == 0 ? [] : await db.QueryAsync<RecruitmentCandidateChecklistItem>("SELECT * FROM recruitment_candidate_checklist_items WHERE ApplicationId IN @Ids ORDER BY ApplicationId,Mandatory DESC,DisplayOrder,ChecklistName", new { Ids = applicationIds }),
            Activity = await ActivityForCandidateAsync(db, id, user)
        };
    }

    public async Task<(RecruitmentCandidate? Row, string Error)> SaveCandidateAsync(SaveRecruitmentCandidate request, AuthUser user)
    {
        request.ClientId = user.ClientId ?? request.ClientId;
        if (request.ClientId <= 0) return (null, "Client is required.");
        if (string.IsNullOrWhiteSpace(request.FirstName)) return (null, "Candidate first name is required.");
        if (string.IsNullOrWhiteSpace(request.Email) && string.IsNullOrWhiteSpace(request.Phone)) return (null, "Candidate email or phone is required.");
        if (!CanAccessClient(user, request.ClientId)) return (null, "Candidate client is outside your permitted scope.");
        if (!CandidateProfileStatuses.Contains(request.ProfileStatus, StringComparer.OrdinalIgnoreCase)) return (null, "Select a valid talent profile status.");
        if (!CandidateConsentStatuses.Contains(request.ConsentStatus, StringComparer.OrdinalIgnoreCase)) return (null, "Select a valid candidate consent status.");
        request.ProfileStatus = CandidateProfileStatuses.First(value => value.Equals(request.ProfileStatus, StringComparison.OrdinalIgnoreCase));
        request.ConsentStatus = CandidateConsentStatuses.First(value => value.Equals(request.ConsentStatus, StringComparison.OrdinalIgnoreCase));
        var normalizedEmail = NormalizeEmail(request.Email);
        var normalizedPhone = NormalizePhone(request.Phone);
        await using var db = Db();
        await db.OpenAsync();
        var setting = await db.QueryFirstOrDefaultAsync<(bool AllowDuplicateCandidate, int CandidateRetentionMonths)>("SELECT AllowDuplicateCandidate,CandidateRetentionMonths FROM recruitment_settings WHERE ClientId=@ClientId AND IsActive=TRUE LIMIT 1", new { request.ClientId });
        var duplicate = await db.QueryFirstOrDefaultAsync<RecruitmentCandidate>(@"SELECT * FROM recruitment_candidates WHERE Id<>@Id AND ProfileStatus<>'Archived' AND ((@Email<>'' AND NormalizedEmail=@Email) OR (@Phone<>'' AND NormalizedPhone=@Phone)) ORDER BY Id LIMIT 1", new { request.Id, Email = normalizedEmail, Phone = normalizedPhone });
        if (duplicate is not null && !setting.AllowDuplicateCandidate)
            return (null, $"A talent profile already exists: {duplicate.CandidateCode} - {duplicate.FirstName} {duplicate.LastName}. Open the existing profile instead of creating a duplicate.");
        var retention = request.RetentionUntil ?? (request.Id <= 0 && setting.CandidateRetentionMonths > 0 ? DateTime.UtcNow.AddMonths(setting.CandidateRetentionMonths) : null);
        var consentCapturedAt = request.ConsentStatus == "Granted" ? request.ConsentCapturedAt ?? DateTime.UtcNow : request.ConsentCapturedAt;
        long id;
        var action = request.Id > 0 ? "Updated" : "Created";
        if (request.Id <= 0)
        {
            var code = await NextNumberAsync(db, request.ClientId, "CAN", "CAN");
            id = await db.ExecuteScalarAsync<long>(@"INSERT INTO recruitment_candidates
(CandidateCode,ClientId,FirstName,LastName,Email,NormalizedEmail,Phone,NormalizedPhone,CurrentCompany,CurrentTitle,TotalExperienceMonths,CurrentLocation,PreferredLocationsJson,NoticePeriodDays,CurrentCtc,ExpectedCtc,HighestQualification,SourceType,SourceReferenceId,ProfileStatus,ConsentStatus,ConsentCapturedAt,RetentionUntil,DuplicateOfCandidateId,CreatedByUserId)
VALUES (@CandidateCode,@ClientId,@FirstName,@LastName,@Email,@NormalizedEmail,@Phone,@NormalizedPhone,@CurrentCompany,@CurrentTitle,@TotalExperienceMonths,@CurrentLocation,@PreferredLocationsJson,@NoticePeriodDays,@CurrentCtc,@ExpectedCtc,@HighestQualification,@SourceType,@SourceReferenceId,@ProfileStatus,@ConsentStatus,@ConsentCapturedAt,@RetentionUntil,@DuplicateOfCandidateId,@UserId);SELECT LAST_INSERT_ID();", new
            {
                CandidateCode = code,
                request.ClientId,
                FirstName = request.FirstName.Trim(), LastName = request.LastName.Trim(), Email = request.Email.Trim(), NormalizedEmail = normalizedEmail,
                Phone = request.Phone.Trim(), NormalizedPhone = normalizedPhone, request.CurrentCompany, request.CurrentTitle,
                TotalExperienceMonths = Math.Max(0, request.TotalExperienceMonths), request.CurrentLocation,
                PreferredLocationsJson = ValidJson(request.PreferredLocationsJson, "[]"), NoticePeriodDays = Math.Max(0, request.NoticePeriodDays),
                CurrentCtc = Math.Max(0, request.CurrentCtc), ExpectedCtc = Math.Max(0, request.ExpectedCtc), request.HighestQualification,
                request.SourceType, request.SourceReferenceId, request.ProfileStatus, request.ConsentStatus, ConsentCapturedAt = consentCapturedAt,
                RetentionUntil = retention, DuplicateOfCandidateId = duplicate?.Id, UserId = user.Id
            });
        }
        else
        {
            var existing = await db.QueryFirstOrDefaultAsync<RecruitmentCandidate>("SELECT * FROM recruitment_candidates WHERE Id=@Id", new { request.Id });
            if (existing is null || !await CanAccessCandidateAsync(db, user, existing)) return (null, "Candidate was not found.");
            if (existing.ClientId != request.ClientId) return (null, "A talent profile's client cannot be changed after creation. Create an application for another client instead.");
            retention ??= existing.RetentionUntil;
            id = request.Id;
            await db.ExecuteAsync(@"UPDATE recruitment_candidates SET FirstName=@FirstName,LastName=@LastName,Email=@Email,NormalizedEmail=@NormalizedEmail,Phone=@Phone,NormalizedPhone=@NormalizedPhone,CurrentCompany=@CurrentCompany,CurrentTitle=@CurrentTitle,TotalExperienceMonths=@TotalExperienceMonths,CurrentLocation=@CurrentLocation,PreferredLocationsJson=@PreferredLocationsJson,NoticePeriodDays=@NoticePeriodDays,CurrentCtc=@CurrentCtc,ExpectedCtc=@ExpectedCtc,HighestQualification=@HighestQualification,SourceType=@SourceType,SourceReferenceId=@SourceReferenceId,ProfileStatus=@ProfileStatus,ConsentStatus=@ConsentStatus,ConsentCapturedAt=@ConsentCapturedAt,RetentionUntil=@RetentionUntil,UpdatedAt=UTC_TIMESTAMP() WHERE Id=@Id", new
            {
                request.Id, FirstName = request.FirstName.Trim(), LastName = request.LastName.Trim(), Email = request.Email.Trim(), NormalizedEmail = normalizedEmail,
                Phone = request.Phone.Trim(), NormalizedPhone = normalizedPhone, request.CurrentCompany, request.CurrentTitle,
                TotalExperienceMonths = Math.Max(0, request.TotalExperienceMonths), request.CurrentLocation,
                PreferredLocationsJson = ValidJson(request.PreferredLocationsJson, "[]"), NoticePeriodDays = Math.Max(0, request.NoticePeriodDays),
                CurrentCtc = Math.Max(0, request.CurrentCtc), ExpectedCtc = Math.Max(0, request.ExpectedCtc), request.HighestQualification,
                request.SourceType, request.SourceReferenceId, request.ProfileStatus, request.ConsentStatus, ConsentCapturedAt = consentCapturedAt, RetentionUntil = retention
            });
        }
        await WriteRecruitmentAuditAsync(db, "RecruitmentCandidate", id, action, user.Id, request);
        await WriteActivityAsync(db, request.ClientId, id, null, "RECRUITMENT", $"CANDIDATE_{action.ToUpperInvariant()}", $"Talent profile {action.ToLowerInvariant()}", $"{request.FirstName} {request.LastName}".Trim(), "RecruitmentCandidate", id.ToString(), user);
        return (await CandidateByIdAsync(db, id), "");
    }

    public async Task<IEnumerable<RecruitmentCandidateApplication>> GetApplicationsAsync(AuthUser user, long? positionId, long? candidateId, string stage)
    {
        await using var db = Db();
        await db.OpenAsync();
        return await ApplicationsAsync(db, user, positionId, candidateId, stage);
    }

    public async Task<(RecruitmentCandidateDetail? Row, string Error)> SaveCandidateProfileSectionsAsync(long candidateId, SaveCandidateProfileSections request, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        var candidate = await CandidateByIdAsync(db, candidateId);
        if (candidate is null || !await CanAccessCandidateAsync(db, user, candidate)) return (null, "Candidate was not found.");
        await using var transaction = await db.BeginTransactionAsync();
        await db.ExecuteAsync("DELETE FROM recruitment_candidate_experience WHERE CandidateId=@CandidateId;DELETE FROM recruitment_candidate_education WHERE CandidateId=@CandidateId;DELETE FROM recruitment_candidate_certifications WHERE CandidateId=@CandidateId;", new { CandidateId = candidateId }, transaction);
        var order = 10;
        foreach (var row in request.Experience.Where(row => !string.IsNullOrWhiteSpace(row.Employer) || !string.IsNullOrWhiteSpace(row.JobTitle)).Take(50))
        {
            await db.ExecuteAsync(@"INSERT INTO recruitment_candidate_experience (CandidateId,Employer,JobTitle,StartDate,EndDate,IsCurrent,Description,DisplayOrder) VALUES (@CandidateId,@Employer,@JobTitle,@StartDate,@EndDate,@IsCurrent,@Description,@DisplayOrder)", new { CandidateId = candidateId, Employer = row.Employer.Trim(), JobTitle = row.JobTitle.Trim(), row.StartDate, EndDate = row.IsCurrent ? null : row.EndDate, row.IsCurrent, Description = row.Description.Trim(), DisplayOrder = order }, transaction);
            order += 10;
        }
        order = 10;
        foreach (var row in request.Education.Where(row => !string.IsNullOrWhiteSpace(row.Qualification) || !string.IsNullOrWhiteSpace(row.Institution)).Take(30))
        {
            await db.ExecuteAsync(@"INSERT INTO recruitment_candidate_education (CandidateId,Qualification,Institution,Specialization,CompletionYear,Score,DisplayOrder) VALUES (@CandidateId,@Qualification,@Institution,@Specialization,@CompletionYear,@Score,@DisplayOrder)", new { CandidateId = candidateId, Qualification = row.Qualification.Trim(), Institution = row.Institution.Trim(), Specialization = row.Specialization.Trim(), row.CompletionYear, Score = row.Score.Trim(), DisplayOrder = order }, transaction);
            order += 10;
        }
        foreach (var row in request.Certifications.Where(row => !string.IsNullOrWhiteSpace(row.CertificationName)).Take(50))
            await db.ExecuteAsync(@"INSERT INTO recruitment_candidate_certifications (CandidateId,CertificationName,Issuer,IssueDate,ExpiryDate,CredentialId) VALUES (@CandidateId,@CertificationName,@Issuer,@IssueDate,@ExpiryDate,@CredentialId)", new { CandidateId = candidateId, CertificationName = row.CertificationName.Trim(), Issuer = row.Issuer.Trim(), row.IssueDate, row.ExpiryDate, CredentialId = row.CredentialId.Trim() }, transaction);
        await transaction.CommitAsync();
        await WriteRecruitmentAuditAsync(db, "RecruitmentCandidate", candidateId, "Profile Sections Updated", user.Id, request);
        await WriteActivityAsync(db, candidate.ClientId, candidateId, candidate.EmployeeId, "RECRUITMENT", "CANDIDATE_PROFILE_UPDATED", "Candidate profile details updated", $"{request.Experience.Count} experience, {request.Education.Count} education, {request.Certifications.Count} certification entries", "RecruitmentCandidate", candidateId.ToString(CultureInfo.InvariantCulture), user);
        return (await GetCandidateDetailAsync(candidateId, user), "");
    }

    public async Task<(RecruitmentCandidateApplication? Row, string Error)> CreateApplicationAsync(SaveCandidateApplication request, AuthUser user)
    {
        if (request.CandidateId <= 0 || request.PositionId <= 0) return (null, "Candidate and open position are required.");
        await using var db = Db();
        await db.OpenAsync();
        var candidate = await db.QueryFirstOrDefaultAsync<RecruitmentCandidate>("SELECT * FROM recruitment_candidates WHERE Id=@Id AND ProfileStatus<>'Archived'", new { Id = request.CandidateId });
        var position = await db.QueryFirstOrDefaultAsync<RecruitmentOpenPosition>("SELECT * FROM recruitment_open_positions WHERE Id=@Id", new { Id = request.PositionId });
        if (candidate is null || position is null || !CanAccessClient(user, position.ClientId) || !await CanAccessCandidateAsync(db, user, candidate)) return (null, "Candidate or open position was not found.");
        if (!candidate.ProfileStatus.Equals("Active", StringComparison.OrdinalIgnoreCase)) return (null, $"A new application cannot be created for a {candidate.ProfileStatus} talent profile.");
        if (candidate.ConsentStatus.Equals("Revoked", StringComparison.OrdinalIgnoreCase)) return (null, "Candidate consent is revoked. A new application cannot be created.");
        if (candidate.RetentionUntil.HasValue && candidate.RetentionUntil.Value < DateTime.UtcNow) return (null, "Candidate retention period has expired. Review or archive the talent profile before further processing.");
        if (position.Status is "Closed" or "Cancelled" or "Filled") return (null, "Applications cannot be added to this position status.");
        var existingId = await db.ExecuteScalarAsync<long?>("SELECT Id FROM recruitment_candidate_applications WHERE CandidateId=@CandidateId AND PositionId=@PositionId", request);
        if (existingId.HasValue) return (await ApplicationByIdAsync(db, existingId.Value, user), "This candidate is already linked to the position.");
        var code = await NextNumberAsync(db, position.ClientId, "APP", "APP");
        var resumeId = request.ResumeId ?? await db.ExecuteScalarAsync<long?>("SELECT Id FROM recruitment_candidate_resumes WHERE CandidateId=@CandidateId AND IsPrimary=TRUE ORDER BY CreatedAt DESC LIMIT 1", request);
        if (resumeId.HasValue)
        {
            var validResume = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*)
FROM recruitment_candidate_resumes r
JOIN entity_attachments attachment ON attachment.public_id=r.AttachmentPublicId
 AND attachment.entity_type='CANDIDATE' AND attachment.entity_id=r.CandidateId
 AND attachment.is_current=TRUE AND attachment.is_deleted=FALSE
WHERE r.Id=@ResumeId AND r.CandidateId=@CandidateId", new { ResumeId = resumeId.Value, request.CandidateId });
            if (validResume == 0) return (null, "The selected resume is not an active document for this candidate.");
        }
        var recruiter = request.RecruiterUserId ?? (position.RecruiterUserId > 0 ? position.RecruiterUserId : null);
        var id = await db.ExecuteScalarAsync<long>(@"INSERT INTO recruitment_candidate_applications
(ApplicationCode,CandidateId,PositionId,ClientId,SourceType,SourceReferenceId,ResumeId,CurrentStatus,CurrentStage,RecruiterUserId,AppliedAt,LastStageChangedAt)
VALUES (@Code,@CandidateId,@PositionId,@ClientId,@SourceType,@SourceReferenceId,@ResumeId,'New','New',@RecruiterUserId,UTC_TIMESTAMP(),UTC_TIMESTAMP());SELECT LAST_INSERT_ID();", new { Code = code, request.CandidateId, request.PositionId, position.ClientId, request.SourceType, request.SourceReferenceId, ResumeId = resumeId, RecruiterUserId = recruiter });
        await db.ExecuteAsync("INSERT INTO recruitment_application_stage_history (ApplicationId,FromStage,ToStage,Reason,ChangedByUserId) VALUES (@Id,'','New','Application created',@UserId)", new { Id = id, UserId = user.Id });
        await AddPositionTimelineAsync(db, position.Id, "Candidate Added", "Candidate added", $"{candidate.FirstName} {candidate.LastName} / {code}", user.Id);
        await WriteActivityAsync(db, position.ClientId, candidate.Id, candidate.EmployeeId, "RECRUITMENT", "APPLICATION_CREATED", "Application created", $"Applied for {position.PositionTitle} ({position.PositionCode})", "RecruitmentApplication", id.ToString(), user);
        await RefreshPositionCountersAsync(db, position.Id);
        if (resumeId.HasValue) await ScoreApplicationInternalAsync(db, id, user, false);
        return (await ApplicationByIdAsync(db, id, user), "");
    }

    public async Task<(RecruitmentCandidateApplication? Row, string Error)> ChangeStageAsync(long applicationId, ChangeCandidateStageRequest request, AuthUser user)
    {
        if (string.IsNullOrWhiteSpace(request.Stage)) return (null, "Candidate stage is required.");
        await using var db = Db();
        await db.OpenAsync();
        var row = await ApplicationByIdAsync(db, applicationId, user);
        if (row is null) return (null, "Application was not found.");
        var targetStage = request.Stage.Trim();
        if (row.CurrentStage.Equals(targetStage, StringComparison.OrdinalIgnoreCase)) return (row, "");
        if (row.CurrentStage is "Rejected" or "Withdrawn" or "Joined") return (null, $"An application in {row.CurrentStage} stage cannot be moved manually.");
        if (targetStage is "Offer Released" or "Offer Accepted" or "Joined") return (null, $"{targetStage} is controlled by the offer or employee-conversion workflow.");
        if (string.IsNullOrWhiteSpace(request.Reason)) return (null, "Reason is required for a manual candidate stage change.");
        if (!await IsConfiguredCandidateStatusAsync(db, row.ClientId, targetStage)) return (null, "Selected candidate stage is not configured in Recruitment Administration.");
        var features = await FeatureSettingsAsync(db, row.ClientId);
        if (features.RequireResumeForApplication
            && !targetStage.Equals("New", StringComparison.OrdinalIgnoreCase)
            && !targetStage.Equals("Rejected", StringComparison.OrdinalIgnoreCase)
            && !targetStage.Equals("Withdrawn", StringComparison.OrdinalIgnoreCase)
            && !await HasResumeAsync(db, row.CandidateId, row.ResumeId))
            return (null, "Upload a resume through Candidate Documents before moving this application forward.");
        var nextStatus = string.IsNullOrWhiteSpace(request.Status) ? request.Stage : request.Status.Trim();
        await db.ExecuteAsync(@"UPDATE recruitment_candidate_applications SET CurrentStage=@Stage,CurrentStatus=@Status,DispositionReason=@Reason,LastStageChangedAt=UTC_TIMESTAMP(),RejectedAt=CASE WHEN @Stage='Rejected' THEN UTC_TIMESTAMP() ELSE RejectedAt END,WithdrawnAt=CASE WHEN @Stage='Withdrawn' THEN UTC_TIMESTAMP() ELSE WithdrawnAt END,UpdatedAt=UTC_TIMESTAMP() WHERE Id=@Id", new { Id = applicationId, Stage = request.Stage.Trim(), Status = nextStatus, Reason = request.Reason.Trim() });
        await db.ExecuteAsync("INSERT INTO recruitment_application_stage_history (ApplicationId,FromStage,ToStage,Reason,ChangedByUserId) VALUES (@Id,@From,@To,@Reason,@UserId)", new { Id = applicationId, From = row.CurrentStage, To = request.Stage.Trim(), request.Reason, UserId = user.Id });
        await AddPositionTimelineAsync(db, row.PositionId, "Candidate Stage", $"{row.CandidateName}: {request.Stage}", request.Reason, user.Id);
        await WriteRecruitmentAuditAsync(db, "RecruitmentApplication", applicationId, "Stage Change", user.Id, new { from = row.CurrentStage, to = request.Stage, request.Reason });
        await WriteActivityAsync(db, row.ClientId, row.CandidateId, null, "RECRUITMENT", "APPLICATION_STAGE_CHANGED", $"Application moved to {request.Stage}", $"{row.PositionTitle}: {request.Reason}".Trim(), "RecruitmentApplication", applicationId.ToString(), user);
        await RefreshPositionCountersAsync(db, row.PositionId);
        return (await ApplicationByIdAsync(db, applicationId, user), "");
    }

    public async Task<(EntityAttachment? Attachment, RecruitmentCandidateResume? Resume, string Error)> UploadResumeAsync(long candidateId, CandidateResumeUploadRequest request, AuthUser user, string ipAddress, string userAgent, CancellationToken cancellationToken)
    {
        if (request.File is null) return (null, null, "Select a resume file.");
        await using var db = Db();
        await db.OpenAsync();
        var candidate = await CandidateByIdAsync(db, candidateId);
        if (candidate is null || !await CanAccessCandidateAsync(db, user, candidate)) return (null, null, "Candidate was not found.");
        if (candidate.ConsentStatus.Equals("Revoked", StringComparison.OrdinalIgnoreCase)) return (null, null, "Candidate consent is revoked. A new resume cannot be stored.");
        if (candidate.RetentionUntil.HasValue && candidate.RetentionUntil.Value < DateTime.UtcNow) return (null, null, "Candidate retention period has expired. Review the talent profile before uploading another resume.");
        var maximumFileSize = await db.ExecuteScalarAsync<long?>(@"SELECT f.maximum_file_size_bytes FROM attachment_field_configurations f JOIN attachment_attributes a ON a.id=f.attachment_attribute_id WHERE f.id=@Id AND f.is_active=TRUE AND a.is_active=TRUE AND a.attribute_code='RESUME' AND f.module_code='RECRUITMENT' AND f.form_code IN ('CANDIDATE_APPLICATION','EMPLOYEE_REFERRAL') LIMIT 1", new { Id = request.FieldConfigurationId });
        if (!maximumFileSize.HasValue) return (null, null, "Select an active global Resume attachment field.");
        if (request.File.Length > maximumFileSize.Value) return (null, null, $"Resume exceeds the configured {Math.Ceiling(maximumFileSize.Value / 1048576m):0.#} MB limit.");
        var features = await FeatureSettingsAsync(db, candidate.ClientId);
        var parse = features.EnableResumeParsing
            ? await resumeParser.ParseAsync(request.File, cancellationToken)
            : ResumeParseResult.WithoutContent("Disabled", "Disabled", "", "Resume parsing is disabled in Recruitment Administration.");
        var (attachment, error) = await attachments.UploadAsync(new AttachmentUploadMetadata
        {
            FieldConfigurationId = request.FieldConfigurationId,
            EntityType = "CANDIDATE",
            EntityId = candidateId,
            DocumentNumber = request.DocumentNumber,
            IssueDate = request.IssueDate,
            ExpiryDate = request.ExpiryDate
        }, request.File, user, ipAddress, userAgent, cancellationToken);
        if (attachment is null) return (null, null, error ?? "Resume upload failed.");
        var resume = await RegisterResumeAsync(db, candidate, attachment, parse, user);
        return (attachment, resume, "");
    }

    public async Task<(EntityAttachment? Attachment, RecruitmentCandidateResume? Resume, string Error)> UploadReferralResumeAsync(long referralId, CandidateResumeUploadRequest request, AuthUser user, string ipAddress, string userAgent, CancellationToken cancellationToken)
    {
        if (!user.EmployeeId.HasValue) return (null, null, "Employee profile is required.");
        await using var db = Db();
        await db.OpenAsync(cancellationToken);
        var candidateId = await db.ExecuteScalarAsync<long?>(@"SELECT CandidateId FROM recruitment_employee_referrals
WHERE Id=@ReferralId AND ReferrerEmployeeId=@EmployeeId", new { ReferralId = referralId, EmployeeId = user.EmployeeId.Value });
        if (!candidateId.HasValue) return (null, null, "Referral candidate was not found.");
        return await UploadResumeAsync(candidateId.Value, request, user, ipAddress, userAgent, cancellationToken);
    }

    public async Task<(RecruitmentCandidateResume? Resume, string Error)> RegisterExistingResumeAsync(long candidateId, Guid publicId, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        var candidate = await CandidateByIdAsync(db, candidateId);
        if (candidate is null || !await CanAccessCandidateAsync(db, user, candidate)) return (null, "Candidate was not found.");
        var attachment = await db.QueryFirstOrDefaultAsync<EntityAttachment>(@"SELECT id Id,public_id PublicId,client_id ClientId,attachment_attribute_id AttachmentAttributeId,field_configuration_id FieldConfigurationId,storage_server_id StorageServerId,entity_type EntityType,entity_id EntityId,original_file_name OriginalFileName,version_number VersionNumber,uploaded_at_utc UploadedAtUtc FROM entity_attachments WHERE public_id=@PublicId AND entity_type='CANDIDATE' AND entity_id=@CandidateId AND is_current=TRUE AND is_deleted=FALSE", new { PublicId = publicId.ToString(), CandidateId = candidateId });
        if (attachment is null) return (null, "The selected global document is not linked to this candidate.");
        var existing = await db.QueryFirstOrDefaultAsync<RecruitmentCandidateResume>("SELECT * FROM recruitment_candidate_resumes WHERE AttachmentPublicId=@PublicId AND CandidateId=@CandidateId", new { PublicId = publicId.ToString(), CandidateId = candidateId });
        if (existing is null) return (null, "Use the candidate resume upload action so parsing can be completed.");
        await HydrateResumeIntelligenceAsync(db, [existing]);
        return (existing, "");
    }

    public async Task<(RecruitmentApplicationScore? Row, string Error)> ScoreApplicationAsync(long applicationId, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        return await ScoreApplicationInternalAsync(db, applicationId, user, true);
    }

    public async Task<(RecruitmentApplicationScore? Score, string Warning)> ProcessPublicApplicationResumeAsync(
        long applicationId,
        AuthUser user,
        string ipAddress,
        string userAgent,
        CancellationToken cancellationToken)
    {
        await using var db = Db();
        await db.OpenAsync(cancellationToken);
        var link = await db.QueryFirstOrDefaultAsync<PublicApplicationResumeRow>(@"SELECT a.Id ApplicationId,a.CandidateId,a.ClientId,a.ResumeId RequestedResumeId,
r.Id ResumeId,r.AttachmentPublicId,r.ParsingStatus
FROM recruitment_candidate_applications a
LEFT JOIN recruitment_candidate_resumes r ON r.Id=a.ResumeId AND r.CandidateId=a.CandidateId
LEFT JOIN entity_attachments attachment ON attachment.public_id=r.AttachmentPublicId
 AND attachment.entity_type='CANDIDATE' AND attachment.entity_id=a.CandidateId
 AND attachment.is_current=TRUE AND attachment.is_deleted=FALSE
WHERE a.Id=@ApplicationId AND (r.Id IS NULL OR attachment.id IS NOT NULL)
LIMIT 1", new { ApplicationId = applicationId });
        if (link is null || !CanAccessClient(user, link.ClientId)) return (null, "The submitted application could not be prepared for ATS review.");
        if (link.RequestedResumeId.HasValue && link.ResumeId <= 0)
            return (null, "The selected resume is not linked to this candidate and needs HR review.");
        if (link.ResumeId <= 0 || !link.AttachmentPublicId.HasValue) return (null, "");

        var features = await FeatureSettingsAsync(db, link.ClientId);
        if (!features.EnableResumeParsing)
        {
            await db.ExecuteAsync(@"UPDATE recruitment_candidate_resumes SET ParsingStatus='Disabled',ParserName='Disabled',
ParserVersion='',ParsedAt=UTC_TIMESTAMP(),ParsingError='Resume parsing is disabled in Recruitment Administration.'
WHERE Id=@ResumeId AND CandidateId=@CandidateId AND ParsingStatus<>'Parsed'", link);
            return (null, "");
        }

        if (!link.ParsingStatus.Equals("Parsed", StringComparison.OrdinalIgnoreCase))
        {
            var publicId = link.AttachmentPublicId.Value;
            try
            {
                var (attachment, server, accessError) = await attachments.GetForContentAsync(publicId, user, "PUBLIC_RESUME_PARSE", ipAddress, userAgent);
                if (attachment is null || server is null)
                    return (null, string.IsNullOrWhiteSpace(accessError) ? "The uploaded resume could not be opened for parsing." : accessError!);
                await using var handle = await attachmentStorage.OpenReadAsync(server, attachment.StorageKey, cancellationToken);
                var parse = await resumeParser.ParseAsync(handle.Stream, attachment.OriginalFileName, attachment.FileSizeBytes, cancellationToken);
                var candidate = await CandidateByIdAsync(db, link.CandidateId);
                if (candidate is null) return (null, "The candidate profile could not be prepared for ATS review.");

                await using var transaction = await db.BeginTransactionAsync(cancellationToken);
                await db.ExecuteAsync(@"UPDATE recruitment_candidate_resumes SET ParsingStatus=@Status,ParsedText=@Text,
ParsedJson=JSON_OBJECT(),ParserName=@ParserName,ParserVersion=@ParserVersion,ParsedAt=UTC_TIMESTAMP(),ParsingError=@Error
WHERE Id=@ResumeId AND CandidateId=@CandidateId", new { parse.Status, parse.Text, ParserName = parse.ParserName, parse.ParserVersion, parse.Error, link.ResumeId, link.CandidateId }, transaction);
                await db.ExecuteAsync(@"INSERT INTO recruitment_resume_parser_runs
(ResumeId,ParserName,ParserVersion,ParseStatus,ExtractedCharacterCount,ExtractedLineCount,ErrorMessage,StartedAt,CompletedAt)
VALUES (@ResumeId,@ParserName,@ParserVersion,@Status,@CharacterCount,@LineCount,@Error,UTC_TIMESTAMP(),UTC_TIMESTAMP())", new { link.ResumeId, ParserName = parse.ParserName, parse.ParserVersion, parse.Status, CharacterCount = parse.Facts.CharacterCount, LineCount = parse.Facts.LineCount, parse.Error }, transaction);
                await db.ExecuteAsync(@"INSERT INTO recruitment_resume_parse_facts
(ResumeId,ExtractedEmail,ExtractedPhone,CharacterCount,LineCount,LanguageCode,SummaryText,TotalExperienceMonths)
VALUES (@ResumeId,@Email,@Phone,@CharacterCount,@LineCount,@LanguageCode,@SummaryText,@TotalExperienceMonths)
ON DUPLICATE KEY UPDATE ExtractedEmail=VALUES(ExtractedEmail),ExtractedPhone=VALUES(ExtractedPhone),
CharacterCount=VALUES(CharacterCount),LineCount=VALUES(LineCount),LanguageCode=VALUES(LanguageCode),
SummaryText=VALUES(SummaryText),TotalExperienceMonths=VALUES(TotalExperienceMonths),UpdatedAt=UTC_TIMESTAMP()", new { link.ResumeId, Email = parse.Facts.Email, Phone = parse.Facts.Phone, CharacterCount = parse.Facts.CharacterCount, LineCount = parse.Facts.LineCount, parse.Facts.LanguageCode, parse.Facts.SummaryText, parse.Facts.TotalExperienceMonths }, transaction);
                await db.ExecuteAsync("DELETE FROM recruitment_resume_sections WHERE ResumeId=@ResumeId;DELETE FROM recruitment_resume_skills WHERE ResumeId=@ResumeId;", new { link.ResumeId }, transaction);
                foreach (var section in parse.Sections)
                    await db.ExecuteAsync(@"INSERT INTO recruitment_resume_sections
(ResumeId,SectionCode,Heading,Content,DisplayOrder,Confidence)
VALUES (@ResumeId,@SectionCode,@Heading,@Content,@DisplayOrder,@Confidence)", new { link.ResumeId, section.SectionCode, section.Heading, section.Content, section.DisplayOrder, section.Confidence }, transaction);
                if (parse.Status == "Parsed")
                {
                    await db.ExecuteAsync("DELETE FROM recruitment_candidate_skills WHERE CandidateId=@CandidateId AND Source='Resume'", new { link.CandidateId }, transaction);
                    await ExtractCandidateSkillsAsync(db, candidate, link.ResumeId, parse.Text, transaction);
                    await ApplyParsedContactAsync(db, candidate, parse.Facts, transaction);
                }
                await transaction.CommitAsync(cancellationToken);
                await WriteActivityAsync(db, link.ClientId, link.CandidateId, candidate.EmployeeId, "RECRUITMENT", "PUBLIC_RESUME_PARSED",
                    "Public application resume processed", $"{attachment.OriginalFileName} / {parse.Status}", "CandidateResume", link.ResumeId.ToString(CultureInfo.InvariantCulture), user);
                if (parse.Status != "Parsed")
                    return (null, "The application was submitted, but the resume needs manual review before ATS scoring.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                await db.ExecuteAsync(@"UPDATE recruitment_candidate_resumes SET ParsingStatus='Failed',ParserName='BuiltIn',
ParserVersion='2.0',ParsedAt=UTC_TIMESTAMP(),ParsingError='The stored resume could not be opened or parsed.'
WHERE Id=@ResumeId AND CandidateId=@CandidateId", link);
                return (null, "The application was submitted, but resume parsing needs HR review.");
            }
        }

        if (!features.EnableAtsScoring) return (null, "");
        var (score, scoringError) = await ScoreApplicationInternalAsync(db, applicationId, user, false);
        if (score is not null || scoringError.Contains("Automatic ATS scoring is disabled", StringComparison.OrdinalIgnoreCase))
            return (score, "");
        return (null, string.IsNullOrWhiteSpace(scoringError) ? "" : $"The application was submitted, but ATS scoring needs HR review: {scoringError}");
    }

    public async Task<(RecruitmentApplicationScore? Row, string Error)> OverrideScoreAsync(long scoreId, OverrideApplicationScoreRequest request, AuthUser user)
    {
        if (request.Score is < 0 or > 100) return (null, "Override score must be between 0 and 100.");
        if (string.IsNullOrWhiteSpace(request.Reason)) return (null, "Override reason is required.");
        await using var db = Db();
        await db.OpenAsync();
        var row = await db.QueryFirstOrDefaultAsync<(long ApplicationId, int ClientId, long CandidateId, string PositionTitle, long? ScoringProfileId, bool IsCurrent, string CurrentStage)>(@"SELECT s.ApplicationId,a.ClientId,a.CandidateId,p.PositionTitle,s.ScoringProfileId,s.IsCurrent,a.CurrentStage FROM recruitment_application_scores s JOIN recruitment_candidate_applications a ON a.Id=s.ApplicationId JOIN recruitment_open_positions p ON p.Id=a.PositionId WHERE s.Id=@Id", new { Id = scoreId });
        if (row.ApplicationId <= 0 || !CanAccessClient(user, row.ClientId)) return (null, "Score was not found.");
        if (!row.IsCurrent) return (null, "Only the current ATS score can be overridden.");
        if ((row.CurrentStage is "Rejected" or "Withdrawn" or "Joined") || row.CurrentStage.StartsWith("Offer", StringComparison.OrdinalIgnoreCase)) return (null, $"ATS score cannot be overridden after the application reaches {row.CurrentStage} stage.");
        var features = await FeatureSettingsAsync(db, row.ClientId);
        if (!features.AllowManualScoreOverride) return (null, "Manual ATS score override is disabled in Recruitment Administration.");
        var allow = row.ScoringProfileId is null || await db.ExecuteScalarAsync<bool>("SELECT AllowManualOverride FROM recruitment_ats_scoring_profiles WHERE Id=@Id", new { Id = row.ScoringProfileId });
        if (!allow) return (null, "Manual score override is disabled for this scoring profile.");
        await db.ExecuteAsync("UPDATE recruitment_application_scores SET OverrideScore=@Score,OverrideReason=@Reason,OverriddenByUserId=@UserId,OverriddenAt=UTC_TIMESTAMP() WHERE Id=@Id", new { Id = scoreId, request.Score, Reason = request.Reason.Trim(), UserId = user.Id });
        await WriteRecruitmentAuditAsync(db, "RecruitmentApplicationScore", scoreId, "Manual Override", user.Id, request);
        await WriteActivityAsync(db, row.ClientId, row.CandidateId, null, "RECRUITMENT", "ATS_SCORE_OVERRIDDEN", "ATS score manually overridden", $"{row.PositionTitle}: {request.Score:0.##}/100 / {request.Reason.Trim()}", "RecruitmentApplicationScore", scoreId.ToString(CultureInfo.InvariantCulture), user);
        var result = await db.QueryFirstAsync<RecruitmentApplicationScore>("SELECT * FROM recruitment_application_scores WHERE Id=@Id", new { Id = scoreId });
        await HydrateScoresAsync(db, [result]);
        return (result, "");
    }

    public async Task<IEnumerable<RecruitmentInterview>> GetInterviewsAsync(AuthUser user, long? applicationId = null)
    {
        await using var db = Db();
        await db.OpenAsync();
        return await InterviewRowsAsync(db, user, applicationId, null);
    }

    public async Task<(RecruitmentInterviewSchedulingContext? Row, string Error)> GetInterviewSchedulingContextAsync(long applicationId, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        var application = await ApplicationByIdAsync(db, applicationId, user);
        if (application is null) return (null, "Application was not found.");
        var pipeline = await InterviewPipelineContextAsync(db, applicationId, null);
        if (pipeline is { HasPipelineInstance: true } && !string.Equals(pipeline.StageType, "Interview", StringComparison.OrdinalIgnoreCase))
            return (null, $"Interview scheduling is available when the application reaches an Interview pipeline stage. Current stage: {pipeline.PipelineStageName}.");
        if (pipeline is { HasPipelineInstance: true, RoundConfigurationId: null })
            return (null, "The current Interview pipeline stage has no round configuration. Configure the round before scheduling.");
        var context = new RecruitmentInterviewSchedulingContext { ApplicationId = applicationId };
        if (pipeline is { HasPipelineInstance: true })
        {
            context.IsPipelineManaged = true;
            context.PipelineStageInstanceId = pipeline.PipelineStageInstanceId;
            context.RoundConfigurationId = pipeline.RoundConfigurationId;
            context.PipelineStageName = pipeline.PipelineStageName;
            context.RoundCode = pipeline.PipelineStageName;
            context.InterviewType = pipeline.InterviewType;
            context.DefaultDurationMinutes = Math.Max(1, pipeline.DefaultDurationMinutes);
            context.MinimumPanelCount = Math.Max(1, pipeline.MinimumPanelCount);
            context.MinimumPassingScore = pipeline.MinimumPassingScore;
            context.FeedbackRequired = pipeline.FeedbackRequired;
            context.CalendarEnabled = pipeline.CalendarEnabled;
            context.AllowReschedule = pipeline.AllowReschedule;
            context.NextAttemptNumber = await db.ExecuteScalarAsync<int>("SELECT COALESCE(MAX(AttemptNumber),0)+1 FROM recruitment_interviews WHERE ApplicationId=@ApplicationId AND RoundConfigurationId=@RoundConfigurationId", new { ApplicationId = applicationId, RoundConfigurationId = pipeline.RoundConfigurationId });
            context.Competencies = (await InterviewCompetenciesAsync(db, pipeline.RoundConfigurationId!.Value)).Select(ToStageCompetency).ToList();
        }
        return (context, "");
    }

    public async Task<(RecruitmentInterview? Row, string Error)> SaveInterviewAsync(SaveRecruitmentInterview request, AuthUser user)
    {
        request.PanelUserIds ??= [];
        request.RoundCode = (request.RoundCode ?? "").Trim();
        request.InterviewType = (request.InterviewType ?? "").Trim();
        request.Mode = (request.Mode ?? "").Trim();
        request.LocationOrLink = (request.LocationOrLink ?? "").Trim();
        request.Status = (request.Status ?? "").Trim();
        request.Result = (request.Result ?? "").Trim();
        request.OverallFeedback = (request.OverallFeedback ?? "").Trim();
        request.TimeZoneId = (request.TimeZoneId ?? "").Trim();
        if (request.ApplicationId <= 0) return (null, "Application is required.");
        if (request.ScheduledEnd <= request.ScheduledStart) return (null, "Interview end time must be after start time.");
        if (!InterviewStatuses.Contains(request.Status, StringComparer.OrdinalIgnoreCase)) return (null, "Select a valid interview status.");
        if (!InterviewResults.Contains(request.Result, StringComparer.OrdinalIgnoreCase)) return (null, "Select a valid interview result.");
        request.Status = InterviewStatuses.First(value => value.Equals(request.Status, StringComparison.OrdinalIgnoreCase));
        request.Result = InterviewResults.First(value => value.Equals(request.Result, StringComparison.OrdinalIgnoreCase));
        if (request.Status == "Completed" && request.Result == "Pending") return (null, "Select the final interview result before marking the interview completed.");
        if (request.Id <= 0 && request.Status == "Completed") return (null, "Save the interview schedule before completing it.");
        await using var db = Db();
        await db.OpenAsync();
        var application = await ApplicationByIdAsync(db, request.ApplicationId, user);
        if (application is null) return (null, "Application was not found.");
        var applicationStage = application.CurrentStage ?? "";
        if (request.Id <= 0 && ((applicationStage is "Rejected" or "Withdrawn" or "Joined") || applicationStage.StartsWith("Offer", StringComparison.OrdinalIgnoreCase))) return (null, $"An interview cannot be scheduled for an application in {applicationStage} stage.");
        var pipeline = await InterviewPipelineContextAsync(db, request.ApplicationId, request.Id > 0 ? request.Id : null);
        if (pipeline is { HasPipelineInstance: true } && !string.Equals(pipeline.StageType, "Interview", StringComparison.OrdinalIgnoreCase))
            return (null, $"Interview scheduling is available when the application reaches an Interview pipeline stage. Current stage: {pipeline.PipelineStageName}.");
        if (pipeline is { HasPipelineInstance: true, RoundConfigurationId: null })
            return (null, "The current Interview pipeline stage has no round configuration. Configure the round before scheduling.");
        var isPipelineManaged = pipeline is { HasPipelineInstance: true };
        if (isPipelineManaged)
        {
            request.RoundCode = pipeline!.PipelineStageName;
            request.InterviewType = pipeline.InterviewType;
            request.TimeZoneId = string.IsNullOrWhiteSpace(request.TimeZoneId) ? "Asia/Kolkata" : request.TimeZoneId.Trim();
            var durationMinutes = (request.ScheduledEnd - request.ScheduledStart).TotalMinutes;
            if (durationMinutes < pipeline.DefaultDurationMinutes)
                return (null, $"This round requires at least {pipeline.DefaultDurationMinutes} minutes.");
        }
        else if (string.IsNullOrWhiteSpace(request.RoundCode)) return (null, "Interview round is required.");
        var panelUserIds = request.PanelUserIds.Where(value => value > 0).Distinct().ToArray();
        if (isPipelineManaged && panelUserIds.Length < pipeline!.MinimumPanelCount)
            return (null, $"This round requires at least {pipeline.MinimumPanelCount} panel member(s).");
        if (panelUserIds.Length > 0)
        {
            var validPanelUsers = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM authusers u WHERE u.Id IN @Ids AND (u.ClientId=@ClientId OR u.ClientId IS NULL) AND (u.IsActive=TRUE OR (@InterviewId>0 AND EXISTS (SELECT 1 FROM recruitment_interview_panel_members pm WHERE pm.InterviewId=@InterviewId AND pm.PanelUserId=u.Id)))", new { Ids = panelUserIds, application.ClientId, InterviewId = request.Id });
            if (validPanelUsers != panelUserIds.Length) return (null, "One or more panel members are inactive or outside the application's client.");
            var conflict = await db.QueryFirstOrDefaultAsync<InterviewScheduleConflictRow>(@"SELECT i.Id,i.RoundCode,i.ScheduledStart,i.ScheduledEnd,COALESCE(u.DisplayName,u.Email,'Panel member') PanelUserName
FROM recruitment_interviews i JOIN recruitment_interview_panel_members pm ON pm.InterviewId=i.Id JOIN authusers u ON u.Id=pm.PanelUserId
WHERE pm.PanelUserId IN @PanelUserIds AND i.Id<>@InterviewId AND i.Status IN ('Scheduled','Rescheduled')
AND i.ScheduledStart<@ScheduledEnd AND i.ScheduledEnd>@ScheduledStart ORDER BY i.ScheduledStart LIMIT 1", new { PanelUserIds = panelUserIds, InterviewId = request.Id, request.ScheduledStart, request.ScheduledEnd });
            if (conflict is not null) return (null, $"{conflict.PanelUserName} is already assigned to {conflict.RoundCode} from {conflict.ScheduledStart:g} to {conflict.ScheduledEnd:g}.");
        }
        long id;
        if (request.Id <= 0)
        {
            var attempt = isPipelineManaged ? await db.ExecuteScalarAsync<int>("SELECT COALESCE(MAX(AttemptNumber),0)+1 FROM recruitment_interviews WHERE ApplicationId=@ApplicationId AND RoundConfigurationId=@RoundConfigurationId", new { ApplicationId = request.ApplicationId, RoundConfigurationId = pipeline!.RoundConfigurationId }) : 1;
            id = await db.ExecuteScalarAsync<long>(@"INSERT INTO recruitment_interviews (ApplicationId,RoundCode,InterviewType,ScheduledStart,ScheduledEnd,Mode,LocationOrLink,Status,Result,OverallFeedback,OverallScore,CreatedByUserId,PipelineStageInstanceId,RoundConfigurationId,TimeZoneId,AttemptNumber,RescheduleCount) VALUES (@ApplicationId,@RoundCode,@InterviewType,@ScheduledStart,@ScheduledEnd,@Mode,@LocationOrLink,@Status,@Result,@OverallFeedback,0,@UserId,@PipelineStageInstanceId,@RoundConfigurationId,@TimeZoneId,@AttemptNumber,0);SELECT LAST_INSERT_ID();", new { request.ApplicationId, request.RoundCode, request.InterviewType, request.ScheduledStart, request.ScheduledEnd, request.Mode, request.LocationOrLink, request.Status, request.Result, request.OverallFeedback, UserId = user.Id, PipelineStageInstanceId = pipeline?.PipelineStageInstanceId, RoundConfigurationId = pipeline?.RoundConfigurationId, TimeZoneId = string.IsNullOrWhiteSpace(request.TimeZoneId) ? "Asia/Kolkata" : request.TimeZoneId, AttemptNumber = attempt });
        }
        else
        {
            var existing = await db.QueryFirstOrDefaultAsync<ExistingInterviewRow>(@"SELECT i.ApplicationId,i.Status,i.ScheduledStart,i.ScheduledEnd,i.RescheduleCount,i.PipelineStageInstanceId,i.RoundConfigurationId,i.TimeZoneId,i.AttemptNumber FROM recruitment_interviews i JOIN recruitment_candidate_applications a ON a.Id=i.ApplicationId WHERE i.Id=@Id AND (@ClientId IS NULL OR a.ClientId=@ClientId)", new { request.Id, ClientId = user.ClientId });
            if (existing is null) return (null, "Interview was not found.");
            if (existing.ApplicationId != request.ApplicationId) return (null, "An interview cannot be moved to another application.");
            var scheduleChanged = existing.ScheduledStart != request.ScheduledStart || existing.ScheduledEnd != request.ScheduledEnd;
            if (isPipelineManaged && scheduleChanged && !pipeline!.AllowReschedule) return (null, "Rescheduling is disabled for this interview round.");
            var feedbackPanelUserIds = (await db.QueryAsync<int>("SELECT PanelUserId FROM recruitment_interview_feedback WHERE InterviewId=@Id", new { request.Id })).ToArray();
            if (feedbackPanelUserIds.Except(panelUserIds).Any()) return (null, "A panel member with submitted feedback cannot be removed from the interview.");
            var feedbackMustBeComplete = !isPipelineManaged || pipeline!.FeedbackRequired;
            if (request.Status == "Completed" && feedbackMustBeComplete && panelUserIds.Except(feedbackPanelUserIds).Any()) return (null, "Collect feedback from every assigned panel member before completing the interview.");
            if (request.Status == "Completed" && isPipelineManaged && pipeline!.FeedbackRequired)
            {
                var competencyCount = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_interview_stage_competencies WHERE InterviewStageConfigurationId=@Id", new { Id = pipeline.RoundConfigurationId });
                if (competencyCount > 0)
                {
                    var completeFeedback = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM recruitment_interview_feedback f WHERE f.InterviewId=@InterviewId AND (SELECT COUNT(*) FROM recruitment_interview_feedback_competency_scores s WHERE s.InterviewFeedbackId=f.Id)=@CompetencyCount", new { InterviewId = request.Id, CompetencyCount = competencyCount });
                    if (completeFeedback < panelUserIds.Length) return (null, "Every panel member must score all configured competencies before completing this round.");
                }
            }
            id = request.Id;
            await db.ExecuteAsync(@"UPDATE recruitment_interviews SET RoundCode=@RoundCode,InterviewType=@InterviewType,ScheduledStart=@ScheduledStart,ScheduledEnd=@ScheduledEnd,Mode=@Mode,LocationOrLink=@LocationOrLink,Status=@Status,Result=@Result,OverallFeedback=@OverallFeedback,TimeZoneId=@TimeZoneId,RescheduleCount=@RescheduleCount,UpdatedAt=UTC_TIMESTAMP() WHERE Id=@Id", new { request.Id, request.RoundCode, request.InterviewType, request.ScheduledStart, request.ScheduledEnd, request.Mode, request.LocationOrLink, request.Status, request.Result, request.OverallFeedback, TimeZoneId = string.IsNullOrWhiteSpace(request.TimeZoneId) ? existing.TimeZoneId : request.TimeZoneId.Trim(), RescheduleCount = existing.RescheduleCount + (scheduleChanged ? 1 : 0) });
        }
        await db.ExecuteAsync("DELETE FROM recruitment_interview_panel_members WHERE InterviewId=@Id", new { Id = id });
        foreach (var panelUserId in panelUserIds)
            await db.ExecuteAsync("INSERT INTO recruitment_interview_panel_members (InterviewId,PanelUserId,PanelRole) VALUES (@InterviewId,@PanelUserId,'Panelist')", new { InterviewId = id, PanelUserId = panelUserId });
        var stage = request.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase)
            ? "Interview Completed"
            : (request.Status.Equals("Scheduled", StringComparison.OrdinalIgnoreCase) || request.Status.Equals("Rescheduled", StringComparison.OrdinalIgnoreCase)) ? "Interview Scheduled" : "";
        if (!isPipelineManaged && !string.IsNullOrWhiteSpace(stage) && !string.Equals(applicationStage, stage, StringComparison.OrdinalIgnoreCase))
        {
            await db.ExecuteAsync("UPDATE recruitment_candidate_applications SET CurrentStage=@Stage,CurrentStatus=@Stage,LastStageChangedAt=UTC_TIMESTAMP(),UpdatedAt=UTC_TIMESTAMP() WHERE Id=@Id", new { Id = application.Id, Stage = stage });
            await db.ExecuteAsync("INSERT INTO recruitment_application_stage_history (ApplicationId,FromStage,ToStage,Reason,ChangedByUserId) VALUES (@Id,@From,@To,@Reason,@UserId)", new { Id = application.Id, From = applicationStage, To = stage, Reason = $"{request.RoundCode} interview {request.Status}", UserId = user.Id });
        }
        if (isPipelineManaged && pipeline!.PipelineStageInstanceId is > 0)
            await db.ExecuteAsync("INSERT INTO recruitment_stage_events (StageInstanceId,EventType,EventTitle,EventDetails,ActorUserId) VALUES (@StageInstanceId,@EventType,@EventTitle,@Details,@UserId)", new { StageInstanceId = pipeline.PipelineStageInstanceId, EventType = request.Status == "Completed" ? "InterviewCompleted" : "InterviewScheduled", EventTitle = $"{request.RoundCode} {request.Status}", Details = $"{request.InterviewType} / {request.ScheduledStart:g} - {request.ScheduledEnd:g} / {request.TimeZoneId}", UserId = user.Id });
        var activityTitle = string.IsNullOrWhiteSpace(stage) ? $"Interview {request.Status}" : stage;
        await AddPositionTimelineAsync(db, application.PositionId, "Interview", $"{activityTitle}: {application.CandidateName}", $"{request.RoundCode} / {request.InterviewType}", user.Id);
        await WriteActivityAsync(db, application.ClientId, application.CandidateId, null, "RECRUITMENT", "INTERVIEW_UPDATED", activityTitle, $"{application.PositionTitle}: {request.RoundCode} / {request.Result}", "RecruitmentInterview", id.ToString(), user);
        await RefreshPositionCountersAsync(db, application.PositionId);
        return ((await InterviewRowsAsync(db, user, null, [application.Id])).FirstOrDefault(row => row.Id == id), "");
    }

    public async Task<IEnumerable<RecruitmentInterviewFeedback>> GetInterviewFeedbackAsync(long interviewId, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        var interview = (await InterviewRowsAsync(db, user, null, null)).FirstOrDefault(row => row.Id == interviewId);
        if (interview is null) return [];
        return await InterviewFeedbackRowsAsync(db, interviewId);
    }

    public async Task<(RecruitmentInterviewFeedback? Row, string Error)> SaveInterviewFeedbackAsync(long interviewId, SaveRecruitmentInterviewFeedback request, AuthUser user)
    {
        request.CompetencyScores ??= [];
        request.Recommendation = (request.Recommendation ?? "").Trim();
        request.CompetencyScoresJson = string.IsNullOrWhiteSpace(request.CompetencyScoresJson) ? "{}" : request.CompetencyScoresJson;
        request.Comments = (request.Comments ?? "").Trim();
        if (request.PanelUserId <= 0) return (null, "Panel member is required.");
        if (request.OverallScore is < 0 or > 100) return (null, "Interview score must be between 0 and 100.");
        if (!InterviewRecommendations.Contains(request.Recommendation, StringComparer.OrdinalIgnoreCase)) return (null, "Select a valid interview recommendation.");
        request.Recommendation = InterviewRecommendations.First(value => value.Equals(request.Recommendation, StringComparison.OrdinalIgnoreCase));
        await using var db = Db();
        await db.OpenAsync();
        var interview = (await InterviewRowsAsync(db, user, null, null)).FirstOrDefault(row => row.Id == interviewId);
        if (interview is null) return (null, "Interview was not found.");
        if ((interview.Status ?? "") is "Cancelled" or "No Show") return (null, $"Feedback cannot be submitted for an interview marked {interview.Status}.");
        var application = await ApplicationByIdAsync(db, interview.ApplicationId, user);
        if (application is null) return (null, "Interview application was not found.");
        var isPanelMember = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_interview_panel_members WHERE InterviewId=@InterviewId AND PanelUserId=@PanelUserId", new { InterviewId = interviewId, request.PanelUserId });
        if (isPanelMember == 0) return (null, "Feedback can only be recorded for an assigned panel member.");
        var configured = interview.RoundConfigurationId is > 0 ? (await InterviewCompetenciesAsync(db, interview.RoundConfigurationId.Value)).ToList() : [];
        if (configured.Count > 0 && request.CompetencyScores.Count == 0 && !string.IsNullOrWhiteSpace(request.CompetencyScoresJson) && request.CompetencyScoresJson.Trim() != "{}")
            request.CompetencyScores = LegacyCompetencyScores(request.CompetencyScoresJson, configured);
        var scoreSource = "LegacyOverall";
        if (configured.Count > 0)
        {
            var duplicate = request.CompetencyScores.GroupBy(row => row.InterviewStageCompetencyId).FirstOrDefault(group => group.Count() > 1);
            if (duplicate is not null) return (null, "Each configured competency can be scored only once.");
            var configuredIds = configured.Select(row => row.Id).ToHashSet();
            if (request.CompetencyScores.Any(row => !configuredIds.Contains(row.InterviewStageCompetencyId))) return (null, "One or more competency scores do not belong to this interview round.");
            if (interview.FeedbackRequired && configured.Any(row => request.CompetencyScores.All(score => score.InterviewStageCompetencyId != row.Id))) return (null, "Score every configured competency before submitting feedback.");
            if (request.CompetencyScores.Any(row => row.Score is < 0 or > 100)) return (null, "Competency scores must be between 0 and 100.");
            var selected = configured.Join(request.CompetencyScores, config => config.Id, score => score.InterviewStageCompetencyId, (config, score) => new { Config = config, Score = score }).ToList();
            if (selected.Count == 0) return (null, "At least one configured competency score is required.");
            var totalWeight = selected.Sum(row => row.Config.WeightPercent);
            request.OverallScore = totalWeight > 0
                ? Math.Round(selected.Sum(row => row.Score.Score * row.Config.WeightPercent) / totalWeight, 2)
                : Math.Round(selected.Average(row => row.Score.Score), 2);
            scoreSource = "ConfiguredCompetencies";
        }
        var legacyJson = scoreSource == "LegacyOverall" ? ValidJson(request.CompetencyScoresJson, "{}") : "{}";
        await using var transaction = await db.BeginTransactionAsync();
        var id = await db.ExecuteScalarAsync<long>(@"INSERT INTO recruitment_interview_feedback (InterviewId,PanelUserId,OverallScore,Recommendation,CompetencyScoresJson,WeightedScore,ScoreSource,Comments,SubmittedAt) VALUES (@InterviewId,@PanelUserId,@OverallScore,@Recommendation,@CompetencyScoresJson,@WeightedScore,@ScoreSource,@Comments,UTC_TIMESTAMP()) ON DUPLICATE KEY UPDATE Id=LAST_INSERT_ID(Id),OverallScore=VALUES(OverallScore),Recommendation=VALUES(Recommendation),CompetencyScoresJson=VALUES(CompetencyScoresJson),WeightedScore=VALUES(WeightedScore),ScoreSource=VALUES(ScoreSource),Comments=VALUES(Comments),SubmittedAt=UTC_TIMESTAMP();SELECT LAST_INSERT_ID();", new { InterviewId = interviewId, request.PanelUserId, request.OverallScore, request.Recommendation, CompetencyScoresJson = legacyJson, WeightedScore = request.OverallScore, ScoreSource = scoreSource, Comments = (request.Comments ?? "").Trim() }, transaction);
        await db.ExecuteAsync("DELETE FROM recruitment_interview_feedback_competency_scores WHERE InterviewFeedbackId=@Id", new { Id = id }, transaction);
        foreach (var score in request.CompetencyScores)
        {
            var config = configured.First(row => row.Id == score.InterviewStageCompetencyId);
            await db.ExecuteAsync(@"INSERT INTO recruitment_interview_feedback_competency_scores (InterviewFeedbackId,InterviewStageCompetencyId,CompetencyId,CompetencyCode,CompetencyName,WeightPercent,MinimumScore,Score,WeightedScore,Comments) VALUES (@InterviewFeedbackId,@InterviewStageCompetencyId,@CompetencyId,@CompetencyCode,@CompetencyName,@WeightPercent,@MinimumScore,@Score,@WeightedScore,@Comments)", new { InterviewFeedbackId = id, score.InterviewStageCompetencyId, config.CompetencyId, config.CompetencyCode, config.CompetencyName, config.WeightPercent, config.MinimumScore, score.Score, WeightedScore = Math.Round(score.Score * config.WeightPercent / 100m, 2), Comments = (score.Comments ?? "").Trim() }, transaction);
        }
        await db.ExecuteAsync("UPDATE recruitment_interview_panel_members SET AttendanceStatus='Attended' WHERE InterviewId=@InterviewId AND PanelUserId=@PanelUserId; UPDATE recruitment_interviews SET OverallScore=COALESCE((SELECT ROUND(AVG(OverallScore),2) FROM recruitment_interview_feedback WHERE InterviewId=@InterviewId),0),UpdatedAt=UTC_TIMESTAMP() WHERE Id=@InterviewId;", new { InterviewId = interviewId, request.PanelUserId }, transaction);
        await transaction.CommitAsync();
        await WriteRecruitmentAuditAsync(db, "RecruitmentInterviewFeedback", id, "Submitted", user.Id, request);
        await WriteActivityAsync(db, application.ClientId, application.CandidateId, null, "RECRUITMENT", "INTERVIEW_FEEDBACK_SUBMITTED", $"Interview feedback: {request.Recommendation}", $"{interview.RoundCode} / {request.OverallScore:0.##}/100", "RecruitmentInterview", interviewId.ToString(CultureInfo.InvariantCulture), user);
        var saved = (await InterviewFeedbackRowsAsync(db, interviewId)).FirstOrDefault(row => row.Id == id);
        if (saved is null) return (null, "Interview feedback was saved but could not be reloaded.");
        return (saved, "");
    }

    public async Task<IEnumerable<RecruitmentOffer>> GetOffersAsync(AuthUser user, long? applicationId = null)
    {
        await using var db = Db();
        await db.OpenAsync();
        return await OfferRowsAsync(db, user, applicationId, null);
    }

    public async Task<(RecruitmentOffer? Row, string Error)> GenerateOfferLetterAsync(
        long offerId,
        AuthUser user,
        string ipAddress,
        string userAgent,
        CancellationToken cancellationToken)
    {
        await using var db = Db();
        await db.OpenAsync(cancellationToken);
        var context = await db.QueryFirstOrDefaultAsync<OfferLetterContext>(@"SELECT o.*,a.CandidateId,
c.FirstName CandidateFirstName,c.LastName CandidateLastName,CONCAT(c.FirstName,' ',c.LastName) CandidateName,
p.PositionTitle,COALESCE(client.Name,'') ClientName,
t.Id TemplateId,t.ClientId TemplateClientId,t.TemplateType,t.SubjectTemplate,t.BodyTemplate,t.IsActive TemplateIsActive
FROM recruitment_offers o
JOIN recruitment_candidate_applications a ON a.Id=o.ApplicationId
JOIN recruitment_candidates c ON c.Id=a.CandidateId
JOIN recruitment_open_positions p ON p.Id=a.PositionId
LEFT JOIN clients client ON client.Id=o.ClientId
LEFT JOIN recruitment_templates t ON t.Id=o.OfferTemplateId
WHERE o.Id=@OfferId AND (@ClientId IS NULL OR o.ClientId=@ClientId)", new { OfferId = offerId, user.ClientId });
        if (context is null) return (null, "Offer was not found.");
        if (!string.Equals(context.Status, "Draft", StringComparison.OrdinalIgnoreCase))
            return (null, "Generate or regenerate the offer letter while the offer is still in Draft status.");
        if (context.TemplateId is null or <= 0 || !context.TemplateIsActive
            || !(context.TemplateType ?? "").Contains("offer", StringComparison.OrdinalIgnoreCase)
            || (context.TemplateClientId != 0 && context.TemplateClientId != context.ClientId))
            return (null, "Select an active Offer Letter template in the current pipeline Offer stage, then save the draft again.");

        var culture = CultureInfo.GetCultureInfo("en-IN");
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["offerNumber"] = context.OfferNumber,
            ["candidateName"] = context.CandidateName.Trim(),
            ["candidateFirstName"] = context.CandidateFirstName,
            ["candidateLastName"] = context.CandidateLastName,
            ["positionTitle"] = context.PositionTitle,
            ["clientName"] = context.ClientName,
            ["companyName"] = context.ClientName,
            ["currency"] = context.Currency,
            ["offeredCtc"] = context.OfferedCtc.ToString("0.##", CultureInfo.InvariantCulture),
            ["formattedCtc"] = context.OfferedCtc.ToString("N2", culture),
            ["proposedJoiningDate"] = context.ProposedJoiningDate.ToString("dd MMMM yyyy", culture),
            ["joiningDate"] = context.ProposedJoiningDate.ToString("dd MMMM yyyy", culture),
            ["expiryDate"] = context.ExpiryDate?.ToString("dd MMMM yyyy", culture) ?? "",
            ["offerDate"] = DateTime.Today.ToString("dd MMMM yyyy", culture),
            ["date"] = DateTime.Today.ToString("dd MMMM yyyy", culture),
            ["remarks"] = context.Remarks ?? ""
        };
        var (bytes, renderError) = templatePdf.Create(context.SubjectTemplate, context.BodyTemplate, values);
        if (bytes is null) return (null, renderError);

        var fieldConfigurationId = await db.ExecuteScalarAsync<long?>(@"SELECT field.id
FROM attachment_field_configurations field
JOIN attachment_attributes attribute ON attribute.id=field.attachment_attribute_id
WHERE field.is_active=TRUE AND attribute.is_active=TRUE
AND attribute.attribute_code='OFFER_LETTER' AND field.module_code='RECRUITMENT' AND field.form_code='PRE_ONBOARDING'
AND field.client_id IN (0,@ClientId)
AND (field.effective_from_utc IS NULL OR field.effective_from_utc<=UTC_TIMESTAMP(6))
AND (field.effective_until_utc IS NULL OR field.effective_until_utc>=UTC_TIMESTAMP(6))
ORDER BY CASE WHEN field.client_id=@ClientId THEN 0 ELSE 1 END,field.display_order,field.id DESC LIMIT 1", new { context.ClientId });
        if (fieldConfigurationId is null or <= 0)
            return (null, "No active global Offer Letter attachment field is configured for this client.");

        await using var source = new MemoryStream(bytes, writable: false);
        var safeOfferNumber = Regex.Replace(context.OfferNumber, @"[^A-Za-z0-9_-]+", "-").Trim('-');
        if (safeOfferNumber.Length == 0) safeOfferNumber = $"offer-{context.Id}";
        var file = new FormFile(source, 0, bytes.Length, "file", $"{safeOfferNumber}.pdf")
        {
            Headers = new HeaderDictionary(),
            ContentType = "application/pdf"
        };
        var upload = await attachments.UploadAsync(new AttachmentUploadMetadata
        {
            FieldConfigurationId = fieldConfigurationId.Value,
            EntityType = "CANDIDATE",
            EntityId = context.CandidateId,
            DocumentNumber = context.OfferNumber,
            IssueDate = DateTime.Today,
            ExpiryDate = context.ExpiryDate
        }, file, user, ipAddress, userAgent, cancellationToken);
        if (upload.Attachment is null) return (null, upload.Error ?? "Offer letter could not be stored.");

        var linked = await db.ExecuteAsync(@"UPDATE recruitment_offers
SET OfferLetterAttachmentPublicId=@PublicId,UpdatedAt=UTC_TIMESTAMP()
WHERE Id=@Id AND Status='Draft'", new { Id = context.Id, PublicId = upload.Attachment.PublicId.ToString() });
        if (linked == 0)
        {
            await attachments.DeleteAsync(upload.Attachment.PublicId, user, ipAddress, userAgent);
            return (null, "Offer status changed while the letter was being generated. Review the offer and retry from Draft status.");
        }
        if (context.OfferLetterAttachmentPublicId.HasValue
            && context.OfferLetterAttachmentPublicId.Value != upload.Attachment.PublicId)
            await attachments.DeleteAsync(context.OfferLetterAttachmentPublicId.Value, user, ipAddress, userAgent);

        await WriteActivityAsync(db, context.ClientId, context.CandidateId, null, "RECRUITMENT", "OFFER_LETTER_GENERATED",
            "Offer letter generated", $"{context.OfferNumber} / {context.TemplateType}", "RecruitmentOffer", context.Id.ToString(CultureInfo.InvariantCulture), user);
        return ((await OfferRowsAsync(db, user, context.ApplicationId, null)).FirstOrDefault(row => row.Id == context.Id), "");
    }

    public async Task<(RecruitmentOffer? Row, string Error)> SaveOfferAsync(SaveRecruitmentOffer request, AuthUser user)
    {
        if (request.ApplicationId <= 0 || request.OfferedCtc <= 0) return (null, "Application and offered CTC are required.");
        if (!OfferStatuses.Contains(request.Status, StringComparer.OrdinalIgnoreCase)) return (null, "Select a valid offer status.");
        if (!Regex.IsMatch(request.Currency?.Trim() ?? "", "^[A-Za-z]{3}$")) return (null, "Currency must be a three-letter code such as INR.");
        request.Currency = (request.Currency ?? "").Trim().ToUpperInvariant();
        if (request.Id <= 0 && !request.Status.Equals("Draft", StringComparison.OrdinalIgnoreCase)) return (null, "A new offer must be saved as Draft before release or approval.");
        await using var db = Db();
        await db.OpenAsync();
        var application = await ApplicationByIdAsync(db, request.ApplicationId, user);
        if (application is null) return (null, "Application was not found.");
        if (application.CurrentStage is "Rejected" or "Withdrawn" or "Joined") return (null, $"An offer cannot be saved for an application in {application.CurrentStage} stage.");
        RecruitmentOffer? existingOffer = null;
        if (request.Id > 0)
        {
            existingOffer = await db.QueryFirstOrDefaultAsync<RecruitmentOffer>("SELECT * FROM recruitment_offers WHERE Id=@Id AND (@ClientId IS NULL OR ClientId=@ClientId)", new { request.Id, ClientId = user.ClientId });
            if (existingOffer is null) return (null, "Offer was not found.");
            if (existingOffer.ApplicationId != request.ApplicationId) return (null, "An offer cannot be moved to another application.");
            if (!existingOffer.Status.Equals("Draft", StringComparison.OrdinalIgnoreCase)) return (null, "Only a Draft offer can be edited. Use the approval or offer-status action for further processing.");
        }
        var (pipelineOfferPolicy, policyError) = await PipelineOfferPolicyAsync(db, request.ApplicationId, request.OfferedCtc, request.Currency, request.Id > 0 ? request.Id : null, existingOffer?.StageOfferConfigurationId, existingOffer?.PipelineStageInstanceId);
        if (!string.IsNullOrWhiteSpace(policyError)) return (null, policyError);
        if (existingOffer?.StageOfferConfigurationId is > 0 && pipelineOfferPolicy is null)
            return (null, "The pipeline offer configuration used by this offer is no longer available. Restore the published pipeline configuration before editing it.");
        if (request.ProposedJoiningDate.Date < DateTime.Today) return (null, "Proposed joining date cannot be in the past.");
        if (pipelineOfferPolicy is not null)
            request.ExpiryDate = CandidateResponseExpiry(request.ProposedJoiningDate, pipelineOfferPolicy.CandidateResponseValidityDays);
        else
        {
            if (request.ExpiryDate.HasValue && request.ExpiryDate.Value.Date < DateTime.Today) return (null, "Offer expiry date cannot be in the past.");
            if (request.ExpiryDate.HasValue && request.ExpiryDate.Value.Date > request.ProposedJoiningDate.Date) return (null, "Offer expiry date cannot be after the proposed joining date.");
        }
        if (request.OfferLetterAttachmentPublicId.HasValue)
        {
            var validOfferLetter = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM entity_attachments ea JOIN attachment_attributes aa ON aa.id=ea.attachment_attribute_id WHERE ea.public_id=@PublicId AND ea.entity_type='CANDIDATE' AND ea.entity_id=@CandidateId AND ea.is_current=TRUE AND ea.is_deleted=FALSE AND aa.attribute_code='OFFER_LETTER'", new { PublicId = request.OfferLetterAttachmentPublicId.Value.ToString(), application.CandidateId });
            if (validOfferLetter == 0) return (null, "Select a current global Offer Letter document linked to this candidate.");
        }
        long id;
        if (request.Id <= 0)
        {
            var activeOffer = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_offers WHERE ApplicationId=@ApplicationId AND Status NOT IN ('Rejected','Expired','Withdrawn')", new { request.ApplicationId });
            if (activeOffer > 0) return (null, "An active offer already exists for this application. Complete or withdraw it before creating another offer.");
            var number = await NextNumberAsync(db, application.ClientId, "OFF", "OFF");
            id = await db.ExecuteScalarAsync<long>(@"INSERT INTO recruitment_offers
(OfferNumber,ApplicationId,ClientId,OfferedCtc,Currency,ProposedJoiningDate,ExpiryDate,Status,
 PipelineStageInstanceId,StageOfferConfigurationId,OfferTemplateId,BudgetBasis,ApprovedBudgetAmount,BudgetExposureAmount,
 MaximumVariancePercent,VariancePercent,VarianceExceeded,CandidateResponseValidityDays,
 OfferLetterAttachmentPublicId,Remarks,CreatedByUserId)
VALUES (@Number,@ApplicationId,@ClientId,@OfferedCtc,@Currency,@ProposedJoiningDate,@ExpiryDate,'Draft',
 @PipelineStageInstanceId,@StageOfferConfigurationId,@OfferTemplateId,@BudgetBasis,@ApprovedBudgetAmount,@BudgetExposureAmount,
 @MaximumVariancePercent,@VariancePercent,@VarianceExceeded,@CandidateResponseValidityDays,
 @Attachment,@Remarks,@UserId);SELECT LAST_INSERT_ID();", new
            {
                Number = number,
                request.ApplicationId,
                application.ClientId,
                request.OfferedCtc,
                request.Currency,
                request.ProposedJoiningDate,
                request.ExpiryDate,
                PipelineStageInstanceId = pipelineOfferPolicy?.PipelineStageInstanceId,
                StageOfferConfigurationId = pipelineOfferPolicy?.StageOfferConfigurationId,
                OfferTemplateId = pipelineOfferPolicy?.OfferTemplateId,
                BudgetBasis = pipelineOfferPolicy?.BudgetBasis ?? "",
                ApprovedBudgetAmount = pipelineOfferPolicy?.ApprovedBudgetAmount ?? 0,
                BudgetExposureAmount = pipelineOfferPolicy?.BudgetExposureAmount ?? request.OfferedCtc,
                MaximumVariancePercent = pipelineOfferPolicy?.MaximumVariancePercent ?? 0,
                VariancePercent = pipelineOfferPolicy?.VariancePercent ?? 0,
                VarianceExceeded = pipelineOfferPolicy?.VarianceExceeded ?? false,
                CandidateResponseValidityDays = pipelineOfferPolicy?.CandidateResponseValidityDays ?? 0,
                Attachment = request.OfferLetterAttachmentPublicId?.ToString(),
                request.Remarks,
                UserId = user.Id
            });
        }
        else
        {
            id = request.Id;
            await db.ExecuteAsync(@"UPDATE recruitment_offers SET OfferedCtc=@OfferedCtc,Currency=@Currency,
ProposedJoiningDate=@ProposedJoiningDate,ExpiryDate=@ExpiryDate,
PipelineStageInstanceId=@PipelineStageInstanceId,StageOfferConfigurationId=@StageOfferConfigurationId,
OfferTemplateId=@OfferTemplateId,BudgetBasis=@BudgetBasis,ApprovedBudgetAmount=@ApprovedBudgetAmount,
BudgetExposureAmount=@BudgetExposureAmount,
MaximumVariancePercent=@MaximumVariancePercent,VariancePercent=@VariancePercent,VarianceExceeded=@VarianceExceeded,
CandidateResponseValidityDays=@CandidateResponseValidityDays,AppliedApprovalWorkflowId=NULL,ApprovalPolicy='',
OfferLetterAttachmentPublicId=@Attachment,Remarks=@Remarks,UpdatedAt=UTC_TIMESTAMP() WHERE Id=@Id", new
            {
                request.Id,
                request.OfferedCtc,
                request.Currency,
                request.ProposedJoiningDate,
                request.ExpiryDate,
                PipelineStageInstanceId = pipelineOfferPolicy?.PipelineStageInstanceId,
                StageOfferConfigurationId = pipelineOfferPolicy?.StageOfferConfigurationId,
                OfferTemplateId = pipelineOfferPolicy?.OfferTemplateId,
                BudgetBasis = pipelineOfferPolicy?.BudgetBasis ?? "",
                ApprovedBudgetAmount = pipelineOfferPolicy?.ApprovedBudgetAmount ?? 0,
                BudgetExposureAmount = pipelineOfferPolicy?.BudgetExposureAmount ?? request.OfferedCtc,
                MaximumVariancePercent = pipelineOfferPolicy?.MaximumVariancePercent ?? 0,
                VariancePercent = pipelineOfferPolicy?.VariancePercent ?? 0,
                VarianceExceeded = pipelineOfferPolicy?.VarianceExceeded ?? false,
                CandidateResponseValidityDays = pipelineOfferPolicy?.CandidateResponseValidityDays ?? 0,
                Attachment = request.OfferLetterAttachmentPublicId?.ToString(),
                request.Remarks
            });
        }
        await AddPositionTimelineAsync(db, application.PositionId, "Offer", $"Offer saved: {application.CandidateName}", request.Remarks, user.Id);
        var offerSummary = pipelineOfferPolicy is null
            ? $"{application.PositionTitle} / {request.Currency} {request.OfferedCtc:0.##}"
            : $"{application.PositionTitle} / {request.Currency} {request.OfferedCtc:0.##}; {pipelineOfferPolicy.BudgetBasis} budget {pipelineOfferPolicy.ApprovedBudgetAmount:0.##}; exposure {pipelineOfferPolicy.BudgetExposureAmount:0.##}; variance {pipelineOfferPolicy.VariancePercent:0.##}%";
        await WriteActivityAsync(db, application.ClientId, application.CandidateId, null, "RECRUITMENT", "OFFER_UPDATED", "Offer saved", offerSummary, "RecruitmentOffer", id.ToString(), user);
        await RefreshPositionCountersAsync(db, application.PositionId);
        return ((await OfferRowsAsync(db, user, application.Id, null)).FirstOrDefault(row => row.Id == id), "");
    }

    public async Task<(RecruitmentOffer? Row, string Error)> UpdateOfferStatusAsync(long id, string status, string remarks, AuthUser user)
    {
        status = status?.Trim() ?? "";
        remarks = remarks?.Trim() ?? "";
        if (!OfferStatuses.Contains(status, StringComparer.OrdinalIgnoreCase)) return (null, "Select a valid offer status.");
        status = OfferStatuses.First(value => value.Equals(status, StringComparison.OrdinalIgnoreCase));
        if (status is ("Rejected" or "Withdrawn" or "Negotiation") && string.IsNullOrWhiteSpace(remarks)) return (null, $"Reason is required when an offer is marked {status}.");
        await using var db = Db();
        await db.OpenAsync();
        var offer = (await OfferRowsAsync(db, user, null, null)).FirstOrDefault(row => row.Id == id);
        if (offer is null) return (null, "Offer was not found.");
        if (status is "Approved" or "Pending Approval") return (null, "Offer approval status can only be changed by the configured workflow.");
        if (status.Equals("Pending Candidate", StringComparison.OrdinalIgnoreCase) && offer.Status is not ("Draft" or "Approved")) return (null, "Only a draft or approved offer can be released.");
        if (status.Equals("Pending Candidate", StringComparison.OrdinalIgnoreCase) && !offer.OfferLetterAttachmentPublicId.HasValue) return (null, "Link the current global Offer Letter document before releasing the offer.");
        if ((status.Equals("Accepted", StringComparison.OrdinalIgnoreCase) || status.Equals("Rejected", StringComparison.OrdinalIgnoreCase))
            && offer.Status is not ("Pending Candidate" or "Released" or "Negotiation")) return (null, "The candidate can respond only after the offer is released.");
        if (status.Equals("Negotiation", StringComparison.OrdinalIgnoreCase) && offer.Status is not ("Pending Candidate" or "Released")) return (null, "Negotiation can start only after the offer is released.");
        if (status.Equals("Expired", StringComparison.OrdinalIgnoreCase) && offer.Status is not ("Pending Candidate" or "Released" or "Negotiation")) return (null, "Only a released offer can expire.");
        if (status.Equals("Withdrawn", StringComparison.OrdinalIgnoreCase) && offer.Status.Equals("Pending Approval", StringComparison.OrdinalIgnoreCase)) return (null, "Complete or send back the pending approval workflow before withdrawing this offer.");
        if (status.Equals("Withdrawn", StringComparison.OrdinalIgnoreCase) && offer.Status is ("Accepted" or "Rejected" or "Expired" or "Withdrawn")) return (null, "This completed offer cannot be withdrawn.");
        if (status.Equals("Draft", StringComparison.OrdinalIgnoreCase) || status.Equals("Released", StringComparison.OrdinalIgnoreCase)) return (null, "Use the supported offer actions instead of directly assigning this status.");
        PipelineOfferPolicyContext? pipelineOfferPolicy = null;
        if (status.Equals("Pending Candidate", StringComparison.OrdinalIgnoreCase))
        {
            var (resolvedPolicy, policyError) = await PipelineOfferPolicyAsync(db, offer.ApplicationId, offer.OfferedCtc, offer.Currency, offer.Id, offer.StageOfferConfigurationId, offer.PipelineStageInstanceId);
            if (!string.IsNullOrWhiteSpace(policyError)) return (null, policyError);
            if (offer.StageOfferConfigurationId.HasValue && resolvedPolicy is null)
                return (null, "The pipeline offer configuration used by this offer is no longer available. Restore the published pipeline configuration before release.");
            pipelineOfferPolicy = resolvedPolicy;
            if (pipelineOfferPolicy is null && offer.ExpiryDate.HasValue && offer.ExpiryDate.Value.Date < DateTime.Today)
                return (null, "This offer has expired. Update its expiry date before release.");

            if (pipelineOfferPolicy is not null)
            {
                await db.ExecuteAsync(@"UPDATE recruitment_offers SET
PipelineStageInstanceId=@PipelineStageInstanceId,StageOfferConfigurationId=@StageOfferConfigurationId,
OfferTemplateId=@OfferTemplateId,BudgetBasis=@BudgetBasis,ApprovedBudgetAmount=@ApprovedBudgetAmount,
BudgetExposureAmount=@BudgetExposureAmount,
MaximumVariancePercent=@MaximumVariancePercent,VariancePercent=@VariancePercent,VarianceExceeded=@VarianceExceeded,
CandidateResponseValidityDays=@CandidateResponseValidityDays,UpdatedAt=UTC_TIMESTAMP() WHERE Id=@Id", new
                {
                    Id = id,
                    pipelineOfferPolicy.PipelineStageInstanceId,
                    pipelineOfferPolicy.StageOfferConfigurationId,
                    pipelineOfferPolicy.OfferTemplateId,
                    pipelineOfferPolicy.BudgetBasis,
                    pipelineOfferPolicy.ApprovedBudgetAmount,
                    pipelineOfferPolicy.BudgetExposureAmount,
                    pipelineOfferPolicy.MaximumVariancePercent,
                    pipelineOfferPolicy.VariancePercent,
                    pipelineOfferPolicy.VarianceExceeded,
                    pipelineOfferPolicy.CandidateResponseValidityDays
                });
                offer = (await OfferRowsAsync(db, user, offer.ApplicationId, null)).First(row => row.Id == id);
            }

            if (!offer.Status.Equals("Approved", StringComparison.OrdinalIgnoreCase))
            {
                long? workflowId = null;
                var approvalPolicy = "";
                if (pipelineOfferPolicy is not null)
                {
                    if (pipelineOfferPolicy.VarianceExceeded && pipelineOfferPolicy.RequireApprovalWhenVarianceExceeded)
                    {
                        workflowId = pipelineOfferPolicy.VarianceApprovalWorkflowId;
                        approvalPolicy = "BudgetVariance";
                        if (workflowId is null or <= 0)
                            return (null, "The offer exceeds the configured budget variance but no variance approval workflow is available.");
                    }
                    else if (pipelineOfferPolicy.ApprovalWorkflowId is > 0)
                    {
                        workflowId = pipelineOfferPolicy.ApprovalWorkflowId;
                        approvalPolicy = "Standard";
                    }
                }
                else
                {
                    var features = await FeatureSettingsAsync(db, offer.ClientId);
                    if (features.EnableOfferApproval)
                    {
                        workflowId = await db.ExecuteScalarAsync<int?>("SELECT WorkflowId FROM recruitment_approval_mappings WHERE ClientId=@ClientId AND ProcessCode='OFFER_APPROVAL' AND IsActive=TRUE AND WorkflowId>0 LIMIT 1", new { offer.ClientId })
                            ?? await workflows.GetDefaultIdAsync("RecruitmentOffer", offer.ClientId);
                        approvalPolicy = "GlobalFallback";
                        if (!workflowId.HasValue) return (null, "Offer approval is enabled but no active OFFER_APPROVAL workflow is mapped for this client.");
                    }
                }

                if (workflowId is > 0)
                {
                    if (workflowId > int.MaxValue) return (null, "The configured offer approval workflow identifier is invalid.");
                    var workflowRequestorUserId = await ResolveOfferWorkflowRequestorAsync(db, offer.ApplicationId, user.Id);
                    if (workflowRequestorUserId <= 0)
                        return (null, "Offer approval could not start because no active hiring requestor, recruiter, or fallback user is available.");
                    var instance = await workflows.StartAsync(new StartWorkflowRequest
                    {
                        WorkflowId = checked((int)workflowId.Value),
                        ResourceType = "RecruitmentOffer",
                        ResourceId = id.ToString(CultureInfo.InvariantCulture),
                        PayloadJson = JsonSerializer.Serialize(offer)
                    }, workflowRequestorUserId);
                    if (instance is null)
                        return (null, approvalPolicy == "BudgetVariance"
                            ? "Variance approval could not start. Check the configured workflow stages and approvers."
                            : "Offer approval could not start. Check workflow stages and approver setup.");
                    await db.ExecuteAsync(@"UPDATE recruitment_offers SET Status='Pending Approval',WorkflowInstanceId=@WorkflowInstanceId,
AppliedApprovalWorkflowId=@WorkflowId,ApprovalPolicy=@ApprovalPolicy,Remarks=@Remarks,UpdatedAt=UTC_TIMESTAMP() WHERE Id=@Id",
                        new { Id = id, WorkflowInstanceId = instance.Id, WorkflowId = workflowId, ApprovalPolicy = approvalPolicy, Remarks = remarks });
                    await WriteActivityAsync(db, offer.ClientId, (await ApplicationByIdAsync(db, offer.ApplicationId, user))?.CandidateId, null,
                        "RECRUITMENT", "OFFER_APPROVAL_STARTED",
                        approvalPolicy == "BudgetVariance" ? "Offer variance approval started" : "Offer approval started",
                        remarks, "RecruitmentOffer", id.ToString(CultureInfo.InvariantCulture), user);
                    return ((await OfferRowsAsync(db, user, offer.ApplicationId, null)).FirstOrDefault(row => row.Id == id), "");
                }
            }
        }
        var responseExpiry = status.Equals("Pending Candidate", StringComparison.OrdinalIgnoreCase) && pipelineOfferPolicy is not null
            ? CandidateResponseExpiry(offer.ProposedJoiningDate, pipelineOfferPolicy.CandidateResponseValidityDays)
            : offer.ExpiryDate;
        await db.ExecuteAsync(@"UPDATE recruitment_offers SET Status=@Status,ExpiryDate=@ExpiryDate,
ApprovalPolicy=CASE WHEN @Status='Pending Candidate' AND ApprovalPolicy='' THEN 'Direct' ELSE ApprovalPolicy END,
Remarks=@Remarks,UpdatedAt=UTC_TIMESTAMP() WHERE Id=@Id", new { Id = id, Status = status, ExpiryDate = responseExpiry, Remarks = remarks });
        var application = await ApplicationByIdAsync(db, offer.ApplicationId, user);
        if (application is not null)
        {
            var stage = status switch
            {
                "Accepted" => "Offer Accepted",
                "Rejected" => "Rejected",
                "Pending Candidate" => "Offer Released",
                "Negotiation" => "Offer Negotiation",
                "Expired" => "Offer Expired",
                "Withdrawn" => "Offer Withdrawn",
                _ => ""
            };
            if (!string.IsNullOrWhiteSpace(stage) && !application.CurrentStage.Equals(stage, StringComparison.OrdinalIgnoreCase))
            {
                await db.ExecuteAsync("UPDATE recruitment_candidate_applications SET CurrentStage=@Stage,CurrentStatus=@Stage,LastStageChangedAt=UTC_TIMESTAMP(),UpdatedAt=UTC_TIMESTAMP() WHERE Id=@Id", new { Id = application.Id, Stage = stage });
                await db.ExecuteAsync("INSERT INTO recruitment_application_stage_history (ApplicationId,FromStage,ToStage,Reason,ChangedByUserId) VALUES (@Id,@From,@To,@Reason,@UserId)", new { Id = application.Id, From = application.CurrentStage, To = stage, Reason = $"Offer {status}: {remarks}".Trim(), UserId = user.Id });
            }
            if (stage == "Offer Accepted") await CreateCandidateChecklistSnapshotAsync(db, application);
            await AddPositionTimelineAsync(db, application.PositionId, "Offer Status", $"{application.CandidateName}: {stage}", remarks, user.Id);
            await WriteActivityAsync(db, application.ClientId, application.CandidateId, null, "RECRUITMENT", "OFFER_STATUS_CHANGED", $"Offer {status}", remarks, "RecruitmentOffer", id.ToString(), user);
            await RefreshPositionCountersAsync(db, application.PositionId);
        }
        return ((await OfferRowsAsync(db, user, offer.ApplicationId, null)).FirstOrDefault(row => row.Id == id), "");
    }

    public async Task SyncOfferWorkflowStatusAsync(string resourceId, string workflowStatus, AuthUser actor, long? workflowInstanceId = null)
    {
        if (!long.TryParse(resourceId, CultureInfo.InvariantCulture, out var offerId)) return;
        var offerStatus = workflowStatus switch { "Approved" => "Approved", "Rejected" => "Rejected", "Sent Back" => "Draft", _ => "Pending Approval" };
        await using var db = Db();
        await db.OpenAsync();
        var offer = await db.QueryFirstOrDefaultAsync<RecruitmentOffer>("SELECT * FROM recruitment_offers WHERE Id=@Id", new { Id = offerId });
        if (offer is null) return;
        if (workflowInstanceId.HasValue && offer.WorkflowInstanceId != workflowInstanceId) return;
        await db.ExecuteAsync("UPDATE recruitment_offers SET Status=@Status,UpdatedAt=UTC_TIMESTAMP() WHERE Id=@Id", new { Id = offerId, Status = offerStatus });
        var application = await ApplicationByIdAsync(db, offer.ApplicationId, actor);
        if (application is not null)
            await WriteActivityAsync(db, offer.ClientId, application.CandidateId, null, "RECRUITMENT", "OFFER_APPROVAL_UPDATED", $"Offer approval {workflowStatus}", "", "RecruitmentOffer", offerId.ToString(CultureInfo.InvariantCulture), actor);
    }

    public async Task<(RecruitmentCandidateChecklistItem? Row, string Error)> CompleteChecklistItemAsync(long applicationId, long itemId, Guid? attachmentPublicId, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        var application = await ApplicationByIdAsync(db, applicationId, user);
        if (application is null) return (null, "Application was not found.");
        var item = await db.QueryFirstOrDefaultAsync<RecruitmentCandidateChecklistItem>("SELECT * FROM recruitment_candidate_checklist_items WHERE Id=@ItemId AND ApplicationId=@ApplicationId", new { ItemId = itemId, ApplicationId = applicationId });
        if (item is null) return (null, "Checklist item was not found.");
        if (attachmentPublicId.HasValue)
        {
            var valid = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM entity_attachments WHERE public_id=@PublicId AND entity_type='CANDIDATE' AND entity_id=@CandidateId AND is_current=TRUE AND is_deleted=FALSE AND (@AttributeId IS NULL OR attachment_attribute_id=@AttributeId) AND (@RequiresVerification=FALSE OR verification_status='Verified')", new { PublicId = attachmentPublicId.Value.ToString(), application.CandidateId, AttributeId = item.AttachmentAttributeId, item.RequiresVerification });
            if (valid == 0) return (null, "Selected document is not linked to the candidate through the global document system.");
        }
        if (item.Mandatory && item.AttachmentAttributeId.HasValue && !attachmentPublicId.HasValue) return (null, "A global candidate document is required to complete this item.");
        await db.ExecuteAsync("UPDATE recruitment_candidate_checklist_items SET Status='Completed',AttachmentPublicId=@AttachmentPublicId,CompletedByUserId=@UserId,CompletedAt=UTC_TIMESTAMP() WHERE Id=@Id", new { Id = itemId, AttachmentPublicId = attachmentPublicId?.ToString(), UserId = user.Id });
        await WriteActivityAsync(db, application.ClientId, application.CandidateId, null, "RECRUITMENT", "PREBOARDING_ITEM_COMPLETED", $"Pre-onboarding completed: {item.ChecklistName}", "", "RecruitmentChecklist", itemId.ToString(), user);
        return (await db.QueryFirstAsync<RecruitmentCandidateChecklistItem>("SELECT * FROM recruitment_candidate_checklist_items WHERE Id=@Id", new { Id = itemId }), "");
    }

    public async Task<(Employee? Employee, string Error)> ConvertToEmployeeAsync(long applicationId, ConvertCandidateToEmployeeRequest request, AuthUser user)
    {
        if (string.IsNullOrWhiteSpace(request.EmployeeCode)) return (null, "Employee code is required.");
        if (string.IsNullOrWhiteSpace(request.DateOfJoining)) return (null, "Date of joining is required.");
        if (!DateOnly.TryParse(request.DateOfJoining, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return (null, "Enter a valid date of joining.");
        await using var db = Db();
        await db.OpenAsync();
        var application = await ApplicationByIdAsync(db, applicationId, user);
        if (application is null) return (null, "Application was not found.");
        var candidate = await CandidateByIdAsync(db, application.CandidateId);
        if (candidate is null) return (null, "Candidate was not found.");
        if (candidate.EmployeeId.HasValue)
        {
            var existing = await db.QueryFirstOrDefaultAsync<Employee>("SELECT * FROM employees WHERE Id=@Id", new { Id = candidate.EmployeeId });
            return (existing, existing is null ? "Candidate is linked to a missing employee record." : "");
        }
        var acceptedOffer = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_offers WHERE ApplicationId=@Id AND Status='Accepted'", new { Id = applicationId });
        if (acceptedOffer == 0) return (null, "Accept the candidate offer before converting the profile to an employee.");
        var pendingMandatory = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_candidate_checklist_items WHERE ApplicationId=@Id AND Mandatory=TRUE AND Status<>'Completed'", new { Id = applicationId });
        if (pendingMandatory > 0) return (null, $"Complete {pendingMandatory} mandatory pre-onboarding checklist item(s) before employee creation.");
        var duplicateCode = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employees WHERE ClientId=@ClientId AND EmployeeCode=@EmployeeCode", new { application.ClientId, request.EmployeeCode });
        if (duplicateCode > 0) return (null, "Employee code already exists for this client.");
        var position = await db.QueryFirstAsync<RecruitmentOpenPosition>("SELECT * FROM recruitment_open_positions WHERE Id=@Id", new { Id = application.PositionId });
        var personal = JsonSerializer.Serialize(new EmployeePersonalDetails { Mobile = candidate.Phone, Source = "Recruitment", SourceLocation = candidate.CurrentLocation });
        var employee = new Employee
        {
            ClientId = application.ClientId,
            EmployeeCode = request.EmployeeCode.Trim(),
            FirstName = candidate.FirstName,
            LastName = candidate.LastName,
            Gender = request.Gender,
            DateOfJoining = request.DateOfJoining,
            WorkEmail = string.IsNullOrWhiteSpace(request.WorkEmail) ? candidate.Email : request.WorkEmail.Trim(),
            Department = string.IsNullOrWhiteSpace(request.Department) ? position.Department : request.Department,
            Designation = string.IsNullOrWhiteSpace(request.Designation) ? position.PositionTitle : request.Designation,
            Grade = request.Grade,
            WorkLocationId = request.WorkLocationId,
            ReportingManagerId = request.ReportingManagerId,
            ReportingManagerUserId = request.ReportingManagerUserId,
            PortalAccess = request.PortalAccess,
            SalaryStructureId = request.SalaryStructureId,
            AnnualCtc = request.AnnualCtc > 0 ? request.AnnualCtc : await LatestOfferCtcAsync(db, applicationId),
            SalaryJson = "{}",
            PersonalJson = personal,
            PaymentJson = "{}",
            IsActive = true
        };
        employee.Id = await employees.SaveAsync(employee, user.DisplayName, null, $"Joined from recruitment application {application.ApplicationCode}");
        if (employee.Id <= 0) return (null, "Employee profile could not be created. Recruitment status was not changed.");
        await db.ExecuteAsync("UPDATE recruitment_candidates SET EmployeeId=@EmployeeId,ProfileStatus='Joined',UpdatedAt=UTC_TIMESTAMP() WHERE Id=@CandidateId; UPDATE recruitment_candidate_applications SET CurrentStage='Joined',CurrentStatus='Joined',JoinedEmployeeId=@EmployeeId,LastStageChangedAt=UTC_TIMESTAMP(),UpdatedAt=UTC_TIMESTAMP() WHERE Id=@ApplicationId; UPDATE person_activity_events SET EmployeeId=@EmployeeId WHERE CandidateId=@CandidateId AND EmployeeId IS NULL;", new { EmployeeId = employee.Id, CandidateId = candidate.Id, ApplicationId = applicationId });
        await db.ExecuteAsync("INSERT INTO recruitment_application_stage_history (ApplicationId,FromStage,ToStage,Reason,ChangedByUserId) VALUES (@Id,@From,'Joined','Converted to employee',@UserId)", new { Id = applicationId, From = application.CurrentStage, UserId = user.Id });
        await AddPositionTimelineAsync(db, position.Id, "Joined", $"{candidate.FirstName} {candidate.LastName} joined", employee.EmployeeCode, user.Id);
        await WriteActivityAsync(db, application.ClientId, candidate.Id, employee.Id, "RECRUITMENT", "CANDIDATE_CONVERTED", "Candidate converted to employee", $"{employee.EmployeeCode} - {employee.FirstName} {employee.LastName}", "Employee", employee.Id.ToString(), user);
        await RefreshPositionCountersAsync(db, position.Id);
        return (employee, "");
    }

    public async Task<IEnumerable<PersonActivityEvent>> GetEmployee360Async(int employeeId, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        var clientId = await db.ExecuteScalarAsync<int?>("SELECT ClientId FROM employees WHERE Id=@Id", new { Id = employeeId });
        if (!clientId.HasValue || !CanAccessClient(user, clientId.Value)) return [];
        var selfServiceOnly = user.EmployeeId == employeeId
            && !user.Permissions.Contains("employees.view", StringComparer.OrdinalIgnoreCase)
            && !user.Permissions.Contains("employees.manage", StringComparer.OrdinalIgnoreCase)
            && !user.Permissions.Contains("settings.manage", StringComparer.OrdinalIgnoreCase);
        var events = (await db.QueryAsync<PersonActivityEvent>(@"SELECT p.*,COALESCE(u.DisplayName,u.Email,'System') ActorName FROM person_activity_events p LEFT JOIN authusers u ON u.Id=p.ActorUserId WHERE p.EmployeeId=@EmployeeId OR p.CandidateId IN (SELECT Id FROM recruitment_candidates WHERE EmployeeId=@EmployeeId) ORDER BY p.OccurredAt DESC,p.Id DESC", new { EmployeeId = employeeId })).ToList();
        events.AddRange(await db.QueryAsync<PersonActivityEvent>(@"SELECT id Id,client_id ClientId,NULL CandidateId,employee_id EmployeeId,'ATTENDANCE' ModuleCode,CONCAT('ATTENDANCE_',UPPER(action)) EventType,CONCAT('Attendance ',action) EventTitle,CONCAT(decision,' / ',validation_status,CASE WHEN reason='' THEN '' ELSE CONCAT(' / ',reason) END) EventSummary,'AttendancePunch' ResourceType,CAST(id AS CHAR) ResourceId,NULL ActorUserId,'Employee' ActorName,'Employee' Visibility,FALSE IsSensitive,'{}' MetadataJson,captured_at OccurredAt,created_at CreatedAt FROM employee_attendance_punches WHERE employee_id=@EmployeeId ORDER BY captured_at DESC LIMIT 250", new { EmployeeId = employeeId }));
        events.AddRange(await db.QueryAsync<PersonActivityEvent>(@"SELECT r.Id,r.ClientId,NULL CandidateId,r.EmployeeId,'LEAVE' ModuleCode,'LEAVE_REQUEST' EventType,CONCAT('Leave request - ',COALESCE(lt.Name,lt.Code,'Leave')) EventTitle,CONCAT(DATE_FORMAT(r.FromDate,'%d-%b-%Y'),' to ',DATE_FORMAT(r.ToDate,'%d-%b-%Y'),' / ',r.Status) EventSummary,'LeaveRequest' ResourceType,CAST(r.Id AS CHAR) ResourceId,NULL ActorUserId,'Employee' ActorName,'Employee' Visibility,FALSE IsSensitive,'{}' MetadataJson,r.CreatedAt OccurredAt,r.CreatedAt FROM essleaverequests r LEFT JOIN leave_types lt ON lt.Id=r.LeaveTypeId WHERE r.EmployeeId=@EmployeeId ORDER BY r.CreatedAt DESC LIMIT 250", new { EmployeeId = employeeId }));
        events.AddRange(await db.QueryAsync<PersonActivityEvent>(@"SELECT pe.Id,pe.ClientId,NULL CandidateId,pe.EmployeeId,'PAYROLL' ModuleCode,'PAYSLIP_GENERATED' EventType,CONCAT('Payroll - ',pr.PayPeriod) EventTitle,CONCAT('Net pay ',ROUND(pe.NetPay,2),' / ',pr.Status,' / ',pe.PaymentStatus) EventSummary,'PayRun' ResourceType,CAST(pe.PayRunId AS CHAR) ResourceId,NULL ActorUserId,'System' ActorName,'Employee' Visibility,FALSE IsSensitive,'{}' MetadataJson,pr.UpdatedAt OccurredAt,pr.UpdatedAt CreatedAt FROM payrunemployees pe JOIN payruns pr ON pr.Id=pe.PayRunId WHERE pe.EmployeeId=@EmployeeId AND pe.IsSkipped=FALSE ORDER BY pr.PayPeriod DESC,pr.Id DESC LIMIT 120", new { EmployeeId = employeeId }));
        if (selfServiceOnly)
            return events.Where(row => !row.IsSensitive && row.Visibility.Equals("Employee", StringComparison.OrdinalIgnoreCase)).OrderByDescending(row => row.OccurredAt).Take(1000);
        var audit = await db.QueryAsync<PersonActivityEvent>(@"SELECT Id,@ClientId ClientId,NULL CandidateId,@EmployeeId EmployeeId,'EMPLOYEE' ModuleCode,CONCAT('INFOTYPE_',InfotypeCode) EventType,CONCAT(ActionType,' - ',FieldName) EventTitle,CONCAT(COALESCE(OldValue,''),' -> ',COALESCE(NewValue,'')) EventSummary,'EmployeeAudit' ResourceType,CAST(Id AS CHAR) ResourceId,NULL ActorUserId,ChangedBy ActorName,'HR' Visibility,TRUE IsSensitive,'{}' MetadataJson,ChangedAt OccurredAt,ChangedAt CreatedAt FROM employee_audit_trail WHERE EmployeeId=@EmployeeId", new { ClientId = clientId.Value, EmployeeId = employeeId });
        events.AddRange(audit);
        var documentEvents = await db.QueryAsync<PersonActivityEvent>(@"SELECT l.id Id,l.client_id ClientId,NULL CandidateId,@EmployeeId EmployeeId,'DOCUMENTS' ModuleCode,CONCAT('DOCUMENT_',l.action) EventType,CONCAT('Document ',LOWER(l.action)) EventTitle,COALESCE(JSON_UNQUOTE(JSON_EXTRACT(l.metadata_json,'$.publicId')),'') EventSummary,'Attachment' ResourceType,COALESCE(CAST(l.attachment_id AS CHAR),'') ResourceId,l.actor_user_id ActorUserId,COALESCE(u.DisplayName,u.Email,'System') ActorName,'HR' Visibility,TRUE IsSensitive,l.metadata_json MetadataJson,l.created_at_utc OccurredAt,l.created_at_utc CreatedAt FROM attachment_audit_logs l LEFT JOIN authusers u ON u.Id=l.actor_user_id WHERE ((l.entity_type='EMPLOYEE' AND l.entity_id=@EmployeeId) OR (l.entity_type='CANDIDATE' AND l.entity_id IN (SELECT Id FROM recruitment_candidates WHERE EmployeeId=@EmployeeId))) AND l.success=TRUE", new { EmployeeId = employeeId });
        events.AddRange(documentEvents);
        return events.OrderByDescending(row => row.OccurredAt).ThenByDescending(row => row.Id).Take(1000);
    }

    public async Task<IEnumerable<RecruitmentAtsScoringProfile>> GetScoringProfilesAsync(AuthUser user, int? clientId)
    {
        await using var db = Db();
        await db.OpenAsync();
        var scope = user.ClientId ?? clientId;
        var rows = (await db.QueryAsync<RecruitmentAtsScoringProfile>(@"SELECT p.*,COALESCE(c.Name,'Global') ClientName FROM recruitment_ats_scoring_profiles p LEFT JOIN clients c ON c.Id=p.ClientId WHERE (@ClientId IS NULL OR p.ClientId=@ClientId OR p.ClientId=0) ORDER BY p.ClientId,p.IsDefault DESC,p.ProfileName", new { ClientId = scope })).ToList();
        await HydrateScoringProfilesAsync(db, rows);
        return rows;
    }

    public IReadOnlyList<RecruitmentAtsScoringCriterion> GetScoringCriterionCatalog() => DefaultScoringCriteria();

    public async Task<(RecruitmentAtsScoringProfile? Row, string Error)> SaveScoringProfileAsync(RecruitmentAtsScoringProfile row, AuthUser user)
    {
        row.ClientId = user.ClientId ?? row.ClientId;
        row.ProfileName = (row.ProfileName ?? "").Trim();
        row.PositionCategory = (row.PositionCategory ?? "").Trim();
        row.ScoringMethod = (row.ScoringMethod ?? "").Trim();
        row.ParserProvider = (row.ParserProvider ?? "").Trim();
        row.ScoringProvider = (row.ScoringProvider ?? "").Trim();
        row.ModelName = (row.ModelName ?? "").Trim();
        if (row.ClientId <= 0 || string.IsNullOrWhiteSpace(row.ProfileName)) return (null, "Client and profile name are required.");
        if (!CanAccessClient(user, row.ClientId)) return (null, "Scoring profile is outside your client scope.");
        if (row.ProfileName.Length > 180 || row.PositionCategory.Length > 120) return (null, "Profile name or position category is too long.");
        if (!row.ScoringMethod.Equals("RuleBased", StringComparison.OrdinalIgnoreCase)) return (null, "Only the explainable rule-based scoring method is currently supported.");
        if (!row.ParserProvider.Equals("BuiltIn", StringComparison.OrdinalIgnoreCase) || !row.ScoringProvider.Equals("BuiltIn", StringComparison.OrdinalIgnoreCase))
            return (null, "Only the built-in secured parser and deterministic scorer are currently supported.");
        row.ScoringMethod = "RuleBased";
        row.ParserProvider = "BuiltIn";
        row.ScoringProvider = "BuiltIn";
        row.ModelName = string.IsNullOrWhiteSpace(row.ModelName) ? "Deterministic-v1" : row.ModelName;
        if (row.ModelName.Length > 120) return (null, "Model name cannot exceed 120 characters.");
        var (criteria, criteriaError) = NormalizeScoringCriteria(row);
        if (!string.IsNullOrWhiteSpace(criteriaError)) return (null, criteriaError);
        if (row.MinimumShortlistScore is < 0 or > 100) return (null, "Minimum shortlist score must be between 0 and 100.");
        await using var db = Db();
        await db.OpenAsync();
        if (row.Id > 0)
        {
            var existingClientId = await db.ExecuteScalarAsync<int?>("SELECT ClientId FROM recruitment_ats_scoring_profiles WHERE Id=@Id", row);
            if (!existingClientId.HasValue || !CanAccessClient(user, existingClientId.Value)) return (null, "Scoring profile was not found.");
            if (existingClientId.Value != row.ClientId) return (null, "A scoring profile's client cannot be changed after creation.");
        }
        var duplicate = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_ats_scoring_profiles WHERE Id<>@Id AND ClientId=@ClientId AND ProfileName=@ProfileName AND PositionCategory=@PositionCategory", row);
        if (duplicate > 0) return (null, "A scoring profile with the same name and position category already exists for this client.");
        await using var transaction = await db.BeginTransactionAsync();
        if (row.IsDefault) await db.ExecuteAsync("UPDATE recruitment_ats_scoring_profiles SET IsDefault=FALSE WHERE ClientId=@ClientId", row, transaction);
        if (row.Id <= 0)
            row.Id = await db.ExecuteScalarAsync<long>(@"INSERT INTO recruitment_ats_scoring_profiles (ClientId,ProfileName,PositionCategory,ScoringMethod,MinimumShortlistScore,AutoScoreOnResumeUpload,AllowManualOverride,ParserProvider,ScoringProvider,ModelName,VersionNumber,IsDefault,IsActive) VALUES (@ClientId,@ProfileName,@PositionCategory,@ScoringMethod,@MinimumShortlistScore,@AutoScoreOnResumeUpload,@AllowManualOverride,@ParserProvider,@ScoringProvider,@ModelName,1,@IsDefault,@IsActive);SELECT LAST_INSERT_ID();", new { row.ClientId, ProfileName = row.ProfileName.Trim(), PositionCategory = row.PositionCategory.Trim(), row.ScoringMethod, row.MinimumShortlistScore, row.AutoScoreOnResumeUpload, row.AllowManualOverride, row.ParserProvider, row.ScoringProvider, row.ModelName, row.IsDefault, row.IsActive }, transaction);
        else
            await db.ExecuteAsync(@"UPDATE recruitment_ats_scoring_profiles SET ProfileName=@ProfileName,PositionCategory=@PositionCategory,ScoringMethod=@ScoringMethod,MinimumShortlistScore=@MinimumShortlistScore,AutoScoreOnResumeUpload=@AutoScoreOnResumeUpload,AllowManualOverride=@AllowManualOverride,ParserProvider=@ParserProvider,ScoringProvider=@ScoringProvider,ModelName=@ModelName,VersionNumber=VersionNumber+1,IsDefault=@IsDefault,IsActive=@IsActive,UpdatedAt=UTC_TIMESTAMP() WHERE Id=@Id AND ClientId=@ClientId", new { row.Id, row.ClientId, ProfileName = row.ProfileName.Trim(), PositionCategory = row.PositionCategory.Trim(), row.ScoringMethod, row.MinimumShortlistScore, row.AutoScoreOnResumeUpload, row.AllowManualOverride, row.ParserProvider, row.ScoringProvider, row.ModelName, row.IsDefault, row.IsActive }, transaction);
        await db.ExecuteAsync("DELETE FROM recruitment_ats_profile_criteria WHERE ScoringProfileId=@Id", new { row.Id }, transaction);
        foreach (var criterion in criteria)
            await db.ExecuteAsync(@"INSERT INTO recruitment_ats_profile_criteria (ScoringProfileId,CriterionCode,CriterionLabel,EvaluationType,Weight,DisplayOrder,IsActive) VALUES (@ScoringProfileId,@CriterionCode,@CriterionLabel,@EvaluationType,@Weight,@DisplayOrder,@IsActive)", new { ScoringProfileId = row.Id, criterion.CriterionCode, criterion.CriterionLabel, criterion.EvaluationType, criterion.Weight, criterion.DisplayOrder, criterion.IsActive }, transaction);
        await transaction.CommitAsync();
        var saved = await db.QueryFirstAsync<RecruitmentAtsScoringProfile>("SELECT * FROM recruitment_ats_scoring_profiles WHERE Id=@Id", new { row.Id });
        await HydrateScoringProfilesAsync(db, [saved]);
        return (saved, "");
    }

    public async Task<IEnumerable<RecruitmentSkill>> GetSkillsAsync(AuthUser user, int? clientId)
    {
        await using var db = Db();
        await db.OpenAsync();
        var scope = user.ClientId ?? clientId;
        var rows = (await db.QueryAsync<RecruitmentSkill>(@"SELECT s.*,COALESCE(c.Name,'Global') ClientName FROM recruitment_skills s LEFT JOIN clients c ON c.Id=s.ClientId WHERE (@ClientId IS NULL OR s.ClientId IN (0,@ClientId)) ORDER BY s.Category,s.SkillName", new { ClientId = scope })).ToList();
        if (rows.Count == 0) return rows;
        var aliases = (await db.QueryAsync<SkillAliasListRow>("SELECT SkillId,AliasName FROM recruitment_skill_aliases WHERE SkillId IN @Ids ORDER BY SkillId,AliasName", new { Ids = rows.Select(row => row.Id).ToArray() }))
            .ToLookup(row => row.SkillId, row => row.AliasName);
        foreach (var item in rows) item.Aliases = aliases[item.Id].ToList();
        return rows;
    }

    public async Task<(RecruitmentSkill? Row, string Error)> SaveSkillAsync(RecruitmentSkill row, AuthUser user)
    {
        row.ClientId = user.ClientId ?? row.ClientId;
        if (string.IsNullOrWhiteSpace(row.SkillName)) return (null, "Skill name is required.");
        if (!CanAccessClient(user, row.ClientId) && row.ClientId != 0) return (null, "Skill is outside your client scope.");
        row.SkillCode = string.IsNullOrWhiteSpace(row.SkillCode) ? NormalizeCode(row.SkillName) : NormalizeCode(row.SkillCode);
        row.SkillName = row.SkillName.Trim();
        row.Category = (row.Category ?? "").Trim();
        var aliases = (row.Aliases ?? [])
            .Select(value => (value ?? "").Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (row.SkillName.Length > 180 || row.Category.Length > 120 || aliases.Any(alias => alias.Length > 180))
            return (null, "Skill name, category or alias exceeds the supported length.");
        var aliasRows = aliases
            .Select(alias => new { Alias = alias, Normalized = NormalizeSearch(alias) })
            .Where(alias => alias.Normalized.Length > 0)
            .GroupBy(alias => alias.Normalized, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        if (aliasRows.Count > 100) return (null, "A skill can have at most 100 aliases.");
        if (string.IsNullOrWhiteSpace(row.SkillCode) || row.SkillCode.Length > 100)
            return (null, "Skill code could not be normalized or exceeds 100 characters.");
        await using var db = Db();
        await db.OpenAsync();
        await using var transaction = await db.BeginTransactionAsync();
        if (row.Id <= 0)
            row.Id = await db.ExecuteScalarAsync<long>(@"INSERT INTO recruitment_skills (ClientId,SkillCode,SkillName,Category,IsActive) VALUES (@ClientId,@SkillCode,@SkillName,@Category,@IsActive) ON DUPLICATE KEY UPDATE Id=LAST_INSERT_ID(Id),SkillName=VALUES(SkillName),Category=VALUES(Category),IsActive=VALUES(IsActive);SELECT LAST_INSERT_ID();", row, transaction);
        else
        {
            var allowed = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_skills WHERE Id=@Id AND ClientId=@ClientId", row, transaction);
            if (allowed == 0)
            {
                await transaction.RollbackAsync();
                return (null, "Skill was not found in your permitted client scope.");
            }
            await db.ExecuteAsync("UPDATE recruitment_skills SET SkillCode=@SkillCode,SkillName=@SkillName,Category=@Category,IsActive=@IsActive,UpdatedAt=UTC_TIMESTAMP() WHERE Id=@Id AND ClientId=@ClientId", row, transaction);
        }
        await db.ExecuteAsync("DELETE FROM recruitment_skill_aliases WHERE SkillId=@Id", row, transaction);
        foreach (var alias in aliasRows)
            await db.ExecuteAsync("INSERT INTO recruitment_skill_aliases (SkillId,AliasName,NormalizedAlias) VALUES (@SkillId,@Alias,@Normalized)", new { SkillId = row.Id, alias.Alias, alias.Normalized }, transaction);
        await transaction.CommitAsync();
        return ((await GetSkillsAsync(user, row.ClientId)).FirstOrDefault(value => value.Id == row.Id), "");
    }

    public async Task<RecruitmentEmployeeReferral> LinkReferralAsync(RecruitmentEmployeeReferral referral, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        var position = await db.QueryFirstAsync<RecruitmentOpenPosition>("SELECT * FROM recruitment_open_positions WHERE Id=@Id", new { Id = referral.PositionId });
        var normalizedEmail = NormalizeEmail(referral.CandidateEmail);
        var normalizedPhone = NormalizePhone(referral.CandidatePhone);
        var candidateId = await db.ExecuteScalarAsync<long?>(@"SELECT Id FROM recruitment_candidates WHERE ProfileStatus<>'Archived' AND ((@Email<>'' AND NormalizedEmail=@Email) OR (@Phone<>'' AND NormalizedPhone=@Phone)) ORDER BY ClientId=@ClientId DESC,Id LIMIT 1", new { Email = normalizedEmail, Phone = normalizedPhone, position.ClientId });
        if (!candidateId.HasValue)
        {
            var names = SplitName(referral.CandidateName);
            var saved = await SaveCandidateAsync(new SaveRecruitmentCandidate
            {
                ClientId = position.ClientId,
                FirstName = names.FirstName,
                LastName = names.LastName,
                Email = referral.CandidateEmail,
                Phone = referral.CandidatePhone,
                SourceType = "Employee Referral",
                SourceReferenceId = referral.Id,
                ConsentStatus = "Pending"
            }, user);
            candidateId = saved.Row?.Id;
        }
        if (!candidateId.HasValue) return referral;
        await db.ExecuteAsync("UPDATE recruitment_employee_referrals SET CandidateId=@CandidateId,UpdatedAt=UTC_TIMESTAMP() WHERE Id=@Id", new { CandidateId = candidateId, referral.Id });
        var features = await FeatureSettingsAsync(db, position.ClientId);
        (RecruitmentCandidateApplication? Row, string Error) application = (null, "");
        if (features.AutoCreateApplicationFromReferral)
            application = await CreateApplicationAsync(new SaveCandidateApplication { CandidateId = candidateId.Value, PositionId = referral.PositionId, SourceType = "Employee Referral", SourceReferenceId = referral.Id }, user);
        await db.ExecuteAsync("UPDATE recruitment_employee_referrals SET CandidateId=@CandidateId,ApplicationId=@ApplicationId,UpdatedAt=UTC_TIMESTAMP() WHERE Id=@Id", new { CandidateId = candidateId, ApplicationId = application.Row?.Id, referral.Id });
        referral.CandidateId = candidateId.Value;
        referral.ApplicationId = application.Row?.Id;
        return referral;
    }

    private async Task<RecruitmentCandidateResume> RegisterResumeAsync(MySqlConnection db, RecruitmentCandidate candidate, EntityAttachment attachment, ResumeParseResult parse, AuthUser user)
    {
        await using var transaction = await db.BeginTransactionAsync();
        var conflictingOwner = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_candidate_resumes WHERE AttachmentPublicId=@PublicId AND CandidateId<>@CandidateId", new { PublicId = attachment.PublicId.ToString(), CandidateId = candidate.Id }, transaction);
        if (conflictingOwner > 0) throw new InvalidOperationException("The selected resume document is already linked to another candidate profile.");
        await db.ExecuteAsync("UPDATE recruitment_candidate_resumes SET IsPrimary=FALSE WHERE CandidateId=@CandidateId", new { CandidateId = candidate.Id }, transaction);
        var id = await db.ExecuteScalarAsync<long>(@"INSERT INTO recruitment_candidate_resumes (CandidateId,AttachmentPublicId,VersionNumber,IsPrimary,ParsingStatus,ParsedText,ParsedJson,ParserName,ParserVersion,ParsedAt,ParsingError) VALUES (@CandidateId,@PublicId,@Version,TRUE,@Status,@Text,'{}',@Parser,@ParserVersion,UTC_TIMESTAMP(),@Error) ON DUPLICATE KEY UPDATE Id=LAST_INSERT_ID(Id),IsPrimary=TRUE,ParsingStatus=VALUES(ParsingStatus),ParsedText=VALUES(ParsedText),ParsedJson='{}',ParserName=VALUES(ParserName),ParserVersion=VALUES(ParserVersion),ParsedAt=UTC_TIMESTAMP(),ParsingError=VALUES(ParsingError);SELECT LAST_INSERT_ID();", new { CandidateId = candidate.Id, PublicId = attachment.PublicId.ToString(), Version = attachment.VersionNumber, parse.Status, parse.Text, Parser = parse.ParserName, parse.ParserVersion, parse.Error }, transaction);
        await db.ExecuteAsync(@"INSERT INTO recruitment_resume_parser_runs (ResumeId,ParserName,ParserVersion,ParseStatus,ExtractedCharacterCount,ExtractedLineCount,ErrorMessage,StartedAt,CompletedAt) VALUES (@ResumeId,@ParserName,@ParserVersion,@Status,@CharacterCount,@LineCount,@Error,UTC_TIMESTAMP(),UTC_TIMESTAMP())", new { ResumeId = id, ParserName = parse.ParserName, ParserVersion = parse.ParserVersion, Status = parse.Status, CharacterCount = parse.Facts.CharacterCount, LineCount = parse.Facts.LineCount, Error = parse.Error }, transaction);
        await db.ExecuteAsync(@"INSERT INTO recruitment_resume_parse_facts (ResumeId,ExtractedEmail,ExtractedPhone,CharacterCount,LineCount,LanguageCode,SummaryText,TotalExperienceMonths) VALUES (@ResumeId,@Email,@Phone,@CharacterCount,@LineCount,@LanguageCode,@SummaryText,@TotalExperienceMonths) ON DUPLICATE KEY UPDATE ExtractedEmail=VALUES(ExtractedEmail),ExtractedPhone=VALUES(ExtractedPhone),CharacterCount=VALUES(CharacterCount),LineCount=VALUES(LineCount),LanguageCode=VALUES(LanguageCode),SummaryText=VALUES(SummaryText),TotalExperienceMonths=VALUES(TotalExperienceMonths),UpdatedAt=UTC_TIMESTAMP()", new { ResumeId = id, Email = parse.Facts.Email, Phone = parse.Facts.Phone, CharacterCount = parse.Facts.CharacterCount, LineCount = parse.Facts.LineCount, LanguageCode = parse.Facts.LanguageCode, SummaryText = parse.Facts.SummaryText, parse.Facts.TotalExperienceMonths }, transaction);
        await db.ExecuteAsync("DELETE FROM recruitment_resume_sections WHERE ResumeId=@ResumeId;DELETE FROM recruitment_resume_skills WHERE ResumeId=@ResumeId;", new { ResumeId = id }, transaction);
        foreach (var section in parse.Sections)
            await db.ExecuteAsync(@"INSERT INTO recruitment_resume_sections (ResumeId,SectionCode,Heading,Content,DisplayOrder,Confidence) VALUES (@ResumeId,@SectionCode,@Heading,@Content,@DisplayOrder,@Confidence)", new { ResumeId = id, section.SectionCode, section.Heading, section.Content, section.DisplayOrder, section.Confidence }, transaction);
        if (parse.Status == "Parsed")
        {
            await db.ExecuteAsync("DELETE FROM recruitment_candidate_skills WHERE CandidateId=@CandidateId AND Source='Resume'", new { CandidateId = candidate.Id }, transaction);
            await ExtractCandidateSkillsAsync(db, candidate, id, parse.Text, transaction);
            await ApplyParsedContactAsync(db, candidate, parse.Facts, transaction);
        }
        await transaction.CommitAsync();
        await WriteActivityAsync(db, candidate.ClientId, candidate.Id, candidate.EmployeeId, "RECRUITMENT", "RESUME_UPLOADED", parse.Status == "Disabled" ? "Resume uploaded" : "Resume uploaded and parsed", $"{attachment.OriginalFileName} / {parse.Status}", "CandidateResume", id.ToString(), user);
        if (parse.Status == "Parsed")
        {
            var applications = await db.QueryAsync<long>("SELECT Id FROM recruitment_candidate_applications WHERE CandidateId=@CandidateId AND CurrentStage NOT IN ('Rejected','Withdrawn','Joined')", new { CandidateId = candidate.Id });
            foreach (var applicationId in applications) await ScoreApplicationInternalAsync(db, applicationId, user, false);
        }
        var result = await db.QueryFirstAsync<RecruitmentCandidateResume>($"{ResumeSummarySelect} WHERE r.Id=@Id", new { Id = id });
        await HydrateResumeIntelligenceAsync(db, [result]);
        return result;
    }

    private async Task<(RecruitmentApplicationScore? Row, string Error)> ScoreApplicationInternalAsync(MySqlConnection db, long applicationId, AuthUser user, bool force)
    {
        var data = await db.QueryFirstOrDefaultAsync<ScoringRow>(@"SELECT a.Id ApplicationId,a.CandidateId,a.PositionId,a.ClientId,
a.ResumeId ApplicationResumeId,a.CurrentStage,c.CurrentTitle,c.TotalExperienceMonths,c.CurrentLocation,c.NoticePeriodDays,
c.HighestQualification,p.PositionCode,p.PositionTitle,p.PositionCategory,p.RequiredSkills,p.PreferredSkills,p.ExperienceRange,
p.JobLocation,r.Qualification,r.Certifications,jd.Id JobDescriptionVersionId,COALESCE(jd.VersionNumber,0) JobDescriptionVersionNumber,
COALESCE(NULLIF(jd.Title,''),p.PositionTitle) ScoringPositionTitle,cr.Id EffectiveResumeId,COALESCE(cr.ParsedText,'') ResumeText,
COALESCE(cr.ParsingStatus,'Pending') ParsingStatus
FROM recruitment_candidate_applications a
JOIN recruitment_candidates c ON c.Id=a.CandidateId
JOIN recruitment_open_positions p ON p.Id=a.PositionId
JOIN recruitment_requisitions r ON r.Id=p.RequisitionId
LEFT JOIN recruitment_job_postings posting ON posting.Id=COALESCE(a.JobPostingId,CASE WHEN a.SourceType='Public Job' THEN a.SourceReferenceId END) AND posting.PositionId=a.PositionId
LEFT JOIN recruitment_job_description_versions jd ON jd.Id=COALESCE(posting.JobDescriptionVersionId,p.ApprovedJobDescriptionVersionId)
LEFT JOIN recruitment_candidate_resumes cr ON cr.CandidateId=a.CandidateId AND cr.Id=COALESCE(a.ResumeId,
 (SELECT x.Id FROM recruitment_candidate_resumes x WHERE x.CandidateId=a.CandidateId AND x.IsPrimary=TRUE ORDER BY x.CreatedAt DESC,x.Id DESC LIMIT 1))
WHERE a.Id=@Id", new { Id = applicationId });
        if (data is null || !CanAccessClient(user, data.ClientId)) return (null, "Application was not found.");
        if ((data.CurrentStage is "Rejected" or "Withdrawn" or "Joined") || data.CurrentStage.StartsWith("Offer", StringComparison.OrdinalIgnoreCase)) return (null, $"ATS score cannot be recalculated after the application reaches {data.CurrentStage} stage.");
        var features = await FeatureSettingsAsync(db, data.ClientId);
        if (!features.EnableAtsScoring) return (null, "ATS scoring is disabled in Recruitment Administration.");
        if (data.EffectiveResumeId <= 0) return (null, "Upload or select a resume before scoring the application.");
        if (data.ParsingStatus != "Parsed") return (null, $"Resume parsing status is {data.ParsingStatus}. A parsed resume is required for ATS scoring.");
        var pipelineSelection = await db.QueryFirstOrDefaultAsync<PipelineAtsScoringSelection>(@"SELECT configuration.ScoringProfileId,configuration.RequireHumanConfirmation
FROM recruitment_application_pipeline_instances pipelineInstance
JOIN recruitment_application_stage_instances stageInstance ON stageInstance.Id=pipelineInstance.CurrentStageInstanceId
 AND stageInstance.Status IN ('Active','Paused')
JOIN recruitment_stage_ats_configurations configuration ON configuration.PipelineStageId=stageInstance.PipelineStageId
WHERE pipelineInstance.ApplicationId=@ApplicationId LIMIT 1", new { ApplicationId = applicationId });
        var pipelineProfileId = pipelineSelection?.ScoringProfileId;
        var preferredProfileId = pipelineProfileId ?? features.DefaultAtsScoringProfileId;
        RecruitmentAtsScoringProfile? profile = null;
        if (preferredProfileId.HasValue)
            profile = await db.QueryFirstOrDefaultAsync<RecruitmentAtsScoringProfile>(@"SELECT * FROM recruitment_ats_scoring_profiles
WHERE Id=@ProfileId AND IsActive=TRUE AND ClientId IN (0,@ClientId)
 AND (PositionCategory='' OR PositionCategory=@PositionCategory) LIMIT 1", new { ProfileId = preferredProfileId.Value, data.ClientId, data.PositionCategory });
        if (pipelineProfileId.HasValue && profile is null)
            return (null, "The ATS profile configured for the current pipeline stage is inactive or outside this client/position category.");
        profile ??= await db.QueryFirstOrDefaultAsync<RecruitmentAtsScoringProfile>(@"SELECT * FROM recruitment_ats_scoring_profiles
WHERE IsActive=TRUE AND ClientId IN (0,@ClientId) AND (PositionCategory='' OR PositionCategory=@PositionCategory)
ORDER BY ClientId=@ClientId DESC,PositionCategory=@PositionCategory DESC,IsDefault DESC,Id LIMIT 1", new { data.ClientId, data.PositionCategory });
        if (profile is null)
            profile = new RecruitmentAtsScoringProfile { ProfileName = "Built-in default", IsDefault = true, Criteria = DefaultScoringCriteria() };
        else
            await HydrateScoringProfilesAsync(db, [profile]);
        if (!force && !profile.AutoScoreOnResumeUpload) return (null, "Automatic ATS scoring is disabled for the selected profile.");
        var criteria = profile.Criteria.Where(row => row.IsActive).OrderBy(row => row.DisplayOrder).ToList();
        if (criteria.Count == 0) criteria = DefaultScoringCriteria();
        var jdSkills = data.JobDescriptionVersionId.HasValue
            ? (await db.QueryAsync<JdSkillScoringRow>(@"SELECT SkillId,SkillName,IsRequired,MinimumYears,MinimumProficiency,WeightPercent
FROM recruitment_jd_skill_requirements WHERE JobDescriptionVersionId=@Id ORDER BY IsRequired DESC,DisplayOrder,Id", new { Id = data.JobDescriptionVersionId.Value })).ToList()
            : [];
        var jdQualifications = data.JobDescriptionVersionId.HasValue
            ? (await db.QueryAsync<string>(@"SELECT TRIM(CONCAT_WS(' ',QualificationName,NULLIF(Specialization,'')))
FROM recruitment_jd_qualification_requirements WHERE JobDescriptionVersionId=@Id ORDER BY IsMandatory DESC,DisplayOrder,Id", new { Id = data.JobDescriptionVersionId.Value })).Where(value => !string.IsNullOrWhiteSpace(value)).ToList()
            : [];
        var jdCertifications = data.JobDescriptionVersionId.HasValue
            ? (await db.QueryAsync<string>(@"SELECT CertificationName FROM recruitment_jd_certification_requirements
WHERE JobDescriptionVersionId=@Id ORDER BY IsMandatory DESC,DisplayOrder,Id", new { Id = data.JobDescriptionVersionId.Value })).Where(value => !string.IsNullOrWhiteSpace(value)).ToList()
            : [];
        var requiredRequirements = jdSkills.Where(row => row.IsRequired && !string.IsNullOrWhiteSpace(row.SkillName)).ToList();
        var preferredRequirements = jdSkills.Where(row => !row.IsRequired && !string.IsNullOrWhiteSpace(row.SkillName)).ToList();
        var required = requiredRequirements.Count > 0 ? requiredRequirements.Select(row => row.SkillName).Distinct(StringComparer.OrdinalIgnoreCase).ToList() : SplitTerms(data.RequiredSkills);
        var preferred = preferredRequirements.Count > 0 ? preferredRequirements.Select(row => row.SkillName).Distinct(StringComparer.OrdinalIgnoreCase).ToList() : SplitTerms(data.PreferredSkills);
        var requiredSkillsSnapshot = string.Join(", ", required);
        var preferredSkillsSnapshot = string.Join(", ", preferred);
        var qualificationRequirement = jdQualifications.Count > 0 ? string.Join(", ", jdQualifications) : data.Qualification;
        var certificationRequirement = jdCertifications.Count > 0 ? string.Join(", ", jdCertifications) : data.Certifications;
        var experienceRange = data.ExperienceRange;
        if (string.IsNullOrWhiteSpace(experienceRange) && requiredRequirements.Any(row => row.MinimumYears > 0))
            experienceRange = $"{requiredRequirements.Max(row => row.MinimumYears):0.#}+ years";
        var scoringPositionTitle = string.IsNullOrWhiteSpace(data.ScoringPositionTitle) ? data.PositionTitle : data.ScoringPositionTitle;
        var resumeSearch = NormalizeSearch(string.Join(' ', data.ResumeText, data.CurrentTitle, data.HighestQualification, data.CurrentLocation));
        var skillAliases = (await db.QueryAsync<SkillAliasRow>(@"SELECT s.SkillName,COALESCE(a.AliasName,'') AliasName FROM recruitment_skills s LEFT JOIN recruitment_skill_aliases a ON a.SkillId=s.Id WHERE s.IsActive=TRUE AND s.ClientId IN (0,@ClientId)", new { data.ClientId }))
            .GroupBy(row => row.SkillName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(row => row.AliasName).Where(value => !string.IsNullOrWhiteSpace(value)).Append(group.Key).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.OrdinalIgnoreCase);
        var requiredMatches = requiredRequirements.Count > 0
            ? requiredRequirements.Select(requirement => ResolveSkillMatch(data.ResumeText, resumeSearch, requirement.SkillName, "Required", skillAliases, requirement)).ToList()
            : required.Select(term => ResolveSkillMatch(data.ResumeText, resumeSearch, term, "Required", skillAliases)).ToList();
        var preferredMatches = preferredRequirements.Count > 0
            ? preferredRequirements.Select(requirement => ResolveSkillMatch(data.ResumeText, resumeSearch, requirement.SkillName, "Preferred", skillAliases, requirement)).ToList()
            : preferred.Select(term => ResolveSkillMatch(data.ResumeText, resumeSearch, term, "Preferred", skillAliases)).ToList();
        var requiredRatio = SkillRequirementRatio(requiredMatches);
        var preferredRatio = SkillRequirementRatio(preferredMatches);
        var experienceRatio = ExperienceRatio(data.TotalExperienceMonths, experienceRange);
        var qualificationRatio = TextCriterionScore(resumeSearch, qualificationRequirement, data.HighestQualification);
        var certificationRatio = TextCriterionScore(resumeSearch, certificationRequirement, "");
        var roleRatio = TokenSimilarity(data.CurrentTitle, scoringPositionTitle);
        var locationRatio = string.IsNullOrWhiteSpace(data.JobLocation) || ContainsTerm(resumeSearch, data.JobLocation) || ContainsTerm(NormalizeSearch(data.CurrentLocation), data.JobLocation) ? 1m : 0m;
        var noticeRatio = data.NoticePeriodDays <= 30 ? 1m : data.NoticePeriodDays <= 60 ? .6m : data.NoticePeriodDays <= 90 ? .3m : 0m;
        var ratios = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
        {
            ["requiredSkills"] = requiredRatio,
            ["preferredSkills"] = preferredRatio,
            ["experience"] = experienceRatio,
            ["qualification"] = qualificationRatio,
            ["certifications"] = certificationRatio,
            ["roleSimilarity"] = roleRatio,
            ["location"] = locationRatio,
            ["noticePeriod"] = noticeRatio
        };
        var evidenceSummaries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["requiredSkills"] = $"{requiredMatches.Count(row => row.IsMatched)} of {requiredMatches.Count} required skills matched",
            ["preferredSkills"] = $"{preferredMatches.Count(row => row.IsMatched)} of {preferredMatches.Count} preferred skills matched",
            ["experience"] = $"{data.TotalExperienceMonths} months against requirement '{experienceRange}'",
            ["qualification"] = $"Candidate '{data.HighestQualification}' against '{qualificationRequirement}'",
            ["certifications"] = string.IsNullOrWhiteSpace(certificationRequirement) ? "No certification requirement" : $"Required: {certificationRequirement}",
            ["roleSimilarity"] = $"Current title '{data.CurrentTitle}' against '{scoringPositionTitle}'",
            ["location"] = $"Candidate '{data.CurrentLocation}' against '{data.JobLocation}'",
            ["noticePeriod"] = $"Candidate notice period: {data.NoticePeriodDays} days"
        };
        var components = criteria.Select(criterion => new CalculatedScoreComponent(
            criterion.CriterionCode,
            criterion.CriterionLabel,
            criterion.Weight,
            Round(ratios.GetValueOrDefault(criterion.CriterionCode)),
            Round(ratios.GetValueOrDefault(criterion.CriterionCode) * criterion.Weight),
            evidenceSummaries.GetValueOrDefault(criterion.CriterionCode, ""),
            criterion.DisplayOrder)).ToList();
        var total = Math.Clamp(Round(components.Sum(row => row.AwardedScore)), 0, 100);
        var recommendation = total >= profile.MinimumShortlistScore ? "Review for shortlist" : "Below shortlist threshold";
        var humanReviewRequired = pipelineSelection?.RequireHumanConfirmation ?? true;
        var explanationText = humanReviewRequired
            ? $"{recommendation}. Deterministic ATS score is decision support; the current pipeline stage requires human confirmation."
            : $"{recommendation}. Deterministic ATS score is decision support; any configured pipeline automation remains audit logged.";
        var evidence = new[]
        {
            new CalculatedScoreEvidence("experience", "Experience", experienceRange, $"{data.TotalExperienceMonths} months", experienceRatio),
            new CalculatedScoreEvidence("qualification", "Qualification", qualificationRequirement, data.HighestQualification, qualificationRatio),
            new CalculatedScoreEvidence("certifications", "Certification", certificationRequirement, certificationRatio > 0 ? "Resume match found" : "No match found", certificationRatio),
            new CalculatedScoreEvidence("roleSimilarity", "Role", scoringPositionTitle, data.CurrentTitle, roleRatio),
            new CalculatedScoreEvidence("location", "Location", data.JobLocation, data.CurrentLocation, locationRatio),
            new CalculatedScoreEvidence("noticePeriod", "NoticePeriod", "30 days preferred", $"{data.NoticePeriodDays} days", noticeRatio)
        };
        var resumeSectionReferences = (await db.QueryAsync<ResumeSectionReference>("SELECT Id,SectionCode FROM recruitment_resume_sections WHERE ResumeId=@ResumeId ORDER BY DisplayOrder,Id", new { ResumeId = data.EffectiveResumeId }))
            .GroupBy(row => row.SectionCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Id, StringComparer.OrdinalIgnoreCase);

        await using var transaction = await db.BeginTransactionAsync();
        await db.ExecuteAsync("UPDATE recruitment_application_scores SET IsCurrent=FALSE WHERE ApplicationId=@Id AND IsCurrent=TRUE", new { Id = applicationId }, transaction);
        var scoreId = await db.ExecuteScalarAsync<long>(@"INSERT INTO recruitment_application_scores (ApplicationId,ResumeId,ScoringProfileId,PositionSnapshotJson,PositionSnapshotHash,TotalScore,ComponentScoresJson,MatchedSkillsJson,MissingSkillsJson,ExplanationJson,ScoringMethod,ModelName,ModelVersion,ScoreStatus,IsCurrent,ShortlistThreshold,Recommendation,ExplanationText,ProfileVersionNumber,HumanReviewRequired,ScoredAt) VALUES (@ApplicationId,@ResumeId,@ProfileId,NULL,SHA2(CONCAT_WS('|',@JobDescriptionVersionId,@PositionCode,@PositionTitle,@PositionCategory,@RequiredSkills,@PreferredSkills,@ExperienceRange,@Qualification,@Certifications,@JobLocation),256),@Total,JSON_OBJECT(),JSON_ARRAY(),JSON_ARRAY(),JSON_OBJECT(),@Method,@Model,@Version,'Completed',TRUE,@Threshold,@Recommendation,@ExplanationText,@ProfileVersion,@HumanReviewRequired,UTC_TIMESTAMP());SELECT LAST_INSERT_ID();", new { ApplicationId = applicationId, ResumeId = data.EffectiveResumeId, ProfileId = profile.Id > 0 ? (long?)profile.Id : null, Total = total, Method = profile.ScoringMethod, Model = profile.ModelName, Version = profile.VersionNumber.ToString(CultureInfo.InvariantCulture), Threshold = profile.MinimumShortlistScore, Recommendation = recommendation, ExplanationText = explanationText, ProfileVersion = profile.VersionNumber, HumanReviewRequired = humanReviewRequired, data.JobDescriptionVersionId, data.PositionCode, PositionTitle = scoringPositionTitle, data.PositionCategory, RequiredSkills = requiredSkillsSnapshot, PreferredSkills = preferredSkillsSnapshot, ExperienceRange = experienceRange, Qualification = qualificationRequirement, Certifications = certificationRequirement, data.JobLocation }, transaction);
        await db.ExecuteAsync(@"INSERT INTO recruitment_application_score_position_snapshots (ApplicationScoreId,PositionId,JobDescriptionVersionId,JobDescriptionVersionNumber,PositionCode,PositionTitle,PositionCategory,RequiredSkills,PreferredSkills,ExperienceRange,Qualification,Certifications,JobLocation) VALUES (@ScoreId,@PositionId,@JobDescriptionVersionId,@JobDescriptionVersionNumber,@PositionCode,@PositionTitle,@PositionCategory,@RequiredSkills,@PreferredSkills,@ExperienceRange,@Qualification,@Certifications,@JobLocation)", new { ScoreId = scoreId, data.PositionId, data.JobDescriptionVersionId, data.JobDescriptionVersionNumber, data.PositionCode, PositionTitle = scoringPositionTitle, data.PositionCategory, RequiredSkills = requiredSkillsSnapshot, PreferredSkills = preferredSkillsSnapshot, ExperienceRange = experienceRange, Qualification = qualificationRequirement, Certifications = certificationRequirement, data.JobLocation }, transaction);
        foreach (var component in components)
            await db.ExecuteAsync(@"INSERT INTO recruitment_application_score_components (ApplicationScoreId,CriterionCode,CriterionLabel,Weight,RawRatio,AwardedScore,MaximumScore,EvidenceSummary,DisplayOrder) VALUES (@ScoreId,@CriterionCode,@CriterionLabel,@Weight,@RawRatio,@AwardedScore,@Weight,@EvidenceSummary,@DisplayOrder)", new { ScoreId = scoreId, component.CriterionCode, component.CriterionLabel, component.Weight, component.RawRatio, component.AwardedScore, component.EvidenceSummary, component.DisplayOrder }, transaction);
        foreach (var skill in requiredMatches.Concat(preferredMatches))
            await db.ExecuteAsync(@"INSERT INTO recruitment_application_score_skill_matches (ApplicationScoreId,SkillType,SkillName,MatchStatus,MatchedTerm,EvidenceExcerpt,RequirementWeight,MinimumYears,MinimumProficiency,Confidence) VALUES (@ScoreId,@SkillType,@SkillName,@MatchStatus,@MatchedTerm,@EvidenceExcerpt,@RequirementWeight,@MinimumYears,@MinimumProficiency,@Confidence)", new { ScoreId = scoreId, skill.SkillType, skill.SkillName, MatchStatus = skill.IsMatched ? "Matched" : "Missing", skill.MatchedTerm, skill.EvidenceExcerpt, skill.RequirementWeight, skill.MinimumYears, skill.MinimumProficiency, Confidence = skill.IsMatched ? 0.9m : 0m }, transaction);
        foreach (var item in evidence)
            await db.ExecuteAsync(@"INSERT INTO recruitment_application_score_evidence (ApplicationScoreId,CriterionCode,EvidenceType,ExpectedValue,ActualValue,MatchStatus,Confidence,ResumeSectionId) VALUES (@ScoreId,@CriterionCode,@EvidenceType,@ExpectedValue,@ActualValue,@MatchStatus,@Confidence,@ResumeSectionId)", new { ScoreId = scoreId, item.CriterionCode, item.EvidenceType, item.ExpectedValue, item.ActualValue, MatchStatus = item.Ratio >= 1m ? "Matched" : item.Ratio > 0 ? "Partial" : "NotMatched", Confidence = Round(item.Ratio), ResumeSectionId = EvidenceSectionId(item.CriterionCode, resumeSectionReferences) }, transaction);
        await db.ExecuteAsync("UPDATE recruitment_candidate_applications SET ResumeId=@ResumeId,UpdatedAt=UTC_TIMESTAMP() WHERE Id=@Id", new { Id = applicationId, ResumeId = data.EffectiveResumeId }, transaction);
        await transaction.CommitAsync();
        await WriteActivityAsync(db, data.ClientId, data.CandidateId, null, "RECRUITMENT", "ATS_SCORE_GENERATED", "ATS score generated", $"Score {total:0.##}/100 for {data.PositionTitle}", "RecruitmentApplicationScore", scoreId.ToString(), user);
        var result = await db.QueryFirstAsync<RecruitmentApplicationScore>("SELECT * FROM recruitment_application_scores WHERE Id=@Id", new { Id = scoreId });
        await HydrateScoresAsync(db, [result]);
        return (result, "");
    }

    private static async Task<IEnumerable<RecruitmentCandidateApplication>> ApplicationsAsync(MySqlConnection db, AuthUser user, long? positionId = null, long? candidateId = null, string stage = "") =>
        await db.QueryAsync<RecruitmentCandidateApplication>(@"SELECT a.*,c.CandidateCode,CONCAT(c.FirstName,' ',c.LastName) CandidateName,c.Email CandidateEmail,c.Phone CandidatePhone,p.PositionCode,p.PositionTitle,cl.Name ClientName,COALESCE(u.DisplayName,u.Email,'') RecruiterName,COALESCE(s.OverrideScore,s.TotalScore) AtsScore,COALESCE(s.ScoreStatus,'Not Scored') ScoreStatus
FROM recruitment_candidate_applications a JOIN recruitment_candidates c ON c.Id=a.CandidateId JOIN recruitment_open_positions p ON p.Id=a.PositionId LEFT JOIN clients cl ON cl.Id=a.ClientId LEFT JOIN authusers u ON u.Id=a.RecruiterUserId LEFT JOIN recruitment_application_scores s ON s.ApplicationId=a.Id AND s.IsCurrent=TRUE
WHERE (@ClientId IS NULL OR a.ClientId=@ClientId) AND (@PositionId IS NULL OR a.PositionId=@PositionId) AND (@CandidateId IS NULL OR a.CandidateId=@CandidateId) AND (@Stage='' OR a.CurrentStage=@Stage) ORDER BY a.UpdatedAt DESC", new { ClientId = user.ClientId, PositionId = positionId, CandidateId = candidateId, Stage = stage ?? "" });

    private static async Task<RecruitmentCandidateApplication?> ApplicationByIdAsync(MySqlConnection db, long id, AuthUser user) =>
        (await ApplicationsAsync(db, user)).FirstOrDefault(row => row.Id == id);

    private static async Task<IEnumerable<RecruitmentInterview>> InterviewRowsAsync(MySqlConnection db, AuthUser user, long? applicationId, long[]? applicationIds)
    {
        var rows = (await db.QueryAsync<RecruitmentInterview>(@"SELECT i.*,CONCAT(c.FirstName,' ',c.LastName) CandidateName,p.PositionTitle,
COALESCE(ps.StageName,'') PipelineStageName,(i.PipelineStageInstanceId IS NOT NULL) IsPipelineManaged,
COALESCE(rc.DefaultDurationMinutes,0) DefaultDurationMinutes,COALESCE(rc.MinimumPanelCount,0) MinimumPanelCount,
COALESCE(rc.MinimumPassingScore,0) MinimumPassingScore,COALESCE(rc.FeedbackRequired,FALSE) FeedbackRequired,
COALESCE(rc.CalendarEnabled,FALSE) CalendarEnabled,COALESCE(rc.AllowReschedule,TRUE) AllowReschedule,
COALESCE((SELECT JSON_ARRAYAGG(pm.PanelUserId) FROM recruitment_interview_panel_members pm WHERE pm.InterviewId=i.Id),'[]') PanelUserIdsJson
FROM recruitment_interviews i JOIN recruitment_candidate_applications a ON a.Id=i.ApplicationId
JOIN recruitment_candidates c ON c.Id=a.CandidateId JOIN recruitment_open_positions p ON p.Id=a.PositionId
LEFT JOIN recruitment_application_stage_instances si ON si.Id=i.PipelineStageInstanceId
LEFT JOIN recruitment_pipeline_stages ps ON ps.Id=si.PipelineStageId
LEFT JOIN recruitment_interview_stage_configurations rc ON rc.Id=i.RoundConfigurationId
WHERE (@ClientId IS NULL OR a.ClientId=@ClientId) AND (@ApplicationId IS NULL OR i.ApplicationId=@ApplicationId)
AND (@UseIds=FALSE OR i.ApplicationId IN @Ids) ORDER BY i.ScheduledStart DESC", new { ClientId = user.ClientId, ApplicationId = applicationId, UseIds = applicationIds is { Length: > 0 }, Ids = applicationIds ?? [0L] })).ToList();
        if (rows.Count == 0) return rows;
        var ids = rows.Select(row => row.Id).ToArray();
        var panels = (await db.QueryAsync<(long InterviewId, int PanelUserId)>("SELECT InterviewId,PanelUserId FROM recruitment_interview_panel_members WHERE InterviewId IN @Ids ORDER BY InterviewId,Id", new { Ids = ids })).ToLookup(row => row.InterviewId, row => row.PanelUserId);
        var configurationIds = rows.Where(row => row.RoundConfigurationId is > 0).Select(row => row.RoundConfigurationId!.Value).Distinct().ToArray();
        var competencies = configurationIds.Length == 0 ? [] : (await db.QueryAsync<InterviewCompetencyConfigRow>(InterviewCompetencySelect + " WHERE sc.InterviewStageConfigurationId IN @Ids ORDER BY sc.InterviewStageConfigurationId,sc.DisplayOrder,sc.Id", new { Ids = configurationIds })).ToList();
        var competencyLookup = competencies.ToLookup(row => row.InterviewStageConfigurationId);
        foreach (var row in rows)
        {
            row.PanelUserIds = panels[row.Id].ToList();
            row.Competencies = row.RoundConfigurationId is > 0 ? competencyLookup[row.RoundConfigurationId.Value].Select(ToStageCompetency).ToList() : [];
        }
        return rows;
    }

    private static async Task<InterviewPipelineContextRow?> InterviewPipelineContextAsync(MySqlConnection db, long applicationId, long? interviewId)
    {
        if (interviewId is > 0)
            return await db.QueryFirstOrDefaultAsync<InterviewPipelineContextRow>(@"SELECT a.ClientId,(i.PipelineStageInstanceId IS NOT NULL) HasPipelineInstance,i.PipelineStageInstanceId,
i.RoundConfigurationId,COALESCE(ps.StageType,'') StageType,COALESCE(ps.StageName,i.RoundCode) PipelineStageName,
COALESCE(rc.InterviewType,i.InterviewType) InterviewType,COALESCE(rc.DefaultDurationMinutes,60) DefaultDurationMinutes,
COALESCE(rc.MinimumPanelCount,1) MinimumPanelCount,COALESCE(rc.MinimumPassingScore,60) MinimumPassingScore,
COALESCE(rc.FeedbackRequired,FALSE) FeedbackRequired,COALESCE(rc.CalendarEnabled,TRUE) CalendarEnabled,
COALESCE(rc.AllowReschedule,TRUE) AllowReschedule
FROM recruitment_interviews i JOIN recruitment_candidate_applications a ON a.Id=i.ApplicationId
LEFT JOIN recruitment_application_stage_instances si ON si.Id=i.PipelineStageInstanceId
LEFT JOIN recruitment_pipeline_stages ps ON ps.Id=si.PipelineStageId
LEFT JOIN recruitment_interview_stage_configurations rc ON rc.Id=i.RoundConfigurationId
WHERE i.Id=@InterviewId AND i.ApplicationId=@ApplicationId", new { InterviewId = interviewId, ApplicationId = applicationId });
        return await db.QueryFirstOrDefaultAsync<InterviewPipelineContextRow>(@"SELECT a.ClientId,(pi.Id IS NOT NULL) HasPipelineInstance,si.Id PipelineStageInstanceId,
rc.Id RoundConfigurationId,COALESCE(ps.StageType,'') StageType,COALESCE(ps.StageName,a.CurrentStage) PipelineStageName,
COALESCE(rc.InterviewType,'Technical') InterviewType,COALESCE(rc.DefaultDurationMinutes,60) DefaultDurationMinutes,
COALESCE(rc.MinimumPanelCount,1) MinimumPanelCount,COALESCE(rc.MinimumPassingScore,60) MinimumPassingScore,
COALESCE(rc.FeedbackRequired,FALSE) FeedbackRequired,COALESCE(rc.CalendarEnabled,TRUE) CalendarEnabled,
COALESCE(rc.AllowReschedule,TRUE) AllowReschedule
FROM recruitment_candidate_applications a LEFT JOIN recruitment_application_pipeline_instances pi ON pi.ApplicationId=a.Id
LEFT JOIN recruitment_application_stage_instances si ON si.Id=pi.CurrentStageInstanceId AND si.Status IN ('Active','Paused')
LEFT JOIN recruitment_pipeline_stages ps ON ps.Id=si.PipelineStageId
LEFT JOIN recruitment_interview_stage_configurations rc ON rc.PipelineStageId=ps.Id
WHERE a.Id=@ApplicationId", new { ApplicationId = applicationId });
    }

    private static async Task<IEnumerable<InterviewCompetencyConfigRow>> InterviewCompetenciesAsync(MySqlConnection db, long roundConfigurationId) =>
        await db.QueryAsync<InterviewCompetencyConfigRow>(InterviewCompetencySelect + " WHERE sc.InterviewStageConfigurationId=@Id ORDER BY sc.DisplayOrder,sc.Id", new { Id = roundConfigurationId });

    private static RecruitmentInterviewStageCompetency ToStageCompetency(InterviewCompetencyConfigRow row) => new()
    {
        Id = row.Id,
        InterviewStageConfigurationId = row.InterviewStageConfigurationId,
        CompetencyId = row.CompetencyId,
        CompetencyName = row.CompetencyName ?? "",
        WeightPercent = row.WeightPercent,
        MinimumScore = row.MinimumScore,
        DisplayOrder = row.DisplayOrder
    };

    private static async Task<List<RecruitmentInterviewFeedback>> InterviewFeedbackRowsAsync(MySqlConnection db, long interviewId)
    {
        var rows = (await db.QueryAsync<RecruitmentInterviewFeedback>(@"SELECT f.Id,f.InterviewId,f.PanelUserId,COALESCE(u.DisplayName,u.Email,'') PanelUserName,f.OverallScore,f.Recommendation,f.CompetencyScoresJson,f.WeightedScore,f.ScoreSource,COALESCE(f.Comments,'') Comments,f.SubmittedAt FROM recruitment_interview_feedback f LEFT JOIN authusers u ON u.Id=f.PanelUserId WHERE f.InterviewId=@InterviewId ORDER BY f.SubmittedAt,f.Id", new { InterviewId = interviewId })).ToList();
        if (rows.Count == 0) return rows;
        var ids = rows.Select(row => row.Id).ToArray();
        var scores = (await db.QueryAsync<RecruitmentInterviewFeedbackCompetencyScore>(@"SELECT s.*,d.CompetencyCode,COALESCE(NULLIF(s.CompetencyName,''),d.CompetencyName) CompetencyName,(s.Score>=s.MinimumScore) MeetsMinimum FROM recruitment_interview_feedback_competency_scores s LEFT JOIN recruitment_interview_competency_definitions d ON d.Id=s.CompetencyId WHERE s.InterviewFeedbackId IN @Ids ORDER BY s.InterviewFeedbackId,s.Id", new { Ids = ids })).ToLookup(row => row.InterviewFeedbackId);
        foreach (var row in rows) row.CompetencyScores = scores[row.Id].ToList();
        return rows;
    }

    private static List<SaveRecruitmentInterviewFeedbackCompetencyScore> LegacyCompetencyScores(string json, IReadOnlyCollection<InterviewCompetencyConfigRow> configured)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return [];
            var result = new List<SaveRecruitmentInterviewFeedbackCompetencyScore>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!property.Value.TryGetDecimal(out var score)) continue;
                var config = configured.FirstOrDefault(row => string.Equals(row.CompetencyCode, property.Name, StringComparison.OrdinalIgnoreCase) || string.Equals(row.CompetencyName, property.Name, StringComparison.OrdinalIgnoreCase));
                if (config is not null) result.Add(new SaveRecruitmentInterviewFeedbackCompetencyScore { InterviewStageCompetencyId = config.Id, Score = score });
            }
            return result;
        }
        catch { return []; }
    }

    private static async Task<(PipelineOfferPolicyContext? Policy, string Error)> PipelineOfferPolicyAsync(
        MySqlConnection db,
        long applicationId,
        decimal offeredCtc,
        string currency,
        long? offerId = null,
        long? savedConfigurationId = null,
        long? savedStageInstanceId = null)
    {
        const string select = @"SELECT a.ClientId,a.PositionId,si.Id PipelineStageInstanceId,oc.Id StageOfferConfigurationId,
oc.OfferTemplateId,oc.ApprovalWorkflowId,oc.BudgetBasis,oc.MaximumVariancePercent,
oc.RequireApprovalWhenVarianceExceeded,oc.VarianceApprovalWorkflowId,oc.CandidateResponseValidityDays,
p.BudgetAvailable,p.BudgetAmount,p.SalaryMax,p.Currency PositionCurrency,p.ApprovedPositions
FROM recruitment_candidate_applications a
JOIN recruitment_open_positions p ON p.Id=a.PositionId
JOIN recruitment_application_pipeline_instances pi ON pi.ApplicationId=a.Id AND pi.Status='Active'
JOIN recruitment_application_stage_instances si ON si.Id=pi.CurrentStageInstanceId AND si.ApplicationId=a.Id AND si.Status IN ('Active','Paused')
JOIN recruitment_pipeline_stages ps ON ps.Id=si.PipelineStageId AND ps.PipelineVersionId=pi.PipelineVersionId AND ps.StageType='Offer'
JOIN recruitment_stage_offer_configurations oc ON oc.PipelineStageId=ps.Id
WHERE a.Id=@ApplicationId";
        var policy = await db.QueryFirstOrDefaultAsync<PipelineOfferPolicyContext>(select, new { ApplicationId = applicationId });
        if (policy is null && savedConfigurationId is > 0)
        {
            policy = await db.QueryFirstOrDefaultAsync<PipelineOfferPolicyContext>(@"SELECT a.ClientId,a.PositionId,si.Id PipelineStageInstanceId,
oc.Id StageOfferConfigurationId,oc.OfferTemplateId,oc.ApprovalWorkflowId,oc.BudgetBasis,
oc.MaximumVariancePercent,oc.RequireApprovalWhenVarianceExceeded,oc.VarianceApprovalWorkflowId,
oc.CandidateResponseValidityDays,p.BudgetAvailable,p.BudgetAmount,p.SalaryMax,p.Currency PositionCurrency,p.ApprovedPositions
FROM recruitment_candidate_applications a
JOIN recruitment_open_positions p ON p.Id=a.PositionId
JOIN recruitment_application_pipeline_instances pi ON pi.ApplicationId=a.Id
JOIN recruitment_stage_offer_configurations oc ON oc.Id=@ConfigurationId
JOIN recruitment_pipeline_stages ps ON ps.Id=oc.PipelineStageId AND ps.PipelineVersionId=pi.PipelineVersionId AND ps.StageType='Offer'
LEFT JOIN recruitment_application_stage_instances si ON si.Id=@StageInstanceId AND si.ApplicationId=a.Id AND si.PipelineStageId=ps.Id
WHERE a.Id=@ApplicationId", new { ApplicationId = applicationId, ConfigurationId = savedConfigurationId, StageInstanceId = savedStageInstanceId });
        }
        if (policy is null)
        {
            if (savedConfigurationId is > 0) return (null, "");
            var hasActivePipeline = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*)
FROM recruitment_application_pipeline_instances pi
JOIN recruitment_application_stage_instances si ON si.Id=pi.CurrentStageInstanceId AND si.Status IN ('Active','Paused')
WHERE pi.ApplicationId=@ApplicationId AND pi.Status='Active'", new { ApplicationId = applicationId });
            return hasActivePipeline > 0
                ? (null, "Move the candidate to the configured Offer stage before creating or releasing an offer.")
                : (null, "");
        }

        policy.BudgetBasis = NormalizeOfferBudgetBasis(policy.BudgetBasis);
        if (policy.BudgetBasis != "SalaryRangeMaximum" && !policy.BudgetAvailable)
            return (null, "The offer stage uses an approved-budget basis, but budget availability is not approved for this open position.");
        policy.ApprovedBudgetAmount = policy.BudgetBasis switch
        {
            "ApprovedTotal" => policy.BudgetAmount,
            "SalaryRangeMaximum" => policy.SalaryMax,
            _ => policy.BudgetAmount > 0
                ? Math.Round(policy.BudgetAmount / Math.Max(1, policy.ApprovedPositions), 2, MidpointRounding.AwayFromZero)
                : policy.SalaryMax
        };
        if (policy.ApprovedBudgetAmount <= 0)
            return (null, policy.BudgetBasis == "SalaryRangeMaximum"
                ? "The offer stage uses salary-range maximum, but the open position has no approved maximum salary."
                : "The offer stage uses approved budget, but the open position has no positive approved budget amount.");
        if (!string.IsNullOrWhiteSpace(policy.PositionCurrency)
            && !policy.PositionCurrency.Equals(currency, StringComparison.OrdinalIgnoreCase))
            return (null, $"Offer currency must match the approved position currency ({policy.PositionCurrency.ToUpperInvariant()}).");
        if (policy.OfferTemplateId is > 0)
        {
            var validTemplate = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM recruitment_templates
WHERE Id=@Id AND IsActive=TRUE AND ClientId IN (0,@ClientId)
AND LOWER(TemplateType) LIKE '%offer%'", new { Id = policy.OfferTemplateId.Value, policy.ClientId });
            if (validTemplate == 0) return (null, "The offer template configured for the current pipeline stage is inactive, is not an offer template, or belongs to another client.");
        }
        policy.BudgetExposureAmount = offeredCtc;
        if (policy.BudgetBasis == "ApprovedTotal")
        {
            var mismatchedCurrencies = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM recruitment_offers existingOffer
JOIN recruitment_candidate_applications existingApplication ON existingApplication.Id=existingOffer.ApplicationId
WHERE existingApplication.PositionId=@PositionId AND existingOffer.Id<>COALESCE(@OfferId,0)
AND existingOffer.Status NOT IN ('Rejected','Expired','Withdrawn')
AND UPPER(existingOffer.Currency)<>UPPER(@Currency)", new { policy.PositionId, OfferId = offerId, Currency = policy.PositionCurrency });
            if (mismatchedCurrencies > 0)
                return (null, "The total approved budget cannot be evaluated because another active offer for this position uses a different currency.");
            var committed = await db.ExecuteScalarAsync<decimal?>(@"SELECT COALESCE(SUM(existingOffer.OfferedCtc),0)
FROM recruitment_offers existingOffer
JOIN recruitment_candidate_applications existingApplication ON existingApplication.Id=existingOffer.ApplicationId
WHERE existingApplication.PositionId=@PositionId AND existingOffer.Id<>COALESCE(@OfferId,0)
AND existingOffer.Status NOT IN ('Rejected','Expired','Withdrawn')", new { policy.PositionId, OfferId = offerId }) ?? 0;
            policy.BudgetExposureAmount = committed + offeredCtc;
        }
        policy.VariancePercent = Math.Round(
            (policy.BudgetExposureAmount - policy.ApprovedBudgetAmount) * 100m / policy.ApprovedBudgetAmount,
            2,
            MidpointRounding.AwayFromZero);
        policy.VarianceExceeded = policy.VariancePercent > policy.MaximumVariancePercent;
        policy.CandidateResponseValidityDays = Math.Clamp(policy.CandidateResponseValidityDays, 1, 365);
        return (policy, "");
    }

    private static async Task<int> ResolveOfferWorkflowRequestorAsync(MySqlConnection db, long applicationId, int fallbackUserId) =>
        await db.ExecuteScalarAsync<int?>(@"SELECT COALESCE(requesterUser.Id,applicationRecruiter.Id,positionRecruiter.Id,fallbackUser.Id)
FROM recruitment_candidate_applications applicationRow
JOIN recruitment_open_positions positionRow ON positionRow.Id=applicationRow.PositionId
LEFT JOIN recruitment_requisitions requisition ON requisition.Id=positionRow.RequisitionId
LEFT JOIN authusers requesterUser ON requesterUser.Id=requisition.RequestedByUserId AND requesterUser.IsActive=TRUE
LEFT JOIN authusers applicationRecruiter ON applicationRecruiter.Id=applicationRow.RecruiterUserId AND applicationRecruiter.IsActive=TRUE
LEFT JOIN authusers positionRecruiter ON positionRecruiter.Id=positionRow.RecruiterUserId AND positionRecruiter.IsActive=TRUE
LEFT JOIN authusers fallbackUser ON fallbackUser.Id=@FallbackUserId AND fallbackUser.IsActive=TRUE
WHERE applicationRow.Id=@ApplicationId LIMIT 1", new { ApplicationId = applicationId, FallbackUserId = fallbackUserId }) ?? 0;

    private static DateTime CandidateResponseExpiry(DateTime proposedJoiningDate, int validityDays)
    {
        var configuredExpiry = DateTime.Today.AddDays(Math.Clamp(validityDays, 1, 365));
        return proposedJoiningDate.Date < configuredExpiry ? proposedJoiningDate.Date : configuredExpiry;
    }

    private static string NormalizeOfferBudgetBasis(string value) => (value ?? "").Trim().ToUpperInvariant() switch
    {
        "APPROVEDTOTAL" => "ApprovedTotal",
        "SALARYRANGEMAXIMUM" => "SalaryRangeMaximum",
        _ => "ApprovedMaximum"
    };

    private static async Task<IEnumerable<RecruitmentOffer>> OfferRowsAsync(MySqlConnection db, AuthUser user, long? applicationId, long[]? applicationIds) =>
        await db.QueryAsync<RecruitmentOffer>(@"SELECT o.*,CONCAT(c.FirstName,' ',c.LastName) CandidateName,p.PositionTitle,
COALESCE(t.TemplateName,'') OfferTemplateName FROM recruitment_offers o
JOIN recruitment_candidate_applications a ON a.Id=o.ApplicationId
JOIN recruitment_candidates c ON c.Id=a.CandidateId
JOIN recruitment_open_positions p ON p.Id=a.PositionId
LEFT JOIN recruitment_templates t ON t.Id=o.OfferTemplateId
WHERE (@ClientId IS NULL OR o.ClientId=@ClientId) AND (@ApplicationId IS NULL OR o.ApplicationId=@ApplicationId)
AND (@UseIds=FALSE OR o.ApplicationId IN @Ids) ORDER BY o.UpdatedAt DESC", new { ClientId = user.ClientId, ApplicationId = applicationId, UseIds = applicationIds is { Length: > 0 }, Ids = applicationIds ?? [0L] });

    private static async Task<RecruitmentCandidate?> CandidateByIdAsync(MySqlConnection db, long id) => await db.QueryFirstOrDefaultAsync<RecruitmentCandidate>($"{CandidateSelect} WHERE c.Id=@Id", new { Id = id, ScopeClientId = (int?)null });

    private static async Task<IEnumerable<PersonActivityEvent>> ActivityForCandidateAsync(MySqlConnection db, long candidateId, AuthUser user)
    {
        var events = (await db.QueryAsync<PersonActivityEvent>(@"SELECT p.*,COALESCE(u.DisplayName,u.Email,'System') ActorName FROM person_activity_events p LEFT JOIN authusers u ON u.Id=p.ActorUserId WHERE p.CandidateId=@CandidateId AND (@ClientId IS NULL OR p.ClientId=@ClientId)", new { CandidateId = candidateId, ClientId = user.ClientId })).ToList();
        events.AddRange(await db.QueryAsync<PersonActivityEvent>(@"SELECT l.id Id,l.client_id ClientId,@CandidateId CandidateId,NULL EmployeeId,'DOCUMENTS' ModuleCode,CONCAT('DOCUMENT_',l.action) EventType,CONCAT('Candidate document ',LOWER(l.action)) EventTitle,COALESCE(JSON_UNQUOTE(JSON_EXTRACT(l.metadata_json,'$.publicId')),'') EventSummary,'Attachment' ResourceType,COALESCE(CAST(l.attachment_id AS CHAR),'') ResourceId,l.actor_user_id ActorUserId,COALESCE(u.DisplayName,u.Email,'System') ActorName,'HR' Visibility,TRUE IsSensitive,l.metadata_json MetadataJson,l.created_at_utc OccurredAt,l.created_at_utc CreatedAt FROM attachment_audit_logs l LEFT JOIN authusers u ON u.Id=l.actor_user_id WHERE l.entity_type='CANDIDATE' AND l.entity_id=@CandidateId AND l.success=TRUE", new { CandidateId = candidateId }));
        return events.OrderByDescending(row => row.OccurredAt).ThenByDescending(row => row.Id).Take(1000);
    }

    private static async Task HydrateScoringProfilesAsync(MySqlConnection db, IReadOnlyCollection<RecruitmentAtsScoringProfile> profiles)
    {
        if (profiles.Count == 0) return;
        var ids = profiles.Select(row => row.Id).Where(id => id > 0).ToArray();
        var criteria = ids.Length == 0
            ? new List<RecruitmentAtsScoringCriterion>()
            : (await db.QueryAsync<RecruitmentAtsScoringCriterion>(@"SELECT Id,ScoringProfileId,CriterionCode,CriterionLabel,EvaluationType,Weight,DisplayOrder,IsActive FROM recruitment_ats_profile_criteria WHERE ScoringProfileId IN @Ids ORDER BY ScoringProfileId,DisplayOrder,Id", new { Ids = ids })).ToList();
        var lookup = criteria.GroupBy(row => row.ScoringProfileId).ToDictionary(group => group.Key, group => group.ToList());
        foreach (var profile in profiles)
            profile.Criteria = lookup.GetValueOrDefault(profile.Id) ?? DefaultScoringCriteria();
    }

    private static async Task HydrateResumeIntelligenceAsync(MySqlConnection db, IReadOnlyCollection<RecruitmentCandidateResume> resumes)
    {
        if (resumes.Count == 0) return;
        var ids = resumes.Select(row => row.Id).ToArray();
        var facts = (await db.QueryAsync<RecruitmentResumeParseFacts>("SELECT * FROM recruitment_resume_parse_facts WHERE ResumeId IN @Ids", new { Ids = ids })).ToDictionary(row => row.ResumeId);
        var runs = (await db.QueryAsync<RecruitmentResumeParserRun>("SELECT * FROM recruitment_resume_parser_runs WHERE ResumeId IN @Ids ORDER BY ResumeId,StartedAt DESC,Id DESC", new { Ids = ids })).GroupBy(row => row.ResumeId).ToDictionary(group => group.Key, group => group.ToList());
        var sections = (await db.QueryAsync<RecruitmentResumeSection>("SELECT * FROM recruitment_resume_sections WHERE ResumeId IN @Ids ORDER BY ResumeId,DisplayOrder,Id", new { Ids = ids })).GroupBy(row => row.ResumeId).ToDictionary(group => group.Key, group => group.ToList());
        var skills = (await db.QueryAsync<RecruitmentResumeParsedSkill>("SELECT Id,ResumeId,SkillId,SkillName,MatchedTerm,EvidenceExcerpt,Confidence FROM recruitment_resume_skills WHERE ResumeId IN @Ids ORDER BY ResumeId,Confidence DESC,SkillName", new { Ids = ids })).GroupBy(row => row.ResumeId).ToDictionary(group => group.Key, group => group.ToList());
        foreach (var resume in resumes)
        {
            resume.ParseFacts = facts.GetValueOrDefault(resume.Id);
            resume.ParserRuns = runs.GetValueOrDefault(resume.Id) ?? [];
            resume.Sections = sections.GetValueOrDefault(resume.Id) ?? [];
            resume.ParsedSkills = skills.GetValueOrDefault(resume.Id) ?? [];
            resume.ParsedJson = "{}";
        }
    }

    private static async Task HydrateScoresAsync(MySqlConnection db, IReadOnlyCollection<RecruitmentApplicationScore> scores)
    {
        if (scores.Count == 0) return;
        var ids = scores.Select(row => row.Id).ToArray();
        var components = (await db.QueryAsync<RecruitmentApplicationScoreComponent>("SELECT * FROM recruitment_application_score_components WHERE ApplicationScoreId IN @Ids ORDER BY ApplicationScoreId,DisplayOrder,Id", new { Ids = ids })).GroupBy(row => row.ApplicationScoreId).ToDictionary(group => group.Key, group => group.ToList());
        var skills = (await db.QueryAsync<RecruitmentApplicationScoreSkillMatch>("SELECT * FROM recruitment_application_score_skill_matches WHERE ApplicationScoreId IN @Ids ORDER BY ApplicationScoreId,SkillType,SkillName", new { Ids = ids })).GroupBy(row => row.ApplicationScoreId).ToDictionary(group => group.Key, group => group.ToList());
        var evidence = (await db.QueryAsync<RecruitmentApplicationScoreEvidence>("SELECT * FROM recruitment_application_score_evidence WHERE ApplicationScoreId IN @Ids ORDER BY ApplicationScoreId,CriterionCode,Id", new { Ids = ids })).GroupBy(row => row.ApplicationScoreId).ToDictionary(group => group.Key, group => group.ToList());
        var snapshots = (await db.QueryAsync<RecruitmentApplicationScorePositionSnapshot>("SELECT * FROM recruitment_application_score_position_snapshots WHERE ApplicationScoreId IN @Ids", new { Ids = ids })).ToDictionary(row => row.ApplicationScoreId);
        foreach (var score in scores)
        {
            score.Components = components.GetValueOrDefault(score.Id) ?? [];
            score.SkillMatches = skills.GetValueOrDefault(score.Id) ?? [];
            score.Evidence = evidence.GetValueOrDefault(score.Id) ?? [];
            score.PositionSnapshot = snapshots.GetValueOrDefault(score.Id);
            score.ComponentScoresJson = JsonSerializer.Serialize(score.Components.ToDictionary(row => row.CriterionCode, row => row.AwardedScore, StringComparer.OrdinalIgnoreCase));
            score.MatchedSkillsJson = JsonSerializer.Serialize(score.SkillMatches.Where(row => row.MatchStatus == "Matched").Select(row => row.SkillName).Distinct(StringComparer.OrdinalIgnoreCase));
            score.MissingSkillsJson = JsonSerializer.Serialize(score.SkillMatches.Where(row => row.MatchStatus == "Missing").Select(row => row.SkillName).Distinct(StringComparer.OrdinalIgnoreCase));
            score.ExplanationJson = JsonSerializer.Serialize(new { summary = score.Recommendation, threshold = score.ShortlistThreshold, note = score.ExplanationText });
        }
    }

    private static (List<RecruitmentAtsScoringCriterion> Criteria, string Error) NormalizeScoringCriteria(RecruitmentAtsScoringProfile profile)
    {
        var criteria = (profile.Criteria ?? []).Select(row => new RecruitmentAtsScoringCriterion
        {
            CriterionCode = (row.CriterionCode ?? "").Trim(),
            CriterionLabel = (row.CriterionLabel ?? "").Trim(),
            EvaluationType = (row.EvaluationType ?? "").Trim(),
            Weight = row.Weight,
            DisplayOrder = row.DisplayOrder,
            IsActive = row.IsActive
        }).ToList();
        if (criteria.Count == 0) return ([], "At least one ATS scoring criterion is required.");
        var duplicate = criteria.GroupBy(row => row.CriterionCode, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) return ([], $"ATS criterion '{duplicate.Key}' is duplicated.");
        var unknown = criteria.Where(row => !AtsWeightKeys.Contains(row.CriterionCode, StringComparer.OrdinalIgnoreCase)).Select(row => row.CriterionCode).ToArray();
        if (unknown.Length > 0) return ([], $"Unsupported ATS criteria: {string.Join(", ", unknown)}.");
        if (criteria.Any(row => row.Weight is < 0 or > 100)) return ([], "Every ATS criterion weight must be between 0 and 100.");
        if (criteria.Where(row => row.IsActive).Sum(row => row.Weight) != 100) return ([], "Active ATS criterion weights must total exactly 100.");
        foreach (var criterion in criteria)
        {
            var definition = AtsCriterionDefinitions.First(row => row.Code.Equals(criterion.CriterionCode, StringComparison.OrdinalIgnoreCase));
            criterion.CriterionCode = definition.Code;
            criterion.CriterionLabel = definition.Label;
            criterion.EvaluationType = definition.EvaluationType;
            criterion.DisplayOrder = criterion.DisplayOrder <= 0 ? definition.DisplayOrder : criterion.DisplayOrder;
        }
        return (criteria.OrderBy(row => row.DisplayOrder).ToList(), "");
    }

    private static List<RecruitmentAtsScoringCriterion> DefaultScoringCriteria() => AtsCriterionDefinitions.Select(definition => new RecruitmentAtsScoringCriterion
    {
        CriterionCode = definition.Code,
        CriterionLabel = definition.Label,
        EvaluationType = definition.EvaluationType,
        Weight = definition.DefaultWeight,
        DisplayOrder = definition.DisplayOrder,
        IsActive = true
    }).ToList();

    private static CalculatedSkillMatch ResolveSkillMatch(string originalResumeText, string normalizedResumeText, string skillName, string skillType, IReadOnlyDictionary<string, string[]> aliases, JdSkillScoringRow? requirement = null)
    {
        var normalizedTerm = NormalizeSearch(skillName);
        var group = aliases.FirstOrDefault(pair => NormalizeSearch(pair.Key) == normalizedTerm || pair.Value.Any(alias => NormalizeSearch(alias) == normalizedTerm));
        var candidates = string.IsNullOrWhiteSpace(group.Key) ? new[] { skillName } : group.Value.Append(group.Key).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var matched = candidates.FirstOrDefault(term => ContainsTerm(normalizedResumeText, term)) ?? "";
        return new CalculatedSkillMatch(skillType, skillName, !string.IsNullOrWhiteSpace(matched), matched, string.IsNullOrWhiteSpace(matched) ? "" : ExtractEvidenceExcerpt(originalResumeText, matched), requirement?.WeightPercent ?? 0, requirement?.MinimumYears ?? 0, requirement?.MinimumProficiency ?? "");
    }

    private static decimal SkillRequirementRatio(IReadOnlyCollection<CalculatedSkillMatch> matches)
    {
        if (matches.Count == 0) return 1m;
        var totalWeight = matches.Sum(row => Math.Max(0, row.RequirementWeight));
        return totalWeight > 0
            ? Math.Clamp(matches.Where(row => row.IsMatched).Sum(row => Math.Max(0, row.RequirementWeight)) / totalWeight, 0, 1)
            : Ratio(matches.Count(row => row.IsMatched), matches.Count);
    }

    private static string ExtractEvidenceExcerpt(string text, string term)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(term)) return "";
        var index = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return "";
        var start = Math.Max(0, index - 120);
        var length = Math.Min(text.Length - start, term.Length + 240);
        var excerpt = Regex.Replace(text.Substring(start, length), @"\s+", " ").Trim();
        return excerpt.Length <= 500 ? excerpt : excerpt[..500];
    }

    private static long? EvidenceSectionId(string criterionCode, IReadOnlyDictionary<string, long> sectionIds)
    {
        var preferredCodes = criterionCode switch
        {
            "experience" => new[] { "EXPERIENCE", "SUMMARY" },
            "qualification" => new[] { "EDUCATION", "SUMMARY" },
            "certifications" => new[] { "CERTIFICATIONS", "SKILLS" },
            "roleSimilarity" => new[] { "EXPERIENCE", "SUMMARY" },
            "location" or "noticePeriod" => new[] { "CONTACT", "SUMMARY" },
            _ => new[] { "SKILLS", "SUMMARY" }
        };
        foreach (var code in preferredCodes)
            if (sectionIds.TryGetValue(code, out var id)) return id;
        var fallback = sectionIds.Values.FirstOrDefault();
        return fallback > 0 ? fallback : null;
    }

    private static async Task ExtractCandidateSkillsAsync(MySqlConnection db, RecruitmentCandidate candidate, long resumeId, string text, MySqlTransaction transaction)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var search = NormalizeSearch(text);
        var terms = await db.QueryAsync<SkillDictionaryTermRow>(@"SELECT s.Id SkillId,s.SkillName,s.SkillName MatchTerm FROM recruitment_skills s WHERE s.IsActive=TRUE AND s.ClientId IN (0,@ClientId)
UNION ALL SELECT s.Id SkillId,s.SkillName,a.AliasName MatchTerm FROM recruitment_skills s JOIN recruitment_skill_aliases a ON a.SkillId=s.Id WHERE s.IsActive=TRUE AND s.ClientId IN (0,@ClientId)", new { candidate.ClientId }, transaction);
        foreach (var skill in terms.GroupBy(row => new { row.SkillId, row.SkillName }))
        {
            var matchedTerm = skill.Select(row => row.MatchTerm).FirstOrDefault(term => ContainsTerm(search, term));
            if (string.IsNullOrWhiteSpace(matchedTerm)) continue;
            var evidence = ExtractEvidenceExcerpt(text, matchedTerm);
            await db.ExecuteAsync(@"INSERT INTO recruitment_resume_skills (ResumeId,SkillId,SkillName,MatchedTerm,EvidenceExcerpt,Confidence) VALUES (@ResumeId,@SkillId,@SkillName,@MatchedTerm,@Evidence,0.85) ON DUPLICATE KEY UPDATE MatchedTerm=VALUES(MatchedTerm),EvidenceExcerpt=VALUES(EvidenceExcerpt),Confidence=GREATEST(Confidence,VALUES(Confidence))", new { ResumeId = resumeId, skill.Key.SkillId, skill.Key.SkillName, MatchedTerm = matchedTerm, Evidence = evidence }, transaction);
            await db.ExecuteAsync(@"INSERT INTO recruitment_candidate_skills (CandidateId,SkillId,SkillName,Source,Confidence) VALUES (@CandidateId,@SkillId,@SkillName,'Resume',0.85) ON DUPLICATE KEY UPDATE SkillName=VALUES(SkillName),Confidence=GREATEST(Confidence,VALUES(Confidence)),UpdatedAt=UTC_TIMESTAMP()", new { CandidateId = candidate.Id, skill.Key.SkillId, skill.Key.SkillName }, transaction);
        }
    }

    private static Task ApplyParsedContactAsync(MySqlConnection db, RecruitmentCandidate candidate, ResumeParsedFacts facts, MySqlTransaction transaction)
    {
        return db.ExecuteAsync(@"UPDATE recruitment_candidates SET
Email=CASE WHEN Email='' THEN @Email ELSE Email END,
NormalizedEmail=CASE WHEN NormalizedEmail='' THEN @NormalizedEmail ELSE NormalizedEmail END,
Phone=CASE WHEN Phone='' THEN @Phone ELSE Phone END,
NormalizedPhone=CASE WHEN NormalizedPhone='' THEN @NormalizedPhone ELSE NormalizedPhone END,
TotalExperienceMonths=CASE WHEN TotalExperienceMonths=0 AND @TotalExperienceMonths IS NOT NULL THEN @TotalExperienceMonths ELSE TotalExperienceMonths END,
UpdatedAt=UTC_TIMESTAMP() WHERE Id=@Id", new { candidate.Id, Email = facts.Email, NormalizedEmail = NormalizeEmail(facts.Email), Phone = facts.Phone, NormalizedPhone = NormalizePhone(facts.Phone), facts.TotalExperienceMonths }, transaction);
    }

    private static async Task CreateCandidateChecklistSnapshotAsync(MySqlConnection db, RecruitmentCandidateApplication application)
    {
        var rows = await db.QueryAsync<ChecklistConfigurationRow>(@"SELECT d.Id,d.DocumentName,d.Stage,d.Mandatory,d.AttachmentAttributeId,d.RequiresVerification,d.DueOffsetDays,d.DisplayOrder FROM recruitment_document_checklist d JOIN recruitment_open_positions p ON p.Id=@PositionId WHERE d.IsActive=TRUE AND d.ClientId IN (0,@ClientId) AND (d.HiringType='' OR d.HiringType=p.HiringType) ORDER BY d.ClientId DESC,d.Mandatory DESC,d.DisplayOrder,d.DocumentName", new { application.PositionId, application.ClientId });
        foreach (var row in rows.GroupBy(value => $"{value.Stage}|{value.DocumentName}", StringComparer.OrdinalIgnoreCase).Select(group => group.First()))
            await db.ExecuteAsync(@"INSERT INTO recruitment_candidate_checklist_items (ApplicationId,CandidateId,ChecklistConfigurationId,ChecklistName,Stage,Mandatory,AttachmentAttributeId,RequiresVerification,DueDate,Status,DisplayOrder) VALUES (@ApplicationId,@CandidateId,@Id,@DocumentName,@Stage,@Mandatory,@AttachmentAttributeId,@RequiresVerification,DATE_ADD(UTC_DATE(),INTERVAL @DueOffsetDays DAY),'Pending',@DisplayOrder) ON DUPLICATE KEY UPDATE Mandatory=VALUES(Mandatory),AttachmentAttributeId=VALUES(AttachmentAttributeId),RequiresVerification=VALUES(RequiresVerification),DueDate=VALUES(DueDate),DisplayOrder=VALUES(DisplayOrder)", new { ApplicationId = application.Id, application.CandidateId, row.Id, row.DocumentName, row.Stage, row.Mandatory, row.AttachmentAttributeId, row.RequiresVerification, row.DueOffsetDays, row.DisplayOrder });
    }

    private static async Task RefreshPositionCountersAsync(MySqlConnection db, long positionId)
    {
        await db.ExecuteAsync(@"UPDATE recruitment_open_positions p SET CandidateCount=(SELECT COUNT(*) FROM recruitment_candidate_applications a WHERE a.PositionId=p.Id),InterviewCount=(SELECT COUNT(*) FROM recruitment_interviews i JOIN recruitment_candidate_applications a ON a.Id=i.ApplicationId WHERE a.PositionId=p.Id),OfferCount=(SELECT COUNT(*) FROM recruitment_offers o JOIN recruitment_candidate_applications a ON a.Id=o.ApplicationId WHERE a.PositionId=p.Id),JoinedCount=(SELECT COUNT(*) FROM recruitment_candidate_applications a WHERE a.PositionId=p.Id AND a.CurrentStage='Joined'),FilledPositions=LEAST(ApprovedPositions,(SELECT COUNT(*) FROM recruitment_candidate_applications a WHERE a.PositionId=p.Id AND a.CurrentStage='Joined')),RemainingPositions=GREATEST(0,ApprovedPositions-CancelledPositions-OnHoldPositions-(SELECT COUNT(*) FROM recruitment_candidate_applications a WHERE a.PositionId=p.Id AND a.CurrentStage='Joined')),Status=CASE WHEN (SELECT COUNT(*) FROM recruitment_candidate_applications a WHERE a.PositionId=p.Id AND a.CurrentStage='Joined')>=ApprovedPositions THEN 'Filled' WHEN (SELECT COUNT(*) FROM recruitment_candidate_applications a WHERE a.PositionId=p.Id AND a.CurrentStage='Joined')>0 THEN 'Partially Filled' ELSE Status END,UpdatedAt=UTC_TIMESTAMP() WHERE p.Id=@Id", new { Id = positionId });
    }

    private static async Task<decimal> LatestOfferCtcAsync(MySqlConnection db, long applicationId) => await db.ExecuteScalarAsync<decimal?>("SELECT OfferedCtc FROM recruitment_offers WHERE ApplicationId=@Id AND Status IN ('Accepted','Released','Pending Candidate') ORDER BY UpdatedAt DESC LIMIT 1", new { Id = applicationId }) ?? 0;

    private static async Task<bool> IsConfiguredCandidateStatusAsync(MySqlConnection db, int clientId, string status) =>
        await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM dropdownmasters WHERE Type='Candidate Status' AND Value=@Status AND IsActive=TRUE AND ClientId IN (0,@ClientId)", new { ClientId = clientId, Status = status }) > 0
        || await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM recruitment_master_values WHERE MasterType='Candidate Status' AND Name=@Status AND IsActive=TRUE AND ClientId IN (0,@ClientId)", new { ClientId = clientId, Status = status }) > 0;

    private static async Task<bool> HasResumeAsync(MySqlConnection db, long candidateId, long? applicationResumeId) =>
        await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*)
FROM recruitment_candidate_resumes resume
JOIN entity_attachments attachment ON attachment.public_id=resume.AttachmentPublicId
 AND attachment.entity_type='CANDIDATE' AND attachment.entity_id=resume.CandidateId
 AND attachment.is_current=TRUE AND attachment.is_deleted=FALSE
WHERE resume.CandidateId=@CandidateId AND (@ResumeId IS NULL OR resume.Id=@ResumeId)", new { CandidateId = candidateId, ResumeId = applicationResumeId }) > 0;

    private static async Task<RecruitmentFeatureSettings> FeatureSettingsAsync(MySqlConnection db, int clientId) =>
        await db.QueryFirstOrDefaultAsync<RecruitmentFeatureSettings>(@"SELECT EnableTalentPool,EnableResumeParsing,EnableAtsScoring,EnableOfferApproval,RequireResumeForApplication,AllowManualScoreOverride,AutoCreateApplicationFromReferral,DefaultAtsScoringProfileId FROM recruitment_settings WHERE ClientId=@ClientId AND IsActive=TRUE LIMIT 1", new { ClientId = clientId })
        ?? new RecruitmentFeatureSettings();

    private static Task AddPositionTimelineAsync(MySqlConnection db, long positionId, string eventType, string title, string details, int? userId) =>
        db.ExecuteAsync("INSERT INTO recruitment_position_timeline (PositionId,EventType,EventTitle,EventDetails,ActorUserId) VALUES (@PositionId,@EventType,@Title,@Details,@UserId)", new { PositionId = positionId, EventType = eventType, Title = title, Details = details ?? "", UserId = userId });

    private static Task WriteRecruitmentAuditAsync(MySqlConnection db, string entityType, long entityId, string action, int userId, object payload) =>
        db.ExecuteAsync("INSERT INTO recruitment_audit (EntityType,EntityId,Action,NewValueJson,ChangedByUserId) VALUES (@EntityType,@EntityId,@Action,@Json,@UserId)", new { EntityType = entityType, EntityId = entityId, Action = action, Json = JsonSerializer.Serialize(payload), UserId = userId });

    private static Task WriteActivityAsync(MySqlConnection db, int clientId, long? candidateId, int? employeeId, string module, string eventType, string title, string summary, string resourceType, string resourceId, AuthUser user, string metadataJson = "{}") =>
        db.ExecuteAsync(@"INSERT INTO person_activity_events (ClientId,CandidateId,EmployeeId,ModuleCode,EventType,EventTitle,EventSummary,ResourceType,ResourceId,ActorUserId,Visibility,IsSensitive,MetadataJson,OccurredAt) VALUES (@ClientId,@CandidateId,@EmployeeId,@Module,@EventType,@Title,@Summary,@ResourceType,@ResourceId,@UserId,'HR',FALSE,@MetadataJson,UTC_TIMESTAMP())", new { ClientId = clientId, CandidateId = candidateId, EmployeeId = employeeId, Module = module, EventType = eventType, Title = title, Summary = summary ?? "", ResourceType = resourceType, ResourceId = resourceId, UserId = user.Id, MetadataJson = ValidJson(metadataJson, "{}") });

    private static bool CanAccessClient(AuthUser user, int clientId) => user.ClientId is null || user.ClientId == clientId;
    private static async Task<bool> CanAccessCandidateAsync(MySqlConnection db, AuthUser user, RecruitmentCandidate candidate)
    {
        if (user.ClientId is null || user.ClientId == candidate.ClientId) return true;
        return await db.ExecuteScalarAsync<int>(@"SELECT CASE WHEN EXISTS (
    SELECT 1 FROM recruitment_candidate_applications a WHERE a.CandidateId=@CandidateId AND a.ClientId=@ClientId
) OR EXISTS (
    SELECT 1 FROM recruitment_employee_referrals r JOIN recruitment_open_positions p ON p.Id=r.PositionId
    WHERE r.CandidateId=@CandidateId AND p.ClientId=@ClientId
) THEN 1 ELSE 0 END", new { CandidateId = candidate.Id, ClientId = user.ClientId.Value }) == 1;
    }

    private static string NormalizeEmail(string value) => value?.Trim().ToLowerInvariant() ?? "";
    private static string NormalizePhone(string value) => new((value ?? "").Where(char.IsDigit).ToArray());
    private static string NormalizeCode(string value) => Regex.Replace((value ?? "").Trim().ToUpperInvariant(), @"[^A-Z0-9]+", "_").Trim('_');
    private static string NormalizeSearch(string value) => Regex.Replace((value ?? "").ToLowerInvariant(), @"[^a-z0-9+#.]+", " ").Trim();
    private static bool ContainsTerm(string normalizedText, string term)
    {
        var normalized = NormalizeSearch(term);
        return !string.IsNullOrWhiteSpace(normalized) && $" {normalizedText} ".Contains($" {normalized} ", StringComparison.OrdinalIgnoreCase);
    }
    private static List<string> SplitTerms(string value) => (value ?? "").Split([',', ';', '\n', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(item => item.Length > 1).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    private static decimal Ratio(int value, int total) => total <= 0 ? 1m : Math.Clamp((decimal)value / total, 0, 1);
    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
    private static decimal TextCriterionScore(string text, string expected, string candidateValue)
    {
        var terms = SplitTerms(expected);
        if (terms.Count == 0) return 1;
        var candidateText = $"{text} {NormalizeSearch(candidateValue)}";
        return Ratio(terms.Count(term => ContainsTerm(candidateText, term)), terms.Count);
    }
    private static decimal ExperienceRatio(int months, string range)
    {
        var numbers = Regex.Matches(range ?? "", @"\d+").Select(match => int.Parse(match.Value, CultureInfo.InvariantCulture)).ToList();
        if (numbers.Count == 0) return 1;
        var minimumMonths = numbers[0] * 12;
        if (minimumMonths <= 0) return 1;
        return Math.Clamp((decimal)months / minimumMonths, 0, 1);
    }
    private static decimal TokenSimilarity(string left, string right)
    {
        var a = NormalizeSearch(left).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var b = NormalizeSearch(right).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return a.Count == 0 || b.Count == 0 ? 0 : (decimal)a.Intersect(b, StringComparer.OrdinalIgnoreCase).Count() / a.Union(b, StringComparer.OrdinalIgnoreCase).Count();
    }
    private static Dictionary<string, decimal> ReadLegacyWeights(string json)
    {
        try
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, decimal>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return values is null ? new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) : new Dictionary<string, decimal>(values, StringComparer.OrdinalIgnoreCase);
        }
        catch { return DefaultScoringCriteria().ToDictionary(row => row.CriterionCode, row => row.Weight, StringComparer.OrdinalIgnoreCase); }
    }
    private static List<string> ReadStringList(string json)
    {
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch { return []; }
    }
    private static string ValidJson(string value, string fallback)
    {
        try { using var _ = JsonDocument.Parse(string.IsNullOrWhiteSpace(value) ? fallback : value); return string.IsNullOrWhiteSpace(value) ? fallback : value; }
        catch { return fallback; }
    }
    private static (string FirstName, string LastName) SplitName(string value)
    {
        var parts = (value ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? ("Candidate", "") : (parts[0], string.Join(' ', parts.Skip(1)));
    }

    private static async Task<string> NextNumberAsync(MySqlConnection db, int clientId, string series, string prefix)
    {
        var next = await db.ExecuteScalarAsync<int>(@"INSERT INTO recruitment_number_sequences (ClientId,SeriesCode,LastNumber) VALUES (@ClientId,@Series,0) ON DUPLICATE KEY UPDATE LastNumber=LastNumber; UPDATE recruitment_number_sequences SET LastNumber=LAST_INSERT_ID(LastNumber+1) WHERE ClientId=@ClientId AND SeriesCode=@Series; SELECT LAST_INSERT_ID();", new { ClientId = clientId, Series = series });
        var code = await db.ExecuteScalarAsync<string>("SELECT COALESCE(NULLIF(Code,''),CONCAT('C',Id)) FROM clients WHERE Id=@Id", new { Id = clientId }) ?? $"C{clientId}";
        return $"{prefix}-{code}-{next:D6}";
    }

    private static async Task SeedAttachmentConfigurationsAsync(MySqlConnection db)
    {
        var attachmentTable = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name='attachment_attributes'");
        if (attachmentTable == 0) return;
        var resumeAttributeId = await db.ExecuteScalarAsync<long>(@"INSERT INTO attachment_attributes (client_id,attribute_code,attribute_name,description,data_classification,is_active) VALUES (0,'RESUME','Resume','Candidate resume/CV stored through the global document system.','Restricted',TRUE) ON DUPLICATE KEY UPDATE id=LAST_INSERT_ID(id),attribute_name=VALUES(attribute_name),description=VALUES(description),data_classification=VALUES(data_classification),is_active=TRUE;SELECT LAST_INSERT_ID();");
        foreach (var form in new[] { "CANDIDATE_APPLICATION", "EMPLOYEE_REFERRAL" })
            await db.ExecuteAsync(@"INSERT INTO attachment_field_configurations (client_id,attachment_attribute_id,module_code,form_code,section_code,field_key,field_label,help_text,is_required,allow_multiple,minimum_file_count,maximum_file_count,allowed_extensions_json,allowed_mime_types_json,maximum_file_size_bytes,owner_can_view,owner_can_upload,owner_can_replace,owner_can_delete,requires_verification,versioning_enabled,requirement_scope,display_order,is_active) VALUES (0,@AttributeId,'RECRUITMENT',@FormCode,'DOCUMENTS','RESUME','Resume / CV','PDF, DOCX, RTF or TXT. The file is stored privately and parsed for ATS matching.',TRUE,FALSE,1,1,@Extensions,@Mimes,10485760,TRUE,TRUE,TRUE,FALSE,FALSE,TRUE,'AllEntities',10,TRUE) ON DUPLICATE KEY UPDATE attachment_attribute_id=VALUES(attachment_attribute_id),field_label=VALUES(field_label),help_text=VALUES(help_text),allowed_extensions_json=VALUES(allowed_extensions_json),allowed_mime_types_json=VALUES(allowed_mime_types_json),maximum_file_size_bytes=VALUES(maximum_file_size_bytes),owner_can_view=TRUE,owner_can_upload=TRUE,owner_can_replace=TRUE,is_active=TRUE", new { AttributeId = resumeAttributeId, FormCode = form, Extensions = "[\"pdf\",\"docx\",\"rtf\",\"txt\"]", Mimes = "[\"application/pdf\",\"application/vnd.openxmlformats-officedocument.wordprocessingml.document\",\"application/rtf\",\"text/rtf\",\"text/plain\"]" });
        var offerAttributeId = await db.ExecuteScalarAsync<long>(@"INSERT INTO attachment_attributes (client_id,attribute_code,attribute_name,description,data_classification,is_active) VALUES (0,'OFFER_LETTER','Offer letter','Generated or signed offer letter.','Restricted',TRUE) ON DUPLICATE KEY UPDATE id=LAST_INSERT_ID(id),is_active=TRUE;SELECT LAST_INSERT_ID();");
        await db.ExecuteAsync(@"INSERT INTO attachment_field_configurations (client_id,attachment_attribute_id,module_code,form_code,section_code,field_key,field_label,help_text,is_required,allow_multiple,minimum_file_count,maximum_file_count,allowed_extensions_json,allowed_mime_types_json,maximum_file_size_bytes,owner_can_view,owner_can_upload,owner_can_replace,owner_can_delete,requires_verification,versioning_enabled,requirement_scope,display_order,is_active) VALUES (0,@AttributeId,'RECRUITMENT','PRE_ONBOARDING','DOCUMENTS','OFFER_LETTER','Offer letter','Offer letter managed through the global document system.',FALSE,TRUE,0,50,'[""pdf""]','[""application/pdf""]',10485760,FALSE,FALSE,FALSE,FALSE,FALSE,TRUE,'AllEntities',10,TRUE) ON DUPLICATE KEY UPDATE attachment_attribute_id=VALUES(attachment_attribute_id),allow_multiple=TRUE,maximum_file_count=50,owner_can_view=FALSE,owner_can_upload=FALSE,owner_can_replace=FALSE,owner_can_delete=FALSE,versioning_enabled=TRUE,is_active=TRUE", new { AttributeId = offerAttributeId });
    }

    private static async Task SeedScoringProfilesAsync(MySqlConnection db) => await db.ExecuteAsync(@"INSERT INTO recruitment_ats_scoring_profiles (ClientId,ProfileName,PositionCategory,ScoringMethod,MinimumShortlistScore,AutoScoreOnResumeUpload,AllowManualOverride,ParserProvider,ScoringProvider,ModelName,VersionNumber,IsDefault,IsActive) SELECT s.ClientId,'Default ATS profile','','RuleBased',60,TRUE,TRUE,'BuiltIn','BuiltIn','Deterministic-v1',1,TRUE,TRUE FROM recruitment_settings s WHERE s.RecruitmentEnabled=TRUE AND NOT EXISTS (SELECT 1 FROM recruitment_ats_scoring_profiles p WHERE p.ClientId=s.ClientId) ");

    private static async Task SeedMissingScoringProfileCriteriaAsync(MySqlConnection db)
    {
        await using var transaction = await db.BeginTransactionAsync();
        var profileIds = await db.QueryAsync<long>(@"SELECT p.Id FROM recruitment_ats_scoring_profiles p
WHERE NOT EXISTS (SELECT 1 FROM recruitment_ats_profile_criteria c WHERE c.ScoringProfileId=p.Id)", transaction: transaction);
        foreach (var profileId in profileIds)
        foreach (var definition in AtsCriterionDefinitions)
            await db.ExecuteAsync(@"INSERT INTO recruitment_ats_profile_criteria
(ScoringProfileId,CriterionCode,CriterionLabel,EvaluationType,Weight,DisplayOrder,IsActive)
VALUES (@ProfileId,@Code,@Label,@EvaluationType,@Weight,@DisplayOrder,TRUE)", new
            {
                ProfileId = profileId,
                definition.Code,
                definition.Label,
                definition.EvaluationType,
                Weight = definition.DefaultWeight,
                definition.DisplayOrder
            }, transaction);
        await transaction.CommitAsync();
    }

    private static async Task MigrateLegacyRecruitmentIntelligenceAsync(MySqlConnection db)
    {
        var hasLegacyWeights = await ColumnExistsAsync(db, "recruitment_ats_scoring_profiles", "WeightsJson");
        await using var profileTransaction = await db.BeginTransactionAsync();
        var profiles = hasLegacyWeights
            ? await db.QueryAsync<LegacyAtsProfileRow>(@"SELECT Id,CAST(WeightsJson AS CHAR) WeightsJson FROM recruitment_ats_scoring_profiles p WHERE NOT EXISTS (SELECT 1 FROM recruitment_ats_profile_criteria c WHERE c.ScoringProfileId=p.Id)", transaction: profileTransaction)
            : await db.QueryAsync<LegacyAtsProfileRow>(@"SELECT Id,'' WeightsJson FROM recruitment_ats_scoring_profiles p WHERE NOT EXISTS (SELECT 1 FROM recruitment_ats_profile_criteria c WHERE c.ScoringProfileId=p.Id)", transaction: profileTransaction);
        foreach (var profile in profiles)
        {
            var weights = hasLegacyWeights ? ReadLegacyWeights(profile.WeightsJson) : DefaultScoringCriteria().ToDictionary(row => row.CriterionCode, row => row.Weight, StringComparer.OrdinalIgnoreCase);
            if (AtsWeightKeys.Any(key => !weights.ContainsKey(key)) || weights.Values.Sum() != 100)
                weights = DefaultScoringCriteria().ToDictionary(row => row.CriterionCode, row => row.Weight, StringComparer.OrdinalIgnoreCase);
            foreach (var definition in AtsCriterionDefinitions)
                await db.ExecuteAsync(@"INSERT INTO recruitment_ats_profile_criteria (ScoringProfileId,CriterionCode,CriterionLabel,EvaluationType,Weight,DisplayOrder,IsActive) VALUES (@ProfileId,@Code,@Label,@EvaluationType,@Weight,@DisplayOrder,TRUE) ON DUPLICATE KEY UPDATE CriterionLabel=VALUES(CriterionLabel),EvaluationType=VALUES(EvaluationType),Weight=VALUES(Weight),DisplayOrder=VALUES(DisplayOrder),IsActive=TRUE", new { ProfileId = profile.Id, definition.Code, definition.Label, definition.EvaluationType, Weight = weights.GetValueOrDefault(definition.Code, definition.DefaultWeight), definition.DisplayOrder }, profileTransaction);
        }
        await profileTransaction.CommitAsync();
        var resumes = await db.QueryAsync<LegacyResumeMigrationRow>(@"SELECT r.Id,r.CandidateId,COALESCE(r.ParsedText,'') ParsedText,CAST(COALESCE(r.ParsedJson,JSON_OBJECT()) AS CHAR) ParsedJson,r.ParserName,r.ParserVersion,r.ParsingStatus,COALESCE(r.ParsingError,'') ParsingError,r.ParsedAt FROM recruitment_candidate_resumes r WHERE NOT EXISTS (SELECT 1 FROM recruitment_resume_parse_facts f WHERE f.ResumeId=r.Id) OR (r.ParsedText<>'' AND NOT EXISTS (SELECT 1 FROM recruitment_resume_sections s WHERE s.ResumeId=r.Id))");
        foreach (var resume in resumes)
        {
            var contact = ReadLegacyResumeContact(resume.ParsedJson);
            var characterCount = resume.ParsedText.Length;
            var lineCount = resume.ParsedText.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
            var summary = Regex.Replace(resume.ParsedText, @"\s+", " ").Trim();
            if (summary.Length > 1000) summary = summary[..1000];
            await db.ExecuteAsync(@"INSERT INTO recruitment_resume_parse_facts (ResumeId,ExtractedEmail,ExtractedPhone,CharacterCount,LineCount,LanguageCode,SummaryText) VALUES (@ResumeId,@Email,@Phone,@CharacterCount,@LineCount,'und',@Summary) ON DUPLICATE KEY UPDATE ExtractedEmail=VALUES(ExtractedEmail),ExtractedPhone=VALUES(ExtractedPhone),CharacterCount=VALUES(CharacterCount),LineCount=VALUES(LineCount),SummaryText=VALUES(SummaryText),UpdatedAt=UTC_TIMESTAMP()", new { ResumeId = resume.Id, contact.Email, contact.Phone, CharacterCount = characterCount, LineCount = lineCount, Summary = summary });
            if (!string.IsNullOrWhiteSpace(resume.ParsedText))
                await db.ExecuteAsync(@"INSERT INTO recruitment_resume_sections (ResumeId,SectionCode,Heading,Content,DisplayOrder,Confidence) SELECT @ResumeId,'GENERAL','Imported resume text',@Content,10,0.40 WHERE NOT EXISTS (SELECT 1 FROM recruitment_resume_sections WHERE ResumeId=@ResumeId)", new { ResumeId = resume.Id, Content = resume.ParsedText });
            await db.ExecuteAsync(@"INSERT INTO recruitment_resume_parser_runs (ResumeId,ParserName,ParserVersion,ParseStatus,ExtractedCharacterCount,ExtractedLineCount,ErrorMessage,StartedAt,CompletedAt) SELECT @ResumeId,@ParserName,@ParserVersion,@Status,@CharacterCount,@LineCount,@Error,COALESCE(@ParsedAt,UTC_TIMESTAMP()),COALESCE(@ParsedAt,UTC_TIMESTAMP()) WHERE NOT EXISTS (SELECT 1 FROM recruitment_resume_parser_runs WHERE ResumeId=@ResumeId)", new { ResumeId = resume.Id, resume.ParserName, resume.ParserVersion, Status = resume.ParsingStatus, CharacterCount = characterCount, LineCount = lineCount, Error = resume.ParsingError, resume.ParsedAt });
        }
        await db.ExecuteAsync(@"INSERT INTO recruitment_resume_skills (ResumeId,SkillId,SkillName,MatchedTerm,EvidenceExcerpt,Confidence)
SELECT r.Id,cs.SkillId,cs.SkillName,cs.SkillName,'',cs.Confidence FROM recruitment_candidate_resumes r JOIN recruitment_candidate_skills cs ON cs.CandidateId=r.CandidateId AND cs.Source='Resume' WHERE r.IsPrimary=TRUE AND cs.SkillId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM recruitment_resume_skills rs WHERE rs.ResumeId=r.Id AND rs.SkillId=cs.SkillId)");
        await db.ExecuteAsync(@"UPDATE recruitment_candidate_resumes r SET ParsedJson=JSON_OBJECT() WHERE EXISTS (SELECT 1 FROM recruitment_resume_parse_facts f WHERE f.ResumeId=r.Id)");

        var legacyScores = await db.QueryAsync<LegacyScoreMigrationRow>(@"SELECT s.Id,s.ApplicationId,s.ResumeId,s.ScoringProfileId,s.TotalScore,s.ModelVersion,CAST(COALESCE(s.ComponentScoresJson,JSON_OBJECT()) AS CHAR) ComponentScoresJson,CAST(COALESCE(s.MatchedSkillsJson,JSON_ARRAY()) AS CHAR) MatchedSkillsJson,CAST(COALESCE(s.MissingSkillsJson,JSON_ARRAY()) AS CHAR) MissingSkillsJson,CAST(COALESCE(s.ExplanationJson,JSON_OBJECT()) AS CHAR) ExplanationJson FROM recruitment_application_scores s WHERE NOT EXISTS (SELECT 1 FROM recruitment_application_score_components c WHERE c.ApplicationScoreId=s.Id)");
        foreach (var score in legacyScores)
        {
            var profileCriteria = score.ScoringProfileId.HasValue
                ? (await db.QueryAsync<RecruitmentAtsScoringCriterion>("SELECT * FROM recruitment_ats_profile_criteria WHERE ScoringProfileId=@Id AND IsActive=TRUE ORDER BY DisplayOrder", new { Id = score.ScoringProfileId.Value })).ToList()
                : DefaultScoringCriteria();
            if (profileCriteria.Count == 0) profileCriteria = DefaultScoringCriteria();
            var componentScores = ReadLegacyComponentScores(score.ComponentScoresJson);
            foreach (var criterion in profileCriteria)
            {
                var awarded = componentScores.GetValueOrDefault(criterion.CriterionCode, Round(score.TotalScore * criterion.Weight / 100m));
                var ratio = criterion.Weight <= 0 ? 0 : Math.Clamp(awarded / criterion.Weight, 0, 1);
                await db.ExecuteAsync(@"INSERT INTO recruitment_application_score_components (ApplicationScoreId,CriterionCode,CriterionLabel,Weight,RawRatio,AwardedScore,MaximumScore,EvidenceSummary,DisplayOrder) VALUES (@ScoreId,@Code,@Label,@Weight,@Ratio,@Awarded,@Weight,'Migrated from legacy ATS score',@DisplayOrder) ON DUPLICATE KEY UPDATE CriterionLabel=VALUES(CriterionLabel),Weight=VALUES(Weight),RawRatio=VALUES(RawRatio),AwardedScore=VALUES(AwardedScore),MaximumScore=VALUES(MaximumScore),EvidenceSummary=VALUES(EvidenceSummary),DisplayOrder=VALUES(DisplayOrder)", new { ScoreId = score.Id, Code = criterion.CriterionCode, Label = criterion.CriterionLabel, criterion.Weight, Ratio = Round(ratio), Awarded = Round(awarded), criterion.DisplayOrder });
            }
            foreach (var skill in ReadStringList(score.MatchedSkillsJson).Distinct(StringComparer.OrdinalIgnoreCase))
                await db.ExecuteAsync(@"INSERT IGNORE INTO recruitment_application_score_skill_matches (ApplicationScoreId,SkillType,SkillName,MatchStatus,MatchedTerm,EvidenceExcerpt,Confidence) VALUES (@ScoreId,'Required',@Skill,'Matched',@Skill,'',0.75)", new { ScoreId = score.Id, Skill = skill });
            foreach (var skill in ReadStringList(score.MissingSkillsJson).Distinct(StringComparer.OrdinalIgnoreCase))
                await db.ExecuteAsync(@"INSERT IGNORE INTO recruitment_application_score_skill_matches (ApplicationScoreId,SkillType,SkillName,MatchStatus,MatchedTerm,EvidenceExcerpt,Confidence) VALUES (@ScoreId,'Required',@Skill,'Missing','','',0)", new { ScoreId = score.Id, Skill = skill });
            var explanation = ReadLegacyScoreExplanation(score.ExplanationJson);
            var threshold = explanation.Threshold > 0 ? explanation.Threshold : await db.ExecuteScalarAsync<decimal?>("SELECT MinimumShortlistScore FROM recruitment_ats_scoring_profiles WHERE Id=@Id", new { Id = score.ScoringProfileId }) ?? 60m;
            var recommendation = string.IsNullOrWhiteSpace(explanation.Summary) ? (score.TotalScore >= threshold ? "Review for shortlist" : "Below shortlist threshold") : explanation.Summary;
            await db.ExecuteAsync(@"UPDATE recruitment_application_scores SET ShortlistThreshold=@Threshold,Recommendation=@Recommendation,ExplanationText='Migrated to normalized ATS evidence. Human recruiter review remains mandatory.',ProfileVersionNumber=CASE WHEN ProfileVersionNumber=0 THEN @ProfileVersion ELSE ProfileVersionNumber END,HumanReviewRequired=TRUE WHERE Id=@Id", new { score.Id, Threshold = threshold, Recommendation = recommendation, ProfileVersion = int.TryParse(score.ModelVersion, out var version) ? version : 1 });
            await db.ExecuteAsync(@"INSERT INTO recruitment_application_score_position_snapshots (ApplicationScoreId,PositionId,PositionCode,PositionTitle,PositionCategory,RequiredSkills,PreferredSkills,ExperienceRange,Qualification,Certifications,JobLocation)
SELECT @ScoreId,p.Id,p.PositionCode,p.PositionTitle,p.PositionCategory,p.RequiredSkills,p.PreferredSkills,p.ExperienceRange,r.Qualification,r.Certifications,p.JobLocation FROM recruitment_candidate_applications a JOIN recruitment_open_positions p ON p.Id=a.PositionId JOIN recruitment_requisitions r ON r.Id=p.RequisitionId WHERE a.Id=@ApplicationId ON DUPLICATE KEY UPDATE ApplicationScoreId=VALUES(ApplicationScoreId)", new { ScoreId = score.Id, score.ApplicationId });
            await db.ExecuteAsync(@"INSERT INTO recruitment_application_score_evidence (ApplicationScoreId,CriterionCode,EvidenceType,ExpectedValue,ActualValue,MatchStatus,Confidence) SELECT @ScoreId,'legacyMigration','LegacyScore','','Score migrated from legacy ATS snapshot','ReviewRequired',0 WHERE NOT EXISTS (SELECT 1 FROM recruitment_application_score_evidence WHERE ApplicationScoreId=@ScoreId)", new { ScoreId = score.Id });
        }
        await db.ExecuteAsync(@"UPDATE recruitment_application_scores s SET PositionSnapshotJson=NULL,ComponentScoresJson=JSON_OBJECT(),MatchedSkillsJson=JSON_ARRAY(),MissingSkillsJson=JSON_ARRAY(),ExplanationJson=JSON_OBJECT() WHERE EXISTS (SELECT 1 FROM recruitment_application_score_components c WHERE c.ApplicationScoreId=s.Id)");
    }

    private static Dictionary<string, decimal> ReadLegacyComponentScores(string json)
    {
        try
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, decimal>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return values is null ? new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) : new Dictionary<string, decimal>(values, StringComparer.OrdinalIgnoreCase);
        }
        catch { return new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase); }
    }

    private static (string Email, string Phone) ReadLegacyResumeContact(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            var email = document.RootElement.TryGetProperty("email", out var emailNode) ? emailNode.GetString() ?? "" : "";
            var phone = document.RootElement.TryGetProperty("phone", out var phoneNode) ? phoneNode.GetString() ?? "" : "";
            return (email, phone);
        }
        catch { return ("", ""); }
    }

    private static (string Summary, decimal Threshold) ReadLegacyScoreExplanation(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            var summary = document.RootElement.TryGetProperty("summary", out var summaryNode) ? summaryNode.GetString() ?? "" : "";
            var threshold = document.RootElement.TryGetProperty("threshold", out var thresholdNode) && thresholdNode.TryGetDecimal(out var value) ? value : 0;
            return (summary, threshold);
        }
        catch { return ("", 0); }
    }

    private static async Task EnsureNormalizedAtsForeignKeysAsync(MySqlConnection db)
    {
        var keys = new (string Table, string Name, string Column, string Parent, string ParentColumn, string DeleteRule)[]
        {
            ("recruitment_candidate_resumes","FK_resume_candidate","CandidateId","recruitment_candidates","Id","CASCADE"),
            ("recruitment_resume_parser_runs","FK_resume_parser_run_resume","ResumeId","recruitment_candidate_resumes","Id","CASCADE"),
            ("recruitment_resume_parse_facts","FK_resume_parse_facts_resume","ResumeId","recruitment_candidate_resumes","Id","CASCADE"),
            ("recruitment_resume_sections","FK_resume_section_resume","ResumeId","recruitment_candidate_resumes","Id","CASCADE"),
            ("recruitment_resume_skills","FK_resume_skill_resume","ResumeId","recruitment_candidate_resumes","Id","CASCADE"),
            ("recruitment_resume_skills","FK_resume_skill_master","SkillId","recruitment_skills","Id","SET NULL"),
            ("recruitment_skill_aliases","FK_recruitment_skill_alias_skill","SkillId","recruitment_skills","Id","CASCADE"),
            ("recruitment_candidate_skills","FK_recruitment_candidate_skill_candidate","CandidateId","recruitment_candidates","Id","CASCADE"),
            ("recruitment_candidate_skills","FK_recruitment_candidate_skill_master","SkillId","recruitment_skills","Id","SET NULL"),
            ("recruitment_candidate_applications","FK_recruitment_application_candidate","CandidateId","recruitment_candidates","Id","RESTRICT"),
            ("recruitment_ats_profile_criteria","FK_ats_criterion_profile","ScoringProfileId","recruitment_ats_scoring_profiles","Id","CASCADE"),
            ("recruitment_application_scores","FK_ats_score_application","ApplicationId","recruitment_candidate_applications","Id","CASCADE"),
            ("recruitment_application_scores","FK_ats_score_resume","ResumeId","recruitment_candidate_resumes","Id","RESTRICT"),
            ("recruitment_application_scores","FK_ats_score_profile","ScoringProfileId","recruitment_ats_scoring_profiles","Id","SET NULL"),
            ("recruitment_application_score_components","FK_ats_component_score","ApplicationScoreId","recruitment_application_scores","Id","CASCADE"),
            ("recruitment_application_score_skill_matches","FK_ats_skill_score","ApplicationScoreId","recruitment_application_scores","Id","CASCADE"),
            ("recruitment_application_score_evidence","FK_ats_evidence_score","ApplicationScoreId","recruitment_application_scores","Id","CASCADE"),
            ("recruitment_application_score_evidence","FK_ats_evidence_resume_section","ResumeSectionId","recruitment_resume_sections","Id","SET NULL"),
            ("recruitment_application_score_position_snapshots","FK_ats_snapshot_score","ApplicationScoreId","recruitment_application_scores","Id","CASCADE"),
            ("recruitment_application_score_position_snapshots","FK_ats_snapshot_position","PositionId","recruitment_open_positions","Id","RESTRICT")
        };
        foreach (var key in keys)
            await EnsureNormalizedAtsForeignKeyAsync(db, key.Table, key.Name, key.Column, key.Parent, key.ParentColumn, key.DeleteRule);
    }

    private static async Task EnsureApplicationResumeIntegrityAsync(MySqlConnection db)
    {
        await db.ExecuteAsync(@"UPDATE recruitment_candidate_applications applicationRow
LEFT JOIN recruitment_candidate_resumes resume ON resume.Id=applicationRow.ResumeId AND resume.CandidateId=applicationRow.CandidateId
SET applicationRow.ResumeId=NULL
WHERE applicationRow.ResumeId IS NOT NULL AND resume.Id IS NULL");

        var indexExists = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM information_schema.statistics
WHERE table_schema=DATABASE() AND table_name='recruitment_candidate_resumes' AND index_name='UX_recruitment_resume_candidate_link'");
        if (indexExists == 0)
            await db.ExecuteAsync("ALTER TABLE recruitment_candidate_resumes ADD UNIQUE KEY UX_recruitment_resume_candidate_link (Id,CandidateId)");

        var constraintExists = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM information_schema.table_constraints
WHERE constraint_schema=DATABASE() AND table_name='recruitment_candidate_applications'
 AND constraint_name='FK_recruitment_application_resume_candidate' AND constraint_type='FOREIGN KEY'");
        if (constraintExists == 0)
            await db.ExecuteAsync(@"ALTER TABLE recruitment_candidate_applications
ADD CONSTRAINT FK_recruitment_application_resume_candidate FOREIGN KEY (ResumeId,CandidateId)
REFERENCES recruitment_candidate_resumes (Id,CandidateId) ON DELETE RESTRICT");
    }

    private static async Task DropObsoleteAtsConfigurationColumnsAsync(MySqlConnection db)
    {
        var invalidProfiles = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*)
FROM recruitment_ats_scoring_profiles profile
LEFT JOIN (
    SELECT ScoringProfileId,COUNT(*) CriterionCount,
           SUM(CASE WHEN IsActive=TRUE THEN Weight ELSE 0 END) ActiveWeight
    FROM recruitment_ats_profile_criteria GROUP BY ScoringProfileId
) criteria ON criteria.ScoringProfileId=profile.Id
WHERE COALESCE(criteria.CriterionCount,0)=0 OR ABS(COALESCE(criteria.ActiveWeight,0)-100)>0.001");
        if (invalidProfiles > 0)
            throw new InvalidOperationException("Legacy ATS configuration was not removed because one or more scoring profiles could not be normalized to relational criteria totaling 100%.");
        foreach (var column in new[] { "WeightsJson", "ConfigurationJson" })
        {
            if (await ColumnExistsAsync(db, "recruitment_ats_scoring_profiles", column))
                await db.ExecuteAsync($"ALTER TABLE recruitment_ats_scoring_profiles DROP COLUMN `{column}`");
        }
    }

    private static async Task<bool> ColumnExistsAsync(MySqlConnection db, string table, string column) =>
        await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM information_schema.columns
WHERE table_schema=DATABASE() AND table_name=@Table AND LOWER(column_name)=LOWER(@Column)", new { Table = table, Column = column }) > 0;

    private static async Task EnsureNormalizedAtsForeignKeyAsync(MySqlConnection db, string table, string constraint, string column, string parentTable, string parentColumn, string deleteRule)
    {
        var tablesExist = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name IN (@Table,@Parent)", new { Table = table, Parent = parentTable });
        if (tablesExist != 2) return;
        var exists = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM information_schema.table_constraints WHERE constraint_schema=DATABASE() AND table_name=@Table AND constraint_name=@Constraint AND constraint_type='FOREIGN KEY'", new { Table = table, Constraint = constraint });
        if (exists > 0) return;
        var orphanCount = await db.ExecuteScalarAsync<long>($@"SELECT COUNT(*) FROM `{table}` child LEFT JOIN `{parentTable}` parent ON parent.`{parentColumn}`=child.`{column}` WHERE child.`{column}` IS NOT NULL AND parent.`{parentColumn}` IS NULL");
        if (orphanCount > 0) return;
        await db.ExecuteAsync($"ALTER TABLE `{table}` ADD CONSTRAINT `{constraint}` FOREIGN KEY (`{column}`) REFERENCES `{parentTable}` (`{parentColumn}`) ON DELETE {deleteRule}");
    }

    private static async Task EnsureExistingColumnsAsync(MySqlConnection db)
    {
        var columns = new (string Table, string Column, string Definition)[]
        {
            ("recruitment_settings","enabletalentpool","BOOLEAN NOT NULL DEFAULT TRUE"),
            ("recruitment_settings","enableresumeparsing","BOOLEAN NOT NULL DEFAULT TRUE"),
            ("recruitment_settings","enableatsscoring","BOOLEAN NOT NULL DEFAULT TRUE"),
            ("recruitment_settings","requireresumeforapplication","BOOLEAN NOT NULL DEFAULT TRUE"),
            ("recruitment_settings","allowmanualscoreoverride","BOOLEAN NOT NULL DEFAULT TRUE"),
            ("recruitment_settings","allowduplicatecandidate","BOOLEAN NOT NULL DEFAULT FALSE"),
            ("recruitment_settings","autocreateapplicationfromreferral","BOOLEAN NOT NULL DEFAULT TRUE"),
            ("recruitment_settings","defaultatsscoringprofileid","BIGINT NULL"),
            ("recruitment_settings","candidateretentionmonths","INT NOT NULL DEFAULT 24"),
            ("recruitment_requisitions","jobdescriptiontemplateid","BIGINT NULL"),
            ("recruitment_requisitions","jobdescriptiontext","LONGTEXT NULL"),
            ("recruitment_requisitions","atsscoringprofileid","BIGINT NULL"),
            ("recruitment_open_positions","jobdescriptiontext","LONGTEXT NULL"),
            ("recruitment_open_positions","jobdescriptionversion","INT NOT NULL DEFAULT 1"),
            ("recruitment_open_positions","atsscoringprofileid","BIGINT NULL"),
            ("recruitment_resume_parse_facts","totalexperiencemonths","INT NULL"),
            ("recruitment_application_scores","shortlistthreshold","DECIMAL(5,2) NOT NULL DEFAULT 0"),
            ("recruitment_application_scores","recommendation","VARCHAR(120) NOT NULL DEFAULT ''"),
            ("recruitment_application_scores","explanationtext","VARCHAR(1000) NOT NULL DEFAULT ''"),
            ("recruitment_application_scores","profileversionnumber","INT NOT NULL DEFAULT 0"),
            ("recruitment_application_scores","humanreviewrequired","BOOLEAN NOT NULL DEFAULT TRUE"),
            ("recruitment_application_score_skill_matches","requirementweight","DECIMAL(5,2) NOT NULL DEFAULT 0"),
            ("recruitment_application_score_skill_matches","minimumyears","DECIMAL(5,2) NOT NULL DEFAULT 0"),
            ("recruitment_application_score_skill_matches","minimumproficiency","VARCHAR(80) NOT NULL DEFAULT ''"),
            ("recruitment_application_score_position_snapshots","jobdescriptionversionid","BIGINT NULL"),
            ("recruitment_application_score_position_snapshots","jobdescriptionversionnumber","INT NOT NULL DEFAULT 0"),
            ("recruitment_employee_referrals","candidateid","BIGINT NULL"),
            ("recruitment_employee_referrals","applicationid","BIGINT NULL"),
            ("recruitment_document_checklist","attachmentattributeid","BIGINT NULL"),
            ("recruitment_document_checklist","requiresverification","BOOLEAN NOT NULL DEFAULT FALSE"),
            ("recruitment_document_checklist","dueoffsetdays","INT NOT NULL DEFAULT 0"),
            ("recruitment_document_checklist","displayorder","INT NOT NULL DEFAULT 100"),
            ("recruitment_candidate_checklist_items","duedate","DATE NULL"),
            ("recruitment_requisition_documents","attachmentpublicid","CHAR(36) NULL"),
            ("recruitment_interviews","pipelinestageinstanceid","BIGINT NULL"),
            ("recruitment_interviews","roundconfigurationid","BIGINT NULL"),
            ("recruitment_interviews","timezoneid","VARCHAR(80) NOT NULL DEFAULT 'Asia/Kolkata'"),
            ("recruitment_interviews","attemptnumber","INT NOT NULL DEFAULT 1"),
            ("recruitment_interviews","reschedulecount","INT NOT NULL DEFAULT 0"),
            ("recruitment_interview_feedback","weightedscore","DECIMAL(5,2) NOT NULL DEFAULT 0"),
            ("recruitment_interview_feedback","scoresource","VARCHAR(40) NOT NULL DEFAULT 'LegacyOverall'"),
            ("recruitment_offers","pipelinestageinstanceid","BIGINT NULL"),
            ("recruitment_offers","stageofferconfigurationid","BIGINT NULL"),
            ("recruitment_offers","offertemplateid","BIGINT NULL"),
            ("recruitment_offers","budgetbasis","VARCHAR(40) NOT NULL DEFAULT ''"),
            ("recruitment_offers","approvedbudgetamount","DECIMAL(18,2) NOT NULL DEFAULT 0"),
            ("recruitment_offers","budgetexposureamount","DECIMAL(18,2) NOT NULL DEFAULT 0"),
            ("recruitment_offers","maximumvariancepercent","DECIMAL(7,2) NOT NULL DEFAULT 0"),
            ("recruitment_offers","variancepercent","DECIMAL(9,2) NOT NULL DEFAULT 0"),
            ("recruitment_offers","varianceexceeded","BOOLEAN NOT NULL DEFAULT FALSE"),
            ("recruitment_offers","appliedapprovalworkflowid","BIGINT NULL"),
            ("recruitment_offers","approvalpolicy","VARCHAR(40) NOT NULL DEFAULT ''"),
            ("recruitment_offers","candidateresponsevaliditydays","INT NOT NULL DEFAULT 0")
        };
        foreach (var column in columns) await EnsureColumnAsync(db, column.Table, column.Column, column.Definition);
    }

    private static async Task EnsureColumnAsync(MySqlConnection db, string table, string column, string definition)
    {
        var exists = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name=@Table AND LOWER(column_name)=@Column", new { Table = table, Column = column.ToLowerInvariant() });
        if (exists == 0) await db.ExecuteAsync($"ALTER TABLE `{table}` ADD COLUMN `{column}` {definition}");
    }

    private static async Task EnsureOfferIndexesAsync(MySqlConnection db)
    {
        foreach (var (name, column) in new[]
        {
            ("IX_recruitment_offer_pipeline_stage", "PipelineStageInstanceId"),
            ("IX_recruitment_offer_stage_config", "StageOfferConfigurationId")
        })
        {
            var exists = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM information_schema.statistics
WHERE table_schema=DATABASE() AND table_name='recruitment_offers' AND index_name=@Name", new { Name = name });
            if (exists == 0) await db.ExecuteAsync($"ALTER TABLE recruitment_offers ADD INDEX `{name}` (`{column}`)");
        }
    }

    private static Task EnsureTablesAsync(MySqlConnection db) => db.ExecuteAsync(@"
CREATE TABLE IF NOT EXISTS recruitment_candidates (
 Id BIGINT PRIMARY KEY AUTO_INCREMENT,CandidateCode VARCHAR(80) NOT NULL,ClientId INT NOT NULL,EmployeeId INT NULL,FirstName VARCHAR(120) NOT NULL,LastName VARCHAR(120) NOT NULL DEFAULT '',Email VARCHAR(190) NOT NULL DEFAULT '',NormalizedEmail VARCHAR(190) NOT NULL DEFAULT '',Phone VARCHAR(50) NOT NULL DEFAULT '',NormalizedPhone VARCHAR(50) NOT NULL DEFAULT '',CurrentCompany VARCHAR(180) NOT NULL DEFAULT '',CurrentTitle VARCHAR(180) NOT NULL DEFAULT '',TotalExperienceMonths INT NOT NULL DEFAULT 0,CurrentLocation VARCHAR(180) NOT NULL DEFAULT '',PreferredLocationsJson JSON NOT NULL,NoticePeriodDays INT NOT NULL DEFAULT 0,CurrentCtc DECIMAL(18,2) NOT NULL DEFAULT 0,ExpectedCtc DECIMAL(18,2) NOT NULL DEFAULT 0,HighestQualification VARCHAR(250) NOT NULL DEFAULT '',SourceType VARCHAR(80) NOT NULL DEFAULT 'Direct',SourceReferenceId BIGINT NULL,ProfileStatus VARCHAR(40) NOT NULL DEFAULT 'Active',ConsentStatus VARCHAR(40) NOT NULL DEFAULT 'Pending',ConsentCapturedAt DATETIME NULL,RetentionUntil DATETIME NULL,DuplicateOfCandidateId BIGINT NULL,CreatedByUserId INT NOT NULL,CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,UNIQUE KEY UX_recruitment_candidate_code (CandidateCode),UNIQUE KEY UX_recruitment_candidate_employee (EmployeeId),INDEX IX_recruitment_candidate_client_status (ClientId,ProfileStatus),INDEX IX_recruitment_candidate_email (NormalizedEmail),INDEX IX_recruitment_candidate_phone (NormalizedPhone));
CREATE TABLE IF NOT EXISTS recruitment_candidate_resumes (
 Id BIGINT PRIMARY KEY AUTO_INCREMENT,CandidateId BIGINT NOT NULL,AttachmentPublicId CHAR(36) NOT NULL,VersionNumber INT NOT NULL DEFAULT 1,IsPrimary BOOLEAN NOT NULL DEFAULT TRUE,ParsingStatus VARCHAR(40) NOT NULL DEFAULT 'Pending',ParsedText LONGTEXT NULL,ParsedJson JSON NOT NULL,ParserName VARCHAR(100) NOT NULL DEFAULT '',ParserVersion VARCHAR(50) NOT NULL DEFAULT '',ParsedAt DATETIME NULL,ParsingError VARCHAR(1000) NOT NULL DEFAULT '',CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,UNIQUE KEY UX_recruitment_candidate_resume_attachment (AttachmentPublicId),INDEX IX_recruitment_candidate_resume (CandidateId,IsPrimary,CreatedAt));
CREATE TABLE IF NOT EXISTS recruitment_resume_parser_runs (
 Id BIGINT PRIMARY KEY AUTO_INCREMENT,ResumeId BIGINT NOT NULL,ParserName VARCHAR(100) NOT NULL DEFAULT '',ParserVersion VARCHAR(50) NOT NULL DEFAULT '',ParseStatus VARCHAR(40) NOT NULL,ExtractedCharacterCount INT NOT NULL DEFAULT 0,ExtractedLineCount INT NOT NULL DEFAULT 0,ErrorMessage VARCHAR(1000) NOT NULL DEFAULT '',StartedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,CompletedAt DATETIME NULL,INDEX IX_recruitment_resume_parser_run (ResumeId,StartedAt));
CREATE TABLE IF NOT EXISTS recruitment_resume_parse_facts (
 Id BIGINT PRIMARY KEY AUTO_INCREMENT,ResumeId BIGINT NOT NULL,ExtractedEmail VARCHAR(190) NOT NULL DEFAULT '',ExtractedPhone VARCHAR(50) NOT NULL DEFAULT '',CharacterCount INT NOT NULL DEFAULT 0,LineCount INT NOT NULL DEFAULT 0,LanguageCode VARCHAR(20) NOT NULL DEFAULT 'und',SummaryText VARCHAR(1000) NOT NULL DEFAULT '',TotalExperienceMonths INT NULL,CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,UNIQUE KEY UX_recruitment_resume_parse_facts (ResumeId));
CREATE TABLE IF NOT EXISTS recruitment_resume_sections (
 Id BIGINT PRIMARY KEY AUTO_INCREMENT,ResumeId BIGINT NOT NULL,SectionCode VARCHAR(80) NOT NULL,Heading VARCHAR(180) NOT NULL DEFAULT '',Content LONGTEXT NOT NULL,DisplayOrder INT NOT NULL DEFAULT 100,Confidence DECIMAL(5,4) NOT NULL DEFAULT 0,INDEX IX_recruitment_resume_section (ResumeId,DisplayOrder),INDEX IX_recruitment_resume_section_code (ResumeId,SectionCode));
CREATE TABLE IF NOT EXISTS recruitment_resume_skills (
 Id BIGINT PRIMARY KEY AUTO_INCREMENT,ResumeId BIGINT NOT NULL,SkillId BIGINT NULL,SkillName VARCHAR(180) NOT NULL,MatchedTerm VARCHAR(180) NOT NULL DEFAULT '',EvidenceExcerpt VARCHAR(500) NOT NULL DEFAULT '',Confidence DECIMAL(5,4) NOT NULL DEFAULT 0,UNIQUE KEY UX_recruitment_resume_skill (ResumeId,SkillId),INDEX IX_recruitment_resume_skill_name (ResumeId,SkillName));
CREATE TABLE IF NOT EXISTS recruitment_candidate_applications (
 Id BIGINT PRIMARY KEY AUTO_INCREMENT,ApplicationCode VARCHAR(80) NOT NULL,CandidateId BIGINT NOT NULL,PositionId BIGINT NOT NULL,ClientId INT NOT NULL,SourceType VARCHAR(80) NOT NULL DEFAULT 'Direct',SourceReferenceId BIGINT NULL,ResumeId BIGINT NULL,CurrentStatus VARCHAR(80) NOT NULL DEFAULT 'New',CurrentStage VARCHAR(80) NOT NULL DEFAULT 'New',RecruiterUserId INT NULL,AppliedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,LastStageChangedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,DispositionReason VARCHAR(500) NOT NULL DEFAULT '',RejectedAt DATETIME NULL,WithdrawnAt DATETIME NULL,JoinedEmployeeId INT NULL,CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,UNIQUE KEY UX_recruitment_application_code (ApplicationCode),UNIQUE KEY UX_recruitment_application_candidate_position (CandidateId,PositionId),INDEX IX_recruitment_application_client_stage (ClientId,CurrentStage),INDEX IX_recruitment_application_position_status (PositionId,CurrentStatus));
CREATE TABLE IF NOT EXISTS recruitment_application_stage_history (
 Id BIGINT PRIMARY KEY AUTO_INCREMENT,ApplicationId BIGINT NOT NULL,FromStage VARCHAR(80) NOT NULL DEFAULT '',ToStage VARCHAR(80) NOT NULL,Reason VARCHAR(1000) NOT NULL DEFAULT '',ChangedByUserId INT NOT NULL,MetadataJson JSON NULL,ChangedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,INDEX IX_recruitment_application_stage (ApplicationId,ChangedAt));
CREATE TABLE IF NOT EXISTS recruitment_skills (
 Id BIGINT PRIMARY KEY AUTO_INCREMENT,ClientId INT NOT NULL DEFAULT 0,SkillCode VARCHAR(100) NOT NULL,SkillName VARCHAR(180) NOT NULL,Category VARCHAR(120) NOT NULL DEFAULT '',IsActive BOOLEAN NOT NULL DEFAULT TRUE,CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,UNIQUE KEY UX_recruitment_skill (ClientId,SkillCode));
CREATE TABLE IF NOT EXISTS recruitment_skill_aliases (
 Id BIGINT PRIMARY KEY AUTO_INCREMENT,SkillId BIGINT NOT NULL,AliasName VARCHAR(180) NOT NULL,NormalizedAlias VARCHAR(180) NOT NULL,UNIQUE KEY UX_recruitment_skill_alias (SkillId,NormalizedAlias),INDEX IX_recruitment_skill_alias_search (NormalizedAlias));
CREATE TABLE IF NOT EXISTS recruitment_candidate_skills (
 Id BIGINT PRIMARY KEY AUTO_INCREMENT,CandidateId BIGINT NOT NULL,SkillId BIGINT NULL,SkillName VARCHAR(180) NOT NULL,YearsExperience DECIMAL(5,2) NOT NULL DEFAULT 0,Proficiency VARCHAR(80) NOT NULL DEFAULT '',Source VARCHAR(40) NOT NULL DEFAULT 'Resume',Confidence DECIMAL(5,4) NOT NULL DEFAULT 0,CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,UNIQUE KEY UX_recruitment_candidate_skill (CandidateId,SkillId),INDEX IX_recruitment_candidate_skill_name (SkillName));
CREATE TABLE IF NOT EXISTS recruitment_candidate_experience (
 Id BIGINT PRIMARY KEY AUTO_INCREMENT,CandidateId BIGINT NOT NULL,Employer VARCHAR(180) NOT NULL DEFAULT '',JobTitle VARCHAR(180) NOT NULL DEFAULT '',StartDate DATE NULL,EndDate DATE NULL,IsCurrent BOOLEAN NOT NULL DEFAULT FALSE,Description TEXT NULL,DisplayOrder INT NOT NULL DEFAULT 100,INDEX IX_recruitment_candidate_experience (CandidateId,DisplayOrder));
CREATE TABLE IF NOT EXISTS recruitment_candidate_education (
 Id BIGINT PRIMARY KEY AUTO_INCREMENT,CandidateId BIGINT NOT NULL,Qualification VARCHAR(180) NOT NULL DEFAULT '',Institution VARCHAR(250) NOT NULL DEFAULT '',Specialization VARCHAR(180) NOT NULL DEFAULT '',CompletionYear INT NULL,Score VARCHAR(80) NOT NULL DEFAULT '',DisplayOrder INT NOT NULL DEFAULT 100,INDEX IX_recruitment_candidate_education (CandidateId,DisplayOrder));
CREATE TABLE IF NOT EXISTS recruitment_candidate_certifications (
 Id BIGINT PRIMARY KEY AUTO_INCREMENT,CandidateId BIGINT NOT NULL,CertificationName VARCHAR(180) NOT NULL,Issuer VARCHAR(180) NOT NULL DEFAULT '',IssueDate DATE NULL,ExpiryDate DATE NULL,CredentialId VARCHAR(180) NOT NULL DEFAULT '',INDEX IX_recruitment_candidate_certification (CandidateId,CertificationName));
CREATE TABLE IF NOT EXISTS recruitment_ats_scoring_profiles (
 Id BIGINT PRIMARY KEY AUTO_INCREMENT,ClientId INT NOT NULL,ProfileName VARCHAR(180) NOT NULL,PositionCategory VARCHAR(120) NOT NULL DEFAULT '',ScoringMethod VARCHAR(40) NOT NULL DEFAULT 'RuleBased',MinimumShortlistScore DECIMAL(5,2) NOT NULL DEFAULT 60,AutoScoreOnResumeUpload BOOLEAN NOT NULL DEFAULT TRUE,AllowManualOverride BOOLEAN NOT NULL DEFAULT TRUE,ParserProvider VARCHAR(80) NOT NULL DEFAULT 'BuiltIn',ScoringProvider VARCHAR(80) NOT NULL DEFAULT 'BuiltIn',ModelName VARCHAR(120) NOT NULL DEFAULT 'Deterministic-v1',VersionNumber INT NOT NULL DEFAULT 1,IsDefault BOOLEAN NOT NULL DEFAULT FALSE,IsActive BOOLEAN NOT NULL DEFAULT TRUE,CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,INDEX IX_recruitment_ats_profile (ClientId,PositionCategory,IsDefault,IsActive));
CREATE TABLE IF NOT EXISTS recruitment_ats_profile_criteria (
 Id BIGINT PRIMARY KEY AUTO_INCREMENT,ScoringProfileId BIGINT NOT NULL,CriterionCode VARCHAR(80) NOT NULL,CriterionLabel VARCHAR(180) NOT NULL,EvaluationType VARCHAR(80) NOT NULL DEFAULT 'TextMatch',Weight DECIMAL(5,2) NOT NULL DEFAULT 0,DisplayOrder INT NOT NULL DEFAULT 100,IsActive BOOLEAN NOT NULL DEFAULT TRUE,UNIQUE KEY UX_recruitment_ats_profile_criterion (ScoringProfileId,CriterionCode),INDEX IX_recruitment_ats_profile_criterion_order (ScoringProfileId,DisplayOrder));
CREATE TABLE IF NOT EXISTS recruitment_application_scores (
 Id BIGINT PRIMARY KEY AUTO_INCREMENT,ApplicationId BIGINT NOT NULL,ResumeId BIGINT NOT NULL,ScoringProfileId BIGINT NULL,PositionSnapshotJson JSON NULL,PositionSnapshotHash CHAR(64) NOT NULL DEFAULT '',TotalScore DECIMAL(5,2) NOT NULL,ComponentScoresJson JSON NOT NULL,MatchedSkillsJson JSON NOT NULL,MissingSkillsJson JSON NOT NULL,ExplanationJson JSON NOT NULL,ScoringMethod VARCHAR(40) NOT NULL,ModelName VARCHAR(120) NOT NULL DEFAULT '',ModelVersion VARCHAR(80) NOT NULL DEFAULT '',ScoreStatus VARCHAR(40) NOT NULL DEFAULT 'Completed',IsCurrent BOOLEAN NOT NULL DEFAULT TRUE,OverrideScore DECIMAL(5,2) NULL,OverrideReason VARCHAR(500) NOT NULL DEFAULT '',OverriddenByUserId INT NULL,OverriddenAt DATETIME NULL,ScoredAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,INDEX IX_recruitment_application_score (ApplicationId,IsCurrent,ScoredAt));
CREATE TABLE IF NOT EXISTS recruitment_application_score_components (
 Id BIGINT PRIMARY KEY AUTO_INCREMENT,ApplicationScoreId BIGINT NOT NULL,CriterionCode VARCHAR(80) NOT NULL,CriterionLabel VARCHAR(180) NOT NULL,Weight DECIMAL(5,2) NOT NULL,RawRatio DECIMAL(7,4) NOT NULL DEFAULT 0,AwardedScore DECIMAL(5,2) NOT NULL DEFAULT 0,MaximumScore DECIMAL(5,2) NOT NULL DEFAULT 0,EvidenceSummary VARCHAR(1000) NOT NULL DEFAULT '',DisplayOrder INT NOT NULL DEFAULT 100,UNIQUE KEY UX_recruitment_score_component (ApplicationScoreId,CriterionCode),INDEX IX_recruitment_score_component_order (ApplicationScoreId,DisplayOrder));
CREATE TABLE IF NOT EXISTS recruitment_application_score_skill_matches (
 Id BIGINT PRIMARY KEY AUTO_INCREMENT,ApplicationScoreId BIGINT NOT NULL,SkillType VARCHAR(40) NOT NULL,SkillName VARCHAR(180) NOT NULL,MatchStatus VARCHAR(40) NOT NULL,MatchedTerm VARCHAR(180) NOT NULL DEFAULT '',EvidenceExcerpt VARCHAR(500) NOT NULL DEFAULT '',RequirementWeight DECIMAL(5,2) NOT NULL DEFAULT 0,MinimumYears DECIMAL(5,2) NOT NULL DEFAULT 0,MinimumProficiency VARCHAR(80) NOT NULL DEFAULT '',Confidence DECIMAL(5,4) NOT NULL DEFAULT 0,UNIQUE KEY UX_recruitment_score_skill (ApplicationScoreId,SkillType,SkillName),INDEX IX_recruitment_score_skill_status (ApplicationScoreId,MatchStatus));
CREATE TABLE IF NOT EXISTS recruitment_application_score_evidence (
 Id BIGINT PRIMARY KEY AUTO_INCREMENT,ApplicationScoreId BIGINT NOT NULL,CriterionCode VARCHAR(80) NOT NULL,EvidenceType VARCHAR(80) NOT NULL,ExpectedValue VARCHAR(1000) NOT NULL DEFAULT '',ActualValue VARCHAR(1000) NOT NULL DEFAULT '',MatchStatus VARCHAR(40) NOT NULL,Confidence DECIMAL(5,4) NOT NULL DEFAULT 0,ResumeSectionId BIGINT NULL,INDEX IX_recruitment_score_evidence (ApplicationScoreId,CriterionCode));
CREATE TABLE IF NOT EXISTS recruitment_application_score_position_snapshots (
 Id BIGINT PRIMARY KEY AUTO_INCREMENT,ApplicationScoreId BIGINT NOT NULL,PositionId BIGINT NOT NULL,JobDescriptionVersionId BIGINT NULL,JobDescriptionVersionNumber INT NOT NULL DEFAULT 0,PositionCode VARCHAR(80) NOT NULL DEFAULT '',PositionTitle VARCHAR(180) NOT NULL DEFAULT '',PositionCategory VARCHAR(120) NOT NULL DEFAULT '',RequiredSkills TEXT NULL,PreferredSkills TEXT NULL,ExperienceRange VARCHAR(120) NOT NULL DEFAULT '',Qualification TEXT NULL,Certifications TEXT NULL,JobLocation VARCHAR(180) NOT NULL DEFAULT '',CapturedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,UNIQUE KEY UX_recruitment_score_position_snapshot (ApplicationScoreId));
CREATE TABLE IF NOT EXISTS recruitment_interviews (
 Id BIGINT PRIMARY KEY AUTO_INCREMENT,ApplicationId BIGINT NOT NULL,RoundCode VARCHAR(80) NOT NULL,InterviewType VARCHAR(80) NOT NULL,ScheduledStart DATETIME NOT NULL,ScheduledEnd DATETIME NOT NULL,Mode VARCHAR(40) NOT NULL DEFAULT 'Virtual',LocationOrLink VARCHAR(500) NOT NULL DEFAULT '',Status VARCHAR(40) NOT NULL DEFAULT 'Scheduled',Result VARCHAR(80) NOT NULL DEFAULT 'Pending',OverallFeedback TEXT NULL,OverallScore DECIMAL(5,2) NOT NULL DEFAULT 0,PipelineStageInstanceId BIGINT NULL,RoundConfigurationId BIGINT NULL,TimeZoneId VARCHAR(80) NOT NULL DEFAULT 'Asia/Kolkata',AttemptNumber INT NOT NULL DEFAULT 1,RescheduleCount INT NOT NULL DEFAULT 0,CreatedByUserId INT NOT NULL,CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,INDEX IX_recruitment_interview_application (ApplicationId,ScheduledStart),INDEX IX_recruitment_interview_status (Status,ScheduledStart),INDEX IX_recruitment_interview_pipeline_round (PipelineStageInstanceId,RoundConfigurationId,AttemptNumber));
CREATE TABLE IF NOT EXISTS recruitment_interview_panel_members (
 Id BIGINT PRIMARY KEY AUTO_INCREMENT,InterviewId BIGINT NOT NULL,PanelUserId INT NOT NULL,PanelRole VARCHAR(80) NOT NULL DEFAULT 'Panelist',AttendanceStatus VARCHAR(40) NOT NULL DEFAULT 'Pending',UNIQUE KEY UX_recruitment_interview_panel (InterviewId,PanelUserId));
CREATE TABLE IF NOT EXISTS recruitment_interview_feedback (
 Id BIGINT PRIMARY KEY AUTO_INCREMENT,InterviewId BIGINT NOT NULL,PanelUserId INT NOT NULL,OverallScore DECIMAL(5,2) NOT NULL DEFAULT 0,Recommendation VARCHAR(80) NOT NULL DEFAULT '',CompetencyScoresJson JSON NOT NULL,WeightedScore DECIMAL(5,2) NOT NULL DEFAULT 0,ScoreSource VARCHAR(40) NOT NULL DEFAULT 'LegacyOverall',Comments TEXT NULL,SubmittedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,UNIQUE KEY UX_recruitment_interview_feedback (InterviewId,PanelUserId));
CREATE TABLE IF NOT EXISTS recruitment_interview_feedback_competency_scores (
 Id BIGINT PRIMARY KEY AUTO_INCREMENT,InterviewFeedbackId BIGINT NOT NULL,InterviewStageCompetencyId BIGINT NOT NULL,CompetencyId BIGINT NOT NULL,CompetencyCode VARCHAR(80) NOT NULL DEFAULT '',CompetencyName VARCHAR(180) NOT NULL DEFAULT '',WeightPercent DECIMAL(5,2) NOT NULL DEFAULT 0,MinimumScore DECIMAL(5,2) NOT NULL DEFAULT 0,Score DECIMAL(5,2) NOT NULL DEFAULT 0,WeightedScore DECIMAL(5,2) NOT NULL DEFAULT 0,Comments VARCHAR(1000) NOT NULL DEFAULT '',CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,UNIQUE KEY UX_recruitment_interview_feedback_competency (InterviewFeedbackId,InterviewStageCompetencyId),INDEX IX_recruitment_interview_feedback_competency_master (CompetencyId));
CREATE TABLE IF NOT EXISTS recruitment_offers (
 Id BIGINT PRIMARY KEY AUTO_INCREMENT,OfferNumber VARCHAR(80) NOT NULL,ApplicationId BIGINT NOT NULL,ClientId INT NOT NULL,OfferedCtc DECIMAL(18,2) NOT NULL,Currency VARCHAR(10) NOT NULL DEFAULT 'INR',ProposedJoiningDate DATE NOT NULL,ExpiryDate DATE NULL,Status VARCHAR(80) NOT NULL DEFAULT 'Draft',WorkflowInstanceId BIGINT NULL,PipelineStageInstanceId BIGINT NULL,StageOfferConfigurationId BIGINT NULL,OfferTemplateId BIGINT NULL,BudgetBasis VARCHAR(40) NOT NULL DEFAULT '',ApprovedBudgetAmount DECIMAL(18,2) NOT NULL DEFAULT 0,BudgetExposureAmount DECIMAL(18,2) NOT NULL DEFAULT 0,MaximumVariancePercent DECIMAL(7,2) NOT NULL DEFAULT 0,VariancePercent DECIMAL(9,2) NOT NULL DEFAULT 0,VarianceExceeded BOOLEAN NOT NULL DEFAULT FALSE,AppliedApprovalWorkflowId BIGINT NULL,ApprovalPolicy VARCHAR(40) NOT NULL DEFAULT '',CandidateResponseValidityDays INT NOT NULL DEFAULT 0,OfferLetterAttachmentPublicId CHAR(36) NULL,Remarks VARCHAR(1000) NOT NULL DEFAULT '',CreatedByUserId INT NOT NULL,CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,UNIQUE KEY UX_recruitment_offer_number (OfferNumber),INDEX IX_recruitment_offer_application (ApplicationId,Status),INDEX IX_recruitment_offer_client (ClientId,Status),INDEX IX_recruitment_offer_pipeline_stage (PipelineStageInstanceId),INDEX IX_recruitment_offer_stage_config (StageOfferConfigurationId));
CREATE TABLE IF NOT EXISTS recruitment_candidate_checklist_items (
 Id BIGINT PRIMARY KEY AUTO_INCREMENT,ApplicationId BIGINT NOT NULL,CandidateId BIGINT NOT NULL,ChecklistConfigurationId INT NOT NULL,ChecklistName VARCHAR(180) NOT NULL,Stage VARCHAR(120) NOT NULL DEFAULT 'Pre-Onboarding',Mandatory BOOLEAN NOT NULL DEFAULT TRUE,AttachmentAttributeId BIGINT NULL,RequiresVerification BOOLEAN NOT NULL DEFAULT FALSE,DueDate DATE NULL,Status VARCHAR(40) NOT NULL DEFAULT 'Pending',AttachmentPublicId CHAR(36) NULL,CompletedByUserId INT NULL,CompletedAt DATETIME NULL,DisplayOrder INT NOT NULL DEFAULT 100,CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,UNIQUE KEY UX_recruitment_candidate_checklist (ApplicationId,ChecklistConfigurationId),INDEX IX_recruitment_candidate_checklist_status (CandidateId,Status));
CREATE TABLE IF NOT EXISTS person_activity_events (
 Id BIGINT PRIMARY KEY AUTO_INCREMENT,ClientId INT NOT NULL,CandidateId BIGINT NULL,EmployeeId INT NULL,ModuleCode VARCHAR(80) NOT NULL,EventType VARCHAR(100) NOT NULL,EventTitle VARCHAR(200) NOT NULL,EventSummary VARCHAR(1000) NOT NULL DEFAULT '',ResourceType VARCHAR(80) NOT NULL DEFAULT '',ResourceId VARCHAR(100) NOT NULL DEFAULT '',ActorUserId INT NULL,Visibility VARCHAR(40) NOT NULL DEFAULT 'HR',IsSensitive BOOLEAN NOT NULL DEFAULT FALSE,MetadataJson JSON NOT NULL,OccurredAt DATETIME NOT NULL,CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,INDEX IX_person_activity_employee (EmployeeId,OccurredAt),INDEX IX_person_activity_candidate (CandidateId,OccurredAt),INDEX IX_person_activity_client (ClientId,OccurredAt));
");

    private const string CandidateSelect = @"SELECT c.*,CONCAT(c.FirstName,' ',c.LastName) CandidateName,COALESCE(cl.Name,'') ClientName,COALESCE(e.EmployeeCode,'') EmployeeCode,(SELECT COUNT(*) FROM recruitment_candidate_applications a WHERE a.CandidateId=c.Id AND (@ScopeClientId IS NULL OR a.ClientId=@ScopeClientId)) ApplicationCount,(SELECT COALESCE(s.OverrideScore,s.TotalScore) FROM recruitment_application_scores s JOIN recruitment_candidate_applications a ON a.Id=s.ApplicationId WHERE a.CandidateId=c.Id AND s.IsCurrent=TRUE AND (@ScopeClientId IS NULL OR a.ClientId=@ScopeClientId) ORDER BY s.ScoredAt DESC LIMIT 1) LatestScore FROM recruitment_candidates c LEFT JOIN clients cl ON cl.Id=c.ClientId LEFT JOIN employees e ON e.Id=c.EmployeeId";
    private const string ResumeSummarySelect = @"SELECT r.Id,r.CandidateId,r.AttachmentPublicId,r.VersionNumber,r.IsPrimary,r.ParsingStatus,r.ParsedJson,r.ParserName,r.ParserVersion,r.ParsedAt,r.ParsingError,r.CreatedAt,COALESCE(a.original_file_name,'') OriginalFileName FROM recruitment_candidate_resumes r LEFT JOIN entity_attachments a ON a.public_id=CAST(r.AttachmentPublicId AS CHAR(36))";
    private const string InterviewCompetencySelect = @"SELECT sc.*,d.CompetencyCode,d.CompetencyName FROM recruitment_interview_stage_competencies sc JOIN recruitment_interview_competency_definitions d ON d.Id=sc.CompetencyId";

    private sealed record AtsCriterionDefinition(string Code, string Label, string EvaluationType, decimal DefaultWeight, int DisplayOrder);
    private sealed record CalculatedScoreComponent(string CriterionCode, string CriterionLabel, decimal Weight, decimal RawRatio, decimal AwardedScore, string EvidenceSummary, int DisplayOrder);
    private sealed record CalculatedScoreEvidence(string CriterionCode, string EvidenceType, string ExpectedValue, string ActualValue, decimal Ratio);
    private sealed record CalculatedSkillMatch(string SkillType, string SkillName, bool IsMatched, string MatchedTerm, string EvidenceExcerpt, decimal RequirementWeight, decimal MinimumYears, string MinimumProficiency);

    private sealed class InterviewPipelineContextRow
    {
        public int ClientId { get; set; }
        public bool HasPipelineInstance { get; set; }
        public long? PipelineStageInstanceId { get; set; }
        public long? RoundConfigurationId { get; set; }
        public string StageType { get; set; } = "";
        public string PipelineStageName { get; set; } = "";
        public string InterviewType { get; set; } = "Technical";
        public int DefaultDurationMinutes { get; set; } = 60;
        public int MinimumPanelCount { get; set; } = 1;
        public decimal MinimumPassingScore { get; set; } = 60;
        public bool FeedbackRequired { get; set; }
        public bool CalendarEnabled { get; set; } = true;
        public bool AllowReschedule { get; set; } = true;
    }

    private sealed class PipelineOfferPolicyContext
    {
        public int ClientId { get; set; }
        public long PositionId { get; set; }
        public long? PipelineStageInstanceId { get; set; }
        public long StageOfferConfigurationId { get; set; }
        public long? OfferTemplateId { get; set; }
        public long? ApprovalWorkflowId { get; set; }
        public string BudgetBasis { get; set; } = "ApprovedMaximum";
        public decimal MaximumVariancePercent { get; set; }
        public bool RequireApprovalWhenVarianceExceeded { get; set; } = true;
        public long? VarianceApprovalWorkflowId { get; set; }
        public int CandidateResponseValidityDays { get; set; } = 7;
        public bool BudgetAvailable { get; set; }
        public decimal BudgetAmount { get; set; }
        public decimal SalaryMax { get; set; }
        public string PositionCurrency { get; set; } = "INR";
        public int ApprovedPositions { get; set; } = 1;
        public decimal ApprovedBudgetAmount { get; set; }
        public decimal BudgetExposureAmount { get; set; }
        public decimal VariancePercent { get; set; }
        public bool VarianceExceeded { get; set; }
    }

    private sealed class OfferLetterContext
    {
        public long Id { get; set; }
        public long ApplicationId { get; set; }
        public int ClientId { get; set; }
        public long CandidateId { get; set; }
        public string OfferNumber { get; set; } = "";
        public decimal OfferedCtc { get; set; }
        public string Currency { get; set; } = "INR";
        public DateTime ProposedJoiningDate { get; set; }
        public DateTime? ExpiryDate { get; set; }
        public string Status { get; set; } = "";
        public Guid? OfferLetterAttachmentPublicId { get; set; }
        public string Remarks { get; set; } = "";
        public string CandidateFirstName { get; set; } = "";
        public string CandidateLastName { get; set; } = "";
        public string CandidateName { get; set; } = "";
        public string PositionTitle { get; set; } = "";
        public string ClientName { get; set; } = "";
        public long? TemplateId { get; set; }
        public int TemplateClientId { get; set; }
        public string TemplateType { get; set; } = "";
        public string SubjectTemplate { get; set; } = "";
        public string BodyTemplate { get; set; } = "";
        public bool TemplateIsActive { get; set; }
    }

    private sealed class InterviewCompetencyConfigRow
    {
        public long Id { get; set; }
        public long InterviewStageConfigurationId { get; set; }
        public long CompetencyId { get; set; }
        public string CompetencyCode { get; set; } = "";
        public string CompetencyName { get; set; } = "";
        public decimal WeightPercent { get; set; }
        public decimal MinimumScore { get; set; }
        public int DisplayOrder { get; set; }
    }

    private sealed class ExistingInterviewRow
    {
        public long ApplicationId { get; set; }
        public string Status { get; set; } = "";
        public DateTime ScheduledStart { get; set; }
        public DateTime ScheduledEnd { get; set; }
        public int RescheduleCount { get; set; }
        public long? PipelineStageInstanceId { get; set; }
        public long? RoundConfigurationId { get; set; }
        public string TimeZoneId { get; set; } = "Asia/Kolkata";
        public int AttemptNumber { get; set; }
    }

    private sealed class InterviewScheduleConflictRow
    {
        public long Id { get; set; }
        public string RoundCode { get; set; } = "";
        public DateTime ScheduledStart { get; set; }
        public DateTime ScheduledEnd { get; set; }
        public string PanelUserName { get; set; } = "";
    }

    private sealed class LegacyAtsProfileRow
    {
        public long Id { get; set; }
        public string WeightsJson { get; set; } = "{}";
    }

    private sealed class LegacyResumeMigrationRow
    {
        public long Id { get; set; }
        public long CandidateId { get; set; }
        public string ParsedText { get; set; } = "";
        public string ParsedJson { get; set; } = "{}";
        public string ParserName { get; set; } = "";
        public string ParserVersion { get; set; } = "";
        public string ParsingStatus { get; set; } = "Pending";
        public string ParsingError { get; set; } = "";
        public DateTime? ParsedAt { get; set; }
    }

    private sealed class LegacyScoreMigrationRow
    {
        public long Id { get; set; }
        public long ApplicationId { get; set; }
        public long ResumeId { get; set; }
        public long? ScoringProfileId { get; set; }
        public decimal TotalScore { get; set; }
        public string ModelVersion { get; set; } = "";
        public string ComponentScoresJson { get; set; } = "{}";
        public string MatchedSkillsJson { get; set; } = "[]";
        public string MissingSkillsJson { get; set; } = "[]";
        public string ExplanationJson { get; set; } = "{}";
    }

    private sealed class SkillDictionaryTermRow
    {
        public long SkillId { get; set; }
        public string SkillName { get; set; } = "";
        public string MatchTerm { get; set; } = "";
    }

    private sealed class ResumeSectionReference
    {
        public long Id { get; set; }
        public string SectionCode { get; set; } = "";
    }

    private sealed class ScoringRow
    {
        public long ApplicationId { get; set; }
        public long CandidateId { get; set; }
        public long PositionId { get; set; }
        public int ClientId { get; set; }
        public long? ApplicationResumeId { get; set; }
        public long EffectiveResumeId { get; set; }
        public string CurrentStage { get; set; } = "";
        public string ResumeText { get; set; } = "";
        public string ParsingStatus { get; set; } = "Pending";
        public string CurrentTitle { get; set; } = "";
        public int TotalExperienceMonths { get; set; }
        public string CurrentLocation { get; set; } = "";
        public int NoticePeriodDays { get; set; }
        public string HighestQualification { get; set; } = "";
        public string PositionCode { get; set; } = "";
        public string PositionTitle { get; set; } = "";
        public string PositionCategory { get; set; } = "";
        public string RequiredSkills { get; set; } = "";
        public string PreferredSkills { get; set; } = "";
        public string ExperienceRange { get; set; } = "";
        public string JobLocation { get; set; } = "";
        public string Qualification { get; set; } = "";
        public string Certifications { get; set; } = "";
        public long? JobDescriptionVersionId { get; set; }
        public int JobDescriptionVersionNumber { get; set; }
        public string ScoringPositionTitle { get; set; } = "";
    }
    private sealed class JdSkillScoringRow
    {
        public long? SkillId { get; set; }
        public string SkillName { get; set; } = "";
        public bool IsRequired { get; set; }
        public decimal MinimumYears { get; set; }
        public string MinimumProficiency { get; set; } = "";
        public decimal WeightPercent { get; set; }
    }
    private sealed class PublicApplicationResumeRow
    {
        public long ApplicationId { get; set; }
        public long CandidateId { get; set; }
        public int ClientId { get; set; }
        public long? RequestedResumeId { get; set; }
        public long ResumeId { get; set; }
        public Guid? AttachmentPublicId { get; set; }
        public string ParsingStatus { get; set; } = "Pending";
    }
    private sealed class PipelineAtsScoringSelection
    {
        public long? ScoringProfileId { get; set; }
        public bool RequireHumanConfirmation { get; set; } = true;
    }
    private sealed class SkillAliasRow
    {
        public string SkillName { get; set; } = "";
        public string AliasName { get; set; } = "";
    }
    private sealed class SkillAliasListRow
    {
        public long SkillId { get; set; }
        public string AliasName { get; set; } = "";
    }
    private sealed class ChecklistConfigurationRow
    {
        public int Id { get; set; }
        public string DocumentName { get; set; } = "";
        public string Stage { get; set; } = "";
        public bool Mandatory { get; set; }
        public long? AttachmentAttributeId { get; set; }
        public bool RequiresVerification { get; set; }
        public int DueOffsetDays { get; set; }
        public int DisplayOrder { get; set; }
    }
    private sealed class RecruitmentFeatureSettings
    {
        public bool EnableTalentPool { get; set; } = true;
        public bool EnableResumeParsing { get; set; } = true;
        public bool EnableAtsScoring { get; set; } = true;
        public bool EnableOfferApproval { get; set; } = true;
        public bool RequireResumeForApplication { get; set; } = true;
        public bool AllowManualScoreOverride { get; set; } = true;
        public bool AutoCreateApplicationFromReferral { get; set; } = true;
        public long? DefaultAtsScoringProfileId { get; set; }
    }
}
