using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class RecruitmentDocumentRepository
{
    // This endpoint provisions only a server-owned default. It accepts no body,
    // custom layout, global scope or template id; custom authoring keeps its
    // existing recruitment.configuration.manage authorization.
    public async Task<object[]> EnsureDefaultTemplateAsync(int clientId, AuthUser user)
    {
        Access(user, clientId);
        if (clientId <= 0) throw new InvalidOperationException("Select an active client before opening employee-list offers.");
        await using var db = Db(); await db.OpenAsync();
        await using var transaction = await db.BeginTransactionAsync();
        var client = await db.QuerySingleOrDefaultAsync<Client>("SELECT Id,Name,IsActive FROM clients WHERE Id=@ClientId FOR UPDATE", new { ClientId = clientId }, transaction);
        if (client is null || !client.IsActive) throw new InvalidOperationException("Select an active client before opening employee-list offers.");

        // Serializing on the selected client makes concurrent first opens
        // idempotent without a new table, a global template or a schema change.
        var templates = (await db.QueryAsync<RecruitmentTemplate>("SELECT * FROM recruitment_templates WHERE ClientId IN (0,@ClientId) AND IsActive=TRUE AND TemplateType='Offer Letter' ORDER BY TemplateName", new { ClientId = clientId }, transaction)).ToArray();
        var descriptions = DescribeDocumentTemplates(templates);
        if (descriptions.Length > 0)
        {
            await transaction.CommitAsync();
            return descriptions;
        }

        // An inactive or repurposed template may own the reserved code. Never
        // overwrite it or reactivate it as a side effect of opening this flow.
        var codes = (await db.QueryAsync<string>("SELECT TemplateCode FROM recruitment_templates WHERE ClientId=@ClientId", new { ClientId = clientId }, transaction)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var code = AvailableDefaultTemplateCode(codes);
        var template = new RecruitmentTemplate
        {
            ClientId = clientId, TemplateType = "Offer Letter", TemplateCode = code,
            TemplateName = DefaultRecruitmentOfferTemplate.Name, SubjectTemplate = "Offer letter - {{candidateName}}",
            BodyTemplate = DefaultRecruitmentOfferTemplate.Body(), IsHtml = false, IsActive = true
        };
        template.Id = await db.ExecuteScalarAsync<int>("""
INSERT INTO recruitment_templates(ClientId,TemplateType,TemplateCode,TemplateName,SubjectTemplate,BodyTemplate,IsHtml,IsActive)
VALUES(@ClientId,@TemplateType,@TemplateCode,@TemplateName,@SubjectTemplate,@BodyTemplate,@IsHtml,@IsActive);
SELECT LAST_INSERT_ID();
""", template, transaction);
        await db.ExecuteAsync("INSERT INTO recruitment_admin_audit(EntityType,EntityId,Action,NewValueJson,ChangedByUserId) VALUES('RecruitmentTemplate',@Id,'Default provisioned',@Json,@UserId)",
            new { template.Id, Json = JsonSerializer.Serialize(new { template.ClientId, template.TemplateCode, template.TemplateName, Source = "System default employee-list offer" }, Json), UserId = user.Id }, transaction);
        await transaction.CommitAsync();
        return DescribeDocumentTemplates([template]);
    }

    internal static string AvailableDefaultTemplateCode(IReadOnlySet<string> codes)
    {
        var code = DefaultRecruitmentOfferTemplate.Code;
        for (var suffix = 2; codes.Contains(code); suffix++) code = DefaultRecruitmentOfferTemplate.Code + "_" + suffix;
        return code;
    }

    private static object[] DescribeDocumentTemplates(IEnumerable<RecruitmentTemplate> rows) => rows.Where(t => LayoutDocumentPdfService.IsLayout(t.BodyTemplate))
        .Select(t => { var layout = LayoutDocumentPdfService.Parse(t.BodyTemplate); return (object)new { t.Id, t.TemplateName, t.ClientId, Fields = layout.Inputs, layout.ImportPresets }; }).ToArray();
}
