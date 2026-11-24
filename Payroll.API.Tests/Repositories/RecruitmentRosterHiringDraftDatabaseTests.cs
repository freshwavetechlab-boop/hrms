using System.Text.Json;
using System.Diagnostics;
using Dapper;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Repositories;

public sealed class RecruitmentRosterHiringDraftDatabaseTests
{
    private sealed class LocalFactAttribute : FactAttribute
    {
        public LocalFactAttribute() { if (Environment.GetEnvironmentVariable("FREVO_OFFER_DELETION_TEST_DB") is null) Skip="Explicit task-owned localhost offer database required."; }
    }

    [LocalFact]
    public async Task Existing_simple_jds_become_real_draft_requests_and_full_jd_versions_per_client_and_location_without_publishing()
    {
        await using var f=await Fixture.CreateAsync();
        await f.Repository.InitializeHiringDraftsAsync();
        var requests=(await f.Db.QueryAsync<(long Id,int ClientId,int BranchId,string Status,string SourceType)>("SELECT Id,ClientId,BranchId,Status,SourceType FROM recruitment_requisitions ORDER BY ClientId,BranchId")).ToArray();
        Assert.Equal(3,requests.Length); Assert.Equal(new[]{(17,31),(17,32),(208,91)},requests.Select(r=>(r.ClientId,r.BranchId)));
        Assert.All(requests,r=>{Assert.Equal("Draft",r.Status);Assert.Equal("Employee List JD",r.SourceType);});
        Assert.Equal(3,await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_job_description_versions WHERE Status='Draft' AND VersionNumber=1 AND WorkflowInstanceId IS NULL"));
        var expected=RecruitmentSimpleJobDescription.Generate("Research Assistant");
        foreach(var request in requests)
        {
            var jd=await f.Db.QuerySingleAsync<(long Id,string Title,string Summary)>("SELECT Id,Title,Summary FROM recruitment_job_description_versions WHERE RequisitionId=@Id AND ClientId=@ClientId",new{request.Id,request.ClientId});
            Assert.Equal(expected.Title,jd.Title);Assert.Equal(expected.Summary,jd.Summary);
            Assert.Equal(expected.Responsibilities,await f.Db.QueryAsync<string>("SELECT ResponsibilityText FROM recruitment_jd_responsibilities WHERE JobDescriptionVersionId=@Id ORDER BY DisplayOrder",new{jd.Id}));
            Assert.Equal(expected.Skills,await f.Db.QueryAsync<string>("SELECT SkillName FROM recruitment_jd_skill_requirements WHERE JobDescriptionVersionId=@Id ORDER BY DisplayOrder",new{jd.Id}));
        }
        Assert.Equal(0,await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_open_positions"));
        Assert.Equal(0,await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_job_postings"));
        Assert.Equal(4,await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_document_roster"));
    }

    [LocalFact]
    public async Task Repeated_starts_reuse_the_same_drafts_and_retain_manual_edits_and_approvals()
    {
        await using var f=await Fixture.CreateAsync();await f.Repository.InitializeHiringDraftsAsync();
        await f.Db.ExecuteAsync("UPDATE recruitment_requisitions SET PositionTitle='Reviewed research role',Status='Approved' WHERE ClientId=17 AND BranchId=31");
        var before=await f.SnapshotAsync();await f.Repository.InitializeHiringDraftsAsync();
        Assert.Equal(before,await f.SnapshotAsync());
    }

    [LocalFact]
    public async Task Startup_does_not_resurrect_a_hiring_draft_that_was_explicitly_deleted()
    {
        await using var f=await Fixture.CreateAsync();await f.Repository.InitializeHiringDraftsAsync();
        var id=await f.Db.ExecuteScalarAsync<long>("SELECT RequisitionId FROM recruitment_roster_jd_hiring_drafts WHERE ClientId=17 AND WorkLocationKey=31");
        await f.Db.ExecuteAsync("DELETE FROM recruitment_job_description_versions WHERE RequisitionId=@Id;DELETE FROM recruitment_requisitions WHERE Id=@Id",new{Id=id});
        var before=await f.SnapshotAsync();await f.Repository.InitializeHiringDraftsAsync();
        Assert.Equal(before,await f.SnapshotAsync());
    }

    [LocalFact]
    public async Task Focused_migration_command_runs_idempotently_and_exits_without_starting_the_host()
    {
        await using var f=await Fixture.CreateAsync();
        await f.RunMigrationAsync();var before=await f.SnapshotAsync();
        Assert.Equal(3,await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_roster_jd_hiring_drafts"));
        await f.RunMigrationAsync();Assert.Equal(before,await f.SnapshotAsync());
    }

    [LocalFact]
    public async Task A_failed_draft_rolls_back_request_description_and_mapping_without_reusing_its_number()
    {
        await using var f=await Fixture.CreateAsync();
        await f.Db.ExecuteAsync("CREATE TRIGGER deny_simple_jd_skill BEFORE INSERT ON recruitment_jd_skill_requirements FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Synthetic JD failure'");
        await Assert.ThrowsAsync<MySqlException>(()=>f.Repository.InitializeHiringDraftsAsync());
        Assert.Equal(0,await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_requisitions"));
        Assert.Equal(0,await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_job_description_versions"));
        Assert.Equal(0,await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_roster_jd_hiring_drafts"));
        Assert.Equal(1,await f.Db.ExecuteScalarAsync<int>("SELECT LastNumber FROM recruitment_number_sequences WHERE ClientId=17"));
    }

    private sealed class Fixture(MySqlConnection admin,MySqlConnection db,string name,IConfiguration config):IAsyncDisposable
    {
        public MySqlConnection Db=>db;
        public RecruitmentDocumentRepository Repository{get;}=new(config,null!,null!);
        public async Task RunMigrationAsync()
        {
            var start=new ProcessStartInfo("dotnet"){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
            start.ArgumentList.Add(typeof(RecruitmentDocumentRepository).Assembly.Location);
            start.ArgumentList.Add("prod");start.ArgumentList.Add("--migrate-recruitment-roster-jds");
            start.Environment["ConnectionStrings__Default"]=config.GetConnectionString("Default");
            start.Environment["Database__AutoMigrate"]="false";start.Environment["OutboundDelivery__Suppressed"]="true";
            using var process=Process.Start(start)!;
            var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
            try{await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(45)).Token);}
            catch{process.Kill(entireProcessTree:true);throw;}
            var logs=await output+await error;
            Assert.True(process.ExitCode==0,logs);Assert.Contains("web host was not started",logs);
            Assert.DoesNotContain("Now listening",logs);
        }
        public async Task<string> SnapshotAsync()=>JsonSerializer.Serialize(new{requests=await db.QueryAsync("SELECT * FROM recruitment_requisitions ORDER BY Id"),jd=await db.QueryAsync("SELECT * FROM recruitment_job_description_versions ORDER BY Id"),links=await db.QueryAsync("SELECT * FROM recruitment_roster_jd_hiring_drafts ORDER BY RequisitionId"),numbers=await db.QueryAsync("SELECT * FROM recruitment_number_sequences ORDER BY ClientId")});
        public static async Task<Fixture> CreateAsync()
        {
            var settings=new MySqlConnectionStringBuilder(Environment.GetEnvironmentVariable("FREVO_OFFER_DELETION_TEST_DB")!);
            if(settings.Server!="127.0.0.1"||settings.Port!=33325||settings.Database!="offer_deletion_test")throw new InvalidOperationException("Only the task-owned localhost database is permitted.");
            settings.Database="";var admin=new MySqlConnection(settings.ConnectionString);await admin.OpenAsync();
            var name="roster_drafts_"+Guid.NewGuid().ToString("N");await admin.ExecuteAsync($"CREATE DATABASE `{name}`");
            settings.Database=name;var db=new MySqlConnection(settings.ConnectionString);await db.OpenAsync();
            var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["ConnectionStrings:Default"]=settings.ConnectionString}).Build();
            await db.ExecuteAsync("""
CREATE TABLE clients(Id INT PRIMARY KEY,Code VARCHAR(80)); INSERT INTO clients VALUES(17,'SYNTHETIC'),(208,'SECOND');
CREATE TABLE worklocations(Id INT PRIMARY KEY,ClientId INT,Name VARCHAR(180),City VARCHAR(120),State VARCHAR(120));
INSERT INTO worklocations VALUES(31,17,'Site A','Pune','Maharashtra'),(32,17,'Site B','Mumbai','Maharashtra'),(91,208,'Site C','Delhi','Delhi');
CREATE TABLE recruitment_candidates(Id BIGINT PRIMARY KEY,ClientId INT,ProfileStatus VARCHAR(40));INSERT INTO recruitment_candidates VALUES(11,17,'Active'),(21,208,'Active');
CREATE TABLE recruitment_document_roster(Id BIGINT PRIMARY KEY,ClientId INT,CandidateId BIGINT,WorkLocationId INT);
INSERT INTO recruitment_document_roster VALUES(1,17,11,31),(2,17,11,31),(3,17,11,32),(4,208,21,91);
CREATE TABLE recruitment_simple_job_descriptions(Id BIGINT PRIMARY KEY,ClientId INT,TitleKey CHAR(64),ContentJson JSON,CreatedByUserId INT);
CREATE TABLE recruitment_document_roster_jds(RosterId BIGINT PRIMARY KEY,ClientId INT,SimpleJdId BIGINT);
INSERT INTO recruitment_document_roster_jds VALUES(1,17,51),(2,17,51),(3,17,51),(4,208,52);
CREATE TABLE recruitment_number_sequences(ClientId INT,SeriesCode VARCHAR(30),LastNumber INT,PRIMARY KEY(ClientId,SeriesCode));
CREATE TABLE recruitment_requisitions(
Id BIGINT AUTO_INCREMENT PRIMARY KEY,RfrNumber VARCHAR(80) UNIQUE,RequestDate DATE,RequestedByEmployeeId INT,RequestedByUserId INT,RequesterUserId INT,ClientId INT,BranchId INT,
BusinessUnit VARCHAR(120),Department VARCHAR(120),CostCenter VARCHAR(120),PositionTitle VARCHAR(180),PositionCategory VARCHAR(120),EmploymentType VARCHAR(80),HiringType VARCHAR(80),NumberOfOpenings INT,
IsReplacement BOOL,ReplacementEmployeeId INT,TargetJoiningDate DATE,JobLocation VARCHAR(160),WorkMode VARCHAR(40),ClientProjectId INT,Project VARCHAR(160),BudgetAvailable BOOL,BudgetAmount DECIMAL(18,2),HiringPriority VARCHAR(40),
BusinessJustification TEXT,ReasonForHiring VARCHAR(500),ExperienceRange VARCHAR(80),Qualification TEXT,RequiredSkills TEXT,PreferredSkills TEXT,Certifications VARCHAR(500),Languages VARCHAR(250),SalaryMin DECIMAL(18,2),SalaryMax DECIMAL(18,2),Currency VARCHAR(10),Benefits TEXT,
ExternalPositionCode VARCHAR(120),SourceType VARCHAR(80),SourceReference VARCHAR(180),SourceDocumentName VARCHAR(255),SourceDocumentDate DATE,SourceAuthority VARCHAR(180),ExternalApprovalStatus VARCHAR(80),CtcFlexibilityPercent DECIMAL(7,2),NegotiationSlaExtensionMinutes INT,SourceNotes TEXT,SourceParsedJson JSON,WorkOrderId BIGINT,WorkOrderLineNumber INT,Status VARCHAR(40) DEFAULT 'Draft');
CREATE TABLE recruitment_job_description_versions(Id BIGINT AUTO_INCREMENT PRIMARY KEY,RequisitionId BIGINT,ClientId INT,VersionNumber INT,Title VARCHAR(180),Summary TEXT,RolePurpose TEXT,Status VARCHAR(40),CreatedByUserId INT,WorkflowInstanceId BIGINT NULL);
CREATE TABLE recruitment_jd_responsibilities(Id BIGINT AUTO_INCREMENT PRIMARY KEY,JobDescriptionVersionId BIGINT,ResponsibilityText TEXT,DisplayOrder INT);
CREATE TABLE recruitment_jd_skill_requirements(Id BIGINT AUTO_INCREMENT PRIMARY KEY,JobDescriptionVersionId BIGINT,SkillId BIGINT,SkillName VARCHAR(180),IsRequired BOOL,MinimumYears DECIMAL(5,2),MinimumProficiency VARCHAR(80),WeightPercent DECIMAL(5,2),DisplayOrder INT);
CREATE TABLE recruitment_open_positions(Id BIGINT PRIMARY KEY);CREATE TABLE recruitment_job_postings(Id BIGINT PRIMARY KEY);
""");
            var content=RecruitmentSimpleJobDescription.Generate("Research Assistant");
            foreach(var pair in new[]{(51,17),(52,208)})await db.ExecuteAsync("INSERT INTO recruitment_simple_job_descriptions VALUES(@Id,@ClientId,SHA2('RESEARCH ASSISTANT',256),@Content,81)",new{Id=pair.Item1,ClientId=pair.Item2,Content=JsonSerializer.Serialize(content,new JsonSerializerOptions(JsonSerializerDefaults.Web))});
            return new(admin,db,name,config);
        }
        public async ValueTask DisposeAsync(){await db.DisposeAsync();await admin.ExecuteAsync($"DROP DATABASE `{name}`");await admin.DisposeAsync();}
    }
}
