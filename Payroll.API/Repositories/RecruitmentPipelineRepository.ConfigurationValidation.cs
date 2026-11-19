using Dapper;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public partial class RecruitmentPipelineRepository
{
    private static async Task<string> ValidateCrudReferencesAsync(MySqlConnection db, MySqlTransaction tx, int clientId, SaveRecruitmentPipelineVersion request)
    {
        var competencyIds = request.Stages.SelectMany(x => x.InterviewConfiguration?.Competencies ?? []).Select(x => x.CompetencyId).Distinct().ToArray();
        if (competencyIds.Length > 0)
        {
            var validCompetencies = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM recruitment_interview_competency_definitions
WHERE Id IN @Ids AND ClientId=@ClientId AND IsActive=TRUE", new { Ids = competencyIds, clientId }, tx);
            if (validCompetencies != competencyIds.Length) return "One or more interview competencies are inactive or belong to another client.";
        }
        var panelUserIds = request.Stages.SelectMany(stage => stage.DefaultPanelMembers).Select(panel => panel.PanelUserId).Where(id => id > 0).Distinct().ToArray();
        if (panelUserIds.Length > 0)
        {
            var validPanelUsers = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM authusers
WHERE Id IN @Ids AND IsActive=TRUE AND (ClientId IS NULL OR ClientId=@ClientId)", new { Ids = panelUserIds, clientId }, tx);
            if (validPanelUsers != panelUserIds.Length) return "One or more default panel members are inactive or outside this client.";
        }
        var recipientUserIds = request.Stages.SelectMany(stage => stage.Actions).SelectMany(action => action.Recipients)
            .Where(recipient => recipient.RecipientType.Equals("SpecificUser", StringComparison.OrdinalIgnoreCase) && recipient.UserId is > 0)
            .Select(recipient => recipient.UserId!.Value).Distinct().ToArray();
        if (recipientUserIds.Length > 0)
        {
            var validRecipientUsers = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM authusers
WHERE Id IN @Ids AND IsActive=TRUE AND (ClientId IS NULL OR ClientId=@ClientId)", new { Ids = recipientUserIds, clientId }, tx);
            if (validRecipientUsers != recipientUserIds.Length) return "One or more notification recipients are inactive or outside this client.";
        }
        var attachmentConfigurationIds = request.Stages.SelectMany(x => x.AttachmentRequirements).Select(x => x.AttachmentFieldConfigurationId).Distinct().ToArray();
        if (attachmentConfigurationIds.Length > 0)
        {
            var validAttachments = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM attachment_field_configurations
WHERE id IN @Ids AND client_id IN (0,@ClientId) AND is_active=TRUE", new { Ids = attachmentConfigurationIds, ClientId = clientId }, tx);
            if (validAttachments != attachmentConfigurationIds.Length) return "One or more stage attachment configurations are inactive or belong to another client.";
        }
        var scoringProfileIds = request.Stages.Select(x => x.AtsConfiguration?.ScoringProfileId).Where(x => x is > 0).Select(x => x!.Value).Distinct().ToArray();
        if (scoringProfileIds.Length > 0)
        {
            var validProfiles = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM recruitment_ats_scoring_profiles
WHERE Id IN @Ids AND ClientId IN (0,@ClientId) AND IsActive=TRUE", new { Ids = scoringProfileIds, ClientId = clientId }, tx);
            if (validProfiles != scoringProfileIds.Length) return "One or more ATS scoring profiles are inactive or belong to another client.";
        }
        var formVersionIds = request.Stages.Select(x => x.ExternalFormConfiguration?.FormVersionId).Where(x => x is > 0).Select(x => x!.Value).Distinct().ToArray();
        if (formVersionIds.Length > 0)
        {
            var validForms = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM form_versions v
JOIN form_definitions d ON d.Id=v.FormDefinitionId
WHERE v.Id IN @Ids AND v.Status IN ('Published','Retired') AND d.ClientId IN (0,@ClientId)", new { Ids = formVersionIds, ClientId = clientId }, tx);
            if (validForms != formVersionIds.Length) return "One or more external-form versions are unavailable or belong to another client.";
        }
        foreach (var stage in request.Stages.Where(x => x.StageType.Equals("Documents", StringComparison.OrdinalIgnoreCase)
            || x.StageType.Equals("PreOnboarding", StringComparison.OrdinalIgnoreCase)))
        {
            if (stage.ExternalFormConfiguration is null || stage.ExternalFormConfiguration.FormVersionId <= 0)
                return $"Select a published candidate form for {stage.StageName}.";
            var requiredConfigurationIds = stage.AttachmentRequirements.Select(x => x.AttachmentFieldConfigurationId).Distinct().ToArray();
            if (requiredConfigurationIds.Length == 0) continue;
            var mappedConfigurationCount = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(DISTINCT f.AttachmentFieldConfigurationId)
FROM form_fields f
JOIN form_field_types t ON t.Id=f.FieldTypeId
WHERE f.FormVersionId=@FormVersionId AND f.IsActive=TRUE AND t.TypeCode='UPLOAD'
  AND f.AttachmentFieldConfigurationId IN @ConfigurationIds", new
            {
                stage.ExternalFormConfiguration.FormVersionId,
                ConfigurationIds = requiredConfigurationIds
            }, tx);
            if (mappedConfigurationCount != requiredConfigurationIds.Length)
                return $"The published form selected for {stage.StageName} does not contain every requested global document field.";
        }
        var templateIds = request.Stages.SelectMany(x => x.Actions.Select(a => a.TemplateId)
            .Append(x.OfferConfiguration?.OfferTemplateId)
            .Concat(x.ProcessDocumentRequirements.Select(document => document.TemplateId)))
            .Where(x => x is > 0).Select(x => x!.Value).Distinct().ToArray();
        if (templateIds.Length > 0)
        {
            var validTemplates = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM recruitment_templates
WHERE Id IN @Ids AND ClientId IN (0,@ClientId) AND IsActive=TRUE", new { Ids = templateIds, ClientId = clientId }, tx);
            if (validTemplates != templateIds.Length) return "One or more stage templates are inactive or belong to another client.";
        }
        var workflowValidation = await ValidateWorkflowBindingsAsync(db, request.Stages, request.Transitions, clientId, tx);
        if (workflowValidation.Length > 0) return workflowValidation;

        return "";
    }
}
