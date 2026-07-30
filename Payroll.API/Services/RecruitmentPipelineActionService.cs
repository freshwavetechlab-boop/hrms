using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Services;

public sealed class RecruitmentPipelineActionService(
    IConfiguration configuration,
    RecruitmentTalentRepository talent,
    RecruitmentPipelineRepository pipelines,
    RecruitmentCandidateActionRepository candidateActions,
    WorkflowRepository workflows,
    NotificationRepository notifications,
    ILogger<RecruitmentPipelineActionService> logger)
{
    private MySqlConnection Db() => new(configuration.GetConnectionString("Default"));

    public async Task<RecruitmentStageActionExecutionResult> ExecuteAsync(
        long applicationId,
        string triggerEvent,
        AuthUser user,
        long? stageInstanceId = null)
    {
        var trigger = NormalizeTrigger(triggerEvent);
        var result = new RecruitmentStageActionExecutionResult
        {
            ApplicationId = applicationId,
            TriggerEvent = trigger
        };
        if (applicationId <= 0 || trigger.Length == 0) return result;

        await using var db = Db();
        await db.OpenAsync();
        var context = await ActionContextAsync(db, applicationId, trigger, stageInstanceId, user.ClientId, user.Id);
        if (context is null) return result;
        result.StageInstanceId = context.StageInstanceId;
        var actions = (await db.QueryAsync<ActionRow>(@"SELECT action.*
FROM recruitment_pipeline_stage_actions action
WHERE action.PipelineStageId=@PipelineStageId AND action.TriggerEvent=@TriggerEvent AND action.IsActive=TRUE
ORDER BY action.ExecutionOrder,action.Id", new { context.PipelineStageId, TriggerEvent = trigger })).ToList();

        foreach (var action in actions)
        {
            var executionId = await db.ExecuteScalarAsync<long>(@"INSERT INTO recruitment_stage_action_executions
(ApplicationId,StageInstanceId,StageActionId,TriggerEvent,ActionCode,Status,IsBlocking)
VALUES (@ApplicationId,@StageInstanceId,@StageActionId,@TriggerEvent,@ActionCode,'Pending',@IsBlocking)
ON DUPLICATE KEY UPDATE Id=LAST_INSERT_ID(Id);
SELECT LAST_INSERT_ID();", new
            {
                ApplicationId = applicationId,
                context.StageInstanceId,
                StageActionId = action.Id,
                TriggerEvent = trigger,
                action.ActionCode,
                action.IsBlocking
            });
            await db.ExecuteAsync(@"UPDATE recruitment_stage_action_executions
SET Status='Failed',ErrorMessage='Previous action execution timed out.',CompletedAtUtc=UTC_TIMESTAMP(6)
WHERE Id=@Id AND Status='Running' AND StartedAtUtc<TIMESTAMPADD(MINUTE,-5,UTC_TIMESTAMP(6))", new { Id = executionId });
            var existing = await db.QueryFirstAsync<RecruitmentStageActionExecution>(
                "SELECT * FROM recruitment_stage_action_executions WHERE Id=@Id", new { Id = executionId });
            if (existing.Status is "Completed" or "Pending Approval" or "Running")
            {
                result.Executions.Add(existing);
                continue;
            }
            var claimed = await db.ExecuteAsync(@"UPDATE recruitment_stage_action_executions
SET Status='Running',ErrorMessage='',StartedAtUtc=UTC_TIMESTAMP(6),CompletedAtUtc=NULL
WHERE Id=@Id AND Status IN ('Pending','Failed')", new { Id = executionId });
            if (claimed == 0)
            {
                result.Executions.Add(await db.QueryFirstAsync<RecruitmentStageActionExecution>(
                    "SELECT * FROM recruitment_stage_action_executions WHERE Id=@Id", new { Id = executionId }));
                continue;
            }

            try
            {
                await ExecuteOneAsync(db, executionId, action, context, user);
            }
            catch (Exception exception)
            {
                var error = Truncate(exception.Message, 1000);
                await db.ExecuteAsync(@"UPDATE recruitment_stage_action_executions
SET Status='Failed',ErrorMessage=@Error,CompletedAtUtc=UTC_TIMESTAMP(6) WHERE Id=@Id",
                    new { Id = executionId, Error = error });
                logger.LogWarning(exception, "Recruitment stage action {ActionId} failed for application {ApplicationId}.", action.Id, applicationId);
            }

            var execution = await db.QueryFirstAsync<RecruitmentStageActionExecution>(
                "SELECT * FROM recruitment_stage_action_executions WHERE Id=@Id", new { Id = executionId });
            result.Executions.Add(execution);
        }

        result.HasBlockingFailure = result.Executions.Any(row => row.IsBlocking && row.Status != "Completed");
        return result;
    }

    public async Task<IReadOnlyList<RecruitmentStageActionExecution>> GetExecutionsAsync(long applicationId, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        return (await db.QueryAsync<RecruitmentStageActionExecution>(@"SELECT execution.*
FROM recruitment_stage_action_executions execution
JOIN recruitment_candidate_applications applicationRow ON applicationRow.Id=execution.ApplicationId
WHERE execution.ApplicationId=@ApplicationId AND (@ClientId IS NULL OR applicationRow.ClientId=@ClientId)
ORDER BY execution.StartedAtUtc DESC,execution.Id DESC", new { ApplicationId = applicationId, user.ClientId })).ToList();
    }

    public async Task<(long ApplicationId, long StageInstanceId, bool Approved)> CompleteWorkflowAsync(long workflowInstanceId, string workflowStatus)
    {
        var status = (workflowStatus ?? "").Trim();
        var terminal = status.Equals("Approved", StringComparison.OrdinalIgnoreCase)
            || status.Equals("Rejected", StringComparison.OrdinalIgnoreCase)
            || status.Equals("Sent Back", StringComparison.OrdinalIgnoreCase)
            || status.Equals("Failed", StringComparison.OrdinalIgnoreCase)
            || status.Equals("Cancelled", StringComparison.OrdinalIgnoreCase);
        if (!terminal)
            return (0, 0, false);
        var approved = status.Equals("Approved", StringComparison.OrdinalIgnoreCase);
        await using var db = Db();
        await db.OpenAsync();
        var execution = await db.QueryFirstOrDefaultAsync<RecruitmentStageActionExecution>(
            "SELECT * FROM recruitment_stage_action_executions WHERE WorkflowInstanceId=@Id", new { Id = workflowInstanceId });
        if (execution is null) return (0, 0, false);
        await db.ExecuteAsync(@"UPDATE recruitment_stage_action_executions
SET Status=@Status,ErrorMessage=@Error,CompletedAtUtc=UTC_TIMESTAMP(6) WHERE Id=@Id",
            new
            {
                execution.Id,
                Status = approved ? "Completed" : "Failed",
                Error = approved ? "" : $"Workflow completed as {status}."
            });
        return (execution.ApplicationId, execution.StageInstanceId, approved);
    }

    public async Task<int> ProcessSlaActionsAsync(CancellationToken cancellationToken)
    {
        await using var db = Db();
        await db.OpenAsync(cancellationToken);
        var rows = (await db.QueryAsync<SlaActionSource>(new CommandDefinition(@"SELECT DISTINCT stageInstance.ApplicationId,
CASE WHEN stageInstance.DueAtUtc<=UTC_TIMESTAMP(6) THEN 'OnSlaBreach' ELSE 'OnSlaWarning' END TriggerEvent
FROM recruitment_application_stage_instances stageInstance
JOIN recruitment_pipeline_stages stageRow ON stageRow.Id=stageInstance.PipelineStageId
JOIN recruitment_pipeline_stage_actions action ON action.PipelineStageId=stageRow.Id AND action.IsActive=TRUE
WHERE stageInstance.Status='Active' AND stageInstance.DueAtUtc IS NOT NULL
AND ((action.TriggerEvent='OnSlaBreach' AND stageInstance.DueAtUtc<=UTC_TIMESTAMP(6))
 OR (action.TriggerEvent='OnSlaWarning' AND stageInstance.DueAtUtc>UTC_TIMESTAMP(6)
     AND stageRow.SlaWarningMinutes>0
     AND stageInstance.DueAtUtc<=TIMESTAMPADD(MINUTE,stageRow.SlaWarningMinutes,UTC_TIMESTAMP(6))))
ORDER BY stageInstance.ApplicationId", cancellationToken: cancellationToken))).ToList();
        var system = new AuthUser { Id = 0, DisplayName = "Recruitment automation", IsActive = true, ClientId = null };
        var processed = 0;
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var execution = await ExecuteAsync(row.ApplicationId, row.TriggerEvent, system);
            processed += execution.Executions.Count;
        }
        return processed;
    }

    private async Task ExecuteOneAsync(
        MySqlConnection db,
        long executionId,
        ActionRow action,
        ActionContext context,
        AuthUser user)
    {
        switch (action.ActionCode.Trim().ToUpperInvariant())
        {
            case "GENERATE_ACTION_LINK":
            {
                if (!action.TriggerEvent.Equals("OnEntry", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Candidate action links can be generated only on stage entry.");
                var (session, error) = await candidateActions.EnsureForCurrentStageAsync(context.ApplicationId, user);
                if (session is null) throw new InvalidOperationException(error);
                await CompleteAsync(db, executionId, "CandidateActionSessionId", session.Id);
                break;
            }
            case "RUN_ATS_SCORE":
            {
                var (score, error) = await talent.ScoreApplicationAsync(context.ApplicationId, user);
                if (score is null) throw new InvalidOperationException(error);
                await CompleteAsync(db, executionId, "ApplicationScoreId", score.Id);
                var (automation, _) = await pipelines.EvaluateAtsStageAutomationAsync(context.ApplicationId, user);
                if (automation?.Status == "Applied")
                {
                    await ExecuteAsync(context.ApplicationId, "OnExit", user, context.StageInstanceId);
                    await ExecuteAsync(context.ApplicationId, "OnEntry", user);
                }
                break;
            }
            case "START_WORKFLOW":
            {
                if (action.WorkflowId is null or <= 0) throw new InvalidOperationException("Select a workflow for this stage action.");
                if (context.WorkflowRequestorUserId <= 0) throw new InvalidOperationException("A valid hiring requester or recruiter is required before starting this workflow.");
                var workflow = await workflows.StartAsync(new StartWorkflowRequest
                {
                    WorkflowId = checked((int)action.WorkflowId.Value),
                    ResourceType = "RecruitmentPipelineStageAction",
                    ResourceId = executionId.ToString(),
                    PayloadJson = JsonSerializer.Serialize(new
                    {
                        context.ApplicationId,
                        context.StageInstanceId,
                        context.StageName,
                        action.ActionCode
                    })
                }, context.WorkflowRequestorUserId);
                if (workflow is null) throw new InvalidOperationException("The configured workflow could not be started. Check stages and approvers.");
                await db.ExecuteAsync(@"UPDATE recruitment_stage_action_executions
SET Status='Pending Approval',WorkflowInstanceId=@WorkflowInstanceId,ErrorMessage='' WHERE Id=@Id",
                    new { Id = executionId, WorkflowInstanceId = workflow.Id });
                break;
            }
            case "SEND_NOTIFICATION":
            {
                if (action.TemplateId is null or <= 0) throw new InvalidOperationException("Select a notification template for this stage action.");
                var recipients = await ResolveRecipientsAsync(db, action.Id, context);
                if (recipients.Count == 0) throw new InvalidOperationException("No active recipient could be resolved for this stage notification.");
                var openSession = (await candidateActions.ListAsync(context.ApplicationId, user))
                    .FirstOrDefault(row => row.Status.Equals("Open", StringComparison.OrdinalIgnoreCase)
                        && row.PipelineStageInstanceId == context.StageInstanceId
                        && row.RevokedAtUtc is null && row.ExpiresAtUtc > DateTime.UtcNow);
                var candidateLinkRequired = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*)
FROM recruitment_pipeline_stage_actions
WHERE PipelineStageId=@PipelineStageId AND TriggerEvent=@TriggerEvent AND IsActive=TRUE
  AND ActionCode='GENERATE_ACTION_LINK' AND ExecutionOrder<@ExecutionOrder", new
                {
                    context.PipelineStageId,
                    action.TriggerEvent,
                    action.ExecutionOrder
                }) > 0;
                if (candidateLinkRequired && openSession is null)
                    throw new InvalidOperationException("Generate the current candidate action link before sending this notification.");
                var portalBaseUrl = (await db.ExecuteScalarAsync<string?>(@"SELECT PublicPortalBaseUrl
FROM recruitment_settings WHERE ClientId=@ClientId AND RecruitmentEnabled=TRUE AND EnableCandidatePortal=TRUE AND IsActive=TRUE LIMIT 1",
                    new { context.ClientId }) ?? "").Trim().TrimEnd('/');
                if (openSession is not null && portalBaseUrl.Length == 0)
                    throw new InvalidOperationException("Configure and enable the public candidate portal URL before sending a candidate action notification.");
                var candidateActionUrl = openSession is null || string.IsNullOrWhiteSpace(openSession.ActionToken) || portalBaseUrl.Length == 0
                    ? ""
                    : $"{portalBaseUrl}/candidate-action/{Uri.EscapeDataString(openSession.ActionToken)}";
                long? firstQueueId = null;
                foreach (var recipient in recipients)
                {
                    var queueId = await notifications.QueueTemplateAsync(action.TemplateId.Value, recipient.Email, new NotificationEvent
                    {
                        EventCode = $"RECRUITMENT_STAGE_{action.TriggerEvent}",
                        ResourceType = "RecruitmentApplication",
                        ResourceId = context.ApplicationId.ToString(),
                        ClientId = context.ClientId,
                        ActorUserId = user.Id,
                        ActorName = user.DisplayName,
                        ActorEmail = user.Email,
                        PayloadJson = JsonSerializer.Serialize(new
                        {
                            applicationId = context.ApplicationId,
                            candidateId = context.CandidateId,
                            candidateName = context.CandidateName,
                            candidateEmail = context.CandidateEmail,
                            positionTitle = context.PositionTitle,
                            stageName = context.StageName,
                            stageType = context.StageType,
                            triggerEvent = action.TriggerEvent,
                            candidateActionUrl,
                            candidateActionExpiresAt = openSession?.ExpiresAtUtc.ToString("O") ?? ""
                        })
                    });
                    if (!queueId.HasValue) throw new InvalidOperationException($"Notification could not be queued for {recipient.Email}. Check the template and recipient.");
                    firstQueueId ??= queueId.Value;
                    await db.ExecuteAsync(@"INSERT INTO recruitment_stage_action_notification_deliveries
(StageActionExecutionId,RecipientType,RecipientEmail,NotificationQueueId)
VALUES (@ExecutionId,@RecipientType,@RecipientEmail,@QueueId)
ON DUPLICATE KEY UPDATE NotificationQueueId=VALUES(NotificationQueueId)", new { ExecutionId = executionId, recipient.RecipientType, RecipientEmail = recipient.Email, QueueId = queueId.Value });
                }
                await CompleteAsync(db, executionId, "NotificationQueueId", firstQueueId!.Value);
                break;
            }
            default:
                throw new InvalidOperationException($"Stage action '{action.ActionCode}' is not supported.");
        }
    }

    private static async Task<ActionContext?> ActionContextAsync(
        MySqlConnection db,
        long applicationId,
        string trigger,
        long? stageInstanceId,
        int? clientId,
        int triggeringUserId)
    {
        var useCompletedStage = trigger is "OnExit" or "OnApproval";
        return await db.QueryFirstOrDefaultAsync<ActionContext>(@"SELECT applicationRow.Id ApplicationId,applicationRow.ClientId,
applicationRow.CandidateId,CONCAT(candidate.FirstName,' ',candidate.LastName) CandidateName,candidate.Email CandidateEmail,
positionRow.PositionTitle,stageInstance.Id StageInstanceId,stageInstance.PipelineStageId,stageRow.StageName,stageRow.StageType,
COALESCE(requesterUser.Id,applicationRecruiter.Id,positionRecruiter.Id,triggeringUser.Id,0) WorkflowRequestorUserId
FROM recruitment_candidate_applications applicationRow
JOIN recruitment_candidates candidate ON candidate.Id=applicationRow.CandidateId
JOIN recruitment_open_positions positionRow ON positionRow.Id=applicationRow.PositionId
LEFT JOIN recruitment_requisitions requisition ON requisition.Id=positionRow.RequisitionId
LEFT JOIN authusers requesterUser ON requesterUser.Id=requisition.RequestedByUserId AND requesterUser.IsActive=TRUE
LEFT JOIN authusers applicationRecruiter ON applicationRecruiter.Id=applicationRow.RecruiterUserId AND applicationRecruiter.IsActive=TRUE
LEFT JOIN authusers positionRecruiter ON positionRecruiter.Id=positionRow.RecruiterUserId AND positionRecruiter.IsActive=TRUE
LEFT JOIN authusers triggeringUser ON triggeringUser.Id=@TriggeringUserId AND triggeringUser.IsActive=TRUE
JOIN recruitment_application_stage_instances stageInstance ON stageInstance.ApplicationId=applicationRow.Id
JOIN recruitment_pipeline_stages stageRow ON stageRow.Id=stageInstance.PipelineStageId
WHERE applicationRow.Id=@ApplicationId AND (@ClientId IS NULL OR applicationRow.ClientId=@ClientId)
AND ((@StageInstanceId IS NOT NULL AND stageInstance.Id=@StageInstanceId)
 OR (@StageInstanceId IS NULL AND @UseCompletedStage=FALSE AND stageInstance.Id=applicationRow.CurrentPipelineStageInstanceId)
 OR (@StageInstanceId IS NULL AND @UseCompletedStage=TRUE AND stageInstance.Status='Completed'))
ORDER BY CASE WHEN stageInstance.Id=applicationRow.CurrentPipelineStageInstanceId THEN 0 ELSE 1 END,
stageInstance.ExitedAtUtc DESC,stageInstance.EnteredAtUtc DESC LIMIT 1", new
        {
            ApplicationId = applicationId,
            StageInstanceId = stageInstanceId,
            UseCompletedStage = useCompletedStage,
            ClientId = clientId,
            TriggeringUserId = triggeringUserId
        });
    }

    private static Task CompleteAsync(MySqlConnection db, long executionId, string referenceColumn, long referenceId)
    {
        if (referenceColumn is not ("NotificationQueueId" or "CandidateActionSessionId" or "ApplicationScoreId"))
            throw new InvalidOperationException("Unsupported stage action reference.");
        return db.ExecuteAsync($@"UPDATE recruitment_stage_action_executions
SET Status='Completed',{referenceColumn}=@ReferenceId,ErrorMessage='',CompletedAtUtc=UTC_TIMESTAMP(6) WHERE Id=@Id",
            new { Id = executionId, ReferenceId = referenceId });
    }

    private static async Task<IReadOnlyList<ResolvedRecipient>> ResolveRecipientsAsync(MySqlConnection db, long stageActionId, ActionContext context)
    {
        var configured = (await db.QueryAsync<RecipientConfiguration>(@"SELECT * FROM recruitment_stage_action_recipients
WHERE StageActionId=@Id AND IsActive=TRUE ORDER BY DisplayOrder,Id", new { Id = stageActionId })).ToList();
        if (configured.Count == 0)
            configured.Add(new RecipientConfiguration { RecipientType = "Candidate" });
        var recipients = new List<ResolvedRecipient>();
        foreach (var configuration in configured)
        {
            var type = (configuration.RecipientType ?? "").Trim();
            if (type.Equals("Candidate", StringComparison.OrdinalIgnoreCase))
                Add(type, context.CandidateEmail);
            else if (type.Equals("SpecificUser", StringComparison.OrdinalIgnoreCase) && configuration.UserId.HasValue)
                AddRange(type, await db.QueryAsync<string>("SELECT Email FROM authusers WHERE Id=@Id AND IsActive=TRUE AND (ClientId IS NULL OR ClientId=@ClientId)", new { Id = configuration.UserId.Value, context.ClientId }));
            else if (type.Equals("UserRole", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(configuration.RoleCode))
                AddRange(type, await db.QueryAsync<string>(@"SELECT DISTINCT userRow.Email FROM authusers userRow
JOIN authuserroles userRole ON userRole.UserId=userRow.Id JOIN authroles roleRow ON roleRow.Id=userRole.RoleId
WHERE roleRow.Code=@RoleCode AND userRow.IsActive=TRUE AND (userRow.ClientId IS NULL OR userRow.ClientId=@ClientId)", new { RoleCode = configuration.RoleCode.Trim(), context.ClientId }));
            else if (type.Equals("StaticEmail", StringComparison.OrdinalIgnoreCase))
                Add(type, configuration.EmailAddress);
            else if (type.Equals("InterviewPanelMembers", StringComparison.OrdinalIgnoreCase))
                AddRange(type, await db.QueryAsync<string>(@"SELECT DISTINCT userRow.Email FROM recruitment_interviews interviewRow
JOIN recruitment_interview_panel_members panel ON panel.InterviewId=interviewRow.Id
JOIN authusers userRow ON userRow.Id=panel.PanelUserId AND userRow.IsActive=TRUE
WHERE interviewRow.ApplicationId=@ApplicationId AND (interviewRow.PipelineStageInstanceId=@StageInstanceId OR @StageInstanceId IS NULL)", new { context.ApplicationId, context.StageInstanceId }));
            else if (type.Equals("StageDefaultPanelMembers", StringComparison.OrdinalIgnoreCase))
                AddRange(type, await db.QueryAsync<string>(@"SELECT DISTINCT userRow.Email FROM recruitment_stage_default_panel_members panel
JOIN authusers userRow ON userRow.Id=panel.PanelUserId AND userRow.IsActive=TRUE
WHERE panel.PipelineStageId=@PipelineStageId AND (userRow.ClientId IS NULL OR userRow.ClientId=@ClientId)", new { context.PipelineStageId, context.ClientId }));
            else if (type.Equals("HiringRequester", StringComparison.OrdinalIgnoreCase))
                AddRange(type, await db.QueryAsync<string>(@"SELECT userRow.Email FROM recruitment_candidate_applications applicationRow
JOIN recruitment_open_positions positionRow ON positionRow.Id=applicationRow.PositionId
JOIN recruitment_requisitions requisition ON requisition.Id=positionRow.RequisitionId
JOIN authusers userRow ON userRow.Id=requisition.RequestedByUserId AND userRow.IsActive=TRUE
WHERE applicationRow.Id=@ApplicationId", new { context.ApplicationId }));
            else if (type.Equals("PositionRecruiter", StringComparison.OrdinalIgnoreCase))
                AddRange(type, await db.QueryAsync<string>(@"SELECT userRow.Email FROM recruitment_candidate_applications applicationRow
JOIN recruitment_open_positions positionRow ON positionRow.Id=applicationRow.PositionId
JOIN authusers userRow ON userRow.Id=COALESCE(NULLIF(applicationRow.RecruiterUserId,0),NULLIF(positionRow.RecruiterUserId,0)) AND userRow.IsActive=TRUE
WHERE applicationRow.Id=@ApplicationId", new { context.ApplicationId }));
        }
        return recipients.GroupBy(row => row.Email, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToList();

        void Add(string recipientType, string? email)
        {
            var value = (email ?? "").Trim();
            if (value.Length > 0 && System.Net.Mail.MailAddress.TryCreate(value, out var address))
                recipients.Add(new ResolvedRecipient(recipientType, address.Address));
        }

        void AddRange(string recipientType, IEnumerable<string> emails)
        {
            foreach (var email in emails) Add(recipientType, email);
        }
    }

    private static string NormalizeTrigger(string value) => (value ?? "").Trim().ToUpperInvariant() switch
    {
        "ONENTRY" => "OnEntry",
        "ONEXIT" => "OnExit",
        "ONSLAWARNING" => "OnSlaWarning",
        "ONSLABREACH" => "OnSlaBreach",
        "ONAPPROVAL" => "OnApproval",
        "ONSUBMISSION" => "OnSubmission",
        _ => ""
    };

    private static string Truncate(string value, int maximum) => string.IsNullOrEmpty(value)
        ? ""
        : value.Length <= maximum ? value : value[..maximum];

    private sealed class ActionRow : RecruitmentPipelineStageAction
    {
    }

    private sealed class ActionContext
    {
        public long ApplicationId { get; set; }
        public int ClientId { get; set; }
        public long CandidateId { get; set; }
        public string CandidateName { get; set; } = "";
        public string CandidateEmail { get; set; } = "";
        public string PositionTitle { get; set; } = "";
        public long StageInstanceId { get; set; }
        public long PipelineStageId { get; set; }
        public string StageName { get; set; } = "";
        public string StageType { get; set; } = "";
        public int WorkflowRequestorUserId { get; set; }
    }

    private sealed class SlaActionSource
    {
        public long ApplicationId { get; set; }
        public string TriggerEvent { get; set; } = "";
    }

    private sealed class RecipientConfiguration
    {
        public string RecipientType { get; set; } = "Candidate";
        public int? UserId { get; set; }
        public string RoleCode { get; set; } = "";
        public string EmailAddress { get; set; } = "";
    }

    private sealed record ResolvedRecipient(string RecipientType, string Email);
}
