using System.Text.Json.Serialization;

namespace Payroll.API.Models;

public class AttachmentAttribute
{
    public long Id { get; set; }
    public int ClientId { get; set; }
    public string ClientName { get; set; } = string.Empty;
    public string AttributeCode { get; set; } = string.Empty;
    public string AttributeName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string DataClassification { get; set; } = "Internal";
    public bool RequiresDocumentNumber { get; set; }
    public bool RequiresIssueDate { get; set; }
    public bool RequiresExpiryDate { get; set; }
    public bool IsActive { get; set; } = true;
    public int? CreatedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public int? UpdatedByUserId { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public class AttachmentFieldConfiguration
{
    public long Id { get; set; }
    public int ClientId { get; set; }
    public string ClientName { get; set; } = string.Empty;
    public long AttachmentAttributeId { get; set; }
    public string AttributeCode { get; set; } = string.Empty;
    public string AttributeName { get; set; } = string.Empty;
    public string DataClassification { get; set; } = "Internal";
    public bool RequiresDocumentNumber { get; set; }
    public bool RequiresIssueDate { get; set; }
    public bool RequiresExpiryDate { get; set; }
    public string ModuleCode { get; set; } = "EMPLOYEE";
    public string FormCode { get; set; } = "EMPLOYEE_CREATE_EDIT";
    public string SectionCode { get; set; } = "DOCUMENTS";
    public string FieldKey { get; set; } = string.Empty;
    public string FieldLabel { get; set; } = string.Empty;
    public string HelpText { get; set; } = string.Empty;
    public bool IsRequired { get; set; }
    public bool AllowMultiple { get; set; }
    public int MinimumFileCount { get; set; }
    public int MaximumFileCount { get; set; } = 1;
    public string AllowedExtensionsJson { get; set; } = "[\"pdf\",\"jpg\",\"jpeg\",\"png\"]";
    public string AllowedMimeTypesJson { get; set; } = "[\"application/pdf\",\"image/jpeg\",\"image/png\"]";
    public long MaximumFileSizeBytes { get; set; } = 5 * 1024 * 1024;
    public long? MaximumTotalSizeBytes { get; set; }
    public bool OwnerCanView { get; set; } = true;
    public bool OwnerCanUpload { get; set; }
    public bool OwnerCanReplace { get; set; }
    public bool OwnerCanDelete { get; set; }
    public bool RequiresVerification { get; set; }
    public bool VersioningEnabled { get; set; } = true;
    public string RequirementScope { get; set; } = "NewEntitiesOnly";
    public int DisplayOrder { get; set; } = 100;
    public DateTime? EffectiveFromUtc { get; set; }
    public DateTime? EffectiveUntilUtc { get; set; }
    public bool IsActive { get; set; } = true;
    public int? CreatedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public int? UpdatedByUserId { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public class AttachmentStorageServer
{
    public long Id { get; set; }
    public string ServerCode { get; set; } = string.Empty;
    public string ServerName { get; set; } = string.Empty;
    public string StorageType { get; set; } = "LocalFileSystem";
    public string BasePath { get; set; } = string.Empty;
    public string ServiceUrl { get; set; } = string.Empty;
    public string Credential { get; set; } = string.Empty;
    public bool HasCredential { get; set; }
    public bool IsReadEnabled { get; set; } = true;
    public bool IsWriteEnabled { get; set; } = true;
    public bool IsDefaultWriteServer { get; set; }
    public int Priority { get; set; } = 100;
    public long? MaximumCapacityBytes { get; set; }
    public int WarningCapacityPercent { get; set; } = 85;
    public bool IsActive { get; set; } = true;
    public DateTime? LastHealthCheckAtUtc { get; set; }
    public string LastHealthCheckStatus { get; set; } = "Not checked";
    public string LastHealthCheckMessage { get; set; } = string.Empty;
    public string GoogleAccountEmail { get; set; } = string.Empty;
    public string GoogleFolderId { get; set; } = string.Empty;
    public string GoogleFolderName { get; set; } = string.Empty;
    public string GoogleFolderUrl { get; set; } = string.Empty;
    public bool GoogleOAuthConfigured { get; set; }
    public string GoogleConnectionStatus { get; set; } = string.Empty;
    public long LinkedAttachmentCount { get; set; }
    public int? CreatedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public int? UpdatedByUserId { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public class EntityAttachment
{
    [JsonIgnore]
    public long Id { get; set; }
    public Guid PublicId { get; set; }
    public int ClientId { get; set; }
    public long AttachmentAttributeId { get; set; }
    public long FieldConfigurationId { get; set; }
    public long StorageServerId { get; set; }
    public string StorageServerName { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public long EntityId { get; set; }
    public string AttributeCode { get; set; } = string.Empty;
    public string AttributeName { get; set; } = string.Empty;
    public string FieldLabel { get; set; } = string.Empty;
    public string OriginalFileName { get; set; } = string.Empty;
    [JsonIgnore]
    public string StoredFileName { get; set; } = string.Empty;
    [JsonIgnore]
    public string StorageKey { get; set; } = string.Empty;
    public string FileExtension { get; set; } = string.Empty;
    public string DeclaredMimeType { get; set; } = string.Empty;
    public string DetectedMimeType { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    [JsonIgnore]
    public string Sha256Hash { get; set; } = string.Empty;
    public int VersionNumber { get; set; } = 1;
    public string DocumentNumber { get; set; } = string.Empty;
    public DateTime? IssueDate { get; set; }
    public DateTime? ExpiryDate { get; set; }
    public string VerificationStatus { get; set; } = "NotRequired";
    public int? VerifiedByUserId { get; set; }
    public DateTime? VerifiedAtUtc { get; set; }
    public string RejectionReason { get; set; } = string.Empty;
    public string MalwareScanStatus { get; set; } = "NotScanned";
    public DateTime? MalwareScannedAtUtc { get; set; }
    public bool IsCurrent { get; set; } = true;
    public bool IsDeleted { get; set; }
    public int UploadedByUserId { get; set; }
    public string UploadedByName { get; set; } = string.Empty;
    public DateTime UploadedAtUtc { get; set; }
    public int? DeletedByUserId { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
}

public class AttachmentTargetOption
{
    public string ModuleCode { get; set; } = string.Empty;
    public string ModuleName { get; set; } = string.Empty;
    public string FormCode { get; set; } = string.Empty;
    public string FormName { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
}

public class AttachmentAccessTicket
{
    public string Url { get; set; } = string.Empty;
    public DateTime ExpiresAtUtc { get; set; }
}

public class AttachmentUploadMetadata
{
    public long FieldConfigurationId { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public long EntityId { get; set; }
    public string DocumentNumber { get; set; } = string.Empty;
    public DateTime? IssueDate { get; set; }
    public DateTime? ExpiryDate { get; set; }
}

public class AttachmentUploadRequest
{
    public long FieldConfigurationId { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public long EntityId { get; set; }
    public string DocumentNumber { get; set; } = string.Empty;
    public DateTime? IssueDate { get; set; }
    public DateTime? ExpiryDate { get; set; }
    public IFormFile? File { get; set; }
}

public class AttachmentReviewRequest
{
    public string Reason { get; set; } = string.Empty;
}

public class AttachmentStorageHealthResult
{
    public bool Healthy { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public long? AvailableBytes { get; set; }
    public long? TotalBytes { get; set; }
}

public sealed class GoogleDriveConnectRequest
{
    public long? StorageServerId { get; set; }
}

public sealed class GoogleDriveConnectResponse
{
    public long StorageServerId { get; set; }
    public string AuthorizationUrl { get; set; } = string.Empty;
}

public sealed class GoogleDriveConnectionStatus
{
    public long StorageServerId { get; set; }
    public bool GoogleOAuthConfigured { get; set; }
    public bool Connected { get; set; }
    public string ConnectionStatus { get; set; } = "Not configured";
    public string AccountEmail { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;
    public string FolderName { get; set; } = string.Empty;
    public string FolderUrl { get; set; } = string.Empty;
    public bool IsDefaultWriteServer { get; set; }
    public bool Healthy { get; set; }
    public DateTime? ConnectedAtUtc { get; set; }
}

public sealed class GoogleDriveOAuthSetup
{
    public long StorageServerId { get; set; }
    public bool GoogleOAuthConfigured { get; set; }
    public string ConnectionStatus { get; set; } = "Not configured";
    public string CallbackUrl { get; set; } = string.Empty;
    public string GoogleCloudCredentialsUrl { get; set; } = "https://console.cloud.google.com/apis/credentials";
}

public sealed class AttachmentFileHandle(Stream stream, IDisposable? owner = null) : IAsyncDisposable
{
    public Stream Stream { get; } = stream;

    public async ValueTask DisposeAsync()
    {
        await Stream.DisposeAsync();
        owner?.Dispose();
    }
}
