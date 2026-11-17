using Microsoft.Extensions.Configuration;
using Payroll.API.Models;
using Payroll.API.Repositories;
using static Payroll.API.Repositories.RecruitmentDocumentRepository;

namespace Payroll.API.Tests.Repositories;

public sealed class RecruitmentDocumentClientSummaryTests
{
    private static AuthUser User(string scope = "Client") => new()
    {
        Id = 7, IsActive = true, ClientAccessMode = "Selected", AssignedClientIds = [20, 55],
        RecruitmentScopeMode = scope, Permissions = ["recruitment.offer.issue"]
    };

    private static readonly ClientLocationDocumentCount[] Counts =
    [
        new() { ClientId = 20, WorkLocationId = 11, RosterCount = 2, OfferCount = 3 },
        new() { ClientId = 20, WorkLocationId = 12, RosterCount = 5, OfferCount = 7 },
        new() { ClientId = 20, WorkLocationId = null, RosterCount = 11, OfferCount = 13 },
        new() { ClientId = 20, WorkLocationId = 14, RosterCount = 17, OfferCount = 19 },
        new() { ClientId = 55, WorkLocationId = 21, RosterCount = 23, OfferCount = 29 },
        new() { ClientId = 99, WorkLocationId = 31, RosterCount = 31, OfferCount = 37 }
    ];
    private static readonly WorkLocation[] Locations =
    [
        new() { Id = 11, ClientId = 20 }, new() { Id = 12, ClientId = 20 },
        new() { Id = 14, ClientId = 20, IsActive = false },
        new() { Id = 21, ClientId = 55 }, new() { Id = 31, ClientId = 99 }
    ];

    [Fact]
    public void Client_scope_includes_unlinked_rows_but_excludes_unassigned_clients()
    {
        var result = BuildClientSummary(User(), [20, 55, 99], Counts, Locations, new Dictionary<int, int?>());
        Assert.Equal(new ClientDocumentSummary(20, 35, 42), result[0]);
        Assert.Equal(new ClientDocumentSummary(55, 23, 29), result[1]);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Effective_client_cannot_count_another_assigned_client()
    {
        var user = User(); user.ClientId = 55;
        Assert.Equal(new ClientDocumentSummary(55, 23, 29),
            Assert.Single(BuildClientSummary(user, [20, 55, 99], Counts, Locations, new Dictionary<int, int?>())));
    }

    [Fact]
    public void Selected_locations_count_only_exact_active_ids_for_both_sources()
    {
        var user = User("SelectedLocations"); user.RecruitmentLocationIds = [11, 14, 31];
        var result = BuildClientSummary(user, [20, 55, 99], Counts, Locations, new Dictionary<int, int?>());
        Assert.Equal(new ClientDocumentSummary(20, 2, 3), result[0]);
        Assert.Equal(new ClientDocumentSummary(55, 0, 0), result[1]);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Employee_location_does_not_extend_to_other_clients_or_unlinked_rows()
    {
        var user = User("EmployeeLocation"); user.EmployeeId = 8;
        var result = BuildClientSummary(user, [20, 55], Counts, Locations, new Dictionary<int, int?> { [20] = 12 });
        Assert.Equal(new ClientDocumentSummary(20, 5, 7), result[0]);
        Assert.Equal(new ClientDocumentSummary(55, 0, 0), result[1]);
    }

    [Fact]
    public void Authorized_clients_without_data_are_present_with_zero_counts()
    {
        Assert.All(BuildClientSummary(User(), [20, 55], [], [], new Dictionary<int, int?>()),
            row => { Assert.Equal(0, row.RosterCount); Assert.Equal(0, row.OfferCount); });
    }

    [Fact]
    public void Candidate_only_reader_cannot_see_native_offer_availability()
    {
        var user = User(); user.Permissions = ["recruitment.candidate.manage"];
        var result = BuildClientSummary(user, [20, 55], Counts, Locations, new Dictionary<int, int?>());
        Assert.Equal(new ClientDocumentSummary(20, 35, 0), result[0]);
        Assert.Equal(new ClientDocumentSummary(55, 23, 0), result[1]);
        Assert.True(CanReadRosterForSummary(user)); Assert.False(CanReadNativeOffersForSummary(user));
    }

    [Theory]
    [InlineData("recruitment.proposal.manage")]
    [InlineData("recruitment.proposal.approve")]
    public void Proposal_only_reader_cannot_see_roster_availability(string permission)
    {
        var user = User(); user.Permissions = [permission];
        var result = BuildClientSummary(user, [20, 55], Counts, Locations, new Dictionary<int, int?>());
        Assert.Equal(new ClientDocumentSummary(20, 0, 42), result[0]);
        Assert.Equal(new ClientDocumentSummary(55, 0, 29), result[1]);
        Assert.False(CanReadRosterForSummary(user)); Assert.True(CanReadNativeOffersForSummary(user));
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("inactive")]
    [InlineData("password")]
    [InlineData("permission")]
    public async Task Unavailable_or_unauthorized_users_are_rejected_before_database_access(string reason)
    {
        var user = User();
        switch (reason)
        {
            case "identity": user.Id = 0; break;
            case "inactive": user.IsActive = false; break;
            case "password": user.MustChangePassword = true; break;
            case "permission": user.Permissions = []; break;
        }
        var repository = new RecruitmentDocumentRepository(new ConfigurationBuilder().Build(), null!, null!);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => repository.ClientSummaryAsync(user));
    }
}
