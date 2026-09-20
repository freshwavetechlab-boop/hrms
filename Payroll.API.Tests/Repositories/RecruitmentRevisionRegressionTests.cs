using Dapper;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Repositories;

public sealed class RecruitmentRevisionRegressionTests
{
    [Theory]
    [InlineData("Scored", true, false, false, true)]
    [InlineData("Scored", false, false, false, false)]
    [InlineData("NeedsReview", false, false, false, true)]
    [InlineData("NeedsReview", false, true, false, true)]
    [InlineData("NeedsReview", true, false, true, false)]
    [InlineData("Scored", true, true, false, false)]
    public void New_confirmation_policy_does_not_erase_missing_evidence(string status, bool confirmation, bool confirmed, bool overridden, bool review) =>
        Assert.Equal(review, RecruitmentPipelineRepository.AtsNeedsHumanReview(status, confirmation, confirmed, overridden));

    [LocalInterviewDatabaseFact]
    public async Task Revision_reuses_assignments_runtime_and_public_links_without_rewriting_completed_history()
    {
        await using var fixture = await Database.CreateAsync(); var db = fixture.Db;
        await db.ExecuteAsync(RevisionSchema);
        await db.ExecuteAsync("""
INSERT INTO recruitment_pipeline_definitions VALUES(1,20,2,TRUE),(2,30,4,TRUE);
INSERT INTO recruitment_pipeline_versions VALUES(1,1),(2,1),(3,2),(4,2);
INSERT INTO recruitment_pipeline_stages(Id,PipelineVersionId,StageCode,StageType,CardScope,StageName,DisplayOrder,IsActive,IsTerminal) VALUES
(11,1,'ATS','ATS','Application','ATS old',1,TRUE,FALSE),(12,1,'INTERVIEW','Interview','Application','Interview old',2,TRUE,FALSE),
(21,2,'ATS','ATS','Application','ATS updated',1,TRUE,FALSE),(22,2,'INTERVIEW','Interview','Application','Interview updated',2,TRUE,FALSE),
(13,1,'ORDER','Screening','Position','Order',1,TRUE,FALSE),(14,1,'MOM','Approval','Position','MOM old',2,TRUE,FALSE),
(23,2,'ORDER','Screening','Position','Order',1,TRUE,FALSE),(24,2,'MOM','Approval','Position','MOM updated',2,TRUE,FALSE),
(31,3,'ATS','ATS','Application','Other client',1,TRUE,FALSE),(41,4,'ATS','ATS','Application','Other new',1,TRUE,FALSE);
INSERT INTO recruitment_position_pipeline_assignments(PositionId,JobPostingId,PipelineVersionId,IsActive,AssignedByUserId) VALUES(10,NULL,1,TRUE,1),(10,7,1,TRUE,1),(30,NULL,3,TRUE,1);
INSERT INTO recruitment_job_postings VALUES(7,10,'unchanged-public-link');
INSERT INTO recruitment_candidate_applications VALUES(100,10,7,'ATS old',NULL),(101,10,7,'Completed',NULL),(300,30,NULL,'Other client',NULL);
INSERT INTO recruitment_application_pipeline_instances VALUES(1,100,1,1,51,'Active'),(2,101,1,1,52,'Completed'),(3,300,3,3,53,'Active');
INSERT INTO recruitment_application_stage_instances VALUES(50,1,100,12,'Completed','2026-09-01','2026-09-02','2026-09-02',0),
(51,1,100,11,'Active','2026-09-02','2026-09-05',NULL,123),
(52,2,101,12,'Completed','2026-09-02','2026-09-05','2026-09-03',0),(53,3,300,31,'Active','2026-09-02','2026-09-05',NULL,0);
INSERT INTO recruitment_position_pipeline_instances(Id,ClientId,PipelineVersionId,CurrentStageInstanceId,Status,SlaAnchorAtUtc) VALUES(5,20,1,61,'Active','2026-09-01');
INSERT INTO recruitment_position_stage_instances(PositionPipelineInstanceId,Id,PipelineStageId,Status,EnteredAtUtc,DueAtUtc) VALUES(5,61,13,'Active','2026-09-01','2026-09-03'),(5,62,14,'Pending',NULL,'2026-09-05');
INSERT INTO recruitment_pipeline_stage_actions VALUES(71,11,'OnEntry','RUN_ATS_SCORE',1,TRUE,TRUE),(72,21,'OnEntry','RUN_ATS_SCORE',1,TRUE,TRUE);
INSERT INTO recruitment_stage_ats_configurations VALUES(11,5),(21,5);
INSERT INTO recruitment_stage_action_executions(ApplicationId,StageInstanceId,StageActionId,TriggerEvent,ActionCode,Status,IsBlocking,ApplicationScoreId,StartedAtUtc,CompletedAtUtc)
VALUES(100,51,71,'OnEntry','RUN_ATS_SCORE','Completed',TRUE,132,'2026-09-02','2026-09-02');
""");
        await using (var tx = await db.BeginTransactionAsync())
        {
            await RecruitmentPipelineRepository.PromotePipelineAssignmentsAsync(db, tx, 1, 2, 1);
            await RecruitmentPipelineRepository.PromotePipelineAssignmentsAsync(db, tx, 1, 2, 1);
            await tx.CommitAsync();
        }
        Assert.Equal(2, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_position_pipeline_assignments WHERE PipelineVersionId=2 AND IsActive=TRUE"));
        Assert.Equal(2, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_position_pipeline_assignments WHERE PipelineVersionId=1 AND IsActive=FALSE"));
        Assert.Equal("unchanged-public-link", await db.ExecuteScalarAsync<string>("SELECT PublicSlug FROM recruitment_job_postings WHERE Id=7"));
        var repository = new RecruitmentPipelineRepository(fixture.Config, new WorkflowRepository(fixture.Config));
        var result = await repository.SynchronizePublishedRevisionsAsync(new AuthUser { Id = 1, ClientId = 20 });
        Assert.Empty(result.Waiting); Assert.Equal(1, result.ApplicationsUpdated); Assert.Equal(1, result.HiringJourneysUpdated);
        Assert.Equal(2, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_stage_action_executions WHERE ApplicationScoreId=132 AND Status='Completed'"));
        Assert.Equal(72, await db.ExecuteScalarAsync<int>("SELECT StageActionId FROM recruitment_stage_action_executions WHERE StageActionId=72"));
        Assert.Equal(21, await db.ExecuteScalarAsync<int>("SELECT PipelineStageId FROM recruitment_application_stage_instances WHERE Id=51"));
        Assert.Equal(123, await db.ExecuteScalarAsync<int>("SELECT PausedDurationSeconds FROM recruitment_application_stage_instances WHERE Id=51"));
        Assert.Equal(new DateTime(2026, 9, 2), await db.ExecuteScalarAsync<DateTime>("SELECT EnteredAtUtc FROM recruitment_application_stage_instances WHERE Id=51"));
        Assert.Equal(12, await db.ExecuteScalarAsync<int>("SELECT PipelineStageId FROM recruitment_application_stage_instances WHERE Id=50"));
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT PipelineVersionId FROM recruitment_application_pipeline_instances WHERE Id=2"));
        Assert.Equal(3, await db.ExecuteScalarAsync<int>("SELECT PipelineVersionId FROM recruitment_application_pipeline_instances WHERE Id=3"));
        Assert.Equal("Superseded", await db.ExecuteScalarAsync<string>("SELECT Status FROM recruitment_position_stage_instances WHERE Id=62"));
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_position_stage_instances WHERE PipelineStageId=24 AND Status='Pending'"));
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_stage_events WHERE EventType='RevisionApplied'"));
        result = await repository.SynchronizePublishedRevisionsAsync(new AuthUser { Id = 1, ClientId = 20 });
        Assert.Equal(0, result.ApplicationsUpdated); Assert.Equal(0, result.HiringJourneysUpdated);
    }

    [LocalInterviewDatabaseFact]
    public async Task Current_approval_is_retained_and_removed_stage_is_not_guessed()
    {
        await using var fixture = await Database.CreateAsync(); var db = fixture.Db;
        await db.ExecuteAsync(RevisionSchema);
        await db.ExecuteAsync("""
INSERT INTO recruitment_pipeline_definitions VALUES(1,20,2,TRUE);
INSERT INTO recruitment_pipeline_versions VALUES(1,1),(2,1);
INSERT INTO recruitment_pipeline_stages(Id,PipelineVersionId,StageCode,StageType,CardScope,StageName,IsActive,IsTerminal) VALUES
(11,1,'ATS','ATS','Application','ATS',TRUE,FALSE),(21,2,'ATS','ATS','Application','ATS',TRUE,FALSE),
(12,1,'REMOVED','Screening','Application','Removed stage',TRUE,FALSE);
INSERT INTO recruitment_candidate_applications VALUES(100,10,NULL,'ATS',NULL),(101,10,NULL,'Removed',NULL);
INSERT INTO recruitment_application_pipeline_instances VALUES(1,100,1,NULL,51,'Active'),(2,101,1,NULL,52,'Active');
INSERT INTO recruitment_application_stage_instances VALUES(51,1,100,11,'Active','2026-09-01',NULL,NULL,0),(52,2,101,12,'Active','2026-09-01',NULL,NULL,0);
INSERT INTO recruitment_pipeline_transition_requests VALUES(51,'Pending Approval');
""");
        var repository = new RecruitmentPipelineRepository(fixture.Config, new WorkflowRepository(fixture.Config));
        var user = new AuthUser { Id = 1, ClientId = 20 };
        var result = await repository.SynchronizePublishedRevisionsAsync(user);
        Assert.Equal(2, result.Waiting.Count); Assert.Equal(0, result.ApplicationsUpdated);
        await repository.SynchronizePublishedRevisionsAsync(user);
        Assert.Equal(2, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_stage_events WHERE EventType='RevisionWaiting'"));
        await db.ExecuteAsync("UPDATE recruitment_pipeline_transition_requests SET Status='Approved'");
        result = await repository.SynchronizePublishedRevisionsAsync(user);
        Assert.Equal(1, result.ApplicationsUpdated); Assert.Single(result.Waiting);
        Assert.Equal("Approved", await db.ExecuteScalarAsync<string>("SELECT Status FROM recruitment_pipeline_transition_requests"));
        // A deferred first page cannot starve a later compatible application.
        await db.ExecuteAsync("UPDATE recruitment_application_pipeline_instances SET PipelineVersionId=1; UPDATE recruitment_application_stage_instances SET PipelineStageId=11; UPDATE recruitment_pipeline_transition_requests SET Status='Pending Approval'");
        var worker = new AuthUser { Id = 0 };
        Assert.Single((await repository.SynchronizePublishedRevisionsAsync(worker, limit: 1)).Waiting);
        Assert.Equal(1, (await repository.SynchronizePublishedRevisionsAsync(worker, limit: 1)).ApplicationsUpdated);
        Assert.Equal(1, await db.ExecuteScalarAsync<long>("SELECT PipelineVersionId FROM recruitment_application_pipeline_instances WHERE Id=1"));
    }

    [LocalInterviewDatabaseFact]
    public async Task Relink_reuses_one_journey_and_concurrent_start_cannot_create_another()
    {
        await using var fixture = await Database.CreateAsync(); var db = fixture.Db;
        await db.ExecuteAsync(RevisionSchema);
        await db.ExecuteAsync("""
CREATE TABLE recruitment_requisitions(Id BIGINT PRIMARY KEY,ClientId INT);
CREATE TABLE recruitment_work_order_lines(Id BIGINT PRIMARY KEY,WorkOrderId BIGINT,RequisitionId BIGINT NULL,PositionId BIGINT NULL,Status VARCHAR(40));
INSERT INTO recruitment_requisitions VALUES(1,20),(2,20);
INSERT INTO recruitment_work_order_lines VALUES(1,1,NULL,NULL,'Open'),(2,1,1,10,'Open'),(3,1,2,11,'Open');
INSERT INTO recruitment_position_pipeline_instances(Id,ClientId,RequisitionId,WorkOrderId,WorkOrderLineId,PipelineVersionId,CurrentStageInstanceId,Status,SlaAnchorAtUtc)
VALUES(1,20,1,1,1,1,50,'Active','2026-09-01');
""");
        await using (var tx = await db.BeginTransactionAsync())
        {
            var reused = await RecruitmentCaseRepository.ReuseHiringJourneyAsync(db, tx, 1, 1, 2, 10, 20, 1);
            Assert.Empty(reused.Error); Assert.Equal(1, reused.Id);
            await tx.CommitAsync();
        }
        Assert.Equal("Superseded", await db.ExecuteScalarAsync<string>("SELECT Status FROM recruitment_work_order_lines WHERE Id=1"));
        Assert.Equal(new DateTime(2026, 9, 1), await db.ExecuteScalarAsync<DateTime>("SELECT SlaAnchorAtUtc FROM recruitment_position_pipeline_instances WHERE Id=1"));
        await using (var tx = await db.BeginTransactionAsync())
        {
            var wrongClient = await RecruitmentCaseRepository.ReuseHiringJourneyAsync(db, tx, 1, 1, 2, 10, 30, 1);
            Assert.Null(wrongClient.Id); Assert.NotEmpty(wrongClient.Error);
        }
        async Task<long> Start()
        {
            await using var connection = new MySqlConnection(fixture.Connection); await connection.OpenAsync();
            await using var tx = await connection.BeginTransactionAsync();
            var reused = await RecruitmentCaseRepository.ReuseHiringJourneyAsync(connection, tx, 2, 1, 3, 11, 20, 1);
            Assert.Empty(reused.Error);
            var id = reused.Id ?? await connection.ExecuteScalarAsync<long>("INSERT INTO recruitment_position_pipeline_instances(ClientId,RequisitionId,WorkOrderId,WorkOrderLineId,PositionId,PipelineVersionId,Status) VALUES(20,2,1,3,11,1,'Active'); SELECT LAST_INSERT_ID();", transaction: tx);
            await tx.CommitAsync(); return id;
        }
        var ids = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Start()));
        Assert.Single(ids.Distinct());
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_position_pipeline_instances WHERE RequisitionId=2"));
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_position_stage_events WHERE EventType='CaseRelinked'"));
    }

    private const string RevisionSchema = """
CREATE TABLE recruitment_pipeline_definitions(Id BIGINT PRIMARY KEY,ClientId INT,CurrentPublishedVersionId BIGINT,IsActive BOOLEAN);
CREATE TABLE recruitment_pipeline_versions(Id BIGINT PRIMARY KEY,PipelineDefinitionId BIGINT);
CREATE TABLE recruitment_pipeline_stages(Id BIGINT PRIMARY KEY,PipelineVersionId BIGINT,StageCode VARCHAR(80),StageType VARCHAR(80),CardScope VARCHAR(40),StageName VARCHAR(100),DisplayOrder INT DEFAULT 0,TargetOffsetMinutes INT NULL,IsActive BOOLEAN,IsTerminal BOOLEAN);
CREATE TABLE recruitment_position_pipeline_assignments(Id BIGINT PRIMARY KEY AUTO_INCREMENT,PositionId BIGINT,JobPostingId BIGINT NULL,PipelineVersionId BIGINT,IsActive BOOLEAN,AssignedByUserId INT);
CREATE TABLE recruitment_job_postings(Id BIGINT PRIMARY KEY,PositionId BIGINT,PublicSlug VARCHAR(80));
CREATE TABLE recruitment_candidate_applications(Id BIGINT PRIMARY KEY,PositionId BIGINT,JobPostingId BIGINT NULL,CurrentStage VARCHAR(100),UpdatedAt DATETIME NULL);
CREATE TABLE recruitment_application_pipeline_instances(Id BIGINT PRIMARY KEY,ApplicationId BIGINT,PipelineVersionId BIGINT,PositionPipelineAssignmentId BIGINT NULL,CurrentStageInstanceId BIGINT,Status VARCHAR(40));
CREATE TABLE recruitment_application_stage_instances(Id BIGINT PRIMARY KEY,ApplicationPipelineInstanceId BIGINT,ApplicationId BIGINT,PipelineStageId BIGINT,Status VARCHAR(40),EnteredAtUtc DATETIME,DueAtUtc DATETIME NULL,ExitedAtUtc DATETIME NULL,PausedDurationSeconds BIGINT);
CREATE TABLE recruitment_position_pipeline_instances(Id BIGINT PRIMARY KEY AUTO_INCREMENT,ClientId INT,RequisitionId BIGINT NULL,WorkOrderId BIGINT NULL,WorkOrderLineId BIGINT NULL,PositionId BIGINT NULL,PipelineVersionId BIGINT,CurrentStageInstanceId BIGINT NULL,Status VARCHAR(40),SlaAnchorAtUtc DATETIME NULL,UpdatedAtUtc DATETIME NULL,UNIQUE KEY(WorkOrderLineId));
CREATE TABLE recruitment_position_stage_instances(Id BIGINT PRIMARY KEY AUTO_INCREMENT,PositionPipelineInstanceId BIGINT,PipelineStageId BIGINT,Status VARCHAR(40),EnteredAtUtc DATETIME NULL,DueAtUtc DATETIME NULL);
CREATE TABLE recruitment_stage_events(Id BIGINT PRIMARY KEY AUTO_INCREMENT,StageInstanceId BIGINT,EventType VARCHAR(80),EventTitle VARCHAR(180),EventDetails VARCHAR(1000),ActorUserId INT);
CREATE TABLE recruitment_position_stage_events(Id BIGINT PRIMARY KEY AUTO_INCREMENT,PositionPipelineInstanceId BIGINT,PositionStageInstanceId BIGINT,EventType VARCHAR(80),EventTitle VARCHAR(220),EventDetails TEXT,ActorUserId INT);
CREATE TABLE recruitment_pipeline_transition_requests(StageInstanceId BIGINT,Status VARCHAR(40));
CREATE TABLE recruitment_hiring_case_advance_requests(PositionStageInstanceId BIGINT,Status VARCHAR(40));
CREATE TABLE recruitment_interviews(PipelineStageInstanceId BIGINT);
CREATE TABLE recruitment_candidate_action_sessions(PipelineStageInstanceId BIGINT);
CREATE TABLE recruitment_offers(PipelineStageInstanceId BIGINT);
CREATE TABLE recruitment_process_documents(ApplicationId BIGINT,HiringCaseId BIGINT,PipelineStageId BIGINT);
CREATE TABLE recruitment_stage_action_executions(Id BIGINT PRIMARY KEY AUTO_INCREMENT,ApplicationId BIGINT,StageInstanceId BIGINT,StageActionId BIGINT,TriggerEvent VARCHAR(40),ActionCode VARCHAR(80),Status VARCHAR(40),IsBlocking BOOLEAN,ApplicationScoreId BIGINT NULL,StartedAtUtc DATETIME,CompletedAtUtc DATETIME,UNIQUE KEY(StageInstanceId,StageActionId,TriggerEvent));
CREATE TABLE recruitment_pipeline_stage_actions(Id BIGINT PRIMARY KEY,PipelineStageId BIGINT,TriggerEvent VARCHAR(40),ActionCode VARCHAR(80),ExecutionOrder INT,IsActive BOOLEAN,IsBlocking BOOLEAN);
CREATE TABLE recruitment_stage_ats_configurations(PipelineStageId BIGINT,ScoringProfileId BIGINT NULL);
""";

    private sealed class Database : IAsyncDisposable
    {
        private readonly MySqlConnection owner; private readonly string name;
        public MySqlConnection Db { get; }
        public IConfiguration Config { get; }
        public string Connection { get; }
        private Database(MySqlConnection owner, MySqlConnection db, string name, string connection)
        { this.owner = owner; Db = db; this.name = name; Connection = connection; Config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = connection }).Build(); }
        public static async Task<Database> CreateAsync()
        {
            var connection = new MySqlConnectionStringBuilder(Environment.GetEnvironmentVariable("HRMS_INTERNAL_INTERVIEW_TEST_CONNECTION") ?? throw new InvalidOperationException("Loopback connection required"));
            Assert.Contains(connection.Server, new[] { "localhost", "127.0.0.1", "::1" });
            var name = "hrms_revision_test_" + Guid.NewGuid().ToString("N"); connection.Database = "";
            var owner = new MySqlConnection(connection.ConnectionString); await owner.OpenAsync();
            await owner.ExecuteAsync($"CREATE DATABASE `{name}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci");
            connection.Database = name; var db = new MySqlConnection(connection.ConnectionString); await db.OpenAsync();
            return new(owner, db, name, connection.ConnectionString);
        }
        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync(); MySqlConnection.ClearAllPools();
            // Only this test's random, loopback-only database is ever removed.
            await owner.ExecuteAsync($"DROP DATABASE `{name}`"); await owner.DisposeAsync();
        }
    }
}
