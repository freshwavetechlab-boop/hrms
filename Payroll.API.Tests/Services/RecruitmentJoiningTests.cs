using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Http;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class RecruitmentJoiningTests
{
    [Fact]
    public void BulkSelectionBoundsBothSourcesAndDoesNotCollideTheirIds()
    {
        Assert.Equal(new long[] { 9 }, RecruitmentTalentRepository.JoiningSelection(new() { ClientId = 7, OfferIds = [9, 9], RosterRowIds = [9] }));
        Assert.Empty(RecruitmentTalentRepository.JoiningSelection(new() { ClientId = 7, RosterRowIds = [9] }));
        Assert.Throws<InvalidOperationException>(() => RecruitmentTalentRepository.JoiningSelection(new() { ClientId = 7 }));
        Assert.Throws<InvalidOperationException>(() => RecruitmentTalentRepository.JoiningSelection(new() { ClientId = 0, OfferIds = [9] }));
        Assert.Throws<InvalidOperationException>(() => RecruitmentTalentRepository.JoiningSelection(new() { ClientId = 7, OfferIds = [-1] }));
        Assert.Throws<InvalidOperationException>(() => RecruitmentTalentRepository.JoiningSelection(new() { ClientId = 7, OfferIds = Enumerable.Range(1, 500).Select(id => (long)id).ToList(), RosterRowIds = [1] }));
    }

    [Fact]
    public void JoiningScopeRetainsAssignedClientsAndStagePolicyRatherThanWideningTheUser()
    {
        var user = new AuthUser { Id = 1, IsActive = true, ClientAccessMode = "Selected", AssignedClientIds = [7], RecruitmentScopeMode = "SelectedLocations", RecruitmentLocationIds = [3] };
        var scoped = RecruitmentTalentRepository.JoiningUser(user, 7);
        Assert.Equal(7, scoped.ClientId); Assert.Equal("Selected", scoped.ClientAccessMode); Assert.Equal(new[] { 3 }, scoped.RecruitmentLocationIds);
        Assert.Null(user.ClientId);
        Assert.Throws<UnauthorizedAccessException>(() => RecruitmentTalentRepository.JoiningUser(user, 8));
        user.MustChangePassword = true;
        Assert.Throws<UnauthorizedAccessException>(() => RecruitmentTalentRepository.JoiningUser(user, 7));
    }

    [Fact]
    public void JoiningDefaultsUseRecordedOfferAndOnlyUnambiguousClientConfiguration()
    {
        var row = new RecruitmentJoiningCandidate { CandidateCode = "CAND-17", ProposedJoiningDate = new DateTime(2026, 11, 2), OfferedCtc = 840000 };
        var request = RecruitmentTalentRepository.JoiningDefaults(row, [9], [15]);
        Assert.Equal("CAND-17", request.EmployeeCode); Assert.Equal("2026-11-02", request.DateOfJoining); Assert.Equal(840000, request.AnnualCtc);
        Assert.Equal(9, request.WorkLocationId); Assert.Equal("15", request.SalaryStructureId); Assert.False(request.PortalAccess);
        var ambiguous = RecruitmentTalentRepository.JoiningDefaults(row, [9, 10], [15, 16]);
        Assert.Equal(0, ambiguous.WorkLocationId); Assert.Empty(ambiguous.SalaryStructureId);
        Assert.Empty(request.WorkEmail); Assert.Equal(0, request.ReportingManagerId);
    }

    [Fact]
    public void EmployeeImportNeedsEmployeeAuthorityAndPartialCountsRemainDistinct()
    {
        Assert.False(RecruitmentJoiningEndpoints.CanImport(new() { Permissions = ["recruitment.offer.issue"] }));
        Assert.True(RecruitmentJoiningEndpoints.CanImport(new() { Permissions = ["employees.manage"] }));
        var result = new RecruitmentJoiningBatchResult { Items = [new() { Status = "Accepted" }, new() { Status = "Already accepted" }, new() { Status = "Imported" }, new() { Status = "Already imported" }, new() { Status = "Review" }] };
        Assert.Equal(1, result.Accepted); Assert.Equal(1, result.AlreadyAccepted); Assert.Equal(1, result.Imported); Assert.Equal(1, result.AlreadyImported); Assert.Equal(1, result.Review);
        Assert.True(SelectedClientOwnershipCatalog.TryReference("/api/recruitment/offers/accept-selected", "rosterRowIds", "9", out var reference));
        Assert.Equal("document-roster", reference.Resource);
    }

    private sealed class IsolatedFactAttribute : FactAttribute
    {
        public IsolatedFactAttribute() { if (Environment.GetEnvironmentVariable("FREVO_JOINING_TEST_DB") is null) Skip = "Explicit isolated localhost joining schema required."; }
    }

    [Fact]
    public async Task PipelineAuthorityFailureDoesNotTurnAnAlreadySavedEmployeeImportIntoAWholeRequestFailure()
    {
        var result = new RecruitmentJoiningBatchResult { Items = [new() { OfferId = 9, ApplicationId = 19, Status = "Imported", EmployeeId = 91 }, new() { OfferId = 10, ApplicationId = 20, Status = "Imported", EmployeeId = 92 }] };
        await RecruitmentJoiningEndpoints.Synchronize(result, "Imported", "Joined", new DefaultHttpContext(), new() { Permissions = ["employees.manage"] },
            (_, id, _, _) => id == 19 ? throw new UnauthorizedAccessException() : Task.FromResult("Pipeline updated"));
        Assert.Equal(2, result.Imported); Assert.Equal(0, result.Review);
        Assert.StartsWith("Saved.", result.Items[0].Message); Assert.Equal(91, result.Items[0].EmployeeId);
        Assert.Equal("Pipeline updated", result.Items[1].Message);
    }

    [IsolatedFact]
    public async Task NormalOffersAcceptInBulkPersistForJoiningAndImportOnceWithPartialReviewAndNoMail()
    {
        var cs = Environment.GetEnvironmentVariable("FREVO_JOINING_TEST_DB")!;
        var target = new MySqlConnectionStringBuilder(cs);
        Assert.Equal("127.0.0.1", target.Server); Assert.Equal((uint)33330, target.Port); Assert.StartsWith("frevo_joining_test_", target.Database);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["ConnectionStrings:Default"] = cs, ["OutboundDelivery:Suppressed"] = "true" }).Build();
        await using var db = new MySqlConnection(cs); await db.OpenAsync();
        var token = Guid.NewGuid().ToString("N")[..12];
        var client = await db.ExecuteScalarAsync<int>("INSERT INTO clients(Name,Code,IsActive) VALUES(@Name,@Code,TRUE);SELECT LAST_INSERT_ID();", new { Name = "Joining test " + token, Code = "JOIN" + token });
        var other = await db.ExecuteScalarAsync<int>("INSERT INTO clients(Name,Code,IsActive) VALUES(@Name,@Code,TRUE);SELECT LAST_INSERT_ID();", new { Name = "Other joining test " + token, Code = "OTHER" + token });
        var actor = await db.ExecuteScalarAsync<int>("INSERT INTO authusers(Email,DisplayName,PasswordHash,IsActive) VALUES(@Email,'Synthetic verifier','not-a-live-login',TRUE);SELECT LAST_INSERT_ID();", new { Email = token + "@example.invalid" });
        var location = await db.ExecuteScalarAsync<int>("INSERT INTO worklocations(ClientId,Name,IsActive) VALUES(@ClientId,'Synthetic site',TRUE);SELECT LAST_INSERT_ID();", new { ClientId = client });
        var rfr = await db.ExecuteScalarAsync<long>("INSERT INTO recruitment_requisitions(RfrNumber,RequestedByEmployeeId,RequestedByUserId,ClientId,PositionTitle,Status) VALUES(@Code,0,@Actor,@Client,'Synthetic researcher','Approved');SELECT LAST_INSERT_ID();", new { Code = "RFR" + token, Actor = actor, Client = client });
        var position = await db.ExecuteScalarAsync<long>("INSERT INTO recruitment_open_positions(RequisitionId,PositionCode,ClientId,BranchId,PositionTitle,Department,JobLocation,NumberOfPositions,ApprovedPositions,RemainingPositions) VALUES(@Rfr,@Code,@Client,@Location,'Synthetic researcher','Research','Synthetic site',10,10,10);SELECT LAST_INSERT_ID();", new { Rfr = rfr, Code = "POS" + token, Client = client, Location = location });
        var user = new AuthUser { Id = actor, DisplayName = "Synthetic verifier", IsActive = true, ClientAccessMode = "Selected", AssignedClientIds = [client], RecruitmentScopeMode = "Client", Permissions = ["employees.manage", "recruitment.offer.issue"] };
        async Task<(long Candidate,long Application,long Offer)> Add(string suffix, string status = "Pending Candidate")
        {
            var candidate = await db.ExecuteScalarAsync<long>("INSERT INTO recruitment_candidates(CandidateCode,ClientId,FirstName,LastName,Email,PreferredLocationsJson,CreatedByUserId) VALUES(@Code,@Client,@Name,'',@Email,'[]',@Actor);SELECT LAST_INSERT_ID();", new { Code = "CAND" + token + suffix, Client = client, Name = "Synthetic " + suffix, Email = token + suffix + "@example.invalid", Actor = actor });
            var application = await db.ExecuteScalarAsync<long>("INSERT INTO recruitment_candidate_applications(ApplicationCode,CandidateId,PositionId,ClientId,CurrentStage,CurrentStatus) VALUES(@Code,@Candidate,@Position,@Client,'Offer Released','Offer Released');SELECT LAST_INSERT_ID();", new { Code = "APP" + token + suffix, Candidate = candidate, Position = position, Client = client });
            var offer = await db.ExecuteScalarAsync<long>("INSERT INTO recruitment_offers(OfferNumber,ApplicationId,ClientId,OfferedCtc,Currency,ProposedJoiningDate,Status,CreatedByUserId) VALUES(@Code,@Application,@Client,600000,'INR','2026-11-02',@Status,@Actor);SELECT LAST_INSERT_ID();", new { Code = "OFF" + token + suffix, Application = application, Client = client, Status = status, Actor = actor });
            return (candidate,application,offer);
        }
        var ready = await Add("ready"); var pending = await Add("pending"); var draft = await Add("draft", "Draft");
        var repository = new RecruitmentTalentRepository(config,null!,null!,null!,null!,null!,null!,null!,null!,new EmployeeRepository(config,null!,null!),null!,NullLogger<RecruitmentTalentRepository>.Instance);
        await repository.InitializeJourneySchemaAsync();
        var beforeMail = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM notification_queue");
        var selection = new RecruitmentJoiningSelection { ClientId = client, OfferIds = [ready.Offer,pending.Offer,draft.Offer] };
        var accepted = await repository.AcceptSelectedOffersAsync(selection, user);
        Assert.Equal(2, accepted.Accepted); Assert.Equal(1, accepted.Review); Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employees WHERE ClientId=@Client", new { Client = client }));
        Assert.Equal(2, (await repository.GetJoiningCandidatesAsync(client, user)).Count(row => row.AcceptanceRecorded));
        Assert.Equal(2, (await repository.AcceptSelectedOffersAsync(selection,user)).AlreadyAccepted);
        await db.ExecuteAsync("INSERT INTO recruitment_candidate_checklist_items(ApplicationId,CandidateId,ChecklistConfigurationId,ChecklistName,Mandatory,Status) VALUES(@Application,@Candidate,1,'Synthetic required document',TRUE,'Pending')", new { pending.Application, pending.Candidate });
        var imported = await repository.ImportSelectedJoiningCandidatesAsync(selection, user);
        Assert.Equal(1, imported.Imported); Assert.Equal(2, imported.Review);
        var employee = Assert.Single(imported.Items, row => row.Status == "Imported"); Assert.True(employee.EmployeeId > 0); Assert.Equal("CAND" + token + "ready", employee.EmployeeCode);
        Assert.Equal(1, (await repository.ImportSelectedJoiningCandidatesAsync(selection,user)).AlreadyImported);
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employees WHERE ClientId=@Client", new { Client = client }));
        Assert.Equal(location, await db.ExecuteScalarAsync<int>("SELECT WorkLocationId FROM employees WHERE Id=@Id", new { Id = employee.EmployeeId }));
        Assert.Equal(600000, await db.ExecuteScalarAsync<decimal>("SELECT AnnualCtc FROM employees WHERE Id=@Id", new { Id = employee.EmployeeId }));
        Assert.Equal(beforeMail, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM notification_queue"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => repository.GetJoiningCandidatesAsync(other,user));
        // A new rejected revision prevents the old accepted offer being used again.
        await db.ExecuteAsync("INSERT INTO recruitment_offers(OfferNumber,ApplicationId,ClientId,OfferedCtc,Currency,ProposedJoiningDate,Status,CreatedByUserId) VALUES(@Code,@Application,@Client,700000,'INR','2026-11-02','Rejected',@Actor)", new { Code = "REV" + token, Application = pending.Application, Client = client, Actor = actor });
        Assert.DoesNotContain(await repository.GetJoiningCandidatesAsync(client,user), row => row.OfferId == pending.Offer);
        var stale = await repository.ImportSelectedJoiningCandidatesAsync(new() { ClientId = client, OfferIds = [pending.Offer] }, user);
        Assert.Equal(1, stale.Review); Assert.Contains("superseded", stale.Items[0].Message);
    }
}
