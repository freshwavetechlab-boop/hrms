using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Configuration;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;
using Payroll.API.Tests.Fixtures;

namespace Payroll.API.Tests.Services;

[Collection("ReviewedPayroll")]
public sealed partial class HistoricalPeriodSalaryDatabaseTests : IAsyncLifetime
{
    private const int Client = 97712, One = 9771201, Two = 9771202, Template = 9771200, OldTemplate = 9771209;
    private const string BatchId = "97712000000000000000000000000001", Trigger = "codex_historical_97712_late_failure";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ReviewedPayrollFixture? fixture = Environment.GetEnvironmentVariable("FREVO_REVIEWED_PAYROLL_TEST_DB") is string connection ? new(connection) : null;
    private ReviewedPayrollFixture F => fixture ?? throw new InvalidOperationException("Explicit guarded synthetic database required.");
    private ExcelPayrollSetupRepository Repository => new(F.Config, new EmployeeRepository(F.Config, null!, null!), F.Runs);
    private static AuthUser Actor => new() { Id = Client, Email = "synthetic-history-reviewer@example.test", IsActive = true, ClientAccessMode = "All",
        Roles = ["super_admin"], Permissions = ["payroll.run", "employees.manage", "settings.manage"] };
    private sealed class IsolatedFactAttribute : FactAttribute
    { public IsolatedFactAttribute() { if (Environment.GetEnvironmentVariable("FREVO_REVIEWED_PAYROLL_TEST_DB") is null) Skip = "Explicit sentinel-guarded synthetic payroll database required."; } }
    private sealed class IsolatedTheoryAttribute : TheoryAttribute
    { public IsolatedTheoryAttribute() { if (Environment.GetEnvironmentVariable("FREVO_REVIEWED_PAYROLL_TEST_DB") is null) Skip = "Explicit sentinel-guarded synthetic payroll database required."; } }
    private ExcelPayslipBatch batch = new(); private ExcelPayslipCalculationSource source = new();

    public async Task InitializeAsync()
    {
        if (fixture is null) return;
        await F.InitializeAsync(); await using var db = await F.OpenAsync();
        await db.ExecuteAsync(@"
CREATE TABLE IF NOT EXISTS attendance_groups(id INT PRIMARY KEY AUTO_INCREMENT,client_id INT NOT NULL,policy_batch_id VARCHAR(64) DEFAULT '',name VARCHAR(160) DEFAULT '',work_location_id INT NULL,department VARCHAR(100) DEFAULT '',designation VARCHAR(100) DEFAULT '',work_week VARCHAR(80) DEFAULT '',attendance_cycle_start_day INT DEFAULT 1,attendance_cycle_end_day INT DEFAULT 31,payroll_report_generation_day INT DEFAULT 3,is_active BOOLEAN DEFAULT TRUE,shift_id INT NULL,attendance_mode VARCHAR(30) DEFAULT 'Monthly',allowed_shift_ids_json JSON NULL);
CREATE TABLE IF NOT EXISTS attendance_group_employees(attendance_group_id INT NOT NULL,employee_id INT NOT NULL,PRIMARY KEY(attendance_group_id,employee_id));");
        await CleanupAsync();
        await db.ExecuteAsync(@"
INSERT INTO clients(Id,Code,Name,IsActive) VALUES(97712,'SYNTH-HIST','Synthetic closed salary',TRUE);
INSERT INTO worklocations(Id,ClientId,Name,City,State) VALUES(97712,97712,'Synthetic history office','Synthetic city','Historical synthetic state');
INSERT INTO salarycomponents(Id,Code,Name,Category,ComponentRole,StatutoryType,CalculationType,ValueText,Formula,ProRata,Taxable,PayType,Priority) VALUES
(9771201,'H_WAGES_97712','Historical wages','Earning','Regular Earning','None','Fixed Amount','0','',TRUE,TRUE,'Fixed Pay',100),
(9771202,'H_DEDUCTION_97712','Historical deduction','Deduction','Regular Deduction','None','Formula','0','H_WAGES_97712_EARNED*12%',FALSE,FALSE,'Fixed Pay',200),
(9771203,'H_EMPLOYER_97712','Historical employer cost','Benefit','Employer Benefit','None','Formula','0','H_WAGES_97712_EARNED*13%',FALSE,FALSE,'Fixed Pay',300);
INSERT INTO salarystructures(Id,ClientId,Name,AnnualCtc,Active) VALUES(9771200,97712,'Exact existing historical source template',933324,TRUE),(9771209,97712,'Retained older historical template',120000,TRUE);
INSERT INTO salarystructurelines(StructureId,ComponentId,ValueText,CalculationType,Formula,ProRataOverride,SortOrder) VALUES
(9771200,'9771201','0','Fixed Amount','','true',1),(9771200,'9771202','0','Formula','H_WAGES_97712_EARNED*12%','false',2),(9771200,'9771203','0','Formula','H_WAGES_97712_EARNED*13%','false',3),
(9771209,'9771201','0','Fixed Amount','','true',1),(9771209,'9771202','0','Formula','H_WAGES_97712_EARNED*12%','false',2),(9771209,'9771203','0','Formula','0','false',3);
INSERT INTO tax_client_settings(client_id,financial_year,enabled,project_monthly_tds,active) VALUES(97712,'2026-27',FALSE,FALSE,TRUE);
INSERT INTO employees(Id,ClientId,EmployeeCode,FirstName,LastName,DateOfJoining,AnnualCtc,SalaryStructureId,SalaryJson,PersonalJson,PaymentJson,WorkLocationId) VALUES
(9771201,97712,'HIST-SYN-A','Synthetic','One','2026-01-01',933324,'9771200',JSON_OBJECT('9771201',77777),JSON_OBJECT(),JSON_OBJECT(),97712),
(9771202,97712,'HIST-SYN-B','Synthetic','Two','2026-01-01',933324,'9771200',JSON_OBJECT('9771201',77777),JSON_OBJECT(),JSON_OBJECT(),97712);
INSERT INTO employeepersonaldetails(EmployeeId,UanNumber) VALUES(9771201,'101234569771'),(9771202,'101234569772');
INSERT INTO employeesalarycomponents(EmployeeId,ComponentId,Amount) VALUES(9771201,'9771201',77777),(9771202,'9771201',77777);
INSERT INTO employee_it0008_basic_pay(EmployeeId,ClientId,ActionType,EffectiveFrom,EffectiveTo,Status,SalaryStructureId,AnnualCtc,SalaryJson,ChangeReason,CreatedBy) VALUES
(9771201,97712,'Legacy salary','2026-01-01',NULL,'Historical','9771209',120000,JSON_OBJECT('9771201',10000),'Retained old basis','synthetic'),
(9771201,97712,'Future salary','2026-10-01',NULL,'Active','9771200',933324,JSON_OBJECT('9771201',77777),'Retained future basis','synthetic'),
(9771202,97712,'Legacy salary','2026-01-01',NULL,'Historical','9771209',120000,JSON_OBJECT('9771201',10000),'Retained old basis','synthetic'),
(9771202,97712,'Future salary','2026-10-01',NULL,'Active','9771200',933324,JSON_OBJECT('9771201',77777),'Retained future basis','synthetic');
INSERT INTO attendance_groups(id,client_id,policy_batch_id,name,work_week,attendance_cycle_start_day,attendance_cycle_end_day,payroll_report_generation_day,is_active,attendance_mode) VALUES(97712,97712,'97712000-0000-0000-0000-000000000001','Historical monthly policy','Sunday off',1,31,3,TRUE,'Monthly');
INSERT INTO attendance_group_employees(attendance_group_id,employee_id) VALUES(97712,9771201),(97712,9771202);
INSERT INTO employee_monthly_attendance(client_id,employee_id,attendance_month,working_days,present_days,payable_days,source_type,remarks) VALUES
(97712,9771201,'2026-09',30,26,30,'Monthly','Existing reviewed actual attendance'),(97712,9771202,'2026-09',30,26,30,'Monthly','Existing reviewed actual attendance'),
(97712,9771201,'2026-10',31,27,31,'Monthly','Existing future attendance');");
        var rows = new List<ExcelPayslipRow>(); var captured = new List<ExcelPayslipCalculationSourceRow>();
        for (var index = 0; index < 2; index++)
        {
            var number = index + 2; var code = index == 0 ? "HIST-SYN-A" : "HIST-SYN-B"; var name = index == 0 ? "Synthetic One" : "Synthetic Two"; var uan = index == 0 ? "101234569771" : "101234569772";
            rows.Add(new() { Id = "hist-row-" + number, SourceRow = number, EmployeeCode = code, EmployeeName = name,
                Information = [new() { Label = "UAN", Value = uan }, new() { Label = "Attendance", Value = "30" }],
                Earnings = [new() { Label = "Wages", Amount = 10000 }], Deductions = [new() { Label = "Deduction", Amount = 1200 }],
                EmployerContributions = [new() { Label = "Employer cost", Amount = 1300 }], DeclaredGross = 10000, DeclaredDeductions = 1200, NetPay = 8800 });
            captured.Add(new() { SourceRow = number, Cells = new[] { code, name, uan, "30", "30", "10000", "10000", "1200", "1300", "8800" }
                .Select((value, column) => (ExcelPayslipCalculationCell?)new ExcelPayslipCalculationCell { Value = value,
                    FormulaText = column switch { 6 => $"F{number}/D{number}*E{number}", 7 => $"G{number}*12%", 8 => $"G{number}*13%", 9 => $"G{number}-H{number}", _ => null } }).ToList() });
        }
        batch = new() { Id = BatchId, ClientId = Client, ClientName = "Synthetic closed salary", Month = "2026-09", SheetName = "Salary", SourceFileName = "Synthetic history.xlsx", HeaderRow = 1,
            Rows = rows, CreatedAtUtc = new(2026, 11, 1), CreatedBy = "isolated test" };
        var labels = new[] { "Employee code", "Employee name", "UAN", "Payroll days", "Attendance", "Monthly wage", "Wages", "Deduction", "Employer cost", "Net pay" };
        var kinds = new[] { "employeeCode", "employeeName", "info", "ignore", "info", "ignore", "earning", "deduction", "employer", "netPay" };
        source = new() { SheetName = "Salary", HeaderRow = 1, ColumnCount = 10, Rows = captured, DependencyReviewStatus = "Legacy",
            Columns = labels.Select((label, index) => new ExcelPayslipCalculationColumn { ColumnIndex = index, SourceHeader = label, Label = label, Kind = kinds[index] }).ToList() };
        await db.ExecuteAsync("INSERT INTO excel_payslip_batches(client_id,id,batch_json,send_state_json,created_at) VALUES(@Client,@Id,CAST(@Batch AS JSON),JSON_OBJECT('requests',JSON_OBJECT('old',JSON_OBJECT('status','Sent'))),UTC_TIMESTAMP());INSERT INTO excel_payslip_calculation_sources(client_id,batch_id,source_json,created_at) VALUES(@Client,@Id,CAST(@Source AS JSON),UTC_TIMESTAMP())",
            new { Client, Id = BatchId, Batch = JsonSerializer.Serialize(batch, Json), Source = JsonSerializer.Serialize(source, Json) });
    }
    public async Task DisposeAsync() { if (fixture is not null) await CleanupAsync(); }
    private async Task CleanupAsync()
    {
        await using var db = await F.OpenAsync();
        await db.ExecuteAsync("DROP TRIGGER IF EXISTS " + Trigger);
        // Only this test's explicitly owned client/IDs are removed; no global resets.
        await db.ExecuteAsync(@"
DELETE FROM employee_audit_trail WHERE EmployeeId IN (9771201,9771202);DELETE FROM modulesettings WHERE client_id=97712;DELETE FROM tax_client_settings WHERE client_id=97712;
DELETE p FROM payrunemployees p JOIN payruns r ON r.Id=p.PayRunId WHERE r.ClientId=97712;DELETE FROM payruns WHERE ClientId=97712;DELETE FROM payrolladjustments WHERE ClientId=97712;
DELETE FROM employee_monthly_attendance WHERE client_id=97712;DELETE FROM employee_daily_attendance WHERE client_id=97712;
DELETE FROM attendance_group_employees WHERE attendance_group_id=97712;DELETE FROM attendance_groups WHERE client_id=97712;
DELETE FROM excel_payslip_batches WHERE client_id=97712;DELETE FROM excel_payslip_calculation_sources WHERE client_id=97712;
DELETE FROM employee_it0008_basic_pay WHERE ClientId=97712;DELETE FROM employee_it0009_bank_details WHERE ClientId=97712;DELETE FROM employee_it0000_actions WHERE ClientId=97712;
DELETE FROM employeesalarycomponents WHERE EmployeeId IN (9771201,9771202);DELETE FROM employeepersonaldetails WHERE EmployeeId IN (9771201,9771202);DELETE FROM employeepaymentdetails WHERE EmployeeId IN (9771201,9771202);
DELETE FROM employees WHERE ClientId=97712;DELETE FROM worklocations WHERE ClientId=97712;DELETE FROM clients WHERE Id=97712;
DELETE FROM salarystructurelines WHERE StructureId IN (9771200,9771209);DELETE FROM salarystructures WHERE Id IN (9771200,9771209);DELETE FROM salarycomponents WHERE Id IN (9771201,9771202,9771203);");
    }
    private HistoricalPeriodSalaryRequest Request(bool both = false)
    {
        var ids = both ? new[] { One, Two } : [One];
        var sourceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { Batch = batch, Source = source }, Json)))).ToLowerInvariant();
        var request = new HistoricalPeriodSalaryRequest { Assignments = ids.Select(id => new HistoricalPeriodSalaryAssignment(id == One ? "hist-row-2" : "hist-row-3", id, Template.ToString(CultureInfo.InvariantCulture))).ToList(),
            Setup = new() { BatchId = BatchId, RowIds = ids.Select(id => id == One ? "hist-row-2" : "hist-row-3").ToList(), TemplateName = "Reviewed closed source period", EffectiveFrom = "2026-09-01",
                PayrollDaysColumnIndex = 3, PayableDaysColumnIndex = 4, WorkWeek = "Sunday off", AttendanceCycleStartDay = 1, AttendanceCycleEndDay = 31, PayrollReportGenerationDay = 3,
                ImportAttendance = false, TransferReviewVersion = 1, ReviewedFixedInputs = true, ReviewedPolicyScope = true,
                TemplateScopeReviewVersion = 1,
                TemplateScope = new() { Reviewed = true, EffectiveTo = "2026-09-30", LocationMode = "Listed", WorkLocationIds = [Client], CategoryKind = "AllClientCategories", DivisorMeaning = "CalendarDays", PayableMeaning = "AlreadyPayable", PolicyBasis = "SavedSourceRules" },
                TemplateDecisions = [new() { VariantKey = Template.ToString(CultureInfo.InvariantCulture), ExistingTemplateId = Template.ToString(CultureInfo.InvariantCulture), ScopeAction = "AttestLegacyScope", ScopeReason = "Reviewed exact synthetic monthly source and existing native policy for September only" }],
                SourceColumnRoles = source.Columns.ToDictionary(c => c.ColumnIndex, c => c.ColumnIndex is 3 or 5 ? "info" : c.Kind),
                AttendanceReview = new() { Mode = "VerifyExistingNative", Reviewed = true, SourceHash = sourceHash, SourceMonth = "2026-09", CycleStart = "2026-09-01", CycleEnd = "2026-09-30", PayableMeaning = "AlreadyPayable", PresentMeaning = "NotMapped" },
                Components = [Component("9771201", "H_WAGES_97712", "Earning", "Regular Earning", "Fixed Amount", "", true, 100, 5, 6, true),
                    Component("9771202", "H_DEDUCTION_97712", "Deduction", "Regular Deduction", "Formula", "H_WAGES_97712_EARNED*12%", false, 200, null, 7, false),
                    Component("9771203", "H_EMPLOYER_97712", "Benefit", "Employer Benefit", "Formula", "H_WAGES_97712_EARNED*13%", false, 300, null, 8, false)] } };
        request.Setup.AttendanceReview!.BoundSourceInputs = ExcelPayrollSetupRepository.AttendanceBinding(request.Setup);
        return request;
    }
    private static ExcelPayrollSetupComponent Component(string id, string code, string category, string role, string calculation, string formula,
        bool prorata, int priority, int? input, int output, bool taxable) => new() { Key = code, ExistingComponentId = id, Code = code, Name = code,
            Category = category, ComponentRole = role, StatutoryType = "None", CalculationType = calculation, Formula = formula, Value = "0", ProRata = prorata,
            Priority = priority, SourceColumnIndex = input, ExpectedColumnIndex = output, Taxable = taxable, PayType = "Fixed Pay" };
    private Task<HistoricalPeriodSalaryReceipt> ApplyAsync(HistoricalPeriodSalaryPreview preview, string? id = null) => Repository.ApplyHistoricalSalaryAsync(Client,
        new() { ReviewToken = preview.ReviewToken, RequestId = id ?? Guid.NewGuid().ToString("N"), Acknowledged = true }, Actor);
    private async Task<HistoricalPeriodSalaryPreview> PreviewAsync(bool both = false)
    {
        var result = await Repository.PreviewHistoricalSalaryAsync(Client, Request(both), Actor);
        Assert.True(result.CanApply, string.Join(";", result.Blockers)); return result;
    }
    private async Task<int> WrittenAsync()
    { await using var db = await F.OpenAsync(); return await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employee_it0008_basic_pay WHERE ClientId=97712 AND ActionType='Closed Excel Salary'"); }
    private async Task<string> WitnessAsync()
    {
        await using var db = await F.OpenAsync(); await using var tx = await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, isReadOnly: true);
        var state = await EmployeeRepository.ReadClosedSalaryHistoryStateAsync(db, tx, Client, [One, Two]);
        var sourceHash = await db.ExecuteScalarAsync<string>("SELECT CONCAT(SHA2(CAST(b.batch_json AS CHAR),256),SHA2(CAST(b.send_state_json AS CHAR),256),SHA2(CAST(s.source_json AS CHAR),256)) FROM excel_payslip_batches b JOIN excel_payslip_calculation_sources s ON s.client_id=b.client_id AND s.batch_id=b.id WHERE b.client_id=97712 AND b.id=@Id", new { Id = BatchId }, tx);
        var attendance = await db.QueryAsync("SELECT * FROM employee_monthly_attendance WHERE client_id=97712 ORDER BY employee_id,attendance_month", transaction: tx);
        await tx.RollbackAsync(); return EmployeeRepository.ClosedSalaryHash(new { state.CurrentMasterHash, Source = sourceHash, Attendance = attendance });
    }

    [IsolatedFact]
    public async Task PreviewIsSelectOnlyAndClosedInsertChangesNoCurrentFutureSourceOrMailData()
    {
        var before = await WitnessAsync(); var preview = await PreviewAsync(); Assert.Equal(0, await WrittenAsync());
        var result = await ApplyAsync(preview); Assert.Single(result.Rows); Assert.False(result.Replayed); Assert.Equal(before, await WitnessAsync());
        await using var db = await F.OpenAsync();
        var history = await db.QuerySingleAsync<EmployeeRepository.ClosedSalaryHistoryRow>("SELECT * FROM employee_it0008_basic_pay WHERE Id=@Id", new { Id = result.Rows[0].HistoryId });
        Assert.Equal(new DateTime(2026, 9, 1), history.EffectiveFrom); Assert.Equal(new DateTime(2026, 9, 30), history.EffectiveTo); Assert.Equal("Historical", history.Status);
        Assert.Equal(120000m, history.AnnualCtc); Assert.Equal(10000m, JsonSerializer.Deserialize<Dictionary<string, decimal>>(history.SalaryJson)!["9771201"]);
        var retained = (await db.QueryAsync<EmployeeRepository.ClosedSalaryHistoryRow>("SELECT * FROM employee_it0008_basic_pay WHERE ClientId=97712 AND ActionType<>'Closed Excel Salary' ORDER BY EmployeeId,EffectiveFrom")).ToArray();
        Assert.Equal(4, retained.Length);
        Assert.All(retained.Where(item => item.ActionType == "Future salary"), item => { Assert.Equal(933324m, item.AnnualCtc); Assert.Equal(Template.ToString(CultureInfo.InvariantCulture), item.SalaryStructureId); Assert.Null(item.EffectiveTo); Assert.Equal(77777m, JsonSerializer.Deserialize<Dictionary<string, decimal>>(item.SalaryJson)!["9771201"]); });
        Assert.All(retained.Where(item => item.ActionType == "Legacy salary"), item => { Assert.Equal(120000m, item.AnnualCtc); Assert.Equal(OldTemplate.ToString(CultureInfo.InvariantCulture), item.SalaryStructureId); Assert.Null(item.EffectiveTo); });
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employee_audit_trail WHERE EmployeeId=9771201"));
        Assert.Equal(2, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employees WHERE ClientId=97712"));
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM payruns WHERE ClientId=97712"));
        var actual = await F.Runs.SimulateEmployeeAsync(new() { Mode = "Employee", ClientId = Client, EmployeeId = One, PayPeriod = "2026-09" }, default);
        Assert.True(actual.Complete); Assert.Equal(10000, actual.Gross); Assert.Equal(1200, actual.Deductions); Assert.Equal(1300, actual.Benefits); Assert.Equal(8800, actual.Net);
        Assert.Equal(Template.ToString(CultureInfo.InvariantCulture), actual.TemplateId);
    }

    [IsolatedFact]
    public async Task ExactRequestReplayReturnsReceiptAndCannotStackAnotherClosedAssignment()
    {
        var preview = await PreviewAsync(); var id = Guid.NewGuid().ToString("N"); var first = await ApplyAsync(preview, id);
        var replay = await ApplyAsync(preview, id); Assert.True(replay.Replayed); Assert.Equal(first.Rows, replay.Rows); Assert.Equal(1, await WrittenAsync());
        await Assert.ThrowsAsync<SelectedPayRunConflictException>(() => ApplyAsync(preview)); Assert.Equal(1, await WrittenAsync());
    }

    [IsolatedTheory]
    [InlineData("future")][InlineData("master")][InlineData("attendance")][InlineData("source")][InlineData("template")][InlineData("unknownSourceField")]
    public async Task EveryRetainedOrFinancialWitnessMustStillMatchAtApply(string changed)
    {
        var preview = await PreviewAsync(); await using (var db = await F.OpenAsync())
            await db.ExecuteAsync(changed switch {
                "future" => "UPDATE employee_it0008_basic_pay SET AnnualCtc=AnnualCtc+1 WHERE EmployeeId=9771201 AND EffectiveFrom='2026-10-01'",
                "master" => "UPDATE employees SET AnnualCtc=AnnualCtc+1 WHERE Id=9771201",
                "attendance" => "UPDATE employee_monthly_attendance SET payable_days=29 WHERE client_id=97712 AND employee_id=9771201 AND attendance_month='2026-09'",
                "source" => "UPDATE excel_payslip_batches SET batch_json=JSON_SET(batch_json,'$.rows[0].employeeName','Changed synthetic name') WHERE client_id=97712",
                "unknownSourceField" => "UPDATE excel_payslip_calculation_sources SET source_json=JSON_SET(source_json,'$.unrecognizedLegacyNote','Changed source witness') WHERE client_id=97712",
                _ => "UPDATE salarystructurelines SET Formula='H_WAGES_97712_EARNED*14%' WHERE StructureId=9771200 AND ComponentId='9771203'"
            });
        await Assert.ThrowsAsync<SelectedPayRunConflictException>(() => ApplyAsync(preview)); Assert.Equal(0, await WrittenAsync());
    }

    [IsolatedFact]
    public async Task ASecondEmployeeDatabaseFailureRollsBackFirstInsertAuditAndReceipt()
    {
        var preview = await PreviewAsync(true); var before = await WitnessAsync();
        await using (var db = await F.OpenAsync()) await db.ExecuteAsync("CREATE TRIGGER " + Trigger + " BEFORE INSERT ON employee_it0008_basic_pay FOR EACH ROW BEGIN IF NEW.ClientId=97712 AND NEW.EmployeeId=9771202 AND NEW.ActionType='Closed Excel Salary' THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Synthetic scoped late failure'; END IF; END");
        await Assert.ThrowsAsync<MySqlConnector.MySqlException>(() => ApplyAsync(preview));
        Assert.Equal(0, await WrittenAsync()); Assert.Equal(before, await WitnessAsync());
        await using (var db = await F.OpenAsync()) { Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM modulesettings WHERE client_id=97712")); Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employee_audit_trail WHERE EmployeeId IN (9771201,9771202)")); }
    }

    [IsolatedFact]
    public async Task ExistingCoreCoverageOrIntentCannotReceiveAnotherHistoricalBasis()
    {
        await using (var db = await F.OpenAsync()) await db.ExecuteAsync("INSERT INTO payruns(Id,ClientId,PayPeriod,RunType,Status,RequestJson) VALUES(97712001,97712,'2026-09','Regular','Draft',JSON_OBJECT('IncludedEmployeeIds',JSON_ARRAY(9771201)))");
        var result = await Repository.PreviewHistoricalSalaryAsync(Client, Request(), Actor); Assert.False(result.CanApply); Assert.Contains(result.Blockers, b => b.Contains("saved regular request")); Assert.Equal(0, await WrittenAsync());
    }

    [IsolatedFact]
    public async Task EarnedSourceOutputsCannotBecomeMonthlySalaryInputs()
    {
        var request = Request(); request.Setup.Components[0].SourceColumnIndex = 6; request.Setup.SourceColumnRoles[6] = "info";
        var result = await Repository.PreviewHistoricalSalaryAsync(Client, request, Actor); Assert.False(result.CanApply); Assert.Contains(result.Blockers, b => b.Contains("independent Information")); Assert.Equal(0, await WrittenAsync());
    }

    [IsolatedFact]
    public async Task MissingActualAttendanceAndWrongActorBothFailClosed()
    {
        await using (var db = await F.OpenAsync()) await db.ExecuteAsync("UPDATE employee_monthly_attendance SET present_days=NULL WHERE client_id=97712 AND attendance_month='2026-09' AND employee_id=9771201");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Repository.PreviewHistoricalSalaryAsync(Client, Request(), Actor)); Assert.Equal(0, await WrittenAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Repository.PreviewHistoricalSalaryAsync(Client, Request(), new() { Id = 1, IsActive = true, Roles = ["admin"], Permissions = ["payroll.run", "employees.manage"] }));
    }

    [IsolatedFact]
    public async Task ReviewIsActorBoundTamperProofAndRequiresSafeRuntimeAndAcknowledgement()
    {
        var preview = await PreviewAsync(); var command = new HistoricalPeriodSalaryApplyRequest { ReviewToken = preview.ReviewToken, RequestId = Guid.NewGuid().ToString("N"), Acknowledged = true };
        var other = Actor; other.Id++;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Repository.ApplyHistoricalSalaryAsync(Client, command, other));
        command.Acknowledged = false; await Assert.ThrowsAsync<InvalidOperationException>(() => Repository.ApplyHistoricalSalaryAsync(Client, command, Actor));
        command.Acknowledged = true; command.ReviewToken = preview.ReviewToken[..^3] + "abc";
        await Assert.ThrowsAsync<InvalidOperationException>(() => Repository.ApplyHistoricalSalaryAsync(Client, command, Actor));
        command.ReviewToken = preview.ReviewToken;
        var unsafeConfig = new ConfigurationBuilder().AddConfiguration(F.Config).AddInMemoryCollection(new Dictionary<string, string?> { ["BackgroundWorkers:Enabled"] = "true" }).Build();
        var unsafeRepository = new ExcelPayrollSetupRepository(unsafeConfig, new EmployeeRepository(unsafeConfig, null!, null!), F.Runs);
        await Assert.ThrowsAsync<SelectedPayRunConflictException>(() => unsafeRepository.ApplyHistoricalSalaryAsync(Client, command, Actor));
        Assert.Equal(0, await WrittenAsync());
    }

    [IsolatedFact]
    public async Task ExistingTemplateRequiresExplicitCohortApplicabilityAndCannotLieAboutTheEmployeeLocation()
    {
        var missingReview = Request(); missingReview.Setup.TemplateDecisions.Clear();
        var result = await Repository.PreviewHistoricalSalaryAsync(Client, missingReview, Actor);
        Assert.False(result.CanApply); Assert.Contains(result.Blockers, error => error.Contains("Template applicability"));
        var wrongLocation = Request(); wrongLocation.Setup.TemplateScope!.WorkLocationIds = [Client + 100];
        result = await Repository.PreviewHistoricalSalaryAsync(Client, wrongLocation, Actor);
        Assert.False(result.CanApply); Assert.Contains(result.Blockers, error => error.Contains("outside the reviewed location"));
        Assert.Equal(0, await WrittenAsync());
    }

    [IsolatedFact]
    public async Task DeterministicCurrencyRoundingRequiresItsExactReviewedProof()
    {
        const decimal monthly = 10000.004m;
        var row = batch.Rows[0]; row.Earnings[0].Amount = monthly;
        row.Deductions[0].Amount = monthly * .12m; row.EmployerContributions[0].Amount = monthly * .13m;
        row.DeclaredGross = monthly; row.DeclaredDeductions = monthly * .12m; row.NetPay = monthly * .88m;
        foreach (var (index, amount) in new[] { (5, monthly), (6, monthly), (7, monthly * .12m), (8, monthly * .13m), (9, monthly * .88m) })
            source.Rows[0].Cells[index]!.Value = amount.ToString(CultureInfo.InvariantCulture);
        await using (var db = await F.OpenAsync()) await db.ExecuteAsync("UPDATE excel_payslip_batches SET batch_json=CAST(@Batch AS JSON) WHERE client_id=97712;UPDATE excel_payslip_calculation_sources SET source_json=CAST(@Source AS JSON) WHERE client_id=97712",
            new { Batch = JsonSerializer.Serialize(batch, Json), Source = JsonSerializer.Serialize(source, Json) });
        var request = Request(); var review = await Repository.PreviewHistoricalSalaryAsync(Client, request, Actor);
        var amountReview = Assert.IsType<ExcelPayrollTransferAmountReview>(review.Rows[0].AmountReview);
        Assert.False(review.CanApply); Assert.Equal("Explained rounding", amountReview.Status);
        Assert.False(amountReview.Acknowledged); Assert.Contains(review.Blockers, b => b.Contains("acknowledge this exact"));
        request.Setup.RoundingReviews[row.Id] = amountReview.ProofHash;
        var acknowledged = await Repository.PreviewHistoricalSalaryAsync(Client, request, Actor);
        Assert.True(acknowledged.CanApply, string.Join(";", acknowledged.Blockers)); Assert.True(Assert.IsType<ExcelPayrollTransferAmountReview>(acknowledged.Rows[0].AmountReview).Acknowledged);
        Assert.Equal(monthly, acknowledged.Rows[0].SourceMonthlyInputs["9771201"]);
        await ApplyAsync(acknowledged); Assert.Equal(1, await WrittenAsync());
    }
}
