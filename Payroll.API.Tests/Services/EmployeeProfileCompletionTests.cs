using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public class EmployeeProfileCompletionTests
{
    [Theory]
    [InlineData("", false, true, true)]
    [InlineData("", false, false, false)]
    [InlineData("", true, true, false)]
    [InlineData("Pending", false, true, false)]
    [InlineData("Pending", true, true, false)]
    [InlineData("Approved", true, false, true)]
    [InlineData("Consumed", true, true, false)]
    [InlineData("Rejected", true, true, false)]
    [InlineData("Sent Back", true, true, false)]
    public void OnlyInitialOrUnusedApprovedGrantAllowsSaving(string state, bool hasSaved, bool enabled, bool expected)
        => Assert.Equal(expected, EssMssRepository.IsProfileEditAllowed(state, hasSaved, enabled));

    [Fact]
    public void EachApprovalProvidesOneSaveAndDoesNotRestoreTheFirstTimeGrant()
    {
        Assert.True(EssMssRepository.IsProfileEditAllowed("", false, true));
        Assert.False(EssMssRepository.IsProfileEditAllowed("", true, true));
        for (var request = 0; request < 3; request++)
        {
            Assert.False(EssMssRepository.IsProfileEditAllowed("Pending", true, true));
            Assert.True(EssMssRepository.IsProfileEditAllowed("Approved", true, true));
            Assert.False(EssMssRepository.IsProfileEditAllowed("Consumed", true, true));
        }
    }

    [Fact]
    public void CompleteEmployeeHasNoMissingFieldsAndCashDoesNotRequireBankDetails()
    {
        var employee = CompleteEmployee();
        Assert.Empty(EmployeeProfileCompletionService.MissingFields(employee, [], []));
        employee.PaymentDetails.PaymentMode = "Bank Transfer";
        Assert.Equal(new[] { "Bank name", "Account number", "IFSC" }, EmployeeProfileCompletionService.MissingFields(employee, [], []));
    }

    [Fact]
    public void RequiredDocumentsUseHighestMinimumAndExcludeRejectedOrExpiredCopies()
    {
        var config = new AttachmentFieldConfiguration { AttachmentAttributeId = 1, IsRequired = true, IsActive = true, MinimumFileCount = 2, FieldLabel = "Identity document" };
        var configs = new[] { config, new AttachmentFieldConfiguration { AttachmentAttributeId = 1, IsRequired = true, IsActive = true, MinimumFileCount = 1, FieldLabel = "Same document" } };
        var valid = new EntityAttachment { AttachmentAttributeId = 1, VerificationStatus = "Pending", ExpiryDate = DateTime.UtcNow.Date };
        var expired = new EntityAttachment { AttachmentAttributeId = 1, VerificationStatus = "Verified", ExpiryDate = DateTime.UtcNow.Date.AddDays(-1) };
        var rejected = new EntityAttachment { AttachmentAttributeId = 1, VerificationStatus = "Rejected" };
        Assert.Equal(new[] { "Identity document" }, EmployeeProfileCompletionService.MissingFields(CompleteEmployee(), configs, [valid, expired, rejected]));
        Assert.Empty(EmployeeProfileCompletionService.MissingFields(CompleteEmployee(), configs, [valid, new EntityAttachment { AttachmentAttributeId = 1, VerificationStatus = "NotRequired" }]));
    }

    private static Employee CompleteEmployee() => new()
    {
        EmployeeCode = "E001", FirstName = "Test", DateOfJoining = "2026-01-01", Department = "Operations", Designation = "Analyst", WorkLocationId = 1,
        WorkEmail = "test@example.test", PortalAccess = true, AnnualCtc = 400000, SalaryStructureId = "S1",
        PersonalDetails = new() { EmploymentType = "Full time", SkillCategory = "Skilled", DateOfBirth = "1990-01-01", Mobile = "9999999999", PermanentAddress = "Test address" },
        PaymentDetails = new() { PaymentMode = "Cash" },
    };
}
