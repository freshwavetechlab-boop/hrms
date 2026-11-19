using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed class RecruitmentPipelineConfigurationConflict(string message) : InvalidOperationException(message);

public partial class RecruitmentPipelineRepository
{
    internal static string CrudConfigurationHash(RecruitmentPipelineDefinition definition, RecruitmentPipelineVersion version)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            Definition = new { definition.Id, definition.ClientId, definition.PipelineCode, definition.PipelineName, definition.Description, definition.IsActive, definition.CurrentPublishedVersionId },
            version.Id, version.PipelineDefinitionId, version.Status, version.ScopeType, version.SlaMode, version.OverallSlaMinutes,
            version.AllRejectedRollback, version.Stages, version.Transitions, version.CloneWarnings
        }))));

    public async Task<(RecruitmentPipelineConfigurationResult? Row, string Error)> SavePipelineConfigurationAsync(SaveRecruitmentPipelineConfiguration request, AuthUser user)
    {
        if (user.Id <= 0 || !user.IsActive || user.MustChangePassword || !RecruitmentPermissions.CanConfigure(user)) throw new UnauthorizedAccessException();
        if (request.Definition is null || request.Configuration is null) return (null, "Provide the pipeline details and configuration.");
        var details = request.Definition; var configuration = request.Configuration;
        if (details.ClientId <= 0 || !SelectedClientAccess.Allows(user, details.ClientId) || user.ClientId.HasValue && user.ClientId != details.ClientId) throw new UnauthorizedAccessException();
        if (details.Id < 0 || configuration.Id < 0) return (null, "Select a valid pipeline configuration.");
        var code = Regex.Replace((details.PipelineCode ?? "").Trim().ToUpperInvariant(), "\\s+", "_");
        if (!Regex.IsMatch(code, "^[A-Z0-9_-]{2,80}$") || string.IsNullOrWhiteSpace(details.PipelineName) || details.PipelineName.Trim().Length > 180 || details.Description?.Length > 1000)
            return (null, "Provide a pipeline code (2–80 letters, numbers, underscore or hyphen), name and description within their limits.");
        if (configuration.Stages is null || configuration.Transitions is null || configuration.Stages.Count > 500 || configuration.Transitions.Count > 2000)
            return (null, "Provide a valid stage and transition configuration.");
        if (configuration.Stages.Where(s => s.Id != 0).GroupBy(s => s.Id).Any(g => g.Count() > 1)
            || configuration.Transitions.Where(t => t.Id > 0).GroupBy(t => t.Id).Any(g => g.Count() > 1)) return (null, "Each stage and transition must have a unique identity.");
        configuration.ScopeType = Canonical(["Application", "Position", "Hybrid"], configuration.ScopeType, "Application");
        configuration.SlaMode = Canonical(["StageEntry", "CumulativeFromAnchor"], configuration.SlaMode, "StageEntry");
        NormalizeStageCardScopes(configuration);
        configuration.AllRejectedRollback ??= new();
        configuration.AllRejectedRollback.TargetStageCode = (configuration.AllRejectedRollback.TargetStageCode ?? "").Trim().ToUpperInvariant();
        var referenceError = CloneReferenceError(configuration.Stages, configuration.Transitions);
        if (referenceError.Length > 0) return (null, referenceError);
        var rollbackError = RecruitmentRollbackRules.Validate(configuration.AllRejectedRollback, configuration.Stages);
        if (rollbackError.Length > 0) return (null, rollbackError);
        await using var db = Db(); await db.OpenAsync();
        // Validate requested role/user grants before opening the write transaction.
        var accessError = await RecruitmentPipelineStageAccess.ValidateSaveAsync(db, details.ClientId, configuration.Id, configuration.Stages, user);
        if (accessError.Length > 0) return (null, accessError);
        await using var tx = await db.BeginTransactionAsync();
        try
        {
            if (!await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM clients WHERE Id=@Id AND IsActive=TRUE)", new { Id = details.ClientId }, tx)) return (null, "Select an active client.");
            var existing = details.Id > 0 ? await db.QuerySingleOrDefaultAsync<RecruitmentPipelineDefinition>("SELECT * FROM recruitment_pipeline_definitions WHERE Id=@Id FOR UPDATE", new { details.Id }, tx) : null;
            if (details.Id > 0 && (existing is null || existing.ClientId != details.ClientId)) throw new UnauthorizedAccessException();
            long definitionId = details.Id, versionId = configuration.Id;
            RecruitmentPipelineVersion? before = null;
            if (existing is not null)
            {
                if (existing.CurrentPublishedVersionId > 0 && versionId != existing.CurrentPublishedVersionId)
                    throw new RecruitmentPipelineConfigurationConflict("The active configuration changed. Reload this pipeline before saving; existing mappings were not changed.");
                if (versionId > 0)
                {
                    var locked = await db.ExecuteScalarAsync<long?>("SELECT Id FROM recruitment_pipeline_versions WHERE Id=@Id AND PipelineDefinitionId=@DefinitionId FOR UPDATE", new { Id = versionId, DefinitionId = definitionId }, tx);
                    if (locked is null) throw new UnauthorizedAccessException();
                    before = await GetPipelineVersionAsync(versionId, user);
                    if (before is null) throw new UnauthorizedAccessException();
                    if (string.IsNullOrEmpty(request.ExpectedConfigurationHash) || request.ExpectedConfigurationHash != before.ConfigurationHash)
                        throw new RecruitmentPipelineConfigurationConflict("This pipeline changed since it was opened. Reload it before saving; no partial changes were saved.");
                    if (before.Status == "Retired") throw new RecruitmentPipelineConfigurationConflict("Open the active pipeline configuration before saving.");
                    if (before.Stages.Any(s => s.AccessPolicy?.Restricted == true) && !RecruitmentPipelineStageAccess.IsAdministrator(user)) throw new UnauthorizedAccessException();
                    if (before.CloneWarnings.Count > 0 && !configuration.AcknowledgeCloneWarnings) return (null, "Review the copied client mappings and stage access before saving this pipeline.");
                }
                else if (await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM recruitment_pipeline_versions WHERE PipelineDefinitionId=@Id)", new { Id = definitionId }, tx))
                    throw new RecruitmentPipelineConfigurationConflict("Open this pipeline's saved configuration before editing it.");
            }
            else if (versionId > 0) return (null, "A new pipeline cannot reuse another pipeline's configuration.");
            configuration.PipelineDefinitionId = definitionId > 0 ? definitionId : 1;
            if (before is null && (configuration.Stages.Any(s => s.Id > 0) || configuration.Transitions.Any(t => t.Id > 0)))
                return (null, "A new pipeline must use its own new stage and transition identities.");
            var validation = ValidatePipelineDraft(configuration);
            if (validation.Length > 0) return (null, validation);
            referenceError = await ValidateCrudReferencesAsync(db, tx, details.ClientId, configuration);
            if (referenceError.Length > 0) return (null, referenceError);
            if (before is not null)
            {
                var identityError = await ValidateCrudIdentitiesAsync(db, tx, before, configuration);
                if (identityError.Length > 0) return (null, identityError);
            }
            if (definitionId == 0)
                definitionId = await db.ExecuteScalarAsync<long>(@"INSERT INTO recruitment_pipeline_definitions(ClientId,PipelineCode,PipelineName,Description,IsActive,CreatedByUserId)
VALUES(@ClientId,@Code,@Name,@Description,@IsActive,@Actor); SELECT LAST_INSERT_ID();", new { details.ClientId, Code = code, Name = details.PipelineName.Trim(), Description = details.Description?.Trim() ?? "", details.IsActive, Actor = user.Id }, tx);
            else
                await db.ExecuteAsync(@"UPDATE recruitment_pipeline_definitions SET PipelineCode=@Code,PipelineName=@Name,Description=@Description,IsActive=@IsActive,UpdatedAtUtc=UTC_TIMESTAMP() WHERE Id=@Id",
                    new { Id = definitionId, Code = code, Name = details.PipelineName.Trim(), Description = details.Description?.Trim() ?? "", details.IsActive }, tx);
            if (versionId == 0)
                versionId = await db.ExecuteScalarAsync<long>(@"INSERT INTO recruitment_pipeline_versions(PipelineDefinitionId,VersionNumber,Status,ScopeType,SlaMode,OverallSlaMinutes,CreatedByUserId,PublishedByUserId,PublishedAtUtc)
VALUES(@DefinitionId,1,'Published',@ScopeType,@SlaMode,@OverallSlaMinutes,@Actor,@Actor,UTC_TIMESTAMP()); SELECT LAST_INSERT_ID();", new { DefinitionId = definitionId, configuration.ScopeType, configuration.SlaMode, configuration.OverallSlaMinutes, Actor = user.Id }, tx);
            else
                await db.ExecuteAsync(@"UPDATE recruitment_pipeline_versions SET Status='Published',ScopeType=@ScopeType,SlaMode=@SlaMode,OverallSlaMinutes=@OverallSlaMinutes,
PublishedByUserId=COALESCE(PublishedByUserId,@Actor),PublishedAtUtc=COALESCE(PublishedAtUtc,UTC_TIMESTAMP()) WHERE Id=@Id",
                    new { Id = versionId, configuration.ScopeType, configuration.SlaMode, configuration.OverallSlaMinutes, Actor = user.Id }, tx);
            details.Id = definitionId; details.PipelineCode = code; details.PipelineName = details.PipelineName.Trim(); details.Description = details.Description?.Trim() ?? "";
            configuration.Id = versionId; configuration.PipelineDefinitionId = definitionId;
            await ApplyCrudGraphAsync(db, tx, details.ClientId, versionId, before, configuration);
            await ReviewCloneWarningsOnSaveAsync(db, tx, details.ClientId, versionId, configuration.AcknowledgeCloneWarnings);
            await RecruitmentPipelineStageAccess.SaveAsync(db, tx, details.ClientId, versionId, configuration.Stages);
            await db.ExecuteAsync(@"INSERT INTO modulesettings(client_id,ModuleCode,IsEnabled,SettingsJson) VALUES(@ClientId,@Key,TRUE,@Json)
ON DUPLICATE KEY UPDATE IsEnabled=TRUE,SettingsJson=@Json", new { details.ClientId, Key = RecruitmentRollbackRules.Key(versionId), Json = JsonSerializer.Serialize(configuration.AllRejectedRollback) }, tx);
            await db.ExecuteAsync("UPDATE recruitment_pipeline_definitions SET CurrentPublishedVersionId=@VersionId,UpdatedAtUtc=UTC_TIMESTAMP() WHERE Id=@Id", new { Id = definitionId, VersionId = versionId }, tx);
            await db.ExecuteAsync(@"INSERT INTO recruitment_audit(EntityType,EntityId,Action,OldValueJson,NewValueJson,ChangedByUserId)
VALUES('RecruitmentPipelineDefinition',@Id,@Action,@Before,@After,@Actor)", new { Id = definitionId, Action = existing is null ? "Create pipeline configuration" : "Update pipeline configuration", Before = before is null ? null : JsonSerializer.Serialize(new { Definition = existing, Configuration = before }), After = JsonSerializer.Serialize(request), Actor = user.Id }, tx);
            await tx.CommitAsync();
            var savedDefinition = (await GetPipelinesAsync(details.ClientId, user)).Single(d => d.Id == definitionId);
            return (new(savedDefinition, (await GetPipelineVersionAsync(versionId, user))!), "");
        }
        catch (MySqlException error) when (error.Number == 1062) { return (null, "A pipeline or stage code already exists. Refresh and use a unique code; no partial changes were saved."); }
        catch (MySqlException error) when (error.Number is 1451 or 1452) { return (null, "This configuration has linked records. Keep the referenced steps or deactivate the pipeline instead; no partial changes were saved."); }
    }

    private static async Task<string> ValidateCrudIdentitiesAsync(MySqlConnection db, MySqlTransaction tx, RecruitmentPipelineVersion before, SaveRecruitmentPipelineVersion request)
    {
        var oldStages = before.Stages.ToDictionary(s => s.Id);
        var oldTransitions = before.Transitions.ToDictionary(t => t.Id);
        if (request.Stages.Any(s => s.Id > 0 && !oldStages.ContainsKey(s.Id)) || request.Transitions.Any(t => t.Id > 0 && !oldTransitions.ContainsKey(t.Id))) return "A stage or transition belongs to a different pipeline. Reload this configuration.";
        var usedStages = (await db.QueryAsync<long>(@"SELECT DISTINCT PipelineStageId FROM recruitment_application_stage_instances WHERE PipelineStageId IN @Ids
UNION SELECT DISTINCT PipelineStageId FROM recruitment_position_stage_instances WHERE PipelineStageId IN @Ids
UNION SELECT DISTINCT PipelineStageId FROM recruitment_process_documents WHERE PipelineStageId IN @Ids
UNION SELECT DISTINCT PipelineStageId FROM recruitment_hiring_case_advance_requests WHERE PipelineStageId IN @Ids
UNION SELECT DISTINCT a.PipelineStageId FROM recruitment_pipeline_stage_actions a JOIN recruitment_stage_action_executions e ON e.StageActionId=a.Id WHERE a.PipelineStageId IN @Ids
UNION SELECT DISTINCT a.PipelineStageId FROM recruitment_pipeline_stage_actions a JOIN recruitment_profile_batch_notification_deliveries d ON d.StageActionId=a.Id WHERE a.PipelineStageId IN @Ids
UNION SELECT DISTINCT c.PipelineStageId FROM recruitment_interview_stage_configurations c JOIN recruitment_interviews i ON i.RoundConfigurationId=c.Id WHERE c.PipelineStageId IN @Ids
UNION SELECT DISTINCT c.PipelineStageId FROM recruitment_stage_offer_configurations c JOIN recruitment_sla_adjustments a ON a.StageOfferConfigurationId=c.Id WHERE c.PipelineStageId IN @Ids
UNION SELECT DISTINCT c.PipelineStageId FROM recruitment_interview_stage_configurations c JOIN recruitment_interview_stage_competencies r ON r.InterviewStageConfigurationId=c.Id JOIN recruitment_interview_feedback_competency_scores s ON s.InterviewStageCompetencyId=r.Id WHERE c.PipelineStageId IN @Ids", new { Ids = oldStages.Keys.ToArray() }, tx)).ToHashSet();
        foreach (var id in usedStages)
        {
            var next = request.Stages.FirstOrDefault(s => s.Id == id); var old = oldStages[id];
            if (next is null) return $"{old.StageName} has linked hiring/candidate history and cannot be deleted. Keep this step or deactivate the pipeline.";
            if (!next.StageCode.Trim().Equals(old.StageCode, StringComparison.OrdinalIgnoreCase) || next.StageType != old.StageType || next.CardScope != old.CardScope || next.IsInitial != old.IsInitial || next.IsTerminal != old.IsTerminal || next.IsActive != old.IsActive)
                return $"{old.StageName} has linked hiring/candidate records. Its identity, card scope and lifecycle cannot change; its name, SLA and responsibility can be edited.";
        }
        var usedTransitions = (await db.QueryAsync<long>(@"SELECT DISTINCT TransitionId FROM recruitment_pipeline_transition_requests WHERE TransitionId IN @Ids
UNION SELECT DISTINCT t.Id FROM recruitment_pipeline_transitions t JOIN recruitment_hiring_case_advance_requests r ON r.PipelineStageId=t.FromStageId AND r.OutcomeCode=t.OutcomeCode WHERE t.Id IN @Ids", new { Ids = oldTransitions.Keys.ToArray() }, tx)).ToHashSet();
        usedTransitions.UnionWith(oldTransitions.Values.Where(t => usedStages.Contains(t.FromStageId) || usedStages.Contains(t.ToStageId)).Select(t => t.Id));
        foreach (var id in usedTransitions)
        {
            var next = request.Transitions.FirstOrDefault(t => t.Id == id); var old = oldTransitions[id];
            if (next is null) return "A transition belongs to a live or historical journey and cannot be deleted. Disable it for future use instead.";
            if (next.FromStageId != old.FromStageId || next.ToStageId != old.ToStageId || !next.OutcomeCode.Trim().Equals(old.OutcomeCode, StringComparison.OrdinalIgnoreCase)
                || next.ApprovalWorkflowId != old.ApprovalWorkflowId || next.RequiresReason != old.RequiresReason)
                return "A transition with linked approvals/history must keep its source, destination, outcome and approval binding.";
            var pending = await db.ExecuteScalarAsync<bool>(@"SELECT EXISTS(SELECT 1 FROM recruitment_pipeline_transition_requests WHERE TransitionId=@Id AND Status='Pending Approval')
OR EXISTS(SELECT 1 FROM recruitment_hiring_case_advance_requests WHERE PipelineStageId=@FromStageId AND OutcomeCode=@OutcomeCode AND Status='Pending Approval')", new { Id = id, old.FromStageId, old.OutcomeCode }, tx);
            if (pending && (next.IsActive != old.IsActive || JsonSerializer.Serialize(next.Rules) != JsonSerializer.Serialize(old.Rules))) return "A transition has a pending approval. Complete that approval before changing its rules or availability.";
        }
        var hasMapping = await db.ExecuteScalarAsync<bool>(@"SELECT EXISTS(SELECT 1 FROM recruitment_position_pipeline_assignments WHERE PipelineVersionId=@Id)
OR EXISTS(SELECT 1 FROM recruitment_application_pipeline_instances WHERE PipelineVersionId=@Id)
OR EXISTS(SELECT 1 FROM recruitment_position_pipeline_instances WHERE PipelineVersionId=@Id)", new { before.Id }, tx);
        if (hasMapping && (before.ScopeType != request.ScopeType || before.SlaMode != request.SlaMode)) return "A mapped pipeline must keep its hiring/candidate scope and SLA mode.";
        return "";
    }

    private static async Task ApplyCrudGraphAsync(MySqlConnection db, MySqlTransaction tx, int clientId, long versionId, RecruitmentPipelineVersion? before, SaveRecruitmentPipelineVersion request)
    {
        var oldStages = before?.Stages ?? []; var oldTransitions = before?.Transitions ?? [];
        var removedTransitions = oldTransitions.Where(t => request.Transitions.All(n => n.Id != t.Id)).Select(t => t.Id).ToArray();
        if (removedTransitions.Length > 0) await db.ExecuteAsync("DELETE FROM recruitment_pipeline_transition_rules WHERE TransitionId IN @Ids; DELETE FROM recruitment_pipeline_transitions WHERE Id IN @Ids", new { Ids = removedTransitions }, tx);
        var removedStages = oldStages.Where(s => request.Stages.All(n => n.Id != s.Id)).Select(s => s.Id).ToArray();
        // Free unique order/code slots before a legitimate reorder or code swap.
        var nonce = Guid.NewGuid().ToString("N");
        for (var index = 0; index < oldStages.Count; index++)
            await db.ExecuteAsync("UPDATE recruitment_pipeline_stages SET DisplayOrder=@Order,StageCode=@Code WHERE Id=@Id", new { oldStages[index].Id, Order = -1000000 - index, Code = "CRUD_" + nonce + "_" + index }, tx);
        for (var index = 0; index < oldTransitions.Count; index++)
            if (!removedTransitions.Contains(oldTransitions[index].Id)) await db.ExecuteAsync("UPDATE recruitment_pipeline_transitions SET OutcomeCode=@Code WHERE Id=@Id", new { oldTransitions[index].Id, Code = "CRUD_" + nonce + "_" + index }, tx);
        var ids = new Dictionary<long, long>(); var codes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var stage in request.Stages.OrderBy(s => s.DisplayOrder))
        {
            var inputId = stage.Id; var values = new DynamicParameters(stage); values.Add("PipelineVersionId", versionId); values.Add("StageCode", stage.StageCode.Trim().ToUpperInvariant()); values.Add("StageName", stage.StageName.Trim());
            var stageId = stage.Id;
            if (stageId > 0)
                await db.ExecuteAsync(@"UPDATE recruitment_pipeline_stages SET StageCode=@StageCode,StageName=@StageName,StageType=@StageType,CardScope=@CardScope,StageNumber=@StageNumber,DisplayOrder=@DisplayOrder,
SlaDurationMinutes=@SlaDurationMinutes,SlaWarningMinutes=@SlaWarningMinutes,TargetOffsetMinutes=@TargetOffsetMinutes,StakeholderCode=@StakeholderCode,AllowPause=@AllowPause,PauseBehavior=@PauseBehavior,
ApprovalWorkflowId=@ApprovalWorkflowId,RequiresApproval=@RequiresApproval,CalendarEnabled=@CalendarEnabled,AllowSkip=@AllowSkip,IsInitial=@IsInitial,IsTerminal=@IsTerminal,IsActive=@IsActive WHERE Id=@Id AND PipelineVersionId=@PipelineVersionId", values, tx);
            else
                stageId = await db.ExecuteScalarAsync<long>(@"INSERT INTO recruitment_pipeline_stages(PipelineVersionId,StageCode,StageName,StageType,CardScope,StageNumber,DisplayOrder,SlaDurationMinutes,SlaWarningMinutes,TargetOffsetMinutes,StakeholderCode,AllowPause,PauseBehavior,ApprovalWorkflowId,RequiresApproval,CalendarEnabled,AllowSkip,IsInitial,IsTerminal,IsActive)
VALUES(@PipelineVersionId,@StageCode,@StageName,@StageType,@CardScope,@StageNumber,@DisplayOrder,@SlaDurationMinutes,@SlaWarningMinutes,@TargetOffsetMinutes,@StakeholderCode,@AllowPause,@PauseBehavior,@ApprovalWorkflowId,@RequiresApproval,@CalendarEnabled,@AllowSkip,@IsInitial,@IsTerminal,@IsActive); SELECT LAST_INSERT_ID();", values, tx);
            if (inputId != 0) ids[inputId] = stageId; codes[stage.StageCode.Trim()] = stageId;
            await ReconcileCrudStageChildrenAsync(db, tx, stageId, stage, clientId);
            stage.Id = stageId; stage.PipelineVersionId = versionId;
        }
        foreach (var transition in request.Transitions.OrderBy(t => t.DisplayOrder))
        {
            var from = ResolveStageId(transition.FromStageId, transition.FromStageCode, ids, codes); var to = ResolveStageId(transition.ToStageId, transition.ToStageCode, ids, codes);
            if (from == 0 || to == 0 || from == to) throw new InvalidOperationException("Transitions must link two steps in this pipeline.");
            var values = new DynamicParameters(transition); values.Add("PipelineVersionId", versionId); values.Add("FromStageId", from); values.Add("ToStageId", to); values.Add("OutcomeCode", transition.OutcomeCode.Trim().ToUpperInvariant());
            var transitionId = transition.Id;
            if (transitionId > 0)
                await db.ExecuteAsync(@"UPDATE recruitment_pipeline_transitions SET FromStageId=@FromStageId,ToStageId=@ToStageId,OutcomeCode=@OutcomeCode,ActionLabel=@ActionLabel,ApprovalWorkflowId=@ApprovalWorkflowId,RequiresReason=@RequiresReason,IsActive=@IsActive,DisplayOrder=@DisplayOrder WHERE Id=@Id AND PipelineVersionId=@PipelineVersionId", values, tx);
            else
                transitionId = await db.ExecuteScalarAsync<long>(@"INSERT INTO recruitment_pipeline_transitions(PipelineVersionId,FromStageId,ToStageId,OutcomeCode,ActionLabel,ApprovalWorkflowId,RequiresReason,IsActive,DisplayOrder)
VALUES(@PipelineVersionId,@FromStageId,@ToStageId,@OutcomeCode,@ActionLabel,@ApprovalWorkflowId,@RequiresReason,@IsActive,@DisplayOrder); SELECT LAST_INSERT_ID();", values, tx);
            await ReconcileCrudTransitionRulesAsync(db, tx, transitionId, transition.Rules);
            transition.Id = transitionId; transition.PipelineVersionId = versionId; transition.FromStageId = from; transition.ToStageId = to;
        }
        // Retarget retained unused edges before deleting an unused step, otherwise
        // its ON DELETE CASCADE would destroy the retained transition identity.
        if (removedStages.Length > 0) await DeleteUnusedCrudStagesAsync(db, tx, removedStages);
    }

    private static async Task DeleteUnusedCrudStagesAsync(MySqlConnection db, MySqlTransaction tx, long[] ids)
    {
        await db.ExecuteAsync(@"DELETE recipient FROM recruitment_stage_action_recipients recipient JOIN recruitment_pipeline_stage_actions actionRow ON actionRow.Id=recipient.StageActionId WHERE actionRow.PipelineStageId IN @Ids;
DELETE rubric FROM recruitment_interview_stage_competencies rubric JOIN recruitment_interview_stage_configurations configuration ON configuration.Id=rubric.InterviewStageConfigurationId WHERE configuration.PipelineStageId IN @Ids;
DELETE FROM recruitment_pipeline_stage_actions WHERE PipelineStageId IN @Ids;
DELETE FROM recruitment_stage_default_panel_members WHERE PipelineStageId IN @Ids;
DELETE FROM recruitment_stage_ats_configurations WHERE PipelineStageId IN @Ids;
DELETE FROM recruitment_stage_external_form_configurations WHERE PipelineStageId IN @Ids;
DELETE FROM recruitment_stage_attachment_requirements WHERE PipelineStageId IN @Ids;
DELETE FROM recruitment_stage_process_document_requirements WHERE PipelineStageId IN @Ids;
DELETE FROM recruitment_stage_offer_configurations WHERE PipelineStageId IN @Ids;
DELETE FROM recruitment_interview_stage_configurations WHERE PipelineStageId IN @Ids;
DELETE FROM recruitment_pipeline_stages WHERE Id IN @Ids", new { Ids = ids }, tx);
    }
}
