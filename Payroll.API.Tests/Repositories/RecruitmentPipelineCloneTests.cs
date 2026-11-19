using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Repositories;

public sealed class RecruitmentPipelineCloneTests
{
    [Theory]
    [InlineData("Application")]
    [InlineData("Position")]
    [InlineData("Hybrid")]
    public void CopiesEachPipelineKindWithIndependentNestedConfigurationAndRemappedEdges(string scope)
    {
        var source = Example(scope);
        var before = JsonSerializer.Serialize(source);
        var draft = RecruitmentPipelineRepository.BuildSimilarPipelineDraft(source);
        Assert.Equal(scope, draft.ScopeType); Assert.Equal(0, draft.Id); Assert.Equal(0, draft.PipelineDefinitionId);
        Assert.Equal(4320, draft.OverallSlaMinutes); Assert.Equal("CumulativeFromAnchor", draft.SlaMode);
        Assert.All(draft.Stages, s => Assert.True(s.Id < 0));
        Assert.Equal(draft.Stages[0].Id, draft.Transitions[0].FromStageId);
        Assert.Equal(draft.Stages[1].Id, draft.Transitions[0].ToStageId);
        Assert.Equal(0, draft.Transitions[0].Rules[0].Id);
        Assert.Equal(77m, draft.Transitions[0].Rules[0].DecimalValue);
        var stage = draft.Stages[0];
        Assert.Equal(21, stage.DefaultPanelMembers[0].PanelUserId);
        Assert.Equal(0, stage.Actions[0].Recipients[0].StageActionId);
        Assert.Equal(12, stage.Actions[0].Recipients[0].UserId);
        Assert.Equal(200, stage.ExternalFormConfiguration!.FormVersionId);
        Assert.Equal(8, stage.AttachmentRequirements[0].AttachmentFieldConfigurationId);
        Assert.Equal(57, stage.OfferConfiguration!.OfferTemplateId);
        Assert.Equal(60m, stage.InterviewConfiguration!.Competencies[0].WeightPercent);
        Assert.Equal(11, stage.InterviewConfiguration.Competencies[0].CompetencyId);
        Assert.Equal("ORDER", draft.AllRejectedRollback.TargetStageCode);
        stage.Actions[0].Recipients[0].UserId = 99;
        stage.InterviewConfiguration.Competencies[0].WeightPercent = 5;
        stage.AccessPolicy.AllowedUserIds.Clear();
        draft.AllRejectedRollback.TargetStageCode = "OTHER";
        Assert.Equal(before, JsonSerializer.Serialize(source));
    }

    [Fact]
    public void ForeignReferencesBecomeBlockingPlaceholdersWithoutDroppingRequiredRowsOrWideningAccess()
    {
        var source = Example("Hybrid");
        var draft = RecruitmentPipelineRepository.BuildSimilarPipelineDraft(source);
        var warnings = new List<string>();
        RecruitmentPipelineRepository.RemoveForeignCloneReferences(draft, new([], [57], [], [], [], [], [], new(StringComparer.OrdinalIgnoreCase) { "gad" }), warnings);
        var stage = draft.Stages[0];
        Assert.True(stage.AccessPolicy.Restricted); Assert.Empty(stage.AccessPolicy.AllowedUserIds); Assert.Equal(["gad"], stage.AccessPolicy.AllowedRoleCodes);
        Assert.True(stage.RequiresApproval); Assert.Equal(0, stage.ApprovalWorkflowId);
        Assert.Equal(0, stage.ExternalFormConfiguration!.FormVersionId); Assert.True(stage.ExternalFormConfiguration.SubmissionRequired);
        Assert.Equal(-8, Assert.Single(stage.AttachmentRequirements).AttachmentFieldConfigurationId); Assert.True(stage.AttachmentRequirements[0].IsRequired);
        Assert.Equal(-21, Assert.Single(stage.DefaultPanelMembers).PanelUserId); Assert.True(stage.DefaultPanelMembers[0].IsRequired);
        Assert.Equal(-11, stage.InterviewConfiguration!.Competencies[0].CompetencyId); Assert.Equal(60m, stage.InterviewConfiguration.Competencies[0].WeightPercent);
        Assert.Equal(0, stage.Actions[0].Recipients[0].UserId);
        Assert.Equal(57, stage.OfferConfiguration!.OfferTemplateId); // a reusable target/global template is retained
        Assert.NotEmpty(warnings); Assert.NotEmpty(RecruitmentPipelineRepository.CloneReferenceError(draft.Stages, draft.Transitions));
    }

    [Fact]
    public async Task AnUnpermittedTargetIsRejectedBeforeAnyDatabaseConnection()
    {
        var config = new ConfigurationBuilder().Build();
        var repo = new RecruitmentPipelineRepository(config, new WorkflowRepository(config));
        var user = new AuthUser { Id = 5, ClientId = 20, IsActive = true, Permissions = ["recruitment.configuration.manage"] };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => repo.CreateSimilarPipelineAsync(1, new() { SourceVersionId = 1, TargetClientId = 30, PipelineCode = "COPY", PipelineName = "Copy" }, user));
    }

    [LocalInterviewDatabaseFact]
    public async Task CloneIsANewDraftAndKeepsPublishedSourceRuntimeAndNestedConfigurationUnchanged()
    {
        await using var f = await Database.CreateAsync();
        await f.Db.ExecuteAsync("""
UPDATE recruitment_pipeline_stages SET StageType='Interview' WHERE Id=11;
INSERT INTO authusers VALUES(5,20,'panel@example.invalid','Synthetic panel',TRUE);
INSERT INTO recruitment_stage_default_panel_members VALUES(61,11,5,'Chairperson',TRUE,1);
INSERT INTO recruitment_stage_ats_configurations VALUES(71,11,7,68,20,FALSE,FALSE,FALSE,TRUE,'SHORTLIST','REJECT');
INSERT INTO recruitment_stage_external_form_configurations VALUES(81,11,9,TRUE,TRUE,10080,20);
INSERT INTO recruitment_stage_attachment_requirements VALUES(91,11,10,TRUE,1,2,TRUE,1);
INSERT INTO recruitment_stage_process_document_requirements VALUES(101,11,'MOM',15,TRUE,TRUE,1);
INSERT INTO recruitment_stage_offer_configurations VALUES(111,11,15,NULL,'ApprovedMaximum',7,FALSE,NULL,10,TRUE,TRUE,30,120,'ORDER');
INSERT INTO recruitment_interview_competency_definitions VALUES(7,20,'Synthetic rubric',TRUE);
INSERT INTO recruitment_interview_stage_configurations VALUES(121,11,1,'Technical',45,1,67,'PercentageWeighted','Average',TRUE,TRUE,TRUE);
INSERT INTO recruitment_interview_stage_competencies VALUES(131,121,7,100,55,1);
""");
        var repo = new RecruitmentPipelineRepository(f.Config, new WorkflowRepository(f.Config));
        var admin = new AuthUser { Id = 1, IsActive = true, Permissions = ["settings.manage"] };
        var before = JsonSerializer.Serialize(await repo.GetPipelineVersionAsync(1, admin));
        var (result, error) = await repo.CreateSimilarPipelineAsync(1, new() { SourceVersionId = 1, TargetClientId = 20, PipelineCode = "SIMILAR", PipelineName = "Similar hiring" }, admin);
        Assert.Empty(error); Assert.NotNull(result); Assert.Empty(result.Warnings);
        Assert.Equal("Draft", result.Version.Status); Assert.Equal(1, result.Version.VersionNumber); Assert.Null(result.Definition.CurrentPublishedVersionId);
        Assert.Equal("Hybrid", result.Version.ScopeType); Assert.Equal(2, result.Version.Stages.Count);
        Assert.All(result.Version.Stages, s => Assert.DoesNotContain(s.Id, new long[] { 11, 12 }));
        Assert.Equal(result.Version.Stages[0].Id, result.Version.Transitions[0].FromStageId);
        Assert.Equal(result.Version.Stages[1].Id, result.Version.Transitions[0].ToStageId);
        Assert.Equal("SEND_NOTIFICATION", Assert.Single(result.Version.Stages[0].Actions).ActionCode);
        Assert.Equal("proof@example.invalid", Assert.Single(result.Version.Stages[0].Actions[0].Recipients).EmailAddress);
        Assert.Equal(77m, Assert.Single(result.Version.Transitions[0].Rules).DecimalValue);
        var nested = result.Version.Stages[0];
        Assert.Equal(7, nested.AtsConfiguration!.ScoringProfileId); Assert.NotEqual(71, nested.AtsConfiguration.Id);
        Assert.Equal(9, nested.ExternalFormConfiguration!.FormVersionId); Assert.NotEqual(81, nested.ExternalFormConfiguration.Id);
        Assert.Equal(10, Assert.Single(nested.AttachmentRequirements).AttachmentFieldConfigurationId);
        Assert.Equal(15, Assert.Single(nested.ProcessDocumentRequirements).TemplateId);
        Assert.Equal("Chairperson", Assert.Single(nested.DefaultPanelMembers).PanelRole);
        Assert.Equal(120, nested.OfferConfiguration!.NegotiationSlaExtensionMinutes);
        Assert.Equal(67m, nested.InterviewConfiguration!.MinimumPassingScore);
        Assert.Equal(100m, Assert.Single(nested.InterviewConfiguration.Competencies).WeightPercent);
        Assert.Equal(55m, nested.InterviewConfiguration.Competencies[0].MinimumScore);
        Assert.Equal(before, JsonSerializer.Serialize(await repo.GetPipelineVersionAsync(1, admin)));
        Assert.Equal(1, await f.Db.ExecuteScalarAsync<int>("SELECT CurrentPublishedVersionId FROM recruitment_pipeline_definitions WHERE Id=1"));
        Assert.Equal(1, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_position_pipeline_assignments"));
        Assert.Equal(1, await f.Db.ExecuteScalarAsync<int>("SELECT PipelineVersionId FROM recruitment_position_pipeline_assignments"));
        var repeat = await repo.CreateSimilarPipelineAsync(1, new() { SourceVersionId = 1, TargetClientId = 20, PipelineCode = "SIMILAR", PipelineName = "Duplicate code" }, admin);
        Assert.Null(repeat.Row); Assert.Contains("already exists", repeat.Error);
        Assert.Equal(2, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_pipeline_definitions"));
        Assert.Equal(2, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_pipeline_versions"));
    }

    [LocalInterviewDatabaseFact]
    public async Task CrossClientWarningsSurviveRefreshAndPreventPublishingAnUnreviewedCopy()
    {
        await using var f = await Database.CreateAsync();
        var repo = new RecruitmentPipelineRepository(f.Config, new WorkflowRepository(f.Config));
        var admin = new AuthUser { Id = 1, IsActive = true, Permissions = ["settings.manage"] };
        var created = await repo.CreateSimilarPipelineAsync(1, new() { SourceVersionId = 1, TargetClientId = 30, PipelineCode = "CLIENT30", PipelineName = "Client 30 copy" }, admin);
        Assert.Empty(created.Error); Assert.NotNull(created.Row); Assert.NotEmpty(created.Row.Warnings);
        var reloaded = await repo.GetPipelineVersionAsync(created.Row.Version.Id, admin);
        Assert.NotNull(reloaded); Assert.NotEmpty(reloaded.CloneWarnings);
        Assert.Equal("", Assert.Single(reloaded.Stages[0].Actions[0].Recipients).EmailAddress);
        var publish = await repo.PublishPipelineVersionAsync(reloaded.Id, admin);
        Assert.Null(publish.Row); Assert.Contains("acknowledge", publish.Error);
        Assert.Equal("Draft", await f.Db.ExecuteScalarAsync<string>("SELECT Status FROM recruitment_pipeline_versions WHERE Id=@Id", new { reloaded.Id }));
        await f.Db.ExecuteAsync("INSERT INTO recruitment_templates VALUES(5,30,TRUE)");
        reloaded.Stages[0].Actions[0].TemplateId = 5;
        reloaded.Stages[0].Actions[0].Recipients[0].EmailAddress = "target@example.invalid";
        var reviewed = RecruitmentPipelineRepository.BuildSimilarPipelineDraft(reloaded);
        reviewed.Id = reloaded.Id; reviewed.PipelineDefinitionId = reloaded.PipelineDefinitionId;
        var savedWithoutAcknowledgement = await repo.SavePipelineVersionAsync(reviewed, admin);
        Assert.Empty(savedWithoutAcknowledgement.Error); Assert.NotNull(savedWithoutAcknowledgement.Row);
        Assert.NotEmpty(savedWithoutAcknowledgement.Row.CloneWarnings); // a blind Save cannot clear review
        reviewed.AcknowledgeCloneWarnings = true;
        var acknowledged = await repo.SavePipelineVersionAsync(reviewed, admin);
        Assert.Empty(acknowledged.Error); Assert.NotNull(acknowledged.Row); Assert.Empty(acknowledged.Row.CloneWarnings);
        Assert.Equal("Published", await f.Db.ExecuteScalarAsync<string>("SELECT Status FROM recruitment_pipeline_versions WHERE Id=1"));
    }

    private static RecruitmentPipelineVersion Example(string scope) => new()
    {
        Id = 91, PipelineDefinitionId = 5, Status = "Published", ScopeType = scope, SlaMode = "CumulativeFromAnchor", OverallSlaMinutes = 4320,
        AllRejectedRollback = new() { Enabled = true, TargetStageCode = "ORDER" },
        Stages = [new() { Id = 501, StageCode = "ORDER", StageName = "Order", CardScope = scope == "Application" ? "Application" : "Position", IsInitial = true, RequiresApproval = true, ApprovalWorkflowId = 99,
            AccessPolicy = new() { OwnerType = "GAD", Restricted = true, AllowedRoleCodes = ["gad"], AllowedUserIds = [12] },
            Actions = [new() { Id = 8, WorkflowId = 99, Recipients = [new() { Id = 9, StageActionId = 8, RecipientType = "SpecificUser", UserId = 12 }] }],
            DefaultPanelMembers = [new() { Id = 3, PanelUserId = 21, IsRequired = true }],
            AtsConfiguration = new() { Id = 15, ScoringProfileId = 42 }, ExternalFormConfiguration = new() { Id = 16, FormVersionId = 200, SubmissionRequired = true },
            AttachmentRequirements = [new() { Id = 17, AttachmentFieldConfigurationId = 8, IsRequired = true }],
            ProcessDocumentRequirements = [new() { Id = 18, DocumentType = "MOM", TemplateId = 88 }], OfferConfiguration = new() { Id = 19, OfferTemplateId = 57 },
            InterviewConfiguration = new() { Id = 20, Competencies = [new() { Id = 21, CompetencyId = 11, WeightPercent = 60, MinimumScore = 30 }] }
        }, new() { Id = 502, StageCode = "DONE", StageName = "Done", CardScope = scope == "Position" ? "Position" : "Application", IsTerminal = true }],
        Transitions = [new() { Id = 44, FromStageId = 501, ToStageId = 502, ApprovalWorkflowId = 99, Rules = [new() { Id = 45, RuleType = "ATS_SCORE", DecimalValue = 77 }] }]
    };

    internal sealed class Database(MySqlConnection owner, MySqlConnection db, string name, IConfiguration config) : IAsyncDisposable
    {
        public MySqlConnection Db => db; public IConfiguration Config => config;
        public static async Task<Database> CreateAsync()
        {
            var cs = new MySqlConnectionStringBuilder(Environment.GetEnvironmentVariable("HRMS_INTERNAL_INTERVIEW_TEST_CONNECTION"));
            Assert.Contains(cs.Server, new[] { "127.0.0.1", "localhost", "::1" }); Assert.Equal(33330u, cs.Port);
            cs.Database = ""; var owner = new MySqlConnection(cs.ConnectionString); await owner.OpenAsync();
            var name = "hrms_pipeline_clone_test_" + Guid.NewGuid().ToString("N");
            await owner.ExecuteAsync($"CREATE DATABASE `{name}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci");
            cs.Database = name; var db = new MySqlConnection(cs.ConnectionString); await db.OpenAsync();
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = cs.ConnectionString }).Build();
            await db.ExecuteAsync(Schema);
            return new(owner, db, name, config);
        }
        public async ValueTask DisposeAsync()
        {
            await db.DisposeAsync();
            if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^hrms_pipeline_clone_test_[a-f0-9]{32}$")) throw new InvalidOperationException("Unexpected test database.");
            await owner.ExecuteAsync($"DROP DATABASE `{name}`"); await owner.DisposeAsync();
        }
    }

    private const string Schema = """
CREATE TABLE clients(Id INT PRIMARY KEY,Name VARCHAR(100),IsActive BOOLEAN); INSERT INTO clients VALUES(20,'Fixture source',TRUE),(30,'Fixture target',TRUE);
CREATE TABLE authusers(Id INT PRIMARY KEY,ClientId INT NULL,Email VARCHAR(190),DisplayName VARCHAR(100),IsActive BOOLEAN); INSERT INTO authusers VALUES(1,NULL,'fixture@example.invalid','Fixture admin',TRUE);
CREATE TABLE authroles(Code VARCHAR(80),ClientId INT NULL);
CREATE TABLE modulesettings(client_id INT,ModuleCode VARCHAR(100) UNIQUE,IsEnabled BOOLEAN,SettingsJson JSON,UpdatedAt DATETIME NULL);
CREATE TABLE recruitment_pipeline_definitions(Id BIGINT PRIMARY KEY AUTO_INCREMENT,ClientId INT,PipelineCode VARCHAR(80),PipelineName VARCHAR(180),Description VARCHAR(1000) DEFAULT '',CurrentPublishedVersionId BIGINT NULL,IsActive BOOLEAN,CreatedByUserId INT,CreatedAtUtc DATETIME DEFAULT CURRENT_TIMESTAMP,UpdatedAtUtc DATETIME DEFAULT CURRENT_TIMESTAMP,UNIQUE KEY(ClientId,PipelineCode));
CREATE TABLE recruitment_pipeline_versions(Id BIGINT PRIMARY KEY AUTO_INCREMENT,PipelineDefinitionId BIGINT,VersionNumber INT,Status VARCHAR(30),ScopeType VARCHAR(40),SlaMode VARCHAR(40),OverallSlaMinutes INT,CreatedByUserId INT,PublishedByUserId INT NULL,PublishedAtUtc DATETIME NULL,CreatedAtUtc DATETIME DEFAULT CURRENT_TIMESTAMP);
CREATE TABLE recruitment_pipeline_stages(Id BIGINT PRIMARY KEY AUTO_INCREMENT,PipelineVersionId BIGINT,StageCode VARCHAR(80),StageName VARCHAR(180),StageType VARCHAR(40),CardScope VARCHAR(40),StageNumber INT,DisplayOrder INT,SlaDurationMinutes INT,SlaWarningMinutes INT,TargetOffsetMinutes INT NULL,StakeholderCode VARCHAR(80),AllowPause BOOLEAN,PauseBehavior VARCHAR(40),ApprovalWorkflowId BIGINT NULL,RequiresApproval BOOLEAN,CalendarEnabled BOOLEAN,AllowSkip BOOLEAN,IsInitial BOOLEAN,IsTerminal BOOLEAN,IsActive BOOLEAN);
CREATE TABLE recruitment_pipeline_transitions(Id BIGINT PRIMARY KEY AUTO_INCREMENT,PipelineVersionId BIGINT,FromStageId BIGINT,ToStageId BIGINT,OutcomeCode VARCHAR(80),ActionLabel VARCHAR(180),ApprovalWorkflowId BIGINT NULL,RequiresReason BOOLEAN,IsActive BOOLEAN,DisplayOrder INT);
CREATE TABLE recruitment_pipeline_transition_rules(Id BIGINT PRIMARY KEY AUTO_INCREMENT,TransitionId BIGINT,RuleType VARCHAR(80),ComparisonOperator VARCHAR(30),TextValue VARCHAR(1000) NULL,IntegerValue BIGINT NULL,DecimalValue DECIMAL(18,4) NULL,BooleanValue BOOLEAN NULL,IsMandatory BOOLEAN,ErrorMessage VARCHAR(500),DisplayOrder INT);
CREATE TABLE recruitment_pipeline_stage_actions(Id BIGINT PRIMARY KEY AUTO_INCREMENT,PipelineStageId BIGINT,TriggerEvent VARCHAR(60),ActionCode VARCHAR(80),ExecutionOrder INT,IsBlocking BOOLEAN,WorkflowId BIGINT NULL,TemplateId BIGINT NULL,IsActive BOOLEAN);
CREATE TABLE recruitment_stage_action_recipients(Id BIGINT PRIMARY KEY AUTO_INCREMENT,StageActionId BIGINT,RecipientType VARCHAR(60),UserId INT NULL,RoleCode VARCHAR(100),EmailAddress VARCHAR(190),DisplayOrder INT,IsActive BOOLEAN);
CREATE TABLE recruitment_stage_default_panel_members(Id BIGINT PRIMARY KEY AUTO_INCREMENT,PipelineStageId BIGINT,PanelUserId INT,PanelRole VARCHAR(80),IsRequired BOOLEAN,DisplayOrder INT);
CREATE TABLE recruitment_stage_ats_configurations(Id BIGINT PRIMARY KEY AUTO_INCREMENT,PipelineStageId BIGINT,ScoringProfileId BIGINT NULL,MinimumAdvanceScore DECIMAL(5,2),MaximumRejectScore DECIMAL(5,2),AutoScoreOnEntry BOOLEAN,AutoAdvance BOOLEAN,AutoReject BOOLEAN,RequireHumanConfirmation BOOLEAN,AdvanceOutcomeCode VARCHAR(80),RejectOutcomeCode VARCHAR(80));
CREATE TABLE recruitment_stage_external_form_configurations(Id BIGINT PRIMARY KEY AUTO_INCREMENT,PipelineStageId BIGINT,FormVersionId BIGINT,SubmissionRequired BOOLEAN,AllowSaveDraft BOOLEAN,ActionTokenValidityMinutes INT,ActionTokenMaximumUses INT);
CREATE TABLE recruitment_stage_attachment_requirements(Id BIGINT PRIMARY KEY AUTO_INCREMENT,PipelineStageId BIGINT,AttachmentFieldConfigurationId BIGINT,IsRequired BOOLEAN,MinimumFileCount INT,MaximumFileCount INT,RequiresVerification BOOLEAN,DisplayOrder INT);
CREATE TABLE recruitment_stage_process_document_requirements(Id BIGINT PRIMARY KEY AUTO_INCREMENT,PipelineStageId BIGINT,DocumentType VARCHAR(80),TemplateId BIGINT NULL,IsRequired BOOLEAN,RequiresSignature BOOLEAN,DisplayOrder INT);
CREATE TABLE recruitment_stage_offer_configurations(Id BIGINT PRIMARY KEY AUTO_INCREMENT,PipelineStageId BIGINT,OfferTemplateId BIGINT NULL,ApprovalWorkflowId BIGINT NULL,BudgetBasis VARCHAR(40),MaximumVariancePercent DECIMAL(7,2),RequireApprovalWhenVarianceExceeded BOOLEAN,VarianceApprovalWorkflowId BIGINT NULL,CandidateResponseValidityDays INT,RequireAcceptedOfferToAdvance BOOLEAN,NegotiationSlaExtensionEnabled BOOLEAN,NegotiationThresholdPercent DECIMAL(7,2),NegotiationSlaExtensionMinutes INT,NegotiationSlaStageCodes VARCHAR(1000));
CREATE TABLE recruitment_interview_stage_configurations(Id BIGINT PRIMARY KEY AUTO_INCREMENT,PipelineStageId BIGINT,RoundNumber INT,InterviewType VARCHAR(80),DefaultDurationMinutes INT,MinimumPanelCount INT,MinimumPassingScore DECIMAL(5,2),ScoreInputMode VARCHAR(40),PanelAggregationMethod VARCHAR(40),FeedbackRequired BOOLEAN,CalendarEnabled BOOLEAN,AllowReschedule BOOLEAN);
CREATE TABLE recruitment_interview_stage_competencies(Id BIGINT PRIMARY KEY AUTO_INCREMENT,InterviewStageConfigurationId BIGINT,CompetencyId BIGINT,WeightPercent DECIMAL(5,2),MinimumScore DECIMAL(5,2),DisplayOrder INT);
CREATE TABLE recruitment_interview_competency_definitions(Id BIGINT,ClientId INT,CompetencyName VARCHAR(180),IsActive BOOLEAN);
CREATE TABLE recruitment_templates(Id BIGINT,ClientId INT,IsActive BOOLEAN);
CREATE TABLE recruitment_ats_scoring_profiles(Id BIGINT,ClientId INT,IsActive BOOLEAN);
CREATE TABLE workflowmasters(Id BIGINT,ClientId INT NULL,IsActive BOOLEAN);
CREATE TABLE form_versions(Id BIGINT,FormDefinitionId BIGINT,Status VARCHAR(40)); CREATE TABLE form_definitions(Id BIGINT,ClientId INT);
CREATE TABLE attachment_field_configurations(id BIGINT,client_id INT,is_active BOOLEAN);
CREATE TABLE recruitment_audit(Id BIGINT PRIMARY KEY AUTO_INCREMENT,EntityType VARCHAR(80),EntityId BIGINT,Action VARCHAR(180),NewValueJson JSON,ChangedByUserId INT);
CREATE TABLE recruitment_position_pipeline_assignments(Id BIGINT,PipelineVersionId BIGINT); INSERT INTO recruitment_position_pipeline_assignments VALUES(99,1);
INSERT INTO recruitment_pipeline_definitions(Id,ClientId,PipelineCode,PipelineName,Description,CurrentPublishedVersionId,IsActive,CreatedByUserId) VALUES(1,20,'SOURCE','Fixture published journey','Fixture',1,TRUE,1);
INSERT INTO recruitment_pipeline_versions(Id,PipelineDefinitionId,VersionNumber,Status,ScopeType,SlaMode,OverallSlaMinutes,CreatedByUserId) VALUES(1,1,1,'Published','Hybrid','StageEntry',0,1);
INSERT INTO recruitment_pipeline_stages VALUES(11,1,'ORDER','Work order','Screening','Position',1,1,1440,60,NULL,'GAD',TRUE,'ShiftStageAndOverall',NULL,FALSE,FALSE,FALSE,TRUE,FALSE,TRUE),(12,1,'DONE','Done','Completed','Application',2,2,0,0,NULL,'CLIENT',TRUE,'ShiftStageAndOverall',NULL,FALSE,FALSE,FALSE,FALSE,TRUE,TRUE);
INSERT INTO recruitment_pipeline_transitions VALUES(21,1,11,12,'ADVANCE','Continue',NULL,FALSE,TRUE,1);
INSERT INTO recruitment_pipeline_transition_rules VALUES(31,21,'ATS_SCORE','GTE',NULL,NULL,77,NULL,TRUE,'Score required',1);
INSERT INTO recruitment_pipeline_stage_actions VALUES(41,11,'OnEntry','SEND_NOTIFICATION',1,FALSE,NULL,NULL,TRUE);
INSERT INTO recruitment_stage_action_recipients VALUES(51,41,'StaticEmail',NULL,'','proof@example.invalid',1,TRUE);
""";
}
