using Dapper;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed record RecruitmentClientConfigurationSummary(int ClientId, bool HasConfiguration);

public partial class RecruitmentPipelineRepository
{
    // Only configuration presence is exposed here. Client permissions still
    // govern every record, workspace and action behind the selector.
    public async Task<IReadOnlyList<RecruitmentClientConfigurationSummary>> ClientConfigurationSummaryAsync(AuthUser user)
    {
        if (user.Id <= 0 || !user.IsActive || user.MustChangePassword || !(RecruitmentPermissions.IsAdmin(user)
            || user.Permissions.Any(p => p.StartsWith("recruitment.", StringComparison.OrdinalIgnoreCase)))) throw new UnauthorizedAccessException();
        await using var db = Db(); await db.OpenAsync();
        var clientId = SelectedClientAccess.IsSelected(user) ? null : user.ClientId;
        var ids = (await db.QueryAsync<int>("SELECT Id FROM clients WHERE (@ClientId IS NULL OR Id=@ClientId)", new { ClientId = clientId }))
            .Where(id => SelectedClientAccess.Allows(user, id)).ToArray();
        if (ids.Length == 0) return [];
        string[] sources = ["recruitment_pipeline_definitions", "recruitment_requisitions", "recruitment_work_orders", "recruitment_open_positions",
            "recruitment_job_postings", "recruitment_document_roster", "recruitment_offers", "recruitment_templates", "form_definitions"];
        var available = (await db.QueryAsync<string>("SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME IN @Names", new { Names = sources })).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var queries = sources.Where(available.Contains).Select(table => $"SELECT ClientId FROM `{table}` WHERE ClientId IN @Ids"
            + (table == "form_definitions" ? " AND ModuleCode='RECRUITMENT'" : "")).ToArray();
        var configured = queries.Length == 0 ? [] : (await db.QueryAsync<int>(string.Join(" UNION ", queries), new { Ids = ids })).ToHashSet();
        return ids.Select(id => new RecruitmentClientConfigurationSummary(id, configured.Contains(id))).ToArray();
    }
}
