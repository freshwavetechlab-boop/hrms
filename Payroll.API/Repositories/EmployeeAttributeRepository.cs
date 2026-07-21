using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public sealed class EmployeeAttributeRepository(IConfiguration configuration)
{
    private MySqlConnection Db() => new(configuration.GetConnectionString("Default"));

    public async Task InitializeAsync()
    {
        await using var db = Db();
        await db.OpenAsync();
        await db.ExecuteAsync(@"
CREATE TABLE IF NOT EXISTS employee_form_bindings (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    ClientId INT NOT NULL,
    FormDefinitionId BIGINT NOT NULL,
    InfotypeCode VARCHAR(30) NOT NULL DEFAULT '0002',
    IsRequired BOOLEAN NOT NULL DEFAULT FALSE,
    DisplayOrder INT NOT NULL DEFAULT 100,
    IsActive BOOLEAN NOT NULL DEFAULT TRUE,
    CreatedByUserId INT NULL,
    CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    UpdatedByUserId INT NULL,
    UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
    UNIQUE KEY UX_employee_form_binding (ClientId,FormDefinitionId,InfotypeCode),
    INDEX IX_employee_form_binding_scope (ClientId,InfotypeCode,IsActive,DisplayOrder)
);");

        if (await TableExistsAsync(db, "form_submissions"))
        {
            await EnsureColumnAsync(db, "form_submissions", "EmployeeFormBindingId", "BIGINT NULL");
            await EnsureColumnAsync(db, "form_submissions", "EmployeeInfotypeCode", "VARCHAR(30) NULL");
            await EnsureColumnAsync(db, "form_submissions", "RevisionNumber", "INT NULL");
            await EnsureColumnAsync(db, "form_submissions", "EffectiveFromUtc", "DATETIME(6) NULL");
            await EnsureColumnAsync(db, "form_submissions", "EffectiveToUtc", "DATETIME(6) NULL");
            await EnsureColumnAsync(db, "form_submissions", "PreviousSubmissionId", "BIGINT NULL");
            await EnsureColumnAsync(db, "form_submissions", "SourceCode", "VARCHAR(80) NULL");
            await EnsureColumnAsync(db, "form_submissions", "SourceReference", "VARCHAR(180) NULL");
            await EnsureColumnAsync(db, "form_submissions", "ChangeReason", "VARCHAR(500) NULL");
            await EnsureColumnAsync(db, "form_submissions", "ChangedByUserId", "INT NULL");
            await EnsureIndexAsync(db, "form_submissions", "IX_form_submission_employee_effective",
                "CREATE INDEX IX_form_submission_employee_effective ON form_submissions (ClientId,EntityType,EntityId,EmployeeInfotypeCode,EffectiveFromUtc,EffectiveToUtc)");
            await EnsureIndexAsync(db, "form_submissions", "IX_form_submission_employee_binding",
                "CREATE INDEX IX_form_submission_employee_binding ON form_submissions (EmployeeFormBindingId,RevisionNumber)");
        }

        await EnsureForeignKeyAsync(db, "employee_form_bindings", "FK_employee_form_binding_client", "ClientId", "clients", "Id", "RESTRICT");
        await EnsureForeignKeyAsync(db, "employee_form_bindings", "FK_employee_form_binding_definition", "FormDefinitionId", "form_definitions", "Id", "CASCADE");
        await EnsureForeignKeyAsync(db, "form_submissions", "FK_form_submission_employee_binding", "EmployeeFormBindingId", "employee_form_bindings", "Id", "SET NULL");
        await EnsureForeignKeyAsync(db, "form_submissions", "FK_form_submission_previous", "PreviousSubmissionId", "form_submissions", "Id", "SET NULL");
    }

    public async Task<(EmployeeAttributeContext? Item, string Error)> GetEmployeeFieldsAsync(
        int employeeId,
        int clientId,
        string? infotypeCode,
        AuthUser user,
        DateTime? asOfUtc = null)
    {
        var code = Infotype(infotypeCode);
        if (employeeId <= 0 || clientId <= 0) return (null, "Employee was not found.");
        if (!ClientAccess(user, clientId)) return (null, "Client access denied.");
        if (!CanRead(user, employeeId)) return (null, "Employee access denied.");

        await using var db = Db();
        await db.OpenAsync();
        var storedClientId = await db.ExecuteScalarAsync<int?>("SELECT ClientId FROM employees WHERE Id=@Id", new { Id = employeeId });
        if (!storedClientId.HasValue || storedClientId.Value != clientId) return (null, "Employee was not found for the selected client.");

        var effectiveAt = Utc(asOfUtc ?? DateTime.UtcNow);
        var forms = await LoadBoundFormsAsync(db, clientId, code);
        var values = new List<EmployeeAttributeValue>();
        foreach (var form in forms)
        {
            await PopulateFormAsync(db, form);
            values.AddRange(await LoadCurrentValuesAsync(db, form, employeeId, clientId, effectiveAt));
        }
        var files = await LoadCurrentFilesAsync(db, forms, employeeId, clientId);

        return (new EmployeeAttributeContext
        {
            EmployeeId = employeeId,
            ClientId = clientId,
            InfotypeCode = code,
            AsOfUtc = effectiveAt,
            Forms = forms,
            Values = values.GroupBy(row => row.FieldId).Select(group => group.First()).ToList(),
            Files = files
        }, "");
    }

    public Task<(SaveEmployeeAttributeValuesResult? Item, string Error)> SaveEmployeeRevisionAsync(
        int employeeId,
        int clientId,
        string? infotypeCode,
        SaveEmployeeAttributeValuesRequest request,
        AuthUser user)
    {
        request ??= new SaveEmployeeAttributeValuesRequest();
        request.ClientId = clientId;
        request.InfotypeCode = Infotype(infotypeCode);
        return SaveEffectiveRevisionAsync(employeeId, request, user);
    }

    public async Task<(SaveEmployeeAttributeValuesResult? Item, string Error)> SaveEffectiveRevisionAsync(
        int employeeId,
        SaveEmployeeAttributeValuesRequest request,
        AuthUser user)
    {
        request ??= new SaveEmployeeAttributeValuesRequest();
        request.Values ??= [];
        var clientId = request.ClientId;
        var infotypeCode = Infotype(request.InfotypeCode);
        if (employeeId <= 0 || clientId <= 0) return (null, "Employee was not found.");
        if (!ClientAccess(user, clientId)) return (null, "Client access denied.");
        if (!CanManage(user)) return (null, "You are not allowed to update employee attributes.");
        if (request.Values.Any(row => row is null)) return (null, "A submitted employee attribute is invalid.");
        if (request.Values.GroupBy(row => row.FieldId).Any(group => group.Key <= 0 || group.Count() > 1))
            return (null, "Each dynamic field can be submitted only once.");
        if (request.Values.Count == 0)
        {
            var current = await GetEmployeeFieldsAsync(employeeId, clientId, infotypeCode, user);
            return current.Item is null
                ? (null, current.Error)
                : (new SaveEmployeeAttributeValuesResult { EmployeeId = employeeId, EffectiveFromUtc = DateTime.UtcNow, Values = current.Item.Values }, "");
        }

        var effectiveFrom = Utc(request.EffectiveFromUtc ?? DateTime.UtcNow);
        var sourceCode = Code(request.SourceCode, 80, "EMPLOYEE_UI");
        var sourceReference = Truncate((request.SourceReference ?? "").Trim(), 180);
        var changeReason = Truncate(string.IsNullOrWhiteSpace(request.ChangeReason) ? "Employee dynamic attributes updated" : request.ChangeReason.Trim(), 500);

        await using var db = Db();
        await db.OpenAsync();
        await using var transaction = await db.BeginTransactionAsync();
        try
        {
            var storedClientId = await db.ExecuteScalarAsync<int?>("SELECT ClientId FROM employees WHERE Id=@Id FOR UPDATE", new { Id = employeeId }, transaction);
            if (!storedClientId.HasValue || storedClientId.Value != clientId) return (null, "Employee was not found for the selected client.");

            var forms = await LoadBoundFormsAsync(db, clientId, infotypeCode, transaction);
            foreach (var form in forms) await PopulateFormAsync(db, form, transaction);
            var fieldOwners = forms
                .SelectMany(form => form.Sections.SelectMany(section => section.Fields).Select(field => new { form, field }))
                .ToDictionary(row => row.field.Id);
            var unknown = request.Values.FirstOrDefault(row => !fieldOwners.ContainsKey(row.FieldId));
            if (unknown is not null) return (null, "A submitted field is not part of a published employee form for this infotype.");

            var touchedForms = request.Values.Select(row => fieldOwners[row.FieldId].form.FormDefinitionId).ToHashSet();
            var revisions = new List<EmployeeAttributeRevision>();
            foreach (var form in forms.Where(row => touchedForms.Contains(row.FormDefinitionId)).OrderBy(row => row.BindingId ?? long.MaxValue).ThenBy(row => row.FormDefinitionId))
            {
                var current = await LoadCurrentValuesAsync(db, form, employeeId, clientId, effectiveFrom, transaction);
                var merged = current.ToDictionary(row => row.FieldId, CloneValue);
                foreach (var supplied in request.Values.Where(row => fieldOwners[row.FieldId].form.FormDefinitionId == form.FormDefinitionId))
                    merged[supplied.FieldId] = CloneValue(supplied);

                var runtimeFields = await LoadRuntimeFieldsAsync(db, form.Id, transaction);
                var validationError = await ValidateSnapshotAsync(db, transaction, runtimeFields, merged, employeeId, clientId);
                if (validationError.Length > 0) return (null, validationError);
                var (revision, revisionError) = await InsertRevisionAsync(db, transaction, form, employeeId, clientId, infotypeCode,
                    effectiveFrom, sourceCode, sourceReference, changeReason, user.Id, runtimeFields, merged);
                if (revision is null) return (null, revisionError);
                revisions.Add(revision);
            }

            await transaction.CommitAsync();
            var refreshed = await GetEmployeeFieldsAsync(employeeId, clientId, infotypeCode, user, effectiveFrom);
            return refreshed.Item is null
                ? (null, refreshed.Error)
                : (new SaveEmployeeAttributeValuesResult
                {
                    EmployeeId = employeeId,
                    SavedCount = request.Values.Count,
                    EffectiveFromUtc = effectiveFrom,
                    SubmissionIds = revisions.Select(row => row.SubmissionId).ToList(),
                    Revisions = revisions,
                    Values = refreshed.Item.Values
                }, "");
        }
        catch (MySqlException exception) when (exception.Number == 1062)
        {
            await transaction.RollbackAsync();
            return (null, "The employee attribute revision conflicts with an existing value. Refresh and retry.");
        }
        catch (Exception exception)
        {
            await transaction.RollbackAsync();
            return (null, exception is InvalidOperationException ? exception.Message : "Employee attributes could not be saved.");
        }
    }

    public async Task<(IEnumerable<EmployeeAttributeLookupOption> Items, string Error)> ResolveLookupAsync(
        int employeeId,
        int clientId,
        long fieldId,
        string search,
        AuthUser user)
    {
        if (employeeId <= 0 || clientId <= 0 || fieldId <= 0) return ([], "Dynamic lookup was not found.");
        if (!ClientAccess(user, clientId) || !CanRead(user, employeeId)) return ([], "Employee access denied.");
        await using var db = Db();
        await db.OpenAsync();
        var validEmployee = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employees WHERE Id=@EmployeeId AND ClientId=@ClientId", new { EmployeeId = employeeId, ClientId = clientId });
        if (validEmployee == 0) return ([], "Employee was not found for the selected client.");
        var source = await db.QueryFirstOrDefaultAsync<LookupSourceRow>(@"SELECT l.Id,l.SourceCode,l.SourceName,l.ResolverCode,l.MinimumSearchLength,l.MaximumResults
FROM form_fields f
JOIN form_versions v ON v.Id=f.FormVersionId AND v.Status='Published'
JOIN form_definitions d ON d.Id=v.FormDefinitionId AND d.CurrentPublishedVersionId=v.Id AND d.ClientId=@ClientId AND d.ModuleCode='EMPLOYEE' AND d.EntityType='EMPLOYEE' AND d.Status='Active'
JOIN form_lookup_sources l ON l.Id=f.LookupSourceId AND l.IsActive=TRUE
WHERE f.Id=@FieldId AND f.IsActive=TRUE AND (
 EXISTS (SELECT 1 FROM employee_form_bindings b WHERE b.ClientId=d.ClientId AND b.FormDefinitionId=d.Id AND b.IsActive=TRUE)
 OR NOT EXISTS (SELECT 1 FROM employee_form_bindings anyBinding WHERE anyBinding.ClientId=d.ClientId AND anyBinding.FormDefinitionId=d.Id)
)", new { ClientId = clientId, FieldId = fieldId });
        if (source is null) return ([], "This field does not have an active lookup.");
        var term = (search ?? "").Trim();
        if (term.Length < source.MinimumSearchLength) return ([], $"Enter at least {source.MinimumSearchLength} characters.");
        return (await ResolveLookupRowsAsync(db, null, source, clientId, term, null), "");
    }

    public async Task<IEnumerable<EmployeeFormBinding>> ListBindingsAsync(int? clientId, AuthUser user)
    {
        var scope = user.ClientId ?? clientId;
        if (user.ClientId.HasValue && clientId.HasValue && user.ClientId.Value != clientId.Value) return [];
        await using var db = Db();
        await db.OpenAsync();
        var rows = (await db.QueryAsync<EmployeeFormBinding>(@"SELECT b.Id,b.ClientId,COALESCE(c.Name,'') ClientName,b.FormDefinitionId,d.FormCode,d.FormName,b.InfotypeCode,b.IsRequired,b.DisplayOrder,b.IsActive,FALSE IsImplicit,
b.CreatedByUserId,b.CreatedAtUtc,b.UpdatedByUserId,b.UpdatedAtUtc
FROM employee_form_bindings b JOIN form_definitions d ON d.Id=b.FormDefinitionId LEFT JOIN clients c ON c.Id=b.ClientId
WHERE (@ClientId IS NULL OR b.ClientId=@ClientId)
UNION ALL
SELECT 0 Id,d.ClientId,COALESCE(c.Name,'') ClientName,d.Id FormDefinitionId,d.FormCode,d.FormName,'0002' InfotypeCode,FALSE IsRequired,100 DisplayOrder,TRUE IsActive,TRUE IsImplicit,
NULL CreatedByUserId,d.CreatedAtUtc,NULL UpdatedByUserId,d.UpdatedAtUtc
FROM form_definitions d JOIN form_versions v ON v.Id=d.CurrentPublishedVersionId AND v.Status='Published' LEFT JOIN clients c ON c.Id=d.ClientId
WHERE d.ModuleCode='EMPLOYEE' AND d.EntityType='EMPLOYEE' AND d.Status='Active' AND (@ClientId IS NULL OR d.ClientId=@ClientId)
AND NOT EXISTS (SELECT 1 FROM employee_form_bindings b WHERE b.ClientId=d.ClientId AND b.FormDefinitionId=d.Id)
ORDER BY ClientName,DisplayOrder,FormName", new { ClientId = scope })).ToList();
        foreach (var row in rows.Where(item => item.IsImplicit))
        {
            var purpose = await db.ExecuteScalarAsync<string>("SELECT PurposeCode FROM form_definitions WHERE Id=@Id", new { Id = row.FormDefinitionId }) ?? "";
            row.InfotypeCode = InfotypeFromPurpose(purpose);
        }
        return rows;
    }

    public async Task<(EmployeeFormBinding? Item, string Error)> SaveBindingAsync(SaveEmployeeFormBinding request, AuthUser user)
    {
        request ??= new SaveEmployeeFormBinding();
        if (request.ClientId <= 0 || request.FormDefinitionId <= 0) return (null, "Client and employee form are required.");
        if (!ClientAccess(user, request.ClientId)) return (null, "Client access denied.");
        if (!HasPermission(user, "settings.manage", "security.manage")) return (null, "You are not allowed to configure employee forms.");
        var infotypeCode = Infotype(request.InfotypeCode);
        await using var db = Db();
        await db.OpenAsync();
        var valid = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM form_definitions d JOIN form_versions v ON v.Id=d.CurrentPublishedVersionId AND v.Status='Published'
WHERE d.Id=@Id AND d.ClientId=@ClientId AND d.ModuleCode='EMPLOYEE' AND d.EntityType='EMPLOYEE'", new { Id = request.FormDefinitionId, request.ClientId });
        if (valid == 0) return (null, "Select a published employee form for this client.");
        try
        {
            if (request.Id <= 0)
            {
                request.Id = await db.ExecuteScalarAsync<long>(@"INSERT INTO employee_form_bindings
(ClientId,FormDefinitionId,InfotypeCode,IsRequired,DisplayOrder,IsActive,CreatedByUserId,UpdatedByUserId)
VALUES (@ClientId,@FormDefinitionId,@InfotypeCode,@IsRequired,@DisplayOrder,@IsActive,@UserId,@UserId);SELECT LAST_INSERT_ID();",
                    new { request.ClientId, request.FormDefinitionId, InfotypeCode = infotypeCode, request.IsRequired, DisplayOrder = Math.Max(0, request.DisplayOrder), request.IsActive, UserId = user.Id });
            }
            else
            {
                var affected = await db.ExecuteAsync(@"UPDATE employee_form_bindings SET InfotypeCode=@InfotypeCode,IsRequired=@IsRequired,DisplayOrder=@DisplayOrder,
IsActive=@IsActive,UpdatedByUserId=@UserId WHERE Id=@Id AND ClientId=@ClientId AND FormDefinitionId=@FormDefinitionId",
                    new { request.Id, request.ClientId, request.FormDefinitionId, InfotypeCode = infotypeCode, request.IsRequired, DisplayOrder = Math.Max(0, request.DisplayOrder), request.IsActive, UserId = user.Id });
                if (affected == 0) return (null, "Employee form binding was not found.");
            }
        }
        catch (MySqlException exception) when (exception.Number == 1062)
        {
            return (null, "This form is already bound to the selected employee infotype.");
        }
        return (await BindingByIdAsync(db, request.Id), "");
    }

    private static async Task<List<EmployeeAttributeForm>> LoadBoundFormsAsync(
        MySqlConnection db,
        int clientId,
        string infotypeCode,
        MySqlTransaction? transaction = null)
    {
        var rows = await db.QueryAsync<EmployeeAttributeForm>(@"SELECT v.Id,d.Id FormDefinitionId,b.Id BindingId,d.FormCode,d.FormName,b.InfotypeCode,v.VersionNumber,v.Status,b.IsRequired,b.DisplayOrder,FALSE IsImplicitBinding
FROM employee_form_bindings b
JOIN form_definitions d ON d.Id=b.FormDefinitionId AND d.ClientId=b.ClientId AND d.ModuleCode='EMPLOYEE' AND d.EntityType='EMPLOYEE' AND d.Status='Active'
JOIN form_versions v ON v.Id=d.CurrentPublishedVersionId AND v.Status='Published'
WHERE b.ClientId=@ClientId AND b.InfotypeCode=@InfotypeCode AND b.IsActive=TRUE
UNION ALL
SELECT v.Id,d.Id FormDefinitionId,NULL BindingId,d.FormCode,d.FormName,
CASE WHEN LEFT(UPPER(d.PurposeCode),18)='EMPLOYEE_INFOTYPE_' THEN SUBSTRING(UPPER(d.PurposeCode),19) ELSE '0002' END InfotypeCode,
v.VersionNumber,v.Status,FALSE IsRequired,100 DisplayOrder,TRUE IsImplicitBinding
FROM form_definitions d
JOIN form_versions v ON v.Id=d.CurrentPublishedVersionId AND v.Status='Published'
WHERE d.ClientId=@ClientId AND d.ModuleCode='EMPLOYEE' AND d.EntityType='EMPLOYEE' AND d.Status='Active'
AND CASE WHEN LEFT(UPPER(d.PurposeCode),18)='EMPLOYEE_INFOTYPE_' THEN SUBSTRING(UPPER(d.PurposeCode),19) ELSE '0002' END=@InfotypeCode
AND NOT EXISTS (SELECT 1 FROM employee_form_bindings b WHERE b.ClientId=d.ClientId AND b.FormDefinitionId=d.Id)
ORDER BY DisplayOrder,FormName,FormDefinitionId", new { ClientId = clientId, InfotypeCode = infotypeCode }, transaction);
        return rows.ToList();
    }

    private static async Task PopulateFormAsync(MySqlConnection db, EmployeeAttributeForm form, MySqlTransaction? transaction = null)
    {
        form.Sections = (await db.QueryAsync<DynamicFormSection>(
            "SELECT Id,FormVersionId,SectionCode,SectionLabel,Description,DisplayOrder FROM form_sections WHERE FormVersionId=@Id ORDER BY DisplayOrder,Id",
            new { form.Id }, transaction)).ToList();
        var fields = (await db.QueryAsync<DynamicFormField>(@"SELECT f.Id,f.FormVersionId,f.SectionId,t.TypeCode FieldTypeCode,f.StableFieldCode,f.Label,f.Placeholder,f.HelpText,f.IsRequired,f.DisplayOrder,f.WidthColumns,
f.MinimumLength,f.MaximumLength,f.MinimumNumber,f.MaximumNumber,f.MinimumDate,f.MaximumDate,f.AttachmentFieldConfigurationId,COALESCE(l.SourceCode,'') LookupSourceCode,f.IsActive,
COALESCE(cfg.allowed_extensions_json,JSON_ARRAY()) AllowedExtensionsJson,COALESCE(cfg.allowed_mime_types_json,JSON_ARRAY()) AllowedMimeTypesJson,
COALESCE(cfg.allow_multiple,FALSE) AllowMultipleFiles,COALESCE(cfg.maximum_file_count,1) MaximumFileCount,COALESCE(cfg.maximum_file_size_bytes,0) MaximumFileSizeBytes,cfg.maximum_total_size_bytes MaximumTotalSizeBytes
FROM form_fields f
JOIN form_field_types t ON t.Id=f.FieldTypeId
JOIN form_versions v ON v.Id=f.FormVersionId
JOIN form_definitions d ON d.Id=v.FormDefinitionId
LEFT JOIN form_lookup_sources l ON l.Id=f.LookupSourceId AND l.IsActive=TRUE
LEFT JOIN attachment_field_configurations cfg ON cfg.id=f.AttachmentFieldConfigurationId AND cfg.is_active=TRUE AND cfg.client_id IN (0,d.ClientId)
 AND (cfg.effective_from_utc IS NULL OR cfg.effective_from_utc<=UTC_TIMESTAMP(6))
 AND (cfg.effective_until_utc IS NULL OR cfg.effective_until_utc>=UTC_TIMESTAMP(6))
WHERE f.FormVersionId=@Id AND f.IsActive=TRUE ORDER BY f.DisplayOrder,f.Id", new { form.Id }, transaction)).ToList();
        if (fields.Count > 0)
        {
            var ids = fields.Select(row => row.Id).ToArray();
            var options = (await db.QueryAsync<DynamicFormFieldOption>(
                "SELECT Id,FieldId,OptionCode,OptionLabel,DisplayOrder,IsActive FROM form_field_options WHERE FieldId IN @Ids AND IsActive=TRUE ORDER BY DisplayOrder,Id",
                new { Ids = ids }, transaction)).GroupBy(row => row.FieldId).ToDictionary(group => group.Key, group => group.ToList());
            var semanticCodes = (await db.QueryAsync<FieldSemanticRow>(@"SELECT m.FieldId,a.SemanticCode FROM form_field_semantic_mappings m
JOIN form_semantic_attributes a ON a.Id=m.SemanticAttributeId AND a.IsActive=TRUE WHERE m.FieldId IN @Ids", new { Ids = ids }, transaction))
                .GroupBy(row => row.FieldId).ToDictionary(group => group.Key, group => group.Select(row => row.SemanticCode).ToList());
            var rules = (await db.QueryAsync<DynamicFormValidationRule>(@"SELECT r.*,COALESCE(compareField.StableFieldCode,'') CompareFieldCode
FROM form_field_validation_rules r LEFT JOIN form_fields compareField ON compareField.Id=r.CompareFieldId
WHERE r.FieldId IN @Ids ORDER BY r.DisplayOrder,r.Id", new { Ids = ids }, transaction))
                .GroupBy(row => row.FieldId).ToDictionary(group => group.Key, group => group.ToList());
            foreach (var field in fields)
            {
                field.Options = options.GetValueOrDefault(field.Id) ?? [];
                field.SemanticCodes = semanticCodes.GetValueOrDefault(field.Id) ?? [];
                field.ValidationRules = rules.GetValueOrDefault(field.Id) ?? [];
                if (field.FieldTypeCode == "UPLOAD" && field.AttachmentFieldConfigurationId.HasValue && field.MaximumFileSizeBytes > 0)
                {
                    field.AttachmentConstraints = new PublicAttachmentConstraints
                    {
                        AllowMultiple = field.AllowMultipleFiles,
                        MaximumFileCount = Math.Max(1, field.MaximumFileCount),
                        MaximumFileSizeBytes = field.MaximumFileSizeBytes,
                        MaximumTotalSizeBytes = field.MaximumTotalSizeBytes,
                        AllowedExtensions = ReadStringList(field.AllowedExtensionsJson),
                        AllowedMimeTypes = ReadStringList(field.AllowedMimeTypesJson)
                    };
                }
            }
        }
        var bySection = fields.GroupBy(row => row.SectionId).ToDictionary(group => group.Key, group => group.ToList());
        foreach (var section in form.Sections) section.Fields = bySection.GetValueOrDefault(section.Id) ?? [];
    }

    private static async Task<List<EmployeeAttributeValue>> LoadCurrentValuesAsync(
        MySqlConnection db,
        EmployeeAttributeForm form,
        int employeeId,
        int clientId,
        DateTime asOfUtc,
        MySqlTransaction? transaction = null)
    {
        var currentFields = form.Sections.SelectMany(section => section.Fields).ToList();
        var currentByCode = currentFields.ToDictionary(row => row.StableFieldCode, StringComparer.OrdinalIgnoreCase);
        var values = new Dictionary<long, EmployeeAttributeValue>();
        EmployeeAttributeValue ValueFor(DynamicFormField field)
        {
            if (values.TryGetValue(field.Id, out var value)) return value;
            value = new EmployeeAttributeValue { FieldId = field.Id };
            values[field.Id] = value;
            return value;
        }

        var submissionId = await db.ExecuteScalarAsync<long?>(@"SELECT s.Id FROM form_submissions s
JOIN form_versions storedVersion ON storedVersion.Id=s.FormVersionId AND storedVersion.FormDefinitionId=@FormDefinitionId
WHERE s.ClientId=@ClientId AND s.EntityType='EMPLOYEE' AND s.EntityId=@EmployeeId AND s.Status='Submitted'
AND COALESCE(s.EmployeeInfotypeCode,'0002')=@InfotypeCode
AND COALESCE(s.EffectiveFromUtc,s.SubmittedAtUtc,s.StartedAtUtc)<=@AsOfUtc
AND (s.EffectiveToUtc IS NULL OR s.EffectiveToUtc>@AsOfUtc)
ORDER BY COALESCE(s.EffectiveFromUtc,s.SubmittedAtUtc,s.StartedAtUtc) DESC,s.Id DESC LIMIT 1",
            new { form.FormDefinitionId, ClientId = clientId, EmployeeId = employeeId, form.InfotypeCode, AsOfUtc = asOfUtc }, transaction);
        if (submissionId.HasValue)
        {
            var scalarRows = await db.QueryAsync<StoredScalarRow>(@"SELECT f.StableFieldCode,v.TextValue,v.IntegerValue,v.DecimalValue,v.DateValue,v.DateTimeValue,v.BooleanValue
FROM form_submission_values v JOIN form_fields f ON f.Id=v.FieldId WHERE v.SubmissionId=@SubmissionId", new { SubmissionId = submissionId.Value }, transaction);
            foreach (var row in scalarRows)
            {
                if (!currentByCode.TryGetValue(row.StableFieldCode, out var field)) continue;
                var value = ValueFor(field);
                value.TextValue = row.TextValue;
                value.IntegerValue = row.IntegerValue;
                value.DecimalValue = row.DecimalValue;
                value.DateValue = row.DateValue;
                value.DateTimeValue = row.DateTimeValue;
                value.BooleanValue = row.BooleanValue;
            }
            var optionRows = await db.QueryAsync<StoredOptionRow>(@"SELECT f.StableFieldCode,o.OptionCode FROM form_submission_selected_options selected
JOIN form_fields f ON f.Id=selected.FieldId JOIN form_field_options o ON o.Id=selected.OptionId
WHERE selected.SubmissionId=@SubmissionId ORDER BY o.DisplayOrder,o.Id", new { SubmissionId = submissionId.Value }, transaction);
            foreach (var row in optionRows)
            {
                if (!currentByCode.TryGetValue(row.StableFieldCode, out var field)) continue;
                var currentOption = field.Options.FirstOrDefault(option => option.OptionCode.Equals(row.OptionCode, StringComparison.OrdinalIgnoreCase));
                if (currentOption is not null) ValueFor(field).SelectedOptionIds.Add(currentOption.Id);
            }
            var lookupRows = await db.QueryAsync<StoredLookupRow>(@"SELECT f.StableFieldCode,l.SelectedValue,l.DisplayLabel,l.DisplayOrder
FROM form_submission_lookup_values l JOIN form_fields f ON f.Id=l.FieldId
WHERE l.SubmissionId=@SubmissionId ORDER BY l.DisplayOrder,l.Id", new { SubmissionId = submissionId.Value }, transaction);
            foreach (var row in lookupRows)
                if (currentByCode.TryGetValue(row.StableFieldCode, out var field)) ValueFor(field).SelectedOptionValues.Add(row.SelectedValue);
        }

        foreach (var field in currentFields.Where(row => row.FieldTypeCode == "UPLOAD" && row.AttachmentFieldConfigurationId.HasValue))
        {
            var publicIds = await db.QueryAsync<string>(@"SELECT public_id FROM entity_attachments
WHERE client_id=@ClientId AND entity_type='EMPLOYEE' AND entity_id=@EmployeeId AND field_configuration_id=@ConfigurationId
AND is_current=TRUE AND is_deleted=FALSE ORDER BY uploaded_at_utc,id",
                new { ClientId = clientId, EmployeeId = employeeId, ConfigurationId = field.AttachmentFieldConfigurationId!.Value }, transaction);
            foreach (var publicId in publicIds)
                if (Guid.TryParse(publicId, out var parsed)) ValueFor(field).AttachmentPublicIds.Add(parsed);
        }
        return values.Values.Where(HasValue).OrderBy(row => row.FieldId).ToList();
    }

    private static async Task<List<RuntimeField>> LoadRuntimeFieldsAsync(MySqlConnection db, long versionId, MySqlTransaction transaction)
    {
        var rows = await db.QueryAsync<RuntimeField>(@"SELECT f.Id,f.FormVersionId,f.StableFieldCode,f.Label,t.TypeCode,f.IsRequired,f.MinimumLength,f.MaximumLength,f.MinimumNumber,f.MaximumNumber,f.MinimumDate,f.MaximumDate,
f.AttachmentFieldConfigurationId,f.LookupSourceId,COALESCE(l.SourceCode,'') LookupSourceCode,COALESCE(l.ResolverCode,'') LookupResolverCode,
cfg.id EffectiveAttachmentConfigurationId,COALESCE(cfg.minimum_file_count,0) MinimumFileCount,COALESCE(cfg.maximum_file_count,1) MaximumFileCount,
COALESCE(cfg.requirement_scope,'NewEntitiesOnly') RequirementScope
FROM form_fields f JOIN form_field_types t ON t.Id=f.FieldTypeId
LEFT JOIN form_lookup_sources l ON l.Id=f.LookupSourceId AND l.IsActive=TRUE
LEFT JOIN attachment_field_configurations cfg ON cfg.id=f.AttachmentFieldConfigurationId AND cfg.is_active=TRUE
 AND (cfg.effective_from_utc IS NULL OR cfg.effective_from_utc<=UTC_TIMESTAMP(6))
 AND (cfg.effective_until_utc IS NULL OR cfg.effective_until_utc>=UTC_TIMESTAMP(6))
WHERE f.FormVersionId=@VersionId AND f.IsActive=TRUE ORDER BY f.DisplayOrder,f.Id", new { VersionId = versionId }, transaction);
        return rows.ToList();
    }

    private static async Task<List<EmployeeAttributeFile>> LoadCurrentFilesAsync(
        MySqlConnection db,
        IEnumerable<EmployeeAttributeForm> forms,
        int employeeId,
        int clientId,
        MySqlTransaction? transaction = null)
    {
        var configurationIds = forms
            .SelectMany(form => form.Sections)
            .SelectMany(section => section.Fields)
            .Where(field => field.FieldTypeCode == "UPLOAD" && field.AttachmentFieldConfigurationId.HasValue)
            .Select(field => field.AttachmentFieldConfigurationId!.Value)
            .Distinct()
            .ToArray();
        if (configurationIds.Length == 0) return [];
        var rows = await db.QueryAsync<EmployeeAttributeFile>(@"SELECT public_id PublicId,field_configuration_id FieldConfigurationId,
original_file_name OriginalFileName,file_size_bytes FileSizeBytes,uploaded_at_utc UploadedAtUtc
FROM entity_attachments
WHERE client_id=@ClientId AND entity_type='EMPLOYEE' AND entity_id=@EmployeeId AND field_configuration_id IN @ConfigurationIds
AND is_current=TRUE AND is_deleted=FALSE ORDER BY field_configuration_id,uploaded_at_utc,id",
            new { ClientId = clientId, EmployeeId = employeeId, ConfigurationIds = configurationIds }, transaction);
        return rows.ToList();
    }

    private static async Task<string> ValidateSnapshotAsync(
        MySqlConnection db,
        MySqlTransaction transaction,
        IReadOnlyList<RuntimeField> fields,
        IReadOnlyDictionary<long, EmployeeAttributeValue> values,
        int employeeId,
        int clientId)
    {
        if (fields.Count == 0) return "The published employee form does not contain any active fields.";
        var states = new Dictionary<long, RuntimeValueState>();
        foreach (var field in fields)
        {
            var value = values.GetValueOrDefault(field.Id) ?? new EmployeeAttributeValue { FieldId = field.Id };
            value.SelectedOptionIds = (value.SelectedOptionIds ?? []).Where(id => id > 0).Distinct().ToList();
            value.SelectedOptionValues = (value.SelectedOptionValues ?? [])
                .Select(item => (item ?? "").Trim()).Where(item => item.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            value.AttachmentPublicIds = (value.AttachmentPublicIds ?? []).Where(id => id != Guid.Empty).Distinct().ToList();

            var typeError = ValidateTypedValue(field, value);
            if (typeError.Length > 0) return $"{field.Label}: {typeError}";

            var optionCount = 0;
            var lookupCount = 0;
            if (field.TypeCode is "RADIO" or "SEARCH_SELECT" or "MULTI_SELECT")
            {
                if (field.LookupSourceId.HasValue)
                {
                    var source = await db.QueryFirstOrDefaultAsync<LookupSourceRow>(@"SELECT Id,SourceCode,SourceName,ResolverCode,MinimumSearchLength,MaximumResults
FROM form_lookup_sources WHERE Id=@Id AND IsActive=TRUE", new { Id = field.LookupSourceId.Value }, transaction);
                    if (source is null) return $"{field.Label}: the configured lookup is inactive.";
                    foreach (var selectedValue in value.SelectedOptionValues)
                    {
                        if (selectedValue.Length > 250) return $"{field.Label}: a selected lookup value is too long.";
                        var resolved = await ResolveLookupRowsAsync(db, transaction, source, clientId, "", selectedValue);
                        if (resolved.Count != 1) return $"{field.Label}: a selected lookup value is invalid or no longer active.";
                    }
                    lookupCount = value.SelectedOptionValues.Count;
                }
                else if (value.SelectedOptionIds.Count > 0)
                {
                    var validIds = (await db.QueryAsync<long>(@"SELECT Id FROM form_field_options
WHERE FieldId=@FieldId AND IsActive=TRUE AND Id IN @Ids", new { FieldId = field.Id, Ids = value.SelectedOptionIds }, transaction)).ToHashSet();
                    if (validIds.Count != value.SelectedOptionIds.Count) return $"{field.Label}: a selected option is invalid or inactive.";
                    optionCount = validIds.Count;
                }
            }

            var attachmentCount = 0;
            if (field.TypeCode == "UPLOAD")
            {
                if (!field.AttachmentFieldConfigurationId.HasValue || !field.EffectiveAttachmentConfigurationId.HasValue)
                    return $"{field.Label}: the attachment field configuration is inactive or missing.";
                var attachments = (await db.QueryAsync<AttachmentRuntimeRow>(@"SELECT id Id,public_id PublicId,file_size_bytes FileSizeBytes
FROM entity_attachments WHERE client_id=@ClientId AND entity_type='EMPLOYEE' AND entity_id=@EmployeeId
AND field_configuration_id=@ConfigurationId AND is_current=TRUE AND is_deleted=FALSE",
                    new { ClientId = clientId, EmployeeId = employeeId, ConfigurationId = field.AttachmentFieldConfigurationId.Value }, transaction)).ToList();
                attachmentCount = attachments.Count;
                if (value.AttachmentPublicIds.Count > 0)
                {
                    var liveIds = attachments.Select(item => item.PublicId).ToHashSet();
                    if (value.AttachmentPublicIds.Any(id => !liveIds.Contains(id)))
                        return $"{field.Label}: an attachment does not belong to this employee field.";
                }
                var minimum = field.IsRequired || field.RequirementScope.Equals("AllEntities", StringComparison.OrdinalIgnoreCase)
                    ? Math.Max(field.IsRequired ? 1 : 0, field.MinimumFileCount)
                    : 0;
                if (attachmentCount < minimum) return $"{field.Label}: upload at least {minimum} file(s).";
                if (field.MaximumFileCount > 0 && attachmentCount > field.MaximumFileCount)
                    return $"{field.Label}: no more than {field.MaximumFileCount} file(s) are allowed.";
            }

            var state = RuntimeValueState.From(field, value, optionCount, lookupCount, attachmentCount);
            states[field.Id] = state;
            if (field.IsRequired && !HasRuleValue(state)) return $"{field.Label} is required.";
        }

        var versionId = fields[0].FormVersionId;
        var rules = (await db.QueryAsync<DynamicFormValidationRule>(@"SELECT r.*,COALESCE(compareField.StableFieldCode,'') CompareFieldCode
FROM form_field_validation_rules r
JOIN form_fields fieldRow ON fieldRow.Id=r.FieldId AND fieldRow.FormVersionId=@VersionId AND fieldRow.IsActive=TRUE
LEFT JOIN form_fields compareField ON compareField.Id=r.CompareFieldId AND compareField.FormVersionId=@VersionId AND compareField.IsActive=TRUE
ORDER BY r.DisplayOrder,r.Id", new { VersionId = versionId }, transaction)).ToList();
        foreach (var rule in rules)
        {
            if (!states.TryGetValue(rule.FieldId, out var state)) continue;
            RuntimeValueState? compare = null;
            if (rule.CompareFieldId.HasValue && !states.TryGetValue(rule.CompareFieldId.Value, out compare))
                return $"The configured validation for '{state.Label}' is invalid. Contact HR.";
            var error = EvaluateRule(rule, state, compare);
            if (error.Length > 0) return error;
        }
        return "";
    }

    private static string ValidateTypedValue(RuntimeField field, EmployeeAttributeValue value)
    {
        var text = value.TextValue?.Trim() ?? "";
        var optionIds = value.SelectedOptionIds ?? [];
        var lookupValues = value.SelectedOptionValues ?? [];
        var attachments = value.AttachmentPublicIds ?? [];
        var hasSelections = optionIds.Count > 0 || lookupValues.Count > 0;
        var hasNumber = value.IntegerValue.HasValue || value.DecimalValue.HasValue;
        var hasDate = value.DateValue.HasValue || value.DateTimeValue.HasValue;
        switch (field.TypeCode)
        {
            case "TEXT":
            case "TEXTAREA":
            case "EMAIL":
            case "PHONE":
                if (hasNumber || hasDate || value.BooleanValue.HasValue || hasSelections || attachments.Count > 0) return "the submitted value type does not match this text field.";
                if (text.Length > 0 && field.MinimumLength.HasValue && text.Length < field.MinimumLength.Value) return $"value must contain at least {field.MinimumLength.Value} characters.";
                if (field.MaximumLength.HasValue && text.Length > field.MaximumLength.Value) return $"value cannot exceed {field.MaximumLength.Value} characters.";
                if (field.TypeCode == "EMAIL" && text.Length > 0 && !ValidEmail(text)) return "enter a valid email address.";
                if (field.TypeCode == "PHONE" && text.Length > 0 && NormalizePhone(text).Length is < 7 or > 15) return "enter a valid phone number.";
                return "";
            case "NUMBER":
                if (text.Length > 0 || hasDate || value.BooleanValue.HasValue || hasSelections || attachments.Count > 0) return "the submitted value type does not match this number field.";
                if (value.IntegerValue.HasValue && value.DecimalValue.HasValue) return "submit either an integer or a decimal value, not both.";
                var number = value.DecimalValue ?? (value.IntegerValue.HasValue ? (decimal)value.IntegerValue.Value : null);
                if (number.HasValue && field.MinimumNumber.HasValue && number.Value < field.MinimumNumber.Value) return $"value cannot be below {field.MinimumNumber.Value}.";
                if (number.HasValue && field.MaximumNumber.HasValue && number.Value > field.MaximumNumber.Value) return $"value cannot exceed {field.MaximumNumber.Value}.";
                return "";
            case "DATE":
                if (text.Length > 0 || hasNumber || value.DateTimeValue.HasValue || value.BooleanValue.HasValue || hasSelections || attachments.Count > 0) return "the submitted value type does not match this date field.";
                if (value.DateValue.HasValue && field.MinimumDate.HasValue && value.DateValue.Value.Date < field.MinimumDate.Value.Date) return $"date cannot be before {field.MinimumDate.Value:dd-MMM-yyyy}.";
                if (value.DateValue.HasValue && field.MaximumDate.HasValue && value.DateValue.Value.Date > field.MaximumDate.Value.Date) return $"date cannot be after {field.MaximumDate.Value:dd-MMM-yyyy}.";
                return "";
            case "DATETIME":
                if (text.Length > 0 || hasNumber || value.DateValue.HasValue || value.BooleanValue.HasValue || hasSelections || attachments.Count > 0) return "the submitted value type does not match this date-time field.";
                if (value.DateTimeValue.HasValue && field.MinimumDate.HasValue && value.DateTimeValue.Value.Date < field.MinimumDate.Value.Date) return $"date cannot be before {field.MinimumDate.Value:dd-MMM-yyyy}.";
                if (value.DateTimeValue.HasValue && field.MaximumDate.HasValue && value.DateTimeValue.Value.Date > field.MaximumDate.Value.Date) return $"date cannot be after {field.MaximumDate.Value:dd-MMM-yyyy}.";
                return "";
            case "CHECKBOX":
                return text.Length > 0 || hasNumber || hasDate || hasSelections || attachments.Count > 0 ? "the submitted value type does not match this checkbox field." : "";
            case "RADIO":
            case "SEARCH_SELECT":
                if (text.Length > 0 || hasNumber || hasDate || value.BooleanValue.HasValue || attachments.Count > 0) return "submit this field using its configured selection.";
                if (field.LookupSourceId.HasValue && optionIds.Count > 0) return "submit this lookup field using selected lookup values.";
                if (!field.LookupSourceId.HasValue && lookupValues.Count > 0) return "submit this field using configured option identifiers.";
                if ((field.LookupSourceId.HasValue ? lookupValues.Count : optionIds.Count) > 1) return "only one option can be selected.";
                return "";
            case "MULTI_SELECT":
                if (text.Length > 0 || hasNumber || hasDate || value.BooleanValue.HasValue || attachments.Count > 0) return "submit this field using its configured selections.";
                if (field.LookupSourceId.HasValue && optionIds.Count > 0) return "submit this lookup field using selected lookup values.";
                if (!field.LookupSourceId.HasValue && lookupValues.Count > 0) return "submit this field using configured option identifiers.";
                return "";
            case "UPLOAD":
                return text.Length > 0 || hasNumber || hasDate || value.BooleanValue.HasValue || hasSelections ? "upload files through the configured global attachment endpoint." : "";
            default:
                return "unsupported form field type.";
        }
    }

    private static string EvaluateRule(DynamicFormValidationRule rule, RuntimeValueState field, RuntimeValueState? compareField)
    {
        var type = CanonicalRuleType(rule.RuleType, field.TypeCode, rule.CompareFieldId.HasValue);
        var hasValue = HasRuleValue(field);
        if (type == "REQUIRED") return hasValue ? "" : RuleError(rule, $"{field.Label} is required.");
        if (type == "BOOLEAN_TRUE") return field.BooleanValue == true ? "" : RuleError(rule, $"{field.Label} must be accepted.");
        if (!hasValue) return "";
        try
        {
            return type switch
            {
                "REGEX" => !string.IsNullOrWhiteSpace(rule.TextValue) && Regex.IsMatch(field.TextValue ?? "", rule.TextValue, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250)) ? "" : RuleError(rule, $"{field.Label} is invalid."),
                "EMAIL" => ValidEmail(field.TextValue ?? "") ? "" : RuleError(rule, $"Enter a valid {field.Label.ToLowerInvariant()}."),
                "PHONE" => NormalizePhone(field.TextValue ?? "").Length is >= 7 and <= 15 ? "" : RuleError(rule, $"Enter a valid {field.Label.ToLowerInvariant()}."),
                "DATE" => TryGetRuleDate(field, out _) ? "" : RuleError(rule, $"Enter a valid {field.Label.ToLowerInvariant()}."),
                "MIN_LENGTH" => (field.TextValue?.Trim().Length ?? 0) >= (rule.IntegerValue ?? 0) ? "" : RuleError(rule, $"{field.Label} is too short."),
                "MAX_LENGTH" => (field.TextValue?.Trim().Length ?? 0) <= (rule.IntegerValue ?? long.MaxValue) ? "" : RuleError(rule, $"{field.Label} is too long."),
                "MIN_NUMBER" => TryGetRuleNumber(field, out var min) && RuleNumberConstant(rule).HasValue && min >= RuleNumberConstant(rule)!.Value ? "" : RuleError(rule, $"{field.Label} is below the allowed value."),
                "MAX_NUMBER" => TryGetRuleNumber(field, out var max) && RuleNumberConstant(rule).HasValue && max <= RuleNumberConstant(rule)!.Value ? "" : RuleError(rule, $"{field.Label} exceeds the allowed value."),
                "MIN_DATE" => TryGetRuleDate(field, out var minDate) && rule.DateValue.HasValue && minDate.Date >= rule.DateValue.Value.Date ? "" : RuleError(rule, $"{field.Label} is before the allowed date."),
                "MAX_DATE" => TryGetRuleDate(field, out var maxDate) && rule.DateValue.HasValue && maxDate.Date <= rule.DateValue.Value.Date ? "" : RuleError(rule, $"{field.Label} is after the allowed date."),
                "COMPARE_VALUE" => TryCompare(field, null, rule, out var valuePassed) && valuePassed ? "" : RuleError(rule, $"{field.Label} does not satisfy the configured comparison."),
                "COMPARE_FIELD" => compareField is not null && HasRuleValue(compareField) && TryCompare(field, compareField, rule, out var fieldPassed) && fieldPassed ? "" : RuleError(rule, $"{field.Label} does not satisfy the configured field comparison."),
                _ => $"The configured validation for '{field.Label}' is invalid. Contact HR."
            };
        }
        catch (ArgumentException) { return $"The configured validation for '{field.Label}' is invalid. Contact HR."; }
        catch (RegexMatchTimeoutException) { return $"The configured validation for '{field.Label}' is invalid. Contact HR."; }
    }

    private static async Task<(EmployeeAttributeRevision? Revision, string Error)> InsertRevisionAsync(
        MySqlConnection db,
        MySqlTransaction transaction,
        EmployeeAttributeForm form,
        int employeeId,
        int clientId,
        string infotypeCode,
        DateTime effectiveFromUtc,
        string sourceCode,
        string sourceReference,
        string changeReason,
        int changedByUserId,
        IReadOnlyList<RuntimeField> fields,
        IReadOnlyDictionary<long, EmployeeAttributeValue> values)
    {
        effectiveFromUtc = Utc(effectiveFromUtc);
        var history = (await db.QueryAsync<SubmissionHistoryRow>(@"SELECT s.Id,COALESCE(s.RevisionNumber,0) RevisionNumber,
COALESCE(s.EffectiveFromUtc,s.SubmittedAtUtc,s.StartedAtUtc) EffectiveFromUtc,s.EffectiveToUtc
FROM form_submissions s
JOIN form_versions versionRow ON versionRow.Id=s.FormVersionId AND versionRow.FormDefinitionId=@FormDefinitionId
WHERE s.ClientId=@ClientId AND s.EntityType='EMPLOYEE' AND s.EntityId=@EmployeeId AND s.Status='Submitted'
AND COALESCE(s.EmployeeInfotypeCode,'0002')=@InfotypeCode
ORDER BY COALESCE(s.EffectiveFromUtc,s.SubmittedAtUtc,s.StartedAtUtc),s.Id FOR UPDATE",
            new { form.FormDefinitionId, ClientId = clientId, EmployeeId = employeeId, InfotypeCode = infotypeCode }, transaction)).ToList();
        if (history.Any(row => Utc(row.EffectiveFromUtc) == effectiveFromUtc))
            return (null, "A revision already exists at the selected effective date and time.");

        var predecessor = history.Where(row => Utc(row.EffectiveFromUtc) < effectiveFromUtc).OrderByDescending(row => row.EffectiveFromUtc).ThenByDescending(row => row.Id).FirstOrDefault();
        var successor = history.Where(row => Utc(row.EffectiveFromUtc) > effectiveFromUtc).OrderBy(row => row.EffectiveFromUtc).ThenBy(row => row.Id).FirstOrDefault();
        if (predecessor is not null)
            await db.ExecuteAsync("UPDATE form_submissions SET EffectiveToUtc=@EffectiveToUtc,UpdatedAtUtc=UTC_TIMESTAMP(6) WHERE Id=@Id",
                new { EffectiveToUtc = effectiveFromUtc, predecessor.Id }, transaction);

        var revisionNumber = history.Select(row => row.RevisionNumber).DefaultIfEmpty(0).Max() + 1;
        var submissionId = await db.ExecuteScalarAsync<long>(@"INSERT INTO form_submissions
(FormVersionId,ClientId,EntityType,EntityId,Status,StartedAtUtc,SubmittedAtUtc,EmployeeFormBindingId,EmployeeInfotypeCode,
RevisionNumber,EffectiveFromUtc,EffectiveToUtc,PreviousSubmissionId,SourceCode,SourceReference,ChangeReason,ChangedByUserId)
VALUES (@FormVersionId,@ClientId,'EMPLOYEE',@EmployeeId,'Submitted',UTC_TIMESTAMP(6),UTC_TIMESTAMP(6),@BindingId,@InfotypeCode,
@RevisionNumber,@EffectiveFromUtc,@EffectiveToUtc,@PreviousSubmissionId,@SourceCode,@SourceReference,@ChangeReason,@ChangedByUserId);
SELECT LAST_INSERT_ID();", new
        {
            FormVersionId = form.Id,
            ClientId = clientId,
            EmployeeId = employeeId,
            BindingId = form.BindingId,
            InfotypeCode = infotypeCode,
            RevisionNumber = revisionNumber,
            EffectiveFromUtc = effectiveFromUtc,
            EffectiveToUtc = successor?.EffectiveFromUtc,
            PreviousSubmissionId = predecessor?.Id,
            SourceCode = sourceCode,
            SourceReference = sourceReference,
            ChangeReason = changeReason,
            ChangedByUserId = changedByUserId
        }, transaction);

        foreach (var field in fields)
        {
            var value = values.GetValueOrDefault(field.Id) ?? new EmployeeAttributeValue { FieldId = field.Id };
            if (HasScalarValue(field.TypeCode, value))
                await db.ExecuteAsync(@"INSERT INTO form_submission_values
(SubmissionId,FieldId,TextValue,IntegerValue,DecimalValue,DateValue,DateTimeValue,BooleanValue)
VALUES (@SubmissionId,@FieldId,@TextValue,@IntegerValue,@DecimalValue,@DateValue,@DateTimeValue,@BooleanValue)", new
                {
                    SubmissionId = submissionId,
                    FieldId = field.Id,
                    TextValue = value.TextValue?.Trim(),
                    value.IntegerValue,
                    value.DecimalValue,
                    DateValue = value.DateValue?.Date,
                    DateTimeValue = value.DateTimeValue.HasValue ? Utc(value.DateTimeValue.Value) : (DateTime?)null,
                    value.BooleanValue
                }, transaction);

            foreach (var optionId in (value.SelectedOptionIds ?? []).Where(id => id > 0).Distinct())
                await db.ExecuteAsync(@"INSERT INTO form_submission_selected_options (SubmissionId,FieldId,OptionId)
VALUES (@SubmissionId,@FieldId,@OptionId)", new { SubmissionId = submissionId, FieldId = field.Id, OptionId = optionId }, transaction);

            if (field.LookupSourceId.HasValue)
            {
                var source = await db.QueryFirstOrDefaultAsync<LookupSourceRow>(@"SELECT Id,SourceCode,SourceName,ResolverCode,MinimumSearchLength,MaximumResults
FROM form_lookup_sources WHERE Id=@Id AND IsActive=TRUE", new { Id = field.LookupSourceId.Value }, transaction);
                if (source is null) return (null, $"{field.Label}: the configured lookup is inactive.");
                var order = 0;
                foreach (var selectedValue in (value.SelectedOptionValues ?? []).Select(item => (item ?? "").Trim()).Where(item => item.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var resolved = (await ResolveLookupRowsAsync(db, transaction, source, clientId, "", selectedValue)).SingleOrDefault();
                    if (resolved is null) return (null, $"{field.Label}: a selected lookup value is invalid or inactive.");
                    await db.ExecuteAsync(@"INSERT INTO form_submission_lookup_values
(SubmissionId,FieldId,SelectedValue,DisplayLabel,DisplayOrder)
VALUES (@SubmissionId,@FieldId,@SelectedValue,@DisplayLabel,@DisplayOrder)", new
                    {
                        SubmissionId = submissionId,
                        FieldId = field.Id,
                        SelectedValue = resolved.Value,
                        DisplayLabel = resolved.Label,
                        DisplayOrder = ++order * 10
                    }, transaction);
                }
            }

            if (field.TypeCode == "UPLOAD" && field.AttachmentFieldConfigurationId.HasValue)
            {
                var attachments = await db.QueryAsync<AttachmentRuntimeRow>(@"SELECT id Id,public_id PublicId,file_size_bytes FileSizeBytes
FROM entity_attachments WHERE client_id=@ClientId AND entity_type='EMPLOYEE' AND entity_id=@EmployeeId
AND field_configuration_id=@ConfigurationId AND is_current=TRUE AND is_deleted=FALSE ORDER BY uploaded_at_utc,id",
                    new { ClientId = clientId, EmployeeId = employeeId, ConfigurationId = field.AttachmentFieldConfigurationId.Value }, transaction);
                foreach (var attachment in attachments)
                    await db.ExecuteAsync(@"INSERT IGNORE INTO form_submission_attachments
(SubmissionId,FieldId,AttachmentId,AttachmentPublicId) VALUES (@SubmissionId,@FieldId,@AttachmentId,@PublicId)",
                        new { SubmissionId = submissionId, FieldId = field.Id, AttachmentId = attachment.Id, PublicId = attachment.PublicId.ToString() }, transaction);
            }
        }

        if (successor is not null)
            await db.ExecuteAsync("UPDATE form_submissions SET PreviousSubmissionId=@PreviousSubmissionId,UpdatedAtUtc=UTC_TIMESTAMP(6) WHERE Id=@Id",
                new { PreviousSubmissionId = submissionId, successor.Id }, transaction);
        await db.ExecuteAsync(@"INSERT INTO form_submission_events
(SubmissionId,EventType,EventSummary,ActorUserId) VALUES (@SubmissionId,'EMPLOYEE_ATTRIBUTE_REVISION',@Summary,@ActorUserId)",
            new { SubmissionId = submissionId, Summary = Truncate(changeReason, 500), ActorUserId = changedByUserId }, transaction);

        return (new EmployeeAttributeRevision
        {
            SubmissionId = submissionId,
            BindingId = form.BindingId,
            FormDefinitionId = form.FormDefinitionId,
            FormVersionId = form.Id,
            RevisionNumber = revisionNumber,
            EffectiveFromUtc = effectiveFromUtc,
            EffectiveToUtc = successor?.EffectiveFromUtc,
            PreviousSubmissionId = predecessor?.Id,
            SourceCode = sourceCode,
            SourceReference = sourceReference,
            ChangeReason = changeReason,
            ChangedByUserId = changedByUserId,
            SubmittedAtUtc = DateTime.UtcNow
        }, "");
    }

    private static bool HasScalarValue(string typeCode, EmployeeAttributeValue value) => typeCode switch
    {
        "TEXT" or "TEXTAREA" or "EMAIL" or "PHONE" => !string.IsNullOrWhiteSpace(value.TextValue),
        "NUMBER" => value.IntegerValue.HasValue || value.DecimalValue.HasValue,
        "DATE" => value.DateValue.HasValue,
        "DATETIME" => value.DateTimeValue.HasValue,
        "CHECKBOX" => value.BooleanValue.HasValue,
        _ => false
    };

    private static bool HasRuleValue(RuntimeValueState field) => field.TypeCode switch
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

    private static bool TryGetRuleNumber(RuntimeValueState field, out decimal value)
    {
        if (field.DecimalValue.HasValue) { value = field.DecimalValue.Value; return true; }
        if (field.IntegerValue.HasValue) { value = field.IntegerValue.Value; return true; }
        value = 0;
        return false;
    }

    private static bool TryGetRuleDate(RuntimeValueState field, out DateTime value)
    {
        if (field.TypeCode == "DATETIME" && field.DateTimeValue.HasValue) { value = field.DateTimeValue.Value; return true; }
        if (field.DateValue.HasValue) { value = field.DateValue.Value; return true; }
        value = default;
        return false;
    }

    private static bool TryCompare(RuntimeValueState field, RuntimeValueState? compareField, DynamicFormValidationRule rule, out bool passed)
    {
        passed = false;
        var comparison = 0;
        if (IsTextField(field.TypeCode))
        {
            var right = compareField?.TextValue ?? rule.TextValue;
            if (right is null) return false;
            comparison = string.Compare(field.TextValue?.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        else if (field.TypeCode == "NUMBER")
        {
            if (!TryGetRuleNumber(field, out var left)) return false;
            decimal? right = null;
            if (compareField is not null && TryGetRuleNumber(compareField, out var comparedNumber)) right = comparedNumber;
            else if (compareField is null) right = RuleNumberConstant(rule);
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
        passed = NormalizeComparisonOperator(rule.ComparisonOperator) switch
        {
            "EQ" => comparison == 0,
            "NE" => comparison != 0,
            "GT" => comparison > 0,
            "GTE" => comparison >= 0,
            "LT" => comparison < 0,
            "LTE" => comparison <= 0,
            _ => false
        };
        return true;
    }

    private static async Task<List<EmployeeAttributeLookupOption>> ResolveLookupRowsAsync(
        MySqlConnection db,
        MySqlTransaction? transaction,
        LookupSourceRow source,
        int clientId,
        string search,
        string? exactValue)
    {
        var limit = Math.Clamp(source.MaximumResults, 1, 200);
        var args = new { ClientId = clientId, Search = (search ?? "").Trim(), Exact = (exactValue ?? "").Trim(), Limit = exactValue is null ? limit : 1 };
        IEnumerable<EmployeeAttributeLookupOption> rows = source.ResolverCode.ToUpperInvariant() switch
        {
            "CLIENTS" => await db.QueryAsync<EmployeeAttributeLookupOption>(exactValue is null
                ? "SELECT CAST(Id AS CHAR) Value,Name Label,'' Description FROM clients WHERE Id=@ClientId AND IsActive=TRUE AND (@Search='' OR Name LIKE CONCAT('%',@Search,'%')) LIMIT @Limit"
                : "SELECT CAST(Id AS CHAR) Value,Name Label,'' Description FROM clients WHERE Id=@ClientId AND IsActive=TRUE AND CAST(Id AS CHAR)=@Exact LIMIT 1", args, transaction),
            "DEPARTMENTS_BY_CLIENT" => await db.QueryAsync<EmployeeAttributeLookupOption>(exactValue is null
                ? "SELECT Department Value,Department Label,'' Description FROM employees WHERE ClientId=@ClientId AND IsActive=TRUE AND Department<>'' AND (@Search='' OR Department LIKE CONCAT('%',@Search,'%')) GROUP BY Department ORDER BY Department LIMIT @Limit"
                : "SELECT Department Value,Department Label,'' Description FROM employees WHERE ClientId=@ClientId AND IsActive=TRUE AND Department=@Exact GROUP BY Department LIMIT 1", args, transaction),
            "WORK_LOCATIONS_BY_CLIENT" => await db.QueryAsync<EmployeeAttributeLookupOption>(exactValue is null
                ? "SELECT CAST(Id AS CHAR) Value,CONCAT(Name,CASE WHEN City='' THEN '' ELSE CONCAT(' - ',City) END) Label,CONCAT_WS(', ',City,State) Description FROM worklocations WHERE ClientId=@ClientId AND IsActive=TRUE AND (@Search='' OR CONCAT(Name,' ',City,' ',State) LIKE CONCAT('%',@Search,'%')) ORDER BY Name LIMIT @Limit"
                : "SELECT CAST(Id AS CHAR) Value,CONCAT(Name,CASE WHEN City='' THEN '' ELSE CONCAT(' - ',City) END) Label,CONCAT_WS(', ',City,State) Description FROM worklocations WHERE ClientId=@ClientId AND IsActive=TRUE AND CAST(Id AS CHAR)=@Exact LIMIT 1", args, transaction),
            "SKILLS" => await db.QueryAsync<EmployeeAttributeLookupOption>(exactValue is null
                ? "SELECT CAST(Id AS CHAR) Value,SkillName Label,'' Description FROM recruitment_skills WHERE (ClientId=0 OR ClientId=@ClientId) AND IsActive=TRUE AND (@Search='' OR SkillName LIKE CONCAT('%',@Search,'%')) ORDER BY SkillName LIMIT @Limit"
                : "SELECT CAST(Id AS CHAR) Value,SkillName Label,'' Description FROM recruitment_skills WHERE (ClientId=0 OR ClientId=@ClientId) AND IsActive=TRUE AND CAST(Id AS CHAR)=@Exact LIMIT 1", args, transaction),
            "EMPLOYEES_BY_CLIENT" => await db.QueryAsync<EmployeeAttributeLookupOption>(exactValue is null
                ? "SELECT CAST(Id AS CHAR) Value,CONCAT(EmployeeCode,' - ',FirstName,' ',LastName) Label,COALESCE(Department,'') Description FROM employees WHERE ClientId=@ClientId AND IsActive=TRUE AND (@Search='' OR CONCAT(EmployeeCode,' ',FirstName,' ',LastName) LIKE CONCAT('%',@Search,'%')) ORDER BY FirstName,LastName LIMIT @Limit"
                : "SELECT CAST(Id AS CHAR) Value,CONCAT(EmployeeCode,' - ',FirstName,' ',LastName) Label,COALESCE(Department,'') Description FROM employees WHERE ClientId=@ClientId AND IsActive=TRUE AND CAST(Id AS CHAR)=@Exact LIMIT 1", args, transaction),
            _ => []
        };
        return rows.ToList();
    }

    private static async Task<EmployeeFormBinding?> BindingByIdAsync(MySqlConnection db, long id) =>
        await db.QueryFirstOrDefaultAsync<EmployeeFormBinding>(@"SELECT b.Id,b.ClientId,COALESCE(c.Name,'') ClientName,b.FormDefinitionId,d.FormCode,d.FormName,
b.InfotypeCode,b.IsRequired,b.DisplayOrder,b.IsActive,FALSE IsImplicit,b.CreatedByUserId,b.CreatedAtUtc,b.UpdatedByUserId,b.UpdatedAtUtc
FROM employee_form_bindings b JOIN form_definitions d ON d.Id=b.FormDefinitionId LEFT JOIN clients c ON c.Id=b.ClientId WHERE b.Id=@Id", new { Id = id });

    private static async Task<bool> TableExistsAsync(MySqlConnection db, string table) =>
        await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM information_schema.tables
WHERE table_schema=DATABASE() AND table_name=@Table", new { Table = table }) > 0;

    private static async Task EnsureColumnAsync(MySqlConnection db, string table, string column, string definition)
    {
        if (!await TableExistsAsync(db, table)) return;
        var exists = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM information_schema.columns
WHERE table_schema=DATABASE() AND table_name=@Table AND column_name=@Column", new { Table = table, Column = column });
        if (exists == 0) await db.ExecuteAsync($"ALTER TABLE `{table}` ADD COLUMN `{column}` {definition}");
    }

    private static async Task EnsureIndexAsync(MySqlConnection db, string table, string index, string sql)
    {
        if (!await TableExistsAsync(db, table)) return;
        var exists = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM information_schema.statistics
WHERE table_schema=DATABASE() AND table_name=@Table AND index_name=@Index", new { Table = table, Index = index });
        if (exists == 0) await db.ExecuteAsync(sql);
    }

    private static async Task EnsureForeignKeyAsync(MySqlConnection db, string table, string constraint, string column, string parentTable, string parentColumn, string deleteRule)
    {
        if (!await TableExistsAsync(db, table) || !await TableExistsAsync(db, parentTable)) return;
        var columnExists = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM information_schema.columns
WHERE table_schema=DATABASE() AND table_name=@Table AND column_name=@Column", new { Table = table, Column = column });
        if (columnExists == 0) return;
        var exists = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM information_schema.table_constraints
WHERE constraint_schema=DATABASE() AND table_name=@Table AND constraint_name=@Constraint AND constraint_type='FOREIGN KEY'", new { Table = table, Constraint = constraint });
        if (exists > 0) return;
        var orphanCount = await db.ExecuteScalarAsync<long>($@"SELECT COUNT(*) FROM `{table}` child LEFT JOIN `{parentTable}` parent
ON parent.`{parentColumn}`=child.`{column}` WHERE child.`{column}` IS NOT NULL AND parent.`{parentColumn}` IS NULL");
        if (orphanCount == 0)
            await db.ExecuteAsync($"ALTER TABLE `{table}` ADD CONSTRAINT `{constraint}` FOREIGN KEY (`{column}`) REFERENCES `{parentTable}` (`{parentColumn}`) ON DELETE {deleteRule}");
    }

    private static EmployeeAttributeValue CloneValue(EmployeeAttributeValue value) => new()
    {
        FieldId = value.FieldId,
        TextValue = value.TextValue,
        IntegerValue = value.IntegerValue,
        DecimalValue = value.DecimalValue,
        DateValue = value.DateValue,
        DateTimeValue = value.DateTimeValue,
        BooleanValue = value.BooleanValue,
        SelectedOptionIds = (value.SelectedOptionIds ?? []).ToList(),
        SelectedOptionValues = (value.SelectedOptionValues ?? []).ToList(),
        AttachmentPublicIds = (value.AttachmentPublicIds ?? []).ToList()
    };

    private static bool HasValue(EmployeeAttributeValue value) =>
        !string.IsNullOrWhiteSpace(value.TextValue) || value.IntegerValue.HasValue || value.DecimalValue.HasValue || value.DateValue.HasValue ||
        value.DateTimeValue.HasValue || value.BooleanValue.HasValue || (value.SelectedOptionIds?.Count ?? 0) > 0 ||
        (value.SelectedOptionValues?.Count ?? 0) > 0 || (value.AttachmentPublicIds?.Count ?? 0) > 0;

    private static bool ClientAccess(AuthUser user, int clientId) => !user.ClientId.HasValue || user.ClientId.Value == clientId;
    private static bool CanRead(AuthUser user, int employeeId) => user.EmployeeId == employeeId || HasPermission(user, "employees.view", "employees.manage", "settings.manage", "security.manage");
    private static bool CanManage(AuthUser user) => HasPermission(user, "employees.manage", "settings.manage", "security.manage");
    private static bool HasPermission(AuthUser user, params string[] permissions) =>
        permissions.Any(permission => (user.Permissions ?? []).Contains(permission, StringComparer.OrdinalIgnoreCase));

    private static string Infotype(string? value)
    {
        var normalized = new string((value ?? "0002").Trim().ToUpperInvariant().Where(character => char.IsLetterOrDigit(character) || character == '_').ToArray());
        return Truncate(normalized.Length == 0 ? "0002" : normalized, 30);
    }

    private static string InfotypeFromPurpose(string? purposeCode)
    {
        const string prefix = "EMPLOYEE_INFOTYPE_";
        var purpose = (purposeCode ?? "").Trim().ToUpperInvariant();
        return purpose.StartsWith(prefix, StringComparison.Ordinal) ? Infotype(purpose[prefix.Length..]) : "0002";
    }

    private static DateTime Utc(DateTime value)
    {
        var utc = value.Kind switch { DateTimeKind.Utc => value, DateTimeKind.Local => value.ToUniversalTime(), _ => DateTime.SpecifyKind(value, DateTimeKind.Utc) };
        return new DateTime(utc.Ticks - utc.Ticks % 10, DateTimeKind.Utc);
    }

    private static string Code(string? value, int maximumLength, string fallback)
    {
        var normalized = string.Join("_", (value ?? "").Trim().ToUpperInvariant().Split([' ', '-', '/', '\\'], StringSplitOptions.RemoveEmptyEntries));
        return Truncate(normalized.Length == 0 ? fallback : normalized, maximumLength);
    }

    private static string Truncate(string value, int maximumLength) => string.IsNullOrEmpty(value) || value.Length <= maximumLength ? value ?? "" : value[..maximumLength];
    private static string NormalizePhone(string value) => new((value ?? "").Where(char.IsDigit).ToArray());
    private static bool ValidEmail(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 190) return false;
        var at = value.IndexOf('@');
        return at > 0 && at == value.LastIndexOf('@') && at < value.Length - 3 && value.IndexOf('.', at + 2) > at + 1;
    }

    private static List<string> ReadStringList(string value)
    {
        try { return JsonSerializer.Deserialize<List<string>>(string.IsNullOrWhiteSpace(value) ? "[]" : value) ?? []; }
        catch { return []; }
    }

    private static string CanonicalRuleType(string? ruleType, string fieldType, bool hasCompareField)
    {
        var code = Code(ruleType, 60, "");
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

    private static string NormalizeComparisonOperator(string? value) => (value ?? "").Trim().ToUpperInvariant() switch
    {
        "=" or "==" or "EQ" or "EQUAL" or "EQUALS" => "EQ",
        "!=" or "<>" or "NE" or "NOT_EQUAL" or "NOT_EQUALS" => "NE",
        ">" or "GT" => "GT", ">=" or "GTE" => "GTE", "<" or "LT" => "LT", "<=" or "LTE" => "LTE", _ => ""
    };

    private static decimal? RuleNumberConstant(DynamicFormValidationRule rule) => rule.DecimalValue ?? (rule.IntegerValue.HasValue ? (decimal)rule.IntegerValue.Value : null);
    private static bool IsTextField(string typeCode) => typeCode is "TEXT" or "TEXTAREA" or "EMAIL" or "PHONE";
    private static bool IsDateField(string typeCode) => typeCode is "DATE" or "DATETIME";
    private static string RuleError(DynamicFormValidationRule rule, string fallback) => string.IsNullOrWhiteSpace(rule.ErrorMessage) ? fallback : rule.ErrorMessage.Trim();

    private sealed class RuntimeField
    {
        public long Id { get; set; }
        public long FormVersionId { get; set; }
        public string StableFieldCode { get; set; } = "";
        public string Label { get; set; } = "";
        public string TypeCode { get; set; } = "";
        public bool IsRequired { get; set; }
        public int? MinimumLength { get; set; }
        public int? MaximumLength { get; set; }
        public decimal? MinimumNumber { get; set; }
        public decimal? MaximumNumber { get; set; }
        public DateTime? MinimumDate { get; set; }
        public DateTime? MaximumDate { get; set; }
        public long? AttachmentFieldConfigurationId { get; set; }
        public long? EffectiveAttachmentConfigurationId { get; set; }
        public long? LookupSourceId { get; set; }
        public string LookupSourceCode { get; set; } = "";
        public string LookupResolverCode { get; set; } = "";
        public int MinimumFileCount { get; set; }
        public int MaximumFileCount { get; set; } = 1;
        public string RequirementScope { get; set; } = "NewEntitiesOnly";
    }

    private sealed class RuntimeValueState
    {
        public long FieldId { get; set; }
        public string StableFieldCode { get; set; } = "";
        public string Label { get; set; } = "";
        public string TypeCode { get; set; } = "";
        public string? TextValue { get; set; }
        public long? IntegerValue { get; set; }
        public decimal? DecimalValue { get; set; }
        public DateTime? DateValue { get; set; }
        public DateTime? DateTimeValue { get; set; }
        public bool? BooleanValue { get; set; }
        public int SelectedOptionCount { get; set; }
        public int LookupValueCount { get; set; }
        public int AttachmentCount { get; set; }
        public static RuntimeValueState From(RuntimeField field, EmployeeAttributeValue value, int selectedOptionCount, int lookupValueCount, int attachmentCount) => new()
        {
            FieldId = field.Id, StableFieldCode = field.StableFieldCode, Label = field.Label, TypeCode = field.TypeCode,
            TextValue = value.TextValue, IntegerValue = value.IntegerValue, DecimalValue = value.DecimalValue,
            DateValue = value.DateValue, DateTimeValue = value.DateTimeValue, BooleanValue = value.BooleanValue,
            SelectedOptionCount = selectedOptionCount, LookupValueCount = lookupValueCount, AttachmentCount = attachmentCount
        };
    }

    private sealed class LookupSourceRow { public long Id { get; set; } public string SourceCode { get; set; } = ""; public string SourceName { get; set; } = ""; public string ResolverCode { get; set; } = ""; public int MinimumSearchLength { get; set; } public int MaximumResults { get; set; } = 50; }
    private sealed class AttachmentRuntimeRow { public long Id { get; set; } public Guid PublicId { get; set; } public long FileSizeBytes { get; set; } }
    private sealed class SubmissionHistoryRow { public long Id { get; set; } public int RevisionNumber { get; set; } public DateTime EffectiveFromUtc { get; set; } public DateTime? EffectiveToUtc { get; set; } }
    private sealed class StoredScalarRow { public string StableFieldCode { get; set; } = ""; public string? TextValue { get; set; } public long? IntegerValue { get; set; } public decimal? DecimalValue { get; set; } public DateTime? DateValue { get; set; } public DateTime? DateTimeValue { get; set; } public bool? BooleanValue { get; set; } }
    private sealed class StoredOptionRow { public string StableFieldCode { get; set; } = ""; public string OptionCode { get; set; } = ""; }
    private sealed class StoredLookupRow { public string StableFieldCode { get; set; } = ""; public string SelectedValue { get; set; } = ""; public string DisplayLabel { get; set; } = ""; public int DisplayOrder { get; set; } }
    private sealed class FieldSemanticRow { public long FieldId { get; set; } public string SemanticCode { get; set; } = ""; }
}
