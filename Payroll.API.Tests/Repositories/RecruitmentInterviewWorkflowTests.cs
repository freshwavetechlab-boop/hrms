using Dapper;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Repositories;

public sealed partial class RecruitmentRevisionRegressionTests
{
    [Fact]
    public void Final_decision_is_assigned_not_inferred_from_panel_recommendation()
    {
        var panel = new AuthUser { Id = 4, ClientId = 20, Permissions = ["recruitment.interview.panel"] };
        var hr = new AuthUser { Id = 5, ClientId = 20, Permissions = ["recruitment.interview.schedule"] };
        Assert.True(RecruitmentTalentRepository.CanRecordInterviewDecision(panel, 4));
        Assert.False(RecruitmentTalentRepository.CanRecordInterviewDecision(panel, 5));
        Assert.False(RecruitmentTalentRepository.CanRecordInterviewDecision(panel, null));
        Assert.False(RecruitmentTalentRepository.CanRecordInterviewDecision(hr, 4));
        Assert.True(RecruitmentTalentRepository.CanRecordInterviewDecision(hr, null));
        Assert.True(RecruitmentTalentRepository.CanRecordInterviewDecision(new AuthUser { Id = 1, Roles = ["super_admin"] }, 4));
        Assert.False(RecruitmentTalentRepository.CanRecordInterviewDecision(new AuthUser { Id = 1, ClientId = 30, Roles = ["super_admin"] }, 4));
        Assert.False(RecruitmentTalentRepository.CanRecordInterviewDecision(new AuthUser { Id = 4 }, 4));
    }

    [Theory]
    [InlineData(4, 1, false)]
    [InlineData(4, 4, true)]
    [InlineData(4, 2, false)]
    [InlineData(4, 0, false)]
    [InlineData(1, 1, true)]
    [InlineData(0, 0, false)]
    public void Vacancy_gate_requires_sufficient_continuing_candidates(int required, int continuing, bool expected) =>
        Assert.Equal(expected, RecruitmentCaseRepository.HasRequiredHiringCohort(required, continuing));

    [LocalInterviewDatabaseFact]
    public async Task Interview_queue_uses_current_round_and_counts_do_not_duplicate_rounds()
    {
        await using var fixture = await Database.CreateAsync(); var db = fixture.Db;
        await db.ExecuteAsync(InterviewObservationSchema);
        async Task<bool> Ready() => await db.ExecuteScalarAsync<bool>("SELECT " + RecruitmentTalentRepository.InterviewReadySql + " FROM recruitment_candidate_applications a WHERE a.Id=100");
        Assert.False(await Ready()); // Selection & HR Review, completed interview.
        await db.ExecuteAsync("UPDATE recruitment_application_stage_instances SET PipelineStageId=4 WHERE Id=51");
        Assert.False(await Ready()); // Same round remains completed, despite renamed stage.
        await db.ExecuteAsync("INSERT INTO recruitment_application_stage_instances VALUES(52,4,'Active'); UPDATE recruitment_application_pipeline_instances SET CurrentStageInstanceId=52");
        Assert.True(await Ready()); // A genuinely configured next round is schedulable.
        await db.ExecuteAsync("INSERT INTO recruitment_interviews VALUES(2,100,'Scheduled','Pending',52)");
        Assert.False(await Ready());
        var counts = await db.QuerySingleAsync<RecruitmentOpenPosition>("SELECT " + RecruitmentHiringProgress.SelectFor("p") + " p.Id FROM recruitment_open_positions p WHERE p.Id=10");
        Assert.Equal(4, counts.RequiredCandidateCount); Assert.Equal(1, counts.InPanelCount); Assert.Equal(0, counts.SelectedCandidateCount);
        await db.ExecuteAsync("UPDATE recruitment_interviews SET Status='Completed',Result='Selected' WHERE Id=2");
        counts = await db.QuerySingleAsync<RecruitmentOpenPosition>("SELECT " + RecruitmentHiringProgress.SelectFor("p") + " p.Id FROM recruitment_open_positions p WHERE p.Id=10");
        Assert.Equal(0, counts.InPanelCount); Assert.Equal(1, counts.SelectedCandidateCount);
        await db.ExecuteAsync("INSERT INTO recruitment_offers VALUES(1,100,'Rejected',UTC_TIMESTAMP())");
        counts = await db.QuerySingleAsync<RecruitmentOpenPosition>("SELECT " + RecruitmentHiringProgress.SelectFor("p") + " p.Id FROM recruitment_open_positions p WHERE p.Id=10");
        Assert.Equal(0, counts.SelectedCandidateCount);
    }

    [LocalInterviewDatabaseFact]
    public async Task One_of_four_returns_job_to_sourcing_without_rewriting_candidate_or_signed_document()
    {
        await using var fixture = await Database.CreateAsync(); var db = fixture.Db;
        await db.ExecuteAsync(InterviewObservationSchema);
        var repository = new RecruitmentCaseRepository(fixture.Config, null!, null!, new WorkflowRepository(fixture.Config));
        var user = new AuthUser { Id = 1, ClientId = 20 };
        Assert.Empty(await repository.AdvanceHiringCaseAutomationAsync(5, user));
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT si.PipelineStageId FROM recruitment_position_pipeline_instances pi JOIN recruitment_position_stage_instances si ON si.Id=pi.CurrentStageInstanceId WHERE pi.Id=5"));
        Assert.Equal("Selection & HR Review", await db.ExecuteScalarAsync<string>("SELECT CurrentStage FROM recruitment_candidate_applications WHERE Id=100"));
        Assert.Equal("Selected", await db.ExecuteScalarAsync<string>("SELECT Result FROM recruitment_interviews WHERE Id=1"));
        Assert.Equal("Signed", await db.ExecuteScalarAsync<string>("SELECT Status FROM recruitment_process_documents WHERE Id=1"));
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_process_documents document WHERE " + RecruitmentCaseRepository.CurrentCohortDocumentSql)); // Old signature remains evidence, not authorization for the replacement cohort.
        Assert.Equal("VACANCY_SHORTFALL", await db.ExecuteScalarAsync<string>("SELECT OutcomeCode FROM recruitment_position_stage_instances WHERE Id=61"));
        Assert.Equal("Superseded", await db.ExecuteScalarAsync<string>("SELECT Status FROM recruitment_hiring_case_advance_requests"));
        Assert.Empty(await repository.AdvanceHiringCaseAutomationAsync(5, user));
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_position_stage_events")); // no clock resets on repeat.
    }

    [LocalInterviewDatabaseFact]
    public async Task Rejection_no_show_and_offer_rejection_reopen_sourcing_for_shortfall()
    {
        await using var fixture = await Database.CreateAsync(); var db = fixture.Db;
        await db.ExecuteAsync(InterviewObservationSchema);
        await db.ExecuteAsync("UPDATE recruitment_open_positions SET NumberOfPositions=1; UPDATE recruitment_process_documents SET Status='Draft'");
        var repository = new RecruitmentCaseRepository(fixture.Config, null!, null!, new WorkflowRepository(fixture.Config));
        var user = new AuthUser { Id = 1, ClientId = 20 };
        Assert.Empty(await repository.AdvanceHiringCaseAutomationAsync(5, user));
        Assert.Equal(61, await db.ExecuteScalarAsync<int>("SELECT CurrentStageInstanceId FROM recruitment_position_pipeline_instances")); // Enough candidates; unsigned MoM still blocks.
        foreach (var failure in new[] { "Rejected", "No Show", "Offer Rejected" })
        {
            await db.ExecuteAsync("UPDATE recruitment_position_stage_instances SET Status='Active' WHERE Id=61; UPDATE recruitment_position_pipeline_instances SET CurrentStageInstanceId=61;");
            await db.ExecuteAsync("UPDATE recruitment_interviews SET Status=@Status,Result=@Result WHERE Id=1", new { Status = failure == "No Show" ? "No Show" : "Completed", Result = failure == "Rejected" ? "Rejected" : "Selected" });
            if (failure == "Offer Rejected") await db.ExecuteAsync("INSERT INTO recruitment_offers VALUES(1,100,'Rejected',UTC_TIMESTAMP())");
            Assert.Empty(await repository.AdvanceHiringCaseAutomationAsync(5, user));
            Assert.NotEqual(61, await db.ExecuteScalarAsync<int>("SELECT CurrentStageInstanceId FROM recruitment_position_pipeline_instances"));
        }
    }

    private const string InterviewObservationSchema = """
CREATE TABLE recruitment_open_positions(Id BIGINT PRIMARY KEY,NumberOfPositions INT,CancelledPositions INT DEFAULT 0,OnHoldPositions INT DEFAULT 0);
CREATE TABLE recruitment_candidate_applications(Id BIGINT PRIMARY KEY,PositionId BIGINT,ClientId INT,JobPostingId BIGINT NULL,ApplicationType VARCHAR(40) DEFAULT 'Application',CurrentStage VARCHAR(100),JoinedEmployeeId INT NULL);
CREATE TABLE recruitment_pipeline_versions(Id BIGINT PRIMARY KEY,SlaMode VARCHAR(40));
CREATE TABLE recruitment_pipeline_stages(Id BIGINT PRIMARY KEY,PipelineVersionId BIGINT DEFAULT 1,StageCode VARCHAR(80),StageName VARCHAR(100),StageType VARCHAR(40),CardScope VARCHAR(40),DisplayOrder INT,SlaDurationMinutes INT DEFAULT 60,TargetOffsetMinutes INT NULL,IsInitial BOOLEAN DEFAULT FALSE,IsTerminal BOOLEAN DEFAULT FALSE,IsActive BOOLEAN DEFAULT TRUE,RequiresApproval BOOLEAN DEFAULT FALSE);
CREATE TABLE recruitment_application_pipeline_instances(Id BIGINT PRIMARY KEY,ApplicationId BIGINT,CurrentStageInstanceId BIGINT,Status VARCHAR(40) DEFAULT 'Active');
CREATE TABLE recruitment_application_stage_instances(Id BIGINT PRIMARY KEY,PipelineStageId BIGINT,Status VARCHAR(40));
CREATE TABLE recruitment_position_pipeline_instances(Id BIGINT PRIMARY KEY,PositionId BIGINT,ClientId INT,PipelineVersionId BIGINT,CurrentStageInstanceId BIGINT,Status VARCHAR(40),SlaAnchorAtUtc DATETIME,CompletedAtUtc DATETIME NULL);
CREATE TABLE recruitment_position_stage_instances(Id BIGINT PRIMARY KEY AUTO_INCREMENT,PositionPipelineInstanceId BIGINT,PipelineStageId BIGINT,Status VARCHAR(40),OutcomeCode VARCHAR(80) DEFAULT '',EnteredAtUtc DATETIME NULL,DueAtUtc DATETIME NULL,CompletedAtUtc DATETIME NULL,PausedDurationSeconds BIGINT DEFAULT 0);
CREATE TABLE recruitment_position_stage_pause_periods(Id BIGINT PRIMARY KEY,PositionStageInstanceId BIGINT,PausedAtUtc DATETIME,ResumedAtUtc DATETIME NULL,ResumedByUserId INT NULL,DurationSeconds BIGINT DEFAULT 0);
CREATE TABLE recruitment_position_stage_events(Id BIGINT PRIMARY KEY AUTO_INCREMENT,PositionPipelineInstanceId BIGINT,PositionStageInstanceId BIGINT,EventType VARCHAR(80),EventTitle VARCHAR(200),EventDetails TEXT,ActorUserId INT,CreatedAtUtc DATETIME(6) DEFAULT CURRENT_TIMESTAMP(6));
CREATE TABLE recruitment_hiring_case_advance_requests(HiringCaseId BIGINT,PositionStageInstanceId BIGINT,Status VARCHAR(40),DecidedAtUtc DATETIME NULL);
CREATE TABLE recruitment_interviews(Id BIGINT PRIMARY KEY,ApplicationId BIGINT,Status VARCHAR(40),Result VARCHAR(40),PipelineStageInstanceId BIGINT);
CREATE TABLE recruitment_interview_stage_configurations(Id BIGINT PRIMARY KEY,PipelineStageId BIGINT);
CREATE TABLE recruitment_offers(Id BIGINT PRIMARY KEY,ApplicationId BIGINT,Status VARCHAR(40),UpdatedAt DATETIME);
CREATE TABLE recruitment_stage_process_document_requirements(PipelineStageId BIGINT,IsRequired BOOLEAN,DocumentType VARCHAR(40));
CREATE TABLE recruitment_process_documents(Id BIGINT PRIMARY KEY,HiringCaseId BIGINT,PipelineStageId BIGINT,DocumentType VARCHAR(40),Status VARCHAR(40),CreatedAtUtc DATETIME(6) DEFAULT '2026-09-01');
INSERT INTO recruitment_open_positions(Id,NumberOfPositions) VALUES(10,4);
INSERT INTO recruitment_candidate_applications(Id,PositionId,ClientId,CurrentStage) VALUES(100,10,20,'Selection & HR Review');
INSERT INTO recruitment_pipeline_versions VALUES(1,'CumulativeFromAnchor');
INSERT INTO recruitment_pipeline_stages(Id,StageCode,StageName,StageType,CardScope,DisplayOrder,TargetOffsetMinutes) VALUES(1,'SHARING_PROFILES','Sharing of Profiles for Interview','Screening','Position',1,60),(2,'SIGNING_MOM','Signing of MOM','Approval','Position',3,180),(3,'HR_REVIEW','Selection & HR Review','HR','Application',6,NULL),(4,'INTERVIEW','Interview / Panel Assessment','Interview','Application',5,NULL);
INSERT INTO recruitment_application_pipeline_instances(Id,ApplicationId,CurrentStageInstanceId) VALUES(1,100,51);
INSERT INTO recruitment_application_stage_instances VALUES(51,3,'Active');
INSERT INTO recruitment_interview_stage_configurations VALUES(1,4);
INSERT INTO recruitment_interviews VALUES(1,100,'Completed','Selected',51);
INSERT INTO recruitment_position_pipeline_instances VALUES(5,10,20,1,61,'Active','2026-09-01',NULL);
INSERT INTO recruitment_position_stage_instances(Id,PositionPipelineInstanceId,PipelineStageId,Status) VALUES(61,5,2,'Active');
INSERT INTO recruitment_hiring_case_advance_requests VALUES(5,61,'Pending Approval',NULL);
INSERT INTO recruitment_stage_process_document_requirements VALUES(2,TRUE,'MOM');
INSERT INTO recruitment_process_documents(Id,HiringCaseId,PipelineStageId,DocumentType,Status) VALUES(1,5,2,'MOM','Signed');
""";
}
