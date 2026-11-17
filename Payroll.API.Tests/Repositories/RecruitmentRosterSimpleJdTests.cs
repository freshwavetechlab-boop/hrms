using Microsoft.Extensions.Configuration;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;
using static Payroll.API.Repositories.RecruitmentDocumentRepository;

namespace Payroll.API.Tests.Repositories;

public sealed class RecruitmentRosterSimpleJdTests
{
    private static AuthUser Editor() => new()
    {
        Id = 8, IsActive = true, ClientAccessMode = "Selected", AssignedClientIds = [20],
        RecruitmentScopeMode = "Client", Permissions = ["recruitment.candidate.manage"]
    };

    [Fact]
    public void Only_a_blank_designation_accepts_an_inline_title()
    {
        Assert.Equal("Research Assistant", ResolveSimpleJdTitle("", "  Research   Assistant  "));
        Assert.Equal("Research Assistant", ResolveSimpleJdTitle("Research Assistant", "research assistant"));
        Assert.Equal("Research Assistant", ResolveSimpleJdTitle("Research Assistant", null));
        Assert.Throws<InvalidOperationException>(() => ResolveSimpleJdTitle("Research Assistant", "Accountant"));
        Assert.Throws<ArgumentException>(() => ResolveSimpleJdTitle("", ""));
    }

    [Theory]
    [InlineData("research assistant", "Research Assistant", true)]
    [InlineData("  Research   Assistant ", "Research Assistant", true)]
    [InlineData("Accountant", "Research Assistant", false)]
    [InlineData("Research Assistant", "Accountant", false)]
    [InlineData("", "Research Assistant", false)]
    [InlineData("\u202eResearch Assistant", "Research Assistant", false)]
    public void Role_edits_do_not_silently_retain_an_old_job_description(string savedTitle, string contentTitle, bool expected)
    {
        var linkedKey = Hash(RecruitmentSimpleJobDescription.NormalizeTitleKey("Research Assistant"));
        Assert.Equal(expected, SimpleJdLinkMatchesTitle(savedTitle, linkedKey, contentTitle));
    }

    [Theory]
    [InlineData("Approved", 11, true)]
    [InlineData("Draft", 11, false)]
    [InlineData("Sent Back", 11, false)]
    [InlineData("Approved", 12, false)]
    [InlineData("Approved", null, false)]
    public void Existing_native_jds_require_approval_and_the_same_permitted_location(string status, int? nativeLocation, bool expected)
    {
        var user = Editor(); user.Permissions.Add("recruitment.rfr.view");
        user.RecruitmentScopeMode = "SelectedLocations"; user.RecruitmentLocationIds = [11];
        Assert.Equal(expected, CanUseExistingSimpleJd(user, 11, nativeLocation, status, [new() { Id = 11, ClientId = 20 }]));
    }

    [Fact]
    public void Existing_native_preview_does_not_extend_native_view_permission_or_reuse_inactive_locations()
    {
        var user = Editor();
        Assert.False(CanUseExistingSimpleJd(user, 11, 11, "Approved", [new() { Id = 11, ClientId = 20 }]));
        user.Permissions.Add("recruitment.rfr.view");
        Assert.False(CanUseExistingSimpleJd(user, 11, 11, "Approved", [new() { Id = 11, ClientId = 20, IsActive = false }]));
        Assert.False(CanUseExistingSimpleJd(user, 11, 11, "Approved", []));
        Assert.True(CanUseExistingSimpleJd(user, null, null, "Approved", []));
    }

    [Fact]
    public void Batch_uses_each_saved_role_and_checks_every_row_revision()
    {
        var rows = new List<SimpleJdRosterRow>
        {
            new() { Id = 1, WorkLocationId = 11, ValuesJson = "{\"positionTitle\":\"Research Assistant\"}" },
            new() { Id = 2, WorkLocationId = 11, ValuesJson = "{\"positionTitle\":\"Accountant\"}" }
        };
        var revisions = rows.ToDictionary(row => row.Id.ToString(), row => RowRevision(row.ValuesJson, row.WorkLocationId));
        var titles = ValidateSimpleJdRows(rows, [1, 2], revisions, [], Editor(), []);
        Assert.Equal("Research Assistant", titles[1]); Assert.Equal("Accountant", titles[2]);
        revisions["2"] = "stale";
        Assert.Throws<InvalidOperationException>(() => ValidateSimpleJdRows(rows, [1, 2], revisions, [], Editor(), []));
    }

    [Fact]
    public void Batch_does_not_link_hidden_missing_or_unlinked_restricted_rows()
    {
        var user = Editor(); user.RecruitmentScopeMode = "SelectedLocations"; user.RecruitmentLocationIds = [11];
        var row = new SimpleJdRosterRow { Id = 1, WorkLocationId = 12, ValuesJson = "{\"positionTitle\":\"Research Assistant\"}" };
        var revision = new Dictionary<string, string> { ["1"] = RowRevision(row.ValuesJson, row.WorkLocationId) };
        Assert.Throws<UnauthorizedAccessException>(() => ValidateSimpleJdRows([row], [1], revision, [], user, [new() { Id = 11, ClientId = 20 }]));
        row.WorkLocationId = null; revision["1"] = RowRevision(row.ValuesJson, null);
        Assert.Throws<UnauthorizedAccessException>(() => ValidateSimpleJdRows([row], [1], revision, [], user, [new() { Id = 11, ClientId = 20 }]));
        Assert.Throws<UnauthorizedAccessException>(() => ValidateSimpleJdRows([], [1], revision, [], user, []));
    }

    [Fact]
    public void Duplicate_empty_or_unversioned_selections_are_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => ValidateSimpleJdSelection([], new Dictionary<string, string>()));
        Assert.Throws<InvalidOperationException>(() => ValidateSimpleJdSelection([1, 1], new Dictionary<string, string> { ["1"] = "revision" }));
        Assert.Throws<InvalidOperationException>(() => ValidateSimpleJdSelection([1], new Dictionary<string, string>()));
        Assert.Throws<InvalidOperationException>(() => ValidateSimpleJdSelection(Enumerable.Range(1, 201).Select(id => (long)id).ToArray(), new Dictionary<string, string>()));
    }

    [Theory]
    [InlineData("issue")]
    [InlineData("foreign-client")]
    [InlineData("password")]
    [InlineData("inactive")]
    public async Task Unavailable_or_read_only_users_are_denied_before_any_database_access(string reason)
    {
        var user = Editor();
        switch (reason)
        {
            case "issue": user.Permissions = ["recruitment.offer.issue"]; break;
            case "foreign-client": user.AssignedClientIds = [55]; break;
            case "password": user.MustChangePassword = true; break;
            case "inactive": user.IsActive = false; break;
        }
        var repository = new RecruitmentDocumentRepository(new ConfigurationBuilder().Build(), null!, null!);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => repository.CreateSimpleJdAsync(20, 1, new("Research Assistant", "revision"), user));
    }

    [Fact]
    public void Batch_ids_retain_roster_ownership_and_do_not_become_native_jd_ids()
    {
        Assert.True(SelectedClientOwnershipCatalog.TryReference("/api/recruitment/document-roster/simple-jds", "ids", "8", out var reference));
        Assert.Equal(new ScopeOwnershipReference("document-roster", "8"), reference);
        Assert.Contains(new ScopeOwnershipReference("document-roster", "8"), SelectedClientOwnershipCatalog.RouteReferences("/api/recruitment/document-roster/rows/8/simple-jd"));
    }
}
