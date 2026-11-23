namespace Payroll.API.Models;

public sealed class RecruitmentRequestedDocument
{
    public long FieldConfigurationId { get; set; }
    public bool Required { get; set; } = true;
}

public sealed class RecruitmentDocumentRequestField
{
    public long FieldConfigurationId { get; set; }
    public string Label { get; set; } = "";
    public bool IsRequired { get; set; }
}

public sealed class PublicRecruitmentDocumentRequest
{
    public string Status { get; set; } = "Open";
    public string Message { get; set; } = "Upload your requested documents and submit once.";
    public DateTime? ExpiresAtUtc { get; set; }
    public IReadOnlyList<PublicRecruitmentRequestedDocument> Documents { get; set; } = [];
}

public sealed class PublicRecruitmentRequestedDocument
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public bool Required { get; set; }
    public int MinimumFileCount { get; set; }
    public int MaximumFileCount { get; set; }
    public long MaximumFileSizeBytes { get; set; }
    public string[] AllowedExtensions { get; set; } = [];
    public bool RequiresDocumentNumber { get; set; }
    public bool RequiresIssueDate { get; set; }
    public bool RequiresExpiryDate { get; set; }
}

public sealed class PublicRecruitmentDocumentMetadata
{
    public List<PublicRecruitmentDocumentMetadataItem> Documents { get; set; } = [];
}

public sealed class PublicRecruitmentDocumentMetadataItem
{
    public string Id { get; set; } = "";
    public string DocumentNumber { get; set; } = "";
    public DateTime? IssueDate { get; set; }
    public DateTime? ExpiryDate { get; set; }
}

public sealed record RecruitmentDocumentCollectionResult(string Status, bool AlreadySubmitted, string Message);

public sealed class RecruitmentDocumentCollectionValidationException(string message) : Exception(message);
