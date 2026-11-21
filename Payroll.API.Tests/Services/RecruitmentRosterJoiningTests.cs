using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class RecruitmentRosterJoiningTests
{
    [Fact]
    public void Acceptance_requires_the_exact_released_letter_and_explicit_hr_attestation()
    {
        var pdf = new string('a', 64); var revision = new string('b', 64);
        string Receipt(long release = 81, string source = "HR bulk confirmation", bool accepted = true) => JsonSerializer.Serialize(new { version = 1, releaseId = release, pdfHash = pdf, revisionKey = revision, acceptedForJoining = accepted, source });
        Assert.True(RecruitmentDocumentRepository.IsRosterAcceptanceForRelease(Receipt(), 81, pdf, revision));
        Assert.False(RecruitmentDocumentRepository.IsRosterAcceptanceForRelease(Receipt(), 82, pdf, revision));
        Assert.False(RecruitmentDocumentRepository.IsRosterAcceptanceForRelease(Receipt(), 81, new string('c', 64), revision));
        Assert.False(RecruitmentDocumentRepository.IsRosterAcceptanceForRelease(Receipt(), 81, pdf, new string('c', 64)));
        Assert.False(RecruitmentDocumentRepository.IsRosterAcceptanceForRelease(Receipt(source: "Offer released"), 81, pdf, revision));
        Assert.False(RecruitmentDocumentRepository.IsRosterAcceptanceForRelease(Receipt(accepted: false), 81, pdf, revision));
        foreach (var malformed in new[] { "not json", "[]", "null", "{}", "{\"version\":\"1\"}", "{\"version\":1,\"releaseId\":\"81\"}" })
            Assert.False(RecruitmentDocumentRepository.IsRosterAcceptanceForRelease(malformed, 81, pdf, revision));
    }

    [Fact]
    public void Existing_employee_link_does_not_invent_candidate_acceptance_or_another_offer_id()
    {
        var row = new RecruitmentDocumentRosterRow { Id = 9, ClientId = 4, CandidateId = 14, CandidateCode = "SYN-1", CandidateName = "Synthetic person", EmployeeId = 27, EmployeeCode = "SYN-EMP", ValuesJson = "{\"joiningDate\":\"2026-11-02\",\"annualCtc\":\"120000\",\"monthlyGross\":\"50000\"}" };
        row.OfferNumber = "OFFER-SYNTHETIC-9";
        var projected = RecruitmentDocumentRepository.RosterJoiningProjection(row, "Synthetic client", 81, false);
        Assert.Equal(row.OfferNumber, projected.OfferNumber);
        Assert.Equal("Roster", projected.SourceType); Assert.Equal("roster:9", projected.RowKey); Assert.Equal(0, projected.OfferId);
        Assert.Equal(9, projected.SourceId); Assert.Equal(81, projected.ReleaseId); Assert.False(projected.AcceptanceRecorded);
        Assert.Equal("Released", projected.OfferStatus); Assert.Equal("Imported", projected.ImportStatus); Assert.Equal("SYN-EMP", projected.EmployeeCode);
        Assert.Equal(120000, projected.OfferedCtc); // Never multiply monthly emoluments into guessed CTC.
        row.EmployeeId = null; row.EmployeeCode = "";
        Assert.Equal("Review", RecruitmentDocumentRepository.RosterJoiningProjection(row, "Synthetic client", 81, false).ImportStatus);
        Assert.Equal("Ready", RecruitmentDocumentRepository.RosterJoiningProjection(row, "Synthetic client", 81, true).ImportStatus);
        var simple = RecruitmentDocumentRepository.RosterJoiningProjection(row, "Synthetic client", 81, false, importReleasedForEss: true);
        Assert.Equal("Ready", simple.ImportStatus); Assert.Empty(simple.Issue);
        Assert.False(simple.AcceptanceRecorded); Assert.Equal("Released", simple.OfferStatus);
    }

    [Fact]
    public async Task Scope_and_write_permissions_are_enforced_before_opening_a_database()
    {
        var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = "not a database" }).Build();
        var repository = new RecruitmentDocumentRepository(settings, null!, new());
        var outsider = new AuthUser { Id = 1, IsActive = true, ClientId = 7, Permissions = ["employees.manage", "recruitment.offer.issue"] };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => repository.GetRosterJoiningCandidatesAsync(8, outsider));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => repository.AcceptSelectedRosterOffersAsync(8, [1], outsider));
        var viewer = new AuthUser { Id = 1, IsActive = true, ClientId = 7, Permissions = ["recruitment.offer.issue"] };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => repository.ImportSelectedRosterJoiningCandidatesAsync(7, [1], viewer));
    }

    private sealed class IsolatedFactAttribute : FactAttribute
    {
        public IsolatedFactAttribute() { if (Environment.GetEnvironmentVariable("FREVO_JOINING_TEST_DB") is null) Skip = "An explicit isolated joining test database is required."; }
    }

    [IsolatedFact]
    public async Task Release_waits_for_recorded_acceptance_and_explicit_import_and_retries_do_not_create_another_employee()
    {
        var connection = Environment.GetEnvironmentVariable("FREVO_JOINING_TEST_DB")!;
        var parsed = new MySqlConnectionStringBuilder(connection);
        Assert.Equal("127.0.0.1", parsed.Server); Assert.Equal((uint)33330, parsed.Port); Assert.StartsWith("frevo_joining_test_", parsed.Database);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = connection, ["OutboundDelivery:Suppressed"] = "true" }).Build();
        await using var db = new MySqlConnection(connection); await db.OpenAsync();
        var token = Guid.NewGuid().ToString("N")[..10];
        var client = await db.ExecuteScalarAsync<int>("INSERT INTO clients(Name,Code,IsActive) VALUES(@Name,@Code,TRUE);SELECT LAST_INSERT_ID();", new { Name = "Joining roster " + token, Code = "TEST" + token });
        var location = await db.ExecuteScalarAsync<int>("INSERT INTO worklocations(Name,ClientId,City,State,IsActive) VALUES('Synthetic site',@Client,'Synthetic city','Synthetic state',TRUE);SELECT LAST_INSERT_ID();", new { Client = client });
        var user = new AuthUser { Id = 1, IsActive = true, ClientAccessMode = "Selected", AssignedClientIds = [client], RecruitmentScopeMode = "Client", Permissions = ["recruitment.offer.issue", "recruitment.offer.manage", "employees.manage"] };
        var repository = new RecruitmentDocumentRepository(config, null!, new()); await repository.GetRosterJoiningCandidatesAsync(client, user);
        var layout = new RecruitmentDocumentLayout { Inputs = [new() { Key = "joiningDate", Label = "Date of joining", Type = "date", Required = true }], Sections = [new() { Elements = [new() { X = 65, Y = 150, Width = 460, Height = 40, Text = "{{candidateName}} {{joiningDate.date}} {{offerNumber}}" }] }] };
        var template = await db.ExecuteScalarAsync<int>("INSERT INTO recruitment_templates(ClientId,TemplateCode,TemplateName,TemplateType,BodyTemplate,IsActive) VALUES(@ClientId,@Code,'Synthetic offer','Offer Letter',@Body,TRUE);SELECT LAST_INSERT_ID();", new { ClientId = client, Code = token, Body = LayoutDocumentPdfService.Marker + JsonSerializer.Serialize(layout, new JsonSerializerOptions(JsonSerializerDefaults.Web)) });
        var candidate = await db.ExecuteScalarAsync<long>("INSERT INTO recruitment_candidates(CandidateCode,ClientId,FirstName,LastName,Email,Phone,PreferredLocationsJson,CreatedByUserId) VALUES(@Code,@ClientId,'Synthetic','Joiner',@Email,'','[]',1);SELECT LAST_INSERT_ID();", new { Code = "SYN-" + token, ClientId = client, Email = token + "@example.invalid" });
        var values = JsonSerializer.Serialize(new Dictionary<string, string> { ["joiningDate"] = "2026-11-02", ["monthlyGross"] = "50000", ["positionTitle"] = "Synthetic role" });
        var roster = await db.ExecuteScalarAsync<long>("INSERT INTO recruitment_document_roster(ClientId,CandidateId,TemplateId,WorkLocationId,SourceGroup,SourceFileName,SourceFileHash,SourceSheetName,SourceRow,TemplateHash,ValuesJson,SourceValuesJson,CreatedByUserId,UpdatedByUserId) VALUES(@ClientId,@CandidateId,@TemplateId,@Location,'Synthetic','synthetic.xlsx',@Hash,'Employees',1,@Hash,@Json,@Json,1,1);SELECT LAST_INSERT_ID();", new { ClientId = client, CandidateId = candidate, TemplateId = template, Location = location, Hash = new string('a', 64), Json = values });
        var notReleased = await repository.AcceptSelectedRosterOffersAsync(client, [roster], user); Assert.Equal(1, notReleased.Review);
        Assert.Empty(await repository.GetRosterJoiningCandidatesAsync(client, user));
        await repository.ReleaseAsync(client, roster, false, user);
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employees WHERE ClientId=@Client", new { Client = client }));
        Assert.Null(await db.ExecuteScalarAsync<int?>("SELECT EmployeeId FROM recruitment_candidates WHERE Id=@Id", new { Id = candidate }));
        var released = Assert.Single(await repository.GetRosterJoiningCandidatesAsync(client, user)); Assert.False(released.AcceptanceRecorded); Assert.Null(released.EmployeeId);
        var restricted = new AuthUser { Id = 2, IsActive = true, ClientAccessMode = "Selected", AssignedClientIds = [client], RecruitmentScopeMode = "SelectedLocations", RecruitmentLocationIds = [], Permissions = ["recruitment.offer.issue", "employees.manage"] };
        Assert.Empty(await repository.GetRosterJoiningCandidatesAsync(client, restricted));
        var outsideLocation = await repository.AcceptSelectedRosterOffersAsync(client, [roster], restricted);
        Assert.Equal(1, outsideLocation.Review); Assert.Equal("", outsideLocation.Items.Single().CandidateName);
        Assert.Equal(1, (await repository.ImportSelectedRosterJoiningCandidatesAsync(client, [roster], user)).Review);
        var acceptedWithMissing = await repository.AcceptSelectedRosterOffersAsync(client, [roster, long.MaxValue], user);
        Assert.Equal(1, acceptedWithMissing.Accepted); Assert.Equal(1, acceptedWithMissing.Review);
        Assert.Equal(1, (await repository.AcceptSelectedRosterOffersAsync(client, [roster], user)).AlreadyAccepted);
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_document_roster_audit WHERE RosterId=@Id AND Action='Candidate acceptance recorded'", new { Id = roster }));
        var accepted = Assert.Single(await repository.GetRosterJoiningCandidatesAsync(client, user)); Assert.True(accepted.AcceptanceRecorded); Assert.Equal("Ready", accepted.ImportStatus);
        await db.ExecuteAsync("UPDATE recruitment_document_roster SET ValuesJson=JSON_SET(ValuesJson,'$.positionTitle','Revised synthetic role') WHERE Id=@Id", new { Id = roster });
        Assert.Contains("changed after release", (await repository.ImportSelectedRosterJoiningCandidatesAsync(client, [roster], user)).Items.Single().Message);
        await repository.ReleaseAsync(client, roster, false, user);
        Assert.False(Assert.Single(await repository.GetRosterJoiningCandidatesAsync(client, user)).AcceptanceRecorded);
        Assert.Equal(1, (await repository.ImportSelectedRosterJoiningCandidatesAsync(client, [roster], user)).Review);
        Assert.Equal(1, (await repository.AcceptSelectedRosterOffersAsync(client, [roster], user)).Accepted);
        var imported = await repository.ImportSelectedRosterJoiningCandidatesAsync(client, [roster], user); Assert.Equal(1, imported.Imported);
        var employee = imported.Items.Single().EmployeeId; Assert.True(employee > 0);
        var before = JsonSerializer.Serialize(await db.QuerySingleAsync("SELECT * FROM employees WHERE Id=@Id", new { Id = employee }));
        Assert.Equal(1, (await repository.ImportSelectedRosterJoiningCandidatesAsync(client, [roster], user)).AlreadyImported);
        Assert.Equal(before, JsonSerializer.Serialize(await db.QuerySingleAsync("SELECT * FROM employees WHERE Id=@Id", new { Id = employee })));
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employees WHERE ClientId=@Client", new { Client = client }));
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM notification_queue WHERE ClientId=@Client", new { Client = client }));
        var employeeOnly = new AuthUser { Id = 2, IsActive = true, ClientId = client, Permissions = ["employees.manage"] };
        Assert.Equal(employee, Assert.Single(await repository.GetRosterJoiningCandidatesAsync(client, employeeOnly)).EmployeeId);

        var simpleCandidate = await db.ExecuteScalarAsync<long>("INSERT INTO recruitment_candidates(CandidateCode,ClientId,FirstName,LastName,Email,Phone,PreferredLocationsJson,CreatedByUserId) VALUES(@Code,@ClientId,'Simple','Joiner',@Email,'','[]',1);SELECT LAST_INSERT_ID();", new { Code = "SIMPLE-" + token, ClientId = client, Email = token + "simple@example.invalid" });
        var simpleRoster = await db.ExecuteScalarAsync<long>("INSERT INTO recruitment_document_roster(ClientId,CandidateId,TemplateId,WorkLocationId,SourceGroup,SourceFileName,SourceFileHash,SourceSheetName,SourceRow,TemplateHash,ValuesJson,SourceValuesJson,CreatedByUserId,UpdatedByUserId) VALUES(@ClientId,@CandidateId,@TemplateId,@Location,'Synthetic','synthetic.xlsx',@Hash,'Employees',2,@Hash,@Json,@Json,1,1);SELECT LAST_INSERT_ID();", new { ClientId = client, CandidateId = simpleCandidate, TemplateId = template, Location = location, Hash = new string('a', 64), Json = values });
        await repository.ReleaseAsync(client, simpleRoster, false, user);
        Assert.Equal("Review", (await repository.GetRosterJoiningCandidatesAsync(client, user)).Single(row => row.SourceId == simpleRoster).ImportStatus);
        Assert.Equal("Ready", (await repository.GetRosterJoiningCandidatesAsync(client, user, true)).Single(row => row.SourceId == simpleRoster).ImportStatus);
        var simpleImport = await repository.ImportSelectedRosterJoiningCandidatesAsync(client, [simpleRoster, long.MaxValue], employeeOnly, true);
        Assert.Equal(1, simpleImport.Imported); Assert.Equal(1, simpleImport.Review);
        var simpleEmployeeId = Assert.Single(simpleImport.Items, row => row.Status == "Imported").EmployeeId;
        Assert.True(await db.ExecuteScalarAsync<bool>("SELECT PortalAccess FROM employees WHERE Id=@Id", new { Id = simpleEmployeeId }));
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_document_roster_audit WHERE RosterId=@Id AND Action='Candidate acceptance recorded'", new { Id = simpleRoster }));
        Assert.False((await repository.GetRosterJoiningCandidatesAsync(client, user, true)).Single(row => row.SourceId == simpleRoster).AcceptanceRecorded);
        Assert.Equal(1, (await repository.ImportSelectedRosterJoiningCandidatesAsync(client, [simpleRoster], employeeOnly, true)).AlreadyImported);
        Assert.Equal(2, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employees WHERE ClientId=@Client", new { Client = client }));
    }
}
