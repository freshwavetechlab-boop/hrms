using System.Data;
using Dapper;
using MySqlConnector;
using Payroll.API.Repositories;

namespace Payroll.API.Services;

/// <summary>Shared by admin release and public response links; no state changes.</summary>
internal static class RecruitmentOfferReleaseGate
{
    private sealed class PositionStage
    {
        public long HiringCaseId { get; set; }
        public long PipelineVersionId { get; set; }
        public int DisplayOrder { get; set; }
        public string StageCode { get; set; } = "";
        public string StageType { get; set; } = "";
        public string StageName { get; set; } = "";
    }
    internal static async Task<string> ValidateAsync(MySqlConnection db, long applicationId,
        bool submittingForApproval = false, IDbTransaction? transaction = null, long? offerId = null)
    {
        var budgetState = await db.ExecuteScalarAsync<string?>(@"SELECT r.BudgetApprovalStatus
FROM recruitment_candidate_applications a JOIN recruitment_open_positions p ON p.Id=a.PositionId
JOIN recruitment_requisitions r ON r.Id=p.RequisitionId
WHERE a.Id=@ApplicationId AND r.BudgetAvailable=TRUE AND r.BudgetApproverUserId IS NOT NULL",
            new { ApplicationId = applicationId }, transaction);
        if (budgetState is not null && budgetState != "Approved")
            return "The selected budget approver must approve the current hiring budget before an offer can proceed.";
        var stage = await db.QueryFirstOrDefaultAsync<PositionStage>(@"SELECT hiring.Id HiringCaseId,hiring.PipelineVersionId,stage.DisplayOrder,stage.StageCode,stage.StageType,stage.StageName
FROM recruitment_candidate_applications application
JOIN recruitment_position_pipeline_instances hiring ON hiring.PositionId=application.PositionId
JOIN recruitment_position_stage_instances currentStage ON currentStage.Id=hiring.CurrentStageInstanceId
JOIN recruitment_pipeline_stages stage ON stage.Id=currentStage.PipelineStageId
WHERE application.Id=@ApplicationId AND hiring.Status IN ('Active','Candidate Flow')
ORDER BY hiring.Id DESC LIMIT 1", new { ApplicationId = applicationId }, transaction);
        // Legacy/application-only clients do not have a position workflow to bypass.
        if (stage is null)
        {
            var hasJourney = await db.ExecuteScalarAsync<bool>(@"SELECT EXISTS(SELECT 1 FROM recruitment_position_pipeline_instances hiring
JOIN recruitment_candidate_applications application ON application.PositionId=hiring.PositionId
WHERE application.Id=@ApplicationId AND hiring.Status<>'Superseded')", new { ApplicationId = applicationId }, transaction);
            return hasJourney ? "The hiring-position journey is not active. Restore the governed offer stage before issuing or accepting an offer."
                : await ValidateBudgetAsync(db, applicationId, offerId, submittingForApproval, transaction);
        }
        var stageError = StageError(stage.StageCode, stage.StageType, stage.StageName, submittingForApproval);
        if (stageError.Length > 0) return stageError;
        var missing = (await db.QueryAsync<string>(@"SELECT required.StageName FROM recruitment_pipeline_stages required
WHERE required.PipelineVersionId=@PipelineVersionId AND required.CardScope='Position' AND required.IsActive=TRUE
 AND required.DisplayOrder<@DisplayOrder
 AND (required.RequiresApproval=TRUE OR required.StageCode IN ('SIGNING_MOM','NEGOTIATION_AND_MOM_TO_HR','HR_DIVISION_APPROVAL')
 OR EXISTS(SELECT 1 FROM recruitment_stage_process_document_requirements document WHERE document.PipelineStageId=required.Id AND document.IsRequired=TRUE))
 AND NOT EXISTS(SELECT 1 FROM recruitment_position_stage_instances completed
 JOIN recruitment_pipeline_stages previous ON previous.Id=completed.PipelineStageId AND previous.StageCode=required.StageCode AND previous.CardScope='Position'
 WHERE completed.PositionPipelineInstanceId=@HiringCaseId AND completed.Status='Completed')
ORDER BY required.DisplayOrder", stage, transaction)).ToList();
        return missing.Count == 0 ? await ValidateBudgetAsync(db, applicationId, offerId, submittingForApproval, transaction)
            : $"Complete the required hiring steps before this offer can proceed: {string.Join(", ", missing)}.";
    }

    // Recheck current money limits even for legacy/public links without an active
    // candidate Offer stage. This is read-only: issued historical PDFs are untouched.
    private static async Task<string> ValidateBudgetAsync(MySqlConnection db, long applicationId, long? offerId,
        bool submittingForApproval, IDbTransaction? transaction)
    {
        if (!offerId.HasValue) return "";
        var budget = await db.QueryFirstOrDefaultAsync<BudgetCheck>(@"SELECT p.BudgetAvailable,p.BudgetAmount,p.ApprovedPositions,p.SalaryMax,
COALESCE(config.BudgetBasis,'SalaryRangeMaximum') Basis,COALESCE(config.MaximumVariancePercent,0) AllowedVariance,
o.OfferedCtc,o.Currency,p.Currency PositionCurrency,
COALESCE((SELECT SUM(other.OfferedCtc) FROM recruitment_offers other JOIN recruitment_candidate_applications a ON a.Id=other.ApplicationId
 WHERE a.PositionId=p.Id AND other.Id<>o.Id AND other.Status NOT IN ('Rejected','Expired','Withdrawn')),0) OtherExposure,
EXISTS(SELECT 1 FROM workflowinstances w WHERE w.Id=o.WorkflowInstanceId AND w.Status='Approved'
 AND w.ResourceType='RecruitmentOffer' AND w.ResourceId=CAST(o.Id AS CHAR) AND o.ApprovalPolicy='BudgetVariance'
 AND JSON_VALID(w.PayloadJson) AND CAST(JSON_UNQUOTE(JSON_EXTRACT(w.PayloadJson,'$.OfferedCtc')) AS DECIMAL(18,2))=o.OfferedCtc
 AND JSON_UNQUOTE(JSON_EXTRACT(w.PayloadJson,'$.Currency'))=o.Currency
 AND LEFT(JSON_UNQUOTE(JSON_EXTRACT(w.PayloadJson,'$.ProposedJoiningDate')),10)=DATE_FORMAT(o.ProposedJoiningDate,'%Y-%m-%d')) VarianceApproved,
config.RequireApprovalWhenVarianceExceeded CanRequestVariance
FROM recruitment_offers o JOIN recruitment_candidate_applications a ON a.Id=o.ApplicationId
JOIN recruitment_open_positions p ON p.Id=a.PositionId
LEFT JOIN recruitment_stage_offer_configurations config ON config.Id=o.StageOfferConfigurationId
WHERE o.Id=@OfferId AND a.Id=@ApplicationId", new { OfferId = offerId, ApplicationId = applicationId }, transaction);
        if (budget is null) return "The offer linked to this application is unavailable.";
        if (!budget.Currency.Equals(budget.PositionCurrency, StringComparison.OrdinalIgnoreCase))
            return "Offer currency must match the approved position currency.";
        var ceiling = RecruitmentTalentRepository.EffectiveOfferBudgetCeiling(budget.Basis, budget.BudgetAvailable,
            budget.BudgetAmount, budget.ApprovedPositions, budget.SalaryMax);
        // Legacy clients without any configured salary/budget retain their existing flow.
        if (ceiling <= 0) return "";
        var exposure = budget.OfferedCtc + (budget.Basis == "ApprovedTotal" ? budget.OtherExposure : 0);
        return exposure <= ceiling * (1 + budget.AllowedVariance / 100m) || budget.VarianceApproved
            || (submittingForApproval && budget.CanRequestVariance) ? ""
            : "The offer exceeds the current approved budget. Reduce the CTC or obtain a configured budget-variance approval before release or acceptance.";
    }

    private sealed class BudgetCheck
    {
        public bool BudgetAvailable { get; set; }
        public decimal BudgetAmount { get; set; }
        public int ApprovedPositions { get; set; }
        public decimal SalaryMax { get; set; }
        public string Basis { get; set; } = "";
        public decimal AllowedVariance { get; set; }
        public decimal OfferedCtc { get; set; }
        public decimal OtherExposure { get; set; }
        public string Currency { get; set; } = "";
        public string PositionCurrency { get; set; } = "";
        public bool VarianceApproved { get; set; }
        public bool CanRequestVariance { get; set; }
    }

    internal static string StageError(string code, string type, string name, bool submittingForApproval)
    {
        if (type.Equals("Offer", StringComparison.OrdinalIgnoreCase)) return "";
        if (submittingForApproval && (code.Equals("NEGOTIATION_AND_MOM_TO_HR", StringComparison.OrdinalIgnoreCase)
            || code.Equals("HR_DIVISION_APPROVAL", StringComparison.OrdinalIgnoreCase))) return "";
        return $"Complete the hiring-position prerequisites before {(submittingForApproval ? "submitting the offer for approval" : "issuing or accepting the offer")}. Current position stage: {name}. MoM, negotiation and required approvals cannot be skipped.";
    }
}
