using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public partial class RecruitmentPipelineRepository
{
    private static async Task ApplyBoardStageAccessAsync(MySqlConnection db, RecruitmentPipelineBoard board, AuthUser user)
    {
        var policies = await RecruitmentPipelineStageAccess.ReadAsync(db, board.ClientId, board.PipelineVersionId);
        foreach (var lane in board.Lanes) lane.AccessPolicy = policies.GetValueOrDefault(lane.StageCode);
        board.Lanes = board.Lanes.Where(l => RecruitmentPipelineStageAccess.Allows(user, board.ClientId, l.AccessPolicy)).ToList();
    }

    private static async Task ApplyWorkspaceStageAccessAsync(MySqlConnection db, RecruitmentPipelineWorkspace workspace, AuthUser user)
    {
        workspace.UnassignedDemandCards = workspace.UnassignedDemandCards.Where(card => SelectedClientAccess.Allows(user, card.ClientId)
            && (!user.ClientId.HasValue || user.ClientId == card.ClientId)).ToList();
        var versions = workspace.Lanes.Select(l => l.PipelineVersionId).Distinct().ToArray();
        if (versions.Length == 0) return;
        var clients = (await db.QueryAsync<VersionClient>(@"SELECT v.Id,d.ClientId FROM recruitment_pipeline_versions v
JOIN recruitment_pipeline_definitions d ON d.Id=v.PipelineDefinitionId WHERE v.Id IN @Ids", new { Ids = versions })).ToDictionary(v => v.Id, v => v.ClientId);
        foreach (var version in versions)
        {
            var clientId = clients[version];
            var policies = await RecruitmentPipelineStageAccess.ReadAsync(db, clientId, version);
            if (!RecruitmentPipelineStageAccess.IsAdministrator(user) && policies.Values.Any(p => p.Restricted)) workspace.StageAccessRestricted = true;
            foreach (var lane in workspace.Lanes.Where(l => l.PipelineVersionId == version)) lane.AccessPolicy = policies.GetValueOrDefault(lane.StageCode);
        }
        workspace.Lanes = workspace.Lanes.Where(l => RecruitmentPipelineStageAccess.Allows(user, clients[l.PipelineVersionId], l.AccessPolicy)).ToList();
        if (workspace.StageAccessRestricted) workspace.UnassignedDemandCards = [];
    }
    private sealed class VersionClient { public long Id { get; set; } public int ClientId { get; set; } }
}
