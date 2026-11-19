using System.Text.Json;
using Dapper;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class RecruitmentFormRepository
{
    public async Task<(SimilarRecruitmentFormResult? Row, string Error)> CreateSimilarFormAsync(long sourceId, CreateSimilarRecruitmentForm request, AuthUser user)
    {
        if (user.Id <= 0 || !user.IsActive || user.MustChangePassword || !RecruitmentPermissions.CanConfigure(user)
            || !SelectedClientAccess.Allows(user, request.TargetClientId)) throw new UnauthorizedAccessException();
        var code = Code(request.FormCode ?? ""); var name = (request.FormName ?? "").Trim();
        if (sourceId <= 0 || request.SourceVersionId is <= 0 || code.Length is 0 or > 120 || name.Length is 0 or > 180)
            return (null, "Choose a source and provide a form name and code within their limits.");
        await using var db = Db(); await db.OpenAsync();
        // Draft writers take the same version lock before replacing children.
        // ReadCommitted ensures a copy that waited for that lock reads their
        // committed children consistently rather than an earlier read view.
        await using var tx = await db.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
        var source = await db.QueryFirstOrDefaultAsync<DynamicFormDefinition>("SELECT * FROM form_definitions WHERE Id=@Id FOR UPDATE", new { Id = sourceId }, tx);
        if (source is null || source.ClientId != 0 && !SelectedClientAccess.Allows(user, source.ClientId)) throw new UnauthorizedAccessException();
        if (!source.ModuleCode.Equals("RECRUITMENT", StringComparison.OrdinalIgnoreCase) || !source.EntityType.Equals("CANDIDATE", StringComparison.OrdinalIgnoreCase)
            || source.PurposeCode.StartsWith("EMPLOYEE", StringComparison.OrdinalIgnoreCase))
            return (null, "Create Similar here supports recruitment candidate forms only.");
        var targetId = await db.ExecuteScalarAsync<int?>("SELECT Id FROM clients WHERE Id=@Id AND IsActive=TRUE FOR SHARE", new { Id = request.TargetClientId }, tx);
        if (!targetId.HasValue) return (null, "The target client is unavailable.");
        var sourceVersionId = request.SourceVersionId ?? source.CurrentPublishedVersionId
            ?? await db.ExecuteScalarAsync<long?>("SELECT Id FROM form_versions WHERE FormDefinitionId=@Id ORDER BY VersionNumber DESC LIMIT 1 FOR UPDATE", new { Id = sourceId }, tx);
        if (sourceVersionId is null) return (null, "The source form has no saved configuration to copy.");
        var lockedVersionId = await db.ExecuteScalarAsync<long?>("SELECT Id FROM form_versions WHERE Id=@Id AND FormDefinitionId=@DefinitionId FOR UPDATE", new { Id = sourceVersionId, DefinitionId = sourceId }, tx);
        if (!lockedVersionId.HasValue)
            return (null, "The source version does not belong to this form.");

        var definitionId = await db.ExecuteScalarAsync<long>(@"INSERT INTO form_definitions
(ClientId,ModuleCode,FormCode,FormName,PurposeCode,EntityType,Status,RequiresEmailVerification,CurrentPublishedVersionId,CreatedByUserId)
VALUES (@TargetClientId,@ModuleCode,@Code,@Name,@PurposeCode,@EntityType,'Active',@RequiresEmailVerification,NULL,@UserId);SELECT LAST_INSERT_ID();",
            new { request.TargetClientId, source.ModuleCode, Code = code, Name = name, source.PurposeCode, source.EntityType, source.RequiresEmailVerification, UserId = user.Id }, tx);
        var versionId = await db.ExecuteScalarAsync<long>(@"INSERT INTO form_versions (FormDefinitionId,VersionNumber,Status,CreatedByUserId)
VALUES (@DefinitionId,1,'Draft',@UserId);SELECT LAST_INSERT_ID();", new { DefinitionId = definitionId, UserId = user.Id }, tx);
        var sections = (await db.QueryAsync<long>("SELECT Id FROM form_sections WHERE FormVersionId=@Id ORDER BY DisplayOrder,Id", new { Id = sourceVersionId }, tx)).ToArray();
        var fieldMap = new Dictionary<long, long>();
        var warnings = new List<string>();
        foreach (var oldSectionId in sections)
        {
            var sectionId = await db.ExecuteScalarAsync<long>(@"INSERT INTO form_sections (FormVersionId,SectionCode,SectionLabel,Description,DisplayOrder)
SELECT @VersionId,SectionCode,SectionLabel,Description,DisplayOrder FROM form_sections WHERE Id=@Id;SELECT LAST_INSERT_ID();", new { VersionId = versionId, Id = oldSectionId }, tx);
            var fields = (await db.QueryAsync<(long Id, string Label, long? AttachmentFieldConfigurationId)>("SELECT Id,Label,AttachmentFieldConfigurationId FROM form_fields WHERE SectionId=@Id AND FormVersionId=@VersionId ORDER BY DisplayOrder,Id", new { Id = oldSectionId, VersionId = sourceVersionId }, tx)).ToArray();
            foreach (var field in fields)
            {
                var attachmentId = field.AttachmentFieldConfigurationId;
                if (attachmentId.HasValue && !await db.ExecuteScalarAsync<bool>(@"SELECT EXISTS(SELECT 1 FROM attachment_field_configurations
WHERE id=@Id AND is_active=TRUE AND client_id IN (0,@TargetClientId))", new { Id = attachmentId, request.TargetClientId }, tx))
                {
                    attachmentId = null;
                    warnings.Add($"'{field.Label}' needs an attachment configuration for the target client before publishing. Its required flag and resume/document mapping were retained.");
                }
                var fieldId = await db.ExecuteScalarAsync<long>(@"INSERT INTO form_fields
(FormVersionId,SectionId,FieldTypeId,StableFieldCode,Label,Placeholder,HelpText,IsRequired,DisplayOrder,WidthColumns,MinimumLength,MaximumLength,MinimumNumber,MaximumNumber,MinimumDate,MaximumDate,AttachmentFieldConfigurationId,LookupSourceId,IsActive)
SELECT @VersionId,@SectionId,FieldTypeId,StableFieldCode,Label,Placeholder,HelpText,IsRequired,DisplayOrder,WidthColumns,MinimumLength,MaximumLength,MinimumNumber,MaximumNumber,MinimumDate,MaximumDate,@AttachmentId,LookupSourceId,IsActive
FROM form_fields WHERE Id=@Id;SELECT LAST_INSERT_ID();", new { VersionId = versionId, SectionId = sectionId, AttachmentId = attachmentId, field.Id }, tx);
                fieldMap.Add(field.Id, fieldId);
                await db.ExecuteAsync(@"INSERT INTO form_field_options (FieldId,OptionCode,OptionLabel,DisplayOrder,IsActive)
SELECT @FieldId,OptionCode,OptionLabel,DisplayOrder,IsActive FROM form_field_options WHERE FieldId=@Id;
INSERT INTO form_field_semantic_mappings (FieldId,SemanticAttributeId)
SELECT @FieldId,SemanticAttributeId FROM form_field_semantic_mappings WHERE FieldId=@Id;", new { FieldId = fieldId, field.Id }, tx);
            }
        }
        foreach (var pair in fieldMap)
        {
            var rules = await db.QueryAsync<DynamicFormValidationRule>("SELECT * FROM form_field_validation_rules WHERE FieldId=@Id ORDER BY DisplayOrder,Id", new { Id = pair.Key }, tx);
            foreach (var rule in rules)
            {
                long? compareId = null;
                if (rule.CompareFieldId.HasValue)
                {
                    if (!fieldMap.TryGetValue(rule.CompareFieldId.Value, out var mapped)) throw new InvalidOperationException("A source form rule references a field outside this form. Correct the source rule before copying.");
                    compareId = mapped;
                }
                await db.ExecuteAsync(@"INSERT INTO form_field_validation_rules
(FieldId,RuleType,ComparisonOperator,CompareFieldId,TextValue,IntegerValue,DecimalValue,DateValue,BooleanValue,ErrorMessage,DisplayOrder)
VALUES (@FieldId,@RuleType,@ComparisonOperator,@CompareFieldId,@TextValue,@IntegerValue,@DecimalValue,@DateValue,@BooleanValue,@ErrorMessage,@DisplayOrder)",
                    new { FieldId = pair.Value, rule.RuleType, rule.ComparisonOperator, CompareFieldId = compareId, rule.TextValue, rule.IntegerValue, rule.DecimalValue, rule.DateValue, rule.BooleanValue, rule.ErrorMessage, rule.DisplayOrder }, tx);
            }
        }
        await db.ExecuteAsync(@"INSERT INTO recruitment_admin_audit (EntityType,EntityId,Action,NewValueJson,ChangedByUserId)
VALUES ('RecruitmentForm',@Id,'CreateSimilar',@Json,@UserId)", new { Id = definitionId, Json = JsonSerializer.Serialize(new { SourceFormId = sourceId, SourceVersionId = sourceVersionId, request.TargetClientId, FormCode = code, FormName = name, Warnings = warnings }), UserId = user.Id }, tx);
        await tx.CommitAsync();
        var saved = await GetAsync(definitionId, user) ?? throw new InvalidOperationException("The copied form could not be loaded.");
        return (new SimilarRecruitmentFormResult { Definition = saved, Version = saved.Versions.Single(row => row.Id == versionId), Warnings = warnings }, "");
    }
}
