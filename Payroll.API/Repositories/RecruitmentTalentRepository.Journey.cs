using Dapper;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class RecruitmentTalentRepository
{
    public async Task<(RecruitmentCandidateApplication? Row, string Error)> CreateDirectInterviewApplicationAsync(DirectInterviewApplication request, AuthUser user)
    {
        if (!RecruitmentPermissions.Has(user, "recruitment.interview.schedule")) return (null, "Scheduling permission is required.");
        if (!System.Net.Mail.MailAddress.TryCreate(request.Email.Trim(), out var email) || email.Address != request.Email.Trim()) return (null, "Enter a valid candidate email.");
        await using var db = Db(); await db.OpenAsync();
        var position = await db.QueryFirstOrDefaultAsync<RecruitmentOpenPosition>("SELECT * FROM recruitment_open_positions WHERE Id=@PositionId AND (@ClientId IS NULL OR ClientId=@ClientId)", new { request.PositionId, user.ClientId });
        if (position is null || !await RecruitmentAccessScope.CanAccessLocationAsync(db, user, position.ClientId, position.JobLocation)) return (null, "The selected job is outside your access.");
        var candidateId = await db.ExecuteScalarAsync<long?>("SELECT Id FROM recruitment_candidates WHERE ClientId=@ClientId AND Email=@Email AND ProfileStatus<>'Archived' ORDER BY Id LIMIT 1", new { position.ClientId, Email = email.Address });
        if (!candidateId.HasValue)
        {
            var name = (string.IsNullOrWhiteSpace(request.FullName) ? email.User : request.FullName).Trim().Split(' ', 2);
            var candidate = await SaveCandidateAsync(new SaveRecruitmentCandidate { ClientId = position.ClientId, FirstName = name[0], LastName = name.Length > 1 ? name[1] : "", Email = email.Address, Phone = request.Phone, SourceType = "Direct", ConsentStatus = "Pending" }, user);
            if (candidate.Row is null) return (null, candidate.Error);
            candidateId = candidate.Row.Id;
        }
        var existing = (await ApplicationsAsync(db, user, positionId: position.Id, candidateId: candidateId)).FirstOrDefault();
        return existing is not null ? (existing, "") : await CreateApplicationAsync(new SaveCandidateApplication { CandidateId = candidateId.Value, PositionId = position.Id, SourceType = "Direct" }, user, suppressAutoScoring: true);
    }

    public async Task<RecruitmentNegotiation?> GetNegotiationAsync(long applicationId, AuthUser user)
    {
        await using var db = Db(); await db.OpenAsync();
        if (await ApplicationByIdAsync(db, applicationId, user) is null) return null;
        var row = await db.QueryFirstAsync<NegotiationBudget>(@"SELECT a.Id ApplicationId,c.ExpectedCtc,a.NegotiationOverride,a.AgreedCtc,a.TermsConfirmedAtUtc,a.TermsVersion,
p.BudgetAvailable,p.BudgetAmount,p.ApprovedPositions,p.SalaryMax,p.Currency,COALESCE(config.BudgetBasis,'ApprovedMaximum') BudgetBasis
FROM recruitment_candidate_applications a JOIN recruitment_candidates c ON c.Id=a.CandidateId
JOIN recruitment_open_positions p ON p.Id=a.PositionId
LEFT JOIN recruitment_application_pipeline_instances flow ON flow.ApplicationId=a.Id
LEFT JOIN recruitment_stage_offer_configurations config ON config.Id=(SELECT policy.Id FROM recruitment_stage_offer_configurations policy
 JOIN recruitment_pipeline_stages stage ON stage.Id=policy.PipelineStageId
 WHERE stage.PipelineVersionId=flow.PipelineVersionId AND stage.IsActive=TRUE ORDER BY stage.DisplayOrder LIMIT 1)
WHERE a.Id=@applicationId", new { applicationId });
        row.ApprovedBudget = EffectiveOfferBudgetCeiling(row.BudgetBasis, row.BudgetAvailable, row.BudgetAmount, row.ApprovedPositions, row.SalaryMax);
        var review = await db.QueryFirstOrDefaultAsync<RecruitmentNegotiation>(@"SELECT COALESCE(w.Status,'Awaiting candidate signature') ApprovalStatus,
COALESCE((SELECT Comment FROM workflowhistory WHERE InstanceId=w.Id AND Action IN ('Rejected','Sent Back') ORDER BY Id DESC LIMIT 1),'') ReviewComment
FROM recruitment_process_documents d LEFT JOIN workflowinstances w ON w.Id=d.WorkflowInstanceId
WHERE d.ApplicationId=@applicationId AND d.TermsVersion=@TermsVersion AND d.DocumentType='MOM' ORDER BY d.Id DESC LIMIT 1", new { applicationId, row.TermsVersion });
        row.ApprovalStatus = review?.ApprovalStatus ?? "Terms not confirmed";
        row.ReviewComment = review?.ReviewComment ?? "";
        return row;
    }

    public async Task<(RecruitmentNegotiation? Row, string Error)> SaveNegotiationAsync(long applicationId, SaveRecruitmentNegotiation request, AuthUser user)
    {
        if (!RecruitmentPermissions.Has(user, "recruitment.offer.manage", "recruitment.proposal.manage")) return (null, "Negotiation permission is required.");
        var current = await GetNegotiationAsync(applicationId, user);
        if (current is null) return (null, "Application was not found.");
        if (request.AgreedCtc is < 0 or > 1000000000) return (null, "Enter a valid annual CTC.");
        await using var db = Db(); await db.OpenAsync();
        await using var tx = await db.BeginTransactionAsync();
        await db.ExecuteScalarAsync<long>("SELECT Id FROM recruitment_candidate_applications WHERE Id=@applicationId FOR UPDATE", new { applicationId }, tx);
        current = await GetNegotiationAsync(applicationId, user);
        if (current is null) return (null, "Application was not found.");
        if (await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM recruitment_offers WHERE ApplicationId=@applicationId AND Status IN ('Pending Approval','Approved','Pending Candidate','Released','Accepted'))", new { applicationId }, tx))
            return (null, "Withdraw or resolve the current offer before changing its agreed terms.");
        if (request.ConfirmTerms)
        {
            if (request.AgreedCtc is not > 0) return (null, "Enter the agreed annual CTC before confirming terms.");
            if (!await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM recruitment_interviews WHERE ApplicationId=@applicationId AND Status='Completed' AND Result='Selected' AND Id=(SELECT MAX(Id) FROM recruitment_interviews WHERE ApplicationId=@applicationId))", new { applicationId }, tx))
                return (null, "Final interview selection is required before confirming terms for MoM.");
        }
        var changed = current.NegotiationOverride != request.NegotiationOverride || current.AgreedCtc != request.AgreedCtc
            || request.ConfirmTerms && current.TermsConfirmedAtUtc is null;
        await db.ExecuteAsync(@"UPDATE recruitment_candidate_applications SET NegotiationOverride=@NegotiationOverride,AgreedCtc=@AgreedCtc,
TermsVersion=TermsVersion+IF(@Changed OR TermsVersion=0,1,0),
TermsConfirmedAtUtc=CASE WHEN @ConfirmTerms THEN UTC_TIMESTAMP(6) WHEN @Changed THEN NULL ELSE TermsConfirmedAtUtc END WHERE Id=@applicationId",
            new { applicationId, request.NegotiationOverride, request.AgreedCtc, request.ConfirmTerms, Changed = changed }, tx);
        await tx.CommitAsync();
        await WriteRecruitmentAuditAsync(db, "RecruitmentApplication", applicationId, "Negotiation terms", user.Id, request);
        return (await GetNegotiationAsync(applicationId, user), "");
    }

    private sealed class NegotiationBudget : RecruitmentNegotiation
    {
        public bool BudgetAvailable { get; set; }
        public decimal BudgetAmount { get; set; }
        public int ApprovedPositions { get; set; }
        public decimal SalaryMax { get; set; }
    }

    // Additive upgrade only; existing applications, decisions and signatures are preserved.
    public async Task InitializeJourneySchemaAsync()
    {
        await using var db = Db();
        await db.OpenAsync();
        foreach (var (table, column, definition) in new[] {
            ("recruitment_interviews", "DirectEmail", "VARCHAR(190) NOT NULL DEFAULT ''"),
            ("recruitment_interviews", "DirectName", "VARCHAR(180) NOT NULL DEFAULT ''"),
            ("recruitment_interviews", "DirectClientId", "INT NOT NULL DEFAULT 0"),
            ("recruitment_candidate_applications", "NegotiationOverride", "BOOLEAN NULL"),
            ("recruitment_candidate_applications", "AgreedCtc", "DECIMAL(18,2) NULL"),
            ("recruitment_candidate_applications", "TermsConfirmedAtUtc", "DATETIME(6) NULL"),
            ("recruitment_candidate_applications", "TermsVersion", "INT NOT NULL DEFAULT 0"),
            ("recruitment_process_documents", "TermsVersion", "INT NULL"),
            ("recruitment_process_documents", "BodySnapshot", "LONGTEXT NULL"),
            ("recruitment_process_document_signatures", "CandidateId", "BIGINT NULL") })
        {
            if (await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME=@table AND COLUMN_NAME=@column", new { table, column }) == 0)
                await db.ExecuteAsync($"ALTER TABLE `{table}` ADD COLUMN `{column}` {definition}");
        }
    }

    public async Task<(RecruitmentInterview? Row, string Error)> LinkDirectInterviewAsync(long id, long applicationId, AuthUser user)
    {
        if (!RecruitmentPermissions.Has(user, "recruitment.interview.schedule")) return (null, "Scheduling permission is required.");
        await using var db = Db(); await db.OpenAsync();
        var interview = (await InterviewRowsAsync(db, user, null, null)).FirstOrDefault(row => row.Id == id);
        var application = await ApplicationByIdAsync(db, applicationId, user);
        if (interview is null || interview.ApplicationId > 0 || application is null) return (null, "Select an unlinked interview and an accessible application.");
        if (interview.ClientId > 0 && interview.ClientId != application.ClientId) return (null, "Link this interview to an application in the same client.");
        if (!interview.DirectEmail.Equals(application.CandidateEmail, StringComparison.OrdinalIgnoreCase))
            return (null, "The application must belong to the same candidate email.");
        var changed = await db.ExecuteAsync("UPDATE recruitment_interviews SET ApplicationId=@applicationId WHERE Id=@id AND ApplicationId=0", new { id, applicationId });
        if (changed != 1) return (null, "This interview was already linked. Refresh the tracker.");
        await WriteRecruitmentAuditAsync(db, "RecruitmentInterview", id, "Linked to application", user.Id, new { applicationId });
        return ((await InterviewRowsAsync(db, user, applicationId, null)).First(row => row.Id == id), "");
    }
}
