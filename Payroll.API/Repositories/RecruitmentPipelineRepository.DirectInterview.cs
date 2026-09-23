using Dapper;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public partial class RecruitmentPipelineRepository
{
    public async Task<string> EnterDirectInterviewAsync(long applicationId, AuthUser user)
    {
        if (!RecruitmentPermissions.Has(user, "recruitment.interview.schedule")) return "Scheduling permission is required.";
        var ensured = await EnsureApplicationPipelineAsync(applicationId, user);
        if (ensured.Error.Length > 0) return ensured.Error;
        await using var db = Db(); await db.OpenAsync(); await using var tx = await db.BeginTransactionAsync();
        var current = await db.QueryFirstOrDefaultAsync<DirectInterviewStage>(@"SELECT flow.Id PipelineId,flow.PipelineVersionId,instance.Id InstanceId,
stage.Id,stage.StageName,stage.StageType,stage.DisplayOrder,instance.Status
FROM recruitment_candidate_applications a JOIN recruitment_application_pipeline_instances flow ON flow.ApplicationId=a.Id AND flow.Status='Active'
JOIN recruitment_application_stage_instances instance ON instance.Id=flow.CurrentStageInstanceId
JOIN recruitment_pipeline_stages stage ON stage.Id=instance.PipelineStageId
WHERE a.Id=@applicationId AND (@ClientId IS NULL OR a.ClientId=@ClientId) FOR UPDATE", new { applicationId, user.ClientId }, tx);
        if (current is null) return "No active candidate pipeline is available for the linked interview.";
        if (current.StageType == "Interview") return "";
        if (current.StageType is not ("Screening" or "ATS")) return ""; // A later round must not rewind an already advanced application.
        if (current.Status != "Active") return "Resume this candidate's pipeline before advancing the direct interview.";
        var interviewId = await db.ExecuteScalarAsync<long?>(@"SELECT MAX(Id) FROM recruitment_interviews
WHERE ApplicationId=@applicationId AND IsStandalone=TRUE AND Status IN ('Scheduled','Rescheduled','Completed')", new { applicationId }, tx);
        if (interviewId is null) return "Save a direct interview before moving its linked candidate.";
        var target = await db.QueryFirstOrDefaultAsync<RecruitmentPipelineStage>(@"SELECT * FROM recruitment_pipeline_stages
WHERE PipelineVersionId=@PipelineVersionId AND CardScope='Application' AND StageType='Interview' AND IsActive=TRUE AND DisplayOrder>@DisplayOrder
ORDER BY DisplayOrder,Id LIMIT 1", current, tx);
        if (target is null) return "Configure an Interview stage in this candidate pipeline.";
        var reason = $"Authorized direct interview {interviewId}; earlier screening stages bypassed by {user.DisplayName}.";
        await db.ExecuteAsync(@"UPDATE recruitment_application_stage_instances SET Status='Completed',OutcomeCode='DIRECT_INTERVIEW',
ExitedAtUtc=UTC_TIMESTAMP(),ExitedByUserId=@UserId,ActiveDurationSeconds=GREATEST(0,TIMESTAMPDIFF(SECOND,EnteredAtUtc,UTC_TIMESTAMP())-PausedDurationSeconds)
WHERE Id=@InstanceId", new { current.InstanceId, UserId = user.Id }, tx);
        await AddStageEventAsync(db, tx, current.InstanceId, "Exited", "Direct interview override", reason, user.Id);
        var nextId = await db.ExecuteScalarAsync<long>(@"INSERT INTO recruitment_application_stage_instances
(ApplicationPipelineInstanceId,ApplicationId,PipelineStageId,Status,EnteredAtUtc,DueAtUtc,EnteredByUserId)
VALUES(@PipelineId,@applicationId,@StageId,'Active',UTC_TIMESTAMP(),IF(@Sla>0,TIMESTAMPADD(MINUTE,@Sla,UTC_TIMESTAMP()),NULL),@UserId); SELECT LAST_INSERT_ID();",
            new { current.PipelineId, applicationId, StageId = target.Id, Sla = target.SlaDurationMinutes, UserId = user.Id }, tx);
        await AddStageEventAsync(db, tx, nextId, "Entered", "Direct interview", reason, user.Id);
        await db.ExecuteAsync(@"UPDATE recruitment_application_pipeline_instances SET CurrentStageInstanceId=@nextId WHERE Id=@PipelineId;
UPDATE recruitment_candidate_applications SET CurrentPipelineStageInstanceId=@nextId,CurrentStage=@StageName,LastStageChangedAt=UTC_TIMESTAMP(),UpdatedAt=UTC_TIMESTAMP() WHERE Id=@applicationId;
UPDATE recruitment_interviews SET PipelineStageInstanceId=@nextId WHERE Id=@interviewId;
INSERT INTO recruitment_application_stage_history(ApplicationId,FromStage,ToStage,Reason,ChangedByUserId,ChangedAt)
VALUES(@applicationId,@FromStage,@StageName,@reason,@UserId,UTC_TIMESTAMP());",
            new { nextId, current.PipelineId, applicationId, interviewId, target.StageName, FromStage = current.StageName, reason, UserId = user.Id }, tx);
        await tx.CommitAsync();
        return "";
    }

    private sealed class DirectInterviewStage
    {
        public long PipelineId { get; set; }
        public long PipelineVersionId { get; set; }
        public long InstanceId { get; set; }
        public long Id { get; set; }
        public int DisplayOrder { get; set; }
        public string StageName { get; set; } = "";
        public string StageType { get; set; } = "";
        public string Status { get; set; } = "";
    }
}
