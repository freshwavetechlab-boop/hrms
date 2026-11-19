using System.Reflection;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Xunit;

namespace Payroll.API.Tests.Repositories;

public sealed class RecruitmentPipelineCrudChildrenTests
{
    [OwnedMySqlFact]
    public async Task EditingNestedConfigurationPreservesEveryRetainedIdentityAndExecutionHistory()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Db.ExecuteAsync("""
INSERT INTO recruitment_pipeline_stage_actions VALUES(10,7,'OnEntry','SEND_NOTIFICATION',1,FALSE,NULL,6,TRUE);
INSERT INTO recruitment_stage_action_recipients VALUES(20,10,'StaticEmail',NULL,'','old@example.test',1,TRUE);
INSERT INTO recruitment_stage_action_executions VALUES(30,10);
INSERT INTO recruitment_stage_default_panel_members VALUES(25,7,101,'Panelist',TRUE,1);
INSERT INTO recruitment_stage_ats_configurations VALUES(40,7,NULL,60,0,FALSE,FALSE,FALSE,TRUE,'SHORTLIST','REJECT');
INSERT INTO recruitment_stage_external_form_configurations VALUES(50,7,300,TRUE,TRUE,10080,20);
INSERT INTO recruitment_stage_attachment_requirements VALUES(60,7,44,TRUE,1,1,FALSE,1);
INSERT INTO recruitment_stage_process_document_requirements VALUES(70,7,'MOM',NULL,TRUE,FALSE,1);
INSERT INTO recruitment_stage_offer_configurations VALUES(80,7,NULL,NULL,'ApprovedMaximum',0,FALSE,NULL,7,TRUE,FALSE,30,0,'');
INSERT INTO recruitment_interview_stage_configurations VALUES(90,7,1,'Technical',60,1,60,'PercentageWeighted','Average',TRUE,TRUE,TRUE);
INSERT INTO recruitment_interview_stage_competencies VALUES(100,90,400,100,60,1);
INSERT INTO recruitment_interview_feedback_competency_scores VALUES(110,100);
""");
        var stage = new RecruitmentPipelineStage
        {
            Id = 7, StageType = "Interview",
            Actions = [new() { Id = 10, TriggerEvent = "OnEntry", ActionCode = "SEND_NOTIFICATION", ExecutionOrder = 2, TemplateId = 8,
                Recipients = [new() { Id = 20, RecipientType = "StaticEmail", EmailAddress = "new@example.test", DisplayOrder = 1 }] }],
            DefaultPanelMembers = [new() { Id = 25, PanelUserId = 101, PanelRole = "Chairperson", DisplayOrder = 1 }],
            AtsConfiguration = new() { Id = 40, AutoScoreOnEntry = false, MinimumAdvanceScore = 75 },
            ExternalFormConfiguration = new() { Id = 50, FormVersionId = 301 },
            AttachmentRequirements = [new() { Id = 60, AttachmentFieldConfigurationId = 44, MinimumFileCount = 2, MaximumFileCount = 3, DisplayOrder = 1 }],
            ProcessDocumentRequirements = [new() { Id = 70, DocumentType = "MOM", RequiresSignature = true, DisplayOrder = 1 }],
            OfferConfiguration = new() { Id = 80, RequireApprovalWhenVarianceExceeded = false, CandidateResponseValidityDays = 14 },
            InterviewConfiguration = new() { Id = 90, RoundNumber = 1, DefaultDurationMinutes = 90,
                Competencies = [new() { Id = 100, CompetencyId = 400, WeightPercent = 100, MinimumScore = 65, DisplayOrder = 1 }] }
        };
        await using (var tx = await f.Db.BeginTransactionAsync()) { await Reconcile(f.Db, tx, stage); await tx.CommitAsync(); }
        foreach (var entry in new[] { ("recruitment_pipeline_stage_actions",10), ("recruitment_stage_action_recipients",20), ("recruitment_stage_default_panel_members",25),
            ("recruitment_stage_ats_configurations",40),("recruitment_stage_external_form_configurations",50),("recruitment_stage_attachment_requirements",60),
            ("recruitment_stage_process_document_requirements",70),("recruitment_stage_offer_configurations",80),("recruitment_interview_stage_configurations",90),("recruitment_interview_stage_competencies",100) })
            Assert.Equal(1, await f.Db.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {entry.Item1} WHERE Id=@Id", new { Id = entry.Item2 }));
        Assert.Equal(10, await f.Db.ExecuteScalarAsync<long>("SELECT StageActionId FROM recruitment_stage_action_executions WHERE Id=30"));
        Assert.Equal(100, await f.Db.ExecuteScalarAsync<long>("SELECT InterviewStageCompetencyId FROM recruitment_interview_feedback_competency_scores WHERE Id=110"));
        Assert.Equal("new@example.test", await f.Db.ExecuteScalarAsync<string>("SELECT EmailAddress FROM recruitment_stage_action_recipients WHERE Id=20"));
        Assert.Equal(90, await f.Db.ExecuteScalarAsync<int>("SELECT DefaultDurationMinutes FROM recruitment_interview_stage_configurations WHERE Id=90"));
    }

    [OwnedMySqlFact]
    public async Task RemovingAnExecutedActionFailsWithoutCascadingAwayItsHistory()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Db.ExecuteAsync("INSERT INTO recruitment_pipeline_stage_actions VALUES(10,7,'OnEntry','SEND_NOTIFICATION',1,FALSE,NULL,NULL,TRUE); INSERT INTO recruitment_stage_action_executions VALUES(30,10)");
        await using (var tx = await f.Db.BeginTransactionAsync())
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Reconcile(f.Db, tx, new() { Id = 7 }));
            Assert.Contains("Disable", error.Message); await tx.RollbackAsync();
        }
        Assert.Equal(1, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_stage_action_executions WHERE Id=30 AND StageActionId=10"));
    }

    [OwnedMySqlFact]
    public async Task ReorderingActionsKeepsIdsAndAllowsSwappingTheirUniqueOrderKeys()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Db.ExecuteAsync("INSERT INTO recruitment_pipeline_stage_actions VALUES(10,7,'OnEntry','SEND_NOTIFICATION',1,FALSE,NULL,NULL,TRUE),(11,7,'OnEntry','SEND_NOTIFICATION',2,FALSE,NULL,NULL,TRUE); INSERT INTO recruitment_stage_action_executions VALUES(30,10)");
        await using (var tx = await f.Db.BeginTransactionAsync())
        {
            await Reconcile(f.Db, tx, new() { Id = 7, Actions = [new() { Id = 10, ActionCode = "SEND_NOTIFICATION", ExecutionOrder = 2 }, new() { Id = 11, ActionCode = "SEND_NOTIFICATION", ExecutionOrder = 1 }] });
            await tx.CommitAsync();
        }
        Assert.Equal(new long[] { 11, 10 }, await f.Db.QueryAsync<long>("SELECT Id FROM recruitment_pipeline_stage_actions ORDER BY ExecutionOrder"));
        Assert.Equal(10, await f.Db.ExecuteScalarAsync<long>("SELECT StageActionId FROM recruitment_stage_action_executions WHERE Id=30"));
    }

    [OwnedMySqlFact]
    public async Task AForeignChildIdCannotBeMovedIntoTheEditedStage()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Db.ExecuteAsync("INSERT INTO recruitment_pipeline_stage_actions VALUES(10,8,'OnEntry','SEND_NOTIFICATION',1,FALSE,NULL,NULL,TRUE)");
        await using (var tx = await f.Db.BeginTransactionAsync())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => Reconcile(f.Db, tx, new() { Id = 7, Actions = [new() { Id = 10, ActionCode = "SEND_NOTIFICATION" }] }));
            await tx.RollbackAsync();
        }
        Assert.Equal(8, await f.Db.ExecuteScalarAsync<long>("SELECT PipelineStageId FROM recruitment_pipeline_stage_actions WHERE Id=10"));
    }

    [OwnedMySqlFact]
    public async Task ScoredCompetenciesCannotBeDeletedThroughAConfigurationEdit()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Db.ExecuteAsync("INSERT INTO recruitment_interview_stage_configurations VALUES(90,7,1,'Technical',60,1,60,'PercentageWeighted','Average',TRUE,TRUE,TRUE); INSERT INTO recruitment_interview_stage_competencies VALUES(100,90,400,100,60,1); INSERT INTO recruitment_interview_feedback_competency_scores VALUES(110,100)");
        await using (var tx = await f.Db.BeginTransactionAsync())
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Reconcile(f.Db, tx, new() { Id = 7, StageType = "Interview", InterviewConfiguration = new() { Id = 90, RoundNumber = 1 } }));
            Assert.Contains("scores", error.Message); await tx.RollbackAsync();
        }
        Assert.Equal(1, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_interview_stage_competencies WHERE Id=100"));
    }

    private static Task Reconcile(MySqlConnection db, MySqlTransaction tx, RecruitmentPipelineStage stage) =>
        (Task)typeof(RecruitmentPipelineRepository).GetMethod("ReconcileCrudStageChildrenAsync", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [db, tx, 7L, stage, 55])!;

    private sealed class OwnedMySqlFactAttribute : FactAttribute
    {
        public OwnedMySqlFactAttribute() { if (string.IsNullOrWhiteSpace(Connection())) Skip = "Set an owned loopback MySQL fixture to run this integration test."; }
    }
    private static string? Connection() => Environment.GetEnvironmentVariable("HRMS_INTERNAL_INTERVIEW_TEST_CONNECTION") ?? Environment.GetEnvironmentVariable("FREVO_ICMR_APPROVAL_TEST_DB");
    private sealed class Fixture(MySqlConnection root, MySqlConnection db, string database) : IAsyncDisposable
    {
        internal MySqlConnection Db => db;
        internal static async Task<Fixture> CreateAsync()
        {
            var target = new MySqlConnectionStringBuilder(Connection());
            Assert.Contains(target.Server, new[] { "127.0.0.1", "localhost" }); Assert.Equal(33330u, target.Port);
            target.Database = ""; var root = new MySqlConnection(target.ConnectionString); await root.OpenAsync();
            var database = "pipeline_crud_children_" + Guid.NewGuid().ToString("N");
            await root.ExecuteAsync($"CREATE DATABASE `{database}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci");
            target.Database = database; var db = new MySqlConnection(target.ConnectionString); await db.OpenAsync();
            await db.ExecuteAsync("""
CREATE TABLE recruitment_pipeline_stage_actions(Id BIGINT PRIMARY KEY AUTO_INCREMENT,PipelineStageId BIGINT,TriggerEvent VARCHAR(40),ActionCode VARCHAR(80),ExecutionOrder INT,IsBlocking BOOLEAN,WorkflowId BIGINT NULL,TemplateId BIGINT NULL,IsActive BOOLEAN,UNIQUE KEY UX_Action(PipelineStageId,TriggerEvent,ActionCode,ExecutionOrder));
CREATE TABLE recruitment_stage_action_executions(Id BIGINT PRIMARY KEY,StageActionId BIGINT,FOREIGN KEY(StageActionId) REFERENCES recruitment_pipeline_stage_actions(Id) ON DELETE CASCADE);
CREATE TABLE recruitment_profile_batch_notification_deliveries(StageActionId BIGINT);
CREATE TABLE recruitment_stage_action_recipients(Id BIGINT PRIMARY KEY AUTO_INCREMENT,StageActionId BIGINT,RecipientType VARCHAR(60),UserId INT NULL,RoleCode VARCHAR(100),EmailAddress VARCHAR(190),DisplayOrder INT,IsActive BOOLEAN);
CREATE TABLE recruitment_stage_default_panel_members(Id BIGINT PRIMARY KEY AUTO_INCREMENT,PipelineStageId BIGINT,PanelUserId INT,PanelRole VARCHAR(80),IsRequired BOOLEAN,DisplayOrder INT,UNIQUE KEY UX_Panel(PipelineStageId,PanelUserId));
CREATE TABLE recruitment_stage_ats_configurations(Id BIGINT PRIMARY KEY AUTO_INCREMENT,PipelineStageId BIGINT UNIQUE,ScoringProfileId BIGINT NULL,MinimumAdvanceScore DECIMAL(8,2),MaximumRejectScore DECIMAL(8,2),AutoScoreOnEntry BOOLEAN,AutoAdvance BOOLEAN,AutoReject BOOLEAN,RequireHumanConfirmation BOOLEAN,AdvanceOutcomeCode VARCHAR(80),RejectOutcomeCode VARCHAR(80));
CREATE TABLE recruitment_stage_external_form_configurations(Id BIGINT PRIMARY KEY AUTO_INCREMENT,PipelineStageId BIGINT UNIQUE,FormVersionId BIGINT,SubmissionRequired BOOLEAN,AllowSaveDraft BOOLEAN,ActionTokenValidityMinutes INT,ActionTokenMaximumUses INT);
CREATE TABLE recruitment_stage_attachment_requirements(Id BIGINT PRIMARY KEY AUTO_INCREMENT,PipelineStageId BIGINT,AttachmentFieldConfigurationId BIGINT,IsRequired BOOLEAN,MinimumFileCount INT,MaximumFileCount INT,RequiresVerification BOOLEAN,DisplayOrder INT,UNIQUE KEY UX_Attach(PipelineStageId,AttachmentFieldConfigurationId));
CREATE TABLE recruitment_stage_process_document_requirements(Id BIGINT PRIMARY KEY AUTO_INCREMENT,PipelineStageId BIGINT,DocumentType VARCHAR(80),TemplateId BIGINT NULL,IsRequired BOOLEAN,RequiresSignature BOOLEAN,DisplayOrder INT,UNIQUE KEY UX_Document(PipelineStageId,DocumentType));
CREATE TABLE recruitment_stage_offer_configurations(Id BIGINT PRIMARY KEY AUTO_INCREMENT,PipelineStageId BIGINT UNIQUE,OfferTemplateId BIGINT NULL,ApprovalWorkflowId BIGINT NULL,BudgetBasis VARCHAR(40),MaximumVariancePercent DECIMAL(8,2),RequireApprovalWhenVarianceExceeded BOOLEAN,VarianceApprovalWorkflowId BIGINT NULL,CandidateResponseValidityDays INT,RequireAcceptedOfferToAdvance BOOLEAN,NegotiationSlaExtensionEnabled BOOLEAN,NegotiationThresholdPercent DECIMAL(8,2),NegotiationSlaExtensionMinutes INT,NegotiationSlaStageCodes VARCHAR(1000));
CREATE TABLE recruitment_sla_adjustments(StageOfferConfigurationId BIGINT);
CREATE TABLE recruitment_interview_stage_configurations(Id BIGINT PRIMARY KEY AUTO_INCREMENT,PipelineStageId BIGINT UNIQUE,RoundNumber INT,InterviewType VARCHAR(80),DefaultDurationMinutes INT,MinimumPanelCount INT,MinimumPassingScore DECIMAL(8,2),ScoreInputMode VARCHAR(40),PanelAggregationMethod VARCHAR(40),FeedbackRequired BOOLEAN,CalendarEnabled BOOLEAN,AllowReschedule BOOLEAN);
CREATE TABLE recruitment_interview_stage_competencies(Id BIGINT PRIMARY KEY AUTO_INCREMENT,InterviewStageConfigurationId BIGINT,CompetencyId BIGINT,WeightPercent DECIMAL(8,2),MinimumScore DECIMAL(8,2),DisplayOrder INT,UNIQUE KEY UX_Competency(InterviewStageConfigurationId,CompetencyId));
CREATE TABLE recruitment_interview_competency_definitions(Id BIGINT,ClientId INT,IsActive BOOLEAN); INSERT INTO recruitment_interview_competency_definitions VALUES(400,55,TRUE);
CREATE TABLE recruitment_interviews(RoundConfigurationId BIGINT);
CREATE TABLE recruitment_interview_feedback_competency_scores(Id BIGINT,InterviewStageCompetencyId BIGINT);
""");
            return new(root, db, database);
        }
        public async ValueTask DisposeAsync()
        {
            await db.DisposeAsync();
            if (!System.Text.RegularExpressions.Regex.IsMatch(database, "^pipeline_crud_children_[a-f0-9]{32}$")) throw new InvalidOperationException("Unexpected fixture database.");
            await root.ExecuteAsync($"DROP DATABASE `{database}`"); await root.DisposeAsync();
        }
    }
}
