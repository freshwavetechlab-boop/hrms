using System.Text.Json;
using Dapper;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Tests.Fixtures;

namespace Payroll.API.Tests.Services;

[Collection("ReviewedPayroll")]
public sealed class AttendanceGroupScopeValidationTests : IAsyncLifetime
{
    private const int Client = 97729, Foreign = 97730, Group = 977290;
    private static readonly int[] Employees = Enumerable.Range(9772901, 8).ToArray();
    private readonly ReviewedPayrollFixture? fixture = Environment.GetEnvironmentVariable("FREVO_REVIEWED_PAYROLL_TEST_DB") is string value ? new(value) : null;
    private ReviewedPayrollFixture F => fixture ?? throw new InvalidOperationException("Explicit guarded fixture required.");
    private sealed class LocalFactAttribute : FactAttribute { public LocalFactAttribute() { if (Environment.GetEnvironmentVariable("FREVO_REVIEWED_PAYROLL_TEST_DB") is null) Skip = "Explicit sentinel-guarded fixture required."; } }
    private sealed class LocalTheoryAttribute : TheoryAttribute { public LocalTheoryAttribute() { if (Environment.GetEnvironmentVariable("FREVO_REVIEWED_PAYROLL_TEST_DB") is null) Skip = "Explicit sentinel-guarded fixture required."; } }
    private LeaveAttendanceRepository Repository => new(F.Config);

    public async Task InitializeAsync()
    {
        if (fixture is null) return;
        await F.InitializeAsync(); await using var db = await F.OpenAsync();
        await db.ExecuteAsync("""
CREATE TABLE IF NOT EXISTS attendance_groups(id INT PRIMARY KEY AUTO_INCREMENT,client_id INT NOT NULL,policy_batch_id VARCHAR(80),name VARCHAR(180),work_location_id INT,department VARCHAR(150),designation VARCHAR(150),work_week VARCHAR(80),attendance_cycle_start_day INT,attendance_cycle_end_day INT,payroll_report_generation_day INT,is_active BOOLEAN,shift_id INT NULL,attendance_mode VARCHAR(30),allowed_shift_ids_json JSON,created_at DATETIME DEFAULT CURRENT_TIMESTAMP,updated_at DATETIME DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP);
CREATE TABLE IF NOT EXISTS attendance_group_employees(id INT PRIMARY KEY AUTO_INCREMENT,attendance_group_id INT NOT NULL,employee_id INT NOT NULL,created_at DATETIME DEFAULT CURRENT_TIMESTAMP);
CREATE TABLE IF NOT EXISTS attendance_shift_assignments(id INT PRIMARY KEY AUTO_INCREMENT,client_id INT NOT NULL,employee_id INT,shift_id INT,effective_to DATE,is_active BOOLEAN);
CREATE TABLE IF NOT EXISTS attendance_shifts(id INT PRIMARY KEY AUTO_INCREMENT,client_id INT NOT NULL,shift_code VARCHAR(50) NOT NULL,shift_name VARCHAR(180) NOT NULL,shift_type VARCHAR(20) NOT NULL DEFAULT 'Fixed',start_time TIME NULL,end_time TIME NULL,is_overnight BOOLEAN NOT NULL DEFAULT FALSE,grace_minutes INT NOT NULL DEFAULT 0,break_minutes INT NOT NULL DEFAULT 0,minimum_full_day_hours DECIMAL(5,2) NOT NULL DEFAULT 8,minimum_half_day_hours DECIMAL(5,2) NOT NULL DEFAULT 4,effective_from DATE NOT NULL,effective_to DATE NULL,is_active BOOLEAN NOT NULL DEFAULT TRUE,rules_json JSON NULL,created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,UNIQUE KEY ux_attendance_shifts_client_code(client_id,shift_code),UNIQUE KEY ux_attendance_shifts_id_client(id,client_id));
""");
        foreach (var (column, definition) in new[]
        {
            ("policy_batch_id", "VARCHAR(80) NULL"), ("name", "VARCHAR(180) NOT NULL DEFAULT ''"),
            ("work_location_id", "INT NOT NULL DEFAULT 0"), ("department", "VARCHAR(150) NOT NULL DEFAULT ''"),
            ("designation", "VARCHAR(150) NOT NULL DEFAULT ''"), ("work_week", "VARCHAR(80) NOT NULL DEFAULT ''"),
            ("attendance_cycle_start_day", "INT NOT NULL DEFAULT 1"), ("attendance_cycle_end_day", "INT NOT NULL DEFAULT 31"),
            ("payroll_report_generation_day", "INT NOT NULL DEFAULT 3"), ("is_active", "BOOLEAN NOT NULL DEFAULT TRUE"),
            ("shift_id", "INT NULL"), ("attendance_mode", "VARCHAR(30) NOT NULL DEFAULT 'Legacy'"),
            ("allowed_shift_ids_json", "JSON NULL"), ("created_at", "DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP"),
            ("updated_at", "DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP")
        })
            if (!await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='attendance_groups' AND column_name=@Column)", new { Column = column }))
                await db.ExecuteAsync($"ALTER TABLE attendance_groups ADD COLUMN `{column}` {definition}");
        if (!await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='worklocations' AND column_name='IsActive')"))
            await db.ExecuteAsync("ALTER TABLE worklocations ADD COLUMN IsActive BOOLEAN NOT NULL DEFAULT TRUE");
        await CleanAsync();
        await db.ExecuteAsync("""
INSERT INTO clients(Id,Code,Name,IsActive) VALUES(97729,'SYN-GROUP-ALL','Synthetic group wildcard',TRUE),(97730,'SYN-GROUP-FOREIGN','Synthetic foreign client',TRUE);
INSERT INTO worklocations(Id,ClientId,Name,State,IsActive) VALUES(97729,97729,'Synthetic group site','Synthetic state',TRUE),(97730,97730,'Foreign site','Synthetic state',TRUE);
INSERT INTO employees(Id,ClientId,EmployeeCode,FirstName,LastName,DateOfJoining,Department,Designation,WorkLocationId,AnnualCtc,IsActive)
VALUES(9772901,97729,'GROUP-1','Synthetic','One','2026-01-01','Ops','Driver',97729,120000,TRUE),
(9772902,97729,'GROUP-2','Synthetic','Two','2026-01-01','Ops','Helper',97729,120000,TRUE),
(9772903,97729,'GROUP-3','Synthetic','Three','2026-01-01','Finance','Clerk',97729,120000,TRUE),
(9772904,97729,'GROUP-4','Synthetic','Four','2026-01-01','IT','Technician',97729,120000,TRUE),
(9772905,97729,'GROUP-5','Synthetic','Five','2026-01-01','Ops','Driver',97729,120000,TRUE),
(9772906,97729,'GROUP-6','Synthetic','Six','2026-01-01','Finance','Manager',97729,120000,TRUE),
(9772907,97729,'GROUP-7','Synthetic','Seven','2026-01-01','IT','Lead',97729,120000,TRUE),
(9772908,97729,'GROUP-8','Synthetic','Eight','2026-01-01','Legal','Counsel',97729,120000,TRUE),
(9773001,97730,'FOREIGN-1','Foreign','Person','2026-01-01','Ops','Driver',97730,120000,TRUE);
INSERT INTO attendance_groups(id,client_id,policy_batch_id,name,work_location_id,department,designation,work_week,attendance_cycle_start_day,attendance_cycle_end_day,payroll_report_generation_day,is_active,shift_id,attendance_mode,allowed_shift_ids_json)
VALUES(977290,97729,'synthetic-existing-policy','Synthetic existing policy',97729,'All','All','Sunday off',1,31,3,TRUE,NULL,'Monthly',JSON_ARRAY());
INSERT INTO attendance_group_employees(attendance_group_id,employee_id) VALUES(977290,9772901),(977290,9772902),(977290,9772903),(977290,9772904);
""");
    }

    public async Task DisposeAsync() { if (fixture is not null) await CleanAsync(); }
    private async Task CleanAsync()
    {
        await using var db = await F.OpenAsync();
        await db.ExecuteAsync("""
DELETE FROM attendance_shift_assignments WHERE client_id IN(97729,97730);
DELETE FROM attendance_group_employees WHERE attendance_group_id=977290 OR employee_id IN(9772901,9772902,9772903,9772904,9772905,9772906,9772907,9772908,9773001);
DELETE FROM attendance_groups WHERE client_id IN(97729,97730);
DELETE FROM employee_it0000_actions WHERE ClientId IN(97729,97730);
DELETE FROM employee_it0008_basic_pay WHERE ClientId IN(97729,97730);
DELETE FROM employees WHERE ClientId IN(97729,97730);
DELETE FROM worklocations WHERE ClientId IN(97729,97730);
DELETE FROM clients WHERE Id IN(97729,97730);
""");
    }

    private static SaveAttendanceGroupRequest Request(string department = "All", string designation = "All", params int[] ids) => new()
    {
        Id = Group, ClientId = Client, PolicyBatchId = "synthetic-existing-policy", Name = "Synthetic existing policy",
        WorkLocationId = Client, Department = department, Designation = designation, WorkWeek = "Sunday off",
        AttendanceCycleStartDay = 1, AttendanceCycleEndDay = 31, PayrollReportGenerationDay = 3, IsActive = true,
        AttendanceMode = "Monthly", ShiftId = null, AllowedShiftIds = [], EmployeeIds = ids.Length == 0 ? Employees.ToList() : ids.ToList()
    };
    private async Task<string> SnapshotAsync()
    {
        await using var db = await F.OpenAsync();
        var group = await db.QueryAsync("SELECT * FROM attendance_groups WHERE client_id=97729 ORDER BY id");
        var members = await db.QueryAsync("SELECT * FROM attendance_group_employees WHERE attendance_group_id=977290 ORDER BY employee_id");
        return JsonSerializer.Serialize(new { Group = group, Members = members });
    }

    [LocalFact]
    public async Task All_scope_updates_existing_group_id_and_rules_without_losing_the_previous_roster()
    {
        await using var db = await F.OpenAsync();
        var before = await db.QuerySingleAsync("SELECT id,client_id,policy_batch_id,name,work_location_id,department,designation,work_week,attendance_cycle_start_day,attendance_cycle_end_day,payroll_report_generation_day,is_active,shift_id,attendance_mode,CAST(allowed_shift_ids_json AS CHAR) allowed_shift_ids_json,created_at FROM attendance_groups WHERE id=977290");
        var master = JsonSerializer.Serialize(await db.QueryAsync("SELECT * FROM employees WHERE ClientId=97729 ORDER BY Id"));
        var (saved, error) = await Repository.SaveAttendanceGroupAsync(Request());
        Assert.Null(error); Assert.NotNull(saved); Assert.Equal(Group, saved.Id);
        Assert.Equal(Employees, (await db.QueryAsync<int>("SELECT employee_id FROM attendance_group_employees WHERE attendance_group_id=977290 ORDER BY employee_id")).ToArray());
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM attendance_groups WHERE client_id=97729"));
        var after = await db.QuerySingleAsync("SELECT id,client_id,policy_batch_id,name,work_location_id,department,designation,work_week,attendance_cycle_start_day,attendance_cycle_end_day,payroll_report_generation_day,is_active,shift_id,attendance_mode,CAST(allowed_shift_ids_json AS CHAR) allowed_shift_ids_json,created_at FROM attendance_groups WHERE id=977290");
        Assert.Equal(JsonSerializer.Serialize((object)before), JsonSerializer.Serialize((object)after));
        Assert.Equal(master, JsonSerializer.Serialize(await db.QueryAsync("SELECT * FROM employees WHERE ClientId=97729 ORDER BY Id")));
    }

    [LocalTheory]
    [InlineData("Ops", "Driver", new[] { 9772901, 9772905 })]
    [InlineData("All", "Driver", new[] { 9772901, 9772905 })]
    [InlineData("Ops", "All", new[] { 9772901, 9772902, 9772905 })]
    public async Task Specific_filters_remain_enforced_when_the_other_filter_is_All(string department, string designation, int[] ids)
    {
        var (saved, error) = await Repository.SaveAttendanceGroupAsync(Request(department, designation, ids));
        Assert.Null(error); Assert.NotNull(saved); Assert.Equal(Group, saved.Id);
        var beforeInvalid = await SnapshotAsync();
        var rejected = await Repository.SaveAttendanceGroupAsync(Request(department, designation, ids.Append(9772906).ToArray()));
        Assert.Null(rejected.Group); Assert.Contains("Selected employees", rejected.Error);
        Assert.Equal(beforeInvalid, await SnapshotAsync());
    }

    [LocalTheory]
    [InlineData("foreign")]
    [InlineData("inactive")]
    [InlineData("different-location")]
    public async Task All_scope_still_rejects_a_foreign_inactive_or_different_location_employee_without_changes(string invalid)
    {
        await using var db = await F.OpenAsync();
        if (invalid == "inactive") await db.ExecuteAsync("UPDATE employees SET IsActive=FALSE WHERE Id=9772908");
        if (invalid == "different-location") await db.ExecuteAsync("UPDATE employees SET WorkLocationId=97730 WHERE Id=9772908");
        var before = await SnapshotAsync();
        var selected = invalid == "foreign" ? Employees.Append(9773001).ToArray() : Employees;
        var (saved, error) = await Repository.SaveAttendanceGroupAsync(Request(ids: selected));
        Assert.Null(saved); Assert.Contains("Selected employees", error);
        Assert.Equal(before, await SnapshotAsync());
    }
}
