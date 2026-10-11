using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class RecruitmentCaseRepository
{
    // Resolve real task assignments, never a user supplied name or an arbitrary stage recipient.
    private static async Task<List<RecruitmentMomWorkOwner>> ReadPendingMomWorkAsync(MySqlConnection db, long[] applicationIds)
    {
        if (applicationIds.Length == 0) return [];
        var rows = (await db.QueryAsync<RecruitmentMomWorkOwner>(@"
SELECT applicationRow.ClientId,work.ApplicationId,work.InterviewId,u.Id UserId,COALESCE(NULLIF(u.DisplayName,''),u.Email) Name,
u.Email,COALESCE(employeeRow.Designation,'') Designation,work.Role,work.Task,
CASE WHEN work.Task='Terms' THEN applicationRow.CurrentStage
 ELSE COALESCE(stageRow.StageName,applicationRow.CurrentStage) END StageName
FROM (
 SELECT i.ApplicationId,i.Id InterviewId,panel.PanelUserId UserId,panel.PanelRole Role,
 CASE WHEN feedback.Id IS NULL THEN 'Feedback' ELSE 'Scores' END Task
 FROM recruitment_interviews i JOIN recruitment_interview_panel_members panel ON panel.InterviewId=i.Id
 LEFT JOIN recruitment_interview_feedback feedback ON feedback.InterviewId=i.Id AND feedback.PanelUserId=panel.PanelUserId
 WHERE i.ApplicationId IN @Ids AND i.Status IN ('Scheduled','Rescheduled','Completed')
 AND (feedback.Id IS NULL OR EXISTS (
   SELECT 1 FROM recruitment_interview_stage_competencies competency
   WHERE competency.InterviewStageConfigurationId=i.RoundConfigurationId AND NOT EXISTS (
     SELECT 1 FROM recruitment_interview_feedback_competency_scores score
     JOIN recruitment_interview_feedback scoredFeedback ON scoredFeedback.Id=score.InterviewFeedbackId
     WHERE scoredFeedback.InterviewId=i.Id AND score.InterviewStageCompetencyId=competency.Id)))
 UNION ALL
 SELECT i.ApplicationId,i.Id,i.DecisionApproverUserId,'Final decision approver','Decision'
 FROM recruitment_interviews i WHERE i.ApplicationId IN @Ids AND i.Status IN ('Scheduled','Rescheduled')
 AND EXISTS (SELECT 1 FROM recruitment_interview_panel_members panel WHERE panel.InterviewId=i.Id)
 AND NOT EXISTS (SELECT 1 FROM recruitment_interview_panel_members panel
   LEFT JOIN recruitment_interview_feedback feedback ON feedback.InterviewId=i.Id AND feedback.PanelUserId=panel.PanelUserId
   WHERE panel.InterviewId=i.Id AND feedback.Id IS NULL)
 UNION ALL
 SELECT app.Id,i.Id,COALESCE(app.RecruiterUserId,positionRow.RecruiterUserId),'Assigned recruiter',
 CASE WHEN i.Id IS NULL OR i.Status='Cancelled' THEN 'Schedule' ELSE 'Terms' END
 FROM recruitment_candidate_applications app JOIN recruitment_open_positions positionRow ON positionRow.Id=app.PositionId
 LEFT JOIN recruitment_interviews i ON i.Id=(SELECT MAX(latest.Id) FROM recruitment_interviews latest WHERE latest.ApplicationId=app.Id)
 WHERE app.Id IN @Ids AND (i.Id IS NULL OR i.Status='Cancelled'
   OR (i.Status='Completed' AND i.Result='Selected' AND (app.TermsConfirmedAtUtc IS NULL OR COALESCE(app.AgreedCtc,0)<=0)))
) work
JOIN recruitment_candidate_applications applicationRow ON applicationRow.Id=work.ApplicationId
LEFT JOIN recruitment_interviews interviewRow ON interviewRow.Id=(SELECT MAX(latest.Id) FROM recruitment_interviews latest WHERE latest.ApplicationId=applicationRow.Id)
LEFT JOIN recruitment_interview_stage_configurations roundRow ON roundRow.Id=interviewRow.RoundConfigurationId
LEFT JOIN recruitment_pipeline_stages stageRow ON stageRow.Id=roundRow.PipelineStageId
JOIN authusers u ON u.Id=work.UserId AND u.IsActive=TRUE AND (u.ClientId IS NULL OR u.ClientId=applicationRow.ClientId)
LEFT JOIN employees employeeRow ON employeeRow.Id=u.EmployeeId
WHERE work.InterviewId <=> interviewRow.Id
ORDER BY work.ApplicationId,work.Task,u.Id", new { Ids = applicationIds })).ToList();
        foreach (var row in rows)
        {
            var scope = $"clientId={row.ClientId}";
            row.Route = row.Task switch {
                "Terms" => $"/recruitment/mom-and-negotiation?section=negotiation&{scope}&applicationId={row.ApplicationId}",
                "Schedule" => $"/recruitment/interview-queue?{scope}",
                "Decision" => $"/recruitment/interviews?{scope}&decisionInterviewId={row.InterviewId}",
                _ => $"/recruitment/interviews?{scope}&feedbackInterviewId={row.InterviewId}"
            };
        }
        return rows;
    }

    public async Task<(bool Ok, string Error)> PingMomWorkAsync(long documentId, RecruitmentMomPingRequest request, AuthUser user)
    {
        if (!RecruitmentPermissions.Has(user, "recruitment.document.manage")) return (false, "You cannot send MoM reminders.");
        var readiness = await GetMomReadinessAsync(documentId, user);
        var owner = readiness?.CandidateChecks.Where(row => row.ApplicationId == request.ApplicationId)
            .SelectMany(row => row.PendingWorkOwners).FirstOrDefault(row => row.UserId == request.UserId && row.Task == request.Task);
        if (owner is null) return (false, "This user has no pending assigned work for this job. Refresh its readiness.");
        await using var db = Db();
        await db.OpenAsync();
        await using var tx = await db.BeginTransactionAsync();
        // Serialize reminders for this document and avoid repeated clicks flooding the bell.
        await db.ExecuteScalarAsync<long>("SELECT Id FROM recruitment_process_documents WHERE Id=@Id FOR UPDATE", new { Id = documentId }, tx);
        var recent = await db.ExecuteScalarAsync<bool>(@"SELECT EXISTS(SELECT 1 FROM recruitment_audit
WHERE EntityType='RecruitmentProcessDocument' AND EntityId=@Id AND Action='MoM work ping'
AND ChangedOn>DATE_SUB(UTC_TIMESTAMP(),INTERVAL 5 MINUTE)
AND JSON_UNQUOTE(JSON_EXTRACT(NewValueJson,'$.ApplicationId'))=@ApplicationId
AND JSON_UNQUOTE(JSON_EXTRACT(NewValueJson,'$.UserId'))=@UserId
AND JSON_UNQUOTE(JSON_EXTRACT(NewValueJson,'$.Task'))=@Task)", new { Id = documentId, request.ApplicationId, request.UserId, request.Task }, tx);
        if (!recent)
            await db.ExecuteAsync(@"INSERT INTO recruitment_audit(EntityType,EntityId,Action,NewValueJson,ChangedByUserId,ChangedOn)
VALUES('RecruitmentProcessDocument',@Id,'MoM work ping',@Payload,@ActorId,UTC_TIMESTAMP())",
                new { Id = documentId, ActorId = user.Id, Payload = JsonSerializer.Serialize(new { owner.ApplicationId, owner.InterviewId, owner.UserId, owner.Task }) }, tx);
        await tx.CommitAsync();
        return (true, "");
    }

    public async Task<List<RecruitmentMomPing>> ListMomPingsAsync(AuthUser user)
    {
        if (!RecruitmentPermissions.Has(user, "recruitment.interview.panel", "recruitment.interview.schedule", "recruitment.proposal.manage", "recruitment.offer.manage", "recruitment.document.view", "recruitment.document.manage")) return [];
        await using var db = Db();
        await db.OpenAsync();
        var pings = (await db.QueryAsync<RecruitmentMomPing>(@"SELECT audit.Id,documentRow.Id DocumentId,
JSON_UNQUOTE(JSON_EXTRACT(audit.NewValueJson,'$.ApplicationId')) ApplicationId,
NULLIF(JSON_UNQUOTE(JSON_EXTRACT(audit.NewValueJson,'$.InterviewId')),'null') InterviewId,
JSON_UNQUOTE(JSON_EXTRACT(audit.NewValueJson,'$.UserId')) UserId,
JSON_UNQUOTE(JSON_EXTRACT(audit.NewValueJson,'$.Task')) Task,
CONCAT('MoM reminder · ',positionRow.PositionTitle) Title,
CONCAT(COALESCE(NULLIF(actor.DisplayName,''),actor.Email,'Hiring administrator'),' requested ',JSON_UNQUOTE(JSON_EXTRACT(audit.NewValueJson,'$.Task')),' for ',candidateRow.FirstName,' ',candidateRow.LastName) Detail
FROM recruitment_audit audit
JOIN recruitment_process_documents documentRow ON documentRow.Id=audit.EntityId AND documentRow.DocumentType IN ('MOM','SIGNED_MOM')
JOIN recruitment_position_pipeline_instances hiring ON hiring.Id=documentRow.HiringCaseId AND hiring.Status<>'Superseded'
JOIN recruitment_candidate_applications applicationRow ON applicationRow.Id=JSON_UNQUOTE(JSON_EXTRACT(audit.NewValueJson,'$.ApplicationId'))
 AND applicationRow.ClientId=documentRow.ClientId AND applicationRow.PositionId=hiring.PositionId
JOIN recruitment_candidates candidateRow ON candidateRow.Id=applicationRow.CandidateId
JOIN recruitment_open_positions positionRow ON positionRow.Id=applicationRow.PositionId
LEFT JOIN authusers actor ON actor.Id=audit.ChangedByUserId
WHERE audit.EntityType='RecruitmentProcessDocument' AND audit.Action='MoM work ping'
AND JSON_UNQUOTE(JSON_EXTRACT(audit.NewValueJson,'$.UserId'))=@UserId
AND (@ClientId IS NULL OR documentRow.ClientId=@ClientId)
AND applicationRow.ApplicationType='Application' AND applicationRow.CurrentStage NOT LIKE '%Reject%' AND applicationRow.CurrentStage NOT LIKE '%Withdraw%'
AND NOT EXISTS (SELECT 1 FROM recruitment_offers offerRow WHERE offerRow.Id=(SELECT latest.Id FROM recruitment_offers latest
 WHERE latest.ApplicationId=applicationRow.Id ORDER BY latest.UpdatedAt DESC,latest.Id DESC LIMIT 1) AND offerRow.Status IN ('Rejected','Withdrawn','Expired'))
ORDER BY audit.Id DESC LIMIT 100", new { UserId = user.Id, user.ClientId })).ToList();
        if (pings.Count == 0) return [];
        var pending = await ReadPendingMomWorkAsync(db, pings.Select(row => row.ApplicationId).Distinct().ToArray());
        return pings.GroupBy(row => (row.DocumentId,row.ApplicationId,row.Task)).Select(group => group.First())
            .Where(ping => pending.Any(owner => owner.ApplicationId == ping.ApplicationId && owner.InterviewId == ping.InterviewId && owner.UserId == user.Id && owner.Task == ping.Task))
            .Select(ping => { ping.Route = pending.First(owner => owner.ApplicationId == ping.ApplicationId && owner.UserId == user.Id && owner.Task == ping.Task).Route; return ping; }).ToList();
    }
}
