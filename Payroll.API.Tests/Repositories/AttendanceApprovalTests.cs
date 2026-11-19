using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;
using Xunit;

namespace Payroll.API.Tests.Repositories;

public sealed class AttendanceApprovalTests
{
    [Theory]
    [InlineData("2026-00")]
    [InlineData("2026-13")]
    [InlineData("2026-1")]
    [InlineData("")]
    public void InvalidPeriodsAreRejected(string month) => Assert.Throws<AttendanceApprovalException>(() => AttendanceApprovalStore.MonthStart(month));

    [Fact]
    public void ClientAndDirectReportAccessCannotBecomeGlobalAccess()
    {
        var pi = User(101, 55, "mss.attendance.manage");
        Assert.True(AttendanceApprovalStore.CanAccess(pi, 55)); Assert.False(AttendanceApprovalStore.CanAccess(pi, 20));
        Assert.False(AttendanceApprovalStore.CanReadAll(pi));
        Assert.True(AttendanceApprovalStore.CanReadAll(User(102, 55, "mss.attendance.client.manage")));
        pi.MustChangePassword = true; Assert.False(AttendanceApprovalStore.CanAccess(pi, 55));
        var selected = User(100, 0, "settings.manage"); selected.ClientId = null; selected.ClientAccessMode = "Selected"; selected.AssignedClientIds = [55];
        Assert.True(AttendanceApprovalStore.CanAccess(selected, 55)); Assert.False(AttendanceApprovalStore.CanAccess(selected, 20));
    }

    [Fact]
    public void AOneStageOrGlobalFallbackWorkflowCannotFinalizeAttendance()
    {
        Assert.Throws<AttendanceApprovalException>(() => AttendanceApprovalStore.ValidateStages([new() { StageOrder = 1, ApproverType = "Reporting Manager" }]));
        Assert.Throws<AttendanceApprovalException>(() => AttendanceApprovalStore.ValidateStages([new() { StageOrder = 1, ApproverType = "Reporting Manager" }, new() { StageOrder = 2, ApproverType = "HR Manager" }]));
        AttendanceApprovalStore.ValidateStages([new() { StageOrder = 1, ApproverType = "Reporting Manager" }, new() { StageOrder = 2, ApproverType = "Specific User", ApproverUserId = 102 }]);
        Assert.Equal(new DateTime(2026, 9, 26), AttendanceApprovalStore.Cycle("2026-10", 26, 25).Start);
        Assert.Equal(new DateTime(2026, 10, 25), AttendanceApprovalStore.Cycle("2026-10", 26, 25).End);
    }

    [Fact]
    public async Task BothSequentialApprovalsAndRosterVerificationAreRequiredAndChangesInvalidateThem()
    {
        await using var f = await Fixture.CreateAsync();
        var pi = User(101, 55, "mss.attendance.manage"); var cell = User(102, 55, "mss.attendance.client.manage");
        var input = new SubmitAttendanceApproval { ClientId = 55, Month = "2026-11", EmployeeIds = [41], ProjectReference = "SYNTHETIC PROJECT A" };
        await Assert.ThrowsAsync<AttendanceApprovalException>(() => AttendanceApprovalStore.RequireApprovedAsync(f.Db, null, 55, "2026-11", [41]));
        // Another client and periods preceding the opt-in policy retain their existing path.
        await AttendanceApprovalStore.RequireApprovedAsync(f.Db, null, 20, "2026-11", [999]);
        await AttendanceApprovalStore.RequireApprovedAsync(f.Db, null, 55, "2026-10", [41]);
        var context = await f.Repository.SubmitAsync(pi, input);
        Assert.Equal("Pending PI", Assert.Single(context.Rows).Status);
        await Assert.ThrowsAsync<AttendanceApprovalException>(() => f.Repository.SubmitAsync(pi, input));
        var task = Assert.Single(await f.Workflows.PendingAsync(101));
        Assert.Empty(await f.Workflows.PendingAsync(102));
        Assert.False(await f.Workflows.ActionAsync(task.Id, 102, "Approved", "Wrong role", true));
        Assert.True(await f.Workflows.ActionAsync(task.Id, 101, "Approved", "PI checked"));
        Assert.Equal("Pending Project Cell", Assert.Single((await f.Repository.ContextAsync(cell, 55, "2026-11")).Rows).Status);
        await Assert.ThrowsAsync<AttendanceApprovalException>(() => AttendanceApprovalStore.RequireApprovedAsync(f.Db, null, 55, "2026-11", [41]));
        task = Assert.Single(await f.Workflows.PendingAsync(102));
        await Assert.ThrowsAsync<AttendanceApprovalException>(() => f.Workflows.ActionAsync(task.Id, 102, "Approved", "Missing roll check"));
        Assert.Single(await f.Workflows.PendingAsync(102));
        Assert.True(await f.Workflows.ActionAsync(task.Id, 102, "Approved", "Project/role and contract verified", true));
        await AttendanceApprovalStore.RequireApprovedAsync(f.Db, null, 55, "2026-11", [41]);
        Assert.Equal("Approved", Assert.Single((await f.Repository.ContextAsync(cell, 55, "2026-11")).Rows).Status);
        Assert.Equal(27m, await f.Db.ExecuteScalarAsync<decimal>("SELECT payable_days FROM employee_monthly_attendance WHERE employee_id=41"));
        await f.Db.ExecuteAsync("UPDATE employee_monthly_attendance SET payable_days=26 WHERE employee_id=41");
        Assert.Equal("Changed — resubmit", Assert.Single((await f.Repository.ContextAsync(cell, 55, "2026-11")).Rows).Status);
        await Assert.ThrowsAsync<AttendanceApprovalException>(() => AttendanceApprovalStore.RequireApprovedAsync(f.Db, null, 55, "2026-11", [41]));
        await f.Repository.SubmitAsync(pi, input);
        task = Assert.Single(await f.Workflows.PendingAsync(101));
        Assert.True(await f.Workflows.ActionAsync(task.Id, 101, "Approved", "Rechecked"));
        task = Assert.Single(await f.Workflows.PendingAsync(102));
        await f.Db.ExecuteAsync("UPDATE employees SET ReportingManagerUserId=103 WHERE Id=41");
        await Assert.ThrowsAsync<AttendanceApprovalException>(() => f.Workflows.ActionAsync(task.Id, 102, "Approved", "Stale PI", true));
        await f.Repository.SubmitAsync(cell, input);
        Assert.Empty(await f.Workflows.PendingAsync(102));
        Assert.Single(await f.Workflows.PendingAsync(103));
        Assert.Equal(1, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM workflowinstances WHERE Status='Superseded'"));
        Assert.Equal(26m, await f.Db.ExecuteScalarAsync<decimal>("SELECT payable_days FROM employee_monthly_attendance WHERE employee_id=41"));
    }

    [Fact]
    public async Task WhatIfAttendanceKeepsNativeValuesWithoutRequiringApproval()
    {
        await using var f = await Fixture.CreateAsync();
        await using var tx = await f.Db.BeginTransactionAsync();
        var reader = typeof(PayRunRepository).GetMethod("GetPayRunAttendanceAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        async Task<PayRunRepository.PayRunAttendance> Read(bool payroll)
            => Assert.Single(await (Task<List<PayRunRepository.PayRunAttendance>>)reader.Invoke(null, [f.Db, tx, 55, "2026-11", 41, payroll])!);
        var whatIf = await Read(false);
        Assert.Equal(25m, whatIf.PresentDays); Assert.Equal(27m, whatIf.PayableDays);
        Assert.True(string.IsNullOrEmpty(whatIf.PolicyError));
        var actual = await Read(true);
        Assert.Equal(whatIf.PayableDays, actual.PayableDays);
        Assert.Contains("PI and Project Cell", actual.PolicyError);
        await Assert.ThrowsAsync<AttendanceApprovalException>(() => AttendanceApprovalStore.RequireApprovedAsync(f.Db, tx, 55, "2026-11", [41]));
        Assert.Equal(0, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM workflowinstances", transaction: tx));
    }

    [Fact]
    public async Task CalendarMonthDaysOutsideADatedCycleAlsoInvalidateApproval()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Db.ExecuteAsync("INSERT INTO attendance_groups VALUES(7,55,TRUE,26,25,''); INSERT INTO attendance_group_employees VALUES(7,41); DELETE FROM employee_monthly_attendance; INSERT INTO employee_daily_attendance VALUES(41,55,'2026-11-30','Present',1,'09:00','18:00',9,'Fixture calendar attendance')");
        var pi = User(101, 55, "mss.attendance.manage");
        var submitted = await f.Repository.SubmitAsync(pi, new() { ClientId = 55, Month = "2026-11", EmployeeIds = [41], ProjectReference = "SYNTHETIC PROJECT A" });
        Assert.Equal("Pending PI", Assert.Single(submitted.Rows).Status);
        await f.Db.ExecuteAsync("UPDATE employee_daily_attendance SET payable_value=0,status='Absent' WHERE employee_id=41 AND attendance_date='2026-11-30'");
        Assert.Equal("Changed — resubmit", Assert.Single((await f.Repository.ContextAsync(pi, 55, "2026-11")).Rows).Status);
        var task = Assert.Single(await f.Workflows.PendingAsync(101));
        await Assert.ThrowsAsync<AttendanceApprovalException>(() => f.Workflows.ActionAsync(task.Id, 101, "Approved", "Stale attendance"));
    }

    [Fact]
    public async Task ScopeRejectionMissingDaysAndForgedGenericStartsDoNotCreateApprovalEvidence()
    {
        await using var f = await Fixture.CreateAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Repository.ContextAsync(User(101, 20, "mss.attendance.manage"), 55, "2026-11"));
        Assert.Empty((await f.Repository.ContextAsync(User(103, 55, "mss.attendance.manage"), 55, "2026-11")).Rows);
        var input = new SubmitAttendanceApproval { ClientId = 55, Month = "2026-11", EmployeeIds = [41], ProjectReference = "Fixture" };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Repository.SubmitAsync(User(103, 55, "mss.attendance.manage"), input));
        await f.Db.ExecuteAsync("DELETE FROM employee_monthly_attendance");
        await Assert.ThrowsAsync<AttendanceApprovalException>(() => f.Repository.SubmitAsync(User(101, 55, "mss.attendance.manage"), input));
        Assert.Null(await f.Workflows.StartAsync(new() { WorkflowId = 1, ResourceType = AttendanceApprovalStore.ResourceType, ResourceId = "55:41:2026-11", PayloadJson = "{}" }, 101));
        Assert.Null(await f.Workflows.StartAsync(new() { WorkflowId = 1, ResourceType = "attendanceperiod", ResourceId = "55:41:2026-11", PayloadJson = "{}" }, 101));
        Assert.Null(await f.Workflows.StartAsync(new() { WorkflowId = 1, ResourceType = "CustomAlias", ResourceId = "55:41:2026-11", PayloadJson = "{}" }, 101));
        Assert.Equal(0, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM workflowinstances"));
    }

    private static AuthUser User(int id, int client, params string[] permissions) => new() { Id = id, ClientId = client, IsActive = true, Permissions = [.. permissions] };

    [Fact]
    public async Task ExistingPayrollCannotBeSubmittedOrApprovedBeforeBothAttendanceStages()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Db.ExecuteAsync("INSERT INTO payruns VALUES(901,55,'2026-11','Draft','Regular'); INSERT INTO payrunemployees VALUES(901,41,FALSE)");
        var payroll = new PayRunRepository(f.Config, new TaxEngineRepository(f.Config));
        await Assert.ThrowsAsync<AttendanceApprovalException>(() => payroll.SubmitForApprovalAsync(901));
        Assert.Equal("Draft", await f.Db.ExecuteScalarAsync<string>("SELECT Status FROM payruns WHERE Id=901"));
        await f.Db.ExecuteAsync("UPDATE payruns SET Status='Pending Approval' WHERE Id=901");
        await Assert.ThrowsAsync<AttendanceApprovalException>(() => payroll.ApproveAsync(901));
        Assert.Equal("Pending Approval", await f.Db.ExecuteScalarAsync<string>("SELECT Status FROM payruns WHERE Id=901"));
    }

    [Fact]
    public async Task PolicyEnrollmentChangesOnlyUnassignedClientPiAndPreservesAttendanceAndOtherSettings()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Db.ExecuteAsync("UPDATE employees SET ReportingManagerUserId=NULL WHERE Id=41; UPDATE modulesettings SET SettingsJson=JSON_SET(SettingsJson,'$.preserved',42)");
        var admin = User(100, 55, "settings.manage"); admin.Email = "fixture-admin";
        var request = new ConfigureAttendanceApproval { ClientId = 55, Enabled = true, WorkflowId = 1, EffectiveFromMonth = "2026-11", AssignUnassignedEmployees = true, DefaultPiUserId = 101 };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Repository.ConfigureAsync(User(102, 55, "mss.attendance.client.manage"), request));
        await f.Repository.ConfigureAsync(admin, request);
        Assert.Equal(101, await f.Db.ExecuteScalarAsync<int>("SELECT ReportingManagerUserId FROM employees WHERE Id=41"));
        Assert.Equal(42, await f.Db.ExecuteScalarAsync<int>("SELECT JSON_EXTRACT(SettingsJson,'$.preserved') FROM modulesettings WHERE client_id=55 AND ModuleCode='leave_attendance'"));
        Assert.True((await f.Repository.ContextAsync(admin, 55, "2026-11")).Enabled);
        Assert.Equal(27m, await f.Db.ExecuteScalarAsync<decimal>("SELECT payable_days FROM employee_monthly_attendance"));
        Assert.Equal(1, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employee_audit_trail"));
        await f.Repository.ConfigureAsync(admin, request);
        Assert.Equal(1, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employee_audit_trail"));
    }

    private sealed class Fixture(MySqlConnection root, MySqlConnection db, string name, IConfiguration config) : IAsyncDisposable
    {
        internal MySqlConnection Db => db;
        internal IConfiguration Config => config;
        internal WorkflowRepository Workflows { get; } = new(config);
        internal AttendanceApprovalRepository Repository { get; } = new(config);
        internal static async Task<Fixture> CreateAsync()
        {
            var target = new MySqlConnectionStringBuilder(Environment.GetEnvironmentVariable("FREVO_ICMR_APPROVAL_TEST_DB") ?? throw new InvalidOperationException("Set an owned loopback MySQL fixture connection."));
            Assert.Contains(target.Server, new[] { "127.0.0.1", "localhost" }); Assert.Equal(33330u, target.Port);
            target.Database = ""; var root = new MySqlConnection(target.ConnectionString); await root.OpenAsync();
            var name = "icmr_approval_test_" + Guid.NewGuid().ToString("N");
            await root.ExecuteAsync($"CREATE DATABASE `{name}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci");
            target.Database = name;
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = target.ConnectionString }).Build();
            var db = new MySqlConnection(target.ConnectionString); await db.OpenAsync();
            var fixture = new Fixture(root, db, name, config);
            await fixture.Workflows.InitializeAsync();
            await db.ExecuteAsync("""
CREATE TABLE clients(Id INT PRIMARY KEY); INSERT INTO clients VALUES(55),(20);
CREATE TABLE authusers(Id INT PRIMARY KEY,ClientId INT,DisplayName VARCHAR(120),IsActive BOOLEAN); INSERT INTO authusers VALUES(101,55,'Fixture PI',TRUE),(102,55,'Fixture Project Cell',TRUE),(103,55,'Other PI',TRUE);
CREATE TABLE authpermissions(Id INT PRIMARY KEY,Code VARCHAR(80)); INSERT INTO authpermissions VALUES(1,'mss.attendance.manage'),(2,'mss.attendance.client.manage');
CREATE TABLE authuserroles(UserId INT,RoleId INT); INSERT INTO authuserroles VALUES(101,1),(102,2),(103,1);
CREATE TABLE authrolepermissions(RoleId INT,PermissionId INT); INSERT INTO authrolepermissions VALUES(1,1),(2,2);
CREATE TABLE modulesettings(client_id INT,ModuleCode VARCHAR(80),SettingsJson JSON,IsEnabled BOOLEAN DEFAULT FALSE,UNIQUE KEY UX_Module(ModuleCode));
INSERT INTO modulesettings(client_id,ModuleCode,SettingsJson) VALUES(55,'attendance_approval_55','{"enabled":true,"workflowId":1,"effectiveFromMonth":"2026-11"}'),(55,'leave_attendance','{}');
CREATE TABLE employees(Id INT PRIMARY KEY,ClientId INT,EmployeeCode VARCHAR(80),FirstName VARCHAR(80),LastName VARCHAR(80),WorkLocationId INT,Department VARCHAR(80),Designation VARCHAR(80),DateOfJoining DATE,IsActive BOOLEAN,ReportingManagerUserId INT);
INSERT INTO employees VALUES(41,55,'ICMR-TEST-41','Synthetic','Person',1,'Fixture project','Technical staff','2026-11-02',TRUE,101);
CREATE TABLE worklocations(Id INT,Name VARCHAR(80)); INSERT INTO worklocations VALUES(1,'Fixture institute');
CREATE TABLE employee_it0000_actions(Id INT,EmployeeId INT,ClientId INT,Status VARCHAR(30),ActionType VARCHAR(30),EffectiveFrom DATE);
CREATE TABLE employee_audit_trail(EmployeeId INT,EmployeeCode VARCHAR(80),ActionType VARCHAR(80),InfotypeCode VARCHAR(30),FieldName VARCHAR(80),OldValue TEXT,NewValue TEXT,EffectiveFrom DATE,ChangedBy VARCHAR(120));
CREATE TABLE attendance_groups(id INT,client_id INT,is_active BOOLEAN,attendance_cycle_start_day INT,attendance_cycle_end_day INT,work_week VARCHAR(80));
CREATE TABLE attendance_group_employees(attendance_group_id INT,employee_id INT);
CREATE TABLE leave_attendance_preferences(client_id INT,work_location_id INT,attendance_cycle_start_day INT,attendance_cycle_end_day INT,work_week VARCHAR(80));
CREATE TABLE employee_monthly_attendance(employee_id INT,client_id INT,attendance_month CHAR(7),working_days DECIMAL(8,2),present_days DECIMAL(8,2),payable_days DECIMAL(8,2),lop_days DECIMAL(8,2),source_type VARCHAR(80),remarks TEXT);
INSERT INTO employee_monthly_attendance VALUES(41,55,'2026-11',30,25,27,3,'Monthly','Fixture');
CREATE TABLE employee_daily_attendance(employee_id INT,client_id INT,attendance_date DATE,status VARCHAR(30),payable_value DECIMAL(8,2),check_in_time TIME,check_out_time TIME,total_hours DECIMAL(8,2),remarks TEXT);
CREATE TABLE payruns(Id INT,ClientId INT,PayPeriod CHAR(7),Status VARCHAR(40),RunType VARCHAR(40)); CREATE TABLE payrunemployees(PayRunId INT,EmployeeId INT,IsSkipped BOOLEAN DEFAULT FALSE);
INSERT INTO workflowmasters(Id,ClientId,Code,Name,ResourceType,IsActive) VALUES(1,55,'ICMR.ATTENDANCE.PERIOD','Fixture attendance','AttendancePeriod',TRUE);
INSERT INTO workflowstages(Id,WorkflowId,StageOrder,Name,ApproverType,ApproverUserId) VALUES(1,1,1,'PI approval','Reporting Manager',NULL),(2,1,2,'Project Cell final approval','Specific User',102);
""");
            return fixture;
        }
        public async ValueTask DisposeAsync()
        {
            await db.DisposeAsync();
            if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^icmr_approval_test_[a-f0-9]{32}$")) throw new InvalidOperationException("Unexpected fixture database.");
            await root.ExecuteAsync($"DROP DATABASE `{name}`"); await root.DisposeAsync();
        }
    }
}
