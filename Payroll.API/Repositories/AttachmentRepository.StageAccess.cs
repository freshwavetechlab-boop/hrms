using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public partial class AttachmentRepository
{
    private async Task<bool> HasRecruitmentStageAccessAsync(AuthUser user, string entityType, long entityId)
    {
        if (entityType is not ("CANDIDATE" or "FORM_SUBMISSION" or "RECRUITMENT_PROCESS_DOCUMENT" or "RECRUITMENT_PROCESS_SUPPORT")) return true;
        await using var db = Connection(); await db.OpenAsync();
        return await AllowsRecruitmentStageAttachmentAsync(db, user, entityType, entityId);
    }

    internal static async Task<bool> AllowsRecruitmentStageAttachmentAsync(MySqlConnection db, AuthUser user, string entityType, long entityId)
    {
        if (!await RecruitmentPipelineStageAccess.HasPoliciesAsync(db)) return true;
        if (entityType == "CANDIDATE") return await RecruitmentPipelineStageAccess.AllowsCandidateAsync(db, entityId, user);
        if (entityType == "FORM_SUBMISSION")
        {
            var application = await db.ExecuteScalarAsync<long?>("SELECT ApplicationId FROM form_submissions WHERE Id=@Id", new { Id = entityId });
            return application is not > 0 || await RecruitmentPipelineStageAccess.AllowsApplicationAsync(db, application.Value, user);
        }
        var document = await db.QuerySingleOrDefaultAsync<StageAttachmentResource>("SELECT ApplicationId,HiringCaseId,PipelineStageId FROM recruitment_process_documents WHERE Id=@Id", new { Id = entityId });
        if (document is null) return false;
        if (document.PipelineStageId > 0 && !await RecruitmentPipelineStageAccess.AllowsStageAsync(db, document.PipelineStageId.Value, user)) return false;
        if (document.ApplicationId > 0 && !await RecruitmentPipelineStageAccess.AllowsApplicationAsync(db, document.ApplicationId.Value, user)) return false;
        if (document.HiringCaseId > 0 && !await RecruitmentPipelineStageAccess.AllowsCaseAsync(db, document.HiringCaseId.Value, user)) return false;
        return true;
    }

    private sealed record StageAttachmentResource(long? ApplicationId, long? HiringCaseId, long? PipelineStageId);
}
