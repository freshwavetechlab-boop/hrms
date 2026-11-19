using Dapper;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public partial class RecruitmentPipelineRepository
{
    public async Task<bool> CanExecuteStageActionsAsync(long applicationId, AuthUser user)
    {
        await using var db = Db(); await db.OpenAsync();
        return await RecruitmentPipelineStageAccess.AllowsApplicationAsync(db, applicationId, user);
    }

    // Check before the generic workflow transaction commits, rather than
    // discovering an unauthorized stage only while applying its side effects.
    public async Task<bool> CanActOnStageWorkflowAsync(WorkflowInstance? instance, AuthUser user)
    {
        if (instance is null || instance.ResourceType is not ("RecruitmentPipelineTransition" or "RecruitmentPipelineStageAction")) return true;
        await using var db = Db(); await db.OpenAsync();
        if (!await RecruitmentPipelineStageAccess.HasPoliciesAsync(db)) return true;
        if (instance.ResourceType == "RecruitmentPipelineStageAction")
        {
            var applicationId = await db.ExecuteScalarAsync<long?>("SELECT ApplicationId FROM recruitment_stage_action_executions WHERE WorkflowInstanceId=@Id LIMIT 1", new { instance.Id });
            return applicationId > 0 && await RecruitmentPipelineStageAccess.AllowsApplicationAsync(db, applicationId.Value, user);
        }
        if (instance.ResourceId.StartsWith("MOM:", StringComparison.OrdinalIgnoreCase) && long.TryParse(instance.ResourceId[4..], out var documentId))
            return await AttachmentRepository.AllowsRecruitmentStageAttachmentAsync(db, user, "RECRUITMENT_PROCESS_DOCUMENT", documentId);
        if (instance.ResourceId.StartsWith("HIRING_CASE:", StringComparison.OrdinalIgnoreCase) && long.TryParse(instance.ResourceId[12..], out var advanceId))
        {
            var caseId = await db.ExecuteScalarAsync<long?>("SELECT HiringCaseId FROM recruitment_hiring_case_advance_requests WHERE Id=@Id", new { Id = advanceId });
            return caseId > 0 && await RecruitmentPipelineStageAccess.AllowsCaseAsync(db, caseId.Value, user);
        }
        if (long.TryParse(instance.ResourceId, out var transitionId))
        {
            var applicationId = await db.ExecuteScalarAsync<long?>("SELECT ApplicationId FROM recruitment_pipeline_transition_requests WHERE Id=@Id", new { Id = transitionId });
            return applicationId > 0 && await RecruitmentPipelineStageAccess.AllowsApplicationAsync(db, applicationId.Value, user);
        }
        return false;
    }
}
