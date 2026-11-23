using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Repositories;

public sealed class AttachmentDocumentMasterNormalizationTests
{
    [Theory]
    [InlineData("Internal", "Confidential", "Confidential")]
    [InlineData("Restricted", "Public", "Restricted")]
    [InlineData("public", "internal", "Internal")]
    [InlineData("Confidential", "Confidential", "Confidential")]
    public void Consolidation_never_reduces_classification(string first, string second, string expected)
    {
        Assert.Equal(expected, AttachmentRepository.JoiningMasterClassification(first, second));
        Assert.Equal(expected, AttachmentRepository.JoiningMasterClassification(second, first));
    }

    [Fact]
    public void Unknown_different_classifications_require_review()
    {
        Assert.Null(AttachmentRepository.JoiningMasterClassification("Custom sensitivity", "Confidential"));
        Assert.Equal("Custom sensitivity", AttachmentRepository.JoiningMasterClassification("Custom sensitivity", "Custom sensitivity"));
    }

    [Fact]
    public void Master_metadata_requirements_must_be_identical_before_ids_are_consolidated()
    {
        var original = new AttachmentAttribute { RequiresDocumentNumber = true, RequiresIssueDate = true, RequiresExpiryDate = true };
        Assert.True(AttachmentRepository.JoiningMasterRequirementsMatch(original,
            new() { RequiresDocumentNumber = true, RequiresIssueDate = true, RequiresExpiryDate = true }));
        Assert.False(AttachmentRepository.JoiningMasterRequirementsMatch(original,
            new() { RequiresIssueDate = true, RequiresExpiryDate = true }));
        Assert.False(AttachmentRepository.JoiningMasterRequirementsMatch(original,
            new() { RequiresDocumentNumber = true, RequiresExpiryDate = true }));
        Assert.False(AttachmentRepository.JoiningMasterRequirementsMatch(original,
            new() { RequiresDocumentNumber = true, RequiresIssueDate = true }));
    }
}
