using System.Text.Json.Serialization;

namespace Payroll.API.Models;

public class DynamicFormDefinition
{
    public long Id { get; set; }
    public int ClientId { get; set; }
    public string ClientName { get; set; } = "";
    public string ModuleCode { get; set; } = "RECRUITMENT";
    public string FormCode { get; set; } = "";
    public string FormName { get; set; } = "";
    public string PurposeCode { get; set; } = "CANDIDATE_APPLICATION";
    public string EntityType { get; set; } = "CANDIDATE";
    public string Status { get; set; } = "Active";
    public long? CurrentPublishedVersionId { get; set; }
    public int CreatedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public List<DynamicFormVersion> Versions { get; set; } = [];
}

public class SaveDynamicFormDefinition
{
    public long Id { get; set; }
    public int ClientId { get; set; }
    public string ModuleCode { get; set; } = "RECRUITMENT";
    public string FormCode { get; set; } = "";
    public string FormName { get; set; } = "";
    public string PurposeCode { get; set; } = "CANDIDATE_APPLICATION";
    public string EntityType { get; set; } = "CANDIDATE";
    public string Status { get; set; } = "Active";
}

public class DynamicFormVersion
{
    public long Id { get; set; }
    public long FormDefinitionId { get; set; }
    public int VersionNumber { get; set; }
    public string Status { get; set; } = "Draft";
    public int CreatedByUserId { get; set; }
    public int? PublishedByUserId { get; set; }
    public DateTime? PublishedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public List<DynamicFormSection> Sections { get; set; } = [];
}

public class SaveDynamicFormVersion
{
    public long Id { get; set; }
    public long FormDefinitionId { get; set; }
    public List<DynamicFormSection> Sections { get; set; } = [];
}

public class DynamicFormSection
{
    public long Id { get; set; }
    public long FormVersionId { get; set; }
    public string SectionCode { get; set; } = "GENERAL";
    public string SectionLabel { get; set; } = "General";
    public string Description { get; set; } = "";
    public int DisplayOrder { get; set; } = 100;
    public List<DynamicFormField> Fields { get; set; } = [];
}

public class DynamicFormField
{
    public long Id { get; set; }
    public long FormVersionId { get; set; }
    public long SectionId { get; set; }
    public string FieldTypeCode { get; set; } = "TEXT";
    public string StableFieldCode { get; set; } = "";
    public string Label { get; set; } = "";
    public string Placeholder { get; set; } = "";
    public string HelpText { get; set; } = "";
    public bool IsRequired { get; set; }
    public int DisplayOrder { get; set; } = 100;
    public int WidthColumns { get; set; } = 12;
    public int? MinimumLength { get; set; }
    public int? MaximumLength { get; set; }
    public decimal? MinimumNumber { get; set; }
    public decimal? MaximumNumber { get; set; }
    public DateTime? MinimumDate { get; set; }
    public DateTime? MaximumDate { get; set; }
    public long? AttachmentFieldConfigurationId { get; set; }
    public PublicAttachmentConstraints? AttachmentConstraints { get; set; }
    public string LookupSourceCode { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public List<DynamicFormFieldOption> Options { get; set; } = [];
    public List<string> SemanticCodes { get; set; } = [];
    public List<DynamicFormValidationRule> ValidationRules { get; set; } = [];
    [JsonIgnore]
    public string AllowedExtensionsJson { get; set; } = "[]";
    [JsonIgnore]
    public string AllowedMimeTypesJson { get; set; } = "[]";
    [JsonIgnore]
    public bool AllowMultipleFiles { get; set; }
    [JsonIgnore]
    public int MaximumFileCount { get; set; } = 1;
    [JsonIgnore]
    public long MaximumFileSizeBytes { get; set; }
    [JsonIgnore]
    public long? MaximumTotalSizeBytes { get; set; }
}

public class PublicAttachmentConstraints
{
    public bool AllowMultiple { get; set; }
    public int MaximumFileCount { get; set; } = 1;
    public long MaximumFileSizeBytes { get; set; }
    public long? MaximumTotalSizeBytes { get; set; }
    public List<string> AllowedExtensions { get; set; } = [];
    public List<string> AllowedMimeTypes { get; set; } = [];
}

public class DynamicFormFieldOption
{
    public long Id { get; set; }
    public long FieldId { get; set; }
    public string OptionCode { get; set; } = "";
    public string OptionLabel { get; set; } = "";
    public int DisplayOrder { get; set; } = 100;
    public bool IsActive { get; set; } = true;
}

public class DynamicFormValidationRule
{
    public long Id { get; set; }
    public long FieldId { get; set; }
    public string RuleType { get; set; } = "";
    public string ComparisonOperator { get; set; } = "";
    public long? CompareFieldId { get; set; }
    public string CompareFieldCode { get; set; } = "";
    public string? TextValue { get; set; }
    public long? IntegerValue { get; set; }
    public decimal? DecimalValue { get; set; }
    public DateTime? DateValue { get; set; }
    public bool? BooleanValue { get; set; }
    public string ErrorMessage { get; set; } = "";
    public int DisplayOrder { get; set; } = 100;
}

public class DynamicFormLookupSource
{
    public long Id { get; set; }
    public string SourceCode { get; set; } = "";
    public string SourceName { get; set; } = "";
    public string ResolverCode { get; set; } = "";
    public bool IsClientScoped { get; set; } = true;
    public int MinimumSearchLength { get; set; } = 0;
    public int MaximumResults { get; set; } = 50;
    public bool IsActive { get; set; } = true;
}

public class DynamicLookupOption
{
    public string Value { get; set; } = "";
    public string Label { get; set; } = "";
}

public class PublicRecruitmentJob
{
    public long PostingId { get; set; }
    public string PublicSlug { get; set; } = "";
    public string PublicTitle { get; set; } = "";
    public string PositionCode { get; set; } = "";
    public string PositionTitle { get; set; } = "";
    public string ClientName { get; set; } = "";
    public string Department { get; set; } = "";
    public string JobLocation { get; set; } = "";
    public string EmploymentType { get; set; } = "";
    public string WorkMode { get; set; } = "";
    public string Summary { get; set; } = "";
    public string RolePurpose { get; set; } = "";
    public DateTime? ClosesAtUtc { get; set; }
    public DynamicFormVersion? ApplicationForm { get; set; }
    public List<RecruitmentJdResponsibility> Responsibilities { get; set; } = [];
    public List<RecruitmentJdSkillRequirement> Skills { get; set; } = [];
    public List<RecruitmentJdQualificationRequirement> Qualifications { get; set; } = [];
}

public class StartPublicApplicationRequest
{
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    public string IdempotencyKey { get; set; } = "";
    public bool ConsentAccepted { get; set; }
}

public class PublicApplicationSession
{
    public string SessionToken { get; set; } = "";
    public long SubmissionId { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public string Status { get; set; } = "Draft";
}

public class SavePublicFormValuesRequest
{
    public List<PublicFormValue> Values { get; set; } = [];
}

public class PublicFormValue
{
    public long FieldId { get; set; }
    public string? TextValue { get; set; }
    public long? IntegerValue { get; set; }
    public decimal? DecimalValue { get; set; }
    public DateTime? DateValue { get; set; }
    public DateTime? DateTimeValue { get; set; }
    public bool? BooleanValue { get; set; }
    public List<long> SelectedOptionIds { get; set; } = [];
    public List<string> SelectedOptionValues { get; set; } = [];
}

public class PublicUploadAuthorization
{
    public long SubmissionId { get; set; }
    public long ExternalSubjectId { get; set; }
    public int ClientId { get; set; }
    public long FieldId { get; set; }
    public long AttachmentFieldConfigurationId { get; set; }
    public bool AllowMultiple { get; set; }
    public int MaximumFileCount { get; set; } = 1;
    public long MaximumFileSizeBytes { get; set; }
    public long? MaximumTotalSizeBytes { get; set; }
}

public class PublicFormAttachmentUploadRequest
{
    public string DocumentNumber { get; set; } = "";
    public DateTime? IssueDate { get; set; }
    public DateTime? ExpiryDate { get; set; }
    public IFormFile? File { get; set; }
}

public class PublicApplicationResult
{
    public long SubmissionId { get; set; }
    public long CandidateId { get; set; }
    public long ApplicationId { get; set; }
    public string ApplicationCode { get; set; } = "";
    public string Status { get; set; } = "Submitted";
    public string Message { get; set; } = "Application submitted successfully.";
}
