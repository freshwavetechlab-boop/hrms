using Dapper;
using Payroll.API.Models;
using Payroll.API.Services;
using System.Text.Json;

namespace Payroll.API.Repositories;

public sealed partial class RecruitmentCaseRepository
{
    public async Task<(RecruitmentProcessDocument? Row, string Error)> PrepareCandidateMomAsync(long applicationId, AuthUser user, CancellationToken cancellationToken)
    {
        await using var db = Db(); await db.OpenAsync(cancellationToken);
        var lockName = $"recruitment-candidate-mom:{applicationId}";
        if (await db.ExecuteScalarAsync<int>("SELECT GET_LOCK(@lockName,5)", new { lockName }) != 1)
            return (null, "MoM is being prepared for this candidate. Refresh and retry.");
        try
        {
        var source = await db.QueryFirstOrDefaultAsync<CandidateMomSource>(@"SELECT a.Id ApplicationId,a.ClientId,a.TermsVersion,
hiring.Id HiringCaseId,requirement.PipelineStageId,requirement.TemplateId,
(SELECT MAX(i.Id) FROM recruitment_interviews i WHERE i.ApplicationId=a.Id AND i.Status='Completed' AND i.Result='Selected') InterviewId
FROM recruitment_candidate_applications a JOIN recruitment_position_pipeline_instances hiring ON hiring.PositionId=a.PositionId AND hiring.Status<>'Superseded'
JOIN recruitment_pipeline_stages stage ON stage.PipelineVersionId=hiring.PipelineVersionId AND stage.IsActive=TRUE
JOIN recruitment_stage_process_document_requirements requirement ON requirement.PipelineStageId=stage.Id AND requirement.DocumentType='MOM'
WHERE a.Id=@applicationId AND a.TermsConfirmedAtUtc IS NOT NULL AND (@ClientId IS NULL OR a.ClientId=@ClientId)
ORDER BY hiring.Id DESC,stage.DisplayOrder LIMIT 1", new { applicationId, user.ClientId });
        if (source?.TemplateId is not > 0 || source.InterviewId is not > 0)
            return (null, "Configure the job's MoM template and confirm the selected candidate's agreed terms first.");
        var existing = await db.QueryFirstOrDefaultAsync<RecruitmentProcessDocument>(@"SELECT * FROM recruitment_process_documents
WHERE ApplicationId=@applicationId AND TermsVersion=@TermsVersion AND DocumentType='MOM'
AND NOT EXISTS(SELECT 1 FROM recruitment_process_document_signatures legacy WHERE legacy.ProcessDocumentId=recruitment_process_documents.Id AND legacy.CandidateId IS NOT NULL) ORDER BY Id DESC LIMIT 1", source);
        if (existing is null)
        {
            var saved = await SaveProcessDocumentAsync(new SaveRecruitmentProcessDocument {
                ApplicationId = applicationId, ClientId = source.ClientId, HiringCaseId = source.HiringCaseId,
                InterviewId = source.InterviewId, PipelineStageId = source.PipelineStageId, TemplateId = source.TemplateId, DocumentType = "MOM"
            }, user);
            if (saved.Row is null) return saved;
            existing = saved.Row;
            await db.ExecuteAsync("UPDATE recruitment_process_documents SET TermsVersion=@TermsVersion WHERE Id=@Id", new { source.TermsVersion, existing.Id });
        }
        if (existing.BodySnapshot is { Length: > 0 }) return (existing, "");
        return await GenerateProcessDocumentAsync(existing.Id, user, "", "Candidate terms confirmation", cancellationToken);
        }
        finally { await db.ExecuteScalarAsync<int>("SELECT RELEASE_LOCK(@lockName)", new { lockName }); }
    }

    public async Task<CandidateMom?> GetCandidateMomAsync(long applicationId, long candidateId)
    {
        await using var db = Db();
        return await db.QueryFirstOrDefaultAsync<CandidateMom>(@"SELECT d.Id,d.ApplicationId,d.VersionNumber,d.TermsVersion,d.BodySnapshot,d.Status,d.SignedAtUtc,
COALESCE(w.Status,'Awaiting panel signatures') ApprovalStatus,
(SELECT h.Comment FROM workflowhistory h WHERE h.InstanceId=w.Id AND h.Action IN ('Rejected','Sent Back') ORDER BY h.Id DESC LIMIT 1) ReviewComment
FROM recruitment_process_documents d JOIN recruitment_candidate_applications a ON a.Id=d.ApplicationId AND a.TermsVersion=d.TermsVersion
LEFT JOIN workflowinstances w ON w.Id=d.WorkflowInstanceId
WHERE a.Id=@applicationId AND a.CandidateId=@candidateId AND a.TermsConfirmedAtUtc IS NOT NULL
AND d.DocumentType='MOM' AND d.BodySnapshot IS NOT NULL ORDER BY d.Id DESC LIMIT 1", new { applicationId, candidateId });
    }

    private async Task<string> StartPanelMomApprovalAsync(MySqlConnector.MySqlConnection db, MySqlConnector.MySqlTransaction tx, RecruitmentProcessDocument document, AuthUser user)
    {
        if (document.ApplicationId is not > 0 || document.TermsVersion is null) return "";
        if (document.WorkflowInstanceId is > 0) return "";
        var workflowId = await db.ExecuteScalarAsync<int?>(@"SELECT stage.ApprovalWorkflowId
FROM recruitment_application_pipeline_instances flow JOIN recruitment_pipeline_stages stage ON stage.PipelineVersionId=flow.PipelineVersionId
JOIN workflowmasters w ON w.Id=stage.ApprovalWorkflowId AND w.IsActive=TRUE AND (w.ClientId IS NULL OR w.ClientId=@ClientId)
WHERE flow.ApplicationId=@ApplicationId AND stage.StageType IN ('HR','Approval') AND stage.IsActive=TRUE
ORDER BY stage.DisplayOrder LIMIT 1", document, tx);
        if (workflowId is not > 0) return "HR Division approval is not configured for this job. Configure it and retry the final panel signature.";
        var signatures = (await db.QueryAsync<RecruitmentProcessDocumentSignature>(
            "SELECT * FROM recruitment_process_document_signatures WHERE ProcessDocumentId=@Id AND CandidateId IS NULL", new { document.Id }, tx)).ToList();
        var workflow = await workflows.StartInTransactionAsync(db, tx, new StartWorkflowRequest {
            WorkflowId = workflowId.Value, ResourceType = "RecruitmentPipelineTransition", ResourceId = $"MOM:{document.Id}",
            PayloadJson = JsonSerializer.Serialize(new { applicationId = document.ApplicationId, documentId = document.Id,
                termsVersion = document.TermsVersion, PanelSignatures = signatures, SignedAtUtc = DateTime.UtcNow, document.BodySnapshot })
        }, user.Id);
        if (workflow is null) return "The HR approval task could not start. Retry the final panel signature.";
        await db.ExecuteAsync("UPDATE recruitment_process_documents SET WorkflowInstanceId=@WorkflowId WHERE Id=@Id", new { document.Id, WorkflowId = workflow.Id }, tx);
        return "";
    }

    public async Task<long?> SyncCandidateMomApprovalAsync(long documentId, long workflowInstanceId, string status)
    {
        await using var db = Db(); await db.OpenAsync();
        var applicationId = await db.ExecuteScalarAsync<long?>(@"SELECT a.Id FROM recruitment_process_documents d
JOIN recruitment_candidate_applications a ON a.Id=d.ApplicationId AND a.TermsVersion=d.TermsVersion AND a.TermsConfirmedAtUtc IS NOT NULL
WHERE d.Id=@documentId AND d.WorkflowInstanceId=@workflowInstanceId AND d.Status='Signed' AND " + RecruitmentPanelSignatures.CompleteFor("d"), new { documentId, workflowInstanceId });
        if (applicationId.HasValue && status is "Rejected" or "Sent Back")
            await db.ExecuteAsync("UPDATE recruitment_candidate_applications SET TermsConfirmedAtUtc=NULL WHERE Id=@applicationId", new { applicationId });
        return status == "Approved" ? applicationId : null;
    }

    private sealed class CandidateMomSource
    {
        public long ApplicationId { get; set; }
        public int ClientId { get; set; }
        public int TermsVersion { get; set; }
        public long HiringCaseId { get; set; }
        public long PipelineStageId { get; set; }
        public long? TemplateId { get; set; }
        public long? InterviewId { get; set; }
    }
}
