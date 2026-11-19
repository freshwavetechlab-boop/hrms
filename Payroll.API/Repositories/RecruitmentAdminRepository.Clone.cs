using System.Text.Json;
using Dapper;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public partial class RecruitmentAdminRepository
{
    public async Task<IReadOnlyList<RecruitmentTemplate>> ListTemplatesAsync(AuthUser user, int? clientId)
    {
        RequireTemplateConfigurationAccess(user);
        if (clientId.HasValue && !SelectedClientAccess.Allows(user, clientId.Value)) throw new UnauthorizedAccessException();
        await using var db = Connection(); await db.OpenAsync();
        var scope = clientId ?? (SelectedClientAccess.IsSelected(user) ? null : user.ClientId);
        var rows = await db.QueryAsync<RecruitmentTemplate>(@"SELECT t.*,COALESCE(c.Name,'') ClientName
FROM recruitment_templates t LEFT JOIN clients c ON c.Id=t.ClientId
WHERE (@ClientId IS NULL OR t.ClientId IN (0,@ClientId)) ORDER BY c.Name,t.TemplateType,t.TemplateName", new { ClientId = scope });
        return rows.Where(row => row.ClientId == 0 || SelectedClientAccess.Allows(user, row.ClientId)).ToList();
    }

    public async Task<(RecruitmentTemplate? Row, string Error)> CreateSimilarTemplateAsync(int sourceId, CreateSimilarRecruitmentTemplate request, AuthUser user)
    {
        RequireTemplateConfigurationAccess(user);
        if (!SelectedClientAccess.Allows(user, request.TargetClientId)) throw new UnauthorizedAccessException();
        var name = (request.TemplateName ?? "").Trim(); var code = (request.TemplateCode ?? "").Trim();
        if (sourceId <= 0 || name.Length is 0 or > 180 || code.Length is 0 or > 80) return (null, "Choose a source and provide a template name and code within their limits.");
        await using var db = Connection(); await db.OpenAsync();
        await using var tx = await db.BeginTransactionAsync();
        var source = await db.QueryFirstOrDefaultAsync<RecruitmentTemplate>("SELECT * FROM recruitment_templates WHERE Id=@Id FOR UPDATE", new { Id = sourceId }, tx);
        if (source is null || source.ClientId != 0 && !SelectedClientAccess.Allows(user, source.ClientId)) throw new UnauthorizedAccessException();
        if (!await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM clients WHERE Id=@Id AND IsActive=TRUE)", new { Id = request.TargetClientId }, tx)) return (null, "The target client is unavailable.");
        var id = await db.ExecuteScalarAsync<int>(@"INSERT INTO recruitment_templates
(ClientId,TemplateType,TemplateCode,TemplateName,SubjectTemplate,BodyTemplate,IsHtml,IsActive)
VALUES (@TargetClientId,@TemplateType,@Code,@Name,@SubjectTemplate,@BodyTemplate,@IsHtml,@IsActive);SELECT LAST_INSERT_ID();",
            new { request.TargetClientId, source.TemplateType, Code = code, Name = name, source.SubjectTemplate, source.BodyTemplate, source.IsHtml, source.IsActive }, tx);
        await db.ExecuteAsync(@"INSERT INTO recruitment_admin_audit (EntityType,EntityId,Action,NewValueJson,ChangedByUserId)
VALUES ('RecruitmentTemplate',@Id,'CreateSimilar',@Json,@UserId)", new { Id = id, Json = JsonSerializer.Serialize(new { SourceTemplateId = sourceId, request.TargetClientId, TemplateCode = code, TemplateName = name }), UserId = user.Id }, tx);
        var saved = await db.QuerySingleAsync<RecruitmentTemplate>("SELECT t.*,c.Name ClientName FROM recruitment_templates t JOIN clients c ON c.Id=t.ClientId WHERE t.Id=@Id", new { Id = id }, tx);
        await tx.CommitAsync();
        return (saved, "");
    }

    private static void RequireTemplateConfigurationAccess(AuthUser user)
    {
        if (user.Id <= 0 || !user.IsActive || user.MustChangePassword || !RecruitmentPermissions.CanConfigure(user)) throw new UnauthorizedAccessException();
    }
}
