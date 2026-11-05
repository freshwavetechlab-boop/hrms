using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class RecruitmentWorkLocationScopeTests
{
    [Theory]
    [InlineData(11, true)]
    [InlineData(12, false)]
    [InlineData(0, false)]
    [InlineData(null, false)]
    public void SelectedLocationsRequireTheExactPostingId(int? locationId, bool expected)
    {
        var user = new AuthUser { ClientId = 55, RecruitmentScopeMode = "SelectedLocations", RecruitmentLocationIds = [11] };
        Assert.Equal(expected, RecruitmentAccessScope.AllowsKnownWorkLocation(user, 55, locationId));
        Assert.False(RecruitmentAccessScope.AllowsKnownWorkLocation(user, 20, locationId));
    }

    [Theory]
    [InlineData(11, 11, true)]
    [InlineData(12, 11, false)]
    [InlineData(11, null, false)]
    [InlineData(null, 11, false)]
    public void EmployeeLocationCannotInferAnotherInstituteFromItsCity(int? postingId, int? employeeLocation, bool expected)
    {
        var user = new AuthUser { ClientId = 55, RecruitmentScopeMode = "EmployeeLocation", EmployeeId = 8 };
        Assert.Equal(expected, RecruitmentAccessScope.AllowsKnownWorkLocation(user, 55, postingId, employeeLocation));
    }

    [Fact]
    public void ClientWideUsersKeepLegacyUnlinkedRowsWhileSelectedClientAssignmentsStillApply()
    {
        var user = new AuthUser { ClientAccessMode = "Selected", AssignedClientIds = [55, 20], RecruitmentScopeMode = "Client" };
        Assert.True(RecruitmentAccessScope.AllowsKnownWorkLocation(user, 55, null));
        Assert.True(RecruitmentAccessScope.AllowsKnownWorkLocation(user, 20, 11));
        Assert.False(RecruitmentAccessScope.AllowsKnownWorkLocation(user, 99, 11));
    }

    [Fact]
    public void AnEffectiveSelectedRequestCannotUseAnotherAssignedClientLocation()
    {
        var user = new AuthUser { ClientId = 55, ClientAccessMode = "Selected", AssignedClientIds = [55, 20], RecruitmentScopeMode = "Client" };
        Assert.True(RecruitmentAccessScope.AllowsKnownWorkLocation(user, 55, 11));
        Assert.False(RecruitmentAccessScope.AllowsKnownWorkLocation(user, 20, 11));
    }

    [Theory]
    [InlineData("/api/recruitment/document-roster/releases")]
    [InlineData("/api/recruitment/document-roster/drafts")]
    [InlineData("/api/recruitment/document-roster/rows/work-location")]
    public void RosterBulkIdsHaveDocumentRowOwnership(string path)
    {
        Assert.True(SelectedClientOwnershipCatalog.TryReference(path, "ids", "8", out var reference));
        Assert.Equal(new ScopeOwnershipReference("document-roster", "8"), reference);
        Assert.True(SelectedClientOwnershipCatalog.TryReference(path, "workLocationId", "11", out var location));
        Assert.Equal(new ScopeOwnershipReference("location", "11"), location);
    }

    [Fact]
    public void GenericIdsDoNotAcquireRosterAuthorityInUnrelatedRequests() =>
        Assert.False(SelectedClientOwnershipCatalog.TryReference("/api/pay-runs", "ids", "8", out _));
}
