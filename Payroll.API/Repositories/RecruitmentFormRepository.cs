using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;
using System.Text.RegularExpressions;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public sealed class RecruitmentFormRepository(IConfiguration configuration, ILogger<RecruitmentFormRepository> logger)
{
    private MySqlConnection Db() => new(configuration.GetConnectionString("Default"));

    public async Task InitializeAsync()
    {
        await using var db = Db();
        await db.OpenAsync();
        await db.ExecuteAsync(@"
CREATE TABLE IF NOT EXISTS form_definitions (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    ClientId INT NOT NULL,
    ModuleCode VARCHAR(80) NOT NULL,
    FormCode VARCHAR(120) NOT NULL,
    FormName VARCHAR(180) NOT NULL,
    PurposeCode VARCHAR(100) NOT NULL,
    EntityType VARCHAR(80) NOT NULL,
    Status VARCHAR(30) NOT NULL DEFAULT 'Active',
    CurrentPublishedVersionId BIGINT NULL,
    CreatedByUserId INT NOT NULL,
    CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
    UNIQUE KEY UX_form_definition_code (ClientId,ModuleCode,FormCode),
    INDEX IX_form_definition_scope (ClientId,ModuleCode,PurposeCode,Status)
);
CREATE TABLE IF NOT EXISTS form_versions (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    FormDefinitionId BIGINT NOT NULL,
    VersionNumber INT NOT NULL,
    Status VARCHAR(30) NOT NULL DEFAULT 'Draft',
    CreatedByUserId INT NOT NULL,
    PublishedByUserId INT NULL,
    PublishedAtUtc DATETIME(6) NULL,
    CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    UNIQUE KEY UX_form_version_number (FormDefinitionId,VersionNumber),
    INDEX IX_form_version_status (FormDefinitionId,Status)
);
CREATE TABLE IF NOT EXISTS form_sections (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    FormVersionId BIGINT NOT NULL,
    SectionCode VARCHAR(100) NOT NULL,
    SectionLabel VARCHAR(180) NOT NULL,
    Description VARCHAR(500) NOT NULL DEFAULT '',
    DisplayOrder INT NOT NULL DEFAULT 100,
    UNIQUE KEY UX_form_section_code (FormVersionId,SectionCode),
    INDEX IX_form_section_order (FormVersionId,DisplayOrder)
);
CREATE TABLE IF NOT EXISTS form_field_types (
    Id INT PRIMARY KEY AUTO_INCREMENT,
    TypeCode VARCHAR(40) NOT NULL,
    TypeName VARCHAR(100) NOT NULL,
    SupportsOptions BOOLEAN NOT NULL DEFAULT FALSE,
    SupportsMultipleValues BOOLEAN NOT NULL DEFAULT FALSE,
    SupportsAttachment BOOLEAN NOT NULL DEFAULT FALSE,
    IsActive BOOLEAN NOT NULL DEFAULT TRUE,
    UNIQUE KEY UX_form_field_type_code (TypeCode)
);
CREATE TABLE IF NOT EXISTS form_fields (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    FormVersionId BIGINT NOT NULL,
    SectionId BIGINT NOT NULL,
    FieldTypeId INT NOT NULL,
    StableFieldCode VARCHAR(120) NOT NULL,
    Label VARCHAR(180) NOT NULL,
    Placeholder VARCHAR(250) NOT NULL DEFAULT '',
    HelpText VARCHAR(500) NOT NULL DEFAULT '',
    IsRequired BOOLEAN NOT NULL DEFAULT FALSE,
    DisplayOrder INT NOT NULL DEFAULT 100,
    WidthColumns INT NOT NULL DEFAULT 12,
    MinimumLength INT NULL,
    MaximumLength INT NULL,
    MinimumNumber DECIMAL(20,4) NULL,
    MaximumNumber DECIMAL(20,4) NULL,
    MinimumDate DATE NULL,
    MaximumDate DATE NULL,
    AttachmentFieldConfigurationId BIGINT NULL,
    LookupSourceId BIGINT NULL,
    IsActive BOOLEAN NOT NULL DEFAULT TRUE,
    UNIQUE KEY UX_form_field_code (FormVersionId,StableFieldCode),
    INDEX IX_form_field_order (SectionId,DisplayOrder),
    INDEX IX_form_field_attachment (AttachmentFieldConfigurationId)
);
CREATE TABLE IF NOT EXISTS form_field_options (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    FieldId BIGINT NOT NULL,
    OptionCode VARCHAR(120) NOT NULL,
    OptionLabel VARCHAR(180) NOT NULL,
    DisplayOrder INT NOT NULL DEFAULT 100,
    IsActive BOOLEAN NOT NULL DEFAULT TRUE,
    UNIQUE KEY UX_form_field_option_code (FieldId,OptionCode),
    INDEX IX_form_field_option_order (FieldId,DisplayOrder)
);
CREATE TABLE IF NOT EXISTS form_lookup_sources (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    SourceCode VARCHAR(100) NOT NULL,
    SourceName VARCHAR(180) NOT NULL,
    ResolverCode VARCHAR(100) NOT NULL,
    IsClientScoped BOOLEAN NOT NULL DEFAULT TRUE,
    MinimumSearchLength INT NOT NULL DEFAULT 0,
    MaximumResults INT NOT NULL DEFAULT 50,
    IsActive BOOLEAN NOT NULL DEFAULT TRUE,
    UNIQUE KEY UX_form_lookup_source_code (SourceCode)
);
CREATE TABLE IF NOT EXISTS form_semantic_attributes (
    Id INT PRIMARY KEY AUTO_INCREMENT,
    SemanticCode VARCHAR(100) NOT NULL,
    SemanticName VARCHAR(180) NOT NULL,
    DataTypeCode VARCHAR(40) NOT NULL,
    IsActive BOOLEAN NOT NULL DEFAULT TRUE,
    UNIQUE KEY UX_form_semantic_code (SemanticCode)
);
CREATE TABLE IF NOT EXISTS form_field_semantic_mappings (
    FieldId BIGINT NOT NULL,
    SemanticAttributeId INT NOT NULL,
    PRIMARY KEY (FieldId,SemanticAttributeId),
    INDEX IX_form_semantic_mapping_attribute (SemanticAttributeId,FieldId)
);
CREATE TABLE IF NOT EXISTS form_field_validation_rules (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    FieldId BIGINT NOT NULL,
    RuleType VARCHAR(60) NOT NULL,
    ComparisonOperator VARCHAR(30) NOT NULL DEFAULT '',
    CompareFieldId BIGINT NULL,
    TextValue VARCHAR(500) NULL,
    IntegerValue BIGINT NULL,
    DecimalValue DECIMAL(20,4) NULL,
    DateValue DATE NULL,
    BooleanValue BOOLEAN NULL,
    ErrorMessage VARCHAR(500) NOT NULL,
    DisplayOrder INT NOT NULL DEFAULT 100,
    INDEX IX_form_validation_field (FieldId,DisplayOrder)
);
CREATE TABLE IF NOT EXISTS external_portal_subjects (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    ClientId INT NOT NULL,
    CandidateId BIGINT NULL,
    Email VARCHAR(190) NOT NULL DEFAULT '',
    NormalizedEmail VARCHAR(190) NOT NULL DEFAULT '',
    Phone VARCHAR(50) NOT NULL DEFAULT '',
    NormalizedPhone VARCHAR(50) NOT NULL DEFAULT '',
    ConsentAccepted BOOLEAN NOT NULL DEFAULT FALSE,
    ConsentAcceptedAtUtc DATETIME(6) NULL,
    CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
    INDEX IX_external_subject_email (ClientId,NormalizedEmail),
    INDEX IX_external_subject_phone (ClientId,NormalizedPhone)
);
CREATE TABLE IF NOT EXISTS form_submissions (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    FormVersionId BIGINT NOT NULL,
    ClientId INT NOT NULL,
    ExternalSubjectId BIGINT NULL,
    EntityType VARCHAR(80) NOT NULL DEFAULT 'FORM_SUBMISSION',
    EntityId BIGINT NULL,
    CandidateId BIGINT NULL,
    ApplicationId BIGINT NULL,
    Status VARCHAR(30) NOT NULL DEFAULT 'Draft',
    StartedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    SubmittedAtUtc DATETIME(6) NULL,
    UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
    INDEX IX_form_submission_status (ClientId,Status,StartedAtUtc),
    INDEX IX_form_submission_candidate (CandidateId,ApplicationId)
);
CREATE TABLE IF NOT EXISTS form_public_sessions (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    TokenHash CHAR(64) NOT NULL,
    PostingId BIGINT NOT NULL,
    SubmissionId BIGINT NOT NULL,
    ExternalSubjectId BIGINT NOT NULL,
    Purpose VARCHAR(40) NOT NULL DEFAULT 'APPLICATION',
    IdempotencyHash CHAR(64) NOT NULL,
    MaximumUses INT NOT NULL DEFAULT 500,
    UseCount INT NOT NULL DEFAULT 0,
    ExpiresAtUtc DATETIME(6) NOT NULL,
    LastUsedAtUtc DATETIME(6) NULL,
    RevokedAtUtc DATETIME(6) NULL,
    IpAddress VARCHAR(80) NOT NULL DEFAULT '',
    UserAgent VARCHAR(500) NOT NULL DEFAULT '',
    CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    UNIQUE KEY UX_form_public_session_token (TokenHash),
    UNIQUE KEY UX_form_public_session_idempotency (PostingId,IdempotencyHash),
    UNIQUE KEY UX_form_public_session_submission (SubmissionId),
    INDEX IX_form_public_session_expiry (ExpiresAtUtc,RevokedAtUtc)
);
CREATE TABLE IF NOT EXISTS form_submission_values (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    SubmissionId BIGINT NOT NULL,
    FieldId BIGINT NOT NULL,
    TextValue LONGTEXT NULL,
    IntegerValue BIGINT NULL,
    DecimalValue DECIMAL(20,4) NULL,
    DateValue DATE NULL,
    DateTimeValue DATETIME(6) NULL,
    BooleanValue BOOLEAN NULL,
    CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
    UNIQUE KEY UX_form_submission_field_value (SubmissionId,FieldId),
    INDEX IX_form_submission_value_field (FieldId,SubmissionId)
);
CREATE TABLE IF NOT EXISTS form_submission_selected_options (
    SubmissionId BIGINT NOT NULL,
    FieldId BIGINT NOT NULL,
    OptionId BIGINT NOT NULL,
    PRIMARY KEY (SubmissionId,FieldId,OptionId),
    INDEX IX_form_submission_option (OptionId,SubmissionId)
);
CREATE TABLE IF NOT EXISTS form_submission_lookup_values (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    SubmissionId BIGINT NOT NULL,
    FieldId BIGINT NOT NULL,
    SelectedValue VARCHAR(250) NOT NULL,
    DisplayLabel VARCHAR(500) NOT NULL,
    DisplayOrder INT NOT NULL DEFAULT 100,
    CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    UNIQUE KEY UX_form_submission_lookup_value (SubmissionId,FieldId,SelectedValue),
    INDEX IX_form_submission_lookup_field (SubmissionId,FieldId,DisplayOrder)
);
CREATE TABLE IF NOT EXISTS form_submission_attachments (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    SubmissionId BIGINT NOT NULL,
    FieldId BIGINT NOT NULL,
    AttachmentId BIGINT NOT NULL,
    AttachmentPublicId CHAR(36) NOT NULL,
    CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    UNIQUE KEY UX_form_submission_attachment (SubmissionId,FieldId,AttachmentId),
    UNIQUE KEY UX_form_submission_attachment_public (AttachmentPublicId),
    INDEX IX_form_submission_attachment_field (SubmissionId,FieldId)
);
CREATE TABLE IF NOT EXISTS form_submission_events (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    SubmissionId BIGINT NOT NULL,
    EventType VARCHAR(60) NOT NULL,
    EventSummary VARCHAR(500) NOT NULL DEFAULT '',
    ActorUserId INT NULL,
    ExternalSubjectId BIGINT NULL,
    IpAddress VARCHAR(80) NOT NULL DEFAULT '',
    UserAgent VARCHAR(500) NOT NULL DEFAULT '',
    OccurredAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    INDEX IX_form_submission_event (SubmissionId,OccurredAtUtc)
);
");
        await EnsureIntegrationColumnsAsync(db);
        await EnsureForeignKeysAsync(db);
        await SeedCatalogAsync(db);
    }

    public async Task<IEnumerable<DynamicFormDefinition>> ListAsync(AuthUser user, int? clientId)
    {
        await using var db = Db();
        await db.OpenAsync();
        var scope = EffectiveScope(user, clientId);
        return await db.QueryAsync<DynamicFormDefinition>(@"SELECT d.Id,d.ClientId,COALESCE(c.Name,'') ClientName,d.ModuleCode,d.FormCode,d.FormName,d.PurposeCode,d.EntityType,d.Status,d.CurrentPublishedVersionId,d.CreatedByUserId,d.CreatedAtUtc,d.UpdatedAtUtc
FROM form_definitions d LEFT JOIN clients c ON c.Id=d.ClientId
WHERE (@ClientId IS NULL OR d.ClientId=@ClientId) ORDER BY d.ModuleCode,d.FormName", new { ClientId = scope });
    }

    public async Task<DynamicFormDefinition?> GetAsync(long id, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        var definition = await db.QueryFirstOrDefaultAsync<DynamicFormDefinition>(@"SELECT d.Id,d.ClientId,COALESCE(c.Name,'') ClientName,d.ModuleCode,d.FormCode,d.FormName,d.PurposeCode,d.EntityType,d.Status,d.CurrentPublishedVersionId,d.CreatedByUserId,d.CreatedAtUtc,d.UpdatedAtUtc
FROM form_definitions d LEFT JOIN clients c ON c.Id=d.ClientId WHERE d.Id=@Id AND (@ClientId IS NULL OR d.ClientId=@ClientId)", new { Id = id, user.ClientId });
        if (definition is null) return null;
        definition.Versions = (await db.QueryAsync<DynamicFormVersion>("SELECT * FROM form_versions WHERE FormDefinitionId=@Id ORDER BY VersionNumber DESC", new { Id = id })).ToList();
        foreach (var version in definition.Versions)
            await PopulateVersionAsync(db, version);
        return definition;
    }

    public async Task<DynamicFormVersion?> GetPublishedVersionAsync(long versionId)
    {
        await using var db = Db();
        await db.OpenAsync();
        var version = await db.QueryFirstOrDefaultAsync<DynamicFormVersion>("SELECT * FROM form_versions WHERE Id=@Id AND Status IN ('Published','Retired')", new { Id = versionId });
        if (version is not null)
        {
            await PopulateVersionAsync(db, version);
            SanitizePublicVersion(version);
        }
        return version;
    }

    public async Task<(DynamicFormDefinition? Item, string Error)> SaveDefinitionAsync(SaveDynamicFormDefinition request, AuthUser user)
    {
        request.ModuleCode = Code(request.ModuleCode);
        request.FormCode = Code(request.FormCode);
        request.PurposeCode = Code(request.PurposeCode);
        request.EntityType = Code(request.EntityType);
        request.FormName = request.FormName.Trim();
        if (request.ClientId <= 0 || string.IsNullOrWhiteSpace(request.FormCode) || string.IsNullOrWhiteSpace(request.FormName))
            return (null, "Client, form code and form name are required.");
        if (user.ClientId is not null && user.ClientId != request.ClientId) return (null, "Client access denied.");
        await using var db = Db();
        await db.OpenAsync();
        try
        {
            if (request.Id == 0)
            {
                request.Id = await db.ExecuteScalarAsync<long>(@"INSERT INTO form_definitions (ClientId,ModuleCode,FormCode,FormName,PurposeCode,EntityType,Status,CreatedByUserId)
VALUES (@ClientId,@ModuleCode,@FormCode,@FormName,@PurposeCode,@EntityType,@Status,@UserId);SELECT LAST_INSERT_ID();", new { request.ClientId, request.ModuleCode, request.FormCode, request.FormName, request.PurposeCode, request.EntityType, Status = ActiveStatus(request.Status), UserId = user.Id });
            }
            else
            {
                var affected = await db.ExecuteAsync(@"UPDATE form_definitions SET ModuleCode=@ModuleCode,FormCode=@FormCode,FormName=@FormName,PurposeCode=@PurposeCode,EntityType=@EntityType,Status=@Status
WHERE Id=@Id AND ClientId=@ClientId", new { request.Id, request.ClientId, request.ModuleCode, request.FormCode, request.FormName, request.PurposeCode, request.EntityType, Status = ActiveStatus(request.Status) });
                if (affected == 0) return (null, "Form definition was not found.");
            }
        }
        catch (MySqlException exception) when (exception.Number == 1062)
        {
            return (null, "A form with this client, module and code already exists.");
        }
        return (await GetAsync(request.Id, user), "");
    }

    public async Task<(DynamicFormVersion? Item, string Error)> SaveVersionAsync(SaveDynamicFormVersion request, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        var definition = await db.QueryFirstOrDefaultAsync<DynamicFormDefinition>("SELECT * FROM form_definitions WHERE Id=@Id AND (@ClientId IS NULL OR ClientId=@ClientId)", new { Id = request.FormDefinitionId, user.ClientId });
        if (definition is null) return (null, "Form definition was not found.");
        if (request.Sections.Count == 0) return (null, "Add at least one form section.");
        await using var transaction = await db.BeginTransactionAsync();
        try
        {
            long versionId = request.Id;
            if (versionId > 0)
            {
                var status = await db.ExecuteScalarAsync<string>("SELECT Status FROM form_versions WHERE Id=@Id AND FormDefinitionId=@FormDefinitionId", new { Id = versionId, request.FormDefinitionId }, transaction);
                if (status is null) return (null, "Form version was not found.");
                if (!status.Equals("Draft", StringComparison.OrdinalIgnoreCase)) versionId = 0;
            }
            if (versionId == 0)
            {
                var next = await db.ExecuteScalarAsync<int>("SELECT COALESCE(MAX(VersionNumber),0)+1 FROM form_versions WHERE FormDefinitionId=@Id", new { Id = request.FormDefinitionId }, transaction);
                versionId = await db.ExecuteScalarAsync<long>("INSERT INTO form_versions (FormDefinitionId,VersionNumber,Status,CreatedByUserId) VALUES (@FormDefinitionId,@VersionNumber,'Draft',@UserId);SELECT LAST_INSERT_ID();", new { request.FormDefinitionId, VersionNumber = next, UserId = user.Id }, transaction);
            }
            else
            {
                var ids = (await db.QueryAsync<long>("SELECT Id FROM form_fields WHERE FormVersionId=@Id", new { Id = versionId }, transaction)).ToArray();
                if (ids.Length > 0)
                {
                    await db.ExecuteAsync("DELETE FROM form_field_validation_rules WHERE FieldId IN @Ids;DELETE FROM form_field_semantic_mappings WHERE FieldId IN @Ids;DELETE FROM form_field_options WHERE FieldId IN @Ids;", new { Ids = ids }, transaction);
                }
                await db.ExecuteAsync("DELETE FROM form_fields WHERE FormVersionId=@Id;DELETE FROM form_sections WHERE FormVersionId=@Id;", new { Id = versionId }, transaction);
            }
            var fieldIdByCode = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var fieldIdByRequestId = new Dictionary<long, long>();
            var fieldInfoById = new Dictionary<long, (string Code, string Label, string TypeCode, bool IsActive)>();
            var pendingRules = new List<(long FieldId, List<DynamicFormValidationRule> Rules)>();
            foreach (var section in request.Sections.OrderBy(row => row.DisplayOrder))
            {
                var sectionCode = Code(section.SectionCode);
                if (string.IsNullOrWhiteSpace(sectionCode) || string.IsNullOrWhiteSpace(section.SectionLabel)) throw new InvalidOperationException("Every section needs a code and label.");
                var sectionId = await db.ExecuteScalarAsync<long>(@"INSERT INTO form_sections (FormVersionId,SectionCode,SectionLabel,Description,DisplayOrder)
VALUES (@VersionId,@SectionCode,@Label,@Description,@DisplayOrder);SELECT LAST_INSERT_ID();", new { VersionId = versionId, SectionCode = sectionCode, Label = section.SectionLabel.Trim(), Description = section.Description.Trim(), section.DisplayOrder }, transaction);
                foreach (var field in section.Fields.OrderBy(row => row.DisplayOrder))
                {
                    var fieldCode = Code(field.StableFieldCode);
                    var fieldType = Code(field.FieldTypeCode);
                    if (string.IsNullOrWhiteSpace(fieldCode) || string.IsNullOrWhiteSpace(field.Label)) throw new InvalidOperationException("Every field needs a stable code and label.");
                    var fieldTypeId = await db.ExecuteScalarAsync<int?>("SELECT Id FROM form_field_types WHERE TypeCode=@Code AND IsActive=TRUE", new { Code = fieldType }, transaction);
                    if (!fieldTypeId.HasValue) throw new InvalidOperationException($"Unsupported field type {fieldType}.");
                    long? lookupSourceId = null;
                    if (!string.IsNullOrWhiteSpace(field.LookupSourceCode))
                        lookupSourceId = await db.ExecuteScalarAsync<long?>("SELECT Id FROM form_lookup_sources WHERE SourceCode=@Code AND IsActive=TRUE", new { Code = Code(field.LookupSourceCode) }, transaction);
                    var fieldId = await db.ExecuteScalarAsync<long>(@"INSERT INTO form_fields (FormVersionId,SectionId,FieldTypeId,StableFieldCode,Label,Placeholder,HelpText,IsRequired,DisplayOrder,WidthColumns,MinimumLength,MaximumLength,MinimumNumber,MaximumNumber,MinimumDate,MaximumDate,AttachmentFieldConfigurationId,LookupSourceId,IsActive)
VALUES (@VersionId,@SectionId,@FieldTypeId,@StableFieldCode,@Label,@Placeholder,@HelpText,@IsRequired,@DisplayOrder,@WidthColumns,@MinimumLength,@MaximumLength,@MinimumNumber,@MaximumNumber,@MinimumDate,@MaximumDate,@AttachmentFieldConfigurationId,@LookupSourceId,@IsActive);SELECT LAST_INSERT_ID();",
                        new { VersionId = versionId, SectionId = sectionId, FieldTypeId = fieldTypeId.Value, StableFieldCode = fieldCode, Label = field.Label.Trim(), Placeholder = field.Placeholder.Trim(), HelpText = field.HelpText.Trim(), field.IsRequired, field.DisplayOrder, WidthColumns = Math.Clamp(field.WidthColumns, 1, 24), field.MinimumLength, field.MaximumLength, field.MinimumNumber, field.MaximumNumber, field.MinimumDate, field.MaximumDate, field.AttachmentFieldConfigurationId, LookupSourceId = lookupSourceId, field.IsActive }, transaction);
                    if (!fieldIdByCode.TryAdd(fieldCode, fieldId)) throw new InvalidOperationException($"Field code '{fieldCode}' is duplicated in this form version.");
                    if (field.Id != 0 && !fieldIdByRequestId.TryAdd(field.Id, fieldId)) throw new InvalidOperationException("A form field identifier is duplicated in this request.");
                    fieldInfoById[fieldId] = (fieldCode, field.Label.Trim(), fieldType, field.IsActive);
                    foreach (var option in field.Options.OrderBy(row => row.DisplayOrder))
                        await db.ExecuteAsync("INSERT INTO form_field_options (FieldId,OptionCode,OptionLabel,DisplayOrder,IsActive) VALUES (@FieldId,@Code,@Label,@DisplayOrder,@IsActive)", new { FieldId = fieldId, Code = Code(option.OptionCode), Label = option.OptionLabel.Trim(), option.DisplayOrder, option.IsActive }, transaction);
                    foreach (var semanticCode in field.SemanticCodes.Select(Code).Where(value => value.Length > 0).Distinct())
                        await db.ExecuteAsync(@"INSERT INTO form_field_semantic_mappings (FieldId,SemanticAttributeId)
SELECT @FieldId,Id FROM form_semantic_attributes WHERE SemanticCode=@Code AND IsActive=TRUE", new { FieldId = fieldId, Code = semanticCode }, transaction);
                    if ((field.ValidationRules ?? []).Any(row => row is null)) throw new InvalidOperationException($"Validation for '{field.Label.Trim()}' contains an invalid rule row.");
                    pendingRules.Add((fieldId, (field.ValidationRules ?? []).OrderBy(row => row.DisplayOrder).ToList()));
                }
            }
            foreach (var pending in pendingRules)
            {
                var fieldInfo = fieldInfoById[pending.FieldId];
                foreach (var sourceRule in pending.Rules)
                {
                    long? compareFieldId = null;
                    var compareCode = Code(sourceRule.CompareFieldCode);
                    if (compareCode.Length > 0)
                    {
                        if (!fieldIdByCode.TryGetValue(compareCode, out var mappedId))
                            throw new InvalidOperationException($"Validation for '{fieldInfo.Label}' references an unknown comparison field.");
                        if (sourceRule.CompareFieldId.HasValue)
                        {
                            long? mappedRequestId = fieldIdByRequestId.TryGetValue(sourceRule.CompareFieldId.Value, out var oldMappedId)
                                ? oldMappedId
                                : fieldInfoById.ContainsKey(sourceRule.CompareFieldId.Value) ? sourceRule.CompareFieldId.Value : null;
                            if (mappedRequestId.HasValue && mappedRequestId.Value != mappedId)
                                throw new InvalidOperationException($"Validation for '{fieldInfo.Label}' has conflicting comparison-field references.");
                        }
                        compareFieldId = mappedId;
                    }
                    else if (sourceRule.CompareFieldId.HasValue)
                    {
                        if (fieldIdByRequestId.TryGetValue(sourceRule.CompareFieldId.Value, out var mappedId)) compareFieldId = mappedId;
                        else if (fieldInfoById.ContainsKey(sourceRule.CompareFieldId.Value)) compareFieldId = sourceRule.CompareFieldId.Value;
                        else throw new InvalidOperationException($"Validation for '{fieldInfo.Label}' references an unknown comparison field.");
                    }
                    var compareInfo = compareFieldId.HasValue && fieldInfoById.TryGetValue(compareFieldId.Value, out var resolvedCompare)
                        ? resolvedCompare
                        : ((string Code, string Label, string TypeCode, bool IsActive)?)null;
                    var (rule, ruleError) = NormalizeValidationRule(sourceRule, pending.FieldId, fieldInfo, compareFieldId, compareInfo);
                    if (rule is null) throw new InvalidOperationException(ruleError);
                    await db.ExecuteAsync(@"INSERT INTO form_field_validation_rules (FieldId,RuleType,ComparisonOperator,CompareFieldId,TextValue,IntegerValue,DecimalValue,DateValue,BooleanValue,ErrorMessage,DisplayOrder)
VALUES (@FieldId,@RuleType,@ComparisonOperator,@CompareFieldId,@TextValue,@IntegerValue,@DecimalValue,@DateValue,@BooleanValue,@ErrorMessage,@DisplayOrder)", rule, transaction);
                }
            }
            await transaction.CommitAsync();
            var saved = await db.QueryFirstAsync<DynamicFormVersion>("SELECT * FROM form_versions WHERE Id=@Id", new { Id = versionId });
            await PopulateVersionAsync(db, saved);
            return (saved, "");
        }
        catch (Exception exception)
        {
            await transaction.RollbackAsync();
            return (null, exception.Message);
        }
    }

    public async Task<(DynamicFormVersion? Item, string Error)> PublishAsync(long versionId, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        var row = await db.QueryFirstOrDefaultAsync<PublishFormRow>(@"SELECT v.*,d.ClientId,d.PurposeCode FROM form_versions v JOIN form_definitions d ON d.Id=v.FormDefinitionId
WHERE v.Id=@Id AND v.Status='Draft' AND (@ClientId IS NULL OR d.ClientId=@ClientId)", new { Id = versionId, user.ClientId });
        if (row is null) return (null, "Only an accessible draft version can be published.");
        var fieldCount = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM form_fields WHERE FormVersionId=@Id AND IsActive=TRUE", new { Id = versionId });
        if (fieldCount == 0) return (null, "Add at least one active field before publishing.");
        var invalidField = await db.ExecuteScalarAsync<string?>(@"SELECT f.Label FROM form_fields f JOIN form_field_types t ON t.Id=f.FieldTypeId
WHERE f.FormVersionId=@Id AND f.IsActive=TRUE AND (
 (f.MinimumLength IS NOT NULL AND f.MaximumLength IS NOT NULL AND f.MinimumLength>f.MaximumLength)
 OR (f.MinimumNumber IS NOT NULL AND f.MaximumNumber IS NOT NULL AND f.MinimumNumber>f.MaximumNumber)
 OR (f.MinimumDate IS NOT NULL AND f.MaximumDate IS NOT NULL AND f.MinimumDate>f.MaximumDate)
 OR (t.TypeCode='UPLOAD' AND (f.AttachmentFieldConfigurationId IS NULL OR NOT EXISTS (
      SELECT 1 FROM attachment_field_configurations cfg WHERE cfg.id=f.AttachmentFieldConfigurationId AND cfg.is_active=TRUE AND cfg.client_id IN (0,@FormClientId))))
 OR (t.TypeCode<>'UPLOAD' AND f.AttachmentFieldConfigurationId IS NOT NULL)
 OR (f.LookupSourceId IS NOT NULL AND t.TypeCode NOT IN ('SEARCH_SELECT','MULTI_SELECT'))
 OR (t.TypeCode IN ('RADIO','SEARCH_SELECT','MULTI_SELECT') AND f.LookupSourceId IS NULL AND NOT EXISTS (
      SELECT 1 FROM form_field_options o WHERE o.FieldId=f.Id AND o.IsActive=TRUE))
        ) ORDER BY f.DisplayOrder LIMIT 1", new { Id = versionId, FormClientId = row.ClientId });
        if (!string.IsNullOrWhiteSpace(invalidField)) return (null, $"Review the configuration for field '{invalidField}' before publishing.");
        var invalidRule = await StoredRuleConfigurationErrorAsync(db, versionId);
        if (invalidRule.Length > 0) return (null, invalidRule);
        if (row.PurposeCode.Equals("CANDIDATE_APPLICATION", StringComparison.OrdinalIgnoreCase))
        {
            var firstNameField = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM form_fields f
JOIN form_field_semantic_mappings m ON m.FieldId=f.Id JOIN form_semantic_attributes s ON s.Id=m.SemanticAttributeId
WHERE f.FormVersionId=@Id AND f.IsActive=TRUE AND f.IsRequired=TRUE AND s.SemanticCode='FIRST_NAME'", new { Id = versionId });
            if (firstNameField == 0) return (null, "A required field mapped to FIRST_NAME is needed for a candidate application form.");
        }
        await using var transaction = await db.BeginTransactionAsync();
        await db.ExecuteAsync("UPDATE form_versions SET Status='Retired' WHERE FormDefinitionId=@DefinitionId AND Status='Published';UPDATE form_versions SET Status='Published',PublishedByUserId=@UserId,PublishedAtUtc=UTC_TIMESTAMP(6) WHERE Id=@Id;UPDATE form_definitions SET CurrentPublishedVersionId=@Id WHERE Id=@DefinitionId;", new { DefinitionId = row.FormDefinitionId, UserId = user.Id, Id = versionId }, transaction);
        await transaction.CommitAsync();
        var published = await db.QueryFirstAsync<DynamicFormVersion>("SELECT * FROM form_versions WHERE Id=@Id", new { Id = versionId });
        await PopulateVersionAsync(db, published);
        return (published, "");
    }

    public async Task<IEnumerable<DynamicFormLookupSource>> LookupSourcesAsync()
    {
        await using var db = Db();
        await db.OpenAsync();
        return await db.QueryAsync<DynamicFormLookupSource>("SELECT * FROM form_lookup_sources WHERE IsActive=TRUE ORDER BY SourceName");
    }

    public async Task<IEnumerable<DynamicLookupOption>> ResolveLookupAsync(string sourceCode, int clientId, string search)
    {
        await using var db = Db();
        await db.OpenAsync();
        var source = await db.QueryFirstOrDefaultAsync<DynamicFormLookupSource>("SELECT * FROM form_lookup_sources WHERE SourceCode=@Code AND IsActive=TRUE", new { Code = Code(sourceCode) });
        if (source is null || search.Trim().Length < source.MinimumSearchLength) return [];
        var term = search.Trim();
        var limit = Math.Clamp(source.MaximumResults, 1, 200);
        return source.ResolverCode switch
        {
            "CLIENTS" => await db.QueryAsync<DynamicLookupOption>("SELECT CAST(Id AS CHAR) Value,Name Label FROM clients WHERE IsActive=TRUE AND (@Search='' OR Name LIKE CONCAT('%',@Search,'%')) ORDER BY Name LIMIT @Limit", new { Search = term, Limit = limit }),
            "DEPARTMENTS_BY_CLIENT" => await db.QueryAsync<DynamicLookupOption>("SELECT Department Value,Department Label FROM employees WHERE ClientId=@ClientId AND IsActive=TRUE AND Department<>'' AND (@Search='' OR Department LIKE CONCAT('%',@Search,'%')) GROUP BY Department ORDER BY Department LIMIT @Limit", new { ClientId = clientId, Search = term, Limit = limit }),
            "WORK_LOCATIONS_BY_CLIENT" => await db.QueryAsync<DynamicLookupOption>("SELECT CAST(Id AS CHAR) Value,CONCAT(Name,CASE WHEN City='' THEN '' ELSE CONCAT(' - ',City) END) Label FROM worklocations WHERE ClientId=@ClientId AND IsActive=TRUE AND (@Search='' OR CONCAT(Name,' ',City,' ',State) LIKE CONCAT('%',@Search,'%')) ORDER BY Name LIMIT @Limit", new { ClientId = clientId, Search = term, Limit = limit }),
            "SKILLS" => await db.QueryAsync<DynamicLookupOption>("SELECT CAST(Id AS CHAR) Value,SkillName Label FROM recruitment_skills WHERE (ClientId=0 OR ClientId=@ClientId) AND IsActive=TRUE AND (@Search='' OR SkillName LIKE CONCAT('%',@Search,'%')) ORDER BY SkillName LIMIT @Limit", new { ClientId = clientId, Search = term, Limit = limit }),
            "EMPLOYEES_BY_CLIENT" => await db.QueryAsync<DynamicLookupOption>("SELECT CAST(Id AS CHAR) Value,CONCAT(EmployeeCode,' - ',FirstName,' ',LastName) Label FROM employees WHERE ClientId=@ClientId AND IsActive=TRUE AND (@Search='' OR CONCAT(EmployeeCode,' ',FirstName,' ',LastName) LIKE CONCAT('%',@Search,'%')) ORDER BY FirstName,LastName LIMIT @Limit", new { ClientId = clientId, Search = term, Limit = limit }),
            _ => []
        };
    }

    public async Task<(IEnumerable<DynamicLookupOption> Items, string Error)> ResolvePublicLookupAsync(string token, long fieldId, string search)
    {
        if (string.IsNullOrWhiteSpace(token) || fieldId <= 0) return ([], "Application session is invalid or expired.");
        await using var db = Db();
        await db.OpenAsync();
        var session = await ValidateSessionAsync(db, token, true);
        if (session is null) return ([], "Application session is invalid or expired.");
        var source = await db.QueryFirstOrDefaultAsync<DynamicFormLookupSource>(@"SELECT l.Id,l.SourceCode,l.SourceName,l.ResolverCode,l.IsClientScoped,l.MinimumSearchLength,l.MaximumResults,l.IsActive
FROM form_submissions s JOIN form_fields f ON f.FormVersionId=s.FormVersionId
JOIN form_lookup_sources l ON l.Id=f.LookupSourceId AND l.IsActive=TRUE
WHERE s.Id=@SubmissionId AND f.Id=@FieldId AND f.IsActive=TRUE", new { session.SubmissionId, FieldId = fieldId });
        if (source is null) return ([], "This field does not have an active lookup.");
        if (source.ResolverCode.Equals("EMPLOYEES_BY_CLIENT", StringComparison.OrdinalIgnoreCase))
            return ([], "This lookup is not available on the public application form.");
        var term = (search ?? "").Trim();
        if (term.Length < source.MinimumSearchLength) return ([], $"Enter at least {source.MinimumSearchLength} characters.");
        var items = await ResolvePublicLookupOptionsAsync(db, null, source, await SubmissionClientIdAsync(db, session.SubmissionId), term, null);
        return (items, "");
    }

    public async Task<(IEnumerable<DynamicLookupOption> Items, string Error)> ResolveSubmissionLookupAsync(long submissionId, int clientId, long fieldId, string search)
    {
        if (submissionId <= 0 || clientId <= 0 || fieldId <= 0) return ([], "Submission was not found.");
        await using var db = Db();
        await db.OpenAsync();
        var source = await db.QueryFirstOrDefaultAsync<DynamicFormLookupSource>(@"SELECT l.Id,l.SourceCode,l.SourceName,l.ResolverCode,l.IsClientScoped,l.MinimumSearchLength,l.MaximumResults,l.IsActive
FROM form_submissions s JOIN form_fields f ON f.FormVersionId=s.FormVersionId
JOIN form_lookup_sources l ON l.Id=f.LookupSourceId AND l.IsActive=TRUE
WHERE s.Id=@SubmissionId AND s.ClientId=@ClientId AND s.Status='Draft' AND f.Id=@FieldId AND f.IsActive=TRUE", new { SubmissionId = submissionId, ClientId = clientId, FieldId = fieldId });
        if (source is null) return ([], "This field does not have an active lookup.");
        if (source.ResolverCode.Equals("EMPLOYEES_BY_CLIENT", StringComparison.OrdinalIgnoreCase))
            return ([], "This lookup is not available on an external form.");
        var term = (search ?? "").Trim();
        if (term.Length < source.MinimumSearchLength) return ([], $"Enter at least {source.MinimumSearchLength} characters.");
        return (await ResolvePublicLookupOptionsAsync(db, null, source, clientId, term, null), "");
    }

    public async Task<PublicRecruitmentJob?> GetPublicJobAsync(string slug)
    {
        slug = (slug ?? "").Trim();
        if (!ValidPublicSlug(slug)) return null;
        await using var db = Db();
        await db.OpenAsync();
        var job = await db.QueryFirstOrDefaultAsync<PublicRecruitmentJob>(@"SELECT j.Id PostingId,j.PublicSlug,j.PublicTitle,p.PositionCode,p.PositionTitle,c.Name ClientName,p.Department,p.JobLocation,p.EmploymentType,'' WorkMode,j.ClosesAtUtc,d.Summary,d.RolePurpose
FROM recruitment_job_postings j JOIN recruitment_open_positions p ON p.Id=j.PositionId JOIN clients c ON c.Id=j.ClientId
JOIN recruitment_settings settings ON settings.ClientId=j.ClientId AND settings.RecruitmentEnabled=TRUE AND settings.EnableCandidatePortal=TRUE AND settings.IsActive=TRUE
JOIN recruitment_job_description_versions d ON d.Id=j.JobDescriptionVersionId
WHERE j.PublicSlug=@Slug AND j.Status='Published' AND (j.OpensAtUtc IS NULL OR j.OpensAtUtc<=UTC_TIMESTAMP(6)) AND (j.ClosesAtUtc IS NULL OR j.ClosesAtUtc>=UTC_TIMESTAMP(6)) AND (j.MaximumApplications IS NULL OR j.ApplicationCount<j.MaximumApplications)", new { Slug = slug.Trim() });
        if (job is null) return null;
        var formVersionId = await db.ExecuteScalarAsync<long?>("SELECT ApplicationFormVersionId FROM recruitment_job_postings WHERE Id=@Id", new { Id = job.PostingId });
        if (formVersionId.HasValue)
        {
            // A posting pins an immutable version. It must keep working when a newer
            // version is published and the pinned one becomes Retired.
            job.ApplicationForm = await db.QueryFirstOrDefaultAsync<DynamicFormVersion>("SELECT * FROM form_versions WHERE Id=@Id AND Status IN ('Published','Retired')", new { Id = formVersionId.Value });
            if (job.ApplicationForm is not null)
            {
                await PopulateVersionAsync(db, job.ApplicationForm);
                SanitizePublicVersion(job.ApplicationForm);
            }
        }
        var jdId = await db.ExecuteScalarAsync<long>("SELECT JobDescriptionVersionId FROM recruitment_job_postings WHERE Id=@Id", new { Id = job.PostingId });
        job.Responsibilities = (await db.QueryAsync<RecruitmentJdResponsibility>("SELECT * FROM recruitment_jd_responsibilities WHERE JobDescriptionVersionId=@Id ORDER BY DisplayOrder", new { Id = jdId })).ToList();
        job.Skills = (await db.QueryAsync<RecruitmentJdSkillRequirement>("SELECT * FROM recruitment_jd_skill_requirements WHERE JobDescriptionVersionId=@Id ORDER BY DisplayOrder", new { Id = jdId })).ToList();
        job.Qualifications = (await db.QueryAsync<RecruitmentJdQualificationRequirement>("SELECT * FROM recruitment_jd_qualification_requirements WHERE JobDescriptionVersionId=@Id ORDER BY DisplayOrder", new { Id = jdId })).ToList();
        return job;
    }

    public async Task<(PublicApplicationSession? Session, string Error)> StartPublicSessionAsync(string slug, StartPublicApplicationRequest request, string ipAddress, string userAgent)
    {
        request ??= new StartPublicApplicationRequest();
        if (!request.ConsentAccepted) return (null, "Consent is required before starting an application.");
        var email = (request.Email ?? "").Trim();
        var phone = (request.Phone ?? "").Trim();
        if (string.IsNullOrWhiteSpace(email) && string.IsNullOrWhiteSpace(phone)) return (null, "Email or phone is required.");
        if (email.Length > 0 && !ValidEmail(email)) return (null, "Enter a valid email address.");
        var normalizedPhone = NormalizePhone(phone);
        if (phone.Length > 0 && (normalizedPhone.Length < 7 || normalizedPhone.Length > 15)) return (null, "Enter a valid phone number.");
        slug = (slug ?? "").Trim();
        if (!ValidPublicSlug(slug)) return (null, "This job is not accepting applications.");
        await using var db = Db();
        await db.OpenAsync();
        var posting = await db.QueryFirstOrDefaultAsync<PostingSessionRow>(@"SELECT posting.Id PostingId,posting.ClientId,posting.PositionId,posting.ApplicationFormVersionId FROM recruitment_job_postings posting
JOIN recruitment_settings settings ON settings.ClientId=posting.ClientId AND settings.RecruitmentEnabled=TRUE AND settings.EnableCandidatePortal=TRUE AND settings.IsActive=TRUE
WHERE posting.PublicSlug=@Slug AND posting.Status='Published' AND posting.ApplicationFormVersionId IS NOT NULL
AND EXISTS (SELECT 1 FROM form_versions v WHERE v.Id=ApplicationFormVersionId AND v.Status IN ('Published','Retired'))
AND (posting.OpensAtUtc IS NULL OR posting.OpensAtUtc<=UTC_TIMESTAMP(6)) AND (posting.ClosesAtUtc IS NULL OR posting.ClosesAtUtc>=UTC_TIMESTAMP(6))
AND (posting.MaximumApplications IS NULL OR posting.ApplicationCount<posting.MaximumApplications)", new { Slug = slug });
        if (posting is null) return (null, "This job is not accepting applications.");
        var normalizedEmail = NormalizeEmail(email);
        var idempotency = string.IsNullOrWhiteSpace(request.IdempotencyKey) ? Guid.NewGuid().ToString("N") : request.IdempotencyKey.Trim();
        var idempotencyHash = Hash($"{posting.PostingId}:{normalizedEmail}:{normalizedPhone}:{idempotency}");
        await using var transaction = await db.BeginTransactionAsync();
        try
        {
            var duplicate = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM form_public_sessions WHERE PostingId=@PostingId AND IdempotencyHash=@Hash", new { posting.PostingId, Hash = idempotencyHash }, transaction);
            if (duplicate > 0) return (null, "This application session has already been created. Start a new application if the previous session expired.");
            var matchingSubjects = (await db.QueryAsync<ExternalSubjectRow>(@"SELECT Id,CandidateId,Email,NormalizedEmail,Phone,NormalizedPhone
FROM external_portal_subjects
WHERE ClientId=@ClientId AND ((@Email<>'' AND NormalizedEmail=@Email) OR (@Phone<>'' AND NormalizedPhone=@Phone))
ORDER BY Id FOR UPDATE", new { posting.ClientId, Email = normalizedEmail, Phone = normalizedPhone }, transaction)).ToList();
            if (matchingSubjects.Select(row => row.Id).Distinct().Count() > 1)
                return (null, "The supplied email and phone belong to different existing profiles. Contact HR or use one verified identifier.");
            var subject = matchingSubjects.FirstOrDefault();
            long subjectId;
            if (subject is null)
            {
                subjectId = await db.ExecuteScalarAsync<long>(@"INSERT INTO external_portal_subjects (ClientId,Email,NormalizedEmail,Phone,NormalizedPhone,ConsentAccepted,ConsentAcceptedAtUtc)
VALUES (@ClientId,@Email,@NormalizedEmail,@Phone,@NormalizedPhone,TRUE,UTC_TIMESTAMP(6));SELECT LAST_INSERT_ID();", new { posting.ClientId, Email = email, NormalizedEmail = normalizedEmail, Phone = phone, NormalizedPhone = normalizedPhone }, transaction);
            }
            else
            {
                if (normalizedEmail.Length > 0 && subject.NormalizedEmail.Length > 0 && subject.NormalizedEmail != normalizedEmail)
                    return (null, "The supplied email does not match the existing applicant profile.");
                if (normalizedPhone.Length > 0 && subject.NormalizedPhone.Length > 0 && subject.NormalizedPhone != normalizedPhone)
                    return (null, "The supplied phone does not match the existing applicant profile.");
                subjectId = subject.Id;
                await db.ExecuteAsync(@"UPDATE external_portal_subjects SET
Email=CASE WHEN @NormalizedEmail='' THEN Email ELSE @Email END,
NormalizedEmail=CASE WHEN @NormalizedEmail='' THEN NormalizedEmail ELSE @NormalizedEmail END,
Phone=CASE WHEN @NormalizedPhone='' THEN Phone ELSE @Phone END,
NormalizedPhone=CASE WHEN @NormalizedPhone='' THEN NormalizedPhone ELSE @NormalizedPhone END,
ConsentAccepted=TRUE,ConsentAcceptedAtUtc=UTC_TIMESTAMP(6) WHERE Id=@Id", new { Email = email, NormalizedEmail = normalizedEmail, Phone = phone, NormalizedPhone = normalizedPhone, Id = subjectId }, transaction);
            }
            var submissionId = await db.ExecuteScalarAsync<long>(@"INSERT INTO form_submissions (FormVersionId,ClientId,ExternalSubjectId,EntityType,Status) VALUES (@FormVersionId,@ClientId,@SubjectId,'FORM_SUBMISSION','Draft');SELECT LAST_INSERT_ID();", new { FormVersionId = posting.ApplicationFormVersionId!.Value, posting.ClientId, SubjectId = subjectId }, transaction);
            var rawToken = RandomToken();
            var expires = DateTime.UtcNow.AddHours(Math.Clamp(configuration.GetValue("Recruitment:PublicSessionLifetimeHours", 24), 1, 168));
            await db.ExecuteAsync(@"INSERT INTO form_public_sessions (TokenHash,PostingId,SubmissionId,ExternalSubjectId,Purpose,IdempotencyHash,ExpiresAtUtc,IpAddress,UserAgent) VALUES (@TokenHash,@PostingId,@SubmissionId,@SubjectId,'APPLICATION',@IdempotencyHash,@Expires,@IpAddress,@UserAgent)", new { TokenHash = Hash(rawToken), posting.PostingId, SubmissionId = submissionId, SubjectId = subjectId, IdempotencyHash = idempotencyHash, Expires = expires, IpAddress = Truncate(ipAddress, 80), UserAgent = Truncate(userAgent, 500) }, transaction);
            await EventAsync(db, transaction, submissionId, "STARTED", "Public application started.", subjectId, ipAddress, userAgent);
            await transaction.CommitAsync();
            return (new PublicApplicationSession { SessionToken = rawToken, SubmissionId = submissionId, ExpiresAtUtc = expires, Status = "Draft" }, "");
        }
        catch (Exception exception)
        {
            await transaction.RollbackAsync();
            return (null, exception is InvalidOperationException ? exception.Message : "The application session could not be created. Please retry.");
        }
    }

    public async Task<(bool Ok, string Error)> SavePublicValuesAsync(string token, SavePublicFormValuesRequest request, string ipAddress, string userAgent)
    {
        if (string.IsNullOrWhiteSpace(token)) return (false, "Application session is invalid or expired.");
        request ??= new SavePublicFormValuesRequest();
        request.Values ??= [];
        await using var db = Db();
        await db.OpenAsync();
        await using var transaction = await db.BeginTransactionAsync();
        try
        {
            var session = await db.QueryFirstOrDefaultAsync<PublicSessionRow>(SessionSql + " FOR UPDATE", new { TokenHash = Hash(token) }, transaction);
            if (!SessionValid(session)) return (false, "Application session is invalid or expired.");
            var clientId = await SubmissionClientIdAsync(db, session!.SubmissionId, transaction);
            var saveError = await SaveSubmissionValuesInternalAsync(db, transaction, session.SubmissionId, clientId, request);
            if (saveError.Length > 0) throw new InvalidOperationException(saveError);
            await db.ExecuteAsync("UPDATE form_submissions SET UpdatedAtUtc=UTC_TIMESTAMP(6) WHERE Id=@Id", new { Id = session!.SubmissionId }, transaction);
            await TouchSessionAsync(db, transaction, session.Id);
            await EventAsync(db, transaction, session.SubmissionId, "SAVED", "Application draft saved.", session.ExternalSubjectId, ipAddress, userAgent);
            await transaction.CommitAsync();
            return (true, "");
        }
        catch (Exception exception)
        {
            await transaction.RollbackAsync();
            return (false, exception is InvalidOperationException ? exception.Message : "The application draft could not be saved. Please retry.");
        }
    }

    public async Task<(bool Ok, string Error)> SaveSubmissionValuesAsync(long submissionId, int clientId, SavePublicFormValuesRequest request)
    {
        if (submissionId <= 0 || clientId <= 0) return (false, "Submission was not found.");
        request ??= new SavePublicFormValuesRequest();
        request.Values ??= [];
        await using var db = Db();
        await db.OpenAsync();
        await using var transaction = await db.BeginTransactionAsync();
        try
        {
            var lockedId = await db.ExecuteScalarAsync<long?>("SELECT Id FROM form_submissions WHERE Id=@Id AND ClientId=@ClientId AND Status='Draft' FOR UPDATE", new { Id = submissionId, ClientId = clientId }, transaction);
            if (!lockedId.HasValue) return (false, "Submission was not found or is no longer editable.");
            var error = await SaveSubmissionValuesInternalAsync(db, transaction, submissionId, clientId, request);
            if (error.Length > 0) return (false, error);
            await db.ExecuteAsync("UPDATE form_submissions SET UpdatedAtUtc=UTC_TIMESTAMP(6) WHERE Id=@Id", new { Id = submissionId }, transaction);
            await transaction.CommitAsync();
            return (true, "");
        }
        catch (Exception exception)
        {
            await transaction.RollbackAsync();
            return (false, exception is InvalidOperationException ? exception.Message : "The submission values could not be saved.");
        }
    }

    private static async Task<string> SaveSubmissionValuesInternalAsync(MySqlConnection db, MySqlTransaction transaction, long submissionId, int clientId, SavePublicFormValuesRequest request)
    {
        var values = request.Values ?? [];
        if (values.Any(value => value is null)) return "A submitted form value is invalid.";
        if (values.GroupBy(value => value.FieldId).Any(group => group.Key <= 0 || group.Count() > 1))
            return "Each form field can be submitted only once per save request.";
        foreach (var value in values)
        {
            var field = await db.QueryFirstOrDefaultAsync<FieldValidationRow>(@"SELECT f.Id,f.FieldTypeId,t.TypeCode,t.SupportsOptions,t.SupportsMultipleValues,t.SupportsAttachment,
f.MinimumLength,f.MaximumLength,f.MinimumNumber,f.MaximumNumber,f.MinimumDate,f.MaximumDate,f.LookupSourceId
FROM form_fields f JOIN form_field_types t ON t.Id=f.FieldTypeId JOIN form_submissions s ON s.FormVersionId=f.FormVersionId
WHERE s.Id=@SubmissionId AND s.ClientId=@ClientId AND f.Id=@FieldId AND f.IsActive=TRUE", new { SubmissionId = submissionId, ClientId = clientId, value.FieldId }, transaction);
            if (field is null) return "A submitted field is not part of this form.";
            var validationError = ValidateValue(field, value);
            if (validationError.Length > 0) return validationError;
            await db.ExecuteAsync("DELETE FROM form_submission_selected_options WHERE SubmissionId=@SubmissionId AND FieldId=@FieldId;DELETE FROM form_submission_lookup_values WHERE SubmissionId=@SubmissionId AND FieldId=@FieldId;DELETE FROM form_submission_values WHERE SubmissionId=@SubmissionId AND FieldId=@FieldId;", new { SubmissionId = submissionId, value.FieldId }, transaction);
            if (HasScalarValue(field.TypeCode, value))
                await db.ExecuteAsync(@"INSERT INTO form_submission_values (SubmissionId,FieldId,TextValue,IntegerValue,DecimalValue,DateValue,DateTimeValue,BooleanValue)
VALUES (@SubmissionId,@FieldId,@TextValue,@IntegerValue,@DecimalValue,@DateValue,@DateTimeValue,@BooleanValue)", new { SubmissionId = submissionId, value.FieldId, TextValue = value.TextValue?.Trim(), value.IntegerValue, value.DecimalValue, value.DateValue, value.DateTimeValue, value.BooleanValue }, transaction);
            foreach (var optionId in (value.SelectedOptionIds ?? []).Distinct())
            {
                var valid = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM form_field_options WHERE Id=@OptionId AND FieldId=@FieldId AND IsActive=TRUE", new { OptionId = optionId, value.FieldId }, transaction);
                if (valid == 0) return "A selected option is invalid.";
                await db.ExecuteAsync("INSERT INTO form_submission_selected_options (SubmissionId,FieldId,OptionId) VALUES (@SubmissionId,@FieldId,@OptionId)", new { SubmissionId = submissionId, value.FieldId, OptionId = optionId }, transaction);
            }
            if (field.LookupSourceId.HasValue)
            {
                var source = await db.QueryFirstOrDefaultAsync<DynamicFormLookupSource>("SELECT * FROM form_lookup_sources WHERE Id=@Id AND IsActive=TRUE", new { Id = field.LookupSourceId.Value }, transaction);
                if (source is null || source.ResolverCode.Equals("EMPLOYEES_BY_CLIENT", StringComparison.OrdinalIgnoreCase))
                    return "This lookup is not available on the public application form.";
                var order = 0;
                foreach (var selectedValue in (value.SelectedOptionValues ?? []).Select(item => (item ?? "").Trim()).Where(item => item.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (selectedValue.Length > 250) return "A selected lookup value is too long.";
                    var resolved = (await ResolvePublicLookupOptionsAsync(db, transaction, source, clientId, "", selectedValue)).SingleOrDefault();
                    if (resolved is null) return "A selected lookup value is invalid or no longer active.";
                    await db.ExecuteAsync(@"INSERT INTO form_submission_lookup_values (SubmissionId,FieldId,SelectedValue,DisplayLabel,DisplayOrder)
VALUES (@SubmissionId,@FieldId,@SelectedValue,@DisplayLabel,@DisplayOrder)", new { SubmissionId = submissionId, value.FieldId, SelectedValue = resolved.Value, DisplayLabel = resolved.Label, DisplayOrder = ++order * 10 }, transaction);
                }
            }
        }
        if (values.Count > 0)
        {
            var affectedFieldIds = values.Select(value => value.FieldId).ToHashSet();
            var ruleError = await SubmissionRuleErrorAsync(db, transaction, submissionId, false, affectedFieldIds);
            if (ruleError.Length > 0) return ruleError;
        }
        return "";
    }

    public async Task<(bool Ok, string Error)> ValidateRequiredSubmissionAsync(long submissionId)
    {
        if (submissionId <= 0) return (false, "Submission was not found.");
        await using var db = Db();
        await db.OpenAsync();
        var versionId = await db.ExecuteScalarAsync<long?>("SELECT FormVersionId FROM form_submissions WHERE Id=@Id", new { Id = submissionId });
        if (!versionId.HasValue) return (false, "Submission was not found.");
        var error = await RequiredSubmissionErrorAsync(db, null, submissionId, versionId.Value);
        if (error.Length == 0) error = await SubmissionRuleErrorAsync(db, null, submissionId, true, null);
        return (error.Length == 0, error);
    }

    private static async Task<string> RequiredSubmissionErrorAsync(MySqlConnection db, MySqlTransaction? transaction, long submissionId, long versionId)
    {
        var missing = (await db.QueryAsync<string>(@"SELECT f.Label
FROM form_fields f JOIN form_sections sectionRow ON sectionRow.Id=f.SectionId JOIN form_field_types t ON t.Id=f.FieldTypeId
WHERE f.FormVersionId=@VersionId AND f.IsActive=TRUE AND f.IsRequired=TRUE AND (
 (t.TypeCode='UPLOAD' AND NOT EXISTS (SELECT 1 FROM form_submission_attachments a
      JOIN entity_attachments attachment ON attachment.id=a.AttachmentId AND attachment.is_current=TRUE AND attachment.is_deleted=FALSE
      WHERE a.SubmissionId=@SubmissionId AND a.FieldId=f.Id))
 OR (t.TypeCode IN ('RADIO','MULTI_SELECT','SEARCH_SELECT') AND (
      (f.LookupSourceId IS NULL AND NOT EXISTS (SELECT 1 FROM form_submission_selected_options o WHERE o.SubmissionId=@SubmissionId AND o.FieldId=f.Id))
      OR (f.LookupSourceId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM form_submission_lookup_values l WHERE l.SubmissionId=@SubmissionId AND l.FieldId=f.Id))
 ))
 OR (t.TypeCode IN ('TEXT','TEXTAREA','EMAIL','PHONE') AND NOT EXISTS (SELECT 1 FROM form_submission_values v WHERE v.SubmissionId=@SubmissionId AND v.FieldId=f.Id AND TRIM(COALESCE(v.TextValue,''))<>''))
 OR (t.TypeCode='NUMBER' AND NOT EXISTS (SELECT 1 FROM form_submission_values v WHERE v.SubmissionId=@SubmissionId AND v.FieldId=f.Id AND (v.IntegerValue IS NOT NULL OR v.DecimalValue IS NOT NULL)))
 OR (t.TypeCode='DATE' AND NOT EXISTS (SELECT 1 FROM form_submission_values v WHERE v.SubmissionId=@SubmissionId AND v.FieldId=f.Id AND v.DateValue IS NOT NULL))
 OR (t.TypeCode='DATETIME' AND NOT EXISTS (SELECT 1 FROM form_submission_values v WHERE v.SubmissionId=@SubmissionId AND v.FieldId=f.Id AND v.DateTimeValue IS NOT NULL))
 OR (t.TypeCode='CHECKBOX' AND NOT EXISTS (SELECT 1 FROM form_submission_values v WHERE v.SubmissionId=@SubmissionId AND v.FieldId=f.Id AND v.BooleanValue=TRUE))
)
ORDER BY sectionRow.DisplayOrder,sectionRow.Id,f.DisplayOrder,f.Id", new { VersionId = versionId, SubmissionId = submissionId }, transaction)).ToList();
        return missing.Count == 0 ? "" : $"Complete required fields: {string.Join(", ", missing)}.";
    }

    private static async Task<string> StoredRuleConfigurationErrorAsync(MySqlConnection db, long versionId)
    {
        var rules = await db.QueryAsync<StoredValidationRuleRow>(@"SELECT r.*,f.StableFieldCode FieldCode,f.Label FieldLabel,t.TypeCode FieldTypeCode,
compareField.Id CompareFieldResolvedId,COALESCE(compareField.StableFieldCode,'') CompareFieldCode,
COALESCE(compareField.Label,'') CompareFieldLabel,COALESCE(compareType.TypeCode,'') CompareFieldTypeCode,
COALESCE(compareField.IsActive,FALSE) CompareFieldIsActive
FROM form_field_validation_rules r
JOIN form_fields f ON f.Id=r.FieldId AND f.FormVersionId=@VersionId AND f.IsActive=TRUE
JOIN form_sections sectionRow ON sectionRow.Id=f.SectionId
JOIN form_field_types t ON t.Id=f.FieldTypeId
LEFT JOIN form_fields compareField ON compareField.Id=r.CompareFieldId AND compareField.FormVersionId=f.FormVersionId
LEFT JOIN form_field_types compareType ON compareType.Id=compareField.FieldTypeId
ORDER BY sectionRow.DisplayOrder,sectionRow.Id,f.DisplayOrder,f.Id,r.DisplayOrder,r.Id", new { VersionId = versionId });
        foreach (var stored in rules)
        {
            var field = (stored.FieldCode, stored.FieldLabel, stored.FieldTypeCode, true);
            (string Code, string Label, string TypeCode, bool IsActive)? compareField = stored.CompareFieldId.HasValue && stored.CompareFieldResolvedId.HasValue
                ? (stored.CompareFieldCode, stored.CompareFieldLabel, stored.CompareFieldTypeCode, stored.CompareFieldIsActive)
                : null;
            var (_, error) = NormalizeValidationRule(stored, stored.FieldId, field, stored.CompareFieldId, compareField);
            if (error.Length > 0) return $"Review validation rules for field '{stored.FieldLabel}': {error}";
        }
        return "";
    }

    private static async Task<string> SubmissionRuleErrorAsync(MySqlConnection db, MySqlTransaction? transaction, long submissionId, bool isFinal, ISet<long>? affectedFieldIds)
    {
        var versionId = await db.ExecuteScalarAsync<long?>("SELECT FormVersionId FROM form_submissions WHERE Id=@Id", new { Id = submissionId }, transaction);
        if (!versionId.HasValue) return "Submission was not found.";
        var states = (await db.QueryAsync<SubmissionFieldState>(@"SELECT f.Id FieldId,f.StableFieldCode,f.Label,t.TypeCode,
v.TextValue,v.IntegerValue,v.DecimalValue,v.DateValue,v.DateTimeValue,v.BooleanValue,
(SELECT COUNT(*) FROM form_submission_selected_options selectedOption WHERE selectedOption.SubmissionId=@SubmissionId AND selectedOption.FieldId=f.Id) SelectedOptionCount,
(SELECT COUNT(*) FROM form_submission_lookup_values lookupValue WHERE lookupValue.SubmissionId=@SubmissionId AND lookupValue.FieldId=f.Id) LookupValueCount,
(SELECT COUNT(*) FROM form_submission_attachments linkedAttachment
 JOIN entity_attachments attachment ON attachment.id=linkedAttachment.AttachmentId AND attachment.is_current=TRUE AND attachment.is_deleted=FALSE
 WHERE linkedAttachment.SubmissionId=@SubmissionId AND linkedAttachment.FieldId=f.Id) AttachmentCount
FROM form_fields f
JOIN form_field_types t ON t.Id=f.FieldTypeId
LEFT JOIN form_submission_values v ON v.SubmissionId=@SubmissionId AND v.FieldId=f.Id
WHERE f.FormVersionId=@VersionId AND f.IsActive=TRUE", new { SubmissionId = submissionId, VersionId = versionId.Value }, transaction)).ToList();
        var stateById = states.ToDictionary(row => row.FieldId);
        var rules = (await db.QueryAsync<DynamicFormValidationRule>(@"SELECT r.*,COALESCE(compareField.StableFieldCode,'') CompareFieldCode
FROM form_field_validation_rules r
JOIN form_fields f ON f.Id=r.FieldId AND f.FormVersionId=@VersionId AND f.IsActive=TRUE
JOIN form_sections sectionRow ON sectionRow.Id=f.SectionId
LEFT JOIN form_fields compareField ON compareField.Id=r.CompareFieldId AND compareField.FormVersionId=f.FormVersionId
ORDER BY sectionRow.DisplayOrder,sectionRow.Id,f.DisplayOrder,f.Id,r.DisplayOrder,r.Id", new { VersionId = versionId.Value }, transaction)).ToList();
        foreach (var sourceRule in rules)
        {
            if (!stateById.TryGetValue(sourceRule.FieldId, out var field)) continue;
            if (!isFinal && affectedFieldIds is not null && !affectedFieldIds.Contains(sourceRule.FieldId)
                && (!sourceRule.CompareFieldId.HasValue || !affectedFieldIds.Contains(sourceRule.CompareFieldId.Value))) continue;
            SubmissionFieldState? compareState = null;
            if (sourceRule.CompareFieldId.HasValue) stateById.TryGetValue(sourceRule.CompareFieldId.Value, out compareState);
            var fieldInfo = (field.StableFieldCode, field.Label, field.TypeCode, true);
            (string Code, string Label, string TypeCode, bool IsActive)? compareInfo = compareState is null
                ? null
                : (compareState.StableFieldCode, compareState.Label, compareState.TypeCode, true);
            var (rule, _) = NormalizeValidationRule(sourceRule, sourceRule.FieldId, fieldInfo, sourceRule.CompareFieldId, compareInfo);
            if (rule is null) return RuleConfigurationRuntimeError(field.Label);
            var error = EvaluateSubmissionRule(rule, field, compareState, isFinal);
            if (error.Length > 0) return error;
        }
        return "";
    }

    private static (DynamicFormValidationRule? Rule, string Error) NormalizeValidationRule(
        DynamicFormValidationRule source,
        long fieldId,
        (string Code, string Label, string TypeCode, bool IsActive) field,
        long? compareFieldId,
        (string Code, string Label, string TypeCode, bool IsActive)? compareField)
    {
        var rawType = Code(source.RuleType);
        var canonicalType = CanonicalRuleType(rawType, field.TypeCode, compareFieldId.HasValue);
        if (canonicalType.Length == 0) return (null, $"Rule type '{rawType}' is unsupported or is incompatible with this field type.");
        var comparisonOperator = NormalizeComparisonOperator(source.ComparisonOperator);
        if (rawType == "EQUALS") comparisonOperator = "EQ";
        if (rawType == "NOT_EQUALS") comparisonOperator = "NE";
        var usesComparisonField = canonicalType == "COMPARE_FIELD";
        if (usesComparisonField)
        {
            if (!compareFieldId.HasValue || compareField is null) return (null, "A comparison field from the same form version is required.");
            if (compareFieldId.Value == fieldId) return (null, "A field cannot be compared with itself.");
            if (!compareField.Value.IsActive) return (null, "The comparison field must be active.");
            if (!ScalarTypesCompatible(field.TypeCode, compareField.Value.TypeCode)) return (null, "The compared fields must use compatible scalar data types.");
        }
        else if (compareFieldId.HasValue)
        {
            return (null, "This rule type does not accept a comparison field.");
        }
        var normalized = new DynamicFormValidationRule
        {
            FieldId = fieldId,
            RuleType = rawType,
            ComparisonOperator = comparisonOperator,
            CompareFieldId = usesComparisonField ? compareFieldId : null,
            CompareFieldCode = usesComparisonField ? compareField!.Value.Code : "",
            TextValue = source.TextValue,
            IntegerValue = source.IntegerValue,
            DecimalValue = source.DecimalValue,
            DateValue = source.DateValue,
            BooleanValue = source.BooleanValue,
            ErrorMessage = Truncate((source.ErrorMessage ?? "").Trim(), 500),
            DisplayOrder = source.DisplayOrder
        };
        if (normalized.TextValue?.Length > 500) return (null, "Text or pattern values cannot exceed 500 characters.");
        switch (canonicalType)
        {
            case "REQUIRED":
                ClearRuleOperands(normalized);
                normalized.ComparisonOperator = "";
                break;
            case "EMAIL":
                if (!IsTextField(field.TypeCode)) return (null, "Email validation can only be applied to text fields.");
                ClearRuleOperands(normalized);
                normalized.ComparisonOperator = "";
                break;
            case "PHONE":
                if (!IsTextField(field.TypeCode)) return (null, "Phone validation can only be applied to text fields.");
                ClearRuleOperands(normalized);
                normalized.ComparisonOperator = "";
                break;
            case "DATE":
                if (!IsDateField(field.TypeCode)) return (null, "Date validation can only be applied to date fields.");
                ClearRuleOperands(normalized);
                normalized.ComparisonOperator = "";
                break;
            case "REGEX":
                if (!IsTextField(field.TypeCode)) return (null, "Regular-expression validation can only be applied to text fields.");
                if (string.IsNullOrWhiteSpace(normalized.TextValue)) return (null, "A regular-expression pattern is required.");
                try { _ = new Regex(normalized.TextValue, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250)); }
                catch (ArgumentException) { return (null, "The regular-expression pattern is invalid."); }
                normalized.IntegerValue = null;
                normalized.DecimalValue = null;
                normalized.DateValue = null;
                normalized.BooleanValue = null;
                normalized.ComparisonOperator = "MATCHES";
                break;
            case "MIN_LENGTH":
            case "MAX_LENGTH":
                if (!IsTextField(field.TypeCode)) return (null, "Length validation can only be applied to text fields.");
                if (!TryReadRuleInteger(source, out var length) || length < 0 || length > int.MaxValue) return (null, "A non-negative integer length is required.");
                ClearRuleOperands(normalized);
                normalized.IntegerValue = length;
                normalized.ComparisonOperator = canonicalType == "MIN_LENGTH" ? "GTE" : "LTE";
                break;
            case "MIN_NUMBER":
            case "MAX_NUMBER":
                if (field.TypeCode != "NUMBER") return (null, "Numeric limit validation can only be applied to number fields.");
                if (!TryReadRuleDecimal(source, out var number)) return (null, "A numeric comparison value is required.");
                ClearRuleOperands(normalized);
                normalized.DecimalValue = number;
                normalized.ComparisonOperator = canonicalType == "MIN_NUMBER" ? "GTE" : "LTE";
                break;
            case "MIN_DATE":
            case "MAX_DATE":
                if (!IsDateField(field.TypeCode)) return (null, "Date limit validation can only be applied to date fields.");
                if (!TryReadRuleDate(source, out var date)) return (null, "A valid date comparison value is required.");
                ClearRuleOperands(normalized);
                normalized.DateValue = date;
                normalized.ComparisonOperator = canonicalType == "MIN_DATE" ? "GTE" : "LTE";
                break;
            case "BOOLEAN_TRUE":
                if (field.TypeCode != "CHECKBOX") return (null, "Boolean-true validation can only be applied to checkbox fields.");
                ClearRuleOperands(normalized);
                normalized.BooleanValue = true;
                normalized.ComparisonOperator = "EQ";
                break;
            case "COMPARE_VALUE":
                if (!IsScalarField(field.TypeCode)) return (null, "Scalar comparison is not supported for this field type.");
                if (!ValidComparisonOperator(comparisonOperator, field.TypeCode)) return (null, "A supported comparison operator is required.");
                if (!NormalizeScalarConstant(normalized, source, field.TypeCode)) return (null, "A single typed comparison value is required.");
                break;
            case "COMPARE_FIELD":
                if (!IsScalarField(field.TypeCode)) return (null, "Cross-field comparison is only supported for scalar fields.");
                if (!ValidComparisonOperator(comparisonOperator, field.TypeCode)) return (null, "A supported comparison operator is required.");
                ClearRuleOperands(normalized);
                break;
            default:
                return (null, $"Rule type '{rawType}' is not supported.");
        }
        return (normalized, "");
    }

    private static string EvaluateSubmissionRule(DynamicFormValidationRule rule, SubmissionFieldState field, SubmissionFieldState? compareField, bool isFinal)
    {
        var canonicalType = CanonicalRuleType(rule.RuleType, field.TypeCode, rule.CompareFieldId.HasValue);
        var hasValue = HasRuleValue(field);
        if (canonicalType == "REQUIRED")
        {
            if (!isFinal || hasValue) return "";
            return RuleError(rule, $"{field.Label} is required.");
        }
        if (canonicalType == "BOOLEAN_TRUE")
        {
            if (!hasValue && !isFinal) return "";
            return field.BooleanValue == true ? "" : RuleError(rule, $"{field.Label} must be accepted.");
        }
        if (!hasValue) return "";
        switch (canonicalType)
        {
            case "REGEX":
                try
                {
                    return Regex.IsMatch(field.TextValue ?? "", rule.TextValue!, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250))
                        ? ""
                        : RuleError(rule, $"{field.Label} is invalid.");
                }
                catch (ArgumentException) { return RuleConfigurationRuntimeError(field.Label); }
                catch (RegexMatchTimeoutException) { return RuleConfigurationRuntimeError(field.Label); }
            case "EMAIL":
                return ValidEmail(field.TextValue ?? "") ? "" : RuleError(rule, $"Enter a valid {field.Label.ToLowerInvariant()}.");
            case "PHONE":
                var digits = NormalizePhone(field.TextValue ?? "");
                return digits.Length is >= 7 and <= 15 ? "" : RuleError(rule, $"Enter a valid {field.Label.ToLowerInvariant()}.");
            case "DATE":
                return TryGetRuleDate(field, out _) ? "" : RuleError(rule, $"Enter a valid {field.Label.ToLowerInvariant()}.");
            case "MIN_LENGTH":
                return (field.TextValue?.Trim().Length ?? 0) >= rule.IntegerValue!.Value ? "" : RuleError(rule, $"{field.Label} must contain at least {rule.IntegerValue} characters.");
            case "MAX_LENGTH":
                return (field.TextValue?.Trim().Length ?? 0) <= rule.IntegerValue!.Value ? "" : RuleError(rule, $"{field.Label} cannot exceed {rule.IntegerValue} characters.");
            case "MIN_NUMBER":
                return TryGetRuleNumber(field, out var minimumNumberValue) && minimumNumberValue >= rule.DecimalValue!.Value ? "" : RuleError(rule, $"{field.Label} cannot be below {rule.DecimalValue}.");
            case "MAX_NUMBER":
                return TryGetRuleNumber(field, out var maximumNumberValue) && maximumNumberValue <= rule.DecimalValue!.Value ? "" : RuleError(rule, $"{field.Label} cannot exceed {rule.DecimalValue}.");
            case "MIN_DATE":
                return TryGetRuleDate(field, out var minimumDateValue) && minimumDateValue.Date >= rule.DateValue!.Value.Date ? "" : RuleError(rule, $"{field.Label} cannot be before {rule.DateValue.Value:dd-MMM-yyyy}.");
            case "MAX_DATE":
                return TryGetRuleDate(field, out var maximumDateValue) && maximumDateValue.Date <= rule.DateValue!.Value.Date ? "" : RuleError(rule, $"{field.Label} cannot be after {rule.DateValue.Value:dd-MMM-yyyy}.");
            case "COMPARE_VALUE":
                return TryCompareScalar(field, null, rule, out var scalarPassed) && scalarPassed ? "" : RuleError(rule, $"{field.Label} does not satisfy the configured comparison.");
            case "COMPARE_FIELD":
                if (compareField is null) return RuleConfigurationRuntimeError(field.Label);
                if (!HasRuleValue(compareField))
                {
                    if (!isFinal) return "";
                    return RuleError(rule, $"{compareField.Label} is required to validate {field.Label}.");
                }
                return TryCompareScalar(field, compareField, rule, out var fieldPassed) && fieldPassed
                    ? ""
                    : RuleError(rule, $"{field.Label} does not satisfy the comparison with {compareField.Label}.");
            default:
                return RuleConfigurationRuntimeError(field.Label);
        }
    }

    private static bool TryCompareScalar(SubmissionFieldState field, SubmissionFieldState? compareField, DynamicFormValidationRule rule, out bool passed)
    {
        passed = false;
        var comparison = 0;
        if (IsTextField(field.TypeCode))
        {
            var right = compareField is null ? rule.TextValue : compareField.TextValue;
            if (right is null) return false;
            comparison = string.Compare(field.TextValue?.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        else if (field.TypeCode == "NUMBER")
        {
            if (!TryGetRuleNumber(field, out var left)) return false;
            decimal? right = null;
            if (compareField is not null && TryGetRuleNumber(compareField, out var comparedNumber)) right = comparedNumber;
            else if (compareField is null) right = rule.DecimalValue ?? (rule.IntegerValue.HasValue ? (decimal)rule.IntegerValue.Value : null);
            if (!right.HasValue) return false;
            comparison = left.CompareTo(right.Value);
        }
        else if (IsDateField(field.TypeCode))
        {
            if (!TryGetRuleDate(field, out var left)) return false;
            DateTime? right = null;
            if (compareField is not null && TryGetRuleDate(compareField, out var comparedDate)) right = comparedDate;
            else if (compareField is null) right = rule.DateValue;
            if (!right.HasValue) return false;
            comparison = left.CompareTo(right.Value);
        }
        else if (field.TypeCode == "CHECKBOX")
        {
            if (!field.BooleanValue.HasValue) return false;
            var right = compareField?.BooleanValue ?? rule.BooleanValue;
            if (!right.HasValue) return false;
            comparison = field.BooleanValue.Value.CompareTo(right.Value);
        }
        else return false;
        passed = ApplyComparison(comparison, rule.ComparisonOperator);
        return true;
    }

    private static bool ApplyComparison(int comparison, string comparisonOperator) => NormalizeComparisonOperator(comparisonOperator) switch
    {
        "EQ" => comparison == 0,
        "NE" => comparison != 0,
        "GT" => comparison > 0,
        "GTE" => comparison >= 0,
        "LT" => comparison < 0,
        "LTE" => comparison <= 0,
        _ => false
    };

    private static string CanonicalRuleType(string ruleType, string fieldType, bool hasCompareField)
    {
        var code = Code(ruleType);
        return code switch
        {
            "PATTERN" => "REGEX",
            "MIN" or "MINIMUM" => IsTextField(fieldType) ? "MIN_LENGTH" : fieldType == "NUMBER" ? "MIN_NUMBER" : IsDateField(fieldType) ? "MIN_DATE" : "",
            "MAX" or "MAXIMUM" => IsTextField(fieldType) ? "MAX_LENGTH" : fieldType == "NUMBER" ? "MAX_NUMBER" : IsDateField(fieldType) ? "MAX_DATE" : "",
            "EQUALS" or "NOT_EQUALS" => hasCompareField ? "COMPARE_FIELD" : "COMPARE_VALUE",
            "SCALAR_COMPARE" => "COMPARE_VALUE",
            "CROSS_FIELD_COMPARE" => "COMPARE_FIELD",
            "REQUIRED" or "REGEX" or "EMAIL" or "PHONE" or "DATE" or "MIN_LENGTH" or "MAX_LENGTH" or "MIN_NUMBER" or "MAX_NUMBER" or "MIN_DATE" or "MAX_DATE" or "BOOLEAN_TRUE" or "COMPARE_VALUE" or "COMPARE_FIELD" => code,
            _ => ""
        };
    }

    private static string NormalizeComparisonOperator(string value) => (value ?? "").Trim().ToUpperInvariant() switch
    {
        "=" or "==" or "EQ" or "EQUAL" or "EQUALS" => "EQ",
        "!=" or "<>" or "NE" or "NOT_EQUAL" or "NOT_EQUALS" => "NE",
        ">" or "GT" => "GT",
        ">=" or "GTE" => "GTE",
        "<" or "LT" => "LT",
        "<=" or "LTE" => "LTE",
        "MATCHES" => "MATCHES",
        _ => ""
    };

    private static bool ValidComparisonOperator(string comparisonOperator, string fieldType)
    {
        comparisonOperator = NormalizeComparisonOperator(comparisonOperator);
        if (comparisonOperator is "EQ" or "NE") return IsScalarField(fieldType);
        return (comparisonOperator is "GT" or "GTE" or "LT" or "LTE") && (fieldType == "NUMBER" || IsDateField(fieldType));
    }

    private static bool NormalizeScalarConstant(DynamicFormValidationRule normalized, DynamicFormValidationRule source, string fieldType)
    {
        ClearRuleOperands(normalized);
        if (IsTextField(fieldType))
        {
            if (source.TextValue is null || source.IntegerValue.HasValue || source.DecimalValue.HasValue || source.DateValue.HasValue || source.BooleanValue.HasValue) return false;
            normalized.TextValue = source.TextValue;
            return normalized.TextValue.Length <= 500;
        }
        if (fieldType == "NUMBER")
        {
            if (!TryReadRuleDecimal(source, out var number)) return false;
            normalized.DecimalValue = number;
            return true;
        }
        if (IsDateField(fieldType))
        {
            if (!TryReadRuleDate(source, out var date)) return false;
            normalized.DateValue = date;
            return true;
        }
        if (fieldType == "CHECKBOX")
        {
            if (!TryReadRuleBoolean(source, out var boolean)) return false;
            normalized.BooleanValue = boolean;
            return true;
        }
        return false;
    }

    private static bool TryReadRuleInteger(DynamicFormValidationRule rule, out long value)
    {
        value = 0;
        var text = rule.TextValue?.Trim();
        var supplied = (rule.IntegerValue.HasValue ? 1 : 0) + (rule.DecimalValue.HasValue ? 1 : 0) + (!string.IsNullOrWhiteSpace(text) ? 1 : 0);
        if (supplied != 1 || rule.DateValue.HasValue || rule.BooleanValue.HasValue) return false;
        if (rule.IntegerValue.HasValue) { value = rule.IntegerValue.Value; return true; }
        if (rule.DecimalValue.HasValue && decimal.Truncate(rule.DecimalValue.Value) == rule.DecimalValue.Value
            && rule.DecimalValue.Value >= long.MinValue && rule.DecimalValue.Value <= long.MaxValue) { value = (long)rule.DecimalValue.Value; return true; }
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryReadRuleDecimal(DynamicFormValidationRule rule, out decimal value)
    {
        value = 0;
        var text = rule.TextValue?.Trim();
        var supplied = (rule.IntegerValue.HasValue ? 1 : 0) + (rule.DecimalValue.HasValue ? 1 : 0) + (!string.IsNullOrWhiteSpace(text) ? 1 : 0);
        if (supplied != 1 || rule.DateValue.HasValue || rule.BooleanValue.HasValue) return false;
        if (rule.DecimalValue.HasValue) { value = rule.DecimalValue.Value; return true; }
        if (rule.IntegerValue.HasValue) { value = rule.IntegerValue.Value; return true; }
        return decimal.TryParse(text, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryReadRuleDate(DynamicFormValidationRule rule, out DateTime value)
    {
        value = default;
        var text = rule.TextValue?.Trim();
        var supplied = (rule.DateValue.HasValue ? 1 : 0) + (!string.IsNullOrWhiteSpace(text) ? 1 : 0);
        if (supplied != 1 || rule.IntegerValue.HasValue || rule.DecimalValue.HasValue || rule.BooleanValue.HasValue) return false;
        if (rule.DateValue.HasValue) { value = rule.DateValue.Value; return true; }
        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out value);
    }

    private static bool TryReadRuleBoolean(DynamicFormValidationRule rule, out bool value)
    {
        value = false;
        var text = rule.TextValue?.Trim();
        var supplied = (rule.BooleanValue.HasValue ? 1 : 0) + (!string.IsNullOrWhiteSpace(text) ? 1 : 0);
        if (supplied != 1 || rule.IntegerValue.HasValue || rule.DecimalValue.HasValue || rule.DateValue.HasValue) return false;
        if (rule.BooleanValue.HasValue) { value = rule.BooleanValue.Value; return true; }
        return bool.TryParse(text, out value);
    }

    private static void ClearRuleOperands(DynamicFormValidationRule rule)
    {
        rule.TextValue = null;
        rule.IntegerValue = null;
        rule.DecimalValue = null;
        rule.DateValue = null;
        rule.BooleanValue = null;
    }

    private static bool HasRuleValue(SubmissionFieldState field) => field.TypeCode switch
    {
        "TEXT" or "TEXTAREA" or "EMAIL" or "PHONE" => !string.IsNullOrWhiteSpace(field.TextValue),
        "NUMBER" => field.IntegerValue.HasValue || field.DecimalValue.HasValue,
        "DATE" => field.DateValue.HasValue,
        "DATETIME" => field.DateTimeValue.HasValue,
        "CHECKBOX" => field.BooleanValue.HasValue,
        "RADIO" or "SEARCH_SELECT" or "MULTI_SELECT" => field.SelectedOptionCount > 0 || field.LookupValueCount > 0,
        "UPLOAD" => field.AttachmentCount > 0,
        _ => false
    };

    private static bool TryGetRuleNumber(SubmissionFieldState field, out decimal value)
    {
        if (field.DecimalValue.HasValue) { value = field.DecimalValue.Value; return true; }
        if (field.IntegerValue.HasValue) { value = field.IntegerValue.Value; return true; }
        value = 0;
        return false;
    }

    private static bool TryGetRuleDate(SubmissionFieldState field, out DateTime value)
    {
        if (field.TypeCode == "DATETIME" && field.DateTimeValue.HasValue) { value = field.DateTimeValue.Value; return true; }
        if (field.DateValue.HasValue) { value = field.DateValue.Value; return true; }
        value = default;
        return false;
    }

    private static bool IsTextField(string typeCode) => typeCode is "TEXT" or "TEXTAREA" or "EMAIL" or "PHONE";
    private static bool IsDateField(string typeCode) => typeCode is "DATE" or "DATETIME";
    private static bool IsScalarField(string typeCode) => IsTextField(typeCode) || typeCode == "NUMBER" || IsDateField(typeCode) || typeCode == "CHECKBOX";
    private static bool ScalarTypesCompatible(string left, string right) => (IsTextField(left) && IsTextField(right)) || (left == "NUMBER" && right == "NUMBER") || (IsDateField(left) && IsDateField(right)) || (left == "CHECKBOX" && right == "CHECKBOX");
    private static string RuleError(DynamicFormValidationRule rule, string fallback) => string.IsNullOrWhiteSpace(rule.ErrorMessage) ? fallback : rule.ErrorMessage.Trim();
    private static string RuleConfigurationRuntimeError(string label) => $"The configured validation for '{label}' is invalid. Contact HR.";

    public async Task<(PublicUploadAuthorization? Authorization, string Error)> AuthorizeUploadAsync(string token, long fieldId)
    {
        if (string.IsNullOrWhiteSpace(token)) return (null, "Application session is invalid or expired.");
        await using var db = Db();
        await db.OpenAsync();
        var session = await ValidateSessionAsync(db, token, false);
        if (session is null) return (null, "Application session is invalid or expired.");
        var row = await db.QueryFirstOrDefaultAsync<PublicUploadAuthorization>(@"SELECT s.Id SubmissionId,s.ExternalSubjectId,s.ClientId,f.Id FieldId,f.AttachmentFieldConfigurationId,
cfg.allow_multiple AllowMultiple,cfg.maximum_file_count MaximumFileCount,cfg.maximum_file_size_bytes MaximumFileSizeBytes,cfg.maximum_total_size_bytes MaximumTotalSizeBytes
FROM form_submissions s JOIN form_fields f ON f.FormVersionId=s.FormVersionId JOIN form_field_types t ON t.Id=f.FieldTypeId
JOIN attachment_field_configurations cfg ON cfg.id=f.AttachmentFieldConfigurationId AND cfg.is_active=TRUE AND cfg.client_id IN (0,s.ClientId)
AND (cfg.effective_from_utc IS NULL OR cfg.effective_from_utc<=UTC_TIMESTAMP(6)) AND (cfg.effective_until_utc IS NULL OR cfg.effective_until_utc>=UTC_TIMESTAMP(6))
WHERE s.Id=@SubmissionId AND f.Id=@FieldId AND f.IsActive=TRUE AND t.TypeCode='UPLOAD' AND f.AttachmentFieldConfigurationId IS NOT NULL", new { session.SubmissionId, FieldId = fieldId });
        return row is null ? (null, "This field is not an active upload field.") : (row, "");
    }

    public async Task LinkAttachmentAsync(string token, long fieldId, long attachmentId, Guid publicId, string ipAddress, string userAgent)
    {
        if (string.IsNullOrWhiteSpace(token) || attachmentId <= 0 || fieldId <= 0 || publicId == Guid.Empty)
            throw new InvalidOperationException("Application session or attachment is invalid.");
        await using var db = Db();
        await db.OpenAsync();
        await using var transaction = await db.BeginTransactionAsync();
        try
        {
            var session = await db.QueryFirstOrDefaultAsync<PublicSessionRow>(SessionSql + " FOR UPDATE", new { TokenHash = Hash(token) }, transaction);
            if (!SessionValid(session)) throw new InvalidOperationException("Application session is invalid or expired.");
            var authorization = await db.QueryFirstOrDefaultAsync<PublicUploadAuthorization>(@"SELECT s.Id SubmissionId,s.ExternalSubjectId,s.ClientId,f.Id FieldId,f.AttachmentFieldConfigurationId,
cfg.allow_multiple AllowMultiple,cfg.maximum_file_count MaximumFileCount,cfg.maximum_file_size_bytes MaximumFileSizeBytes,cfg.maximum_total_size_bytes MaximumTotalSizeBytes
FROM form_submissions s JOIN form_fields f ON f.FormVersionId=s.FormVersionId JOIN form_field_types t ON t.Id=f.FieldTypeId
JOIN attachment_field_configurations cfg ON cfg.id=f.AttachmentFieldConfigurationId AND cfg.is_active=TRUE AND cfg.client_id IN (0,s.ClientId)
AND (cfg.effective_from_utc IS NULL OR cfg.effective_from_utc<=UTC_TIMESTAMP(6)) AND (cfg.effective_until_utc IS NULL OR cfg.effective_until_utc>=UTC_TIMESTAMP(6))
WHERE s.Id=@SubmissionId AND f.Id=@FieldId AND f.IsActive=TRUE AND t.TypeCode='UPLOAD' AND f.AttachmentFieldConfigurationId IS NOT NULL", new { session!.SubmissionId, FieldId = fieldId }, transaction);
            if (authorization is null) throw new InvalidOperationException("This field is not an active upload field.");
            var attachment = await db.QueryFirstOrDefaultAsync<PublicAttachmentLinkRow>(@"SELECT id Id,client_id ClientId,field_configuration_id FieldConfigurationId,entity_type EntityType,entity_id EntityId,file_size_bytes FileSizeBytes
FROM entity_attachments
WHERE id=@AttachmentId AND public_id=@PublicId AND client_id=@ClientId AND field_configuration_id=@FieldConfigurationId
AND entity_type='FORM_SUBMISSION' AND entity_id=@SubmissionId AND is_current=TRUE AND is_deleted=FALSE FOR UPDATE", new
            {
                AttachmentId = attachmentId,
                PublicId = publicId.ToString(),
                authorization.ClientId,
                FieldConfigurationId = authorization.AttachmentFieldConfigurationId,
                authorization.SubmissionId
            }, transaction);
            if (attachment is null) throw new InvalidOperationException("The uploaded file does not belong to this application field.");
            if (authorization.MaximumFileSizeBytes > 0 && attachment.FileSizeBytes > authorization.MaximumFileSizeBytes)
                throw new InvalidOperationException("The uploaded file exceeds this field's configured size limit.");
            var alreadyLinked = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM form_submission_attachments
WHERE SubmissionId=@SubmissionId AND FieldId=@FieldId AND AttachmentId=@AttachmentId", new { authorization.SubmissionId, authorization.FieldId, AttachmentId = attachmentId }, transaction);
            if (alreadyLinked == 0)
            {
                var currentCount = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM form_submission_attachments WHERE SubmissionId=@SubmissionId AND FieldId=@FieldId", new { authorization.SubmissionId, authorization.FieldId }, transaction);
                var maximum = authorization.AllowMultiple ? Math.Max(1, authorization.MaximumFileCount) : 1;
                if (currentCount >= maximum) throw new InvalidOperationException($"A maximum of {maximum} file(s) can be uploaded for this field.");
                if (authorization.MaximumTotalSizeBytes.HasValue)
                {
                    var currentBytes = await db.ExecuteScalarAsync<long>(@"SELECT COALESCE(SUM(a.file_size_bytes),0) FROM form_submission_attachments sa
JOIN entity_attachments a ON a.id=sa.AttachmentId WHERE sa.SubmissionId=@SubmissionId AND sa.FieldId=@FieldId", new { authorization.SubmissionId, authorization.FieldId }, transaction);
                    if (currentBytes + attachment.FileSizeBytes > authorization.MaximumTotalSizeBytes.Value)
                        throw new InvalidOperationException("The uploaded files exceed this field's combined size limit.");
                }
                await db.ExecuteAsync(@"INSERT INTO form_submission_attachments (SubmissionId,FieldId,AttachmentId,AttachmentPublicId)
VALUES (@SubmissionId,@FieldId,@AttachmentId,@PublicId)", new { authorization.SubmissionId, authorization.FieldId, AttachmentId = attachmentId, PublicId = publicId.ToString() }, transaction);
                await db.ExecuteAsync(@"UPDATE entity_attachments SET uploaded_by_external_subject_id=@SubjectId
WHERE id=@AttachmentId AND entity_type='FORM_SUBMISSION' AND entity_id=@SubmissionId", new { SubjectId = session.ExternalSubjectId, AttachmentId = attachmentId, authorization.SubmissionId }, transaction);
                await EventAsync(db, transaction, session.SubmissionId, "FILE_UPLOADED", "A configured application document was uploaded.", session.ExternalSubjectId, ipAddress, userAgent);
            }
            await TouchSessionAsync(db, transaction, session.Id);
            await transaction.CommitAsync();
        }
        catch (Exception exception)
        {
            await transaction.RollbackAsync();
            if (exception is InvalidOperationException) throw;
            throw new InvalidOperationException("The uploaded file could not be linked to this application.");
        }
    }

    public async Task<(PublicApplicationResult? Result, string Error)> SubmitPublicApplicationAsync(string token, string ipAddress, string userAgent)
    {
        if (string.IsNullOrWhiteSpace(token)) return (null, "Application session is invalid or expired.");
        await using var db = Db();
        await db.OpenAsync();
        await using var transaction = await db.BeginTransactionAsync();
        try
        {
            var session = await db.QueryFirstOrDefaultAsync<PublicSessionRow>(SessionSql + " FOR UPDATE", new { TokenHash = Hash(token) }, transaction);
            if (session is null || !session.Purpose.Equals("APPLICATION", StringComparison.OrdinalIgnoreCase)
                || session.ExpiresAtUtc <= DateTime.UtcNow || session.UseCount >= session.MaximumUses)
                return (null, "Application session is invalid or expired.");
            var submission = await db.QueryFirstOrDefaultAsync<SubmissionPostingRow>(@"SELECT s.Id SubmissionId,s.FormVersionId,s.ClientId,s.Status,s.CandidateId,s.ApplicationId,
j.Id PostingId,j.PositionId,j.ApplicationCount,j.MaximumApplications,j.Status PostingStatus,j.OpensAtUtc,j.ClosesAtUtc,COALESCE(position.RecruiterUserId,0) RecruiterUserId
FROM form_submissions s JOIN recruitment_job_postings j ON j.Id=@PostingId AND j.ClientId=s.ClientId AND j.ApplicationFormVersionId=s.FormVersionId
JOIN recruitment_settings settings ON settings.ClientId=s.ClientId AND settings.RecruitmentEnabled=TRUE AND settings.EnableCandidatePortal=TRUE AND settings.IsActive=TRUE
JOIN recruitment_open_positions position ON position.Id=j.PositionId
WHERE s.Id=@SubmissionId FOR UPDATE", new { session.PostingId, session.SubmissionId }, transaction);
            if (submission is null) return (null, "Application session is invalid or expired.");
            if (submission.Status == "Submitted" && submission.ApplicationId.HasValue)
            {
                var existingCode = await db.ExecuteScalarAsync<string>("SELECT ApplicationCode FROM recruitment_candidate_applications WHERE Id=@Id", new { Id = submission.ApplicationId.Value }, transaction) ?? "";
                return (new PublicApplicationResult { SubmissionId = submission.SubmissionId, CandidateId = submission.CandidateId ?? 0, ApplicationId = submission.ApplicationId.Value, ApplicationCode = existingCode }, "");
            }
            if (!SessionValid(session)) return (null, "Application session is invalid or expired.");
            if (submission.PostingStatus != "Published"
                || (submission.OpensAtUtc.HasValue && submission.OpensAtUtc > DateTime.UtcNow)
                || (submission.ClosesAtUtc.HasValue && submission.ClosesAtUtc < DateTime.UtcNow)
                || (submission.MaximumApplications.HasValue && submission.ApplicationCount >= submission.MaximumApplications))
                return (null, "This job is no longer accepting applications.");
            var requiredError = await RequiredSubmissionErrorAsync(db, transaction, submission.SubmissionId, submission.FormVersionId);
            if (requiredError.Length > 0) return (null, requiredError);
            var ruleError = await SubmissionRuleErrorAsync(db, transaction, submission.SubmissionId, true, null);
            if (ruleError.Length > 0) return (null, ruleError);
            var semantic = (await db.QueryAsync<SemanticValueRow>(@"SELECT a.SemanticCode,v.TextValue,v.IntegerValue,v.DecimalValue,v.DateValue,v.DateTimeValue,v.BooleanValue
FROM form_field_semantic_mappings m JOIN form_semantic_attributes a ON a.Id=m.SemanticAttributeId JOIN form_submission_values v ON v.FieldId=m.FieldId AND v.SubmissionId=@SubmissionId", new { submission.SubmissionId }, transaction)).ToList();
            string S(string code) => semantic.FirstOrDefault(row => row.SemanticCode == code)?.TextValue?.Trim() ?? "";
            var subject = await db.QueryFirstAsync<ExternalSubjectRow>("SELECT Id,CandidateId,Email,NormalizedEmail,Phone,NormalizedPhone FROM external_portal_subjects WHERE Id=@Id FOR UPDATE", new { Id = session.ExternalSubjectId }, transaction);
            var firstName = S("FIRST_NAME");
            var lastName = S("LAST_NAME");
            var email = S("EMAIL"); if (email.Length == 0) email = subject.Email;
            var phone = S("PHONE"); if (phone.Length == 0) phone = subject.Phone;
            if (firstName.Length == 0) return (null, "Candidate first name is required by the published form.");
            if (email.Length > 0 && !ValidEmail(email)) return (null, "Enter a valid email address.");
            var normalizedEmail = NormalizeEmail(email);
            var normalizedPhone = NormalizePhone(phone);
            if (normalizedEmail.Length == 0 && normalizedPhone.Length == 0) return (null, "Candidate email or phone is required.");
            if (subject.NormalizedEmail.Length > 0 && normalizedEmail.Length > 0 && subject.NormalizedEmail != normalizedEmail)
                return (null, "The application email does not match the email used to start this session.");
            if (subject.NormalizedPhone.Length > 0 && normalizedPhone.Length > 0 && subject.NormalizedPhone != normalizedPhone)
                return (null, "The application phone does not match the phone used to start this session.");
            long? candidateId = subject.CandidateId;
            if (candidateId.HasValue)
            {
                var candidateBelongsToClient = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_candidates WHERE Id=@Id AND ClientId=@ClientId", new { Id = candidateId.Value, submission.ClientId }, transaction);
                if (candidateBelongsToClient == 0) return (null, "The applicant identity is invalid for this job.");
            }
            else
            {
                var matchingCandidateIds = (await db.QueryAsync<long>(@"SELECT Id FROM recruitment_candidates
WHERE ClientId=@ClientId AND ((@Email<>'' AND NormalizedEmail=@Email) OR (@Phone<>'' AND NormalizedPhone=@Phone))
ORDER BY Id FOR UPDATE", new { submission.ClientId, Email = normalizedEmail, Phone = normalizedPhone }, transaction)).Distinct().ToList();
                if (matchingCandidateIds.Count > 1)
                    return (null, "The supplied email and phone match different candidate profiles. Contact HR before applying.");
                candidateId = matchingCandidateIds.SingleOrDefault();
                if (candidateId == 0) candidateId = null;
            }
            if (!candidateId.HasValue)
            {
                var candidateCode = await NextNumberAsync(db, transaction, submission.ClientId, "CAN", "CAN");
                candidateId = await db.ExecuteScalarAsync<long>(@"INSERT INTO recruitment_candidates (CandidateCode,ClientId,FirstName,LastName,Email,NormalizedEmail,Phone,NormalizedPhone,CurrentCompany,CurrentTitle,TotalExperienceMonths,CurrentLocation,PreferredLocationsJson,NoticePeriodDays,CurrentCtc,ExpectedCtc,HighestQualification,SourceType,SourceReferenceId,ProfileStatus,ConsentStatus,ConsentCapturedAt,RetentionUntil,CreatedByUserId)
VALUES (@Code,@ClientId,@FirstName,@LastName,@Email,@NormalizedEmail,@Phone,@NormalizedPhone,'','',0,'',JSON_ARRAY(),0,0,0,'','Public Job',@PostingId,'Active','Granted',UTC_TIMESTAMP(6),DATE_ADD(UTC_DATE(),INTERVAL 24 MONTH),0);SELECT LAST_INSERT_ID();", new { Code = candidateCode, ClientId = submission.ClientId, FirstName = firstName, LastName = lastName, Email = email, NormalizedEmail = normalizedEmail, Phone = phone, NormalizedPhone = normalizedPhone, submission.PostingId }, transaction);
            }
            await db.ExecuteAsync(@"UPDATE external_portal_subjects SET CandidateId=@CandidateId,
Email=CASE WHEN NormalizedEmail='' THEN @Email ELSE Email END,NormalizedEmail=CASE WHEN NormalizedEmail='' THEN @NormalizedEmail ELSE NormalizedEmail END,
Phone=CASE WHEN NormalizedPhone='' THEN @Phone ELSE Phone END,NormalizedPhone=CASE WHEN NormalizedPhone='' THEN @NormalizedPhone ELSE NormalizedPhone END
WHERE Id=@Id", new { CandidateId = candidateId.Value, Email = email, NormalizedEmail = normalizedEmail, Phone = phone, NormalizedPhone = normalizedPhone, Id = subject.Id }, transaction);
            var existingApplication = await db.QueryFirstOrDefaultAsync<ApplicationIdentityRow>("SELECT Id,ApplicationCode,JobPostingId FROM recruitment_candidate_applications WHERE CandidateId=@CandidateId AND PositionId=@PositionId", new { CandidateId = candidateId.Value, submission.PositionId }, transaction);
            long applicationId;
            string applicationCode;
            if (existingApplication is not null)
            {
                return (null, $"You have already applied for this position under application {existingApplication.ApplicationCode}.");
            }
            else
            {
                applicationCode = await NextNumberAsync(db, transaction, submission.ClientId, "APP", "APP");
                applicationId = await db.ExecuteScalarAsync<long>(@"INSERT INTO recruitment_candidate_applications (ApplicationCode,CandidateId,PositionId,ClientId,SourceType,SourceReferenceId,JobPostingId,CurrentStatus,CurrentStage,RecruiterUserId,AppliedAt,LastStageChangedAt)
VALUES (@Code,@CandidateId,@PositionId,@ClientId,'Public Job',@PostingId,@PostingId,'New','New',NULLIF(@RecruiterUserId,0),UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));SELECT LAST_INSERT_ID();", new { Code = applicationCode, CandidateId = candidateId.Value, submission.PositionId, submission.ClientId, submission.PostingId, submission.RecruiterUserId }, transaction);
                await db.ExecuteAsync("INSERT INTO recruitment_application_stage_history (ApplicationId,FromStage,ToStage,Reason,ChangedByUserId,MetadataJson) VALUES (@ApplicationId,'','New','Public application submitted.',0,JSON_OBJECT())", new { ApplicationId = applicationId }, transaction);
                await db.ExecuteAsync("UPDATE recruitment_job_postings SET ApplicationCount=ApplicationCount+1 WHERE Id=@Id;UPDATE recruitment_open_positions SET CandidateCount=CandidateCount+1 WHERE Id=@PositionId", new { Id = submission.PostingId, submission.PositionId }, transaction);
            }
            await PromoteSubmissionAttachmentsAsync(db, transaction, submission.SubmissionId, candidateId.Value, applicationId, session.ExternalSubjectId);
            await db.ExecuteAsync("UPDATE form_submissions SET CandidateId=@CandidateId,ApplicationId=@ApplicationId,EntityType='CANDIDATE',EntityId=@CandidateId,Status='Submitted',SubmittedAtUtc=UTC_TIMESTAMP(6) WHERE Id=@SubmissionId;UPDATE form_public_sessions SET RevokedAtUtc=UTC_TIMESTAMP(6),LastUsedAtUtc=UTC_TIMESTAMP(6),UseCount=UseCount+1 WHERE Id=@SessionId", new { CandidateId = candidateId.Value, ApplicationId = applicationId, submission.SubmissionId, SessionId = session.Id }, transaction);
            await EventAsync(db, transaction, submission.SubmissionId, "SUBMITTED", $"Application {applicationCode} submitted.", session.ExternalSubjectId, ipAddress, userAgent);
            await transaction.CommitAsync();
            return (new PublicApplicationResult { SubmissionId = submission.SubmissionId, CandidateId = candidateId.Value, ApplicationId = applicationId, ApplicationCode = applicationCode }, "");
        }
        catch (Exception exception)
        {
            await transaction.RollbackAsync();
            logger.LogError(exception, "Public recruitment submission failed for token hash {TokenHash}.", Hash(token));
            return (null, exception is InvalidOperationException ? exception.Message : "The application could not be submitted. Please retry.");
        }
    }

    private static async Task PopulateVersionAsync(MySqlConnection db, DynamicFormVersion version)
    {
        version.Sections = (await db.QueryAsync<DynamicFormSection>("SELECT * FROM form_sections WHERE FormVersionId=@Id ORDER BY DisplayOrder", new { Id = version.Id })).ToList();
        var fields = (await db.QueryAsync<DynamicFormField>(@"SELECT f.Id,f.FormVersionId,f.SectionId,t.TypeCode FieldTypeCode,f.StableFieldCode,f.Label,f.Placeholder,f.HelpText,f.IsRequired,f.DisplayOrder,f.WidthColumns,f.MinimumLength,f.MaximumLength,f.MinimumNumber,f.MaximumNumber,f.MinimumDate,f.MaximumDate,f.AttachmentFieldConfigurationId,COALESCE(l.SourceCode,'') LookupSourceCode,f.IsActive,
COALESCE(cfg.allow_multiple,FALSE) AllowMultipleFiles,COALESCE(cfg.maximum_file_count,1) MaximumFileCount,
COALESCE(cfg.maximum_file_size_bytes,0) MaximumFileSizeBytes,cfg.maximum_total_size_bytes MaximumTotalSizeBytes,
COALESCE(CAST(cfg.allowed_extensions_json AS CHAR),'[]') AllowedExtensionsJson,COALESCE(CAST(cfg.allowed_mime_types_json AS CHAR),'[]') AllowedMimeTypesJson
FROM form_fields f JOIN form_versions v ON v.Id=f.FormVersionId JOIN form_definitions d ON d.Id=v.FormDefinitionId
JOIN form_field_types t ON t.Id=f.FieldTypeId
LEFT JOIN form_lookup_sources l ON l.Id=f.LookupSourceId
LEFT JOIN attachment_field_configurations cfg ON cfg.id=f.AttachmentFieldConfigurationId AND cfg.is_active=TRUE AND cfg.client_id IN (0,d.ClientId)
 AND (cfg.effective_from_utc IS NULL OR cfg.effective_from_utc<=UTC_TIMESTAMP(6))
 AND (cfg.effective_until_utc IS NULL OR cfg.effective_until_utc>=UTC_TIMESTAMP(6))
WHERE f.FormVersionId=@Id ORDER BY f.DisplayOrder", new { Id = version.Id })).ToList();
        if (fields.Count > 0)
        {
            var ids = fields.Select(row => row.Id).ToArray();
            var options = await db.QueryAsync<DynamicFormFieldOption>("SELECT Id,FieldId,OptionCode,OptionLabel,DisplayOrder,IsActive FROM form_field_options WHERE FieldId IN @Ids ORDER BY DisplayOrder", new { Ids = ids });
            var semantics = await db.QueryAsync<FieldSemanticRow>("SELECT m.FieldId,a.SemanticCode FROM form_field_semantic_mappings m JOIN form_semantic_attributes a ON a.Id=m.SemanticAttributeId WHERE m.FieldId IN @Ids", new { Ids = ids });
            var rules = await db.QueryAsync<DynamicFormValidationRule>(@"SELECT r.*,COALESCE(compareField.StableFieldCode,'') CompareFieldCode
FROM form_field_validation_rules r
LEFT JOIN form_fields compareField ON compareField.Id=r.CompareFieldId
WHERE r.FieldId IN @Ids ORDER BY r.DisplayOrder,r.Id", new { Ids = ids });
            foreach (var field in fields)
            {
                field.Options = options.Where(row => row.FieldId == field.Id).ToList();
                field.SemanticCodes = semantics.Where(row => row.FieldId == field.Id).Select(row => row.SemanticCode).ToList();
                field.ValidationRules = rules.Where(row => row.FieldId == field.Id).ToList();
                if (field.FieldTypeCode == "UPLOAD" && field.AttachmentFieldConfigurationId.HasValue && field.MaximumFileSizeBytes > 0)
                {
                    field.AttachmentConstraints = new PublicAttachmentConstraints
                    {
                        AllowMultiple = field.AllowMultipleFiles,
                        MaximumFileCount = field.AllowMultipleFiles ? Math.Max(1, field.MaximumFileCount) : 1,
                        MaximumFileSizeBytes = field.MaximumFileSizeBytes,
                        MaximumTotalSizeBytes = field.MaximumTotalSizeBytes,
                        AllowedExtensions = ReadStringList(field.AllowedExtensionsJson),
                        AllowedMimeTypes = ReadStringList(field.AllowedMimeTypesJson)
                    };
                }
            }
        }
        foreach (var section in version.Sections)
            section.Fields = fields.Where(row => row.SectionId == section.Id).OrderBy(row => row.DisplayOrder).ToList();
    }

    private static void SanitizePublicVersion(DynamicFormVersion version)
    {
        foreach (var field in version.Sections.SelectMany(section => section.Fields))
        {
            field.LookupSourceCode = "";
            field.AttachmentFieldConfigurationId = null;
        }
    }

    private static async Task SeedCatalogAsync(MySqlConnection db)
    {
        await db.ExecuteAsync(@"INSERT INTO form_field_types (TypeCode,TypeName,SupportsOptions,SupportsMultipleValues,SupportsAttachment,IsActive) VALUES
('TEXT','Text',FALSE,FALSE,FALSE,TRUE),('TEXTAREA','Long text',FALSE,FALSE,FALSE,TRUE),('NUMBER','Number',FALSE,FALSE,FALSE,TRUE),('DATE','Date',FALSE,FALSE,FALSE,TRUE),('DATETIME','Date and time',FALSE,FALSE,FALSE,TRUE),('EMAIL','Email',FALSE,FALSE,FALSE,TRUE),('PHONE','Phone',FALSE,FALSE,FALSE,TRUE),('SEARCH_SELECT','Searchable dropdown',TRUE,FALSE,FALSE,TRUE),('MULTI_SELECT','Searchable multi-select',TRUE,TRUE,FALSE,TRUE),('RADIO','Radio',TRUE,FALSE,FALSE,TRUE),('CHECKBOX','Checkbox',FALSE,FALSE,FALSE,TRUE),('UPLOAD','Upload',FALSE,TRUE,TRUE,TRUE)
ON DUPLICATE KEY UPDATE TypeName=VALUES(TypeName),SupportsOptions=VALUES(SupportsOptions),SupportsMultipleValues=VALUES(SupportsMultipleValues),SupportsAttachment=VALUES(SupportsAttachment),IsActive=TRUE;
INSERT INTO form_semantic_attributes (SemanticCode,SemanticName,DataTypeCode,IsActive) VALUES
('FIRST_NAME','Candidate first name','TEXT',TRUE),('LAST_NAME','Candidate last name','TEXT',TRUE),('EMAIL','Candidate email','EMAIL',TRUE),('PHONE','Candidate phone','PHONE',TRUE),('RESUME','Primary resume','UPLOAD',TRUE),('CONSENT','Candidate consent','CHECKBOX',TRUE),('CURRENT_LOCATION','Current location','TEXT',TRUE),('CURRENT_COMPANY','Current company','TEXT',TRUE),('CURRENT_DESIGNATION','Current designation','TEXT',TRUE),('TOTAL_EXPERIENCE_MONTHS','Total experience in months','NUMBER',TRUE),('HIGHEST_QUALIFICATION','Highest qualification','TEXT',TRUE),('CERTIFICATIONS','Candidate certifications','TEXT',TRUE),('CURRENT_CTC','Current CTC','NUMBER',TRUE),('EXPECTED_CTC','Expected CTC','NUMBER',TRUE),('NOTICE_PERIOD_DAYS','Notice period in days','NUMBER',TRUE)
ON DUPLICATE KEY UPDATE SemanticName=VALUES(SemanticName),DataTypeCode=VALUES(DataTypeCode),IsActive=TRUE;
INSERT INTO form_lookup_sources (SourceCode,SourceName,ResolverCode,IsClientScoped,MinimumSearchLength,MaximumResults,IsActive) VALUES
('CLIENTS','Clients','CLIENTS',FALSE,0,100,TRUE),('DEPARTMENTS_BY_CLIENT','Departments','DEPARTMENTS_BY_CLIENT',TRUE,0,100,TRUE),('WORK_LOCATIONS_BY_CLIENT','Work locations','WORK_LOCATIONS_BY_CLIENT',TRUE,0,100,TRUE),('SKILLS','Recruitment skills','SKILLS',TRUE,1,100,TRUE),('EMPLOYEES_BY_CLIENT','Employees','EMPLOYEES_BY_CLIENT',TRUE,2,50,TRUE)
ON DUPLICATE KEY UPDATE SourceName=VALUES(SourceName),ResolverCode=VALUES(ResolverCode),IsClientScoped=VALUES(IsClientScoped),MinimumSearchLength=VALUES(MinimumSearchLength),MaximumResults=VALUES(MaximumResults),IsActive=TRUE;");
    }

    private static async Task EnsureIntegrationColumnsAsync(MySqlConnection db)
    {
        await EnsureColumnAsync(db, "external_portal_subjects", "CandidateId", "BIGINT NULL");
        var attachmentTableExists = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM information_schema.tables
WHERE table_schema=DATABASE() AND table_name='entity_attachments'");
        if (attachmentTableExists == 0) return;
        await EnsureColumnAsync(db, "entity_attachments", "uploaded_by_external_subject_id", "BIGINT NULL");
        var indexExists = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM information_schema.statistics
WHERE table_schema=DATABASE() AND table_name='entity_attachments' AND index_name='IX_entity_attachment_external_subject'");
        if (indexExists == 0)
            await db.ExecuteAsync("ALTER TABLE entity_attachments ADD INDEX IX_entity_attachment_external_subject (uploaded_by_external_subject_id)");
    }

    private static async Task EnsureColumnAsync(MySqlConnection db, string table, string column, string definition)
    {
        var exists = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM information_schema.columns
WHERE table_schema=DATABASE() AND table_name=@Table AND LOWER(column_name)=LOWER(@Column)", new { Table = table, Column = column });
        if (exists == 0) await db.ExecuteAsync($"ALTER TABLE `{table}` ADD COLUMN `{column}` {definition}");
    }

    private static async Task EnsureForeignKeysAsync(MySqlConnection db)
    {
        var keys = new (string Table, string Name, string Column, string Parent, string ParentColumn, string DeleteRule)[]
        {
            ("form_versions","FK_form_version_definition","FormDefinitionId","form_definitions","Id","CASCADE"),
            ("form_sections","FK_form_section_version","FormVersionId","form_versions","Id","CASCADE"),
            ("form_fields","FK_form_field_version","FormVersionId","form_versions","Id","RESTRICT"),
            ("form_fields","FK_form_field_section","SectionId","form_sections","Id","CASCADE"),
            ("form_fields","FK_form_field_type","FieldTypeId","form_field_types","Id","RESTRICT"),
            ("form_fields","FK_form_field_lookup","LookupSourceId","form_lookup_sources","Id","SET NULL"),
            ("form_fields","FK_form_field_attachment_config","AttachmentFieldConfigurationId","attachment_field_configurations","id","SET NULL"),
            ("form_field_options","FK_form_option_field","FieldId","form_fields","Id","CASCADE"),
            ("form_field_semantic_mappings","FK_form_semantic_field","FieldId","form_fields","Id","CASCADE"),
            ("form_field_semantic_mappings","FK_form_semantic_attribute","SemanticAttributeId","form_semantic_attributes","Id","RESTRICT"),
            ("form_field_validation_rules","FK_form_rule_field","FieldId","form_fields","Id","CASCADE"),
            ("form_field_validation_rules","FK_form_rule_compare_field","CompareFieldId","form_fields","Id","SET NULL"),
            ("external_portal_subjects","FK_external_subject_candidate","CandidateId","recruitment_candidates","Id","SET NULL"),
            ("form_submissions","FK_form_submission_version","FormVersionId","form_versions","Id","RESTRICT"),
            ("form_submissions","FK_form_submission_subject","ExternalSubjectId","external_portal_subjects","Id","RESTRICT"),
            ("form_submissions","FK_form_submission_candidate","CandidateId","recruitment_candidates","Id","SET NULL"),
            ("form_submissions","FK_form_submission_application","ApplicationId","recruitment_candidate_applications","Id","SET NULL"),
            ("form_public_sessions","FK_form_session_posting","PostingId","recruitment_job_postings","Id","RESTRICT"),
            ("form_public_sessions","FK_form_session_submission","SubmissionId","form_submissions","Id","CASCADE"),
            ("form_public_sessions","FK_form_session_subject","ExternalSubjectId","external_portal_subjects","Id","RESTRICT"),
            ("form_submission_values","FK_form_value_submission","SubmissionId","form_submissions","Id","CASCADE"),
            ("form_submission_values","FK_form_value_field","FieldId","form_fields","Id","RESTRICT"),
            ("form_submission_selected_options","FK_form_selected_submission","SubmissionId","form_submissions","Id","CASCADE"),
            ("form_submission_selected_options","FK_form_selected_field","FieldId","form_fields","Id","RESTRICT"),
            ("form_submission_selected_options","FK_form_selected_option","OptionId","form_field_options","Id","RESTRICT"),
            ("form_submission_lookup_values","FK_form_lookup_value_submission","SubmissionId","form_submissions","Id","CASCADE"),
            ("form_submission_lookup_values","FK_form_lookup_value_field","FieldId","form_fields","Id","RESTRICT"),
            ("form_submission_attachments","FK_form_attachment_submission","SubmissionId","form_submissions","Id","CASCADE"),
            ("form_submission_attachments","FK_form_attachment_field","FieldId","form_fields","Id","RESTRICT"),
            ("form_submission_attachments","FK_form_attachment_entity","AttachmentId","entity_attachments","id","RESTRICT"),
            ("form_submission_events","FK_form_event_submission","SubmissionId","form_submissions","Id","CASCADE"),
            ("form_submission_events","FK_form_event_subject","ExternalSubjectId","external_portal_subjects","Id","SET NULL")
        };
        foreach (var key in keys)
            await EnsureForeignKeyAsync(db, key.Table, key.Name, key.Column, key.Parent, key.ParentColumn, key.DeleteRule);
    }

    private static async Task EnsureForeignKeyAsync(MySqlConnection db, string table, string constraint, string column, string parentTable, string parentColumn, string deleteRule)
    {
        var tablesExist = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM information_schema.tables
WHERE table_schema=DATABASE() AND table_name IN (@Table,@Parent)", new { Table = table, Parent = parentTable });
        if (tablesExist != 2) return;
        var exists = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM information_schema.table_constraints
WHERE constraint_schema=DATABASE() AND table_name=@Table AND constraint_name=@Constraint AND constraint_type='FOREIGN KEY'", new { Table = table, Constraint = constraint });
        if (exists > 0) return;
        var orphanCount = await db.ExecuteScalarAsync<long>($@"SELECT COUNT(*) FROM `{table}` child
LEFT JOIN `{parentTable}` parent ON parent.`{parentColumn}`=child.`{column}`
WHERE child.`{column}` IS NOT NULL AND parent.`{parentColumn}` IS NULL");
        if (orphanCount > 0) return;
        await db.ExecuteAsync($"ALTER TABLE `{table}` ADD CONSTRAINT `{constraint}` FOREIGN KEY (`{column}`) REFERENCES `{parentTable}` (`{parentColumn}`) ON DELETE {deleteRule}");
    }

    private static async Task<int> SubmissionClientIdAsync(MySqlConnection db, long submissionId, MySqlTransaction? transaction = null) =>
        await db.ExecuteScalarAsync<int>("SELECT ClientId FROM form_submissions WHERE Id=@Id", new { Id = submissionId }, transaction);

    private static async Task<List<DynamicLookupOption>> ResolvePublicLookupOptionsAsync(MySqlConnection db, MySqlTransaction? transaction, DynamicFormLookupSource source, int clientId, string search, string? exactValue)
    {
        var limit = Math.Clamp(source.MaximumResults, 1, 200);
        object args = new { ClientId = clientId, Search = (search ?? "").Trim(), Exact = (exactValue ?? "").Trim(), Limit = exactValue is null ? limit : 1 };
        IEnumerable<DynamicLookupOption> rows = source.ResolverCode switch
        {
            "CLIENTS" => await db.QueryAsync<DynamicLookupOption>(exactValue is null
                ? "SELECT CAST(Id AS CHAR) Value,Name Label FROM clients WHERE Id=@ClientId AND IsActive=TRUE AND (@Search='' OR Name LIKE CONCAT('%',@Search,'%')) LIMIT @Limit"
                : "SELECT CAST(Id AS CHAR) Value,Name Label FROM clients WHERE Id=@ClientId AND IsActive=TRUE AND CAST(Id AS CHAR)=@Exact LIMIT 1", args, transaction),
            "DEPARTMENTS_BY_CLIENT" => await db.QueryAsync<DynamicLookupOption>(exactValue is null
                ? "SELECT Department Value,Department Label FROM employees WHERE ClientId=@ClientId AND IsActive=TRUE AND Department<>'' AND (@Search='' OR Department LIKE CONCAT('%',@Search,'%')) GROUP BY Department ORDER BY Department LIMIT @Limit"
                : "SELECT Department Value,Department Label FROM employees WHERE ClientId=@ClientId AND IsActive=TRUE AND Department=@Exact GROUP BY Department LIMIT 1", args, transaction),
            "WORK_LOCATIONS_BY_CLIENT" => await db.QueryAsync<DynamicLookupOption>(exactValue is null
                ? "SELECT CAST(Id AS CHAR) Value,CONCAT(Name,CASE WHEN City='' THEN '' ELSE CONCAT(' - ',City) END) Label FROM worklocations WHERE ClientId=@ClientId AND IsActive=TRUE AND (@Search='' OR CONCAT(Name,' ',City,' ',State) LIKE CONCAT('%',@Search,'%')) ORDER BY Name LIMIT @Limit"
                : "SELECT CAST(Id AS CHAR) Value,CONCAT(Name,CASE WHEN City='' THEN '' ELSE CONCAT(' - ',City) END) Label FROM worklocations WHERE ClientId=@ClientId AND IsActive=TRUE AND CAST(Id AS CHAR)=@Exact LIMIT 1", args, transaction),
            "SKILLS" => await db.QueryAsync<DynamicLookupOption>(exactValue is null
                ? "SELECT CAST(Id AS CHAR) Value,SkillName Label FROM recruitment_skills WHERE (ClientId=0 OR ClientId=@ClientId) AND IsActive=TRUE AND (@Search='' OR SkillName LIKE CONCAT('%',@Search,'%')) ORDER BY SkillName LIMIT @Limit"
                : "SELECT CAST(Id AS CHAR) Value,SkillName Label FROM recruitment_skills WHERE (ClientId=0 OR ClientId=@ClientId) AND IsActive=TRUE AND CAST(Id AS CHAR)=@Exact LIMIT 1", args, transaction),
            _ => []
        };
        return rows.ToList();
    }

    private static async Task PromoteSubmissionAttachmentsAsync(MySqlConnection db, MySqlTransaction transaction, long submissionId, long candidateId, long applicationId, long externalSubjectId)
    {
        await db.ExecuteAsync(@"UPDATE entity_attachments a
JOIN form_submission_attachments sa ON sa.AttachmentId=a.id AND sa.SubmissionId=@SubmissionId
SET a.entity_type='CANDIDATE',a.entity_id=@CandidateId,a.uploaded_by_external_subject_id=@ExternalSubjectId
WHERE a.entity_type='FORM_SUBMISSION' AND a.entity_id=@SubmissionId AND a.is_deleted=FALSE", new { SubmissionId = submissionId, CandidateId = candidateId, ExternalSubjectId = externalSubjectId }, transaction);

        var resumePublicId = await db.ExecuteScalarAsync<Guid?>(@"SELECT sa.AttachmentPublicId
FROM form_submission_attachments sa
JOIN form_field_semantic_mappings m ON m.FieldId=sa.FieldId
JOIN form_semantic_attributes s ON s.Id=m.SemanticAttributeId AND s.SemanticCode='RESUME'
JOIN entity_attachments a ON a.id=sa.AttachmentId AND a.entity_type='CANDIDATE' AND a.entity_id=@CandidateId AND a.is_current=TRUE AND a.is_deleted=FALSE
WHERE sa.SubmissionId=@SubmissionId ORDER BY sa.Id DESC LIMIT 1", new { SubmissionId = submissionId, CandidateId = candidateId }, transaction);
        if (!resumePublicId.HasValue) return;
        var resumePublicIdText = resumePublicId.Value.ToString();
        var resumeId = await db.ExecuteScalarAsync<long?>("SELECT Id FROM recruitment_candidate_resumes WHERE AttachmentPublicId=@PublicId AND CandidateId=@CandidateId", new { PublicId = resumePublicIdText, CandidateId = candidateId }, transaction);
        if (!resumeId.HasValue)
        {
            var linkedToAnotherCandidate = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_candidate_resumes WHERE AttachmentPublicId=@PublicId AND CandidateId<>@CandidateId", new { PublicId = resumePublicIdText, CandidateId = candidateId }, transaction);
            if (linkedToAnotherCandidate > 0) throw new InvalidOperationException("The uploaded resume is already linked to another candidate profile.");
        }
        if (!resumeId.HasValue)
        {
            await db.ExecuteAsync("UPDATE recruitment_candidate_resumes SET IsPrimary=FALSE WHERE CandidateId=@CandidateId", new { CandidateId = candidateId }, transaction);
            var version = await db.ExecuteScalarAsync<int>("SELECT COALESCE(MAX(VersionNumber),0)+1 FROM recruitment_candidate_resumes WHERE CandidateId=@CandidateId", new { CandidateId = candidateId }, transaction);
            resumeId = await db.ExecuteScalarAsync<long>(@"INSERT INTO recruitment_candidate_resumes
(CandidateId,AttachmentPublicId,VersionNumber,IsPrimary,ParsingStatus,ParsedText,ParsedJson,ParserName,ParserVersion,ParsingError)
VALUES (@CandidateId,@PublicId,@Version,TRUE,'Pending','',JSON_OBJECT(),'','','');SELECT LAST_INSERT_ID();", new { CandidateId = candidateId, PublicId = resumePublicIdText, Version = version }, transaction);
        }
        await db.ExecuteAsync("UPDATE recruitment_candidate_applications SET ResumeId=@ResumeId WHERE Id=@ApplicationId", new { ResumeId = resumeId.Value, ApplicationId = applicationId }, transaction);
    }

    private static int? EffectiveScope(AuthUser user, int? requested) => user.ClientId ?? requested;
    private static string ActiveStatus(string value) => value.Equals("Inactive", StringComparison.OrdinalIgnoreCase) ? "Inactive" : "Active";
    private static string Code(string value) => string.Join("_", (value ?? "").Trim().ToUpperInvariant().Split([' ', '-', '/', '\\'], StringSplitOptions.RemoveEmptyEntries));
    private static string NormalizeEmail(string value) => (value ?? "").Trim().ToLowerInvariant();
    private static string NormalizePhone(string value) => new((value ?? "").Where(char.IsDigit).ToArray());
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string RandomToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string Truncate(string value, int max) => string.IsNullOrEmpty(value) ? "" : value.Length <= max ? value : value[..max];

    private static List<string> ReadStringList(string value)
    {
        try { return JsonSerializer.Deserialize<List<string>>(string.IsNullOrWhiteSpace(value) ? "[]" : value) ?? []; }
        catch { return []; }
    }

    private static bool ValidEmail(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 190) return false;
        var at = value.IndexOf('@');
        return at > 0 && at == value.LastIndexOf('@') && at < value.Length - 3 && value.IndexOf('.', at + 2) > at + 1;
    }

    private static bool ValidPublicSlug(string value) => value.Length == 32 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool HasScalarValue(string typeCode, PublicFormValue value) => typeCode switch
    {
        "TEXT" or "TEXTAREA" or "EMAIL" or "PHONE" => !string.IsNullOrWhiteSpace(value.TextValue),
        "NUMBER" => value.IntegerValue.HasValue || value.DecimalValue.HasValue,
        "DATE" => value.DateValue.HasValue,
        "DATETIME" => value.DateTimeValue.HasValue,
        "CHECKBOX" => value.BooleanValue.HasValue,
        _ => false
    };

    private static string ValidateValue(FieldValidationRow field, PublicFormValue value)
    {
        var text = value.TextValue?.Trim() ?? "";
        var selectedOptions = value.SelectedOptionIds ?? [];
        var selectedLookupValues = (value.SelectedOptionValues ?? []).Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var hasAnySelection = selectedOptions.Count > 0 || selectedLookupValues.Count > 0;
        var hasOtherScalar = value.IntegerValue.HasValue || value.DecimalValue.HasValue || value.DateValue.HasValue || value.DateTimeValue.HasValue || value.BooleanValue.HasValue;
        switch (field.TypeCode)
        {
            case "TEXT":
            case "TEXTAREA":
            case "EMAIL":
            case "PHONE":
                if (hasOtherScalar || hasAnySelection) return "The submitted value type does not match this text field.";
                if (text.Length > 0 && field.MinimumLength.HasValue && text.Length < field.MinimumLength.Value) return $"Value must contain at least {field.MinimumLength} characters.";
                if (field.MaximumLength.HasValue && text.Length > field.MaximumLength.Value) return $"Value cannot exceed {field.MaximumLength} characters.";
                if (field.TypeCode == "EMAIL" && text.Length > 0 && !ValidEmail(text)) return "Enter a valid email address.";
                if (field.TypeCode == "PHONE" && text.Length > 0 && (NormalizePhone(text).Length < 7 || NormalizePhone(text).Length > 15)) return "Enter a valid phone number.";
                break;
            case "NUMBER":
                if (text.Length > 0 || value.DateValue.HasValue || value.DateTimeValue.HasValue || value.BooleanValue.HasValue || hasAnySelection)
                    return "The submitted value type does not match this number field.";
                if (value.IntegerValue.HasValue && value.DecimalValue.HasValue) return "Submit either an integer or decimal value, not both.";
                decimal? number = value.DecimalValue ?? (value.IntegerValue.HasValue ? (decimal)value.IntegerValue.Value : null);
                if (number.HasValue && field.MinimumNumber.HasValue && number.Value < field.MinimumNumber.Value) return $"Value cannot be below {field.MinimumNumber}.";
                if (number.HasValue && field.MaximumNumber.HasValue && number.Value > field.MaximumNumber.Value) return $"Value cannot exceed {field.MaximumNumber}.";
                break;
            case "DATE":
                if (text.Length > 0 || value.IntegerValue.HasValue || value.DecimalValue.HasValue || value.DateTimeValue.HasValue || value.BooleanValue.HasValue || hasAnySelection)
                    return "The submitted value type does not match this date field.";
                var date = value.DateValue?.Date;
                if (date.HasValue && field.MinimumDate.HasValue && date.Value < field.MinimumDate.Value.Date) return $"Date cannot be before {field.MinimumDate:dd-MMM-yyyy}.";
                if (date.HasValue && field.MaximumDate.HasValue && date.Value > field.MaximumDate.Value.Date) return $"Date cannot be after {field.MaximumDate:dd-MMM-yyyy}.";
                break;
            case "DATETIME":
                if (text.Length > 0 || value.IntegerValue.HasValue || value.DecimalValue.HasValue || value.DateValue.HasValue || value.BooleanValue.HasValue || hasAnySelection)
                    return "The submitted value type does not match this date-time field.";
                var dateTime = value.DateTimeValue;
                if (dateTime.HasValue && field.MinimumDate.HasValue && dateTime.Value.Date < field.MinimumDate.Value.Date) return $"Date cannot be before {field.MinimumDate:dd-MMM-yyyy}.";
                if (dateTime.HasValue && field.MaximumDate.HasValue && dateTime.Value.Date > field.MaximumDate.Value.Date) return $"Date cannot be after {field.MaximumDate:dd-MMM-yyyy}.";
                break;
            case "CHECKBOX":
                if (text.Length > 0 || value.IntegerValue.HasValue || value.DecimalValue.HasValue || value.DateValue.HasValue || value.DateTimeValue.HasValue || hasAnySelection)
                    return "The submitted value type does not match this checkbox field.";
                break;
            case "RADIO":
            case "SEARCH_SELECT":
                if (text.Length > 0 || hasOtherScalar) return "Submit this field using its configured option identifier.";
                if (field.LookupSourceId.HasValue && selectedOptions.Count > 0) return "Submit this lookup field using its selected option value.";
                if (!field.LookupSourceId.HasValue && selectedLookupValues.Count > 0) return "Submit this field using its configured option identifier.";
                if ((field.LookupSourceId.HasValue ? selectedLookupValues.Count : selectedOptions.Distinct().Count()) > 1) return "Only one option can be selected for this field.";
                break;
            case "MULTI_SELECT":
                if (text.Length > 0 || hasOtherScalar) return "Submit this field using its configured option identifiers.";
                if (field.LookupSourceId.HasValue && selectedOptions.Count > 0) return "Submit this lookup field using its selected option values.";
                if (!field.LookupSourceId.HasValue && selectedLookupValues.Count > 0) return "Submit this field using its configured option identifiers.";
                break;
            case "UPLOAD":
                if (text.Length > 0 || hasOtherScalar || hasAnySelection) return "Upload files through the configured attachment endpoint.";
                break;
            default:
                return "Unsupported form field type.";
        }
        return "";
    }

    private static async Task<PublicSessionRow?> ValidateSessionAsync(MySqlConnection db, string token, bool touch)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var row = await db.QueryFirstOrDefaultAsync<PublicSessionRow>(SessionSql, new { TokenHash = Hash(token) });
        if (!SessionValid(row)) return null;
        if (touch)
        {
            var affected = await db.ExecuteAsync(@"UPDATE form_public_sessions ps JOIN form_submissions s ON s.Id=ps.SubmissionId
SET ps.UseCount=ps.UseCount+1,ps.LastUsedAtUtc=UTC_TIMESTAMP(6)
WHERE ps.Id=@Id AND ps.RevokedAtUtc IS NULL AND ps.ExpiresAtUtc>UTC_TIMESTAMP(6) AND ps.UseCount<ps.MaximumUses AND s.Status='Draft'", new { row!.Id });
            if (affected == 0) return null;
            row.UseCount++;
        }
        return row;
    }

    private static bool SessionValid(PublicSessionRow? row) => row is not null && row.Purpose.Equals("APPLICATION", StringComparison.OrdinalIgnoreCase) && row.RevokedAtUtc is null && row.ExpiresAtUtc > DateTime.UtcNow && row.UseCount < row.MaximumUses && row.SubmissionStatus == "Draft";
    private static Task TouchSessionAsync(MySqlConnection db, MySqlTransaction tx, long id) => db.ExecuteAsync("UPDATE form_public_sessions SET UseCount=UseCount+1,LastUsedAtUtc=UTC_TIMESTAMP(6) WHERE Id=@Id", new { Id = id }, tx);
    private static Task EventAsync(MySqlConnection db, MySqlTransaction? tx, long submissionId, string eventType, string summary, long? subjectId, string ip, string agent) => db.ExecuteAsync("INSERT INTO form_submission_events (SubmissionId,EventType,EventSummary,ExternalSubjectId,IpAddress,UserAgent) VALUES (@SubmissionId,@EventType,@Summary,@SubjectId,@Ip,@Agent)", new { SubmissionId = submissionId, EventType = eventType, Summary = summary, SubjectId = subjectId, Ip = Truncate(ip, 80), Agent = Truncate(agent, 500) }, tx);

    private static async Task<string> NextNumberAsync(MySqlConnection db, MySqlTransaction tx, int clientId, string series, string prefix)
    {
        var next = await db.ExecuteScalarAsync<int>(@"INSERT INTO recruitment_number_sequences (ClientId,SeriesCode,LastNumber) VALUES (@ClientId,@Series,0) ON DUPLICATE KEY UPDATE LastNumber=LastNumber;UPDATE recruitment_number_sequences SET LastNumber=LAST_INSERT_ID(LastNumber+1) WHERE ClientId=@ClientId AND SeriesCode=@Series;SELECT LAST_INSERT_ID();", new { ClientId = clientId, Series = series }, tx);
        var code = await db.ExecuteScalarAsync<string>("SELECT COALESCE(NULLIF(Code,''),CONCAT('C',Id)) FROM clients WHERE Id=@Id", new { Id = clientId }, tx) ?? $"C{clientId}";
        return $"{prefix}-{code}-{next:D6}";
    }

    private const string SessionSql = @"SELECT ps.Id,ps.PostingId,ps.SubmissionId,ps.ExternalSubjectId,ps.Purpose,ps.MaximumUses,ps.UseCount,ps.ExpiresAtUtc,ps.RevokedAtUtc,s.Status SubmissionStatus
FROM form_public_sessions ps JOIN form_submissions s ON s.Id=ps.SubmissionId
JOIN recruitment_settings settings ON settings.ClientId=s.ClientId AND settings.RecruitmentEnabled=TRUE AND settings.EnableCandidatePortal=TRUE AND settings.IsActive=TRUE
WHERE ps.TokenHash=@TokenHash";

    private sealed class PostingSessionRow { public long PostingId { get; set; } public int ClientId { get; set; } public long PositionId { get; set; } public long? ApplicationFormVersionId { get; set; } }
    private sealed class PublishFormRow : DynamicFormVersion { public int ClientId { get; set; } public string PurposeCode { get; set; } = ""; }
    private sealed class PublicSessionRow { public long Id { get; set; } public long PostingId { get; set; } public long SubmissionId { get; set; } public long ExternalSubjectId { get; set; } public string Purpose { get; set; } = ""; public int MaximumUses { get; set; } public int UseCount { get; set; } public DateTime ExpiresAtUtc { get; set; } public DateTime? RevokedAtUtc { get; set; } public string SubmissionStatus { get; set; } = ""; }
    private sealed class FieldValidationRow { public long Id { get; set; } public int FieldTypeId { get; set; } public string TypeCode { get; set; } = ""; public bool SupportsOptions { get; set; } public bool SupportsMultipleValues { get; set; } public bool SupportsAttachment { get; set; } public long? LookupSourceId { get; set; } public int? MinimumLength { get; set; } public int? MaximumLength { get; set; } public decimal? MinimumNumber { get; set; } public decimal? MaximumNumber { get; set; } public DateTime? MinimumDate { get; set; } public DateTime? MaximumDate { get; set; } }
    private sealed class StoredValidationRuleRow : DynamicFormValidationRule { public string FieldCode { get; set; } = ""; public string FieldLabel { get; set; } = ""; public string FieldTypeCode { get; set; } = ""; public long? CompareFieldResolvedId { get; set; } public string CompareFieldLabel { get; set; } = ""; public string CompareFieldTypeCode { get; set; } = ""; public bool CompareFieldIsActive { get; set; } }
    private sealed class SubmissionFieldState { public long FieldId { get; set; } public string StableFieldCode { get; set; } = ""; public string Label { get; set; } = ""; public string TypeCode { get; set; } = ""; public string? TextValue { get; set; } public long? IntegerValue { get; set; } public decimal? DecimalValue { get; set; } public DateTime? DateValue { get; set; } public DateTime? DateTimeValue { get; set; } public bool? BooleanValue { get; set; } public int SelectedOptionCount { get; set; } public int LookupValueCount { get; set; } public int AttachmentCount { get; set; } }
    private sealed class FieldSemanticRow { public long FieldId { get; set; } public string SemanticCode { get; set; } = ""; }
    private sealed class SemanticValueRow { public string SemanticCode { get; set; } = ""; public string? TextValue { get; set; } public long? IntegerValue { get; set; } public decimal? DecimalValue { get; set; } public DateTime? DateValue { get; set; } public DateTime? DateTimeValue { get; set; } public bool? BooleanValue { get; set; } }
    private sealed class ExternalSubjectRow { public long Id { get; set; } public long? CandidateId { get; set; } public string Email { get; set; } = ""; public string NormalizedEmail { get; set; } = ""; public string Phone { get; set; } = ""; public string NormalizedPhone { get; set; } = ""; }
    private sealed class PublicAttachmentLinkRow { public long Id { get; set; } public int ClientId { get; set; } public long FieldConfigurationId { get; set; } public string EntityType { get; set; } = ""; public long EntityId { get; set; } public long FileSizeBytes { get; set; } }
    private sealed class ApplicationIdentityRow { public long Id { get; set; } public string ApplicationCode { get; set; } = ""; public long? JobPostingId { get; set; } }
    private sealed class SubmissionPostingRow { public long SubmissionId { get; set; } public long FormVersionId { get; set; } public int ClientId { get; set; } public string Status { get; set; } = ""; public long PostingId { get; set; } public long PositionId { get; set; } public int RecruiterUserId { get; set; } public int ApplicationCount { get; set; } public int? MaximumApplications { get; set; } public string PostingStatus { get; set; } = ""; public DateTime? OpensAtUtc { get; set; } public DateTime? ClosesAtUtc { get; set; } public long? CandidateId { get; set; } public long? ApplicationId { get; set; } }
}
