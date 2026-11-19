using System.Reflection;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public partial class RecruitmentPipelineRepository
{
    // Editing a configuration must not replace identities referenced by running
    // executions, interview scores or historical SLA adjustments.
    private static async Task ReconcileCrudStageChildrenAsync(MySqlConnection db, MySqlTransaction tx, long stageId, RecruitmentPipelineStage stage, int clientId)
    {
        var actions = stage.Actions.ToList();
        if (stage.AtsConfiguration?.AutoScoreOnEntry == true && !actions.Any(a => a.IsActive && a.ActionCode.Equals("RUN_ATS_SCORE", StringComparison.OrdinalIgnoreCase)))
            actions.Add(new() { TriggerEvent = "OnEntry", ActionCode = "RUN_ATS_SCORE", ExecutionOrder = actions.Count == 0 ? 1 : actions.Max(a => a.ExecutionOrder) + 1 });
        foreach (var action in actions)
        {
            action.TriggerEvent = NormalizeTriggerEvent(action.TriggerEvent);
            action.ActionCode = action.ActionCode.Trim().ToUpperInvariant();
        }
        var usedActions = (await db.QueryAsync<long>(@"SELECT a.Id FROM recruitment_pipeline_stage_actions a
WHERE a.PipelineStageId=@StageId AND (EXISTS(SELECT 1 FROM recruitment_stage_action_executions e WHERE e.StageActionId=a.Id)
OR EXISTS(SELECT 1 FROM recruitment_profile_batch_notification_deliveries d WHERE d.StageActionId=a.Id))", new { StageId = stageId }, tx)).ToHashSet();
        await ReconcileCrudRowsAsync(db, tx, "recruitment_pipeline_stage_actions", "PipelineStageId", stageId, actions,
            ["TriggerEvent", "ActionCode", "ExecutionOrder", "IsBlocking", "WorkflowId", "TemplateId", "IsActive"],
            row => $"{row.TriggerEvent}|{row.ActionCode}|{row.ExecutionOrder}",
            (before, after) =>
            {
                if (usedActions.Contains(before.Id) && (before.TriggerEvent != after.TriggerEvent || before.ActionCode != after.ActionCode))
                    throw new InvalidOperationException($"Action {before.ActionCode} already has execution history. Disable it and add a separate action instead of changing its identity.");
                return Task.CompletedTask;
            },
            row => usedActions.Contains(row.Id) ? Task.FromException(new InvalidOperationException($"Action {row.ActionCode} already has execution history. Disable it instead of deleting it.")) : Task.CompletedTask,
            parkActionOrder: true);
        foreach (var action in actions)
        {
            foreach (var row in action.Recipients)
            {
                row.RecipientType = Canonical(["Candidate", "InterviewPanelMembers", "StageDefaultPanelMembers", "SpecificUser", "UserRole", "StaticEmail", "HiringRequester", "PositionRecruiter"], row.RecipientType, "Candidate");
                row.RoleCode = (row.RoleCode ?? "").Trim(); row.EmailAddress = (row.EmailAddress ?? "").Trim();
            }
            await ReconcileCrudRowsAsync(db, tx, "recruitment_stage_action_recipients", "StageActionId", action.Id, action.Recipients,
                ["RecipientType", "UserId", "RoleCode", "EmailAddress", "DisplayOrder", "IsActive"],
                row => $"{row.RecipientType}|{row.UserId}|{row.RoleCode}|{row.EmailAddress}|{row.DisplayOrder}");
        }
        foreach (var row in stage.DefaultPanelMembers) row.PanelRole = string.IsNullOrWhiteSpace(row.PanelRole) ? "Panelist" : row.PanelRole.Trim();
        await ReconcileCrudRowsAsync(db, tx, "recruitment_stage_default_panel_members", "PipelineStageId", stageId, stage.DefaultPanelMembers,
            ["PanelUserId", "PanelRole", "IsRequired", "DisplayOrder"], row => row.PanelUserId.ToString());

        if (stage.AtsConfiguration is { } ats)
        {
            ats.AdvanceOutcomeCode = ats.AdvanceOutcomeCode.Trim().ToUpperInvariant(); ats.RejectOutcomeCode = ats.RejectOutcomeCode.Trim().ToUpperInvariant();
        }
        await ReconcileCrudRowsAsync(db, tx, "recruitment_stage_ats_configurations", "PipelineStageId", stageId, OptionalCrudRow(stage.AtsConfiguration),
            ["ScoringProfileId", "MinimumAdvanceScore", "MaximumRejectScore", "AutoScoreOnEntry", "AutoAdvance", "AutoReject", "RequireHumanConfirmation", "AdvanceOutcomeCode", "RejectOutcomeCode"], _ => "single");
        await ReconcileCrudRowsAsync(db, tx, "recruitment_stage_external_form_configurations", "PipelineStageId", stageId, OptionalCrudRow(stage.ExternalFormConfiguration),
            ["FormVersionId", "SubmissionRequired", "AllowSaveDraft", "ActionTokenValidityMinutes", "ActionTokenMaximumUses"], _ => "single");
        await ReconcileCrudRowsAsync(db, tx, "recruitment_stage_attachment_requirements", "PipelineStageId", stageId, stage.AttachmentRequirements,
            ["AttachmentFieldConfigurationId", "IsRequired", "MinimumFileCount", "MaximumFileCount", "RequiresVerification", "DisplayOrder"], row => row.AttachmentFieldConfigurationId.ToString());
        foreach (var row in stage.ProcessDocumentRequirements) row.DocumentType = row.DocumentType.Trim().ToUpperInvariant();
        await ReconcileCrudRowsAsync(db, tx, "recruitment_stage_process_document_requirements", "PipelineStageId", stageId, stage.ProcessDocumentRequirements,
            ["DocumentType", "TemplateId", "IsRequired", "RequiresSignature", "DisplayOrder"], row => row.DocumentType);
        if (stage.OfferConfiguration is { } offer)
        {
            offer.BudgetBasis = NormalizeBudgetBasis(offer.BudgetBasis);
            offer.NegotiationSlaStageCodes = string.Join(',', (offer.NegotiationSlaStageCodes ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase));
        }
        await ReconcileCrudRowsAsync(db, tx, "recruitment_stage_offer_configurations", "PipelineStageId", stageId, OptionalCrudRow(stage.OfferConfiguration),
            ["OfferTemplateId", "ApprovalWorkflowId", "BudgetBasis", "MaximumVariancePercent", "RequireApprovalWhenVarianceExceeded", "VarianceApprovalWorkflowId", "CandidateResponseValidityDays", "RequireAcceptedOfferToAdvance", "NegotiationSlaExtensionEnabled", "NegotiationThresholdPercent", "NegotiationSlaExtensionMinutes", "NegotiationSlaStageCodes"], _ => "single",
            validateDelete: async row =>
            {
                if (await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM recruitment_sla_adjustments WHERE StageOfferConfigurationId=@Id)", new { row.Id }, tx))
                    throw new InvalidOperationException("This offer configuration has recorded SLA adjustments and cannot be removed.");
            });

        var interview = stage.StageType.Equals("Interview", StringComparison.OrdinalIgnoreCase) ? stage.InterviewConfiguration ?? new() : null;
        if (interview is not null)
        {
            interview.RoundNumber = Math.Max(1, interview.RoundNumber); interview.DefaultDurationMinutes = Math.Max(1, interview.DefaultDurationMinutes);
            interview.MinimumPanelCount = Math.Max(1, interview.MinimumPanelCount); interview.MinimumPassingScore = Math.Clamp(interview.MinimumPassingScore, 0, 100);
            interview.InterviewType = interview.InterviewType.Trim();
            interview.ScoreInputMode = Canonical(["PercentageWeighted", "Points"], interview.ScoreInputMode, "PercentageWeighted");
            interview.PanelAggregationMethod = Canonical(["Average", "Median", "Chairperson"], interview.PanelAggregationMethod, "Average");
        }
        await ReconcileCrudRowsAsync(db, tx, "recruitment_interview_stage_configurations", "PipelineStageId", stageId, OptionalCrudRow(interview),
            ["RoundNumber", "InterviewType", "DefaultDurationMinutes", "MinimumPanelCount", "MinimumPassingScore", "ScoreInputMode", "PanelAggregationMethod", "FeedbackRequired", "CalendarEnabled", "AllowReschedule"], _ => "single",
            validateDelete: async row =>
            {
                if (await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM recruitment_interviews WHERE RoundConfigurationId=@Id)", new { row.Id }, tx))
                    throw new InvalidOperationException("This interview round is referenced by interviews and cannot be removed.");
            });
        if (interview is not null)
        {
            foreach (var competency in interview.Competencies)
                if (!await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM recruitment_interview_competency_definitions WHERE Id=@Id AND ClientId=@ClientId AND IsActive=TRUE)", new { Id = competency.CompetencyId, ClientId = clientId }, tx))
                    throw new InvalidOperationException("Interview competency does not belong to this client or is inactive.");
            await ReconcileCrudRowsAsync(db, tx, "recruitment_interview_stage_competencies", "InterviewStageConfigurationId", interview.Id, interview.Competencies,
                ["CompetencyId", "WeightPercent", "MinimumScore", "DisplayOrder"], row => row.CompetencyId.ToString(),
                validateUpdate: async (before, after) =>
                {
                    if (before.CompetencyId != after.CompetencyId && await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM recruitment_interview_feedback_competency_scores WHERE InterviewStageCompetencyId=@Id)", new { before.Id }, tx))
                        throw new InvalidOperationException("A scored competency cannot be replaced with a different competency. Its original scores and identity must remain available.");
                },
                validateDelete: async row =>
                {
                    if (await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM recruitment_interview_feedback_competency_scores WHERE InterviewStageCompetencyId=@Id)", new { row.Id }, tx))
                        throw new InvalidOperationException("This competency has interview scores and cannot be deleted.");
                });
        }
    }

    private static Task ReconcileCrudTransitionRulesAsync(MySqlConnection db, MySqlTransaction tx, long transitionId, IReadOnlyList<RecruitmentPipelineTransitionRule> rules)
    {
        foreach (var row in rules) { row.RuleType = row.RuleType.Trim().ToUpperInvariant(); row.ComparisonOperator = row.ComparisonOperator.Trim().ToUpperInvariant(); }
        return ReconcileCrudRowsAsync(db, tx, "recruitment_pipeline_transition_rules", "TransitionId", transitionId, rules,
            ["RuleType", "ComparisonOperator", "TextValue", "IntegerValue", "DecimalValue", "BooleanValue", "IsMandatory", "ErrorMessage", "DisplayOrder"], row => $"{row.RuleType}|{row.DisplayOrder}");
    }

    private static List<T> OptionalCrudRow<T>(T? row) where T : class => row is null ? [] : [row];

    // Table/column names are constants supplied by the private call sites above,
    // never values from an HTTP request. Every positive child ID is parent-bound.
    private static async Task ReconcileCrudRowsAsync<T>(MySqlConnection db, MySqlTransaction tx, string table, string parentColumn, long parentId,
        IReadOnlyList<T> requested, string[] columns, Func<T, string> naturalKey,
        Func<T, T, Task>? validateUpdate = null, Func<T, Task>? validateDelete = null, bool parkActionOrder = false) where T : class
    {
        var idProperty = typeof(T).GetProperty("Id", BindingFlags.Public | BindingFlags.Instance) ?? throw new InvalidOperationException("Configuration row has no identity.");
        long Id(T row) => Convert.ToInt64(idProperty.GetValue(row));
        var existing = (await db.QueryAsync<T>($"SELECT * FROM {table} WHERE {parentColumn}=@ParentId ORDER BY Id FOR UPDATE", new { ParentId = parentId }, tx)).ToList();
        var existingById = existing.ToDictionary(Id);
        var kept = new HashSet<long>();
        var planned = new List<(T Row, T? Before)>();
        foreach (var row in requested)
        {
            var id = Id(row); T? before = null;
            if (id > 0)
            {
                if (!existingById.TryGetValue(id, out before)) throw new InvalidOperationException("A configuration row belongs to a different pipeline stage. Reload the pipeline before saving.");
            }
            else before = existing.FirstOrDefault(old => !kept.Contains(Id(old)) && naturalKey(old).Equals(naturalKey(row), StringComparison.OrdinalIgnoreCase));
            if (before is not null)
            {
                if (!kept.Add(Id(before))) throw new InvalidOperationException("A configuration row was supplied more than once.");
                if (validateUpdate is not null) await validateUpdate(before, row);
            }
            planned.Add((row, before));
        }
        var omitted = existing.Where(row => !kept.Contains(Id(row))).ToArray();
        foreach (var row in omitted) if (validateDelete is not null) await validateDelete(row);
        foreach (var row in omitted)
        {
            // Recipient rows are not FK children in older installations.
            if (table == "recruitment_pipeline_stage_actions") await db.ExecuteAsync("DELETE FROM recruitment_stage_action_recipients WHERE StageActionId=@Id", new { Id = Id(row) }, tx);
            await db.ExecuteAsync($"DELETE FROM {table} WHERE Id=@Id AND {parentColumn}=@ParentId", new { Id = Id(row), ParentId = parentId }, tx);
        }
        if (parkActionOrder)
            for (var index = 0; index < existing.Count; index++)
                if (kept.Contains(Id(existing[index]))) await db.ExecuteAsync("UPDATE recruitment_pipeline_stage_actions SET ExecutionOrder=@Order WHERE Id=@Id AND PipelineStageId=@ParentId", new { Order = -1000000000 + index, Id = Id(existing[index]), ParentId = parentId }, tx);
        foreach (var item in planned)
        {
            var values = new DynamicParameters(item.Row); values.Add("ParentId", parentId);
            long id;
            if (item.Before is not null)
            {
                id = Id(item.Before); values.Add("Id", id);
                await db.ExecuteAsync($"UPDATE {table} SET {string.Join(',', columns.Select(column => column + "=@" + column))} WHERE Id=@Id AND {parentColumn}=@ParentId", values, tx);
            }
            else
                id = await db.ExecuteScalarAsync<long>($"INSERT INTO {table} ({parentColumn},{string.Join(',', columns)}) VALUES (@ParentId,{string.Join(',', columns.Select(column => "@" + column))});SELECT LAST_INSERT_ID();", values, tx);
            idProperty.SetValue(item.Row, id);
        }
    }
}
