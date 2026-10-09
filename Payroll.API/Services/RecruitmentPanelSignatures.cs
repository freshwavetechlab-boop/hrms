namespace Payroll.API.Services;

// Keep signer eligibility, displayed counts and progression on the same committee.
internal static class RecruitmentPanelSignatures
{
    internal const string ActualMembersSql = @"SELECT DISTINCT panel.PanelUserId
FROM recruitment_interview_panel_members panel
JOIN recruitment_interviews interviewRow ON interviewRow.Id=panel.InterviewId
JOIN recruitment_candidate_applications applicationRow ON applicationRow.Id=interviewRow.ApplicationId
WHERE applicationRow.ClientId=documentRow.ClientId AND interviewRow.Status='Completed' AND interviewRow.Result='Selected'
AND interviewRow.Id=COALESCE(documentRow.InterviewId,(SELECT MAX(latest.Id) FROM recruitment_interviews latest WHERE latest.ApplicationId=applicationRow.Id))
AND ((documentRow.ApplicationId IS NOT NULL AND applicationRow.Id=documentRow.ApplicationId)
 OR (documentRow.ApplicationId IS NULL AND EXISTS(SELECT 1 FROM recruitment_position_pipeline_instances hiring
 WHERE hiring.Id=documentRow.HiringCaseId AND hiring.PositionId=applicationRow.PositionId
 AND hiring.ClientId=applicationRow.ClientId AND hiring.Status<>'Superseded')))
AND applicationRow.ApplicationType='Application'
AND applicationRow.CurrentStage NOT LIKE '%Reject%' AND applicationRow.CurrentStage NOT LIKE '%Withdraw%'
AND NOT EXISTS(SELECT 1 FROM recruitment_offers offerRow WHERE offerRow.Id=(
 SELECT latestOffer.Id FROM recruitment_offers latestOffer WHERE latestOffer.ApplicationId=applicationRow.Id
 ORDER BY latestOffer.UpdatedAt DESC,latestOffer.Id DESC LIMIT 1) AND offerRow.Status IN ('Rejected','Withdrawn','Expired'))";

    internal const string DefaultMembersSql = @"SELECT DISTINCT panel.PanelUserId FROM recruitment_stage_default_panel_members panel
WHERE panel.PipelineStageId=documentRow.PipelineStageId AND panel.IsRequired=TRUE";
    internal static readonly string SnapshotMembersSql = "SELECT panelUser.Id FROM authusers panelUser WHERE JSON_CONTAINS(" + RecruitmentJobMom.SnapshotSql + ",JSON_OBJECT('UserId',panelUser.Id),'$.PanelMembers')";
    internal static readonly string MembersSql = SnapshotMembersSql + " UNION " + ActualMembersSql + " AND " + RecruitmentJobMom.SnapshotSql + " IS NULL UNION " + DefaultMembersSql + " AND " + RecruitmentJobMom.SnapshotSql + " IS NULL AND NOT EXISTS(" + ActualMembersSql + ")";
    internal static readonly string RequiredSql = "CASE WHEN " + RecruitmentJobMom.SnapshotSql + " IS NOT NULL THEN JSON_LENGTH(" + RecruitmentJobMom.SnapshotSql + ",'$.PanelMembers') ELSE COALESCE(NULLIF((" + ActualMembersSql.Replace("SELECT DISTINCT panel.PanelUserId", "SELECT COUNT(DISTINCT panel.PanelUserId)") + "),0),(SELECT COUNT(DISTINCT requiredPanel.PanelUserId) FROM recruitment_stage_default_panel_members requiredPanel WHERE requiredPanel.PipelineStageId=documentRow.PipelineStageId AND requiredPanel.IsRequired=TRUE)) END";
    internal static readonly string CapturedSql = "(SELECT COUNT(DISTINCT signatureRow.SignerUserId) FROM recruitment_process_document_signatures signatureRow WHERE signatureRow.ProcessDocumentId=documentRow.Id AND signatureRow.CandidateId IS NULL AND signatureRow.SignerUserId IN (" + MembersSql + "))";
    internal static readonly string CompleteSql = "(" + RequiredSql + ">0 AND " + CapturedSql + ">=" + RequiredSql + ")";
    internal static string CompleteFor(string documentAlias) => CompleteSql.Replace("documentRow.", documentAlias + ".", StringComparison.Ordinal);
}
