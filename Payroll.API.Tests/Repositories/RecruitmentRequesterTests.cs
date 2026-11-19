using Dapper;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Repositories;

public sealed class RecruitmentRequesterTests
{
    private static AuthUser Manager() => new() { Id = 1, IsActive = true, ClientAccessMode = "Selected", AssignedClientIds = [20, 55], Permissions = ["recruitment.manage"] };
    private static SaveRecruitmentRequisition Draft(int clientId = 55, int? requesterId = 3337) => new()
    {
        ClientId = clientId, RequesterUserId = requesterId, PositionTitle = "Synthetic research role", Department = "Project Cell", NumberOfOpenings = 1,
        HiringType = "", PositionCategory = "", ExperienceRange = "", EmploymentType = "", WorkMode = "Office", Currency = "INR",
    };

    [Fact]
    public async Task LookupRejectsUnassignedOrUnprivilegedActorsBeforeDatabaseAccess()
    {
        var repository = new RecruitmentRepository(new ConfigurationBuilder().Build());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => repository.GetRequestersAsync(99, Manager()));
        var approver = Manager(); approver.Permissions = ["recruitment.rfr.view", "workflow.approve"];
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => repository.GetRequestersAsync(55, approver));
    }

    [LocalInterviewDatabaseFact]
    public async Task OptionsUseAuthorizedUsersEvenWithoutEmployeeLinksAndNeverFallbackToEmployees()
    {
        await using var f = await RecruitmentPipelineCloneTests.Database.CreateAsync(); await PrepareAsync(f);
        var repository = new RecruitmentRepository(f.Config);
        await f.Db.ExecuteAsync("INSERT INTO employees(Id,ClientId,EmployeeCode,FirstName,LastName,IsActive) VALUES(51,55,'CAN-STAFF','Synthetic','Staff',TRUE),(52,20,'UIDAI-STAFF','Legacy','Staff',TRUE)");
        var rows = await repository.GetRequestersAsync(55, Manager());
        Assert.Equal(3337, Assert.Single(rows).UserId); Assert.Equal("ICMR JD Approver", rows[0].DisplayName); Assert.Equal(55, rows[0].ClientId);
        Assert.Null(await f.Db.ExecuteScalarAsync<int?>("SELECT EmployeeId FROM authusers WHERE Id=3337"));
        Assert.Empty(await repository.GetRequestersAsync(20, Manager())); // A sole employee is not an employer user.
        await f.Db.ExecuteAsync("DROP TABLE employees");
        Assert.Equal(3337, Assert.Single(await repository.GetRequestersAsync(55, Manager())).UserId);
        await f.Db.ExecuteAsync("UPDATE authusers SET IsActive=FALSE WHERE Id=3337");
        Assert.Empty(await repository.GetRequestersAsync(55, Manager())); // No employee fallback after deactivation.
    }

    [LocalInterviewDatabaseFact]
    public async Task AccountAssignmentsAndRoleClientsAreBothRequiredAndNonManagersSeeOnlySelf()
    {
        await using var f = await RecruitmentPipelineCloneTests.Database.CreateAsync(); await PrepareAsync(f);
        await f.Db.ExecuteAsync("""
INSERT INTO authusers(Id,ClientId,Email,DisplayName,IsActive,ClientAccessMode) VALUES
(4001,55,'wrong-role@example.invalid','Wrong role',TRUE,'Legacy'),
(4002,55,'wrong-assignment@example.invalid','Wrong assignment',TRUE,'Selected'),
(4003,99,'foreign@example.invalid','Foreign client',TRUE,'Legacy'),
(4004,20,'selected@example.invalid','Assigned hiring user',TRUE,'Selected'),
(4005,NULL,'global@example.invalid','Global administrator',TRUE,'Legacy');
INSERT INTO authuserroles VALUES(4001,3),(4002,4),(4003,4),(4004,4),(4005,5);
INSERT INTO authuser_clients VALUES(4002,20),(4004,55);
""");
        var repository = new RecruitmentRepository(f.Config); var rows = await repository.GetRequestersAsync(55, Manager());
        Assert.Equal(new[] { 3337, 4004 }, rows.Select(row => row.UserId).Order().ToArray());
        Assert.False(await RecruitmentRepository.IsRequesterUserSelectionAllowedAsync(f.Db, Manager(), 55, 4001));
        Assert.False(await RecruitmentRepository.IsRequesterUserSelectionAllowedAsync(f.Db, Manager(), 55, 4002));
        Assert.False(await RecruitmentRepository.IsRequesterUserSelectionAllowedAsync(f.Db, Manager(), 55, 4003));
        Assert.False(await RecruitmentRepository.IsRequesterUserSelectionAllowedAsync(f.Db, Manager(), 55, 4005));
        var self = Manager(); self.Id = 4004; self.Permissions = ["recruitment.rfr.create"];
        Assert.Equal(4004, Assert.Single(await repository.GetRequestersAsync(55, self)).UserId);
        Assert.False(await RecruitmentRepository.IsRequesterUserSelectionAllowedAsync(f.Db, self, 55, 3337));
    }

    [LocalInterviewDatabaseFact]
    public async Task SavingAUserBackedRequestKeepsCreatorSeparateAndDoesNotCreateAnyEmployees()
    {
        await using var f = await RecruitmentPipelineCloneTests.Database.CreateAsync(); await PrepareAsync(f);
        var repository = new RecruitmentRepository(f.Config);
        Assert.Equal(0, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employees"));
        var (row, error) = await repository.SaveDraftAsync(Draft(), Manager());
        Assert.Empty(error); Assert.NotNull(row); Assert.Equal(3337, row.RequesterUserId); Assert.Equal(1, row.RequestedByUserId);
        Assert.Equal(0, row.RequestedByEmployeeId); Assert.Equal("ICMR JD Approver", row.RequestedByName);
        Assert.Equal(0, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employees"));
        var selectedUser = new AuthUser { Id = 3337, ClientId = 55, IsActive = true, Permissions = ["recruitment.rfr.view", "workflow.approve"] };
        Assert.Equal(row.Id, Assert.Single(await repository.GetMineAsync(selectedUser)).Id);
        Assert.NotNull(await repository.GetAsync(row.Id, selectedUser));
        Assert.Equal(1, (await repository.DashboardAsync(selectedUser, true)).Drafts);
        var zeroEmployee = new AuthUser { Id = 9999, ClientId = 55, EmployeeId = 0, IsActive = true, Permissions = ["recruitment.rfr.view"] };
        Assert.Empty(await repository.GetMineAsync(zeroEmployee));
        Assert.Equal(0, (await repository.DashboardAsync(zeroEmployee, true)).Drafts);
        // Being selected does not turn an approver into a request creator.
        Assert.False(RecruitmentRepository.HasRecruitmentCreateAccess(selectedUser));
        var (approvalOnly, approvalOnlyError) = await repository.SaveDraftAsync(Draft(), selectedUser);
        Assert.Null(approvalOnly); Assert.Contains("does not allow hiring request creation", approvalOnlyError);
        var (denied, deniedError) = await repository.SaveDraftAsync(Draft(requesterId: 1), Manager());
        Assert.Null(denied); Assert.Contains("authorized employer user", deniedError);
        var (missing, missingError) = await repository.SaveDraftAsync(Draft(clientId: 20, requesterId: null), Manager());
        Assert.Null(missing); Assert.Contains("Configure a requester user", missingError);
    }

    [LocalInterviewDatabaseFact]
    public async Task ExistingLegacyRequesterAndUserRequesterRemainImmutableEvenIfTheirProfilesAreInactive()
    {
        await using var f = await RecruitmentPipelineCloneTests.Database.CreateAsync(); await PrepareAsync(f);
        var repository = new RecruitmentRepository(f.Config);
        var (created, createError) = await repository.SaveDraftAsync(Draft(), Manager()); Assert.Empty(createError); Assert.NotNull(created);
        await f.Db.ExecuteAsync("UPDATE authusers SET IsActive=FALSE WHERE Id=3337");
        var edit = Draft(); edit.Id = created.Id; edit.PositionTitle = "Revised synthetic role";
        var (saved, error) = await repository.SaveDraftAsync(edit, Manager()); Assert.Empty(error); Assert.NotNull(saved);
        Assert.Equal(3337, saved.RequesterUserId); Assert.Equal(1, saved.RequestedByUserId);
        await f.Db.ExecuteAsync("INSERT INTO employees(Id,ClientId,EmployeeCode,FirstName,LastName,IsActive) VALUES(5174,55,'LEGACY-REQUESTER','Legacy','Requester',FALSE)");
        var legacyId = await f.Db.ExecuteScalarAsync<long>("INSERT INTO recruitment_requisitions(RfrNumber,RequestedByEmployeeId,RequestedByUserId,ClientId,PositionTitle,Department) VALUES('LEGACY',5174,1,55,'Legacy role','Project Cell');SELECT LAST_INSERT_ID();");
        var legacyEdit = Draft(requesterId: null); legacyEdit.Id = legacyId; legacyEdit.RequestedByEmployeeId = 5174;
        var (legacy, legacyError) = await repository.SaveDraftAsync(legacyEdit, Manager()); Assert.Empty(legacyError); Assert.NotNull(legacy);
        Assert.Null(legacy.RequesterUserId); Assert.Equal(5174, legacy.RequestedByEmployeeId); Assert.Equal("Legacy Requester", legacy.RequestedByName);
        legacyEdit.RequesterUserId = 3337;
        var (changed, changedError) = await repository.SaveDraftAsync(legacyEdit, Manager()); Assert.Null(changed); Assert.Contains("cannot be changed", changedError);
        Assert.Equal(1, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employees"));
        Assert.Equal(5174, await f.Db.ExecuteScalarAsync<int>("SELECT RequestedByEmployeeId FROM recruitment_requisitions WHERE Id=@Id", new { Id = legacyId }));
    }

    private static async Task PrepareAsync(RecruitmentPipelineCloneTests.Database f)
    {
        await f.Db.ExecuteAsync("""
INSERT INTO clients VALUES(55,'Fixture ICMR',TRUE);
ALTER TABLE clients ADD Code VARCHAR(40) NULL;
UPDATE clients SET Code=CONCAT('FIX',Id);
ALTER TABLE authusers ADD EmployeeId INT NULL,ADD ClientAccessMode VARCHAR(40) NOT NULL DEFAULT 'Legacy';
ALTER TABLE authroles ADD Id INT PRIMARY KEY AUTO_INCREMENT;
CREATE TABLE authuserroles(UserId INT,RoleId INT);
CREATE TABLE authrolepermissions(RoleId INT,PermissionId INT);
CREATE TABLE authpermissions(Id INT PRIMARY KEY,Code VARCHAR(100));
CREATE TABLE authuser_clients(UserId INT,ClientId INT);
INSERT INTO authpermissions VALUES(1,'recruitment.rfr.view'),(2,'workflow.approve'),(3,'recruitment.rfr.create'),(4,'ess.self'),(5,'settings.manage');
INSERT INTO authroles(Id,Code,ClientId) VALUES(1,'icmr_jd_approver',55),(2,'employee',NULL),(3,'foreign_role',99),(4,'hiring',55),(5,'admin',NULL);
INSERT INTO authrolepermissions VALUES(1,1),(1,2),(2,4),(3,3),(4,3),(5,5);
INSERT INTO authusers(Id,ClientId,Email,DisplayName,IsActive,EmployeeId) VALUES(3337,55,'jd@example.invalid','ICMR JD Approver',TRUE,NULL),(3338,55,'ess@example.invalid','ESS only',TRUE,NULL);
INSERT INTO authuserroles VALUES(3337,1),(3338,2),(1,5);
CREATE TABLE employees(Id INT PRIMARY KEY,ClientId INT,EmployeeCode VARCHAR(100),FirstName VARCHAR(100),LastName VARCHAR(100),Department VARCHAR(100) DEFAULT '',Designation VARCHAR(100) DEFAULT '',IsActive BOOLEAN);
CREATE TABLE worklocations(Id INT PRIMARY KEY,ClientId INT,Name VARCHAR(100),IsActive BOOLEAN);
CREATE TABLE recruitment_work_orders(Id BIGINT PRIMARY KEY,ClientId INT,Status VARCHAR(40),ReceivedAtUtc DATETIME);
CREATE TABLE recruitment_work_order_lines(Id BIGINT PRIMARY KEY,WorkOrderId BIGINT,LineNumber INT,RequisitionId BIGINT NULL,PositionId BIGINT NULL,Status VARCHAR(40));
CREATE TABLE recruitment_job_description_versions(Id BIGINT,RequisitionId BIGINT,VersionNumber INT,Status VARCHAR(40));
CREATE TABLE workflowtasks(Id BIGINT,InstanceId BIGINT,StageId BIGINT,ApproverUserId INT,Status VARCHAR(40));
CREATE TABLE workflowstages(Id BIGINT,Name VARCHAR(100));
CREATE TABLE recruitment_position_pipeline_instances(Id BIGINT,RequisitionId BIGINT,PipelineVersionId BIGINT,CurrentStageInstanceId BIGINT,Status VARCHAR(40),WorkOrderLineId BIGINT,PositionId BIGINT);
CREATE TABLE recruitment_position_stage_instances(Id BIGINT,PipelineStageId BIGINT);
""");
        var ensure = typeof(RecruitmentRepository).GetMethod("EnsureTablesAsync", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        await (Task)ensure.Invoke(null, [f.Db])!;
        // These two additive fields normally come from CaseRepository startup.
        await f.Db.ExecuteAsync("ALTER TABLE recruitment_requisitions ADD WorkOrderId BIGINT NULL,ADD WorkOrderLineNumber INT NULL");
    }
}
