namespace Payroll.API.Services;

// Freeze the committee and agreed terms in the existing document audit, without a schema change.
internal static class RecruitmentJobMom
{
    internal const string DefaultSubject = "Minutes of Meeting — {{positionName}}";
    internal const string DefaultBody = """
Client: {{clientName}}
Work order: {{workOrderNumber}}
Location: {{location}}
Interview date(s): {{interviewDate}}

Selection committee
{{panelMembersList}}

Interview results
{{candidateResultTable}}

Score annexure
{{scoreAnnexureTable}}

Panel signatures
{{panelSignatureBlock}}
""";

    // Use the same final interview decision as hiring progression. Profile batches are
    // an optional forwarding record, not a second selection decision for a job MoM.
    internal const string SelectedCandidatesSql = @"SELECT applicationRow.Id ApplicationId,
CONCAT(candidateRow.FirstName,' ',candidateRow.LastName) CandidateName,
interviewRow.Id InterviewId,interviewRow.ScheduledStart,interviewRow.Status InterviewStatus,
interviewRow.Result,interviewRow.OverallScore,interviewRow.RoundConfigurationId,
applicationRow.AgreedCtc,applicationRow.TermsVersion,applicationRow.TermsConfirmedAtUtc
FROM recruitment_position_pipeline_instances hiring
JOIN recruitment_candidate_applications applicationRow ON applicationRow.PositionId=hiring.PositionId AND applicationRow.ClientId=hiring.ClientId
JOIN recruitment_candidates candidateRow ON candidateRow.Id=applicationRow.CandidateId
JOIN recruitment_interviews interviewRow ON interviewRow.Id=(SELECT MAX(latest.Id) FROM recruitment_interviews latest WHERE latest.ApplicationId=applicationRow.Id)
WHERE hiring.Id=@HiringCaseId AND hiring.PositionId=@PositionId AND hiring.ClientId=@ClientId AND hiring.Status<>'Superseded'
AND (@ApplicationId IS NULL OR applicationRow.Id=@ApplicationId)
AND applicationRow.ApplicationType='Application'
AND interviewRow.Status='Completed' AND interviewRow.Result='Selected'
AND applicationRow.CurrentStage NOT LIKE '%Reject%' AND applicationRow.CurrentStage NOT LIKE '%Withdraw%'
AND NOT EXISTS (SELECT 1 FROM recruitment_offers offerRow WHERE offerRow.Id=(
 SELECT latestOffer.Id FROM recruitment_offers latestOffer WHERE latestOffer.ApplicationId=applicationRow.Id
 ORDER BY latestOffer.UpdatedAt DESC,latestOffer.Id DESC LIMIT 1) AND offerRow.Status IN ('Rejected','Withdrawn','Expired'))
ORDER BY CandidateName,applicationRow.Id";

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
