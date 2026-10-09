using System.Globalization;
using Dapper;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class RecruitmentCaseRepository
{
    // Read the same requirements used by generation; opening a drawer never changes a case.
    public async Task<RecruitmentMomReadiness?> GetMomReadinessAsync(long id, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        var document = await ReadGenerationContextAsync(db, id, user);
        if (document is null || !IsMoM(document.DocumentType)) return null;
        if (!RecruitmentPermissions.Has(user, "recruitment.document.view", "recruitment.document.manage")
            && !await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM recruitment_process_documents documentRow WHERE documentRow.Id=@Id AND @UserId IN (" + RecruitmentPanelSignatures.MembersSql + "))", new { Id = id, UserId = user.Id })) return null;

        var canManage = RecruitmentPermissions.Has(user, "recruitment.document.manage");
        if (document.Status == "Signed" && string.IsNullOrEmpty(document.BodySnapshot))
            return new() { Action = "Revise", Message = "This older signed document has no frozen job terms. Prepare a new job MoM version; the signed record stays in history." };
        if (!string.IsNullOrEmpty(document.BodySnapshot))
        {
            var current = await db.ExecuteScalarAsync<bool>("SELECT " + RecruitmentJobMom.CurrentFor("d") + " FROM recruitment_process_documents d WHERE d.Id=@Id", new { Id = id });
            if (!current) return new() { Action = "Revise", Message = "Candidates or agreed terms changed. Prepare a new MoM version; earlier signatures remain in history." };
            var workflowStatus = await db.ExecuteScalarAsync<string?>("SELECT Status FROM workflowinstances WHERE Id=@WorkflowInstanceId AND ResourceType='RecruitmentPipelineTransition' AND ResourceId=CONCAT('MOM:',@Id)", document);
            if (workflowStatus is "Rejected" or "Returned") return new() { Action = "Revise", Message = "HR returned this MoM. Review the decision in My Tasks, update the agreed terms if needed and prepare a new version." };
            if (document.Status == "Signed")
            {
                if (!await db.ExecuteScalarAsync<bool>("SELECT " + RecruitmentPanelSignatures.CompleteFor("d") + " FROM recruitment_process_documents d WHERE d.Id=@Id", new { Id = id }))
                    return new() { Action = "Revise", Message = "This older MoM does not contain all required panel signatures. Prepare a new version for the assigned committee." };
                return workflowStatus == "Approved"
                    ? new() { Action = "Offers", Message = "Panel signatures and HR approval are complete. Continue to Offers & Pre-boarding." }
                    : new() { Action = "Approval", Message = "Panel signing is complete. The assigned HR approver continues from My Tasks; the next approval is routed automatically." };
            }

            if (await ResolveMomApprovalWorkflowAsync(db, null, document) is not > 0)
                return new() { Action = "ApprovalSetup", Message = "The hiring administrator needs to assign the HR approval workflow once. Panel signatures will then route to those approvers automatically." };
            var pending = (await db.QueryAsync<PendingMomSigner>(@"SELECT u.Id,COALESCE(NULLIF(u.DisplayName,''),u.Email) Name
FROM recruitment_process_documents documentRow JOIN authusers u ON u.Id IN (" + RecruitmentPanelSignatures.MembersSql + @")
WHERE documentRow.Id=@Id AND NOT EXISTS(SELECT 1 FROM recruitment_process_document_signatures s
 WHERE s.ProcessDocumentId=documentRow.Id AND s.SignerUserId=u.Id AND s.CandidateId IS NULL)
ORDER BY u.Id", new { Id = id })).ToList();
            return new() { Action = "Sign", PendingSigners = pending.Select(row => row.Name).ToList(),
                CanSign = pending.Any(row => row.Id == user.Id) && RecruitmentPermissions.Has(user, "recruitment.interview.panel", "recruitment.document.sign"),
                Message = pending.Count > 0 ? "Waiting for panel signatures: " + string.Join(", ", pending.Select(row => row.Name)) + ". The final signature starts HR approval automatically."
                    : "Panel signatures are recorded. Refresh to see the latest approval status." };
        }

        if (document.TemplateId is not null && (!document.TemplateIsActive || (document.TemplateClientId != 0 && document.TemplateClientId != document.ClientId)))
            return new() { Action = "Template", Message = "The assigned custom MoM template is unavailable. The hiring administrator can restore it or use the standard MoM format." };
        if (!await db.ExecuteScalarAsync<bool>(@"SELECT EXISTS(SELECT 1 FROM recruitment_stage_process_document_requirements
WHERE PipelineStageId=@PipelineStageId AND DocumentType=@DocumentType AND TemplateId <=> @TemplateId)", document))
            return new() { Action = "Template", Message = "This draft no longer matches the job's document requirement. The hiring administrator needs to check its pipeline template before preparation." };
        var (_, error) = await SelectionCommitteeTemplateValuesAsync(db, document, CultureInfo.GetCultureInfo("en-IN"));
        if (error.Length > 0) return new() { Action = document.PreparationAction, Message = error, SelectedCandidateCount = document.SelectedCandidateCount };
        return new() { Action = "Generate", CanGenerate = canManage, SelectedCandidateCount = document.SelectedCandidateCount,
            Message = $"{document.SelectedCandidateCount} selected candidates are ready. Prepare MoM to include their confirmed terms, interview scores and assigned panel automatically." };
    }

    private sealed class PendingMomSigner
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }
}
