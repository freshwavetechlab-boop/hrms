using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class SelectedClientCandidateConflictTests
{
    private static readonly RecruitmentCandidate Duplicate = new() { ClientId = 21, CandidateCode = "CAN-SECRET", FirstName = "Other", LastName = "Candidate" };

    [Fact]
    public void SelectedClientConflictDoesNotRevealForeignCandidateIdentity()
    {
        var result = RecruitmentTalentRepository.CandidateConflictMessage(Duplicate, new AuthUser { ClientAccessMode = "Selected", AssignedClientIds = [20, 21] }, 20);
        Assert.DoesNotContain("CAN-SECRET", result);
        Assert.DoesNotContain("Other", result);
        Assert.DoesNotContain("Candidate", result);
        Assert.Contains("administrator", result);
    }

    [Fact]
    public void SelectedClientKeepsUsefulDuplicateDetailsInsideTheActiveClient()
    {
        var result = RecruitmentTalentRepository.CandidateConflictMessage(Duplicate, new AuthUser { ClientAccessMode = "Selected" }, 21);
        Assert.Contains("CAN-SECRET", result);
        Assert.Contains("Other Candidate", result);
    }

    [Fact]
    public void LegacyDuplicateMessageIsUnchanged()
    {
        var result = RecruitmentTalentRepository.CandidateConflictMessage(Duplicate, new AuthUser(), 20);
        Assert.Equal("A talent profile already exists: CAN-SECRET - Other Candidate. Open the existing profile instead of creating a duplicate.", result);
    }
}
