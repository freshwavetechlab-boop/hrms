using Dapper;
using Payroll.API.Models;
using Payroll.API.Services;
using System.Text.Json;

namespace Payroll.API.Repositories;

public sealed partial class RecruitmentCaseRepository
{
    private async Task<string> StartPanelMomApprovalAsync(MySqlConnector.MySqlConnection db, MySqlConnector.MySqlTransaction tx, RecruitmentProcessDocument document, AuthUser user)
    {
        if (!IsMoM(document.DocumentType)) return "";
        if (document.WorkflowInstanceId is > 0) return "";
        var workflowId = await db.ExecuteScalarAsync<int?>(@"SELECT stage.ApprovalWorkflowId
FROM recruitment_position_pipeline_instances flow JOIN recruitment_pipeline_stages stage ON stage.PipelineVersionId=flow.PipelineVersionId
JOIN workflowmasters w ON w.Id=stage.ApprovalWorkflowId AND w.IsActive=TRUE AND (w.ClientId IS NULL OR w.ClientId=@ClientId)
WHERE flow.Id=@HiringCaseId AND stage.StageType IN ('HR','Approval') AND stage.IsActive=TRUE
ORDER BY stage.DisplayOrder LIMIT 1", document, tx);
        if (workflowId is not > 0)
        {
            // Older job configurations keep the HR mapping on the candidate pipeline; reuse it once for the job.
            var mapped = (await db.QueryAsync<int>(@"SELECT DISTINCT stage.ApprovalWorkflowId
FROM recruitment_process_documents d JOIN recruitment_candidate_applications a ON " + RecruitmentJobMom.CoversApplication("d", "a") + @"
JOIN recruitment_application_pipeline_instances flow ON flow.ApplicationId=a.Id
JOIN recruitment_pipeline_stages stage ON stage.PipelineVersionId=flow.PipelineVersionId AND stage.StageType IN ('HR','Approval') AND stage.IsActive=TRUE
JOIN workflowmasters w ON w.Id=stage.ApprovalWorkflowId AND w.IsActive=TRUE AND (w.ClientId IS NULL OR w.ClientId=d.ClientId)
WHERE d.Id=@Id", document, tx)).ToArray();
            if (mapped.Length == 1) workflowId = mapped[0];
        }
        if (workflowId is not > 0) return "HR Division approval is not configured for this job. Configure it and retry the final panel signature.";
        var signatures = (await db.QueryAsync<RecruitmentProcessDocumentSignature>(
            "SELECT * FROM recruitment_process_document_signatures WHERE ProcessDocumentId=@Id AND CandidateId IS NULL", new { document.Id }, tx)).ToList();
        var workflow = await workflows.StartInTransactionAsync(db, tx, new StartWorkflowRequest {
            WorkflowId = workflowId.Value, ResourceType = "RecruitmentPipelineTransition", ResourceId = $"MOM:{document.Id}",
            PayloadJson = JsonSerializer.Serialize(new { hiringCaseId = document.HiringCaseId, documentId = document.Id,
                PanelSignatures = signatures, SignedAtUtc = DateTime.UtcNow, document.BodySnapshot })
        }, user.Id);
        if (workflow is null) return "The HR approval task could not start. Retry the final panel signature.";
        await db.ExecuteAsync("UPDATE recruitment_process_documents SET WorkflowInstanceId=@WorkflowId WHERE Id=@Id", new { document.Id, WorkflowId = workflow.Id }, tx);
        return "";
    }

    public async Task<IReadOnlyList<long>> SyncJobMomApprovalAsync(long documentId, long workflowInstanceId, string status)
    {
        if (status != "Approved") return []; // Returned job MoMs are revised as a whole; candidate terms stay individual.
        await using var db = Db(); await db.OpenAsync();
        return (await db.QueryAsync<long>(@"SELECT a.Id FROM recruitment_process_documents d
JOIN recruitment_candidate_applications a ON " + RecruitmentJobMom.CoversApplication("d", "a") + @"
JOIN workflowinstances w ON w.Id=d.WorkflowInstanceId AND w.Status='Approved'
 AND w.ResourceType='RecruitmentPipelineTransition' AND w.ResourceId=CONCAT('MOM:',d.Id)
WHERE d.Id=@documentId AND d.WorkflowInstanceId=@workflowInstanceId AND d.Status='Signed'
AND " + RecruitmentJobMom.CurrentFor("d") + " AND " + RecruitmentPanelSignatures.CompleteFor("d"), new { documentId, workflowInstanceId })).ToList();
    }
}
