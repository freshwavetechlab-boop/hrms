using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO.Compression;
using Dapper;
using Microsoft.AspNetCore.DataProtection;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public class AttachmentRepository(
    IConfiguration configuration,
    IWebHostEnvironment environment,
    IDataProtectionProvider dataProtectionProvider,
    AttachmentStorageService storageService)
{
    private const long DefaultGlobalMaximumBytes = 25L * 1024 * 1024;
    private readonly IDataProtector credentialProtector = dataProtectionProvider.CreateProtector("Payroll.API.AttachmentStorageCredentials.v1");
    private MySqlConnection Connection() => new(configuration.GetConnectionString("Default"));

    public async Task InitializeAsync()
    {
        await using var db = Connection();
        await db.OpenAsync();
        await db.ExecuteAsync(@"
CREATE TABLE IF NOT EXISTS attachment_attributes (
    id BIGINT PRIMARY KEY AUTO_INCREMENT,
    client_id INT NOT NULL DEFAULT 0,
    attribute_code VARCHAR(80) NOT NULL,
    attribute_name VARCHAR(180) NOT NULL,
    description VARCHAR(500) NOT NULL DEFAULT '',
    data_classification VARCHAR(30) NOT NULL DEFAULT 'Internal',
    requires_document_number BOOLEAN NOT NULL DEFAULT FALSE,
    requires_issue_date BOOLEAN NOT NULL DEFAULT FALSE,
    requires_expiry_date BOOLEAN NOT NULL DEFAULT FALSE,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    created_by_user_id INT NULL,
    created_at_utc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    updated_by_user_id INT NULL,
    updated_at_utc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
    UNIQUE KEY UX_attachment_attributes_client_code (client_id, attribute_code),
    INDEX IX_attachment_attributes_client_active (client_id, is_active)
);
CREATE TABLE IF NOT EXISTS attachment_field_configurations (
    id BIGINT PRIMARY KEY AUTO_INCREMENT,
    client_id INT NOT NULL DEFAULT 0,
    attachment_attribute_id BIGINT NOT NULL,
    module_code VARCHAR(60) NOT NULL,
    form_code VARCHAR(100) NOT NULL,
    section_code VARCHAR(100) NOT NULL DEFAULT 'DOCUMENTS',
    field_key VARCHAR(120) NOT NULL,
    field_label VARCHAR(180) NOT NULL,
    help_text VARCHAR(500) NOT NULL DEFAULT '',
    is_required BOOLEAN NOT NULL DEFAULT FALSE,
    allow_multiple BOOLEAN NOT NULL DEFAULT FALSE,
    minimum_file_count INT NOT NULL DEFAULT 0,
    maximum_file_count INT NOT NULL DEFAULT 1,
    allowed_extensions_json JSON NOT NULL,
    allowed_mime_types_json JSON NOT NULL,
    maximum_file_size_bytes BIGINT NOT NULL,
    maximum_total_size_bytes BIGINT NULL,
    owner_can_view BOOLEAN NOT NULL DEFAULT TRUE,
    owner_can_upload BOOLEAN NOT NULL DEFAULT FALSE,
    owner_can_replace BOOLEAN NOT NULL DEFAULT FALSE,
    owner_can_delete BOOLEAN NOT NULL DEFAULT FALSE,
    requires_verification BOOLEAN NOT NULL DEFAULT FALSE,
    versioning_enabled BOOLEAN NOT NULL DEFAULT TRUE,
    requirement_scope VARCHAR(30) NOT NULL DEFAULT 'NewEntitiesOnly',
    display_order INT NOT NULL DEFAULT 100,
    effective_from_utc DATETIME(6) NULL,
    effective_until_utc DATETIME(6) NULL,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    created_by_user_id INT NULL,
    created_at_utc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    updated_by_user_id INT NULL,
    updated_at_utc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
    UNIQUE KEY UX_attachment_fields_client_form_key (client_id, module_code, form_code, field_key),
    INDEX IX_attachment_fields_lookup (client_id, module_code, form_code, is_active),
    INDEX IX_attachment_fields_attribute (attachment_attribute_id)
);
CREATE TABLE IF NOT EXISTS attachment_storage_servers (
    id BIGINT PRIMARY KEY AUTO_INCREMENT,
    server_code VARCHAR(80) NOT NULL,
    server_name VARCHAR(180) NOT NULL,
    storage_type VARCHAR(40) NOT NULL DEFAULT 'LocalFileSystem',
    base_path VARCHAR(700) NOT NULL DEFAULT '',
    service_url VARCHAR(700) NOT NULL DEFAULT '',
    credential_cipher_text TEXT NULL,
    is_read_enabled BOOLEAN NOT NULL DEFAULT TRUE,
    is_write_enabled BOOLEAN NOT NULL DEFAULT TRUE,
    is_default_write_server BOOLEAN NOT NULL DEFAULT FALSE,
    priority INT NOT NULL DEFAULT 100,
    maximum_capacity_bytes BIGINT NULL,
    warning_capacity_percent INT NOT NULL DEFAULT 85,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    last_health_check_at_utc DATETIME(6) NULL,
    last_health_check_status VARCHAR(40) NOT NULL DEFAULT 'Not checked',
    last_health_check_message VARCHAR(500) NOT NULL DEFAULT '',
    created_by_user_id INT NULL,
    created_at_utc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    updated_by_user_id INT NULL,
    updated_at_utc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
    UNIQUE KEY UX_attachment_storage_server_code (server_code),
    INDEX IX_attachment_storage_write (is_default_write_server, is_write_enabled, is_active)
);
CREATE TABLE IF NOT EXISTS entity_attachments (
    id BIGINT PRIMARY KEY AUTO_INCREMENT,
    public_id CHAR(36) NOT NULL,
    client_id INT NOT NULL,
    attachment_attribute_id BIGINT NOT NULL,
    field_configuration_id BIGINT NOT NULL,
    storage_server_id BIGINT NOT NULL,
    entity_type VARCHAR(60) NOT NULL,
    entity_id BIGINT NOT NULL,
    original_file_name VARCHAR(255) NOT NULL,
    stored_file_name VARCHAR(120) NOT NULL,
    storage_key VARCHAR(700) NOT NULL,
    file_extension VARCHAR(20) NOT NULL,
    declared_mime_type VARCHAR(150) NOT NULL DEFAULT '',
    detected_mime_type VARCHAR(150) NOT NULL,
    file_size_bytes BIGINT NOT NULL,
    sha256_hash CHAR(64) NOT NULL,
    version_number INT NOT NULL DEFAULT 1,
    document_number VARCHAR(180) NOT NULL DEFAULT '',
    issue_date DATE NULL,
    expiry_date DATE NULL,
    verification_status VARCHAR(30) NOT NULL DEFAULT 'NotRequired',
    verified_by_user_id INT NULL,
    verified_at_utc DATETIME(6) NULL,
    rejection_reason VARCHAR(500) NOT NULL DEFAULT '',
    malware_scan_status VARCHAR(30) NOT NULL DEFAULT 'NotScanned',
    malware_scanned_at_utc DATETIME(6) NULL,
    is_current BOOLEAN NOT NULL DEFAULT TRUE,
    is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
    uploaded_by_user_id INT NOT NULL,
    uploaded_at_utc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    deleted_by_user_id INT NULL,
    deleted_at_utc DATETIME(6) NULL,
    UNIQUE KEY UX_entity_attachments_public_id (public_id),
    INDEX IX_entity_attachments_entity (client_id, entity_type, entity_id, is_current, is_deleted),
    INDEX IX_entity_attachments_field (field_configuration_id, entity_type, entity_id),
    INDEX IX_entity_attachments_storage (storage_server_id),
    INDEX IX_entity_attachments_expiry (expiry_date, is_current, is_deleted),
    INDEX IX_entity_attachments_hash (sha256_hash)
);
CREATE TABLE IF NOT EXISTS attachment_access_tokens (
    id BIGINT PRIMARY KEY AUTO_INCREMENT,
    token_hash CHAR(64) NOT NULL,
    attachment_id BIGINT NOT NULL,
    issued_to_user_id INT NOT NULL,
    purpose VARCHAR(30) NOT NULL,
    use_policy VARCHAR(30) NOT NULL DEFAULT 'UntilExpiry',
    maximum_uses INT NOT NULL DEFAULT 20,
    use_count INT NOT NULL DEFAULT 0,
    expires_at_utc DATETIME(6) NOT NULL,
    last_used_at_utc DATETIME(6) NULL,
    revoked_at_utc DATETIME(6) NULL,
    created_at_utc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    UNIQUE KEY UX_attachment_access_token_hash (token_hash),
    INDEX IX_attachment_access_token_expiry (expires_at_utc, revoked_at_utc),
    INDEX IX_attachment_access_token_attachment (attachment_id)
);
CREATE TABLE IF NOT EXISTS attachment_audit_logs (
    id BIGINT PRIMARY KEY AUTO_INCREMENT,
    attachment_id BIGINT NULL,
    client_id INT NOT NULL,
    entity_type VARCHAR(60) NOT NULL DEFAULT '',
    entity_id BIGINT NULL,
    action VARCHAR(40) NOT NULL,
    actor_user_id INT NULL,
    success BOOLEAN NOT NULL,
    failure_reason VARCHAR(500) NOT NULL DEFAULT '',
    ip_address VARCHAR(80) NOT NULL DEFAULT '',
    user_agent VARCHAR(500) NOT NULL DEFAULT '',
    metadata_json JSON NOT NULL,
    created_at_utc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    INDEX IX_attachment_audit_attachment (attachment_id, created_at_utc),
    INDEX IX_attachment_audit_entity (client_id, entity_type, entity_id, created_at_utc),
    INDEX IX_attachment_audit_action (action, created_at_utc)
);");

        var count = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM attachment_storage_servers");
        if (count == 0)
        {
            var rootPath = configuration["AttachmentStorage:RootPath"];
            if (string.IsNullOrWhiteSpace(rootPath))
                rootPath = Path.Combine("App_Data", "attachments");
            await db.ExecuteAsync(@"INSERT INTO attachment_storage_servers
(server_code,server_name,storage_type,base_path,is_read_enabled,is_write_enabled,is_default_write_server,priority,is_active)
VALUES ('API_LOCAL','API local attachment storage','LocalFileSystem',@RootPath,TRUE,TRUE,TRUE,100,TRUE);", new { RootPath = rootPath });
        }
    }

    public static IReadOnlyList<AttachmentTargetOption> Targets { get; } =
    [
        new() { ModuleCode = "EMPLOYEE", ModuleName = "Employees", FormCode = "EMPLOYEE_CREATE_EDIT", FormName = "Add / Edit Employee", EntityType = "EMPLOYEE" },
        new() { ModuleCode = "EMPLOYEE", ModuleName = "Employees", FormCode = "EMPLOYEE_PROFILE", FormName = "Employee Profile", EntityType = "EMPLOYEE" },
        new() { ModuleCode = "RECRUITMENT", ModuleName = "Recruitment", FormCode = "EMPLOYEE_REFERRAL", FormName = "Employee Referral Candidate", EntityType = "CANDIDATE" },
        new() { ModuleCode = "RECRUITMENT", ModuleName = "Recruitment", FormCode = "CANDIDATE_APPLICATION", FormName = "Candidate Application", EntityType = "CANDIDATE" },
        new() { ModuleCode = "RECRUITMENT", ModuleName = "Recruitment", FormCode = "PRE_ONBOARDING", FormName = "Pre-Onboarding", EntityType = "CANDIDATE" }
    ];

    public async Task<IEnumerable<AttachmentAttribute>> GetAttributesAsync(int? clientId = null)
    {
        await using var db = Connection();
        await db.OpenAsync();
        return await db.QueryAsync<AttachmentAttribute>(@"SELECT a.id Id,a.client_id ClientId,COALESCE(c.Name,'Global') ClientName,
a.attribute_code AttributeCode,a.attribute_name AttributeName,a.description Description,a.data_classification DataClassification,
a.requires_document_number RequiresDocumentNumber,a.requires_issue_date RequiresIssueDate,a.requires_expiry_date RequiresExpiryDate,
a.is_active IsActive,a.created_by_user_id CreatedByUserId,a.created_at_utc CreatedAtUtc,a.updated_by_user_id UpdatedByUserId,a.updated_at_utc UpdatedAtUtc
FROM attachment_attributes a LEFT JOIN clients c ON c.Id=a.client_id
WHERE (@ClientId IS NULL OR a.client_id IN (0,@ClientId))
ORDER BY CASE WHEN a.client_id=0 THEN 0 ELSE 1 END,c.Name,a.attribute_name;", new { ClientId = clientId });
    }

    public async Task<(AttachmentAttribute? Item, string? Error)> SaveAttributeAsync(AttachmentAttribute item, AuthUser user)
    {
        item.ClientId = Math.Max(0, item.ClientId);
        item.AttributeCode = NormalizeCode(item.AttributeCode);
        item.AttributeName = item.AttributeName.Trim();
        item.Description = item.Description.Trim();
        item.DataClassification = NormalizeChoice(item.DataClassification, ["Public", "Internal", "Confidential", "Restricted"], "Internal");
        if (string.IsNullOrWhiteSpace(item.AttributeCode) || string.IsNullOrWhiteSpace(item.AttributeName))
            return (null, "Attribute code and name are required.");
        if (!CanManageClient(user, item.ClientId)) return (null, "You cannot configure attachments for this client.");

        await using var db = Connection();
        await db.OpenAsync();
        try
        {
            if (item.Id == 0)
                item.Id = await db.ExecuteScalarAsync<long>(@"INSERT INTO attachment_attributes
(client_id,attribute_code,attribute_name,description,data_classification,requires_document_number,requires_issue_date,requires_expiry_date,is_active,created_by_user_id,updated_by_user_id)
VALUES (@ClientId,@AttributeCode,@AttributeName,@Description,@DataClassification,@RequiresDocumentNumber,@RequiresIssueDate,@RequiresExpiryDate,@IsActive,@UserId,@UserId);
SELECT LAST_INSERT_ID();", new { item.ClientId, item.AttributeCode, item.AttributeName, item.Description, item.DataClassification, item.RequiresDocumentNumber, item.RequiresIssueDate, item.RequiresExpiryDate, item.IsActive, UserId = user.Id });
            else
                await db.ExecuteAsync(@"UPDATE attachment_attributes SET client_id=@ClientId,attribute_code=@AttributeCode,attribute_name=@AttributeName,
description=@Description,data_classification=@DataClassification,requires_document_number=@RequiresDocumentNumber,requires_issue_date=@RequiresIssueDate,
requires_expiry_date=@RequiresExpiryDate,is_active=@IsActive,updated_by_user_id=@UserId WHERE id=@Id",
                    new { item.Id, item.ClientId, item.AttributeCode, item.AttributeName, item.Description, item.DataClassification, item.RequiresDocumentNumber, item.RequiresIssueDate, item.RequiresExpiryDate, item.IsActive, UserId = user.Id });
        }
        catch (MySqlException exception) when (exception.Number == 1062)
        {
            return (null, "Attribute code already exists for the selected client.");
        }
        return ((await GetAttributesAsync(item.ClientId)).FirstOrDefault(row => row.Id == item.Id), null);
    }

    public async Task<IEnumerable<AttachmentFieldConfiguration>> GetConfigurationsAsync(int? clientId = null)
    {
        await using var db = Connection();
        await db.OpenAsync();
        return await db.QueryAsync<AttachmentFieldConfiguration>($@"{ConfigurationSelect}
WHERE (@ClientId IS NULL OR f.client_id IN (0,@ClientId))
ORDER BY f.module_code,f.form_code,f.display_order,f.field_label;", new { ClientId = clientId });
    }

    public async Task<IEnumerable<AttachmentFieldConfiguration>> GetEffectiveConfigurationsAsync(int clientId, string moduleCode, string formCode)
    {
        var rows = (await GetConfigurationsAsync(clientId))
            .Where(row => row.IsActive &&
                          row.ModuleCode.Equals(NormalizeCode(moduleCode), StringComparison.OrdinalIgnoreCase) &&
                          row.FormCode.Equals(NormalizeCode(formCode), StringComparison.OrdinalIgnoreCase) &&
                          (!row.EffectiveFromUtc.HasValue || row.EffectiveFromUtc <= DateTime.UtcNow) &&
                          (!row.EffectiveUntilUtc.HasValue || row.EffectiveUntilUtc >= DateTime.UtcNow))
            .OrderByDescending(row => row.ClientId == clientId)
            .ThenBy(row => row.DisplayOrder)
            .ToList();
        return rows.GroupBy(row => row.FieldKey, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).OrderBy(row => row.DisplayOrder);
    }

    public async Task<(AttachmentFieldConfiguration? Item, string? Error)> SaveConfigurationAsync(AttachmentFieldConfiguration item, AuthUser user)
    {
        item.ClientId = Math.Max(0, item.ClientId);
        item.ModuleCode = NormalizeCode(item.ModuleCode);
        item.FormCode = NormalizeCode(item.FormCode);
        item.SectionCode = NormalizeCode(item.SectionCode);
        item.FieldKey = NormalizeCode(item.FieldKey);
        item.FieldLabel = item.FieldLabel.Trim();
        item.HelpText = item.HelpText.Trim();
        item.MinimumFileCount = Math.Max(0, item.MinimumFileCount);
        item.MaximumFileCount = item.AllowMultiple ? Math.Clamp(item.MaximumFileCount, 1, 100) : 1;
        item.MaximumFileSizeBytes = Math.Clamp(item.MaximumFileSizeBytes, 1, GlobalMaximumBytes());
        item.AllowedExtensionsJson = NormalizeStringListJson(item.AllowedExtensionsJson, true);
        item.AllowedMimeTypesJson = NormalizeStringListJson(item.AllowedMimeTypesJson, false);
        item.RequirementScope = NormalizeChoice(item.RequirementScope, ["AllEntities", "NewEntitiesOnly"], "NewEntitiesOnly");
        if (!CanManageClient(user, item.ClientId)) return (null, "You cannot configure attachments for this client.");
        if (item.AttachmentAttributeId <= 0 || string.IsNullOrWhiteSpace(item.ModuleCode) || string.IsNullOrWhiteSpace(item.FormCode) || string.IsNullOrWhiteSpace(item.FieldKey) || string.IsNullOrWhiteSpace(item.FieldLabel))
            return (null, "Attribute, module, form, field key and label are required.");
        if (ReadJsonList(item.AllowedExtensionsJson).Count == 0) return (null, "Select at least one allowed file extension.");

        await using var db = Connection();
        await db.OpenAsync();
        var attributeClient = await db.ExecuteScalarAsync<int?>("SELECT client_id FROM attachment_attributes WHERE id=@Id AND is_active=TRUE", new { Id = item.AttachmentAttributeId });
        if (attributeClient is null || (attributeClient.Value != 0 && attributeClient.Value != item.ClientId))
            return (null, "Selected attachment attribute is not available for this client.");
        try
        {
            if (item.Id == 0)
                item.Id = await db.ExecuteScalarAsync<long>(@"INSERT INTO attachment_field_configurations
(client_id,attachment_attribute_id,module_code,form_code,section_code,field_key,field_label,help_text,is_required,allow_multiple,minimum_file_count,maximum_file_count,
allowed_extensions_json,allowed_mime_types_json,maximum_file_size_bytes,maximum_total_size_bytes,owner_can_view,owner_can_upload,owner_can_replace,owner_can_delete,
requires_verification,versioning_enabled,requirement_scope,display_order,effective_from_utc,effective_until_utc,is_active,created_by_user_id,updated_by_user_id)
VALUES (@ClientId,@AttachmentAttributeId,@ModuleCode,@FormCode,@SectionCode,@FieldKey,@FieldLabel,@HelpText,@IsRequired,@AllowMultiple,@MinimumFileCount,@MaximumFileCount,
@AllowedExtensionsJson,@AllowedMimeTypesJson,@MaximumFileSizeBytes,@MaximumTotalSizeBytes,@OwnerCanView,@OwnerCanUpload,@OwnerCanReplace,@OwnerCanDelete,
@RequiresVerification,@VersioningEnabled,@RequirementScope,@DisplayOrder,@EffectiveFromUtc,@EffectiveUntilUtc,@IsActive,@UserId,@UserId);
SELECT LAST_INSERT_ID();", ConfigParameters(item, user.Id));
            else
                await db.ExecuteAsync(@"UPDATE attachment_field_configurations SET client_id=@ClientId,attachment_attribute_id=@AttachmentAttributeId,module_code=@ModuleCode,
form_code=@FormCode,section_code=@SectionCode,field_key=@FieldKey,field_label=@FieldLabel,help_text=@HelpText,is_required=@IsRequired,
allow_multiple=@AllowMultiple,minimum_file_count=@MinimumFileCount,maximum_file_count=@MaximumFileCount,allowed_extensions_json=@AllowedExtensionsJson,
allowed_mime_types_json=@AllowedMimeTypesJson,maximum_file_size_bytes=@MaximumFileSizeBytes,maximum_total_size_bytes=@MaximumTotalSizeBytes,
owner_can_view=@OwnerCanView,owner_can_upload=@OwnerCanUpload,owner_can_replace=@OwnerCanReplace,owner_can_delete=@OwnerCanDelete,
requires_verification=@RequiresVerification,versioning_enabled=@VersioningEnabled,requirement_scope=@RequirementScope,display_order=@DisplayOrder,
effective_from_utc=@EffectiveFromUtc,effective_until_utc=@EffectiveUntilUtc,is_active=@IsActive,updated_by_user_id=@UserId WHERE id=@Id",
                    ConfigParameters(item, user.Id));
        }
        catch (MySqlException exception) when (exception.Number == 1062)
        {
            return (null, "This field key is already configured for the selected client, module and form.");
        }
        return ((await GetConfigurationsAsync(item.ClientId)).FirstOrDefault(row => row.Id == item.Id), null);
    }

    public async Task<IEnumerable<AttachmentStorageServer>> GetStorageServersAsync(bool includeCredential = false)
    {
        await using var db = Connection();
        await db.OpenAsync();
        var rows = await db.QueryAsync<StorageServerRow>(@"SELECT s.id Id,s.server_code ServerCode,s.server_name ServerName,s.storage_type StorageType,s.base_path BasePath,
s.service_url ServiceUrl,s.credential_cipher_text CredentialCipherText,s.is_read_enabled IsReadEnabled,s.is_write_enabled IsWriteEnabled,
s.is_default_write_server IsDefaultWriteServer,s.priority Priority,s.maximum_capacity_bytes MaximumCapacityBytes,s.warning_capacity_percent WarningCapacityPercent,
s.is_active IsActive,s.last_health_check_at_utc LastHealthCheckAtUtc,s.last_health_check_status LastHealthCheckStatus,
s.last_health_check_message LastHealthCheckMessage,s.created_by_user_id CreatedByUserId,s.created_at_utc CreatedAtUtc,s.updated_by_user_id UpdatedByUserId,
s.updated_at_utc UpdatedAtUtc,(SELECT COUNT(*) FROM entity_attachments a WHERE a.storage_server_id=s.id AND a.is_deleted=FALSE) LinkedAttachmentCount
FROM attachment_storage_servers s ORDER BY s.is_default_write_server DESC,s.priority,s.server_name;");
        return rows.Select(row => ToStorageServer(row, includeCredential));
    }

    public async Task<(AttachmentStorageServer? Item, string? Error)> SaveStorageServerAsync(AttachmentStorageServer item, AuthUser user)
    {
        item.ServerCode = NormalizeCode(item.ServerCode);
        item.ServerName = item.ServerName.Trim();
        item.StorageType = NormalizeChoice(item.StorageType, ["LocalFileSystem", "MountedFileSystem", "HttpFileServer"], "LocalFileSystem");
        item.BasePath = item.BasePath.Trim();
        item.ServiceUrl = item.ServiceUrl.Trim();
        item.WarningCapacityPercent = Math.Clamp(item.WarningCapacityPercent, 1, 100);
        if (string.IsNullOrWhiteSpace(item.ServerCode) || string.IsNullOrWhiteSpace(item.ServerName))
            return (null, "Server code and name are required.");
        if (item.StorageType == "HttpFileServer")
        {
            if (!Uri.TryCreate(item.ServiceUrl, UriKind.Absolute, out var remoteUri))
                return (null, "Enter a valid file server URL.");
            if (remoteUri.Scheme != Uri.UriSchemeHttps && !environment.IsDevelopment())
                return (null, "Remote file server must use HTTPS.");
        }
        if (item.StorageType != "HttpFileServer" && string.IsNullOrWhiteSpace(item.BasePath))
            return (null, "Storage folder path is required.");
        if (item.StorageType != "HttpFileServer")
        {
            try { _ = storageService.ResolveRoot(item); }
            catch (Exception exception) { return (null, exception.Message); }
        }
        if (item.IsDefaultWriteServer)
        {
            item.IsActive = true;
            item.IsWriteEnabled = true;
            item.IsReadEnabled = true;
        }

        await using var db = Connection();
        await db.OpenAsync();
        await using var transaction = await db.BeginTransactionAsync();
        try
        {
            var existingSecret = item.Id > 0
                ? await db.ExecuteScalarAsync<string?>("SELECT credential_cipher_text FROM attachment_storage_servers WHERE id=@Id", new { item.Id }, transaction)
                : null;
            var protectedCredential = string.IsNullOrWhiteSpace(item.Credential) ? existingSecret : credentialProtector.Protect(item.Credential.Trim());
            if (item.Id == 0)
                item.Id = await db.ExecuteScalarAsync<long>(@"INSERT INTO attachment_storage_servers
(server_code,server_name,storage_type,base_path,service_url,credential_cipher_text,is_read_enabled,is_write_enabled,is_default_write_server,priority,
maximum_capacity_bytes,warning_capacity_percent,is_active,created_by_user_id,updated_by_user_id)
VALUES (@ServerCode,@ServerName,@StorageType,@BasePath,@ServiceUrl,@CredentialCipherText,@IsReadEnabled,@IsWriteEnabled,@IsDefaultWriteServer,@Priority,
@MaximumCapacityBytes,@WarningCapacityPercent,@IsActive,@UserId,@UserId); SELECT LAST_INSERT_ID();",
                    new { item.ServerCode, item.ServerName, item.StorageType, item.BasePath, item.ServiceUrl, CredentialCipherText = protectedCredential, item.IsReadEnabled, item.IsWriteEnabled, item.IsDefaultWriteServer, item.Priority, item.MaximumCapacityBytes, item.WarningCapacityPercent, item.IsActive, UserId = user.Id }, transaction);
            else
            {
                var linked = await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM entity_attachments WHERE storage_server_id=@Id AND is_deleted=FALSE", new { item.Id }, transaction);
                if (linked > 0 && (!item.IsActive || !item.IsReadEnabled))
                {
                    await transaction.RollbackAsync();
                    return (null, "A storage server linked to existing files must remain active and read-enabled.");
                }
                await db.ExecuteAsync(@"UPDATE attachment_storage_servers SET server_code=@ServerCode,server_name=@ServerName,storage_type=@StorageType,base_path=@BasePath,
service_url=@ServiceUrl,credential_cipher_text=@CredentialCipherText,is_read_enabled=@IsReadEnabled,is_write_enabled=@IsWriteEnabled,
is_default_write_server=@IsDefaultWriteServer,priority=@Priority,maximum_capacity_bytes=@MaximumCapacityBytes,warning_capacity_percent=@WarningCapacityPercent,
is_active=@IsActive,updated_by_user_id=@UserId WHERE id=@Id",
                    new { item.Id, item.ServerCode, item.ServerName, item.StorageType, item.BasePath, item.ServiceUrl, CredentialCipherText = protectedCredential, item.IsReadEnabled, item.IsWriteEnabled, item.IsDefaultWriteServer, item.Priority, item.MaximumCapacityBytes, item.WarningCapacityPercent, item.IsActive, UserId = user.Id }, transaction);
            }
            if (item.IsDefaultWriteServer)
                await db.ExecuteAsync("UPDATE attachment_storage_servers SET is_default_write_server=(id=@Id),updated_by_user_id=@UserId WHERE is_default_write_server=TRUE OR id=@Id", new { item.Id, UserId = user.Id }, transaction);
            var defaultCount = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM attachment_storage_servers WHERE is_default_write_server=TRUE AND is_active=TRUE AND is_write_enabled=TRUE", transaction: transaction);
            if (defaultCount == 0)
            {
                await transaction.RollbackAsync();
                return (null, "At least one active default write server is required.");
            }
            await transaction.CommitAsync();
        }
        catch (MySqlException exception) when (exception.Number == 1062)
        {
            await transaction.RollbackAsync();
            return (null, "Storage server code already exists.");
        }
        return ((await GetStorageServersAsync()).FirstOrDefault(row => row.Id == item.Id), null);
    }

    public async Task<AttachmentStorageHealthResult> TestStorageServerAsync(long id)
    {
        var server = await GetStorageServerAsync(id, true);
        if (server is null) return new AttachmentStorageHealthResult { Healthy = false, Status = "Not found", Message = "Storage server was not found." };
        var result = await storageService.TestAsync(server, CancellationToken.None);
        await using var db = Connection();
        await db.OpenAsync();
        await db.ExecuteAsync(@"UPDATE attachment_storage_servers SET last_health_check_at_utc=UTC_TIMESTAMP(6),last_health_check_status=@Status,
last_health_check_message=@Message WHERE id=@Id", new { Id = id, result.Status, result.Message });
        return result;
    }

    public async Task<IEnumerable<EntityAttachment>> GetAttachmentsAsync(string entityType, long entityId, AuthUser user)
    {
        entityType = NormalizeCode(entityType);
        var clientId = await GetEntityClientIdAsync(entityType, entityId);
        if (clientId is null || !CanReadEntity(user, entityType, entityId, clientId.Value)) return [];
        var ownerOnly = entityType == "EMPLOYEE" && user.EmployeeId == entityId;
        await using var db = Connection();
        await db.OpenAsync();
        return await db.QueryAsync<EntityAttachment>($@"{AttachmentSelect}
WHERE a.client_id=@ClientId AND a.entity_type=@EntityType AND a.entity_id=@EntityId AND a.is_current=TRUE AND a.is_deleted=FALSE
AND (@OwnerOnly=FALSE OR f.owner_can_view=TRUE)
ORDER BY f.display_order,a.uploaded_at_utc DESC;", new { ClientId = clientId.Value, EntityType = entityType, EntityId = entityId, OwnerOnly = ownerOnly });
    }

    public async Task<(EntityAttachment? Attachment, string? Error)> UploadAsync(AttachmentUploadMetadata metadata, IFormFile file, AuthUser user, string ipAddress, string userAgent, CancellationToken cancellationToken)
    {
        metadata.EntityType = NormalizeCode(metadata.EntityType ?? "");
        metadata.DocumentNumber = metadata.DocumentNumber?.Trim() ?? "";
        ipAddress ??= "";
        userAgent ??= "";
        var clientId = await GetEntityClientIdAsync(metadata.EntityType, metadata.EntityId);
        if (clientId is null) return (null, "Target record was not found.");
        var configurationRow = await GetEffectiveConfigurationByIdAsync(metadata.FieldConfigurationId, clientId.Value);
        if (configurationRow is null) return (null, "Attachment field configuration is inactive or not applicable.");
        if (!CanUploadEntity(user, metadata.EntityType, metadata.EntityId, clientId.Value, configurationRow))
            return (null, "You are not allowed to upload this attachment.");
        var validationError = ValidateMetadata(configurationRow, metadata, file);
        if (validationError is not null) return (null, validationError);

        var inspection = await InspectFileAsync(file, cancellationToken);
        if (!inspection.Ok) return (null, inspection.Error);
        var allowedExtensions = ReadJsonList(configurationRow.AllowedExtensionsJson);
        var extension = Path.GetExtension(file.FileName).TrimStart('.').ToLowerInvariant();
        if (!allowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            return (null, $"File type .{extension} is not allowed. Allowed: {string.Join(", ", allowedExtensions.Select(value => $".{value}"))}.");
        var allowedMimeTypes = ReadJsonList(configurationRow.AllowedMimeTypesJson);
        if (allowedMimeTypes.Count > 0 && !allowedMimeTypes.Contains(inspection.DetectedMimeType, StringComparer.OrdinalIgnoreCase))
            return (null, $"Detected file type {inspection.DetectedMimeType} is not allowed.");

        var server = await GetDefaultWriteServerAsync();
        if (server is null) return (null, "No active default write storage server is configured.");
        if (server.MaximumCapacityBytes.HasValue)
        {
            await using var capacityDb = Connection();
            await capacityDb.OpenAsync(cancellationToken);
            var storedBytes = await capacityDb.ExecuteScalarAsync<long>(
                "SELECT COALESCE(SUM(file_size_bytes),0) FROM entity_attachments WHERE storage_server_id=@StorageServerId",
                new { StorageServerId = server.Id });
            if (storedBytes + file.Length > server.MaximumCapacityBytes.Value)
                return (null, "The configured write storage server has reached its capacity limit. Select another default write server.");
        }
        var publicId = Guid.NewGuid();
        var storedFileName = $"{Guid.NewGuid():N}.{extension}";
        var storageKey = BuildStorageKey(clientId.Value, metadata.EntityType, metadata.EntityId, configurationRow.AttributeCode, storedFileName);

        await using (var source = file.OpenReadStream())
            await storageService.WriteAsync(server, storageKey, source, cancellationToken);

        try
        {
            await using var db = Connection();
            await db.OpenAsync();
            await using var transaction = await db.BeginTransactionAsync();
            var current = (await db.QueryAsync<EntityAttachment>("SELECT id Id,version_number VersionNumber,file_size_bytes FileSizeBytes FROM entity_attachments WHERE client_id=@ClientId AND entity_type=@EntityType AND entity_id=@EntityId AND field_configuration_id=@FieldId AND is_current=TRUE AND is_deleted=FALSE FOR UPDATE",
                new { ClientId = clientId.Value, EntityType = metadata.EntityType, EntityId = metadata.EntityId, FieldId = configurationRow.Id }, transaction)).ToList();
            var isOwner = metadata.EntityType == "EMPLOYEE" && user.EmployeeId == metadata.EntityId;
            if (isOwner && !configurationRow.AllowMultiple && current.Count > 0 && !configurationRow.OwnerCanReplace)
            {
                await transaction.RollbackAsync();
                await storageService.DeleteAsync(server, storageKey, cancellationToken);
                return (null, "You are not allowed to replace this attachment.");
            }
            if (!configurationRow.AllowMultiple && current.Count > 0 && !configurationRow.VersioningEnabled)
            {
                await transaction.RollbackAsync();
                await storageService.DeleteAsync(server, storageKey, cancellationToken);
                return (null, "This field already has a file and replacement/versioning is disabled.");
            }
            if (configurationRow.AllowMultiple && current.Count >= configurationRow.MaximumFileCount)
            {
                await transaction.RollbackAsync();
                await storageService.DeleteAsync(server, storageKey, cancellationToken);
                return (null, $"Maximum {configurationRow.MaximumFileCount} files are allowed.");
            }
            var totalSize = current.Sum(row => row.FileSizeBytes) + file.Length;
            if (configurationRow.MaximumTotalSizeBytes.HasValue && totalSize > configurationRow.MaximumTotalSizeBytes.Value)
            {
                await transaction.RollbackAsync();
                await storageService.DeleteAsync(server, storageKey, cancellationToken);
                return (null, "Combined attachment size exceeds the configured limit.");
            }
            var version = current.Count == 0 ? 1 : current.Max(row => row.VersionNumber) + 1;
            if (!configurationRow.AllowMultiple && current.Count > 0)
                await db.ExecuteAsync("UPDATE entity_attachments SET is_current=FALSE WHERE id IN @Ids", new { Ids = current.Select(row => row.Id).ToArray() }, transaction);
            var verificationStatus = configurationRow.RequiresVerification ? "Pending" : "NotRequired";
            var id = await db.ExecuteScalarAsync<long>(@"INSERT INTO entity_attachments
(public_id,client_id,attachment_attribute_id,field_configuration_id,storage_server_id,entity_type,entity_id,original_file_name,stored_file_name,storage_key,
file_extension,declared_mime_type,detected_mime_type,file_size_bytes,sha256_hash,version_number,document_number,issue_date,expiry_date,verification_status,
uploaded_by_user_id)
VALUES (@PublicId,@ClientId,@AttributeId,@FieldId,@StorageServerId,@EntityType,@EntityId,@OriginalFileName,@StoredFileName,@StorageKey,
@Extension,@DeclaredMimeType,@DetectedMimeType,@FileSizeBytes,@Sha256Hash,@VersionNumber,@DocumentNumber,@IssueDate,@ExpiryDate,@VerificationStatus,@UserId);
SELECT LAST_INSERT_ID();", new
            {
                PublicId = publicId.ToString(),
                ClientId = clientId.Value,
                AttributeId = configurationRow.AttachmentAttributeId,
                FieldId = configurationRow.Id,
                StorageServerId = server.Id,
                metadata.EntityType,
                metadata.EntityId,
                OriginalFileName = SafeDisplayFileName(file.FileName),
                StoredFileName = storedFileName,
                StorageKey = storageKey,
                Extension = extension,
                DeclaredMimeType = file.ContentType ?? "",
                inspection.DetectedMimeType,
                FileSizeBytes = file.Length,
                inspection.Sha256Hash,
                VersionNumber = version,
                metadata.DocumentNumber,
                metadata.IssueDate,
                metadata.ExpiryDate,
                VerificationStatus = verificationStatus,
                UserId = user.Id
            }, transaction);
            await WriteAuditAsync(db, transaction, id, clientId.Value, metadata.EntityType, metadata.EntityId, "UPLOAD", user.Id, true, "", ipAddress, userAgent, new { publicId, configurationRow.FieldKey, server.ServerCode, file.Length });
            await transaction.CommitAsync();
            return (await GetAttachmentByIdAsync(id), null);
        }
        catch
        {
            await storageService.DeleteAsync(server, storageKey, cancellationToken);
            throw;
        }
    }

    public async Task<(EntityAttachment? Attachment, AttachmentStorageServer? Server, string? Error)> GetForContentAsync(Guid publicId, AuthUser user, string action, string ipAddress, string userAgent)
    {
        var row = await GetAccessRowAsync(publicId);
        if (row is null || !row.IsCurrent || row.IsDeleted) return (null, null, "Attachment was not found.");
        if (!CanReadEntity(user, row.EntityType, row.EntityId, row.ClientId) || (IsOwner(user, row) && !row.OwnerCanView))
        {
            await LogFailedAccessAsync(row, action, user.Id, "Access denied.", ipAddress, userAgent);
            return (null, null, "Attachment was not found.");
        }
        var server = await GetStorageServerAsync(row.StorageServerId, true);
        if (server is null || !server.IsActive || !server.IsReadEnabled) return (null, null, "Attachment storage is currently unavailable.");
        await LogAccessAsync(row, action, user.Id, ipAddress, userAgent);
        return (row, server, null);
    }

    public async Task<(AttachmentAccessTicket? Ticket, string? Error)> IssueAccessTicketAsync(Guid publicId, AuthUser user, string purpose, string ipAddress, string userAgent)
    {
        purpose = purpose.Equals("download", StringComparison.OrdinalIgnoreCase) ? "Download" : "Preview";
        var access = await GetForContentAsync(publicId, user, "TOKEN_ISSUED", ipAddress, userAgent);
        if (access.Attachment is null) return (null, access.Error);
        var rawToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var expiresAt = DateTime.UtcNow.AddSeconds(purpose == "Download"
            ? configuration.GetValue("AttachmentStorage:DownloadTokenLifetimeSeconds", 120)
            : configuration.GetValue("AttachmentStorage:PreviewTokenLifetimeSeconds", 300));
        await using var db = Connection();
        await db.OpenAsync();
        await db.ExecuteAsync(@"INSERT INTO attachment_access_tokens
(token_hash,attachment_id,issued_to_user_id,purpose,use_policy,maximum_uses,expires_at_utc)
VALUES (@TokenHash,@AttachmentId,@UserId,@Purpose,'UntilExpiry',@MaximumUses,@ExpiresAt);",
            new { TokenHash = HashToken(rawToken), AttachmentId = access.Attachment.Id, UserId = user.Id, Purpose = purpose, MaximumUses = purpose == "Download" ? 3 : 30, ExpiresAt = expiresAt });
        return (new AttachmentAccessTicket { Url = $"/api/public/attachments/content?token={Uri.EscapeDataString(rawToken)}", ExpiresAtUtc = expiresAt }, null);
    }

    public async Task<(EntityAttachment? Attachment, AttachmentStorageServer? Server, string? Purpose)> ConsumeAccessTicketAsync(string rawToken, string ipAddress, string userAgent)
    {
        if (string.IsNullOrWhiteSpace(rawToken)) return (null, null, null);
        await using var db = Connection();
        await db.OpenAsync();
        await using var transaction = await db.BeginTransactionAsync();
        var ticket = await db.QueryFirstOrDefaultAsync<AccessTokenRow>(@"SELECT id Id,attachment_id AttachmentId,issued_to_user_id IssuedToUserId,purpose Purpose,
maximum_uses MaximumUses,use_count UseCount FROM attachment_access_tokens
WHERE token_hash=@TokenHash AND revoked_at_utc IS NULL AND expires_at_utc>UTC_TIMESTAMP(6) AND use_count<maximum_uses FOR UPDATE",
            new { TokenHash = HashToken(rawToken) }, transaction);
        if (ticket is null) return (null, null, null);
        var row = await QueryAccessRowAsync(db, ticket.AttachmentId, transaction);
        if (row is null || !row.IsCurrent || row.IsDeleted)
        {
            await transaction.RollbackAsync();
            return (null, null, null);
        }
        await db.ExecuteAsync("UPDATE attachment_access_tokens SET use_count=use_count+1,last_used_at_utc=UTC_TIMESTAMP(6) WHERE id=@Id", new { ticket.Id }, transaction);
        await WriteAuditAsync(db, transaction, row.Id, row.ClientId, row.EntityType, row.EntityId, ticket.Purpose.ToUpperInvariant(), ticket.IssuedToUserId, true, "", ipAddress, userAgent, new { tokenId = ticket.Id });
        await transaction.CommitAsync();
        var server = await GetStorageServerAsync(row.StorageServerId, true);
        return server is null || !server.IsActive || !server.IsReadEnabled ? (null, null, null) : (row, server, ticket.Purpose);
    }

    public async Task<(bool Ok, string? Error)> DeleteAsync(Guid publicId, AuthUser user, string ipAddress, string userAgent)
    {
        var row = await GetAccessRowAsync(publicId);
        if (row is null || row.IsDeleted) return (false, "Attachment was not found.");
        var allowed = CanManageEntity(user, row.EntityType, row.ClientId) || (IsOwner(user, row) && row.OwnerCanDelete);
        if (!allowed) return (false, "You are not allowed to delete this attachment.");
        await using var db = Connection();
        await db.OpenAsync();
        await using var transaction = await db.BeginTransactionAsync();
        await db.ExecuteAsync(@"UPDATE entity_attachments SET is_deleted=TRUE,is_current=FALSE,deleted_by_user_id=@UserId,deleted_at_utc=UTC_TIMESTAMP(6)
WHERE id=@Id AND is_deleted=FALSE", new { row.Id, UserId = user.Id }, transaction);
        await WriteAuditAsync(db, transaction, row.Id, row.ClientId, row.EntityType, row.EntityId, "DELETE", user.Id, true, "", ipAddress, userAgent, new { publicId });
        await transaction.CommitAsync();
        return (true, null);
    }

    public async Task<(EntityAttachment? Item, string? Error)> ReviewAsync(Guid publicId, bool approve, string reason, AuthUser user, string ipAddress, string userAgent)
    {
        var row = await GetAccessRowAsync(publicId);
        if (row is null || row.IsDeleted) return (null, "Attachment was not found.");
        if (!CanVerifyEntity(user, row.EntityType, row.ClientId)) return (null, "You are not allowed to verify this attachment.");
        await using var db = Connection();
        await db.OpenAsync();
        await using var transaction = await db.BeginTransactionAsync();
        await db.ExecuteAsync(@"UPDATE entity_attachments SET verification_status=@Status,verified_by_user_id=@UserId,verified_at_utc=UTC_TIMESTAMP(6),
rejection_reason=@Reason WHERE id=@Id", new { row.Id, Status = approve ? "Verified" : "Rejected", UserId = user.Id, Reason = approve ? "" : reason.Trim() }, transaction);
        await WriteAuditAsync(db, transaction, row.Id, row.ClientId, row.EntityType, row.EntityId, approve ? "VERIFY" : "REJECT", user.Id, true, "", ipAddress, userAgent, new { reason });
        await transaction.CommitAsync();
        return (await GetAttachmentByIdAsync(row.Id), null);
    }

    private async Task<AttachmentFieldConfiguration?> GetEffectiveConfigurationByIdAsync(long id, int clientId)
    {
        foreach (var target in Targets)
        {
            var row = (await GetEffectiveConfigurationsAsync(clientId, target.ModuleCode, target.FormCode)).FirstOrDefault(item => item.Id == id);
            if (row is not null) return row;
        }
        return null;
    }

    private async Task<AttachmentStorageServer?> GetDefaultWriteServerAsync()
    {
        var servers = await GetStorageServersAsync(true);
        return servers.FirstOrDefault(server => server.IsDefaultWriteServer && server.IsActive && server.IsWriteEnabled);
    }

    private async Task<AttachmentStorageServer?> GetStorageServerAsync(long id, bool includeCredential)
    {
        return (await GetStorageServersAsync(includeCredential)).FirstOrDefault(row => row.Id == id);
    }

    private async Task<EntityAttachment?> GetAttachmentByIdAsync(long id)
    {
        await using var db = Connection();
        await db.OpenAsync();
        return await db.QueryFirstOrDefaultAsync<EntityAttachment>($@"{AttachmentSelect} WHERE a.id=@Id", new { Id = id });
    }

    private async Task<AttachmentAccessRow?> GetAccessRowAsync(Guid publicId)
    {
        await using var db = Connection();
        await db.OpenAsync();
        return await db.QueryFirstOrDefaultAsync<AttachmentAccessRow>($@"{AccessSelect} WHERE a.public_id=@PublicId", new { PublicId = publicId.ToString() });
    }

    private static Task<AttachmentAccessRow?> QueryAccessRowAsync(MySqlConnection db, long id, MySqlTransaction transaction) =>
        db.QueryFirstOrDefaultAsync<AttachmentAccessRow>($@"{AccessSelect} WHERE a.id=@Id", new { Id = id }, transaction);

    private async Task<int?> GetEntityClientIdAsync(string entityType, long entityId)
    {
        await using var db = Connection();
        await db.OpenAsync();
        if (entityType == "EMPLOYEE")
            return await db.ExecuteScalarAsync<int?>("SELECT ClientId FROM employees WHERE Id=@Id", new { Id = entityId });
        if (entityType == "CANDIDATE")
        {
            var candidateTableExists = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES
WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='recruitment_candidates';");
            if (candidateTableExists > 0)
            {
                var candidateClientId = await db.ExecuteScalarAsync<int?>("SELECT client_id FROM recruitment_candidates WHERE id=@Id", new { Id = entityId });
                if (candidateClientId.HasValue) return candidateClientId;
            }
            var referralTableExists = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES
WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='recruitment_employee_referrals';");
            if (referralTableExists > 0)
                return await db.ExecuteScalarAsync<int?>(@"SELECT p.ClientId FROM recruitment_employee_referrals r
JOIN recruitment_open_positions p ON p.Id=r.PositionId WHERE r.Id=@Id", new { Id = entityId });
        }
        return null;
    }

    private static bool CanManageClient(AuthUser user, int clientId) =>
        (user.ClientId is null || user.ClientId == clientId) &&
        HasAnyPermission(user, "attachment.config.manage", "settings.manage", "security.manage");

    private static bool CanReadEntity(AuthUser user, string entityType, long entityId, int clientId)
    {
        if (user.ClientId is not null && user.ClientId != clientId) return false;
        if (entityType == "EMPLOYEE" && user.EmployeeId == entityId) return true;
        return entityType == "EMPLOYEE"
            ? HasAnyPermission(user, "attachment.employee.view", "employees.view", "employees.manage", "settings.manage", "security.manage")
            : HasAnyPermission(user, "attachment.recruitment.view", "recruitment.manage", "security.manage");
    }

    private static bool CanUploadEntity(AuthUser user, string entityType, long entityId, int clientId, AttachmentFieldConfiguration config)
    {
        if (user.ClientId is not null && user.ClientId != clientId) return false;
        if (entityType == "EMPLOYEE" && user.EmployeeId == entityId) return config.OwnerCanUpload;
        return CanManageEntity(user, entityType, clientId);
    }

    private static bool CanManageEntity(AuthUser user, string entityType, int clientId)
    {
        if (user.ClientId is not null && user.ClientId != clientId) return false;
        return entityType == "EMPLOYEE"
            ? HasAnyPermission(user, "attachment.employee.upload", "employees.manage", "settings.manage", "security.manage")
            : HasAnyPermission(user, "attachment.recruitment.upload", "recruitment.manage", "security.manage");
    }

    private static bool CanVerifyEntity(AuthUser user, string entityType, int clientId)
    {
        if (user.ClientId is not null && user.ClientId != clientId) return false;
        return entityType == "EMPLOYEE"
            ? HasAnyPermission(user, "attachment.employee.verify", "employees.manage", "security.manage")
            : HasAnyPermission(user, "attachment.recruitment.verify", "recruitment.manage", "security.manage");
    }

    private static bool HasAnyPermission(AuthUser user, params string[] permissions) =>
        permissions.Any(permission => user.Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase));

    private static bool IsOwner(AuthUser user, AttachmentAccessRow row) => row.EntityType == "EMPLOYEE" && user.EmployeeId == row.EntityId;

    private string? ValidateMetadata(AttachmentFieldConfiguration config, AttachmentUploadMetadata metadata, IFormFile file)
    {
        if (file.Length <= 0) return "Select a non-empty file.";
        if (file.Length > config.MaximumFileSizeBytes) return $"File exceeds the configured {FormatBytes(config.MaximumFileSizeBytes)} limit.";
        if (file.Length > GlobalMaximumBytes()) return $"File exceeds the global {FormatBytes(GlobalMaximumBytes())} limit.";
        if (config.AttachmentAttributeId <= 0) return "Attachment attribute is invalid.";
        if (config.RequiresDocumentNumber && string.IsNullOrWhiteSpace(metadata.DocumentNumber)) return "Document number is required.";
        if (config.RequiresIssueDate && !metadata.IssueDate.HasValue) return "Issue date is required.";
        if (config.RequiresExpiryDate && !metadata.ExpiryDate.HasValue) return "Expiry date is required.";
        if (metadata.ExpiryDate.HasValue && metadata.IssueDate.HasValue && metadata.ExpiryDate < metadata.IssueDate) return "Expiry date cannot be before issue date.";
        return null;
    }

    private async Task<FileInspection> InspectFileAsync(IFormFile file, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(file.FileName).TrimStart('.').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(extension) || extension.Length > 10) return FileInspection.Fail("File extension is invalid.");
        var header = new byte[Math.Min(16, (int)Math.Min(file.Length, 16))];
        await using (var headerStream = file.OpenReadStream())
            _ = await headerStream.ReadAsync(header, cancellationToken);
        await using var hashStream = file.OpenReadStream();
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(hashStream, cancellationToken)).ToLowerInvariant();
        var mime = DetectMimeType(header, extension);
        if (mime == "application/vnd.openxmlformats-officedocument.wordprocessingml.document" && !await IsDocxAsync(file, cancellationToken))
            return FileInspection.Fail("The uploaded ZIP content is not a valid DOCX document.");
        return mime is null ? FileInspection.Fail("File content does not match an approved PDF, image or DOCX format.") : FileInspection.Success(mime, hash);
    }

    private static async Task<bool> IsDocxAsync(IFormFile file, CancellationToken cancellationToken)
    {
        try
        {
            await using var source = file.OpenReadStream();
            Stream archiveStream = source;
            MemoryStream? copy = null;
            if (!source.CanSeek)
            {
                copy = new MemoryStream((int)Math.Min(file.Length, int.MaxValue));
                await source.CopyToAsync(copy, cancellationToken);
                copy.Position = 0;
                archiveStream = copy;
            }
            using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true);
            var valid = archive.GetEntry("[Content_Types].xml") is not null &&
                        archive.GetEntry("_rels/.rels") is not null &&
                        archive.GetEntry("word/document.xml") is not null;
            if (copy is not null) await copy.DisposeAsync();
            return valid;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static string? DetectMimeType(byte[] header, string extension)
    {
        if (header.Length >= 5 && header[0] == 0x25 && header[1] == 0x50 && header[2] == 0x44 && header[3] == 0x46 && header[4] == 0x2D && extension == "pdf") return "application/pdf";
        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF && extension is "jpg" or "jpeg") return "image/jpeg";
        if (header.Length >= 8 && header.Take(8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }) && extension == "png") return "image/png";
        if (header.Length >= 4 && header[0] == 0x50 && header[1] == 0x4B && header[2] is 0x03 or 0x05 or 0x07 && header[3] is 0x04 or 0x06 or 0x08 && extension == "docx")
            return "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
        return null;
    }

    private long GlobalMaximumBytes() => Math.Max(1024, configuration.GetValue("AttachmentStorage:GlobalMaximumFileSizeBytes", DefaultGlobalMaximumBytes));

    private static string BuildStorageKey(int clientId, string entityType, long entityId, string attributeCode, string fileName)
    {
        var segments = new[] { $"client-{clientId}", entityType.ToLowerInvariant(), entityId.ToString(), NormalizePathSegment(attributeCode), DateTime.UtcNow.ToString("yyyy"), fileName };
        return string.Join("/", segments);
    }

    private static string NormalizePathSegment(string value)
    {
        var normalized = new string(value.Where(character => char.IsLetterOrDigit(character) || character is '-' or '_').ToArray()).ToLowerInvariant();
        return string.IsNullOrWhiteSpace(normalized) ? "document" : normalized;
    }

    private static string SafeDisplayFileName(string fileName)
    {
        var safe = Path.GetFileName(fileName).Trim();
        return safe.Length <= 255 ? safe : safe[^255..];
    }

    private static string NormalizeCode(string value) =>
        string.Join("_", (value ?? "").Trim().ToUpperInvariant().Split([' ', '-', '/', '\\'], StringSplitOptions.RemoveEmptyEntries));

    private static string NormalizeChoice(string value, string[] choices, string fallback) =>
        choices.FirstOrDefault(choice => choice.Equals(value?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? fallback;

    private static string NormalizeStringListJson(string value, bool extension)
    {
        var list = ReadJsonList(value)
            .Select(item => extension ? item.Trim().TrimStart('.').ToLowerInvariant() : item.Trim().ToLowerInvariant())
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return JsonSerializer.Serialize(list);
    }

    private static List<string> ReadJsonList(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        try
        {
            if (value.TrimStart().StartsWith('['))
                return JsonSerializer.Deserialize<List<string>>(value) ?? [];
        }
        catch { }
        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    private static object ConfigParameters(AttachmentFieldConfiguration item, int userId) => new
    {
        item.Id, item.ClientId, item.AttachmentAttributeId, item.ModuleCode, item.FormCode, item.SectionCode, item.FieldKey, item.FieldLabel, item.HelpText,
        item.IsRequired, item.AllowMultiple, item.MinimumFileCount, item.MaximumFileCount, item.AllowedExtensionsJson, item.AllowedMimeTypesJson,
        item.MaximumFileSizeBytes, item.MaximumTotalSizeBytes, item.OwnerCanView, item.OwnerCanUpload, item.OwnerCanReplace, item.OwnerCanDelete,
        item.RequiresVerification, item.VersioningEnabled, item.RequirementScope, item.DisplayOrder, item.EffectiveFromUtc, item.EffectiveUntilUtc, item.IsActive,
        UserId = userId
    };

    private AttachmentStorageServer ToStorageServer(StorageServerRow row, bool includeCredential)
    {
        var credential = "";
        if (includeCredential && !string.IsNullOrWhiteSpace(row.CredentialCipherText))
        {
            try { credential = credentialProtector.Unprotect(row.CredentialCipherText); }
            catch { credential = ""; }
        }
        return new AttachmentStorageServer
        {
            Id = row.Id, ServerCode = row.ServerCode, ServerName = row.ServerName, StorageType = row.StorageType, BasePath = row.BasePath,
            ServiceUrl = row.ServiceUrl, Credential = credential, HasCredential = !string.IsNullOrWhiteSpace(row.CredentialCipherText),
            IsReadEnabled = row.IsReadEnabled, IsWriteEnabled = row.IsWriteEnabled, IsDefaultWriteServer = row.IsDefaultWriteServer,
            Priority = row.Priority, MaximumCapacityBytes = row.MaximumCapacityBytes, WarningCapacityPercent = row.WarningCapacityPercent,
            IsActive = row.IsActive, LastHealthCheckAtUtc = row.LastHealthCheckAtUtc, LastHealthCheckStatus = row.LastHealthCheckStatus,
            LastHealthCheckMessage = row.LastHealthCheckMessage, LinkedAttachmentCount = row.LinkedAttachmentCount,
            CreatedByUserId = row.CreatedByUserId, CreatedAtUtc = row.CreatedAtUtc, UpdatedByUserId = row.UpdatedByUserId, UpdatedAtUtc = row.UpdatedAtUtc
        };
    }

    private async Task LogAccessAsync(AttachmentAccessRow row, string action, int actorUserId, string ipAddress, string userAgent)
    {
        await using var db = Connection();
        await db.OpenAsync();
        await WriteAuditAsync(db, null, row.Id, row.ClientId, row.EntityType, row.EntityId, action, actorUserId, true, "", ipAddress, userAgent, new { row.PublicId });
    }

    private async Task LogFailedAccessAsync(AttachmentAccessRow row, string action, int actorUserId, string reason, string ipAddress, string userAgent)
    {
        await using var db = Connection();
        await db.OpenAsync();
        await WriteAuditAsync(db, null, row.Id, row.ClientId, row.EntityType, row.EntityId, action, actorUserId, false, reason, ipAddress, userAgent, new { row.PublicId });
    }

    private static Task WriteAuditAsync(MySqlConnection db, MySqlTransaction? transaction, long? attachmentId, int clientId, string entityType, long? entityId,
        string action, int? actorUserId, bool success, string failureReason, string ipAddress, string userAgent, object metadata) =>
        db.ExecuteAsync(@"INSERT INTO attachment_audit_logs
(attachment_id,client_id,entity_type,entity_id,action,actor_user_id,success,failure_reason,ip_address,user_agent,metadata_json)
VALUES (@AttachmentId,@ClientId,@EntityType,@EntityId,@Action,@ActorUserId,@Success,@FailureReason,@IpAddress,@UserAgent,@MetadataJson);",
            new { AttachmentId = attachmentId, ClientId = clientId, EntityType = entityType, EntityId = entityId, Action = action, ActorUserId = actorUserId, Success = success, FailureReason = failureReason, IpAddress = ipAddress, UserAgent = userAgent, MetadataJson = JsonSerializer.Serialize(metadata) }, transaction);

    private static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
    private static string FormatBytes(long value) => value >= 1024 * 1024 ? $"{value / 1024d / 1024d:0.##} MB" : $"{value / 1024d:0.##} KB";

    private const string ConfigurationSelect = @"SELECT f.id Id,f.client_id ClientId,COALESCE(c.Name,'Global') ClientName,
f.attachment_attribute_id AttachmentAttributeId,a.attribute_code AttributeCode,a.attribute_name AttributeName,
a.data_classification DataClassification,a.requires_document_number RequiresDocumentNumber,a.requires_issue_date RequiresIssueDate,
a.requires_expiry_date RequiresExpiryDate,
f.module_code ModuleCode,f.form_code FormCode,f.section_code SectionCode,f.field_key FieldKey,f.field_label FieldLabel,f.help_text HelpText,
f.is_required IsRequired,f.allow_multiple AllowMultiple,f.minimum_file_count MinimumFileCount,f.maximum_file_count MaximumFileCount,
CAST(f.allowed_extensions_json AS CHAR) AllowedExtensionsJson,CAST(f.allowed_mime_types_json AS CHAR) AllowedMimeTypesJson,
f.maximum_file_size_bytes MaximumFileSizeBytes,f.maximum_total_size_bytes MaximumTotalSizeBytes,f.owner_can_view OwnerCanView,
f.owner_can_upload OwnerCanUpload,f.owner_can_replace OwnerCanReplace,f.owner_can_delete OwnerCanDelete,f.requires_verification RequiresVerification,
f.versioning_enabled VersioningEnabled,f.requirement_scope RequirementScope,f.display_order DisplayOrder,f.effective_from_utc EffectiveFromUtc,
f.effective_until_utc EffectiveUntilUtc,f.is_active IsActive,f.created_by_user_id CreatedByUserId,f.created_at_utc CreatedAtUtc,
f.updated_by_user_id UpdatedByUserId,f.updated_at_utc UpdatedAtUtc
FROM attachment_field_configurations f
JOIN attachment_attributes a ON a.id=f.attachment_attribute_id
LEFT JOIN clients c ON c.Id=f.client_id";

    private const string AttachmentSelect = @"SELECT a.id Id,a.public_id PublicId,a.client_id ClientId,a.attachment_attribute_id AttachmentAttributeId,
a.field_configuration_id FieldConfigurationId,a.storage_server_id StorageServerId,s.server_name StorageServerName,a.entity_type EntityType,a.entity_id EntityId,
at.attribute_code AttributeCode,at.attribute_name AttributeName,f.field_label FieldLabel,a.original_file_name OriginalFileName,a.stored_file_name StoredFileName,
a.storage_key StorageKey,a.file_extension FileExtension,a.declared_mime_type DeclaredMimeType,a.detected_mime_type DetectedMimeType,
a.file_size_bytes FileSizeBytes,a.sha256_hash Sha256Hash,a.version_number VersionNumber,a.document_number DocumentNumber,a.issue_date IssueDate,
a.expiry_date ExpiryDate,a.verification_status VerificationStatus,a.verified_by_user_id VerifiedByUserId,a.verified_at_utc VerifiedAtUtc,
a.rejection_reason RejectionReason,a.malware_scan_status MalwareScanStatus,a.malware_scanned_at_utc MalwareScannedAtUtc,a.is_current IsCurrent,
a.is_deleted IsDeleted,a.uploaded_by_user_id UploadedByUserId,COALESCE(u.DisplayName,u.Email,'') UploadedByName,a.uploaded_at_utc UploadedAtUtc,
a.deleted_by_user_id DeletedByUserId,a.deleted_at_utc DeletedAtUtc
FROM entity_attachments a
JOIN attachment_attributes at ON at.id=a.attachment_attribute_id
JOIN attachment_field_configurations f ON f.id=a.field_configuration_id
JOIN attachment_storage_servers s ON s.id=a.storage_server_id
LEFT JOIN authusers u ON u.Id=a.uploaded_by_user_id";

    private const string AccessSelect = @"SELECT a.id Id,a.public_id PublicId,a.client_id ClientId,a.attachment_attribute_id AttachmentAttributeId,
a.field_configuration_id FieldConfigurationId,a.storage_server_id StorageServerId,s.server_name StorageServerName,a.entity_type EntityType,a.entity_id EntityId,
at.attribute_code AttributeCode,at.attribute_name AttributeName,f.field_label FieldLabel,a.original_file_name OriginalFileName,a.stored_file_name StoredFileName,
a.storage_key StorageKey,a.file_extension FileExtension,a.declared_mime_type DeclaredMimeType,a.detected_mime_type DetectedMimeType,
a.file_size_bytes FileSizeBytes,a.sha256_hash Sha256Hash,a.version_number VersionNumber,a.document_number DocumentNumber,a.issue_date IssueDate,
a.expiry_date ExpiryDate,a.verification_status VerificationStatus,a.verified_by_user_id VerifiedByUserId,a.verified_at_utc VerifiedAtUtc,
a.rejection_reason RejectionReason,a.malware_scan_status MalwareScanStatus,a.malware_scanned_at_utc MalwareScannedAtUtc,a.is_current IsCurrent,
a.is_deleted IsDeleted,a.uploaded_by_user_id UploadedByUserId,COALESCE(u.DisplayName,u.Email,'') UploadedByName,a.uploaded_at_utc UploadedAtUtc,
a.deleted_by_user_id DeletedByUserId,a.deleted_at_utc DeletedAtUtc,f.owner_can_view OwnerCanView,f.owner_can_upload OwnerCanUpload,
f.owner_can_replace OwnerCanReplace,f.owner_can_delete OwnerCanDelete
FROM entity_attachments a
JOIN attachment_attributes at ON at.id=a.attachment_attribute_id
JOIN attachment_field_configurations f ON f.id=a.field_configuration_id
JOIN attachment_storage_servers s ON s.id=a.storage_server_id
LEFT JOIN authusers u ON u.Id=a.uploaded_by_user_id";

    private sealed class StorageServerRow : AttachmentStorageServer
    {
        public string CredentialCipherText { get; set; } = string.Empty;
    }

    private sealed class AttachmentAccessRow : EntityAttachment
    {
        public bool OwnerCanView { get; set; }
        public bool OwnerCanUpload { get; set; }
        public bool OwnerCanReplace { get; set; }
        public bool OwnerCanDelete { get; set; }
    }

    private sealed class AccessTokenRow
    {
        public long Id { get; set; }
        public long AttachmentId { get; set; }
        public int IssuedToUserId { get; set; }
        public string Purpose { get; set; } = string.Empty;
        public int MaximumUses { get; set; }
        public int UseCount { get; set; }
    }

    private sealed record FileInspection(bool Ok, string DetectedMimeType, string Sha256Hash, string Error)
    {
        public static FileInspection Success(string mime, string hash) => new(true, mime, hash, "");
        public static FileInspection Fail(string error) => new(false, "", "", error);
    }
}
