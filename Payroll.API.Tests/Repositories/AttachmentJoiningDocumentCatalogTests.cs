using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Repositories;

public sealed class AttachmentJoiningDocumentCatalogTests
{
    [Theory]
    [InlineData("UIDAI_JOIN_DOB", "DOB")]
    [InlineData("UIDAI_JOIN_PAN", "PAN")]
    [InlineData("UIDAI_JOIN_AADHAAR", "AADHAAR")]
    [InlineData("UIDAI_JOIN_PHOTO", "PHOTO")]
    [InlineData("UIDAI_JOIN_ADDRESS", "ADDRESS")]
    [InlineData("UIDAI_JOIN_ACADEMIC", "EDUCATION")]
    [InlineData("UIDAI_JOIN_PREVIOUS_APPOINTMENT", "PREVIOUS_APPOINTMENT")]
    [InlineData("UIDAI_JOIN_RELIEVING", "EXPERIENCE")]
    [InlineData("UIDAI_JOIN_SALARY", "PAYSLIPS")]
    [InlineData("UIDAI_JOIN_REFERENCES", "REFERENCES")]
    [InlineData("UIDAI_JOIN_BANK", "BANK")]
    [InlineData("UIDAI_JOIN_UAN", "UAN")]
    [InlineData("ADHAAR_DOC", "AADHAAR")]
    [InlineData("FUTURECLIENT_JOIN_ACADEMIC", "EDUCATION")]
    [InlineData("CLIENT_JOINING_DOCUMENTS_BANK", "BANK")]
    public void Existing_personal_document_codes_resolve_without_new_definitions(string code, string expected)
        => Assert.Equal(expected, AttachmentRepository.JoiningDocumentKind(new() { AttributeCode = code }));

    [Fact]
    public void Shared_catalog_preserves_global_ids_and_client_overrides_without_neighboring_clients()
    {
        var global = Field(1, 0, "ADHAAR_DOC");
        var own = Field(89, 20, "UIDAI_JOIN_AADHAAR", "UIDAI_JOINING_DOCUMENTS");
        var neighbor = Field(111, 70, "AADHAAR_CARD");
        var fields = new[] { global, own, neighbor };
        Assert.Equal(89, Assert.Single(AttachmentRepository.ResolveJoiningDocuments(fields, 20, new HashSet<long> { 89 })).Id);
        Assert.Equal(1, Assert.Single(AttachmentRepository.ResolveJoiningDocuments(fields, 55, new HashSet<long> { 89 })).Id);
    }

    [Fact]
    public void Existing_published_global_binding_wins_same_kind_without_copying_its_limits()
    {
        var original = Field(88, 0, "UIDAI_JOIN_ACADEMIC", "UIDAI_JOINING_DOCUMENTS");
        original.MaximumFileSizeBytes = 3145728;
        var fallback = Field(122, 0, "EDUCATIONAL_CERTIFICATES", "PRE_ONBOARDING");
        var result = Assert.Single(AttachmentRepository.ResolveJoiningDocuments([fallback, original], 55, new HashSet<long> { 88 }));
        Assert.Same(original, result);
        Assert.Equal(3145728, result.MaximumFileSizeBytes);
        Assert.Equal("Educational Certificates", AttachmentRepository.JoiningDocumentDisplayLabel(result));
    }

    [Fact]
    public void Unrelated_sources_disabled_and_future_fields_do_not_become_requestable()
    {
        var now = new DateTime(2026, 11, 23);
        var fields = new[] { Field(1, 0, "RESUME", "CANDIDATE_APPLICATION"), Field(2, 0, "BANK", "WORK_ORDER"), Field(3, 0, "PAN_CARD"), Field(4, 0, "ADDRESS_PROOF") };
        fields[2].IsActive = false;
        fields[3].EffectiveFromUtc = now.AddDays(1);
        Assert.Empty(AttachmentRepository.ResolveJoiningDocuments(fields, 55, now: now));
        Assert.Equal(11, AttachmentRepository.JoiningDocumentDefaults.Count(item => item.SeedMissing));
    }

    private static AttachmentFieldConfiguration Field(long id, int client, string code, string form = "EMPLOYEE_CREATE_EDIT") => new()
    {
        Id = id, ClientId = client, AttributeCode = code, FieldKey = code, FieldLabel = code,
        ModuleCode = form == "EMPLOYEE_CREATE_EDIT" ? "EMPLOYEE" : "RECRUITMENT", FormCode = form, IsActive = true
    };
}
