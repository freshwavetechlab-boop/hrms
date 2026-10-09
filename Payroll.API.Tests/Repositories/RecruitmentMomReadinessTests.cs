using Dapper;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Repositories;

public sealed partial class RecruitmentRevisionRegressionTests
{
    [LocalInterviewDatabaseFact]
    public async Task Mom_uses_selected_job_candidates_without_profile_batches_and_keeps_preparation_gates()
    {
        await using var fixture = await Database.CreateAsync();
        var db = fixture.Db;
        await db.ExecuteAsync(MomReadinessSchema);
        var repository = new RecruitmentCaseRepository(fixture.Config, null!, new TemplatePdfService(), new WorkflowRepository(fixture.Config));
        var coordinator = new AuthUser { Id = 1, ClientId = 20, Permissions = ["recruitment.document.manage"] };
        // There are deliberately no profile-batch tables. Other jobs, clients, withdrawn
        // candidates and older selected rounds must never enter this job's MoM.
        var ready = await repository.GetMomReadinessAsync(1, coordinator);
        Assert.Equal("Generate", ready!.Action);
        Assert.True(ready.CanGenerate);
        Assert.Equal(1, ready.SelectedCandidateCount);
        Assert.Null(await repository.GetMomReadinessAsync(1, new AuthUser { Id = 1, ClientId = 30, Permissions = ["recruitment.document.manage"] }));
        var viewer = await repository.GetMomReadinessAsync(1, new AuthUser { Id = 4, ClientId = 20, Permissions = ["recruitment.document.view"] });
        Assert.False(viewer!.CanGenerate);
        Assert.Null(await repository.GetMomReadinessAsync(1, new AuthUser { Id = 4, ClientId = 20, Permissions = ["recruitment.interview.panel"] }));

        await db.ExecuteAsync("UPDATE recruitment_candidate_applications SET TermsConfirmedAtUtc=NULL WHERE Id=100");
        var terms = await repository.GetMomReadinessAsync(1, coordinator);
        Assert.Equal("Terms", terms!.Action);
        Assert.Contains("Selected Candidate", terms.Message);
        Assert.False(terms.CanGenerate);
        var generation = await repository.GenerateProcessDocumentAsync(1, coordinator, "", "", default);
        Assert.Null(generation.Row);
        Assert.Contains("Confirm agreed terms", generation.Error);

        await db.ExecuteAsync("UPDATE recruitment_candidate_applications SET TermsConfirmedAtUtc=UTC_TIMESTAMP() WHERE Id=100; DELETE FROM recruitment_interview_feedback WHERE PanelUserId=3");
        var feedback = await repository.GetMomReadinessAsync(1, coordinator);
        Assert.Equal("Interview", feedback!.Action);
        Assert.Contains("feedback", feedback.Message);
        await db.ExecuteAsync("INSERT INTO recruitment_interview_feedback VALUES(2,1,3)");
        await db.ExecuteAsync("UPDATE recruitment_interviews SET Status='Scheduled' WHERE Id=1");
        Assert.Equal("Interview", (await repository.GetMomReadinessAsync(1, coordinator))!.Action);
        await db.ExecuteAsync("UPDATE recruitment_interviews SET Status='Completed' WHERE Id=1; INSERT INTO recruitment_offers VALUES(1,100,'Rejected',UTC_TIMESTAMP())");
        Assert.Equal("Interview", (await repository.GetMomReadinessAsync(1, coordinator))!.Action);
        await db.ExecuteAsync("DELETE FROM recruitment_offers; UPDATE recruitment_open_positions SET NumberOfPositions=2 WHERE Id=10");
        Assert.Contains("needs 2", (await repository.GetMomReadinessAsync(1, coordinator))!.Message);
        await db.ExecuteAsync("UPDATE recruitment_open_positions SET NumberOfPositions=1 WHERE Id=10; DELETE FROM recruitment_interview_panel_members");
        Assert.Contains("Assign the selection committee", (await repository.GetMomReadinessAsync(1, coordinator))!.Message);
    }

    [LocalInterviewDatabaseFact]
    public async Task Mom_standard_template_renders_without_setup_and_explicit_custom_template_is_preserved()
    {
        await using var fixture = await Database.CreateAsync();
        var db = fixture.Db;
        await db.ExecuteAsync(MomReadinessSchema);
        var repository = new RecruitmentCaseRepository(fixture.Config, null!, new TemplatePdfService(), new WorkflowRepository(fixture.Config));
        var user = new AuthUser { Id = 1, ClientId = 20, Permissions = ["recruitment.document.manage"] };
        var result = await repository.GenerateProcessDocumentAsync(1, user, "", "", default);
        // Reached secure storage after rendering the built-in PDF. This fixture has no
        // storage field, so it must stop without uploading or marking a draft prepared.
        Assert.Contains("No active secure attachment field", result.Error);
        Assert.Equal("Draft", await db.ExecuteScalarAsync<string>("SELECT Status FROM recruitment_process_documents WHERE Id=1"));
        await db.ExecuteAsync("INSERT INTO recruitment_templates VALUES(10,20,'MOM','Custom heading','{{unsupported_custom_token}}',TRUE); UPDATE recruitment_process_documents SET TemplateId=10; UPDATE recruitment_stage_process_document_requirements SET TemplateId=10");
        result = await repository.GenerateProcessDocumentAsync(1, user, "", "", default);
        Assert.Contains("unsupported_custom_token", result.Error); // Custom content was not silently replaced.
        await db.ExecuteAsync("UPDATE recruitment_templates SET IsActive=FALSE WHERE Id=10");
        Assert.Equal("Template", (await repository.GetMomReadinessAsync(1, user))!.Action);
        result = await repository.GenerateProcessDocumentAsync(1, user, "", "", default);
        Assert.Contains("inactive", result.Error);
    }

    [LocalInterviewDatabaseFact]
    public async Task Mom_next_action_respects_assigned_signer_and_hr_decision_and_invalidates_changed_terms()
    {
        await using var fixture = await Database.CreateAsync();
        var db = fixture.Db;
        await db.ExecuteAsync(MomReadinessSchema);
        await db.ExecuteAsync("""
UPDATE recruitment_process_documents SET Status='Prepared',BodySnapshot='Prepared job MoM' WHERE Id=1;
INSERT INTO recruitment_audit(EntityType,EntityId,Action,NewValueJson) VALUES
('RecruitmentProcessDocument',1,'Job MoM prepared','{"Candidates":[{"ApplicationId":100,"TermsVersion":1,"InterviewId":1}],"PanelMembers":[{"UserId":2},{"UserId":3}]}');
INSERT INTO workflowmasters VALUES(7,20,TRUE);
INSERT INTO recruitment_pipeline_stages(Id,StageName,StageType,PipelineVersionId,IsActive,DisplayOrder,ApprovalWorkflowId) VALUES(2,'HR approval','HR',1,TRUE,2,7);
""");
        var repository = new RecruitmentCaseRepository(fixture.Config, null!, new TemplatePdfService(), new WorkflowRepository(fixture.Config));
        var panel = new AuthUser { Id = 2, ClientId = 20, Permissions = ["recruitment.interview.panel"] };
        var observer = new AuthUser { Id = 4, ClientId = 20, Permissions = ["recruitment.document.view"] };
        var ready = await repository.GetMomReadinessAsync(1, panel);
        Assert.Equal("Sign", ready!.Action);
        Assert.True(ready.CanSign);
        Assert.Equal(new[] { "First Panel", "Second Panel" }, ready.PendingSigners);
        Assert.False((await repository.GetMomReadinessAsync(1, observer))!.CanSign);
        await db.ExecuteAsync("INSERT INTO recruitment_process_document_signatures VALUES(1,1,2,NULL)");
        ready = await repository.GetMomReadinessAsync(1, panel);
        Assert.False(ready!.CanSign);
        Assert.Equal(new[] { "Second Panel" }, ready.PendingSigners);
        await db.ExecuteAsync("INSERT INTO recruitment_process_document_signatures VALUES(2,1,3,NULL); UPDATE recruitment_process_documents SET Status='Signed',WorkflowInstanceId=8; INSERT INTO workflowinstances VALUES(8,'RecruitmentPipelineTransition','MOM:1','Pending')");
        Assert.Equal("Approval", (await repository.GetMomReadinessAsync(1, observer))!.Action);
        await db.ExecuteAsync("UPDATE workflowinstances SET Status='Approved'");
        Assert.Equal("Offers", (await repository.GetMomReadinessAsync(1, observer))!.Action);
        await db.ExecuteAsync("UPDATE workflowinstances SET Status='Rejected'");
        Assert.Equal("Revise", (await repository.GetMomReadinessAsync(1, observer))!.Action);
        await db.ExecuteAsync("UPDATE workflowinstances SET Status='Approved'; UPDATE recruitment_candidate_applications SET TermsVersion=2 WHERE Id=100");
        Assert.Equal("Revise", (await repository.GetMomReadinessAsync(1, observer))!.Action);
    }

    [LocalInterviewDatabaseFact]
    public async Task Mom_hr_approval_routes_to_each_assigned_user_without_automatic_approval()
    {
        await using var fixture = await Database.CreateAsync();
        var workflows = new WorkflowRepository(fixture.Config);
        await workflows.InitializeAsync();
        await fixture.Db.ExecuteAsync("CREATE TABLE authusers(Id INT PRIMARY KEY,IsActive BOOLEAN,DisplayName VARCHAR(100)); INSERT INTO authusers VALUES(1,TRUE,'Panel'),(2,TRUE,'Director'),(3,TRUE,'DDG');");
        var workflow = await workflows.SaveAsync(new SaveWorkflowRequest { ClientId = 20, Code = "MOM_HR", Name = "MoM HR approval", ResourceType = "RecruitmentPipelineTransition", Stages = [
            new() { StageOrder = 1, Name = "Director", ApproverType = "Specific User", ApproverUserId = 2 },
            new() { StageOrder = 2, Name = "DDG", ApproverType = "Specific User", ApproverUserId = 3 }] });
        Assert.NotNull(workflow.Row);
        var instance = await workflows.StartAsync(new StartWorkflowRequest { WorkflowId = workflow.Row.Id, ResourceType = "RecruitmentPipelineTransition", ResourceId = "MOM:1", PayloadJson = "{}" }, 1);
        Assert.NotNull(instance);
        var director = Assert.Single(await workflows.PendingAsync(2));
        Assert.Empty(await workflows.PendingAsync(3));
        Assert.False(await workflows.ActionAsync(director.Id, 3, "Approved", "Not assigned yet"));
        Assert.True(await workflows.ActionAsync(director.Id, 2, "Approved", "Approved by Director"));
        Assert.Equal("Pending", (await workflows.GetInstanceForTaskAsync(director.Id))!.Status);
        var ddg = Assert.Single(await workflows.PendingAsync(3));
        Assert.True(await workflows.ActionAsync(ddg.Id, 3, "Approved", "Approved by DDG"));
        Assert.Equal("Approved", (await workflows.GetInstanceForTaskAsync(ddg.Id))!.Status);
    }

    private const string MomReadinessSchema = """
CREATE TABLE clients(Id INT PRIMARY KEY,Name VARCHAR(100));
CREATE TABLE authusers(Id INT PRIMARY KEY,DisplayName VARCHAR(100),Email VARCHAR(100),EmployeeId INT NULL,ClientId INT,IsActive BOOLEAN DEFAULT TRUE);
CREATE TABLE employees(Id INT PRIMARY KEY,Designation VARCHAR(100),ClientId INT);
CREATE TABLE recruitment_open_positions(Id BIGINT PRIMARY KEY,PositionTitle VARCHAR(100),NumberOfPositions INT,CancelledPositions INT DEFAULT 0,OnHoldPositions INT DEFAULT 0);
CREATE TABLE recruitment_position_pipeline_instances(Id BIGINT PRIMARY KEY,PositionId BIGINT,ClientId INT,WorkOrderId BIGINT,WorkOrderLineId BIGINT,PipelineVersionId BIGINT,Status VARCHAR(30));
CREATE TABLE recruitment_work_orders(Id BIGINT PRIMARY KEY,WorkOrderNumber VARCHAR(100),ReceivedAtUtc DATETIME);
CREATE TABLE recruitment_work_order_lines(Id BIGINT PRIMARY KEY,PositionName VARCHAR(100),PayBandLevelCode VARCHAR(30),Location VARCHAR(100),Division VARCHAR(100));
CREATE TABLE recruitment_candidates(Id BIGINT PRIMARY KEY,FirstName VARCHAR(100),LastName VARCHAR(100),Email VARCHAR(100));
CREATE TABLE recruitment_candidate_applications(Id BIGINT PRIMARY KEY,PositionId BIGINT,ClientId INT,CandidateId BIGINT,ApplicationType VARCHAR(40) DEFAULT 'Application',CurrentStage VARCHAR(100),AgreedCtc DECIMAL(18,2),TermsVersion INT DEFAULT 1,TermsConfirmedAtUtc DATETIME NULL);
CREATE TABLE recruitment_interviews(Id BIGINT PRIMARY KEY,ApplicationId BIGINT,Status VARCHAR(30),Result VARCHAR(30),ScheduledStart DATETIME,OverallScore DECIMAL(8,2),RoundConfigurationId BIGINT NULL);
CREATE TABLE recruitment_offers(Id BIGINT PRIMARY KEY,ApplicationId BIGINT,Status VARCHAR(30),UpdatedAt DATETIME);
CREATE TABLE recruitment_templates(Id BIGINT PRIMARY KEY,ClientId INT,TemplateType VARCHAR(30),SubjectTemplate TEXT,BodyTemplate TEXT,IsActive BOOLEAN);
CREATE TABLE recruitment_pipeline_stages(Id BIGINT PRIMARY KEY,StageName VARCHAR(100),StageType VARCHAR(30),PipelineVersionId BIGINT,IsActive BOOLEAN,DisplayOrder INT,ApprovalWorkflowId INT NULL);
CREATE TABLE recruitment_process_documents(Id BIGINT PRIMARY KEY,ClientId INT,HiringCaseId BIGINT,ApplicationId BIGINT NULL,InterviewId BIGINT NULL,PipelineStageId BIGINT,DocumentType VARCHAR(30),VersionNumber INT,TemplateId BIGINT NULL,Status VARCHAR(30),TermsVersion INT NULL,BodySnapshot TEXT NULL,AttachmentPublicId VARCHAR(36) NULL,WorkflowInstanceId BIGINT NULL,CreatedAtUtc DATETIME DEFAULT CURRENT_TIMESTAMP,UpdatedAtUtc DATETIME DEFAULT CURRENT_TIMESTAMP);
CREATE TABLE recruitment_stage_process_document_requirements(PipelineStageId BIGINT,DocumentType VARCHAR(30),TemplateId BIGINT NULL);
CREATE TABLE recruitment_interview_panel_members(InterviewId BIGINT,PanelUserId INT,PanelRole VARCHAR(30));
CREATE TABLE recruitment_interview_feedback(Id BIGINT PRIMARY KEY,InterviewId BIGINT,PanelUserId INT);
CREATE TABLE recruitment_stage_default_panel_members(PipelineStageId BIGINT,PanelUserId INT,PanelRole VARCHAR(30),IsRequired BOOLEAN);
CREATE TABLE recruitment_interview_stage_competencies(Id BIGINT PRIMARY KEY,InterviewStageConfigurationId BIGINT,CompetencyId BIGINT,WeightPercent DECIMAL(8,2),DisplayOrder INT);
CREATE TABLE recruitment_interview_competency_definitions(Id BIGINT PRIMARY KEY,CompetencyName VARCHAR(100));
CREATE TABLE recruitment_interview_feedback_competency_scores(InterviewFeedbackId BIGINT,InterviewStageCompetencyId BIGINT,WeightedScore DECIMAL(8,2));
CREATE TABLE attachment_field_configurations(id BIGINT PRIMARY KEY,attachment_attribute_id BIGINT,is_active BOOLEAN,module_code VARCHAR(40),form_code VARCHAR(40),client_id INT,effective_from_utc DATETIME NULL,effective_until_utc DATETIME NULL,display_order INT);
CREATE TABLE attachment_attributes(id BIGINT PRIMARY KEY,is_active BOOLEAN);
CREATE TABLE recruitment_audit(Id BIGINT PRIMARY KEY AUTO_INCREMENT,EntityType VARCHAR(100),EntityId BIGINT,Action VARCHAR(100),NewValueJson LONGTEXT);
CREATE TABLE recruitment_position_stage_events(PositionPipelineInstanceId BIGINT,EventType VARCHAR(100),CreatedAtUtc DATETIME);
CREATE TABLE recruitment_process_document_signatures(Id BIGINT PRIMARY KEY,ProcessDocumentId BIGINT,SignerUserId INT,CandidateId BIGINT NULL);
CREATE TABLE workflowmasters(Id INT PRIMARY KEY,ClientId INT,IsActive BOOLEAN);
CREATE TABLE workflowinstances(Id BIGINT PRIMARY KEY,ResourceType VARCHAR(100),ResourceId VARCHAR(100),Status VARCHAR(30));
INSERT INTO clients VALUES(20,'Client A'),(30,'Client B');
INSERT INTO authusers(Id,DisplayName,Email,ClientId) VALUES(1,'Coordinator','coordinator@example.test',20),(2,'First Panel','first@example.test',20),(3,'Second Panel','second@example.test',20),(4,'Observer','observer@example.test',20);
INSERT INTO recruitment_open_positions(Id,PositionTitle,NumberOfPositions) VALUES(10,'Engineer',1),(11,'Other job',1);
INSERT INTO recruitment_work_orders VALUES(1,'WO-1',UTC_TIMESTAMP());
INSERT INTO recruitment_work_order_lines VALUES(1,'Engineer','','Delhi','');
INSERT INTO recruitment_position_pipeline_instances VALUES(5,10,20,1,1,1,'Active');
INSERT INTO recruitment_pipeline_stages VALUES(1,'Signing of MOM','Approval',1,TRUE,1,NULL);
INSERT INTO recruitment_process_documents(Id,ClientId,HiringCaseId,PipelineStageId,DocumentType,VersionNumber,Status) VALUES(1,20,5,1,'MOM',1,'Draft');
INSERT INTO recruitment_stage_process_document_requirements VALUES(1,'MOM',NULL);
INSERT INTO recruitment_candidates VALUES(1,'Selected','Candidate','one@example.test'),(2,'Other','Candidate','other@example.test');
INSERT INTO recruitment_candidate_applications(Id,PositionId,ClientId,CandidateId,CurrentStage,AgreedCtc,TermsConfirmedAtUtc) VALUES
(100,10,20,1,'Selection & HR Review',1000000,UTC_TIMESTAMP()),(101,11,20,2,'Selection & HR Review',1000000,UTC_TIMESTAMP()),
(102,10,30,2,'Selection & HR Review',1000000,UTC_TIMESTAMP()),(103,10,20,2,'Withdrawn',1000000,UTC_TIMESTAMP()),(104,10,20,2,'Interview',1000000,UTC_TIMESTAMP());
INSERT INTO recruitment_interviews VALUES(1,100,'Completed','Selected',UTC_TIMESTAMP(),90,NULL),(2,101,'Completed','Selected',UTC_TIMESTAMP(),90,NULL),(3,102,'Completed','Selected',UTC_TIMESTAMP(),90,NULL),(4,103,'Completed','Selected',UTC_TIMESTAMP(),90,NULL),(5,104,'Completed','Selected',UTC_TIMESTAMP(),90,NULL),(6,104,'Scheduled','Pending',UTC_TIMESTAMP(),0,NULL);
INSERT INTO recruitment_interview_panel_members VALUES(1,2,'Chair'),(1,3,'Panelist');
INSERT INTO recruitment_interview_feedback VALUES(1,1,2),(2,1,3);
""";
}
