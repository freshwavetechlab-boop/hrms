using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class RecruitmentTalentRepository
{
    private sealed class RoundScope
    {
        public long ApplicationId { get; set; }
        public long PositionId { get; set; }
        public int ClientId { get; set; }
        public long PipelineStageId { get; set; }
        public long? StageInstanceId { get; set; }
        public string StageCode { get; set; } = "";
        public string StageName { get; set; } = "";
        public string InterviewType { get; set; } = "Technical";
    }
    private sealed class SavedRounds
    {
        public int Revision { get; set; }
        public List<RecruitmentInterviewRoundDefinition> Rounds { get; set; } = [];
    }
    private static Task<RoundScope?> RoundScopeAsync(MySqlConnection db, long applicationId, long? definitionId = null, MySqlTransaction? tx = null) =>
        db.QueryFirstOrDefaultAsync<RoundScope>(@"SELECT a.Id ApplicationId,a.PositionId,a.ClientId,stage.Id PipelineStageId,stage.StageCode,stage.StageName,
COALESCE(config.InterviewType,'Technical') InterviewType,
(SELECT MAX(visit.Id) FROM recruitment_application_stage_instances visit
 JOIN recruitment_pipeline_stages visited ON visited.Id=visit.PipelineStageId
 WHERE visit.ApplicationPipelineInstanceId=flow.Id AND visited.StageCode=stage.StageCode) StageInstanceId
FROM recruitment_candidate_applications a
JOIN recruitment_application_pipeline_instances flow ON flow.ApplicationId=a.Id
JOIN recruitment_pipeline_stages stage ON stage.PipelineVersionId=flow.PipelineVersionId AND stage.StageType='Interview' AND stage.IsActive=TRUE
LEFT JOIN recruitment_application_stage_instances currentVisit ON currentVisit.Id=flow.CurrentStageInstanceId
LEFT JOIN recruitment_interview_stage_configurations config ON config.PipelineStageId=stage.Id
WHERE a.Id=@ApplicationId AND (@DefinitionId IS NULL OR stage.Id=@DefinitionId)
ORDER BY (stage.Id=currentVisit.PipelineStageId) DESC,stage.DisplayOrder,stage.Id LIMIT 1",new { ApplicationId=applicationId,DefinitionId=definitionId },tx);

    private static async Task<SavedRounds?> ReadRoundsAsync(MySqlConnection db, RoundScope scope, MySqlTransaction? tx = null)
    {
        var json = await db.ExecuteScalarAsync<string?>("SELECT SettingsJson FROM modulesettings WHERE client_id=@ClientId AND ModuleCode=@Key AND IsEnabled=TRUE",
            new { scope.ClientId,Key=RecruitmentInterviewRounds.Key(scope.ClientId,scope.PositionId,scope.StageCode) },tx);
        if (json is null) return null;
        SavedRounds? saved;
        try { saved=JsonSerializer.Deserialize<SavedRounds>(json); }
        catch (JsonException) { throw new InvalidOperationException("The saved interview round plan is invalid. Ask the hiring administrator to review it."); }
        if (saved?.Rounds is null || saved.Revision<1 || saved.Rounds.Any(r=>r is null)
            || RecruitmentInterviewRounds.ValidateDefinitions(saved.Rounds,[],new HashSet<string>()).Length>0)
            throw new InvalidOperationException("The saved interview round plan is invalid. Ask the hiring administrator to review it.");
        return saved;
    }
    private static Task<IEnumerable<RecruitmentInterviewRounds.RoundAttempt>> RoundAttemptsAsync(MySqlConnection db, RoundScope scope, bool entireJob = false, MySqlTransaction? tx = null) =>
        db.QueryAsync<RecruitmentInterviewRounds.RoundAttempt>(@"SELECT i.Id,i.RoundCode,i.InterviewType,i.Status,i.Result
FROM recruitment_interviews i JOIN recruitment_candidate_applications a ON a.Id=i.ApplicationId
JOIN recruitment_application_stage_instances visit ON visit.Id=i.PipelineStageInstanceId
JOIN recruitment_pipeline_stages stage ON stage.Id=visit.PipelineStageId
WHERE a.ClientId=@ClientId AND a.PositionId=@PositionId AND stage.StageCode=@StageCode
AND (@EntireJob=TRUE OR (a.Id=@ApplicationId AND visit.Id=@StageInstanceId)) ORDER BY i.Id",
            new { scope.ClientId,scope.PositionId,scope.StageCode,scope.ApplicationId,scope.StageInstanceId,EntireJob=entireJob },tx);

    public async Task<(RecruitmentInterviewRoundPlan? Row,string Error)> GetInterviewRoundsAsync(long applicationId, AuthUser user)
    {
        await using var db=Db();await db.OpenAsync();
        if (await ApplicationByIdAsync(db,applicationId,user) is null) return (null,"Application was not found.");
        var scope=await RoundScopeAsync(db,applicationId);
        if (scope is null) return (null,"Assign an Interview pipeline stage to this job before configuring rounds.");
        var saved=await ReadRoundsAsync(db,scope);
        var definitions=saved?.Rounds ?? [new RecruitmentInterviewRoundDefinition { Name=scope.StageName,InterviewType=scope.InterviewType }];
        return (new RecruitmentInterviewRoundPlan { ApplicationId=applicationId,PositionId=scope.PositionId,StageName=scope.StageName,
            Revision=saved?.Revision ?? 0,IsConfigured=saved is not null,
            CanConfigure=RecruitmentPermissions.Has(user,"recruitment.interview.schedule","recruitment.manage","settings.manage"),
            Rounds=RecruitmentInterviewRounds.States(definitions,await RoundAttemptsAsync(db,scope)) },"");
    }

    public async Task<(RecruitmentInterviewRoundPlan? Row,string Error)> SaveInterviewRoundsAsync(long applicationId, SaveRecruitmentInterviewRounds request, AuthUser user)
    {
        if (!RecruitmentPermissions.Has(user,"recruitment.interview.schedule","recruitment.manage","settings.manage")) return (null,"Scheduling permission is required.");
        request.Rounds ??= [];
        if (request.Rounds.Any(r => r is null)) return (null,"Each interview round needs a name and type.");
        foreach (var round in request.Rounds) { round.Name=(round.Name ?? "").Trim();round.InterviewType=(round.InterviewType ?? "").Trim(); }
        await using var db=Db();await db.OpenAsync();
        var application=await ApplicationByIdAsync(db,applicationId,user);
        if (application is null) return (null,"Application was not found.");
        await using var lease=await RecruitmentInterviewRounds.LockAsync(db,application.ClientId,application.PositionId);
        await using var tx=await db.BeginTransactionAsync();
        var scope=await RoundScopeAsync(db,applicationId,tx:tx);
        if (scope is null) return (null,"Assign an Interview pipeline stage to this job before configuring rounds.");
        var before=await ReadRoundsAsync(db,scope,tx);
        if (request.ExpectedRevision != (before?.Revision ?? 0)) return (null,"These rounds changed in another session. Reopen them before saving.");
        if (await db.ExecuteScalarAsync<bool>(@"SELECT EXISTS(SELECT 1 FROM recruitment_process_documents d
JOIN recruitment_position_pipeline_instances hiring ON hiring.Id=d.HiringCaseId
WHERE hiring.PositionId=@PositionId AND hiring.ClientId=@ClientId AND d.DocumentType IN ('MOM','SIGNED_MOM')
AND NULLIF(TRIM(d.BodySnapshot),'') IS NOT NULL)",scope,tx))
            return (null,"This job already has a prepared MoM. Its interview round plan is preserved with the recorded selection and signatures.");
        if (await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM recruitment_application_stage_instances visit
JOIN recruitment_candidate_applications a ON a.Id=visit.ApplicationId
JOIN recruitment_pipeline_stages stage ON stage.Id=visit.PipelineStageId
WHERE a.ClientId=@ClientId AND a.PositionId=@PositionId AND stage.StageCode=@StageCode
AND (visit.Status='Completed' OR EXISTS(SELECT 1 FROM recruitment_pipeline_transition_requests r WHERE r.StageInstanceId=visit.Id AND r.Status IN ('Pending Approval','Approved')))",scope,tx)>0)
            return (null,"This job already has an interview stage completed or awaiting transition approval. Its round plan is preserved for that decision history.");
        var attempts=(await RoundAttemptsAsync(db,scope,true,tx)).ToList();
        var previous=before?.Rounds ?? attempts.Select(a=>new RecruitmentInterviewRoundDefinition { Name=a.RoundCode,InterviewType=a.InterviewType }).DistinctBy(r=>r.Name,StringComparer.OrdinalIgnoreCase).ToList();
        var error=RecruitmentInterviewRounds.ValidateDefinitions(request.Rounds,previous,attempts.Select(a=>a.RoundCode).ToHashSet(StringComparer.OrdinalIgnoreCase));
        if (error.Length>0) return (null,error);
        var after=new SavedRounds { Revision=(before?.Revision ?? 0)+1,Rounds=request.Rounds };
        await db.ExecuteAsync(@"INSERT INTO modulesettings(client_id,ModuleCode,IsEnabled,SettingsJson) VALUES(@ClientId,@Key,TRUE,@Json)
ON DUPLICATE KEY UPDATE SettingsJson=@Json,IsEnabled=TRUE,UpdatedAt=UTC_TIMESTAMP()",
            new { scope.ClientId,Key=RecruitmentInterviewRounds.Key(scope.ClientId,scope.PositionId,scope.StageCode),Json=JsonSerializer.Serialize(after) },tx);
        await db.ExecuteAsync(@"INSERT INTO recruitment_audit(EntityType,EntityId,Action,OldValueJson,NewValueJson,ChangedByUserId)
VALUES('RecruitmentInterviewRounds',@PositionId,'Configure job interview rounds',@Before,@After,@UserId)",
            new { scope.PositionId,Before=JsonSerializer.Serialize(before),After=JsonSerializer.Serialize(new { scope.StageCode,after.Revision,after.Rounds }),UserId=user.Id },tx);
        await tx.CommitAsync();
        return await GetInterviewRoundsAsync(applicationId,user);
    }
}
