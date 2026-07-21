namespace Payroll.API.Models;

public class EmployeeAttributeContext
{
    public int EmployeeId { get; set; }
    public int ClientId { get; set; }
    public string InfotypeCode { get; set; } = "0002";
    public DateTime AsOfUtc { get; set; }
    public List<EmployeeAttributeForm> Forms { get; set; } = [];
    public List<EmployeeAttributeValue> Values { get; set; } = [];
    public List<EmployeeAttributeFile> Files { get; set; } = [];
}

public class EmployeeAttributeFile
{
    public Guid PublicId { get; set; }
    public long FieldConfigurationId { get; set; }
    public string OriginalFileName { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public DateTime UploadedAtUtc { get; set; }
}

public class EmployeeAttributeForm
{
    public long Id { get; set; }
    public long FormDefinitionId { get; set; }
    public long? BindingId { get; set; }
    public string FormCode { get; set; } = string.Empty;
    public string FormName { get; set; } = string.Empty;
    public string InfotypeCode { get; set; } = "0002";
    public int VersionNumber { get; set; }
    public string Status { get; set; } = "Published";
    public bool IsRequired { get; set; }
    public int DisplayOrder { get; set; } = 100;
    public bool IsImplicitBinding { get; set; }
    public List<DynamicFormSection> Sections { get; set; } = [];
}

public class EmployeeAttributeValue
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
    public List<Guid> AttachmentPublicIds { get; set; } = [];
}

public class EmployeeAttributeLookupOption
{
    public string Value { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

public class SaveEmployeeAttributeValuesRequest
{
    public int ClientId { get; set; }
    public string InfotypeCode { get; set; } = "0002";
    public DateTime? EffectiveFromUtc { get; set; }
    public string ChangeReason { get; set; } = string.Empty;
    public string SourceCode { get; set; } = "EMPLOYEE_UI";
    public string SourceReference { get; set; } = string.Empty;
    public List<EmployeeAttributeValue> Values { get; set; } = [];
}

public class SaveEmployeeAttributeValuesResult
{
    public int EmployeeId { get; set; }
    public int SavedCount { get; set; }
    public DateTime EffectiveFromUtc { get; set; }
    public List<long> SubmissionIds { get; set; } = [];
    public List<EmployeeAttributeRevision> Revisions { get; set; } = [];
    public List<EmployeeAttributeValue> Values { get; set; } = [];
}

public class EmployeeAttributeRevision
{
    public long SubmissionId { get; set; }
    public long? BindingId { get; set; }
    public long FormDefinitionId { get; set; }
    public long FormVersionId { get; set; }
    public int RevisionNumber { get; set; }
    public DateTime EffectiveFromUtc { get; set; }
    public DateTime? EffectiveToUtc { get; set; }
    public long? PreviousSubmissionId { get; set; }
    public string SourceCode { get; set; } = string.Empty;
    public string SourceReference { get; set; } = string.Empty;
    public string ChangeReason { get; set; } = string.Empty;
    public int? ChangedByUserId { get; set; }
    public DateTime SubmittedAtUtc { get; set; }
}

public class EmployeeFormBinding
{
    public long Id { get; set; }
    public int ClientId { get; set; }
    public string ClientName { get; set; } = string.Empty;
    public long FormDefinitionId { get; set; }
    public string FormCode { get; set; } = string.Empty;
    public string FormName { get; set; } = string.Empty;
    public string InfotypeCode { get; set; } = "0002";
    public bool IsRequired { get; set; }
    public int DisplayOrder { get; set; } = 100;
    public bool IsActive { get; set; } = true;
    public bool IsImplicit { get; set; }
    public int? CreatedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public int? UpdatedByUserId { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public class SaveEmployeeFormBinding
{
    public long Id { get; set; }
    public int ClientId { get; set; }
    public long FormDefinitionId { get; set; }
    public string InfotypeCode { get; set; } = "0002";
    public bool IsRequired { get; set; }
    public int DisplayOrder { get; set; } = 100;
    public bool IsActive { get; set; } = true;
}
