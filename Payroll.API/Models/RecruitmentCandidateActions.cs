using System.Text.Json.Serialization;

namespace Payroll.API.Models;

public class CreateRecruitmentCandidateActionRequest
{
    public long ApplicationId { get; set; }
    public long? PipelineStageInstanceId { get; set; }
    public long? FormVersionId { get; set; }
    public long? OfferId { get; set; }
    public string PurposeCode { get; set; } = "DOCUMENT_REQUEST";
    public string Instructions { get; set; } = "";
    public int ValidForMinutes { get; set; } = 10080;
    public int MaximumUses { get; set; } = 100;
}

public class RecruitmentCandidateActionSession
{
    public long Id { get; set; }
    public int ClientId { get; set; }
    public long ApplicationId { get; set; }
    public long CandidateId { get; set; }
    public long? PipelineStageInstanceId { get; set; }
    public long? FormVersionId { get; set; }
    public long? FormSubmissionId { get; set; }
    public long? OfferId { get; set; }
    public string PurposeCode { get; set; } = "";
    public string Status { get; set; } = "Open";
    public string Instructions { get; set; } = "";
    public DateTime ExpiresAtUtc { get; set; }
    public int MaximumUses { get; set; }
    public int UseCount { get; set; }
    public int CreatedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
    public string CandidateName { get; set; } = "";
    public string PositionTitle { get; set; } = "";
    public string ActionToken { get; set; } = "";
    [JsonIgnore]
    public string TokenCipherText { get; set; } = "";
}

public class PublicRecruitmentCandidateActionContext
{
    public string PurposeCode { get; set; } = "";
    public string Purpose => PurposeCode switch
    {
        "OFFER_RESPONSE" => "OfferResponse",
        "PROFILE_UPDATE" => "ProfileUpdate",
        _ => "DocumentRequest"
    };
    public string CandidateName { get; set; } = "";
    public string PositionTitle { get; set; } = "";
    public string OrganizationName { get; set; } = "";
    public DateTime ExpiresAtUtc { get; set; }
    public string Status { get; set; } = "";
    public string Instructions { get; set; } = "";
    public string Message { get; set; } = "";
    public bool AllowSaveDraft { get; set; }
    public DynamicFormVersion? Form { get; set; }
    public List<PublicFormValue> ExistingValues { get; set; } = [];
    public List<PublicCandidateActionFile> UploadedFiles { get; set; } = [];
    public PublicCandidateOffer? Offer { get; set; }
}

public class PublicCandidateActionFile
{
    public long FieldId { get; set; }
    public Guid AttachmentPublicId { get; set; }
    public string OriginalFileName { get; set; } = "";
    public long FileSizeBytes { get; set; }
    public DateTime UploadedAtUtc { get; set; }
}

public class PublicCandidateOffer
{
    public long Id { get; set; }
    public string OfferNumber { get; set; } = "";
    public decimal OfferedCtc { get; set; }
    public string Currency { get; set; } = "INR";
    public DateTime ProposedJoiningDate { get; set; }
    public DateTime? ExpiryDate { get; set; }
    public Guid? OfferLetterAttachmentPublicId { get; set; }
    public string DocumentUrl { get; set; } = "";
    public string Status { get; set; } = "";
}

public class CompletePublicCandidateActionRequest
{
    public List<PublicFormValue> Values { get; set; } = [];
    public string Decision { get; set; } = "";
    public string Remarks { get; set; } = "";
}

public class PublicCandidateActionResult
{
    [System.Text.Json.Serialization.JsonIgnore]
    public long ApplicationId { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public long? PipelineStageInstanceId { get; set; }
    public string Status { get; set; } = "Completed";
    public string Message { get; set; } = "Your response was submitted successfully.";
}
