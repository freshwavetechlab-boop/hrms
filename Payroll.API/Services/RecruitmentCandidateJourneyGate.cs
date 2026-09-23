using System.Data;
using Dapper;
using MySqlConnector;

namespace Payroll.API.Services;

internal static class RecruitmentCandidateJourneyGate
{
    internal static async Task<string> TermsApprovalAsync(MySqlConnection db, long applicationId, IDbTransaction? tx = null, long? configuredWorkflowId = null)
    {
        var approved = await db.ExecuteScalarAsync<bool>(@"SELECT EXISTS(SELECT 1
FROM recruitment_candidate_applications a JOIN recruitment_process_documents d ON d.ApplicationId=a.Id AND d.TermsVersion=a.TermsVersion
JOIN workflowinstances w ON w.Id=d.WorkflowInstanceId AND w.ResourceType='RecruitmentPipelineTransition' AND w.ResourceId=CONCAT('MOM:',d.Id) AND w.Status='Approved'
WHERE a.Id=@applicationId AND a.TermsConfirmedAtUtc IS NOT NULL AND d.Status='Signed' AND d.DocumentType='MOM'
AND (@configuredWorkflowId IS NULL OR w.WorkflowId=@configuredWorkflowId)
AND EXISTS(SELECT 1 FROM recruitment_process_document_signatures s WHERE s.ProcessDocumentId=d.Id AND s.CandidateId=a.CandidateId))", new { applicationId, configuredWorkflowId }, tx);
        return approved ? "" : "Confirm agreed terms, collect the candidate's MoM signature and complete HR Division approval first.";
    }

    internal static async Task<string> ValidateTransitionAsync(MySqlConnection db, long applicationId, long transitionId)
    {
        var target = await db.ExecuteScalarAsync<string>(@"SELECT s.StageType FROM recruitment_pipeline_transitions t
JOIN recruitment_pipeline_stages s ON s.Id=t.ToStageId WHERE t.Id=@transitionId", new { transitionId });
        var governed = await db.ExecuteScalarAsync<bool>(@"SELECT EXISTS(SELECT 1 FROM recruitment_candidate_applications a
JOIN recruitment_position_pipeline_instances p ON p.PositionId=a.PositionId AND p.Status<>'Superseded'
JOIN recruitment_pipeline_stages stage ON stage.PipelineVersionId=p.PipelineVersionId
JOIN recruitment_stage_process_document_requirements r ON r.PipelineStageId=stage.Id AND r.DocumentType='MOM' AND r.IsRequired=TRUE
WHERE a.Id=@applicationId) OR EXISTS(SELECT 1 FROM recruitment_candidate_applications WHERE Id=@applicationId AND TermsVersion>0)", new { applicationId });
        if (target == "Offer" && governed) return await TermsApprovalAsync(db, applicationId);
        if (target == "Joining" && governed) return await JoiningAsync(db, applicationId);
        if (target is "Completed" or "Joined" or "Hired")
        {
            var employee = await db.ExecuteScalarAsync<bool>(@"SELECT EXISTS(SELECT 1 FROM recruitment_candidate_applications a
WHERE a.Id=@applicationId AND a.JoinedEmployeeId IS NOT NULL)", new { applicationId });
            if (!employee) return "HR must confirm actual joining and create the employee before marking this candidate Joined / Hired.";
        }
        return "";
    }

    internal static async Task<string> JoiningAsync(MySqlConnection db, long applicationId)
    {
        var ready = await db.ExecuteScalarAsync<bool>(@"SELECT EXISTS(SELECT 1 FROM recruitment_offers o
JOIN workflowinstances w ON w.ResourceType='RecruitmentFinalOffer' AND w.ResourceId=CAST(o.Id AS CHAR) AND w.Status='Approved'
JOIN workflowhistory h ON h.InstanceId=w.Id AND h.Action='FinalDocumentGenerated'
WHERE o.ApplicationId=@applicationId AND o.Status='Accepted')", new { applicationId });
        if (!ready) return "Candidate acceptance, final departmental approval and the final signed offer are required before joining.";
        var pending = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM recruitment_candidate_checklist_items
WHERE ApplicationId=@applicationId AND Mandatory=TRUE AND Status<>'Completed'", new { applicationId });
        return pending == 0 ? "" : $"HR must verify {pending} mandatory pre-boarding document(s) before joining.";
    }
}
