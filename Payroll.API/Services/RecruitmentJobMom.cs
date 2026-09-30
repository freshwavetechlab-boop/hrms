namespace Payroll.API.Services;

// Freeze the committee and agreed terms in the existing document audit, without a schema change.
internal static class RecruitmentJobMom
{
    internal const string SnapshotSql = @"(SELECT audit.NewValueJson FROM recruitment_audit audit
WHERE audit.EntityType='RecruitmentProcessDocument' AND audit.EntityId=documentRow.Id AND audit.Action='Job MoM prepared'
ORDER BY audit.Id DESC LIMIT 1)";

    internal static readonly string CoversApplicationSql = @"(
documentRow.ApplicationId IS NULL AND documentRow.InterviewId IS NULL AND documentRow.ClientId=applicationRow.ClientId
AND applicationRow.TermsConfirmedAtUtc IS NOT NULL
AND applicationRow.CurrentStage NOT LIKE '%Reject%' AND applicationRow.CurrentStage NOT LIKE '%Withdraw%'
AND EXISTS(SELECT 1 FROM recruitment_position_pipeline_instances momCase WHERE momCase.Id=documentRow.HiringCaseId
 AND momCase.PositionId=applicationRow.PositionId AND momCase.ClientId=applicationRow.ClientId AND momCase.Status<>'Superseded')
AND EXISTS(SELECT 1 FROM recruitment_interviews momInterview WHERE momInterview.ApplicationId=applicationRow.Id
 AND momInterview.Id=(SELECT MAX(latest.Id) FROM recruitment_interviews latest WHERE latest.ApplicationId=applicationRow.Id)
 AND momInterview.Status='Completed' AND momInterview.Result='Selected'
 AND JSON_CONTAINS(" + SnapshotSql + @",JSON_OBJECT('ApplicationId',applicationRow.Id,'TermsVersion',applicationRow.TermsVersion,'InterviewId',momInterview.Id),'$.Candidates'))
)";

    internal static readonly string CurrentSql = @"(
documentRow.ApplicationId IS NULL AND documentRow.DocumentType IN ('MOM','SIGNED_MOM')
AND JSON_LENGTH(" + SnapshotSql + @",'$.Candidates')>0
AND (SELECT COUNT(*) FROM recruitment_candidate_applications momApplication WHERE " + CoversApplicationSql.Replace("applicationRow.", "momApplication.") + @")
 = JSON_LENGTH(" + SnapshotSql + @",'$.Candidates')
AND NOT EXISTS(SELECT 1 FROM recruitment_position_stage_events rework
 WHERE rework.PositionPipelineInstanceId=documentRow.HiringCaseId AND rework.EventType='ProfilesReworkStarted'
 AND rework.CreatedAtUtc>documentRow.CreatedAtUtc)
AND NOT EXISTS(SELECT 1 FROM recruitment_process_documents newer
 WHERE newer.HiringCaseId=documentRow.HiringCaseId AND newer.DocumentType=documentRow.DocumentType
 AND newer.ApplicationId IS NULL AND newer.Id>documentRow.Id AND newer.BodySnapshot IS NOT NULL)
)";
    internal static string CoversApplication(string document, string application) => CoversApplicationSql.Replace("documentRow.", document + ".").Replace("applicationRow.", application + ".");
    internal static string CurrentFor(string document) => CurrentSql.Replace("documentRow.", document + ".");
}
