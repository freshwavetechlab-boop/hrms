using Microsoft.Extensions.Configuration;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class RecruitmentRosterReleaseTests
{
    [Fact]
    public void Master_location_overrides_all_institution_aliases_without_changing_salary_or_contacts()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        { ["institutionName"] = "Old source", ["institutionShortName"] = "Old abbreviation", ["institutionLocation"] = "Old city", ["monthlyGross"] = "26000.00", ["candidatePhone"] = "09876543210", ["contractEndDate"] = "2027-03-31" };
        RecruitmentDocumentRepository.ApplyLocationValues(values, new() { Id = 11, Name = "Institute A", Address = "Master address", City = "City A", State = "State A" });
        Assert.Equal("Institute A", values["jobLocation"]); Assert.Equal("Institute A", values["workLocation"]);
        Assert.Equal("Institute A", values["institutionName"]); Assert.Equal("Institute A", values["institutionShortName"]);
        Assert.Equal("City A", values["institutionLocation"]); Assert.Equal("City A", values["workLocationCity"]); Assert.Equal("State A", values["workLocationState"]); Assert.Equal("Master address", values["instituteAddress"]);
        Assert.Equal("26000.00", values["monthlyGross"]); Assert.Equal("09876543210", values["candidatePhone"]); Assert.Equal("2027-03-31", values["contractEndDate"]);
    }
    [Theory]
    [InlineData("ICMR-NIRWoH - Mumbai", "Mumbai", "ICMR-NIRWoH")]
    [InlineData("MRHRU, Dahanu", "Dahanu", "MRHRU")]
    [InlineData("MRHRU - Vani, Nashik", "Nashik", "MRHRU - Vani")]
    [InlineData("Institute \u2013 Mumbai", "Mumbai", "Institute")]
    [InlineData("Institute - MUMBAI", "Mumbai", "Institute")]
    [InlineData("ICMR-NIMR - Delhi", "New Delhi", "ICMR-NIMR - Delhi")]
    [InlineData("Mumbai Institute", "Mumbai", "Mumbai Institute")]
    public void Institution_short_name_removes_only_an_exact_terminal_master_city(string name, string city, string expected)
    {
        var values = new Dictionary<string, string>();
        RecruitmentDocumentRepository.ApplyLocationValues(values, new() { Name = name, City = city, State = "State" });
        Assert.Equal(name, values["institutionName"]); Assert.Equal(name, values["jobLocation"]); Assert.Equal(name, values["workLocation"]);
        Assert.Equal(expected, values["institutionShortName"]); Assert.Equal(city, values["institutionLocation"]);
    }
    [Theory]
    [InlineData("State", "Address", "State")]
    [InlineData("", "Address", "Address")]
    public void Institution_location_uses_state_or_address_only_when_city_is_not_supplied(string state, string address, string expected)
    {
        var values = new Dictionary<string, string>();
        RecruitmentDocumentRepository.ApplyLocationValues(values, new() { Name = "Institute", State = state, Address = address });
        Assert.Equal(expected, values["institutionLocation"]); Assert.Equal("Institute", values["institutionShortName"]);
    }
    [Fact]
    public void Location_revision_changes_even_when_document_input_values_do_not()
    {
        var original = RecruitmentDocumentRepository.RowRevision("{\"monthlyGross\":\"26000\"}", null);
        var linked = RecruitmentDocumentRepository.RowRevision("{\"monthlyGross\":\"26000\"}", 11);
        Assert.NotEqual(original, linked); Assert.NotEqual(linked, RecruitmentDocumentRepository.RowRevision("{\"monthlyGross\":\"26000\"}", 22));
        Assert.Equal(linked, RecruitmentDocumentRepository.RowRevision("{\"monthlyGross\":\"26000\"}", 11));
    }
    [Fact]
    public void Offer_number_uses_stable_row_and_client_code_without_paths()
    {
        Assert.Equal("GAD/HR/CLIENT_A/2026/000123", RecruitmentDocumentRepository.NewOfferNumber("Client_A", 2026, 123));
        Assert.Equal("GAD/HR/CLIENT/2026/000123", RecruitmentDocumentRepository.NewOfferNumber("../Client/", 2026, 123));
        Assert.Throws<InvalidOperationException>(() => RecruitmentDocumentRepository.NewOfferNumber(" / ", 2026, 123));
    }
    [Fact]
    public async Task New_mutations_require_offer_permission_and_authoritative_client_scope_before_database_access()
    {
        var repository = new RecruitmentDocumentRepository(new ConfigurationBuilder().Build(), null!, new());
        AuthUser candidateOnly = new() { Id = 7, IsActive = true, ClientId = 55, Permissions = ["recruitment.candidate.manage"] };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => repository.AssignWorkLocationAsync(55, new([1], 11, new() { ["1"] = new('a', 64) }), candidateOnly));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => repository.SaveSigningAssetAsync(55, [], "test.png", candidateOnly));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => repository.ReleaseAsync(55, 1, false, candidateOnly));
        AuthUser manageOnly = new() { Id = 9, IsActive = true, ClientId = 55, Permissions = ["recruitment.offer.manage"] };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => repository.ReleaseAsync(55, 1, false, manageOnly));
        AuthUser selected = new() { Id = 7, IsActive = true, ClientAccessMode = "Selected", AssignedClientIds = [55], Permissions = ["recruitment.offer.manage"] };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => repository.SigningAssetAsync(56, selected));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => repository.ReleaseAsync(56, 1, true, selected));
    }
}
