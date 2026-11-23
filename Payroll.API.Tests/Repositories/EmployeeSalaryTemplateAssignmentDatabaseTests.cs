using System.Reflection;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Repositories;

public sealed class EmployeeSalaryTemplateAssignmentDatabaseTests
{
    private sealed class LocalFactAttribute : FactAttribute
    {
        public LocalFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("FREVO_EMPLOYEE_SALARY_TEMPLATE_TEST_DB") is null)
                Skip = "Explicit task-owned localhost salary assignment database required.";
        }
    }

    [LocalFact]
    public async Task New_assignments_require_existing_active_client_global_or_shared_templates_without_writing_salary_data()
    {
        await using var f = await Fixture.CreateAsync();
        var before = await f.FinancialStateAsync();
        foreach (var id in new[] { "101", "301", "302", "303", "304", "305" })
            await EmployeeRepository.ValidateSalaryTemplateAssignmentAsync(f.Db, new() { ClientId = 17, SalaryStructureId = id }, null);
        foreach (var id in new[] { "201", "401", "402", "999", "not-an-id", "0101" })
            await Assert.ThrowsAsync<InvalidOperationException>(() => EmployeeRepository.ValidateSalaryTemplateAssignmentAsync(f.Db,
                new() { ClientId = 17, SalaryStructureId = id, AnnualCtc = 999999, SalaryJson = "{\"10\":999999}" }, null));
        Assert.Equal(before, await f.FinancialStateAsync());
    }

    [LocalFact]
    public async Task Blank_and_unchanged_legacy_references_are_retained_but_client_changes_and_explicit_reassignments_are_checked()
    {
        await using var f = await Fixture.CreateAsync();
        var legacy = new Employee { Id = 2, ClientId = 17, SalaryStructureId = "201", AnnualCtc = 0 };
        await EmployeeRepository.ValidateSalaryTemplateAssignmentAsync(f.Db,
            new() { Id = 2, ClientId = 17, SalaryStructureId = "201", Department = "Updated department" }, legacy);
        await EmployeeRepository.ValidateSalaryTemplateAssignmentAsync(f.Db,
            new() { Id = 2, ClientId = 17, SalaryStructureId = "", AnnualCtc = 120000, SalaryJson = "{\"10\":10000}" }, legacy);
        await EmployeeRepository.ValidateSalaryTemplateAssignmentAsync(f.Db, new() { ClientId = 17, SalaryStructureId = "" }, null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => EmployeeRepository.ValidateSalaryTemplateAssignmentAsync(f.Db,
            new() { Id = 2, ClientId = 17, SalaryStructureId = "401" }, legacy));
        await Assert.ThrowsAsync<InvalidOperationException>(() => EmployeeRepository.ValidateSalaryTemplateAssignmentAsync(f.Db,
            new() { Id = 2, ClientId = 208, SalaryStructureId = "101" }, new() { Id = 2, ClientId = 17, SalaryStructureId = "101" }));
        Assert.Equal("201", await f.Db.ExecuteScalarAsync<string>("SELECT SalaryStructureId FROM employees WHERE Id=2"));
    }

    [LocalFact]
    public async Task Actual_shared_save_and_salary_action_reject_foreign_assignment_before_financial_or_history_writes()
    {
        await using var f = await Fixture.CreateAsync();
        var before = await f.FinancialStateAsync();
        var proposed = new Employee { Id = 1, ClientId = 17, SalaryStructureId = "201", AnnualCtc = 999999,
            SalaryJson = "{\"10\":999999}", EmployeeCode = "SYN-1", FirstName = "Synthetic", IsActive = true };
        var method = typeof(EmployeeRepository).GetMethod("SaveWithOpenConnectionAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        await using (var tx = await f.Db.BeginTransactionAsync())
        {
            var operation = (Task<int>)method.Invoke(null, [f.Db, proposed, "Synthetic reviewer", "0008", "Synthetic correction", tx,
                null, false, true, false, false])!;
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => operation);
            Assert.Contains("not available for this employee's client", error.Message);
            await tx.RollbackAsync();
        }
        Assert.Equal(before, await f.FinancialStateAsync());
        var (saved, actionError) = await f.Repository.ProcessActionAsync(new() { EmployeeId = 1, ActionType = "Salary Change",
            EffectiveDate = new(2026, 11, 23), SalaryStructureId = "201", AnnualCtc = 999999, SalaryJson = "{\"10\":999999}" }, "Synthetic reviewer");
        Assert.Null(saved); Assert.Contains("not available for this employee's client", actionError);
        Assert.Equal(before, await f.FinancialStateAsync());
    }

    [LocalFact]
    public async Task Transactional_assignment_check_keeps_template_ownership_locked_until_the_callers_transaction_ends()
    {
        await using var f = await Fixture.CreateAsync();
        await using var tx = await f.Db.BeginTransactionAsync();
        await EmployeeRepository.ValidateSalaryTemplateAssignmentAsync(f.Db, new() { ClientId = 17, SalaryStructureId = "101" }, null, tx);
        var otherSettings = new MySqlConnectionStringBuilder(Environment.GetEnvironmentVariable("FREVO_EMPLOYEE_SALARY_TEMPLATE_TEST_DB")!) { Database = f.Db.Database };
        await using var other = new MySqlConnection(otherSettings.ConnectionString); await other.OpenAsync();
        await using var otherTx = await other.BeginTransactionAsync();
        var blocked = await Assert.ThrowsAsync<MySqlException>(() => other.QuerySingleAsync<long>(
            "SELECT Id FROM salarystructures WHERE Id=101 FOR UPDATE NOWAIT", transaction: otherTx));
        Assert.Equal(3572, blocked.Number);
        await otherTx.RollbackAsync(); await tx.RollbackAsync();
    }

    private sealed class Fixture(MySqlConnection root, MySqlConnection db, string database, EmployeeRepository repository) : IAsyncDisposable
    {
        public MySqlConnection Db => db;
        public EmployeeRepository Repository => repository;
        public async Task<string> FinancialStateAsync() => JsonSerializer.Serialize(new
        {
            employees = await Db.QueryAsync("SELECT * FROM employees ORDER BY Id"),
            components = await Db.QueryAsync("SELECT * FROM employeesalarycomponents ORDER BY EmployeeId,ComponentId"),
            history = await Db.QueryAsync("SELECT * FROM employee_it0008_basic_pay ORDER BY Id"),
            audit = await Db.QueryAsync("SELECT * FROM employee_audit_trail ORDER BY Id")
        });
        public static async Task<Fixture> CreateAsync()
        {
            var settings = new MySqlConnectionStringBuilder(Environment.GetEnvironmentVariable("FREVO_EMPLOYEE_SALARY_TEMPLATE_TEST_DB")!);
            if (settings.Server != "127.0.0.1" || settings.Port != 33319 || settings.Database != "salary_assignment_test")
                throw new InvalidOperationException("Only the task-owned localhost salary assignment database is permitted.");
            settings.Database = ""; var root = new MySqlConnection(settings.ConnectionString); await root.OpenAsync();
            var database = "salary_assignment_" + Guid.NewGuid().ToString("N");
            await root.ExecuteAsync($"CREATE DATABASE `{database}` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci");
            settings.Database = database; var db = new MySqlConnection(settings.ConnectionString); await db.OpenAsync();
            await db.ExecuteAsync("""
CREATE TABLE salarystructures(Id BIGINT PRIMARY KEY,ClientId INT NOT NULL,ClientRef VARCHAR(200) NOT NULL,Active BOOL NOT NULL);
INSERT INTO salarystructures VALUES(101,17,'17',TRUE),(201,208,'208',TRUE),(301,0,'',TRUE),(302,0,'0',TRUE),
(303,0,'17: Alpha;208: Beta',TRUE),(304,0,'208|17',TRUE),(305,0,'all',TRUE),(401,17,'17',FALSE),(402,0,'invalid-scope',TRUE);
CREATE TABLE employees(Id INT PRIMARY KEY,ClientId INT NOT NULL,EmployeeCode VARCHAR(80),FirstName VARCHAR(80),LastName VARCHAR(80),
Gender VARCHAR(20),DateOfJoining VARCHAR(10),WorkEmail VARCHAR(120),Department VARCHAR(120),Designation VARCHAR(120),Grade VARCHAR(120),
WorkLocationId INT DEFAULT 0,ReportingManagerId INT DEFAULT 0,ReportingManagerUserId INT NULL,PortalAccess BOOL DEFAULT FALSE,
SalaryStructureId VARCHAR(80) NOT NULL DEFAULT '',AnnualCtc DECIMAL(18,2),SalaryJson JSON,PersonalJson JSON,PaymentJson JSON,IsActive BOOL DEFAULT TRUE);
INSERT INTO employees(Id,ClientId,EmployeeCode,FirstName,LastName,DateOfJoining,SalaryStructureId,AnnualCtc,SalaryJson,PersonalJson,PaymentJson)
VALUES(1,17,'SYN-1','Synthetic','One','2026-10-01','',120000,'{"10":10000}','{}','{}'),(2,17,'SYN-2','Synthetic','Legacy','2026-10-01','201',0,'{}','{}','{}');
CREATE TABLE employeesalarycomponents(EmployeeId INT,ComponentId VARCHAR(80),Amount DECIMAL(18,2));
INSERT INTO employeesalarycomponents VALUES(1,'10',10000);
CREATE TABLE employeepersonaldetails(EmployeeId INT PRIMARY KEY);
CREATE TABLE employeepaymentdetails(EmployeeId INT PRIMARY KEY,BankName VARCHAR(120),BankAccountNo VARCHAR(120),IfscCode VARCHAR(120),PaymentMode VARCHAR(120));
""");
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = settings.ConnectionString }).Build();
            var repository = new EmployeeRepository(config, null!, null!); await repository.InitializeAsync();
            await db.ExecuteAsync("""
INSERT INTO employee_it0008_basic_pay(EmployeeId,ClientId,ActionType,EffectiveFrom,Status,SalaryStructureId,AnnualCtc,SalaryJson,ChangeReason,CreatedBy)
VALUES(1,17,'Hire','2026-10-01','Active','',120000,'{"10":10000}','Synthetic initial state','Synthetic reviewer');
""");
            return new(root, db, database, repository);
        }
        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            if (!System.Text.RegularExpressions.Regex.IsMatch(database, "^salary_assignment_[a-f0-9]{32}$")) throw new InvalidOperationException("Unexpected test database.");
            await root.ExecuteAsync($"DROP DATABASE `{database}`"); await root.DisposeAsync();
        }
    }
}
