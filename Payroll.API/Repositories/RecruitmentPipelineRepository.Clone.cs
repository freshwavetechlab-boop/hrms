using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public partial class RecruitmentPipelineRepository
{
    public async Task<(RecruitmentPipelineCloneResult? Row, string Error)> CreateSimilarPipelineAsync(long definitionId,
        CreateSimilarRecruitmentPipelineRequest request, AuthUser user)
    {
        if (user.Id <= 0 || !user.IsActive || user.MustChangePassword || !RecruitmentPermissions.CanConfigure(user)
            || !SelectedClientAccess.Allows(user, request.TargetClientId)) throw new UnauthorizedAccessException();
        var code = Regex.Replace((request.PipelineCode ?? "").Trim().ToUpperInvariant(), "\\s+", "_");
        if (!Regex.IsMatch(code, "^[A-Z0-9_-]{2,80}$", RegexOptions.CultureInvariant)) return (null, "Enter a new pipeline code (2–80 letters, numbers, underscore or hyphen).");
        if (string.IsNullOrWhiteSpace(request.PipelineName) || request.PipelineName.Trim().Length > 180 || request.Description?.Length > 1000)
            return (null, "Enter a pipeline name up to 180 characters and a description up to 1000 characters.");
        if (definitionId <= 0 || request.SourceVersionId <= 0) return (null, "Select the exact pipeline version to copy.");
        await using var db = Db(); await db.OpenAsync();
        await using var tx = await db.BeginTransactionAsync();
        var sourceDefinition = await db.QuerySingleOrDefaultAsync<RecruitmentPipelineDefinition>(
            "SELECT * FROM recruitment_pipeline_definitions WHERE Id=@Id FOR UPDATE", new { Id = definitionId }, tx);
        if (sourceDefinition is null || !SelectedClientAccess.Allows(user, sourceDefinition.ClientId)) throw new UnauthorizedAccessException();
        if (!await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM clients WHERE Id=@Id AND IsActive=TRUE)", new { Id = request.TargetClientId }, tx))
            return (null, "Select an active target client in your permitted scope.");
        // Lock the version before reading its aggregate. Existing draft saves update
        // this row before replacing stage children, so the copied snapshot stays stable.
        var sourceVersionId = await db.ExecuteScalarAsync<long?>("SELECT Id FROM recruitment_pipeline_versions WHERE Id=@Id AND PipelineDefinitionId=@Definition FOR UPDATE",
            new { Id = request.SourceVersionId, Definition = definitionId }, tx);
        if (!sourceVersionId.HasValue) return (null, "The selected version does not belong to this pipeline.");
        var source = await GetPipelineVersionAsync(sourceVersionId.Value, user);
        if (source is null) return (null, "The selected pipeline version is unavailable.");
        SaveRecruitmentPipelineVersion draft;
        try { draft = BuildSimilarPipelineDraft(source); }
        catch (InvalidOperationException error) { return (null, error.Message); }
        if (draft.Stages.Any(s => s.AccessPolicy.Restricted) && !RecruitmentPipelineStageAccess.IsAdministrator(user)) throw new UnauthorizedAccessException();
        var warnings = new List<string>(source.CloneWarnings);
        if (sourceDefinition.ClientId != request.TargetClientId)
            await ReviewCrossClientCloneReferencesAsync(db, tx, request.TargetClientId, draft, warnings);
        try
        {
            var newDefinitionId = await db.ExecuteScalarAsync<long>(@"INSERT INTO recruitment_pipeline_definitions
(ClientId,PipelineCode,PipelineName,Description,IsActive,CreatedByUserId)
VALUES(@ClientId,@Code,@Name,@Description,TRUE,@Actor); SELECT LAST_INSERT_ID();",
                new { ClientId = request.TargetClientId, Code = code, Name = request.PipelineName.Trim(), Description = request.Description?.Trim() ?? sourceDefinition.Description, Actor = user.Id }, tx);
            var versionId = await db.ExecuteScalarAsync<long>(@"INSERT INTO recruitment_pipeline_versions
(PipelineDefinitionId,VersionNumber,Status,ScopeType,SlaMode,OverallSlaMinutes,CreatedByUserId)
VALUES(@Definition,1,'Draft',@ScopeType,@SlaMode,@OverallSlaMinutes,@Actor); SELECT LAST_INSERT_ID();",
                new { Definition = newDefinitionId, draft.ScopeType, draft.SlaMode, draft.OverallSlaMinutes, Actor = user.Id }, tx);
            var ids = new Dictionary<long, long>();
            foreach (var stage in draft.Stages.OrderBy(s => s.DisplayOrder))
            {
                var values = new DynamicParameters(stage); values.Add("PipelineVersionId", versionId);
                var stageId = await db.ExecuteScalarAsync<long>(@"INSERT INTO recruitment_pipeline_stages
(PipelineVersionId,StageCode,StageName,StageType,CardScope,StageNumber,DisplayOrder,SlaDurationMinutes,SlaWarningMinutes,TargetOffsetMinutes,StakeholderCode,AllowPause,PauseBehavior,ApprovalWorkflowId,RequiresApproval,CalendarEnabled,AllowSkip,IsInitial,IsTerminal,IsActive)
VALUES(@PipelineVersionId,@StageCode,@StageName,@StageType,@CardScope,@StageNumber,@DisplayOrder,@SlaDurationMinutes,@SlaWarningMinutes,@TargetOffsetMinutes,@StakeholderCode,@AllowPause,@PauseBehavior,@ApprovalWorkflowId,@RequiresApproval,@CalendarEnabled,@AllowSkip,@IsInitial,@IsTerminal,@IsActive); SELECT LAST_INSERT_ID();", values, tx);
                ids.Add(stage.Id, stageId);
                await InsertStageBehaviorAsync(db, tx, stageId, stage);
                if (stage.StageType.Equals("Interview", StringComparison.OrdinalIgnoreCase))
                {
                    var interview = stage.InterviewConfiguration is null ? null : JsonSerializer.Deserialize<RecruitmentInterviewStageConfiguration>(JsonSerializer.Serialize(stage.InterviewConfiguration));
                    interview?.Competencies.RemoveAll(c => c.CompetencyId <= 0);
                    await InsertInterviewStageConfigurationAsync(db, tx, stageId, interview, request.TargetClientId);
                }
            }
            foreach (var transition in draft.Transitions.OrderBy(t => t.DisplayOrder))
            {
                if (!ids.TryGetValue(transition.FromStageId, out var from) || !ids.TryGetValue(transition.ToStageId, out var to))
                    throw new InvalidOperationException("The source pipeline has a transition outside its configured stages. No copy was saved.");
                var values = new DynamicParameters(transition); values.Add("PipelineVersionId", versionId); values.Add("FromStageId", from); values.Add("ToStageId", to);
                var transitionId = await db.ExecuteScalarAsync<long>(@"INSERT INTO recruitment_pipeline_transitions
(PipelineVersionId,FromStageId,ToStageId,OutcomeCode,ActionLabel,ApprovalWorkflowId,RequiresReason,IsActive,DisplayOrder)
VALUES(@PipelineVersionId,@FromStageId,@ToStageId,@OutcomeCode,@ActionLabel,@ApprovalWorkflowId,@RequiresReason,@IsActive,@DisplayOrder); SELECT LAST_INSERT_ID();", values, tx);
                foreach (var rule in transition.Rules)
                {
                    var ruleValues = new DynamicParameters(rule); ruleValues.Add("TransitionId", transitionId);
                    await db.ExecuteAsync(@"INSERT INTO recruitment_pipeline_transition_rules
(TransitionId,RuleType,ComparisonOperator,TextValue,IntegerValue,DecimalValue,BooleanValue,IsMandatory,ErrorMessage,DisplayOrder)
VALUES(@TransitionId,@RuleType,@ComparisonOperator,@TextValue,@IntegerValue,@DecimalValue,@BooleanValue,@IsMandatory,@ErrorMessage,@DisplayOrder)", ruleValues, tx);
                }
            }
            await db.ExecuteAsync(@"INSERT INTO modulesettings(client_id,ModuleCode,IsEnabled,SettingsJson)
VALUES(@ClientId,@Key,TRUE,@Json)", new { ClientId = request.TargetClientId, Key = RecruitmentRollbackRules.Key(versionId), Json = JsonSerializer.Serialize(draft.AllRejectedRollback) }, tx);
            await RecruitmentPipelineStageAccess.SaveAsync(db, tx, request.TargetClientId, versionId, draft.Stages);
            if (warnings.Count > 0)
                await db.ExecuteAsync(@"INSERT INTO modulesettings(client_id,ModuleCode,IsEnabled,SettingsJson) VALUES(@ClientId,@Key,TRUE,@Json)",
                    new { ClientId = request.TargetClientId, Key = CloneReviewKey(versionId), Json = JsonSerializer.Serialize(new CloneReview { Warnings = warnings.Distinct().ToList(), PendingCompetencies = draft.Stages.Where(s => s.InterviewConfiguration?.Competencies.Any(c => c.CompetencyId <= 0) == true).ToDictionary(s => s.StageCode, s => s.InterviewConfiguration!.Competencies) }) }, tx);
            await db.ExecuteAsync(@"INSERT INTO recruitment_audit(EntityType,EntityId,Action,NewValueJson,ChangedByUserId)
VALUES('RecruitmentPipelineDefinition',@Id,'Create similar pipeline',@Evidence,@Actor)", new
            {
                Id = newDefinitionId, Actor = user.Id,
                Evidence = JsonSerializer.Serialize(new { SourceDefinitionId = definitionId, SourceVersionId = source.Id, TargetClientId = request.TargetClientId, NewVersionId = versionId, Status = "Draft", Warnings = warnings })
            }, tx);
            await tx.CommitAsync();
            var definition = new RecruitmentPipelineDefinition { Id = newDefinitionId, ClientId = request.TargetClientId, PipelineCode = code, PipelineName = request.PipelineName.Trim(), Description = request.Description?.Trim() ?? sourceDefinition.Description, IsActive = true, CreatedByUserId = user.Id };
            return (new(definition, (await GetPipelineVersionAsync(versionId, user))!, warnings), "");
        }
        catch (MySqlException error) when (error.Number == 1062) { return (null, "That pipeline code already exists for this client. Choose another code."); }
        catch (InvalidOperationException error) { return (null, error.Message); }
    }

    internal static SaveRecruitmentPipelineVersion BuildSimilarPipelineDraft(RecruitmentPipelineVersion source)
    {
        var copy = JsonSerializer.Deserialize<RecruitmentPipelineVersion>(JsonSerializer.Serialize(source))!;
        var ids = new Dictionary<long, long>();
        var codes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < copy.Stages.Count; index++)
        {
            var stage = copy.Stages[index]; var id = -(index + 1L);
            stage.AccessPolicy = RecruitmentPipelineStageAccess.Normalize(stage.AccessPolicy);
            if (!ids.TryAdd(stage.Id, id) || !codes.TryAdd(stage.StageCode, id)) throw new InvalidOperationException("The source has duplicate stage identifiers or codes.");
            stage.Id = id; stage.PipelineVersionId = 0;
            foreach (var action in stage.Actions) { action.Id = 0; action.PipelineStageId = id; foreach (var recipient in action.Recipients) { recipient.Id = 0; recipient.StageActionId = 0; } }
            foreach (var panel in stage.DefaultPanelMembers) { panel.Id = 0; panel.PipelineStageId = id; }
            if (stage.AtsConfiguration is { } ats) { ats.Id = 0; ats.PipelineStageId = id; }
            if (stage.ExternalFormConfiguration is { } form) { form.Id = 0; form.PipelineStageId = id; }
            if (stage.OfferConfiguration is { } offer) { offer.Id = 0; offer.PipelineStageId = id; }
            foreach (var item in stage.AttachmentRequirements) { item.Id = 0; item.PipelineStageId = id; }
            foreach (var item in stage.ProcessDocumentRequirements) { item.Id = 0; item.PipelineStageId = id; }
            if (stage.InterviewConfiguration is { } interview) { interview.Id = 0; interview.PipelineStageId = id; foreach (var competency in interview.Competencies) { competency.Id = 0; competency.InterviewStageConfigurationId = 0; } }
        }
        foreach (var transition in copy.Transitions)
        {
            transition.Id = 0; transition.PipelineVersionId = 0;
            transition.FromStageId = ids.GetValueOrDefault(transition.FromStageId, codes.GetValueOrDefault(transition.FromStageCode));
            transition.ToStageId = ids.GetValueOrDefault(transition.ToStageId, codes.GetValueOrDefault(transition.ToStageCode));
            if (transition.FromStageId == 0 || transition.ToStageId == 0) throw new InvalidOperationException("A source transition refers to an unavailable stage.");
            foreach (var rule in transition.Rules) { rule.Id = 0; rule.TransitionId = 0; }
        }
        return new() { ScopeType = copy.ScopeType, SlaMode = copy.SlaMode, OverallSlaMinutes = copy.OverallSlaMinutes, Stages = copy.Stages, Transitions = copy.Transitions, AllRejectedRollback = copy.AllRejectedRollback };
    }

    private static async Task ReviewCrossClientCloneReferencesAsync(MySqlConnection db, MySqlTransaction tx, int clientId,
        SaveRecruitmentPipelineVersion draft, List<string> warnings)
    {
        async Task<HashSet<long>> Allowed(string sql) => (await db.QueryAsync<long>(sql, new { ClientId = clientId }, tx)).ToHashSet();
        var workflows = await Allowed("SELECT Id FROM workflowmasters WHERE IsActive=TRUE AND (ClientId IS NULL OR ClientId IN (0,@ClientId))");
        var templates = await Allowed("SELECT Id FROM recruitment_templates WHERE IsActive=TRUE AND ClientId IN (0,@ClientId)");
        var profiles = await Allowed("SELECT Id FROM recruitment_ats_scoring_profiles WHERE IsActive=TRUE AND ClientId IN (0,@ClientId)");
        var forms = await Allowed("SELECT v.Id FROM form_versions v JOIN form_definitions d ON d.Id=v.FormDefinitionId WHERE v.Status IN ('Published','Retired') AND d.ClientId IN (0,@ClientId)");
        var attachments = await Allowed("SELECT id FROM attachment_field_configurations WHERE is_active=TRUE AND client_id IN (0,@ClientId)");
        var competencies = await Allowed("SELECT Id FROM recruitment_interview_competency_definitions WHERE IsActive=TRUE AND ClientId=@ClientId");
        var users = await Allowed("SELECT Id FROM authusers WHERE IsActive=TRUE AND (ClientId IS NULL OR ClientId IN (0,@ClientId))");
        var roles = (await db.QueryAsync<string>("SELECT Code FROM authroles WHERE ClientId IS NULL OR ClientId IN (0,@ClientId)", new { ClientId = clientId }, tx)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        RemoveForeignCloneReferences(draft, new(workflows, templates, profiles, forms, attachments, competencies, users, roles), warnings);
    }

    internal sealed record CloneReferenceScope(HashSet<long> Workflows, HashSet<long> Templates, HashSet<long> Profiles,
        HashSet<long> Forms, HashSet<long> Attachments, HashSet<long> Competencies, HashSet<long> Users, HashSet<string> Roles);

    internal static void RemoveForeignCloneReferences(SaveRecruitmentPipelineVersion draft, CloneReferenceScope allowed, List<string> warnings)
    {
        long? Retain(long? id, HashSet<long> valid, string label) { if (id is not > 0 || valid.Contains(id.Value)) return id; warnings.Add(label + ": select a target-client value before publishing."); return 0; }
        foreach (var stage in draft.Stages)
        {
            var label = stage.StageName;
            stage.ApprovalWorkflowId = Retain(stage.ApprovalWorkflowId, allowed.Workflows, label + " approval");
            foreach (var action in stage.Actions)
            {
                action.WorkflowId = Retain(action.WorkflowId, allowed.Workflows, label + " action workflow");
                action.TemplateId = Retain(action.TemplateId, allowed.Templates, label + " action template");
                foreach (var recipient in action.Recipients)
                {
                    if (recipient.RecipientType == "StaticEmail") { recipient.EmailAddress = ""; warnings.Add(label + ": enter the target-client notification email recipient."); }
                    if (recipient.UserId is > 0 && !allowed.Users.Contains(recipient.UserId.Value)) { recipient.UserId = 0; warnings.Add(label + ": select the target-client notification user."); }
                    if (recipient.RecipientType == "UserRole" && !allowed.Roles.Contains(recipient.RoleCode)) { recipient.RoleCode = ""; warnings.Add(label + ": select the target-client notification role."); }
                }
            }
            foreach (var panel in stage.DefaultPanelMembers.Where(p => p.PanelUserId > 0 && !allowed.Users.Contains(p.PanelUserId))) { panel.PanelUserId = -panel.PanelUserId; panel.PanelUserName = "Select target-client panel member"; warnings.Add(label + ": select the target-client interview panel member."); }
            if (stage.AtsConfiguration is { } ats) ats.ScoringProfileId = Retain(ats.ScoringProfileId, allowed.Profiles, label + " ATS profile");
            if (stage.ExternalFormConfiguration is { } form && !allowed.Forms.Contains(form.FormVersionId)) { form.FormVersionId = 0; warnings.Add(label + ": select a published form for the target client."); }
            foreach (var attachment in stage.AttachmentRequirements.Where(a => a.AttachmentFieldConfigurationId > 0 && !allowed.Attachments.Contains(a.AttachmentFieldConfigurationId))) { attachment.AttachmentFieldConfigurationId = -attachment.AttachmentFieldConfigurationId; warnings.Add(label + ": map the required target-client attachment field."); }
            foreach (var document in stage.ProcessDocumentRequirements) document.TemplateId = Retain(document.TemplateId, allowed.Templates, label + " document template");
            if (stage.OfferConfiguration is { } offer) { offer.OfferTemplateId = Retain(offer.OfferTemplateId, allowed.Templates, label + " offer template"); offer.ApprovalWorkflowId = Retain(offer.ApprovalWorkflowId, allowed.Workflows, label + " offer approval"); offer.VarianceApprovalWorkflowId = Retain(offer.VarianceApprovalWorkflowId, allowed.Workflows, label + " variance approval"); }
            if (stage.InterviewConfiguration is { } interview) foreach (var competency in interview.Competencies.Where(c => c.CompetencyId > 0 && !allowed.Competencies.Contains(c.CompetencyId))) { competency.CompetencyId = -competency.CompetencyId; warnings.Add(label + ": map the interview competency in the target client; weights and passing scores are retained."); }
            var grants = stage.AccessPolicy.AllowedUserIds.Count + stage.AccessPolicy.AllowedRoleCodes.Count;
            stage.AccessPolicy.AllowedUserIds.RemoveAll(id => !allowed.Users.Contains(id));
            stage.AccessPolicy.AllowedRoleCodes.RemoveAll(role => !allowed.Roles.Contains(role));
            if (grants != stage.AccessPolicy.AllowedUserIds.Count + stage.AccessPolicy.AllowedRoleCodes.Count) warnings.Add(label + ": source-client stage access grants were removed. Restricted stages remain restricted until target users/roles are assigned.");
        }
        foreach (var transition in draft.Transitions) transition.ApprovalWorkflowId = Retain(transition.ApprovalWorkflowId, allowed.Workflows, transition.ActionLabel + " transition approval");
    }

    private static string CloneReviewKey(long versionId) => $"recruitment_pipeline_clone_review_{versionId}";
    private sealed class CloneReview
    {
        public List<string> Warnings { get; set; } = [];
        public Dictionary<string, List<RecruitmentInterviewStageCompetency>> PendingCompetencies { get; set; } = [];
    }

    private static async Task<CloneReview?> ReadCloneReviewAsync(MySqlConnection db, int clientId, long versionId, MySqlTransaction? tx = null)
    {
        string? json;
        try { json = await db.ExecuteScalarAsync<string?>("SELECT SettingsJson FROM modulesettings WHERE client_id=@ClientId AND ModuleCode=@Key AND IsEnabled=TRUE", new { ClientId = clientId, Key = CloneReviewKey(versionId) }, tx); }
        catch (MySqlException error) when (error.Number == 1146) { return null; }
        return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<CloneReview>(json) ?? throw new InvalidOperationException("Review the copied pipeline mappings before publishing.");
    }

    private static async Task HydrateCloneReviewAsync(MySqlConnection db, int clientId, RecruitmentPipelineVersion version)
    {
        var review = await ReadCloneReviewAsync(db, clientId, version.Id);
        if (review is null) return;
        version.CloneWarnings = review.Warnings;
        foreach (var stage in version.Stages)
            if (stage.InterviewConfiguration is { } interview && review.PendingCompetencies.TryGetValue(stage.StageCode, out var pending))
                interview.Competencies = pending;
    }

    private static async Task ReviewCloneWarningsOnSaveAsync(MySqlConnection db, MySqlTransaction tx, int clientId, long versionId, bool acknowledged)
    {
        var review = await ReadCloneReviewAsync(db, clientId, versionId, tx);
        if (review is null) return;
        // Reaching here means every submitted reference passed the ordinary draft
        // validators. Keep the warning until the editor explicitly acknowledges it.
        review.PendingCompetencies.Clear();
        await db.ExecuteAsync("UPDATE modulesettings SET IsEnabled=@Enabled,SettingsJson=@Json WHERE client_id=@ClientId AND ModuleCode=@Key",
            new { ClientId = clientId, Key = CloneReviewKey(versionId), Enabled = !acknowledged, Json = JsonSerializer.Serialize(review) }, tx);
    }

    internal static string CloneReferenceError(IEnumerable<RecruitmentPipelineStage> stages, IEnumerable<RecruitmentPipelineTransition>? transitions = null)
    {
        if (transitions?.Any(t => t.ApprovalWorkflowId.HasValue && t.ApprovalWorkflowId <= 0) == true)
            return "Map the copied transition approval workflow before saving or publishing.";
        foreach (var stage in stages)
        {
            var references = stage.Actions.SelectMany(a => new[] { a.WorkflowId, a.TemplateId })
                .Concat(stage.ProcessDocumentRequirements.Select(d => d.TemplateId))
                .Concat([stage.ApprovalWorkflowId, stage.AtsConfiguration?.ScoringProfileId, stage.OfferConfiguration?.OfferTemplateId,
                    stage.OfferConfiguration?.ApprovalWorkflowId, stage.OfferConfiguration?.VarianceApprovalWorkflowId]);
            if (references.Any(id => id.HasValue && id <= 0) || stage.ExternalFormConfiguration?.FormVersionId <= 0
                || stage.AttachmentRequirements.Any(a => a.AttachmentFieldConfigurationId <= 0)
                || stage.DefaultPanelMembers.Any(p => p.PanelUserId <= 0)
                || stage.InterviewConfiguration?.Competencies.Any(c => c.CompetencyId <= 0) == true)
                return $"{stage.StageName}: map the copied client-specific configuration before saving or publishing. Required fields and rubric weights were retained for review.";
        }
        return "";
    }
}
