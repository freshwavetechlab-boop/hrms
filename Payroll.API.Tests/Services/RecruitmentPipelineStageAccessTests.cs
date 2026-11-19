using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;
using Xunit;

namespace Payroll.API.Tests.Services;

public sealed class RecruitmentPipelineStageAccessTests
{
    private static AuthUser User(int id = 101, int? client = 55, string role = "gad") => new() { Id = id, ClientId = client, IsActive = true, Roles = [role], Permissions = ["recruitment.manage"] };

    [Fact]
    public void OwnershipColourIsNotAnAccessGrantAndBroadRecruitmentPermissionDoesNotBypassRestriction()
    {
        var policy = new RecruitmentStageAccessPolicy { OwnerType = "GAD", DepartmentLabel = "Client admin", Restricted = true, AllowedRoleCodes = ["project_cell"] };
        var user = User();
        Assert.False(RecruitmentPipelineStageAccess.Allows(user, 55, policy));
        user.Roles = ["PROJECT_CELL"];
        Assert.True(RecruitmentPipelineStageAccess.Allows(user, 55, policy));
        user.Roles.Clear(); policy.AllowedUserIds = [user.Id];
        Assert.True(RecruitmentPipelineStageAccess.Allows(user, 55, policy));
        user.IsActive = false; Assert.False(RecruitmentPipelineStageAccess.Allows(user, 55, policy));
    }

    [Fact]
    public void AdminAndExplicitGrantsStillIntersectWithSelectedClients()
    {
        var admin = User(102, null); admin.Permissions = ["settings.manage"]; admin.ClientAccessMode = "Selected"; admin.AssignedClientIds = [55];
        Assert.True(RecruitmentPipelineStageAccess.Allows(admin, 55, new() { Restricted = true }));
        Assert.False(RecruitmentPipelineStageAccess.Allows(admin, 20, new() { AllowedUserIds = [102] }));
        admin.MustChangePassword = true; Assert.False(RecruitmentPipelineStageAccess.Allows(admin, 55, null));
        Assert.True(RecruitmentPipelineStageAccess.Allows(User(), 55, null));
        Assert.True(RecruitmentPipelineStageAccess.Allows(User(), 55, new() { OwnerType = "Client" }));
    }

    [Fact]
    public void OnlyRecognizedActiveInternalAutomationCanUseTheSystemPath()
    {
        Assert.False(RecruitmentPipelineStageAccess.Allows(new() { Id = 0, IsActive = true, DisplayName = "Untrusted" }, 55, new() { Restricted = true }));
        Assert.True(RecruitmentPipelineStageAccess.Allows(new() { Id = 0, IsActive = true, DisplayName = "Recruitment automation" }, 55, new() { Restricted = true }));
        Assert.True(RecruitmentPipelineStageAccess.Allows(new() { Id = 0, IsActive = true, DisplayName = "Public recruitment portal" }, 55, new() { Restricted = true }));
        Assert.False(RecruitmentPipelineStageAccess.Allows(new() { Id = 0, ClientId = 20, IsActive = true, DisplayName = "External candidate" }, 55, new() { Restricted = true }));
    }

    [Fact]
    public async Task RoleAndUserGrantsFilterApplicationsCasesAndDirectAttachmentsWithoutChangingRecords()
    {
        await using var f = await Fixture.CreateAsync();
        var gad = User(); var cell = User(102, 55, "project_cell");
        Assert.Equal(new[] { 41L }, (await RecruitmentPipelineStageAccess.AllowedApplicationIdsAsync(f.Db, [41, 42, 43], gad)).Order());
        Assert.Equal(new[] { 42L }, (await RecruitmentPipelineStageAccess.AllowedApplicationIdsAsync(f.Db, [41, 42, 43], cell)).Order());
        Assert.True(await RecruitmentPipelineStageAccess.AllowsCaseAsync(f.Db, 601, gad));
        Assert.False(await RecruitmentPipelineStageAccess.AllowsCaseAsync(f.Db, 602, gad));
        Assert.True(await AttachmentRepository.AllowsRecruitmentStageAttachmentAsync(f.Db, gad, "FORM_SUBMISSION", 1000));
        Assert.False(await AttachmentRepository.AllowsRecruitmentStageAttachmentAsync(f.Db, cell, "FORM_SUBMISSION", 1000));
        Assert.True(await AttachmentRepository.AllowsRecruitmentStageAttachmentAsync(f.Db, gad, "RECRUITMENT_PROCESS_DOCUMENT", 900));
        Assert.False(await AttachmentRepository.AllowsRecruitmentStageAttachmentAsync(f.Db, cell, "RECRUITMENT_PROCESS_DOCUMENT", 900));
        Assert.True(await RecruitmentPipelineStageAccess.AllowsCandidateAsync(f.Db, 9, gad));
        Assert.False(await RecruitmentPipelineStageAccess.AllowsCandidateAsync(f.Db, 9, cell));
        Assert.Equal(3, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_candidate_applications"));
    }

    [Fact]
    public async Task DirectUnassignedMovePauseHistoryAndStageActionsAreDeniedBeforeAnyWrite()
    {
        await using var f = await Fixture.CreateAsync();
        var cell = User(102, 55, "project_cell");
        var pipeline = new RecruitmentPipelineRepository(f.Config, new WorkflowRepository(f.Config));
        var move = await pipeline.RequestTransitionAsync(41, new() { TransitionId = 1 }, cell);
        Assert.Null(move.Result); Assert.Contains("assigned", move.Error);
        Assert.Empty(await pipeline.GetAvailableTransitionsAsync(41, cell));
        Assert.Empty(await pipeline.GetApplicationStageHistoryAsync(41, cell));
        Assert.Empty(await pipeline.GetStageActionsAsync(1, "OnEntry", cell));
        Assert.Null((await pipeline.PauseStageAsync(41, new() { Reason = "Fixture reason" }, cell)).Row);
        Assert.Null((await pipeline.ResumeStageAsync(41, cell)).Row);
        var cases = new RecruitmentCaseRepository(f.Config, null!, null!, new WorkflowRepository(f.Config));
        Assert.Empty(await cases.GetAvailableHiringCaseTransitionsAsync(601, cell));
        Assert.Null((await cases.PauseHiringCaseAsync(601, new() { Reason = "Fixture reason" }, cell)).Row);
        Assert.Null((await cases.ResumeHiringCaseAsync(601, cell)).Row);
        var approval = new WorkflowInstance { ResourceType = "RecruitmentPipelineTransition", ResourceId = "HIRING_CASE:9001" };
        Assert.False(await pipeline.CanActOnStageWorkflowAsync(approval, cell));
        Assert.True(await pipeline.CanActOnStageWorkflowAsync(approval, User()));
        Assert.Equal(501, await f.Db.ExecuteScalarAsync<int>("SELECT CurrentStageInstanceId FROM recruitment_application_pipeline_instances WHERE ApplicationId=41"));
    }

    [Fact]
    public async Task VersionPolicyRoundTripsAndNonAdminCannotRemoveOrEscalateRestrictions()
    {
        await using var f = await Fixture.CreateAsync();
        var before = await RecruitmentPipelineStageAccess.ReadAsync(f.Db, 55, 10);
        var admin = User(); admin.Permissions = ["settings.manage"];
        var stages = new List<RecruitmentPipelineStage> { new() { StageCode = "GAD", AccessPolicy = before["GAD"] }, new() { StageCode = "CLIENT", AccessPolicy = before["CLIENT"] } };
        Assert.Equal("", await RecruitmentPipelineStageAccess.ValidateSaveAsync(f.Db, 55, 10, stages, admin));
        Assert.NotEmpty(await RecruitmentPipelineStageAccess.ValidateSaveAsync(f.Db, 55, 10, [new() { StageCode = "GAD" }], User()));
        await using (var tx = await f.Db.BeginTransactionAsync()) { await RecruitmentPipelineStageAccess.SaveAsync(f.Db, tx, 55, 11, stages); await tx.CommitAsync(); }
        var copied = await RecruitmentPipelineStageAccess.ReadAsync(f.Db, 55, 11);
        Assert.True(copied["GAD"].Restricted); Assert.Equal("gad", Assert.Single(copied["GAD"].AllowedRoleCodes));
        Assert.Equal(2, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM modulesettings WHERE ModuleCode LIKE 'recruitment_pipeline_access_%'"));
        Assert.Equal(10, await f.Db.ExecuteScalarAsync<int>("SELECT CurrentPublishedVersionId FROM recruitment_pipeline_definitions WHERE Id=100"));
    }

    private sealed class Fixture(MySqlConnection root, MySqlConnection db, string name, IConfiguration config) : IAsyncDisposable
    {
        internal MySqlConnection Db => db;
        internal IConfiguration Config => config;
        internal static async Task<Fixture> CreateAsync()
        {
            var connection = Environment.GetEnvironmentVariable("FREVO_RECRUITMENT_STAGE_ACCESS_TEST_DB") ?? Environment.GetEnvironmentVariable("FREVO_ICMR_APPROVAL_TEST_DB") ?? throw new InvalidOperationException("Set an owned loopback MySQL test fixture.");
            var target = new MySqlConnectionStringBuilder(connection);
            Assert.Contains(target.Server, new[] { "127.0.0.1", "localhost" }); Assert.Equal(33330u, target.Port);
            target.Database = ""; var root = new MySqlConnection(target.ConnectionString); await root.OpenAsync();
            var name = "pipeline_access_test_" + Guid.NewGuid().ToString("N");
            await root.ExecuteAsync($"CREATE DATABASE `{name}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci");
            target.Database = name; var db = new MySqlConnection(target.ConnectionString); await db.OpenAsync();
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = target.ConnectionString }).Build();
            await db.ExecuteAsync("""
CREATE TABLE modulesettings(client_id INT,ModuleCode VARCHAR(100) UNIQUE,IsEnabled BOOLEAN,SettingsJson JSON);
CREATE TABLE recruitment_pipeline_definitions(Id BIGINT,ClientId INT,CurrentPublishedVersionId BIGINT); INSERT INTO recruitment_pipeline_definitions VALUES(100,55,10),(200,20,20);
CREATE TABLE recruitment_pipeline_versions(Id BIGINT,PipelineDefinitionId BIGINT); INSERT INTO recruitment_pipeline_versions VALUES(10,100),(20,200);
CREATE TABLE recruitment_pipeline_stages(Id BIGINT,PipelineVersionId BIGINT,StageCode VARCHAR(80)); INSERT INTO recruitment_pipeline_stages VALUES(1,10,'GAD'),(2,10,'CLIENT'),(3,20,'FOREIGN');
CREATE TABLE recruitment_candidate_applications(Id BIGINT,ClientId INT,CandidateId BIGINT,PositionId BIGINT,JobPostingId BIGINT); INSERT INTO recruitment_candidate_applications VALUES(41,55,9,1,NULL),(42,55,10,1,NULL),(43,20,11,2,NULL);
CREATE TABLE recruitment_application_pipeline_instances(Id BIGINT,ApplicationId BIGINT,CurrentStageInstanceId BIGINT); INSERT INTO recruitment_application_pipeline_instances VALUES(401,41,501),(402,42,502),(403,43,503);
CREATE TABLE recruitment_application_stage_instances(Id BIGINT,PipelineStageId BIGINT); INSERT INTO recruitment_application_stage_instances VALUES(501,1),(502,2),(503,3);
CREATE TABLE recruitment_position_pipeline_instances(Id BIGINT,ClientId INT,CurrentStageInstanceId BIGINT); INSERT INTO recruitment_position_pipeline_instances VALUES(601,55,701),(602,55,702);
CREATE TABLE recruitment_position_stage_instances(Id BIGINT,PipelineStageId BIGINT); INSERT INTO recruitment_position_stage_instances VALUES(701,1),(702,2);
CREATE TABLE recruitment_hiring_case_advance_requests(Id BIGINT,HiringCaseId BIGINT); INSERT INTO recruitment_hiring_case_advance_requests VALUES(9001,601);
CREATE TABLE recruitment_process_documents(Id BIGINT,ApplicationId BIGINT,HiringCaseId BIGINT,PipelineStageId BIGINT); INSERT INTO recruitment_process_documents VALUES(900,41,601,1),(901,42,602,2);
CREATE TABLE form_submissions(Id BIGINT,ApplicationId BIGINT); INSERT INTO form_submissions VALUES(1000,41);
CREATE TABLE authroles(Code VARCHAR(100)); INSERT INTO authroles VALUES('gad'),('project_cell');
CREATE TABLE authusers(Id INT,ClientId INT,IsActive BOOLEAN); INSERT INTO authusers VALUES(101,55,TRUE),(102,55,TRUE);
""");
            var policies = new Dictionary<string, RecruitmentStageAccessPolicy> { ["GAD"] = new() { OwnerType = "GAD", Restricted = true, AllowedRoleCodes = ["gad"] }, ["CLIENT"] = new() { OwnerType = "Client", Restricted = true, AllowedRoleCodes = ["project_cell"] } };
            await db.ExecuteAsync("INSERT INTO modulesettings VALUES(55,@Key,TRUE,@Json)", new { Key = RecruitmentPipelineStageAccess.Key(10), Json = JsonSerializer.Serialize(policies, new JsonSerializerOptions(JsonSerializerDefaults.Web)) });
            return new(root, db, name, config);
        }
        public async ValueTask DisposeAsync()
        {
            await db.DisposeAsync();
            if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^pipeline_access_test_[a-f0-9]{32}$")) throw new InvalidOperationException("Unexpected test database name.");
            await root.ExecuteAsync($"DROP DATABASE `{name}`"); await root.DisposeAsync();
        }
    }
}
