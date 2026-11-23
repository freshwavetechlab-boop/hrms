using System.Text;
using Microsoft.AspNetCore.Http;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Repositories;

public sealed class RecruitmentDocumentCollectionTests
{
    [Fact]
    public void Consumed_link_remains_completed_after_expiry()
    {
        Assert.Equal("Completed", RecruitmentDocumentCollectionRepository.PublicStatus("Completed", DateTime.UtcNow.AddYears(-1), DateTime.UtcNow));
        Assert.Equal("Expired", RecruitmentDocumentCollectionRepository.PublicStatus("Open", DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow));
        Assert.Equal("Unavailable", RecruitmentDocumentCollectionRepository.PublicStatus("Revoked", DateTime.UtcNow.AddDays(1), DateTime.UtcNow));
    }

    [Theory]
    [InlineData(" User@Example.com ", "user@example.com")]
    [InlineData("FIRST.LAST@example.com", "first.last@EXAMPLE.COM")]
    public void Recipient_email_matching_is_case_and_whitespace_insensitive(string first, string second) =>
        Assert.Equal(RecruitmentDocumentCollectionRepository.EmailHash(first), RecruitmentDocumentCollectionRepository.EmailHash(second));

    [Fact]
    public void Catalog_reuses_core_configuration_ids_and_limits_instead_of_copying_documents_to_a_new_context()
    {
        var core = new AttachmentFieldConfiguration { Id = 1, ClientId = 0, AttachmentAttributeId = 900, AttributeCode = "ADHAAR_DOC", AttributeName = "Adhaar Card", ModuleCode = "EMPLOYEE", FormCode = "EMPLOYEE_CREATE_EDIT", FieldKey = "ADHAAR_DOC", FieldLabel = "Adhaar Card", MaximumFileSizeBytes = 1048576 };
        var academic = new AttachmentFieldConfiguration { Id = 87, ClientId = 0, AttachmentAttributeId = 901, AttributeCode = "UIDAI_JOIN_ACADEMIC", AttributeName = "Academic certificates (Class 10 onwards)", ModuleCode = "RECRUITMENT", FormCode = "UIDAI_JOINING_DOCUMENTS", FieldKey = "UIDAI_JOIN_ACADEMIC", FieldLabel = "Academic certificates (Class 10 onwards)", MaximumFileSizeBytes = 2097152 };
        var unrelated = new AttachmentFieldConfiguration { Id = 99, ClientId = 0, AttributeCode = "RECRUITMENT_WORK_ORDER_FILE", ModuleCode = "RECRUITMENT", FormCode = "WORK_ORDER", FieldKey = "WORK_ORDER_AND_JD", FieldLabel = "Work order / JD annexure" };
        var fields = AttachmentRepository.ResolveJoiningDocuments([core, academic, unrelated], 55, new HashSet<long> { 87 });
        Assert.Equal(new long[] { 1, 87 }, fields.Select(field => field.Id).Order().ToArray());
        Assert.Same(core, Assert.Single(fields, field => field.Id == 1));
        Assert.Equal("AADHAAR", AttachmentRepository.JoiningDocumentKind(core));
        Assert.Equal("EDUCATION", AttachmentRepository.JoiningDocumentKind(academic));
        Assert.Equal(1048576, Assert.Single(fields, field => field.Id == 1).MaximumFileSizeBytes);
    }

    [Theory]
    [InlineData("ICMR PAN card", "ICMR", "PAN card")]
    [InlineData("UIDAI / Address proof", "UIDAI", "Address proof")]
    [InlineData("ICMR document", "ICMR Institute", "document")]
    [InlineData("ICMR", "ICMR", "Document")]
    public void Public_document_labels_remove_client_name(string label, string client, string expected) =>
        Assert.Equal(expected, RecruitmentDocumentCollectionRepository.PublicLabel(label, client));

    [Fact]
    public void Requested_selection_rejects_unknown_duplicate_empty_and_null_fields()
    {
        Assert.Throws<RecruitmentDocumentCollectionValidationException>(() => RecruitmentDocumentCollectionRepository.ValidateSelections([], [1]));
        Assert.Throws<RecruitmentDocumentCollectionValidationException>(() => RecruitmentDocumentCollectionRepository.ValidateSelections([new() { FieldConfigurationId = 2 }], [1]));
        Assert.Throws<RecruitmentDocumentCollectionValidationException>(() => RecruitmentDocumentCollectionRepository.ValidateSelections([new() { FieldConfigurationId = 1 }, new() { FieldConfigurationId = 1 }], [1]));
        Assert.Throws<RecruitmentDocumentCollectionValidationException>(() => RecruitmentDocumentCollectionRepository.ValidateSelections([null!], [1]));
    }

    [Fact]
    public void Optional_document_may_be_skipped_and_mandatory_document_may_not()
    {
        var files = Files("files-1");
        RecruitmentDocumentCollectionRepository.ValidateSubmission([("1", true, 1, 3), ("2", false, 1, 3)], new(), files);
        Assert.Throws<RecruitmentDocumentCollectionValidationException>(() => RecruitmentDocumentCollectionRepository.ValidateSubmission([("1", true, 2, 3)], new(), files));
    }

    [Fact]
    public void Unknown_files_duplicate_metadata_and_null_metadata_are_rejected()
    {
        Assert.Throws<RecruitmentDocumentCollectionValidationException>(() => RecruitmentDocumentCollectionRepository.ValidateSubmission([("1", true, 1, 3)], new(), Files("files-2")));
        Assert.Throws<RecruitmentDocumentCollectionValidationException>(() => RecruitmentDocumentCollectionRepository.ValidateSubmission([("1", true, 1, 3)], new() { Documents = [new() { Id = "1" }, new() { Id = "1" }] }, Files("files-1")));
        Assert.Throws<RecruitmentDocumentCollectionValidationException>(() => RecruitmentDocumentCollectionRepository.ValidateSubmission([("1", true, 1, 3)], new() { Documents = [null!] }, Files("files-1")));
    }

    private static FormFileCollection Files(string field) => [new FormFile(new MemoryStream(Encoding.ASCII.GetBytes("%PDF-1.4\nfixture")), 0, 16, field, "document.pdf")];
}
