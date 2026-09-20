using System.Net;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Services;

public sealed partial class RecruitmentLifecycleNotificationService(
    IConfiguration configuration,
    NotificationRepository notifications,
    PublicPortalUrlResolver publicPortalUrls,
    ILogger<RecruitmentLifecycleNotificationService> logger)
{
    private MySqlConnection Db() => new(configuration.GetConnectionString("Default"));

    public async Task InitializeAsync()
    {
        await using var db = Db();
        await db.OpenAsync();
        await EnsureSchemaAsync(db);
        // Establish a deployment boundary: do not email historical stage movements.
        await db.ExecuteAsync("INSERT IGNORE INTO recruitment_lifecycle_notification_deliveries(EventKey,RecipientEmail,Status) VALUES('HIRING_STAGE_MONITOR_START','','Checkpoint')");
    }

    public async Task<IReadOnlyList<RecruitmentLifecycleNotificationRule>> ListRulesAsync(AuthUser user, int? clientId)
    {
        await using var db = Db();
        await db.OpenAsync();
        await EnsureSchemaAsync(db);
        var scopedClientId = user.ClientId ?? clientId;
        var clientIds = scopedClientId is > 0
            ? [scopedClientId.Value]
            : (await db.QueryAsync<int>("SELECT DISTINCT ClientId FROM recruitment_settings WHERE RecruitmentEnabled=TRUE")).ToArray();
        foreach (var id in clientIds) await EnsureDefaultRulesAsync(db, id);
        return (await db.QueryAsync<RecruitmentLifecycleNotificationRule>(@"SELECT ruleRow.Id,ruleRow.ClientId,
COALESCE(clientRow.Name,CONCAT('Client ',ruleRow.ClientId)) ClientName,ruleRow.TriggerCode,ruleRow.TriggerName,
ruleRow.Description,ruleRow.SubjectTemplate,ruleRow.CandidateBodyTemplate,ruleRow.InternalBodyTemplate,
ruleRow.SendToCandidate,ruleRow.SendToPanel,ruleRow.SendToRequester,ruleRow.SendToRecruiter,
ruleRow.SendToApprovers,ruleRow.IsEnabled,ruleRow.CreatedAtUtc,ruleRow.UpdatedAtUtc
FROM recruitment_lifecycle_notification_rules ruleRow
LEFT JOIN clients clientRow ON clientRow.Id=ruleRow.ClientId
WHERE ruleRow.IsDeleted=FALSE AND (@ClientId IS NULL OR ruleRow.ClientId=@ClientId)
ORDER BY clientRow.Name,ruleRow.Id", new { ClientId = scopedClientId })).ToList();
    }

    public async Task<RecruitmentLifecycleNotificationRule> SaveRuleAsync(SaveRecruitmentLifecycleNotificationRule request, AuthUser user)
    {
        if (user.ClientId is not null) request.ClientId = user.ClientId.Value;
        if (request.ClientId <= 0) throw new InvalidOperationException("Client is required.");
        if (string.IsNullOrWhiteSpace(request.TriggerCode) || !DefaultRules.Any(item => item.Code == request.TriggerCode.Trim().ToUpperInvariant()))
            throw new InvalidOperationException("Select a supported recruitment trigger.");
        if (string.IsNullOrWhiteSpace(request.TriggerName) || string.IsNullOrWhiteSpace(request.SubjectTemplate))
            throw new InvalidOperationException("Trigger name and subject are required.");
        if (request.IsEnabled && !request.SendToCandidate && !request.SendToPanel && !request.SendToRequester && !request.SendToRecruiter && !request.SendToApprovers)
            throw new InvalidOperationException("Select at least one recipient group.");
        await using var db = Db();
        await db.OpenAsync();
        await EnsureSchemaAsync(db);
        await EnsureDefaultRulesAsync(db, request.ClientId);
        var code = request.TriggerCode.Trim().ToUpperInvariant();
        if (code == "HIRING_STAGE_MOVED" && request.SendToCandidate)
            throw new InvalidOperationException("Hiring-stage updates are internal. Use the candidate offer/application triggers for candidate email.");
        var id = request.Id > 0
            ? request.Id
            : await db.ExecuteScalarAsync<long?>("SELECT Id FROM recruitment_lifecycle_notification_rules WHERE ClientId=@ClientId AND TriggerCode=@Code", new { request.ClientId, Code = code }) ?? 0;
        if (id <= 0) throw new InvalidOperationException("Recruitment trigger was not found.");
        var changed = await db.ExecuteAsync(@"UPDATE recruitment_lifecycle_notification_rules SET
TriggerName=@TriggerName,Description=@Description,SubjectTemplate=@SubjectTemplate,
CandidateBodyTemplate=@CandidateBodyTemplate,InternalBodyTemplate=@InternalBodyTemplate,
SendToCandidate=@SendToCandidate,SendToPanel=@SendToPanel,SendToRequester=@SendToRequester,
SendToRecruiter=@SendToRecruiter,SendToApprovers=@SendToApprovers,IsEnabled=@IsEnabled,
IsDeleted=FALSE,UpdatedByUserId=@UserId,UpdatedAtUtc=UTC_TIMESTAMP(6)
WHERE Id=@Id AND ClientId=@ClientId", new
        {
            Id = id, request.ClientId, request.TriggerName, request.Description, request.SubjectTemplate,
            request.CandidateBodyTemplate, request.InternalBodyTemplate, request.SendToCandidate, request.SendToPanel,
            request.SendToRequester, request.SendToRecruiter, request.SendToApprovers, request.IsEnabled, UserId = user.Id
        });
        if (changed == 0) throw new InvalidOperationException("Recruitment trigger was not found.");
        return (await ListRulesAsync(user, request.ClientId)).Single(item => item.Id == id);
    }

    public async Task<bool> DeleteRuleAsync(long id, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        await EnsureSchemaAsync(db);
        return await db.ExecuteAsync(@"UPDATE recruitment_lifecycle_notification_rules
SET IsDeleted=TRUE,IsEnabled=FALSE,UpdatedByUserId=@UserId,UpdatedAtUtc=UTC_TIMESTAMP(6)
WHERE Id=@Id AND IsDeleted=FALSE AND (@ClientId IS NULL OR ClientId=@ClientId)",
            new { Id = id, UserId = user.Id, ClientId = user.ClientId }) > 0;
    }

    private static Task EnsureSchemaAsync(MySqlConnection db) => db.ExecuteAsync(@"CREATE TABLE IF NOT EXISTS recruitment_lifecycle_notification_deliveries (
Id BIGINT PRIMARY KEY AUTO_INCREMENT,
EventKey VARCHAR(190) NOT NULL,
RecipientEmail VARCHAR(190) NOT NULL,
Status VARCHAR(30) NOT NULL DEFAULT 'Reserved',
ErrorMessage VARCHAR(1000) NOT NULL DEFAULT '',
CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
QueuedAtUtc DATETIME(6) NULL,
UNIQUE KEY UX_recruitment_lifecycle_delivery (EventKey,RecipientEmail)
);
CREATE TABLE IF NOT EXISTS recruitment_lifecycle_notification_rules (
Id BIGINT PRIMARY KEY AUTO_INCREMENT,
ClientId INT NOT NULL,
TriggerCode VARCHAR(100) NOT NULL,
TriggerName VARCHAR(180) NOT NULL,
Description VARCHAR(500) NOT NULL DEFAULT '',
SubjectTemplate VARCHAR(500) NOT NULL,
CandidateBodyTemplate MEDIUMTEXT NOT NULL,
InternalBodyTemplate MEDIUMTEXT NOT NULL,
SendToCandidate BOOLEAN NOT NULL DEFAULT FALSE,
SendToPanel BOOLEAN NOT NULL DEFAULT FALSE,
SendToRequester BOOLEAN NOT NULL DEFAULT TRUE,
SendToRecruiter BOOLEAN NOT NULL DEFAULT TRUE,
SendToApprovers BOOLEAN NOT NULL DEFAULT FALSE,
IsEnabled BOOLEAN NOT NULL DEFAULT TRUE,
IsDeleted BOOLEAN NOT NULL DEFAULT FALSE,
UpdatedByUserId INT NULL,
CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
UNIQUE KEY UX_recruitment_lifecycle_rule (ClientId,TriggerCode),
KEY IX_recruitment_lifecycle_rule_active (ClientId,IsDeleted,IsEnabled)
);");

    private static readonly (string Code, string Name, string Description, string Subject, string CandidateBody, string InternalBody, bool Candidate, bool Panel, bool Approvers)[] DefaultRules =
    [
        ("HIRING_STAGE_MOVED", "Hiring stage changed", "Internal update after a committed manual or automatic hiring-stage move; historical changes are not mailed.", "Hiring update - {{positionTitle}}", "", "<p>Hello,</p><p><strong>{{positionTitle}}</strong>: {{stageName}}</p><p>Work order: {{workOrderNumber}}</p><p>Please open the hiring pipeline to review the next required action.</p>", false, false, true),
        ("MOM_SIGNED", "MoM fully signed", "Sent when every required panel signature is complete.", "Selection Committee MoM signed - {{positionTitle}}", "", "<p>Hello,</p><p>The Selection Committee MoM for <strong>{{positionTitle}}</strong> has been fully signed.</p><p><strong>Work order:</strong> {{workOrderNumber}}<br/><strong>Version:</strong> {{momVersion}}</p><p>The vacancy will now continue to negotiation and HR Division review.</p>", false, true, true),
        ("OFFER_RELEASED", "Offer released", "Candidate offer is ready for review.", "Offer ready for review - {{positionTitle}}", "<p>Hello {{candidateName}},</p><p>Your offer for <strong>{{positionTitle}}</strong> is ready for review.</p><p><strong>Offer:</strong> {{offerNumber}}<br/><strong>Annual CTC:</strong> {{currency}} {{offeredCtc}}<br/><strong>Proposed joining:</strong> {{proposedJoiningDate}}</p><p><a href=\"{{candidateActionUrl}}\">Review and respond to your offer</a></p>", "<p>Hello,</p><p>Offer <strong>{{offerNumber}}</strong> for <strong>{{candidateName}}</strong> ({{positionTitle}}) is now <strong>{{status}}</strong>.</p>", true, false, false),
        ("OFFER_APPROVED", "Offer approved", "Internal stakeholders are informed after approval.", "Offer approved - {{candidateName}} - {{positionTitle}}", "", "<p>Hello,</p><p>Offer <strong>{{offerNumber}}</strong> for <strong>{{candidateName}}</strong> ({{positionTitle}}) is now <strong>Approved</strong>.</p><p><strong>Annual CTC:</strong> {{currency}} {{offeredCtc}}<br/><strong>Proposed joining:</strong> {{proposedJoiningDate}}</p>", false, false, true),
        ("OFFER_NEGOTIATION", "Negotiation requested", "Candidate and vacancy stakeholders are informed.", "Negotiation requested - {{candidateName}} - {{positionTitle}}", "<p>Hello {{candidateName}},</p><p>Your request to discuss the offer for <strong>{{positionTitle}}</strong> has been recorded. The recruitment team will contact you.</p>", "<p>Hello,</p><p>Negotiation was requested for <strong>{{candidateName}}</strong> ({{positionTitle}}), offer <strong>{{offerNumber}}</strong>.</p>", true, true, true),
        ("OFFER_ACCEPTED", "Offer accepted", "Candidate, panel and approvers receive the acceptance update.", "Offer accepted - {{candidateName}} - {{positionTitle}}", "<p>Hello {{candidateName}},</p><p>Your acceptance of offer <strong>{{offerNumber}}</strong> for <strong>{{positionTitle}}</strong> has been recorded.</p><p><strong>Proposed joining:</strong> {{proposedJoiningDate}}</p><p>HR will share the pre-boarding and joining instructions.</p>", "<p>Hello,</p><p>Offer <strong>{{offerNumber}}</strong> for <strong>{{candidateName}}</strong> ({{positionTitle}}) has been accepted.</p>", true, true, true),
        ("OFFER_REJECTED", "Offer declined", "Candidate, panel and approvers receive the declined update.", "Offer declined - {{candidateName}} - {{positionTitle}}", "<p>Hello {{candidateName}},</p><p>Your decision on offer <strong>{{offerNumber}}</strong> for <strong>{{positionTitle}}</strong> has been recorded.</p>", "<p>Hello,</p><p>Offer <strong>{{offerNumber}}</strong> for <strong>{{candidateName}}</strong> ({{positionTitle}}) has been declined.</p>", true, true, true)
    ];

    private static async Task EnsureDefaultRulesAsync(MySqlConnection db, int clientId)
    {
        foreach (var rule in DefaultRules)
            await db.ExecuteAsync(@"INSERT IGNORE INTO recruitment_lifecycle_notification_rules
(ClientId,TriggerCode,TriggerName,Description,SubjectTemplate,CandidateBodyTemplate,InternalBodyTemplate,
SendToCandidate,SendToPanel,SendToRequester,SendToRecruiter,SendToApprovers,IsEnabled)
VALUES (@ClientId,@Code,@Name,@Description,@Subject,@CandidateBody,@InternalBody,@Candidate,@Panel,TRUE,TRUE,@Approvers,TRUE)",
                new { ClientId = clientId, rule.Code, rule.Name, rule.Description, rule.Subject, rule.CandidateBody, rule.InternalBody, rule.Candidate, rule.Panel, rule.Approvers });
    }

    public async Task QueueMomSignedAsync(long processDocumentId, AuthUser actor)
    {
        await using var db = Db();
        await db.OpenAsync();
        var context = await db.QueryFirstOrDefaultAsync<MomContext>(@"SELECT documentRow.Id,documentRow.ClientId,
documentRow.HiringCaseId,documentRow.VersionNumber,positionRow.Id PositionId,positionRow.PositionTitle,
COALESCE(workOrder.WorkOrderNumber,'') WorkOrderNumber
FROM recruitment_process_documents documentRow
JOIN recruitment_position_pipeline_instances hiringCase ON hiringCase.Id=documentRow.HiringCaseId
JOIN recruitment_open_positions positionRow ON positionRow.Id=hiringCase.PositionId
LEFT JOIN recruitment_work_order_lines workOrderLine ON workOrderLine.Id=hiringCase.WorkOrderLineId
LEFT JOIN recruitment_work_orders workOrder ON workOrder.Id=workOrderLine.WorkOrderId
WHERE documentRow.Id=@Id AND documentRow.Status='Signed' AND documentRow.DocumentType IN ('MOM','SIGNED_MOM')", new { Id = processDocumentId });
        if (context is null) return;
        var rule = await GetRuleAsync(db, context.ClientId, "MOM_SIGNED");
        if (rule is null) return;
        var recipients = await ResolveInternalRecipientsAsync(db, context.PositionId, rule);
        var tokens = new Dictionary<string, string>
        {
            ["positionTitle"] = context.PositionTitle,
            ["workOrderNumber"] = context.WorkOrderNumber,
            ["momVersion"] = context.VersionNumber.ToString()
        };
        await QueueAsync(db, $"MOM_SIGNED:{context.Id}:V{context.VersionNumber}", recipients,
            Render(rule.SubjectTemplate, tokens), Render(rule.InternalBodyTemplate, tokens),
            "RECRUITMENT_MOM_SIGNED", "RecruitmentProcessDocument", context.Id.ToString(), context.ClientId, actor);
    }

    public async Task QueueOfferMilestoneAsync(long offerId, string status, AuthUser actor, string candidateActionToken = "")
    {
        await using var db = Db();
        await db.OpenAsync();
        var context = await db.QueryFirstOrDefaultAsync<OfferContext>(@"SELECT offerRow.Id,offerRow.OfferNumber,offerRow.Status,
offerRow.OfferedCtc,offerRow.Currency,offerRow.ProposedJoiningDate,applicationRow.Id ApplicationId,
applicationRow.ClientId,applicationRow.PositionId,candidate.Email CandidateEmail,
CONCAT(candidate.FirstName,' ',candidate.LastName) CandidateName,positionRow.PositionTitle
FROM recruitment_offers offerRow
JOIN recruitment_candidate_applications applicationRow ON applicationRow.Id=offerRow.ApplicationId
JOIN recruitment_candidates candidate ON candidate.Id=applicationRow.CandidateId
JOIN recruitment_open_positions positionRow ON positionRow.Id=applicationRow.PositionId
WHERE offerRow.Id=@Id", new { Id = offerId });
        if (context is null) return;
        status = string.IsNullOrWhiteSpace(status) ? context.Status : status.Trim();
        var triggerCode = status switch
        {
            "Pending Candidate" or "Released" => "OFFER_RELEASED",
            "Approved" => "OFFER_APPROVED",
            "Negotiation" => "OFFER_NEGOTIATION",
            "Accepted" => "OFFER_ACCEPTED",
            "Rejected" => "OFFER_REJECTED",
            _ => ""
        };
        if (triggerCode.Length == 0) return;
        var rule = await GetRuleAsync(db, context.ClientId, triggerCode);
        if (rule is null) return;
        var internalRecipients = await ResolveInternalRecipientsAsync(db, context.PositionId, rule);
        var candidateRecipient = rule.SendToCandidate && ValidEmail(context.CandidateEmail) ? new[] { context.CandidateEmail.Trim() } : [];
        var eventKey = $"OFFER_{status.ToUpperInvariant().Replace(' ', '_')}:{context.Id}";
        var portalUrl = await CandidateActionUrlAsync(db, context.ClientId, candidateActionToken);
        var tokens = new Dictionary<string, string>
        {
            ["candidateName"] = context.CandidateName,
            ["positionTitle"] = context.PositionTitle,
            ["offerNumber"] = context.OfferNumber,
            ["currency"] = context.Currency,
            ["offeredCtc"] = context.OfferedCtc.ToString("N0"),
            ["proposedJoiningDate"] = context.ProposedJoiningDate.ToString("dd MMM yyyy"),
            ["candidateActionUrl"] = portalUrl,
            ["status"] = status
        };
        var subject = Render(rule.SubjectTemplate, tokens);
        await QueueAsync(db, eventKey + ":CANDIDATE", candidateRecipient, subject, Render(rule.CandidateBodyTemplate, tokens),
            "RECRUITMENT_OFFER_MILESTONE", "RecruitmentOffer", context.Id.ToString(), context.ClientId, actor);
        await QueueAsync(db, eventKey + ":INTERNAL", internalRecipients, subject, Render(rule.InternalBodyTemplate, tokens),
            "RECRUITMENT_OFFER_MILESTONE", "RecruitmentOffer", context.Id.ToString(), context.ClientId, actor);
    }

    public async Task QueueOfferMilestoneForApplicationAsync(long applicationId, string status, AuthUser actor)
    {
        await using var db = Db();
        await db.OpenAsync();
        var offerId = await db.ExecuteScalarAsync<long?>(@"SELECT Id FROM recruitment_offers
WHERE ApplicationId=@ApplicationId ORDER BY UpdatedAt DESC,Id DESC LIMIT 1", new { ApplicationId = applicationId });
        if (offerId is > 0) await QueueOfferMilestoneAsync(offerId.Value, status, actor);
    }

    private async Task QueueAsync(MySqlConnection db, string eventKey, IEnumerable<string> emails, string subject,
        string body, string eventCode, string resourceType, string resourceId, int clientId, AuthUser actor)
    {
        await EnsureSchemaAsync(db);
        foreach (var email in emails.Where(ValidEmail).Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var reserved = await db.ExecuteAsync(@"INSERT IGNORE INTO recruitment_lifecycle_notification_deliveries
(EventKey,RecipientEmail,Status) VALUES (@EventKey,@Email,'Reserved')", new { EventKey = eventKey, Email = email });
            if (reserved == 0) continue;
            var (queued, error) = await notifications.QueueDirectAsync(email, subject, body, new NotificationEvent
            {
                EventCode = eventCode,
                ResourceType = resourceType,
                ResourceId = resourceId,
                ClientId = clientId,
                ActorUserId = actor.Id,
                ActorName = actor.DisplayName,
                ActorEmail = actor.Email
            });
            if (queued)
                await db.ExecuteAsync("UPDATE recruitment_lifecycle_notification_deliveries SET Status='Queued',QueuedAtUtc=UTC_TIMESTAMP(6),ErrorMessage='' WHERE EventKey=@EventKey AND RecipientEmail=@Email", new { EventKey = eventKey, Email = email });
            else
            {
                await db.ExecuteAsync("DELETE FROM recruitment_lifecycle_notification_deliveries WHERE EventKey=@EventKey AND RecipientEmail=@Email", new { EventKey = eventKey, Email = email });
                logger.LogWarning("Recruitment lifecycle notification {EventKey} could not be queued for {Email}: {Error}", eventKey, email, error);
            }
        }
    }

    private static async Task<RecruitmentLifecycleNotificationRule?> GetRuleAsync(MySqlConnection db, int clientId, string triggerCode)
    {
        await EnsureSchemaAsync(db);
        await EnsureDefaultRulesAsync(db, clientId);
        return await db.QueryFirstOrDefaultAsync<RecruitmentLifecycleNotificationRule>(@"SELECT *
FROM recruitment_lifecycle_notification_rules
WHERE ClientId=@ClientId AND TriggerCode=@TriggerCode AND IsEnabled=TRUE AND IsDeleted=FALSE LIMIT 1",
            new { ClientId = clientId, TriggerCode = triggerCode });
    }

    private static async Task<List<string>> ResolveInternalRecipientsAsync(MySqlConnection db, long positionId, RecruitmentLifecycleNotificationRule rule)
    {
        var recipients = new List<string>();
        if (rule.SendToRecruiter || rule.SendToRequester)
            recipients.AddRange(await db.QueryAsync<string>(@"SELECT DISTINCT userRow.Email FROM recruitment_open_positions positionRow
LEFT JOIN recruitment_requisitions requisition ON requisition.Id=positionRow.RequisitionId
JOIN authusers userRow ON ((@SendToRecruiter=TRUE AND userRow.Id=positionRow.RecruiterUserId)
 OR (@SendToRequester=TRUE AND userRow.Id=requisition.RequestedByUserId))
WHERE positionRow.Id=@PositionId AND userRow.IsActive=TRUE", new { PositionId = positionId, rule.SendToRecruiter, rule.SendToRequester }));
        if (rule.SendToPanel)
            recipients.AddRange(await db.QueryAsync<string>(@"SELECT DISTINCT userRow.Email FROM recruitment_candidate_applications applicationRow
JOIN recruitment_interviews interviewRow ON interviewRow.ApplicationId=applicationRow.Id
JOIN recruitment_interview_panel_members panelRow ON panelRow.InterviewId=interviewRow.Id
JOIN authusers userRow ON userRow.Id=panelRow.PanelUserId AND userRow.IsActive=TRUE
WHERE applicationRow.PositionId=@PositionId", new { PositionId = positionId }));
        if (rule.SendToApprovers)
            recipients.AddRange(await db.QueryAsync<string>(@"SELECT DISTINCT userRow.Email FROM recruitment_open_positions positionRow
JOIN authusers userRow ON (userRow.ClientId IS NULL OR userRow.ClientId=positionRow.ClientId) AND userRow.IsActive=TRUE
JOIN authuserroles userRole ON userRole.UserId=userRow.Id
JOIN authroles roleRow ON roleRow.Id=userRole.RoleId AND roleRow.Code='uidai_stakeholder_approver'
WHERE positionRow.Id=@PositionId", new { PositionId = positionId }));
        return recipients;
    }

    private async Task<string> CandidateActionUrlAsync(MySqlConnection db, int clientId, string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return "";
        var configured = await db.ExecuteScalarAsync<string?>(@"SELECT PublicPortalBaseUrl FROM recruitment_settings
WHERE ClientId=@ClientId AND RecruitmentEnabled=TRUE AND EnableCandidatePortal=TRUE AND IsActive=TRUE LIMIT 1", new { ClientId = clientId });
        var baseUrl = publicPortalUrls.ResolveBaseUrl(configured);
        return baseUrl.Length == 0 ? "" : $"{baseUrl}/candidate-action/{Uri.EscapeDataString(token)}";
    }

    private static bool ValidEmail(string? value) => System.Net.Mail.MailAddress.TryCreate((value ?? "").Trim(), out _);
    private static string Html(string? value) => WebUtility.HtmlEncode(value ?? "");
    private static string Render(string template, IReadOnlyDictionary<string, string> tokens)
    {
        var rendered = template ?? "";
        foreach (var token in tokens)
            rendered = rendered.Replace("{{" + token.Key + "}}", Html(token.Value), StringComparison.OrdinalIgnoreCase);
        return rendered;
    }

    private sealed class MomContext
    {
        public long Id { get; set; }
        public int ClientId { get; set; }
        public long? HiringCaseId { get; set; }
        public int VersionNumber { get; set; }
        public long PositionId { get; set; }
        public string PositionTitle { get; set; } = "";
        public string WorkOrderNumber { get; set; } = "";
    }

    private sealed class OfferContext
    {
        public long Id { get; set; }
        public string OfferNumber { get; set; } = "";
        public string Status { get; set; } = "";
        public decimal OfferedCtc { get; set; }
        public string Currency { get; set; } = "INR";
        public DateTime ProposedJoiningDate { get; set; }
        public long ApplicationId { get; set; }
        public int ClientId { get; set; }
        public long PositionId { get; set; }
        public string CandidateEmail { get; set; } = "";
        public string CandidateName { get; set; } = "";
        public string PositionTitle { get; set; } = "";
    }
}
