using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class RecruitmentSeedPackRequesterTests
{
    private static readonly RecruitmentRequesterOption[] Users = [
        new() { UserId = 3337, DisplayName = "Project approver", Email = "approver@example.invalid", ClientId = 55 },
        new() { UserId = 4004, DisplayName = "Hiring requester", Email = "requester@example.invalid", ClientId = 55 }
    ];

    [Theory]
    [InlineData("3337", 3337)]
    [InlineData(" APPROVER@example.invalid ", 3337)]
    [InlineData("requester@example.invalid", 4004)]
    public void ExplicitIdentityMatchesAnEligibleUserIdOrLoginEmail(string identity, int expected)
    {
        var (userId, error) = RecruitmentSeedPackService.ResolveRequesterUserSelection(Users, identity, 1);
        Assert.Equal(expected, userId); Assert.Empty(error);
    }

    [Theory]
    [InlineData("EMP-3337")]
    [InlineData("Project approver")]
    [InlineData("99")]
    [InlineData("3337.0")]
    public void EmployeeCodesDisplayNamesAndIneligibleNumericIdsAreNeverCoercedIntoUsers(string identity)
    {
        var (userId, error) = RecruitmentSeedPackService.ResolveRequesterUserSelection(Users, identity, 1);
        Assert.Equal(0, userId); Assert.Contains("authorized employer user", error);
    }

    [Fact]
    public void BlankUsesOnlySelfOrTheSoleEligibleUserAndNeverTheFirstOfMany()
    {
        Assert.Equal((4004, ""), RecruitmentSeedPackService.ResolveRequesterUserSelection(Users, "", 4004));
        Assert.Equal((3337, ""), RecruitmentSeedPackService.ResolveRequesterUserSelection([Users[0]], "", 1));
        var ambiguous = RecruitmentSeedPackService.ResolveRequesterUserSelection(Users, "", 1);
        Assert.Equal(0, ambiguous.UserId); Assert.Contains("more than one", ambiguous.Error);
        var missing = RecruitmentSeedPackService.ResolveRequesterUserSelection([], "", 1);
        Assert.Equal(0, missing.UserId); Assert.Contains("Configure a requester user", missing.Error);
    }

    [Fact]
    public void DuplicateLoginEmailsRequireAnUnambiguousUserId()
    {
        var duplicates = new[] { Users[0], new RecruitmentRequesterOption { UserId = 4999, Email = Users[0].Email, ClientId = 55 } };
        Assert.NotEmpty(RecruitmentSeedPackService.ResolveRequesterUserSelection(duplicates, Users[0].Email, 1).Error);
        Assert.Equal((4999, ""), RecruitmentSeedPackService.ResolveRequesterUserSelection(duplicates, "4999", 1));
    }
}
