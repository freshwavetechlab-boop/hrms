using Dapper;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public partial class RecruitmentPipelineRepository
{
    private readonly object revisionScanLock = new();
    private (long Id, string Scope) revisionScanCursor = (0, "");
    // Assignment rows are history. New versions do not create jobs or change public slugs.
    internal static async Task PromotePipelineAssignmentsAsync(MySqlConnection db, System.Data.IDbTransaction tx,
        long definitionId, long versionId, int actorId)
    {
        var bindings = (await db.QueryAsync<(long PositionId, long? JobPostingId)>(@"
SELECT DISTINCT binding.PositionId,binding.JobPostingId
FROM recruitment_position_pipeline_assignments binding
JOIN recruitment_pipeline_versions version ON version.Id=binding.PipelineVersionId
WHERE version.PipelineDefinitionId=@DefinitionId AND binding.IsActive=TRUE AND binding.PipelineVersionId<>@VersionId
ORDER BY binding.PositionId,binding.JobPostingId", new { DefinitionId = definitionId, VersionId = versionId }, tx)).ToList();
        await db.ExecuteAsync(@"UPDATE recruitment_position_pipeline_assignments binding
JOIN recruitment_pipeline_versions version ON version.Id=binding.PipelineVersionId
SET binding.IsActive=FALSE WHERE version.PipelineDefinitionId=@DefinitionId AND binding.IsActive=TRUE AND binding.PipelineVersionId<>@VersionId",
            new { DefinitionId = definitionId, VersionId = versionId }, tx);
        foreach (var binding in bindings)
            await db.ExecuteAsync(@"INSERT INTO recruitment_position_pipeline_assignments
(PositionId,JobPostingId,PipelineVersionId,IsActive,AssignedByUserId)
SELECT @PositionId,@JobPostingId,@VersionId,TRUE,@ActorId FROM DUAL
WHERE NOT EXISTS(SELECT 1 FROM recruitment_position_pipeline_assignments WHERE PositionId=@PositionId AND JobPostingId <=> @JobPostingId AND PipelineVersionId=@VersionId AND IsActive=TRUE)",
                new { binding.PositionId, binding.JobPostingId, VersionId = versionId, ActorId = actorId }, tx);
    }

    // Reuse runtime identities and retain entered timestamps. Completed rows remain on their
    // original immutable definitions. In-flight evidence/approvals finish before rebinding.
    public async Task<RecruitmentPipelineRevisionSync> SynchronizePublishedRevisionsAsync(AuthUser user, long? definitionId = null, int limit = 100)
    {
        await using var db = Db();
        await db.OpenAsync();
        // Catch up revisions published before this feature was deployed as well.
        var definitions = (await db.QueryAsync<long>(@"SELECT DISTINCT definition.Id
FROM recruitment_position_pipeline_assignments binding
JOIN recruitment_pipeline_versions version ON version.Id=binding.PipelineVersionId
JOIN recruitment_pipeline_definitions definition ON definition.Id=version.PipelineDefinitionId AND definition.IsActive=TRUE
WHERE binding.IsActive=TRUE AND definition.CurrentPublishedVersionId IS NOT NULL
 AND binding.PipelineVersionId<>definition.CurrentPublishedVersionId
 AND (@ClientId IS NULL OR definition.ClientId=@ClientId) AND (@DefinitionId IS NULL OR definition.Id=@DefinitionId)
ORDER BY definition.Id LIMIT @Limit", new { user.ClientId, DefinitionId = definitionId, Limit = Math.Clamp(limit, 1, 500) })).ToList();
        foreach (var id in definitions)
        {
            await using var tx = await db.BeginTransactionAsync();
            var published = await db.ExecuteScalarAsync<long>("SELECT CurrentPublishedVersionId FROM recruitment_pipeline_definitions WHERE Id=@Id FOR UPDATE", new { Id = id }, tx);
            await PromotePipelineAssignmentsAsync(db, tx, id, published, user.Id);
            await tx.CommitAsync();
        }
        // The global worker rotates through deferred rows too. A full first batch of
        // in-flight approvals must not starve later applications indefinitely.
        var rotate = user.Id == 0 && user.ClientId is null && definitionId is null;
        (long Id, string Scope) cursor;
        lock (revisionScanLock) cursor = rotate ? revisionScanCursor : (0, "");
        var rows = (await db.QueryAsync<RevisionTarget>(@"SELECT * FROM (
SELECT 'Application' Scope,pipeline.Id,pipeline.ApplicationId,pipeline.CurrentStageInstanceId,
 pipeline.PipelineVersionId,definition.CurrentPublishedVersionId TargetVersionId,definition.Id DefinitionId,
 stage.PipelineStageId,stageDefinition.StageCode,stageDefinition.StageType,stageDefinition.CardScope
FROM recruitment_application_pipeline_instances pipeline
JOIN recruitment_pipeline_versions version ON version.Id=pipeline.PipelineVersionId
JOIN recruitment_pipeline_definitions definition ON definition.Id=version.PipelineDefinitionId AND definition.IsActive=TRUE
JOIN recruitment_application_stage_instances stage ON stage.Id=pipeline.CurrentStageInstanceId AND stage.Status IN ('Active','Paused')
JOIN recruitment_pipeline_stages stageDefinition ON stageDefinition.Id=stage.PipelineStageId
WHERE pipeline.Status='Active' AND stageDefinition.IsTerminal=FALSE AND definition.CurrentPublishedVersionId<>pipeline.PipelineVersionId
 AND (@ClientId IS NULL OR definition.ClientId=@ClientId) AND (@DefinitionId IS NULL OR definition.Id=@DefinitionId)
UNION ALL
SELECT 'Position',pipeline.Id,NULL,pipeline.CurrentStageInstanceId,pipeline.PipelineVersionId,
 definition.CurrentPublishedVersionId,definition.Id,stage.PipelineStageId,stageDefinition.StageCode,stageDefinition.StageType,stageDefinition.CardScope
FROM recruitment_position_pipeline_instances pipeline
JOIN recruitment_pipeline_versions version ON version.Id=pipeline.PipelineVersionId
JOIN recruitment_pipeline_definitions definition ON definition.Id=version.PipelineDefinitionId AND definition.IsActive=TRUE
JOIN recruitment_position_stage_instances stage ON stage.Id=pipeline.CurrentStageInstanceId AND stage.Status='Active'
JOIN recruitment_pipeline_stages stageDefinition ON stageDefinition.Id=stage.PipelineStageId
WHERE pipeline.Status IN ('Active','Candidate Flow') AND stageDefinition.IsTerminal=FALSE AND definition.CurrentPublishedVersionId<>pipeline.PipelineVersionId
 AND NOT EXISTS(SELECT 1 FROM recruitment_position_pipeline_instances newer
 WHERE newer.ClientId=pipeline.ClientId AND newer.RequisitionId=pipeline.RequisitionId AND newer.Status<>'Superseded'
 AND ((newer.PositionId IS NOT NULL AND pipeline.PositionId IS NULL)
 OR ((newer.PositionId IS NOT NULL)=(pipeline.PositionId IS NOT NULL) AND newer.Id>pipeline.Id)))
 AND (@ClientId IS NULL OR definition.ClientId=@ClientId) AND (@DefinitionId IS NULL OR definition.Id=@DefinitionId)
) targets ORDER BY (Id>@AfterId OR (Id=@AfterId AND Scope>@AfterScope)) DESC,Id,Scope LIMIT @Limit",
            new { user.ClientId, DefinitionId = definitionId, AfterId = cursor.Id, AfterScope = cursor.Scope, Limit = Math.Clamp(limit, 1, 500) })).ToList();
        if (rotate && rows.Count > 0)
            lock (revisionScanLock) revisionScanCursor = (rows[^1].Id, rows[^1].Scope);
        var applications = 0; var cases = 0; var waiting = new List<string>();
        foreach (var row in rows)
        {
            var stages = (await db.QueryAsync<RecruitmentPipelineStage>("SELECT * FROM recruitment_pipeline_stages WHERE PipelineVersionId=@Id AND IsActive=TRUE", new { Id = row.TargetVersionId })).ToList();
            var target = stages.SingleOrDefault(stage => !stage.IsTerminal
                && stage.StageCode.Equals(row.StageCode, StringComparison.OrdinalIgnoreCase)
                && stage.StageType.Equals(row.StageType, StringComparison.OrdinalIgnoreCase)
                && stage.CardScope.Equals(row.CardScope, StringComparison.OrdinalIgnoreCase));
            var reason = target is null ? $"Stage {row.StageCode} needs an explicit compatible mapping in the new version." : "";
            await using var tx = await db.BeginTransactionAsync();
            // Serialize with publishing and with the normal transition transaction.
            var published = await db.ExecuteScalarAsync<long>("SELECT CurrentPublishedVersionId FROM recruitment_pipeline_definitions WHERE Id=@Id FOR UPDATE", new { Id = row.DefinitionId }, tx);
            if (published != row.TargetVersionId) continue;
            var runtimeTable = row.Scope == "Application" ? "recruitment_application_pipeline_instances" : "recruitment_position_pipeline_instances";
            var current = await db.QueryFirstOrDefaultAsync<(long StageId, long VersionId)>(
                $"SELECT CurrentStageInstanceId StageId,PipelineVersionId VersionId FROM {runtimeTable} WHERE Id=@Id AND Status IN ('Active','Candidate Flow') FOR UPDATE", new { row.Id }, tx);
            if (current.StageId != row.CurrentStageInstanceId || current.VersionId != row.PipelineVersionId) continue;
            var stageTable = row.Scope == "Application" ? "recruitment_application_stage_instances" : "recruitment_position_stage_instances";
            await db.ExecuteScalarAsync<long>($"SELECT Id FROM {stageTable} WHERE Id=@Id FOR UPDATE", new { Id = row.CurrentStageInstanceId }, tx);
            if (reason.Length == 0)
            {
                var hasActivity = row.Scope == "Application"
                    ? await db.ExecuteScalarAsync<bool>(@"SELECT
EXISTS(SELECT 1 FROM recruitment_pipeline_transition_requests WHERE StageInstanceId=@StageId AND Status='Pending Approval') OR
EXISTS(SELECT 1 FROM recruitment_stage_action_executions WHERE StageInstanceId=@StageId AND (Status IN ('Running','Pending Approval') OR ActionCode<>'RUN_ATS_SCORE')) OR
EXISTS(SELECT 1 FROM recruitment_interviews WHERE PipelineStageInstanceId=@StageId) OR
EXISTS(SELECT 1 FROM recruitment_candidate_action_sessions WHERE PipelineStageInstanceId=@StageId) OR
EXISTS(SELECT 1 FROM recruitment_process_documents WHERE ApplicationId=@ApplicationId AND PipelineStageId=@DefinitionStageId) OR
EXISTS(SELECT 1 FROM recruitment_offers WHERE PipelineStageInstanceId=@StageId)",
                        new { StageId = row.CurrentStageInstanceId, row.ApplicationId, DefinitionStageId = row.PipelineStageId }, tx)
                    : await db.ExecuteScalarAsync<bool>(@"SELECT
EXISTS(SELECT 1 FROM recruitment_hiring_case_advance_requests WHERE PositionStageInstanceId=@StageId AND Status='Pending Approval') OR
EXISTS(SELECT 1 FROM recruitment_process_documents WHERE HiringCaseId=@Id AND PipelineStageId=@DefinitionStageId)",
                        new { StageId = row.CurrentStageInstanceId, row.Id, DefinitionStageId = row.PipelineStageId }, tx);
                if (hasActivity) reason = "An interview, approval, action or document is already bound to this stage. The new version will apply after this stage finishes.";
            }
            var details = $"v#{row.PipelineVersionId} -> v#{row.TargetVersionId}; stage {row.StageCode}, definition #{row.PipelineStageId} -> #{target?.Id}. {reason}";
            if (reason.Length > 0)
            {
                await LogRevisionAsync(db, tx, row, "RevisionWaiting", details, user.Id);
                await tx.CommitAsync();
                waiting.Add($"{row.Scope} #{row.ApplicationId ?? row.Id}: {reason}");
                continue;
            }
            await db.ExecuteAsync($"UPDATE {stageTable} SET PipelineStageId=@StageId WHERE Id=@Id", new { Id = row.CurrentStageInstanceId, StageId = target!.Id }, tx);
            await db.ExecuteAsync($"UPDATE {runtimeTable} SET PipelineVersionId=@VersionId WHERE Id=@Id", new { row.Id, VersionId = row.TargetVersionId }, tx);
            if (row.Scope == "Application")
            {
                // Keep old action executions as audit; carry only unchanged completed ATS work.
                await db.ExecuteAsync(@"INSERT IGNORE INTO recruitment_stage_action_executions
(ApplicationId,StageInstanceId,StageActionId,TriggerEvent,ActionCode,Status,IsBlocking,ApplicationScoreId,StartedAtUtc,CompletedAtUtc)
SELECT execution.ApplicationId,execution.StageInstanceId,newAction.Id,execution.TriggerEvent,execution.ActionCode,
 execution.Status,newAction.IsBlocking,execution.ApplicationScoreId,execution.StartedAtUtc,execution.CompletedAtUtc
FROM recruitment_stage_action_executions execution
JOIN recruitment_pipeline_stage_actions oldAction ON oldAction.Id=execution.StageActionId
JOIN recruitment_pipeline_stage_actions newAction ON newAction.PipelineStageId=@NewStageId AND newAction.IsActive=TRUE
 AND newAction.ActionCode=oldAction.ActionCode AND newAction.TriggerEvent=oldAction.TriggerEvent AND newAction.ExecutionOrder=oldAction.ExecutionOrder
JOIN recruitment_stage_ats_configurations oldAts ON oldAts.PipelineStageId=@OldStageId
JOIN recruitment_stage_ats_configurations newAts ON newAts.PipelineStageId=@NewStageId AND newAts.ScoringProfileId <=> oldAts.ScoringProfileId
WHERE execution.StageInstanceId=@InstanceId AND oldAction.PipelineStageId=@OldStageId
 AND execution.ActionCode='RUN_ATS_SCORE' AND execution.Status='Completed' AND execution.ApplicationScoreId IS NOT NULL",
                    new { InstanceId = row.CurrentStageInstanceId, NewStageId = target.Id, OldStageId = row.PipelineStageId }, tx);
                await db.ExecuteAsync(@"UPDATE recruitment_candidate_applications SET CurrentStage=@Name,UpdatedAt=UTC_TIMESTAMP() WHERE Id=@Id;
UPDATE recruitment_application_pipeline_instances pipeline
JOIN recruitment_candidate_applications application ON application.Id=pipeline.ApplicationId
SET pipeline.PositionPipelineAssignmentId=(SELECT binding.Id FROM recruitment_position_pipeline_assignments binding
 WHERE binding.PositionId=application.PositionId AND binding.PipelineVersionId=@VersionId AND binding.IsActive=TRUE
 AND (binding.JobPostingId=application.JobPostingId OR binding.JobPostingId IS NULL)
 ORDER BY (binding.JobPostingId=application.JobPostingId) DESC,binding.Id DESC LIMIT 1)
WHERE pipeline.Id=@PipelineId", new { Id = row.ApplicationId, Name = target.StageName, VersionId = row.TargetVersionId, PipelineId = row.Id }, tx);
                applications++;
            }
            else
            {
                // Unentered placeholders have no business history; archive them rather than delete.
                await db.ExecuteAsync("UPDATE recruitment_position_stage_instances SET Status='Superseded' WHERE PositionPipelineInstanceId=@Id AND Status='Pending' AND EnteredAtUtc IS NULL", new { row.Id }, tx);
                foreach (var next in stages.Where(stage => stage.CardScope == "Position" && stage.DisplayOrder > target.DisplayOrder))
                    await db.ExecuteAsync(@"INSERT INTO recruitment_position_stage_instances(PositionPipelineInstanceId,PipelineStageId,Status,DueAtUtc)
SELECT @Id,@StageId,'Pending',CASE WHEN @Offset IS NULL THEN NULL ELSE TIMESTAMPADD(MINUTE,@Offset,SlaAnchorAtUtc) END
FROM recruitment_position_pipeline_instances WHERE Id=@Id", new { row.Id, StageId = next.Id, Offset = next.TargetOffsetMinutes }, tx);
                cases++;
            }
            await LogRevisionAsync(db, tx, row, "RevisionApplied", details + " Existing SLA clock and completed history retained.", user.Id);
            await tx.CommitAsync();
        }
        return new(applications, cases, waiting);
    }

    private static Task LogRevisionAsync(MySqlConnection db, System.Data.IDbTransaction tx, RevisionTarget row, string type, string details, int actor) =>
        db.ExecuteAsync(row.Scope == "Application" ? @"INSERT INTO recruitment_stage_events(StageInstanceId,EventType,EventTitle,EventDetails,ActorUserId)
SELECT @StageId,@Type,'Pipeline version update',@Details,@Actor FROM DUAL
WHERE NOT EXISTS(SELECT 1 FROM recruitment_stage_events WHERE StageInstanceId=@StageId AND EventType=@Type AND EventDetails=@Details)"
        : @"INSERT INTO recruitment_position_stage_events(PositionPipelineInstanceId,PositionStageInstanceId,EventType,EventTitle,EventDetails,ActorUserId)
SELECT @Id,@StageId,@Type,'Pipeline version update',@Details,@Actor FROM DUAL
WHERE NOT EXISTS(SELECT 1 FROM recruitment_position_stage_events WHERE PositionStageInstanceId=@StageId AND EventType=@Type AND EventDetails=@Details)",
            new { row.Id, StageId = row.CurrentStageInstanceId, Type = type, Details = details, Actor = actor }, tx);

    private sealed class RevisionTarget
    {
        public string Scope { get; set; } = "";
        public long Id { get; set; }
        public long? ApplicationId { get; set; }
        public long CurrentStageInstanceId { get; set; }
        public long PipelineVersionId { get; set; }
        public long TargetVersionId { get; set; }
        public long DefinitionId { get; set; }
        public long PipelineStageId { get; set; }
        public string StageCode { get; set; } = "";
        public string StageType { get; set; } = "";
        public string CardScope { get; set; } = "";
    }
}
