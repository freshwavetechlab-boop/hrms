using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public class AuthClientAssignmentsTests
{
    [Fact]
    public void LegacyAccountsKeepTheirOriginalSingleOrAllClients()
    {
        foreach (var clientId in new int?[] { null, 55 })
        {
            var request = new SaveAuthUserRequest { ClientId = clientId };
            AuthRepository.NormalizeClientAccess(request);
            Assert.Equal("Legacy", request.ClientAccessMode);
            Assert.Equal(clientId, request.ClientId);
            Assert.Empty(request.AssignedClientIds);
        }
    }

    [Fact]
    public void SelectedClientsArePositiveDistinctAndExplicit()
    {
        var request = new SaveAuthUserRequest { ClientAccessMode = " selected ", AssignedClientIds = [55, 20, 55] };
        AuthRepository.NormalizeClientAccess(request);
        Assert.Equal("Selected", request.ClientAccessMode);
        Assert.Equal(new[] { 20, 55 }, request.AssignedClientIds);
        Assert.Null(request.ClientId);
    }

    [Fact]
    public void EmptyUnknownOrAmbiguousScopesCannotBecomeAllClients()
    {
        var requests = new[] {
            new SaveAuthUserRequest { ClientAccessMode="Selected" },
            new SaveAuthUserRequest { ClientAccessMode="All" },
            new SaveAuthUserRequest { ClientAccessMode="Selected", AssignedClientIds=[0,55] },
            new SaveAuthUserRequest { ClientAccessMode="Selected", AssignedClientIds=[55], ClientId=55 },
            new SaveAuthUserRequest { ClientAccessMode="Selected", AssignedClientIds=[55], EmployeeId=1 },
            new SaveAuthUserRequest { AssignedClientIds=[55] },
            new SaveAuthUserRequest { ClientAccessMode="Selected", AssignedClientIds=[55], RecruitmentScopeMode="EmployeeLocation" },
        };
        foreach (var request in requests)
            Assert.Throws<InvalidOperationException>(() => AuthRepository.NormalizeClientAccess(request));
    }

    [Fact]
    public void AClientBoundAdministratorCannotAssignSelectedClients()
    {
        var request = new SaveAuthUserRequest { ClientAccessMode="Selected", AssignedClientIds=[20,55] };
        Assert.Throws<InvalidOperationException>(() => AuthRepository.NormalizeClientAccess(request, 20));
    }
}
