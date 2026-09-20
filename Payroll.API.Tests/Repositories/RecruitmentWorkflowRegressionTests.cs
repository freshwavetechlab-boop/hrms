using Dapper;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Repositories;

public sealed class RecruitmentWorkflowRegressionTests
{
    [Theory]
    [InlineData("SalaryRangeMaximum", true, 1000000, 1, 1100000, 1000000)]
    [InlineData("SalaryRangeMaximum", true, 2000000, 2, 1100000, 1000000)]
    [InlineData("SalaryRangeMaximum", false, 1000000, 1, 1100000, 1100000)]
    [InlineData("ApprovedTotal", true, 2000000, 2, 1100000, 2000000)]
    public void Salary_range_does_not_bypass_the_explicit_approved_budget(string basis, bool available, decimal budget, int openings, decimal salaryMax, decimal ceiling) =>
        Assert.Equal(ceiling, RecruitmentTalentRepository.EffectiveOfferBudgetCeiling(basis, available, budget, openings, salaryMax));

    [Theory]
    [InlineData("SIGNING_MOM", "Approval", false, false)]
    [InlineData("SIGNING_MOM", "Approval", true, false)]
    [InlineData("NEGOTIATION_AND_MOM_TO_HR", "HR", true, true)]
    [InlineData("NEGOTIATION_AND_MOM_TO_HR", "HR", false, false)]
    [InlineData("HR_DIVISION_APPROVAL", "Approval", false, false)]
    [InlineData("HR_DIVISION_APPROVAL", "Approval", true, true)]
    [InlineData("OFFER_ISSUANCE", "Offer", false, true)]
    public void Offer_release_does_not_skip_position_prerequisites(string code, string type, bool approval, bool allowed) =>
        Assert.Equal(allowed, RecruitmentOfferReleaseGate.StageError(code, type, code, approval).Length == 0);

    [Fact]
    public void Only_global_super_admin_can_record_another_panel_members_feedback()
    {
        Assert.False(RecruitmentTalentRepository.CanRecordOtherPanelFeedback(new AuthUser { Id = 1, Permissions = ["settings.manage", "recruitment.interview.schedule"] }));
        Assert.False(RecruitmentTalentRepository.CanRecordOtherPanelFeedback(new AuthUser { Id = 1, ClientId = 20, Roles = ["super_admin"] }));
        Assert.True(RecruitmentTalentRepository.CanRecordOtherPanelFeedback(new AuthUser { Id = 1, Roles = ["super_admin"] }));
    }

    [LocalInterviewDatabaseFact]
    public async Task Two_approvals_remain_pending_until_the_second_assigned_user_approves()
    {
        var connection = new MySqlConnectionStringBuilder(Environment.GetEnvironmentVariable("HRMS_INTERNAL_INTERVIEW_TEST_CONNECTION") ?? throw new InvalidOperationException("Loopback test connection required."));
        Assert.Contains(connection.Server, new[] { "localhost", "127.0.0.1", "::1" });
        var name = "hrms_workflow_test_" + Guid.NewGuid().ToString("N");
        connection.Database = "";
        await using var root = new MySqlConnection(connection.ConnectionString);
        await root.OpenAsync();
        await root.ExecuteAsync($"CREATE DATABASE `{name}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci");
        connection.Database = name;
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = connection.ConnectionString }).Build();
            var workflows = new WorkflowRepository(config);
            await workflows.InitializeAsync();
            await using var db = new MySqlConnection(connection.ConnectionString); await db.OpenAsync();
            await db.ExecuteAsync("CREATE TABLE authusers(Id INT PRIMARY KEY,IsActive BOOLEAN,DisplayName VARCHAR(100)); INSERT INTO authusers VALUES(1,TRUE,'Requester'),(2,TRUE,'First'),(3,TRUE,'Final');");
            var workflow = await workflows.SaveAsync(new SaveWorkflowRequest
            {
                ClientId = 20, Code = "TEST_APPROVAL", Name = "Two approvals", ResourceType = "RegressionFixture", Stages =
                [new() { StageOrder = 1, Name = "First approval", ApproverType = "Specific User", ApproverUserId = 2 },
                 new() { StageOrder = 2, Name = "Final approval", ApproverType = "Specific User", ApproverUserId = 3 }]
            });
            Assert.True(workflow.Row is not null, workflow.Error);
            var instance = await workflows.StartAsync(new StartWorkflowRequest { WorkflowId = workflow.Row.Id, ResourceType = "RegressionFixture", ResourceId = "42", PayloadJson = "{}" }, 1);
            Assert.NotNull(instance);
            var first = Assert.Single(await workflows.PendingAsync(2));
            Assert.False(await workflows.ActionAsync(first.Id, 3, "Approved", "Wrong approver"));
            Assert.True(await workflows.ActionAsync(first.Id, 2, "Approved", "First complete"));
            Assert.Equal("Pending", (await workflows.GetInstanceForTaskAsync(first.Id))!.Status);
            Assert.False(await workflows.ActionAsync(first.Id, 2, "Approved", "Repeated click"));
            var final = Assert.Single(await workflows.PendingAsync(3));
            Assert.Equal(instance.Id, final.InstanceId);
            Assert.True(await workflows.ActionAsync(final.Id, 3, "Approved", "Final complete"));
            Assert.Equal("Approved", (await workflows.GetInstanceForTaskAsync(final.Id))!.Status);
            Assert.Empty(await workflows.PendingAsync(2));
            Assert.Empty(await workflows.PendingAsync(3));

            await db.ExecuteAsync("""
                CREATE TABLE recruitment_requisitions(Id BIGINT PRIMARY KEY,BudgetAvailable BOOLEAN,BudgetApproverUserId INT NULL,BudgetApprovalWorkflowInstanceId BIGINT NULL,BudgetApprovalStatus VARCHAR(30) DEFAULT '',UpdatedAt DATETIME);
                CREATE TABLE recruitment_open_positions(Id BIGINT PRIMARY KEY,RequisitionId BIGINT);
                CREATE TABLE recruitment_candidate_applications(Id BIGINT PRIMARY KEY,PositionId BIGINT,JoinedEmployeeId INT NULL,CurrentStage VARCHAR(100));
                CREATE TABLE recruitment_position_pipeline_instances(Id BIGINT PRIMARY KEY,PositionId BIGINT,CurrentStageInstanceId BIGINT,Status VARCHAR(30),PipelineVersionId BIGINT DEFAULT 0);
                CREATE TABLE recruitment_position_stage_instances(Id BIGINT PRIMARY KEY,PipelineStageId BIGINT,PositionPipelineInstanceId BIGINT,Status VARCHAR(30));
                CREATE TABLE recruitment_pipeline_stages(Id BIGINT PRIMARY KEY,StageCode VARCHAR(80),StageType VARCHAR(80),StageName VARCHAR(100),PipelineVersionId BIGINT DEFAULT 0,CardScope VARCHAR(40),IsActive BOOLEAN DEFAULT TRUE,RequiresApproval BOOLEAN DEFAULT FALSE,DisplayOrder INT DEFAULT 0);
                CREATE TABLE recruitment_stage_process_document_requirements(PipelineStageId BIGINT,IsRequired BOOLEAN);
                CREATE TABLE recruitment_application_pipeline_instances(Id BIGINT PRIMARY KEY,ApplicationId BIGINT,CurrentStageInstanceId BIGINT);
                CREATE TABLE recruitment_application_stage_instances(Id BIGINT PRIMARY KEY,ApplicationId BIGINT,PipelineStageId BIGINT,Status VARCHAR(30));
                CREATE TABLE recruitment_interviews(Id BIGINT PRIMARY KEY,ApplicationId BIGINT,Status VARCHAR(30));
                CREATE TABLE recruitment_offers(Id BIGINT PRIMARY KEY,ApplicationId BIGINT,Status VARCHAR(30));
                INSERT INTO recruitment_requisitions(Id,BudgetAvailable) VALUES(1,TRUE);
                INSERT INTO recruitment_open_positions VALUES(10,1);
                INSERT INTO recruitment_candidate_applications VALUES(100,10,NULL,'New Application / Resume Intake');
                INSERT INTO recruitment_pipeline_stages(Id,StageCode,StageType,StageName) VALUES(1,'SIGNING_MOM','Approval','Signing of MOM'),(2,'NEGOTIATION_AND_MOM_TO_HR','HR','Negotiation'),(3,'OFFER_ISSUANCE','Offer','Offer'),(4,'INTERVIEW_PANEL_ASSESSMENT','Interview','Interview'),(5,'REJECTED','Rejected','Rejected');
                INSERT INTO recruitment_position_stage_instances(Id,PipelineStageId) VALUES(1,1),(2,2),(3,3);
                INSERT INTO recruitment_position_pipeline_instances(Id,PositionId,CurrentStageInstanceId,Status) VALUES(1,10,1,'Active');
                """);
            var recruitment = new RecruitmentRepository(config);
            var budgetRequest = new SaveRecruitmentRequisition { BudgetAvailable = true, BudgetAmount = 1000000, Currency = "INR", NumberOfOpenings = 1, BudgetApproverUserId = 2, PositionTitle = "Synthetic Architect" };
            await using (var tx = await db.BeginTransactionAsync())
            {
                await recruitment.PrepareBudgetApprovalAsync(db, tx, 1, 20, budgetRequest, new AuthUser { Id = 1 }, null, true);
                await tx.CommitAsync();
            }
            Assert.Contains("budget approver", await RecruitmentOfferReleaseGate.ValidateAsync(db, 100));
            var budgetTask = Assert.Single(await workflows.PendingAsync(2));
            Assert.True(await workflows.ActionAsync(budgetTask.Id, 2, "Approved", "Budget approved"));
            await recruitment.SyncBudgetApprovalAsync(1, budgetTask.InstanceId, "Approved");
            Assert.Contains("MoM", await RecruitmentOfferReleaseGate.ValidateAsync(db, 100));
            await db.ExecuteAsync("UPDATE recruitment_position_pipeline_instances SET CurrentStageInstanceId=2 WHERE Id=1");
            Assert.Empty(await RecruitmentOfferReleaseGate.ValidateAsync(db, 100, submittingForApproval: true));
            Assert.NotEmpty(await RecruitmentOfferReleaseGate.ValidateAsync(db, 100));
            await db.ExecuteAsync("UPDATE recruitment_position_pipeline_instances SET CurrentStageInstanceId=3 WHERE Id=1");
            Assert.Empty(await RecruitmentOfferReleaseGate.ValidateAsync(db, 100));
            await db.ExecuteAsync("UPDATE recruitment_pipeline_stages SET CardScope='Position',DisplayOrder=Id WHERE Id<=3");
            Assert.Contains("Signing of MOM", await RecruitmentOfferReleaseGate.ValidateAsync(db, 100));
            await db.ExecuteAsync("UPDATE recruitment_position_stage_instances SET PositionPipelineInstanceId=1,Status='Completed' WHERE Id IN (1,2)");
            Assert.Empty(await RecruitmentOfferReleaseGate.ValidateAsync(db, 100));
            await db.ExecuteAsync("UPDATE recruitment_position_pipeline_instances SET Status='Cancelled' WHERE Id=1");
            Assert.Contains("not active", await RecruitmentOfferReleaseGate.ValidateAsync(db, 100));
            await db.ExecuteAsync("UPDATE recruitment_position_pipeline_instances SET Status='Active' WHERE Id=1");
            await using (var tx = await db.BeginTransactionAsync())
            {
                budgetRequest.BudgetAmount = 1200000;
                await recruitment.PrepareBudgetApprovalAsync(db, tx, 1, 20, budgetRequest, new AuthUser { Id = 1 }, null, true);
                await tx.CommitAsync();
            }
            await recruitment.SyncBudgetApprovalAsync(1, budgetTask.InstanceId, "Approved");
            Assert.Contains("budget approver", await RecruitmentOfferReleaseGate.ValidateAsync(db, 100));

            async Task<bool> PoolAllowed() => await db.ExecuteScalarAsync<bool>("SELECT " + RecruitmentTalentRepository.GlobalPoolEligibilitySql + " FROM recruitment_candidate_applications a WHERE Id=100");
            Assert.True(await PoolAllowed());
            await db.ExecuteAsync("INSERT INTO recruitment_application_stage_instances VALUES(1,100,4,'Active'); INSERT INTO recruitment_application_pipeline_instances VALUES(1,100,1)");
            Assert.False(await PoolAllowed());
            await db.ExecuteAsync("INSERT INTO recruitment_interviews VALUES(1,100,'No Show')");
            Assert.True(await PoolAllowed());
            await db.ExecuteAsync("INSERT INTO recruitment_interviews VALUES(2,100,'Rescheduled')");
            Assert.False(await PoolAllowed());
            await db.ExecuteAsync("UPDATE recruitment_application_stage_instances SET PipelineStageId=5 WHERE Id=1");
            Assert.True(await PoolAllowed());
            await db.ExecuteAsync("UPDATE recruitment_candidate_applications SET JoinedEmployeeId=9 WHERE Id=100");
            Assert.False(await PoolAllowed());
        }
        finally
        {
            // Only the random database created by this test can be removed.
            MySqlConnection.ClearAllPools();
            await root.ExecuteAsync($"DROP DATABASE `{name}`");
        }
    }
}
