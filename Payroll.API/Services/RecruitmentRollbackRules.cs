using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Services;

internal static class RecruitmentRollbackRules
{
    internal static string Key(long versionId) => $"recruitment_rollback:{versionId}";

    internal const string CohortSql = @"SELECT a.Id ApplicationId,
COALESCE(stage.StageName,a.CurrentStage,'') CandidateStageName,COALESCE(stage.StageType,'') CandidateStageType,
COALESCE(CASE WHEN visit.Id>interview.PipelineStageInstanceId THEN ''
 WHEN interview.Status='No Show' THEN 'No Show' ELSE interview.Result END,'') LatestInterviewResult,
COALESCE(CASE WHEN visit.Id>offerRow.PipelineStageInstanceId THEN '' ELSE offerRow.Status END,'') LatestOfferStatus,
(a.JoinedEmployeeId IS NOT NULL OR LOWER(a.CurrentStage) IN ('joined','joined / hired')) IsJoined
FROM recruitment_candidate_applications a
LEFT JOIN recruitment_application_pipeline_instances flow ON flow.ApplicationId=a.Id
LEFT JOIN recruitment_application_stage_instances visit ON visit.Id=flow.CurrentStageInstanceId
LEFT JOIN recruitment_pipeline_stages stage ON stage.Id=visit.PipelineStageId
LEFT JOIN recruitment_interviews interview ON interview.Id=(SELECT MAX(i.Id) FROM recruitment_interviews i WHERE i.ApplicationId=a.Id)
LEFT JOIN recruitment_offers offerRow ON offerRow.Id=(SELECT o.Id FROM recruitment_offers o WHERE o.ApplicationId=a.Id ORDER BY o.UpdatedAt DESC,o.Id DESC LIMIT 1)
WHERE a.PositionId=@PositionId AND a.ClientId=@ClientId AND a.ApplicationType='Application' ORDER BY a.Id";

    internal static async Task<RecruitmentAllRejectedRollback> ReadAsync(MySqlConnection db, int clientId, long versionId, MySqlTransaction? tx = null)
    {
        var json = await db.ExecuteScalarAsync<string?>(
            "SELECT SettingsJson FROM modulesettings WHERE client_id=@ClientId AND ModuleCode=@Key AND IsEnabled=TRUE",
            new { ClientId = clientId, Key = Key(versionId) }, tx);
        if (json is null) return new();
        // Invalid saved configuration must never cause an automatic stage move.
        try { return JsonSerializer.Deserialize<RecruitmentAllRejectedRollback>(json) ?? new(); }
        catch (JsonException) { return new(); }
    }

    internal static string Validate(RecruitmentAllRejectedRollback rule, IEnumerable<RecruitmentPipelineStage> stages)
    {
        if (!rule.Enabled) return "";
        var positions = stages.Where(stage => stage.IsActive && stage.CardScope == "Position").ToList();
        var target = positions.FirstOrDefault(stage => stage.StageCode.Equals(rule.TargetStageCode, StringComparison.OrdinalIgnoreCase));
        if (target is null || target.IsTerminal || target.StageType is "Rejected" or "Withdrawn")
            return "Choose an active, non-terminal hiring stage as the rollback destination.";
        if (!positions.Any(stage => stage.DisplayOrder > target.DisplayOrder))
            return "The rollback destination must precede another hiring stage.";
        return "";
    }

    internal static bool AllRejected(IReadOnlyList<RecruitmentHiringProgress.Candidate> candidates) =>
        candidates.Count > 0 && candidates.All(RecruitmentHiringProgress.Excluded);
}
