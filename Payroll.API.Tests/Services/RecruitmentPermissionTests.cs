using Payroll.API.Models;
using Payroll.API.Services;
using Payroll.API.Repositories;
using Microsoft.Extensions.Configuration;

namespace Payroll.API.Tests.Services;

public sealed class RecruitmentPermissionTests
{
    [Theory]
    [InlineData("recruitment.rfr.view", true, false)]
    [InlineData("recruitment.rfr.create", true, true)]
    [InlineData("recruitment.proposal.approve", false, false)]
    [InlineData("recruitment.configuration.manage", false, false)]
    [InlineData("recruitment.manage", true, true)]
    [InlineData("settings.manage", true, true)]
    public void RequisitionReadAndCreationHaveSeparateCapabilities(string permission, bool canRead, bool canCreate)
    {
        var user = new AuthUser { Id = 3, ClientId = 20, Permissions = [permission] };
        Assert.Equal(canRead, RecruitmentPermissions.CanViewRequisitions(user));
        Assert.Equal(canCreate, RecruitmentPermissions.CanCreateRequisitions(user));
    }

    [Fact]
    public void RecruiterCapabilitiesDoNotGrantApprovalOrSecurity()
    {
        var user = new AuthUser { Id = 3, ClientId = 20, Permissions = ["recruitment.rfr.create", "recruitment.publish", "recruitment.candidate.manage", "recruitment.interview.schedule"] };
        Assert.True(RecruitmentPermissions.CanCreateRequisitions(user));
        Assert.False(RecruitmentPermissions.Has(user, "recruitment.proposal.approve"));
        Assert.False(RecruitmentPermissions.Has(user, "recruitment.shortlist.approve"));
        Assert.False(RecruitmentPermissions.IsAdmin(user));
    }

    [Fact]
    public void ClientBoundSuperAdminRoleDoesNotBypassRequiredPermissions()
    {
        var user = new AuthUser { Id = 3, ClientId = 20, Roles = ["super_admin"] };
        Assert.False(RecruitmentPermissions.CanViewRequisitions(user));
        Assert.False(RecruitmentPermissions.CanCreateRequisitions(user));
    }

    [Theory]
    [InlineData("recruitment.position.view", true, false, false)]
    [InlineData("recruitment.publish", true, true, true)]
    [InlineData("recruitment.candidate.manage", false, false, true)]
    [InlineData("recruitment.configuration.manage", false, false, true)]
    [InlineData("recruitment.rfr.view", false, false, false)]
    public void ReadingJobsAndCandidateFormDefinitionsDoesNotGrantPublishing(string permission, bool canReadJobs, bool canPublish, bool canReadForms)
    {
        var user = new AuthUser { Id = 3, ClientId = 20, Permissions = [permission] };
        Assert.Equal(canReadJobs, RecruitmentPermissions.CanViewJobPostings(user));
        Assert.Equal(canPublish, RecruitmentPermissions.CanPublishJobs(user));
        Assert.Equal(canReadForms, RecruitmentPermissions.CanViewApplicationForms(user));
        Assert.Equal(canReadJobs || canReadForms, RecruitmentPermissions.CanViewOrchestrationLookups(user));
        Assert.Equal(canReadJobs || permission == "recruitment.rfr.view", RecruitmentPermissions.CanViewJobDescriptions(user));
    }

    [Theory]
    [InlineData("recruitment.configuration.manage", 20, true)]
    [InlineData("recruitment.configuration.manage", 39, false)]
    [InlineData("recruitment.configuration.manage", 0, false)]
    [InlineData("recruitment.publish", 20, false)]
    public async Task ClientConfigurationCannotModifyOtherClientsOrGlobalSettings(string permission, int targetClientId, bool allowed)
    {
        var user = new AuthUser { Id = 3, ClientId = 20, Permissions = [permission] };
        var repository = new RecruitmentAdminRepository(new ConfigurationBuilder().Build());
        Assert.Equal(allowed, await repository.CanManageConfigurationRecordAsync("partners", 0, targetClientId, user));
    }
}
