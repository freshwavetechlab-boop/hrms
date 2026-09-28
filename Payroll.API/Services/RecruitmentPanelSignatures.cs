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
 OR (documentRow.ApplicationId IS NULL AND EXISTS(SELECT 1 FROM recruitment_profile_submission_batch_items item
 JOIN recruitment_profile_submission_batches batch ON batch.Id=item.BatchId
 WHERE item.ApplicationId=applicationRow.Id AND batch.HiringCaseId=documentRow.HiringCaseId AND batch.Status IN ('Approved','Forwarded'))))";

    internal const string DefaultMembersSql = @"SELECT DISTINCT panel.PanelUserId FROM recruitment_stage_default_panel_members panel
WHERE panel.PipelineStageId=documentRow.PipelineStageId AND panel.IsRequired=TRUE";
    internal static readonly string MembersSql = ActualMembersSql + " UNION " + DefaultMembersSql + " AND NOT EXISTS(" + ActualMembersSql + ")";
    internal static readonly string RequiredSql = "COALESCE(NULLIF((" + ActualMembersSql.Replace("SELECT DISTINCT panel.PanelUserId", "SELECT COUNT(DISTINCT panel.PanelUserId)") + "),0),(SELECT COUNT(DISTINCT requiredPanel.PanelUserId) FROM recruitment_stage_default_panel_members requiredPanel WHERE requiredPanel.PipelineStageId=documentRow.PipelineStageId AND requiredPanel.IsRequired=TRUE))";
    internal static readonly string CapturedSql = "(SELECT COUNT(DISTINCT signatureRow.SignerUserId) FROM recruitment_process_document_signatures signatureRow WHERE signatureRow.ProcessDocumentId=documentRow.Id AND signatureRow.CandidateId IS NULL AND signatureRow.SignerUserId IN (" + MembersSql + "))";
    internal static readonly string CompleteSql = "(" + RequiredSql + ">0 AND " + CapturedSql + ">=" + RequiredSql + ")";
    internal static string CompleteFor(string documentAlias) => CompleteSql.Replace("documentRow.", documentAlias + ".", StringComparison.Ordinal);
}
