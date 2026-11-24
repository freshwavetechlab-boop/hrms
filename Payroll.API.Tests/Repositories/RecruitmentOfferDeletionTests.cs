using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Repositories;

public sealed class RecruitmentOfferDeletionTests
{
    private static AuthUser Superadmin => new() { Id = 81, IsActive = true, Roles = ["super_admin"] };
    private sealed class LocalFactAttribute : FactAttribute
    {
        public LocalFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("FREVO_OFFER_DELETION_TEST_DB") is null)
                Skip = "Explicit task-owned localhost offer deletion database required.";
        }
    }

    [Fact]
    public async Task Only_unrestricted_superadmins_can_delete_even_when_ordinary_admin_permissions_are_granted()
    {
        Assert.True(RecruitmentPermissions.CanHardDeleteOffers(Superadmin));
        Assert.True(RecruitmentPermissions.CanHardDeleteOffers(new() { Id = 1, IsActive = true, Roles = ["SUPER_ADMIN"] }));
        var config = new ConfigurationBuilder().Build();
        var roster = new RecruitmentDocumentRepository(config, null!, null!);
        foreach (var user in new AuthUser[] {
            new() { Id=1,IsActive=true,Permissions=["settings.manage","recruitment.manage"] },
            new() { Id=1,IsActive=true,Roles=["super_admin"],ClientId=17 },
            new() { Id=1,IsActive=true,Roles=["super_admin"],ClientAccessMode="Selected",AssignedClientIds=[17] },
            new() { Id=1,IsActive=false,Roles=["super_admin"] },
            new() { Id=1,IsActive=true,MustChangePassword=true,Roles=["super_admin"] },
            new() { IsActive=true,Roles=["super_admin"] } })
        {
            Assert.False(RecruitmentPermissions.CanHardDeleteOffers(user));
            // Authorization must fail before opening any database connection.
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => roster.HardDeleteRowAsync(17, 1, user));
        }
    }

    [LocalFact]
    public async Task Hard_delete_removes_only_the_selected_offer_inputs_releases_and_jd_link_and_keeps_the_person_and_other_batches()
    {
        await using var f = await Fixture.CreateAsync();
        var other = await f.UnrelatedAsync();
        Assert.True(await f.Repository.HardDeleteRowAsync(17, 701, Superadmin));
        Assert.Equal(0, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_document_roster WHERE Id=701"));
        Assert.Equal(0, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_document_roster_releases WHERE RosterId=701"));
        Assert.Equal(0, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_document_roster_jds WHERE RosterId=701"));
        var audit = (await f.Db.QueryAsync<(string Action, string Payload)>("SELECT Action,CAST(NewValuesJson AS CHAR) Payload FROM recruitment_document_roster_audit WHERE RosterId=701")).Single();
        Assert.Equal("Hard delete", audit.Action); Assert.DoesNotContain("deleted input", audit.Payload);
        Assert.Equal(other, await f.UnrelatedAsync());
        Assert.False(await f.Repository.HardDeleteRowAsync(17, 701, Superadmin));
        Assert.Equal(1, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_document_roster_audit WHERE RosterId=701"));
    }

    [LocalFact]
    public async Task Wrong_client_and_inflight_delivery_cannot_partially_delete_the_offer()
    {
        await using var f = await Fixture.CreateAsync();
        var before = await f.AllAsync();
        Assert.False(await f.Repository.HardDeleteRowAsync(208, 701, Superadmin));
        Assert.Equal(before, await f.AllAsync());
        await f.Db.ExecuteAsync("UPDATE notification_queue SET Status='Processing' WHERE Id=801");
        before = await f.AllAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Repository.HardDeleteRowAsync(17, 701, Superadmin));
        Assert.Contains("currently being sent", error.Message); Assert.Equal(before, await f.AllAsync());
    }

    [LocalFact]
    public async Task Pending_mail_is_cancelled_and_sole_open_upload_link_expires_but_completed_submission_and_sent_receipts_survive()
    {
        await using var f = await Fixture.CreateAsync();
        Assert.True(await f.Repository.HardDeleteRowAsync(17, 701, Superadmin));
        Assert.Equal("Cancelled", await f.Db.ExecuteScalarAsync<string>("SELECT Status FROM notification_queue WHERE Id=801"));
        Assert.Equal("Sent", await f.Db.ExecuteScalarAsync<string>("SELECT Status FROM notification_queue WHERE Id=802"));
        Assert.Null(await f.Db.ExecuteScalarAsync<long?>("SELECT RosterId FROM recruitment_document_collection_requests WHERE Id=901"));
        Assert.True(await f.Db.ExecuteScalarAsync<bool>("SELECT ExpiresAtUtc<=UTC_TIMESTAMP(6) FROM recruitment_document_collection_requests WHERE Id=901"));
        Assert.Equal(2, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_document_mail_deliveries WHERE RosterId=701"));
        await f.Db.ExecuteAsync("UPDATE recruitment_document_collection_requests SET Status='Completed',RosterId=702,ExpiresAtUtc='2099-01-01' WHERE Id=901");
        Assert.True(await f.Repository.HardDeleteRowAsync(17, 702, Superadmin));
        Assert.Equal("Completed", await f.Db.ExecuteScalarAsync<string>("SELECT Status FROM recruitment_document_collection_requests WHERE Id=901"));
        Assert.Equal(1, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_document_collection_files"));
    }

    [LocalFact]
    public async Task Database_failure_rolls_back_the_row_pdf_mail_and_document_request_changes()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Db.ExecuteAsync("CREATE TRIGGER deny_offer_delete BEFORE DELETE ON recruitment_document_roster FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Synthetic rollback test'");
        var before = await f.AllAsync();
        await Assert.ThrowsAsync<MySqlException>(() => f.Repository.HardDeleteRowAsync(17, 701, Superadmin));
        Assert.Equal(before, await f.AllAsync());
    }

    private sealed class Fixture(MySqlConnection admin, MySqlConnection db, string name, IConfiguration config) : IAsyncDisposable
    {
        public MySqlConnection Db => db;
        public RecruitmentDocumentRepository Repository { get; } = new(config, null!, null!);
        public async Task<string> UnrelatedAsync() => JsonSerializer.Serialize(new {
            people=await db.QueryAsync("SELECT * FROM employees"), candidates=await db.QueryAsync("SELECT * FROM recruitment_candidates"),
            templates=await db.QueryAsync("SELECT * FROM recruitment_templates"), jds=await db.QueryAsync("SELECT * FROM recruitment_simple_job_descriptions"),
            rows=await db.QueryAsync("SELECT * FROM recruitment_document_roster WHERE Id<>701 ORDER BY Id"),
            releases=await db.QueryAsync("SELECT * FROM recruitment_document_roster_releases WHERE RosterId<>701 ORDER BY Id") });
        public async Task<string> AllAsync() => JsonSerializer.Serialize(new { other=await UnrelatedAsync(),
            rows=await db.QueryAsync("SELECT * FROM recruitment_document_roster ORDER BY Id"), releases=await db.QueryAsync("SELECT * FROM recruitment_document_roster_releases ORDER BY Id"),
            links=await db.QueryAsync("SELECT * FROM recruitment_document_roster_jds ORDER BY RosterId"), audits=await db.QueryAsync("SELECT * FROM recruitment_document_roster_audit ORDER BY Id"),
            queue=await db.QueryAsync("SELECT * FROM notification_queue ORDER BY Id"), requests=await db.QueryAsync("SELECT * FROM recruitment_document_collection_requests ORDER BY Id") });
        public static async Task<Fixture> CreateAsync()
        {
            var settings = new MySqlConnectionStringBuilder(Environment.GetEnvironmentVariable("FREVO_OFFER_DELETION_TEST_DB")!);
            if (settings.Server != "127.0.0.1" || settings.Port != 33325 || settings.Database != "offer_deletion_test")
                throw new InvalidOperationException("Only the task-owned localhost offer deletion database is permitted.");
            settings.Database=""; var admin=new MySqlConnection(settings.ConnectionString); await admin.OpenAsync();
            var name="offer_delete_"+Guid.NewGuid().ToString("N"); await admin.ExecuteAsync($"CREATE DATABASE `{name}`");
            settings.Database=name; var db=new MySqlConnection(settings.ConnectionString); await db.OpenAsync();
            var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["ConnectionStrings:Default"]=settings.ConnectionString }).Build();
            await db.ExecuteAsync("""
CREATE TABLE clients(Id INT PRIMARY KEY); INSERT INTO clients VALUES(17),(208);
CREATE TABLE employees(Id INT PRIMARY KEY,SalaryJson JSON); INSERT INTO employees VALUES(91,'{"BASIC":28000}');
CREATE TABLE recruitment_candidates(Id BIGINT PRIMARY KEY,EmployeeId INT); INSERT INTO recruitment_candidates VALUES(11,91);
CREATE TABLE recruitment_templates(Id INT PRIMARY KEY); INSERT INTO recruitment_templates VALUES(41);
CREATE TABLE recruitment_simple_job_descriptions(Id BIGINT PRIMARY KEY); INSERT INTO recruitment_simple_job_descriptions VALUES(51);
CREATE TABLE recruitment_document_roster(Id BIGINT PRIMARY KEY,ClientId INT,CandidateId BIGINT,EmployeeId INT,ValuesJson JSON);
INSERT INTO recruitment_document_roster VALUES(701,17,11,91,'{"name":"deleted input"}'),(702,17,11,91,'{"name":"same person other batch"}'),(703,208,11,NULL,'{}');
CREATE TABLE recruitment_document_roster_releases(Id BIGINT PRIMARY KEY,RosterId BIGINT,ClientId INT,PdfBytes BLOB,FOREIGN KEY(RosterId) REFERENCES recruitment_document_roster(Id));
INSERT INTO recruitment_document_roster_releases VALUES(711,701,17,X'010203'),(712,701,17,X'040506'),(713,702,17,X'070809');
CREATE TABLE recruitment_document_roster_jds(RosterId BIGINT PRIMARY KEY,ClientId INT,SimpleJdId BIGINT);
INSERT INTO recruitment_document_roster_jds VALUES(701,17,51),(702,17,51);
CREATE TABLE recruitment_document_roster_audit(Id BIGINT AUTO_INCREMENT PRIMARY KEY,RosterId BIGINT,ClientId INT,Action VARCHAR(40),NewValuesJson JSON,ActorUserId INT);
INSERT INTO recruitment_document_roster_audit(RosterId,ClientId,Action,NewValuesJson,ActorUserId) VALUES(701,17,'Import','{"name":"deleted input"}',1);
CREATE TABLE notification_queue(Id BIGINT PRIMARY KEY,ClientId INT,Status VARCHAR(30),ErrorMessage TEXT,EventCode VARCHAR(120),ResourceType VARCHAR(100));
INSERT INTO notification_queue VALUES(801,17,'RosterOfferPending','','RECRUITMENT_ROSTER.OFFER_SEND','RecruitmentRosterOffer'),(802,17,'Sent','','RECRUITMENT_ROSTER.OFFER_SEND','RecruitmentRosterOffer');
CREATE TABLE recruitment_document_mail_deliveries(Id BIGINT PRIMARY KEY,ClientId INT,RosterId BIGINT,QueueId BIGINT);
INSERT INTO recruitment_document_mail_deliveries VALUES(811,17,701,801),(812,17,701,802);
CREATE TABLE recruitment_document_collection_requests(Id BIGINT PRIMARY KEY,ClientId INT,RosterId BIGINT,OfferId BIGINT,ApplicationId BIGINT,Status VARCHAR(30),ExpiresAtUtc DATETIME(6),UpdatedAtUtc DATETIME(6));
INSERT INTO recruitment_document_collection_requests VALUES(901,17,701,NULL,NULL,'Open','2099-01-01',UTC_TIMESTAMP(6));
CREATE TABLE recruitment_document_collection_files(Id BIGINT PRIMARY KEY); INSERT INTO recruitment_document_collection_files VALUES(911);
""");
            return new(admin,db,name,config);
        }
        public async ValueTask DisposeAsync() { await db.DisposeAsync(); await admin.ExecuteAsync($"DROP DATABASE `{name}`"); await admin.DisposeAsync(); }
    }
}
