using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class RecruitmentRosterImportLocationTests
{
    private static AuthUser Admin() => new() { ClientId = 55, RecruitmentScopeMode = "Client", Permissions = ["settings.manage"] };
    private static WorkLocation Master(int id, string name, bool active = true, int clientId = 55) =>
        new() { Id = id, ClientId = clientId, Name = name, IsActive = active };
    private static RecruitmentRosterPreviewRow Row(string name) => new()
    {
        SourceRow = 2, ProposedWorkLocation = new() { ClientId = 55, Name = name },
        Values = new() { ["monthlyGross"] = "26000.00", ["candidatePhone"] = "09876543210" },
        SourceValues = new() { ["1: Location"] = name, ["2: Gross"] = "26,000.00" }
    };
    private static void Resolve(RecruitmentRosterPreviewRow row, AuthUser user, params WorkLocation[] masters) =>
        RecruitmentDocumentRepository.ResolveImportLocation(row, 55, user, masters, masters.Where(master => master.ClientId == 55 && master.IsActive).Select(master => master.Id).ToHashSet());

    [Fact]
    public void Case_unicode_and_whitespace_reuse_the_selected_clients_master_without_fuzzy_punctuation_matching()
    {
        var row = Row("  Ｉｎｓｔｉｔｕｔｅ   A  ");
        Resolve(row, Admin(), Master(7, "Institute A"), Master(8, "Institute A", clientId: 99));
        Assert.Empty(row.ImportIssues); Assert.Equal(7, row.WorkLocationId); Assert.Equal("Existing", row.WorkLocationStatus);
        Assert.NotEqual(RecruitmentDocumentRepository.ImportLocationKey("Unit A/B"), RecruitmentDocumentRepository.ImportLocationKey("Unit AB"));
    }

    [Fact]
    public void New_master_is_only_a_plan_during_preview_and_preserves_source_records()
    {
        var row = Row("Institute B");
        var source = JsonSerializer.Serialize(row.SourceValues); var values = JsonSerializer.Serialize(row.Values);
        Resolve(row, Admin(), Master(7, "Institute A"), Master(8, "Institute B", clientId: 99));
        Assert.Empty(row.ImportIssues); Assert.Null(row.WorkLocationId); Assert.Equal("New", row.WorkLocationStatus);
        Assert.Equal(0, row.ProposedWorkLocation!.Id); Assert.Equal(55, row.ProposedWorkLocation.ClientId);
        Assert.Equal(source, JsonSerializer.Serialize(row.SourceValues)); Assert.Equal(values, JsonSerializer.Serialize(row.Values));
    }

    [Fact]
    public void Explicit_master_id_wins_over_a_different_source_location()
    {
        var row = Row("A source location that should be ignored"); row.WorkLocationId = 7;
        Resolve(row, Admin(), Master(7, "Selected institute"));
        Assert.Empty(row.ImportIssues); Assert.Equal("Selected institute", row.WorkLocation);
        Assert.Equal("Existing", row.WorkLocationStatus); Assert.Equal(7, row.ProposedWorkLocation!.Id);
    }

    [Fact]
    public void Inactive_or_ambiguous_names_require_review_instead_of_creating_another_master()
    {
        var inactive = Row("Institute A"); Resolve(inactive, Admin(), Master(7, "Institute A", active: false));
        Assert.Contains(inactive.ImportIssues, message => message.Contains("inactive")); Assert.Null(inactive.WorkLocationId);
        var duplicate = Row("Institute A"); Resolve(duplicate, Admin(), Master(7, "Institute A"), Master(8, " institute   a "));
        Assert.Contains(duplicate.ImportIssues, message => message.Contains("Multiple work location")); Assert.Null(duplicate.WorkLocationId);
        var selected = Row("Institute A"); selected.WorkLocationId = 8;
        Resolve(selected, Admin(), Master(7, "Institute A"), Master(8, " institute   a "));
        Assert.Empty(selected.ImportIssues); Assert.Equal(8, selected.WorkLocationId);
    }

    [Theory]
    [InlineData("settings.manage")]
    [InlineData("client.settings.manage")]
    [InlineData("SETTINGS.MANAGE")]
    public void Creation_uses_native_work_location_permissions(string permission)
    {
        var user = new AuthUser { ClientId = 55, RecruitmentScopeMode = "Client", Permissions = [permission] };
        Assert.True(RecruitmentDocumentRepository.CanCreateImportLocation(user, 55));
        Assert.False(RecruitmentDocumentRepository.CanCreateImportLocation(user, 99));
    }

    [Fact]
    public void Candidate_import_permission_can_link_existing_but_cannot_create_masters()
    {
        var user = new AuthUser { ClientId = 55, RecruitmentScopeMode = "Client", Permissions = ["recruitment.candidate.manage"] };
        var existing = Row("Institute A"); Resolve(existing, user, Master(7, "Institute A"));
        Assert.Empty(existing.ImportIssues); Assert.Equal(7, existing.WorkLocationId);
        var pending = Row("Institute B"); Resolve(pending, user);
        Assert.Contains(pending.ImportIssues, message => message.Contains("Work Location Master permission")); Assert.Null(pending.WorkLocationId);
    }

    [Theory]
    [InlineData("SelectedLocations")]
    [InlineData("EmployeeLocation")]
    public void Restricted_location_scopes_cannot_create_or_link_outside_their_permitted_ids(string mode)
    {
        var user = Admin(); user.RecruitmentScopeMode = mode; user.RecruitmentLocationIds = [7];
        var existing = Row("Institute A");
        RecruitmentDocumentRepository.ResolveImportLocation(existing, 55, user, [Master(7, "Institute A"), Master(8, "Institute B")], new HashSet<int> { 7 });
        Assert.Empty(existing.ImportIssues); Assert.Equal(7, existing.WorkLocationId);
        var outside = Row("Institute B");
        RecruitmentDocumentRepository.ResolveImportLocation(outside, 55, user, [Master(8, "Institute B")], new HashSet<int> { 7 });
        Assert.Contains(outside.ImportIssues, message => message.Contains("outside your permitted"));
        var newMaster = Row("New institute"); Resolve(newMaster, user);
        Assert.False(RecruitmentDocumentRepository.CanCreateImportLocation(user, 55));
        Assert.Contains(newMaster.ImportIssues, message => message.Contains("restricted location scope"));
    }

    [Fact]
    public void Missing_locations_remain_optional_for_client_scope_and_block_restricted_imports()
    {
        var row = Row(""); Resolve(row, Admin()); Assert.Empty(row.ImportIssues); Assert.Equal("Unassigned", row.WorkLocationStatus);
        var restricted = Admin(); restricted.RecruitmentScopeMode = "SelectedLocations";
        var missing = Row(""); Resolve(missing, restricted);
        Assert.Contains(missing.ImportIssues, message => message.Contains("within your permitted"));
    }

    [Fact]
    public void Mapped_dynamic_aliases_capture_only_supplied_workplace_metadata_without_inference()
    {
        RecruitmentDocumentLayout layout = new() { Inputs = [
            new() { Key = "centre", Label = "Posting centre", SourceHeaders = ["Name of Institute"] },
            new() { Key = "workLocationAddress", Label = "Institute address" },
            new() { Key = "workLocationCity", Label = "Institute city" },
            new() { Key = "personalAddress", Label = "Home address", SourceHeaders = ["Address"] },
            new() { Key = "monthlyGross", Label = "Monthly gross", Type = "money" }] };
        Dictionary<string, string> raw = new() { ["centre"] = "Institute - Mumbai", ["workLocationAddress"] = "Actual workplace address", ["workLocationCity"] = "", ["personalAddress"] = "Home only", ["monthlyGross"] = "26000.00" };
        var before = JsonSerializer.Serialize(raw); List<string> issues = [];
        var location = RecruitmentDocumentRepository.ReadImportLocation(layout, raw, 55, issues);
        Assert.Empty(issues); Assert.Equal("Institute - Mumbai", location.Name); Assert.Equal("Actual workplace address", location.Address);
        Assert.Equal("", location.City); Assert.Equal("", location.State); Assert.Equal("", location.PostalCode);
        Assert.Equal(before, JsonSerializer.Serialize(raw));
    }

    [Fact]
    public void Metadata_only_location_column_works_for_custom_layouts_without_a_location_input()
    {
        RecruitmentDocumentLayout layout = new() { Inputs = [new() { Key = "candidateName", Label = "Employee" }] };
        List<string> issues = [];
        var location = RecruitmentDocumentRepository.ReadImportLocation(layout, new Dictionary<string, string>(), 55, issues,
            ["Employee", "Location", "Work Location State", "Address"], ["Example", "Institute A", "State A", "Private home"]);
        Assert.Empty(issues); Assert.Equal("Institute A", location.Name); Assert.Equal("State A", location.State); Assert.Equal("", location.Address);
    }

    [Fact]
    public void Configured_but_unmapped_location_is_not_filled_from_metadata_fallback()
    {
        RecruitmentDocumentLayout layout = new() { Inputs = [new() { Key = "jobLocation", Label = "Work location" }] };
        List<string> issues = [];
        var location = RecruitmentDocumentRepository.ReadImportLocation(layout, new Dictionary<string, string> { ["jobLocation"] = "" }, 55, issues,
            ["Location"], ["A source column intentionally left unmapped"]);
        Assert.Empty(issues); Assert.Equal("", location.Name);
    }

    [Fact]
    public void Configured_empty_markers_remain_unassigned_instead_of_creating_placeholder_masters()
    {
        RecruitmentDocumentLayout layout = new() { Inputs = [new() { Key = "jobLocation", Label = "Work location", EmptyValues = ["To update"] }] };
        List<string> issues = [];
        var location = RecruitmentDocumentRepository.ReadImportLocation(layout, new Dictionary<string, string> { ["jobLocation"] = "To update" }, 55, issues);
        var row = Row(""); row.ProposedWorkLocation = location; Resolve(row, Admin());
        Assert.Empty(issues); Assert.Empty(row.ImportIssues); Assert.Equal("Unassigned", row.WorkLocationStatus);
        Assert.Null(row.WorkLocationId);
    }

    [Fact]
    public void Conflicting_source_location_columns_are_flagged_and_blank_columns_do_not_conflict()
    {
        RecruitmentDocumentLayout layout = new(); List<string> issues = [];
        RecruitmentDocumentRepository.ReadImportLocation(layout, new Dictionary<string, string>(), 55, issues,
            ["Location", "Institute"], ["Institute A", "Institute B"]);
        Assert.Contains(issues, message => message.Contains("More than one work location name"));
        issues.Clear();
        var location = RecruitmentDocumentRepository.ReadImportLocation(layout, new Dictionary<string, string>(), 55, issues,
            ["Location", "Institute"], ["Institute A", ""]);
        Assert.Empty(issues); Assert.Equal("Institute A", location.Name);
    }

    [Fact]
    public void Repeated_new_location_rows_share_known_details_and_conflicts_require_review()
    {
        var first = Row("Institute A"); var second = Row(" institute   a ");
        first.ProposedWorkLocation!.City = "City A"; second.ProposedWorkLocation!.Address = "Address A";
        Resolve(first, Admin()); Resolve(second, Admin());
        RecruitmentDocumentRepository.MergeNewImportLocationDetails([first, second]);
        Assert.Equal("City A", second.ProposedWorkLocation.City); Assert.Equal("Address A", first.ProposedWorkLocation.Address);
        Assert.Empty(first.ImportIssues); Assert.Empty(second.ImportIssues);
        second.ProposedWorkLocation.City = "City B";
        RecruitmentDocumentRepository.MergeNewImportLocationDetails([first, second]);
        Assert.Contains(first.ImportIssues, message => message.Contains("conflicting city"));
        Assert.Contains(second.ImportIssues, message => message.Contains("conflicting city"));
    }

    [Fact]
    public void Oversized_or_invalid_master_metadata_cannot_reach_creation()
    {
        var longName = Row(new string('x', 201)); Resolve(longName, Admin());
        Assert.Contains(longName.ImportIssues, message => message.Contains("200 characters"));
        var invalidPin = Row("Institute A"); invalidPin.ProposedWorkLocation!.PostalCode = "000000"; Resolve(invalidPin, Admin());
        Assert.Contains(invalidPin.ImportIssues, message => message.Contains("6-digit"));
    }

    [Fact]
    public void Selected_client_access_is_enforced_even_with_a_matching_location_id()
    {
        var user = Admin(); user.ClientAccessMode = "Selected"; user.AssignedClientIds = [99];
        var row = Row("Institute A"); row.WorkLocationId = 7; Resolve(row, user, Master(7, "Institute A"));
        Assert.Contains(row.ImportIssues, message => message.Contains("client scope"));
    }
}
