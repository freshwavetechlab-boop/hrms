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
public sealed class HistoricalTemplateConfigurationDatabaseTests : IAsyncLifetime
{
    private const int Client = 97727, One = 9772701, Two = 9772702, Template = 9772700, OldTemplate = 9772709;
    private const string BatchId = "97727000000000000000000000000001", Trigger = "codex_historical_97727_late_failure";
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
INSERT INTO clients(Id,Code,Name,IsActive) VALUES(97727,'SYNTH-HIST','Synthetic closed salary',TRUE);
INSERT INTO worklocations(Id,ClientId,Name,City,State) VALUES(97727,97727,'Synthetic history office','Synthetic city','Historical synthetic state 97727');
INSERT INTO salarycomponents(Id,Code,Name,Category,ComponentRole,StatutoryType,CalculationType,ValueText,Formula,ProRata,Taxable,PayType,Priority) VALUES
(9772701,'H_WAGES_97727','Historical wages','Earning','Regular Earning','None','Fixed Amount','0','',TRUE,TRUE,'Fixed Pay',100),
(9772702,'H_DEDUCTION_97727','Historical deduction','Deduction','Regular Deduction','None','Formula','0','H_WAGES_97727_EARNED*12%',FALSE,FALSE,'Fixed Pay',200),
(9772703,'H_EMPLOYER_97727','Historical employer cost','Benefit','Employer Benefit','None','Formula','0','H_WAGES_97727_EARNED*13%',FALSE,FALSE,'Fixed Pay',300);
INSERT INTO salarystructures(Id,ClientId,Name,AnnualCtc,Active) VALUES(9772700,97727,'Exact existing historical source template',933324,TRUE),(9772709,97727,'Retained older historical template',120000,TRUE);
INSERT INTO salarystructurelines(StructureId,ComponentId,ValueText,CalculationType,Formula,ProRataOverride,SortOrder) VALUES
(9772700,'9772701','0','Fixed Amount','','true',1),(9772700,'9772702','0','Formula','H_WAGES_97727_EARNED*12%','false',2),(9772700,'9772703','0','Formula','H_WAGES_97727_EARNED*13%','false',3),
(9772709,'9772701','0','Fixed Amount','','true',1),(9772709,'9772702','0','Formula','H_WAGES_97727_EARNED*12%','false',2),(9772709,'9772703','0','Formula','0','false',3);
INSERT INTO tax_client_settings(client_id,financial_year,enabled,project_monthly_tds,active) VALUES(97727,'2026-27',FALSE,FALSE,TRUE);
INSERT INTO employees(Id,ClientId,EmployeeCode,FirstName,LastName,DateOfJoining,AnnualCtc,SalaryStructureId,SalaryJson,PersonalJson,PaymentJson,WorkLocationId) VALUES
(9772701,97727,'HIST-SYN-A','Synthetic','One','2026-01-01',933324,'9772700',JSON_OBJECT('9772701',77777),JSON_OBJECT(),JSON_OBJECT(),97727),
(9772702,97727,'HIST-SYN-B','Synthetic','Two','2026-01-01',933324,'9772700',JSON_OBJECT('9772701',77777),JSON_OBJECT(),JSON_OBJECT(),97727);
INSERT INTO employeepersonaldetails(EmployeeId,UanNumber) VALUES(9772701,'101234569771'),(9772702,'101234569772');
INSERT INTO employeesalarycomponents(EmployeeId,ComponentId,Amount) VALUES(9772701,'9772701',77777),(9772702,'9772701',77777);
INSERT INTO employee_it0008_basic_pay(EmployeeId,ClientId,ActionType,EffectiveFrom,EffectiveTo,Status,SalaryStructureId,AnnualCtc,SalaryJson,ChangeReason,CreatedBy) VALUES
(9772701,97727,'Legacy salary','2026-01-01',NULL,'Historical','9772709',120000,JSON_OBJECT('9772701',10000),'Retained old basis','synthetic'),
(9772701,97727,'Future salary','2026-10-01',NULL,'Active','9772700',933324,JSON_OBJECT('9772701',77777),'Retained future basis','synthetic'),
(9772702,97727,'Legacy salary','2026-01-01',NULL,'Historical','9772709',120000,JSON_OBJECT('9772701',10000),'Retained old basis','synthetic'),
(9772702,97727,'Future salary','2026-10-01',NULL,'Active','9772700',933324,JSON_OBJECT('9772701',77777),'Retained future basis','synthetic');
INSERT INTO attendance_groups(id,client_id,policy_batch_id,name,work_week,attendance_cycle_start_day,attendance_cycle_end_day,payroll_report_generation_day,is_active,attendance_mode) VALUES(97727,97727,'97727000-0000-0000-0000-000000000001','Historical monthly policy','Sunday off',1,31,3,TRUE,'Monthly');
INSERT INTO attendance_group_employees(attendance_group_id,employee_id) VALUES(97727,9772701),(97727,9772702);
INSERT INTO employee_monthly_attendance(client_id,employee_id,attendance_month,working_days,present_days,payable_days,source_type,remarks) VALUES
(97727,9772701,'2026-09',30,26,30,'Monthly','Existing reviewed actual attendance'),(97727,9772702,'2026-09',30,26,30,'Monthly','Existing reviewed actual attendance'),
(97727,9772701,'2026-10',31,27,31,'Monthly','Existing future attendance');");
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
DELETE FROM employee_audit_trail WHERE EmployeeId IN (9772701,9772702);DELETE FROM paysliptemplates WHERE Id=9772708;DELETE FROM salarystructurelines WHERE StructureId IN (9772707,9772708);DELETE FROM salarystructures WHERE Id IN (9772707,9772708);DELETE FROM modulesettings WHERE client_id=97727;DELETE FROM tax_client_settings WHERE client_id=97727;
DELETE p FROM payrunemployees p JOIN payruns r ON r.Id=p.PayRunId WHERE r.ClientId=97727;DELETE FROM payruns WHERE ClientId=97727;DELETE FROM payrolladjustments WHERE ClientId=97727;
DELETE FROM employee_monthly_attendance WHERE client_id=97727;DELETE FROM employee_daily_attendance WHERE client_id=97727;
DELETE FROM attendance_group_employees WHERE attendance_group_id=97727;DELETE FROM attendance_groups WHERE client_id=97727;
DELETE FROM excel_payslip_batches WHERE client_id=97727;DELETE FROM excel_payslip_calculation_sources WHERE client_id=97727;
DELETE FROM employee_it0008_basic_pay WHERE ClientId=97727;DELETE FROM employee_it0009_bank_details WHERE ClientId=97727;DELETE FROM employee_it0000_actions WHERE ClientId=97727;
DELETE FROM employeesalarycomponents WHERE EmployeeId IN (9772701,9772702);DELETE FROM employeepersonaldetails WHERE EmployeeId IN (9772701,9772702);DELETE FROM employeepaymentdetails WHERE EmployeeId IN (9772701,9772702);
DELETE FROM employees WHERE ClientId=97727;DELETE FROM worklocations WHERE ClientId=97727;DELETE FROM clients WHERE Id=97727;
DELETE FROM salarystructurelines WHERE StructureId IN (9772700,9772709);DELETE FROM salarystructures WHERE Id IN (9772700,9772709);DELETE FROM salarycomponents WHERE Id IN (9772701,9772702,9772703);");
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
                Components = [Component("9772701", "H_WAGES_97727", "Earning", "Regular Earning", "Fixed Amount", "", true, 100, 5, 6, true),
                    Component("9772702", "H_DEDUCTION_97727", "Deduction", "Regular Deduction", "Formula", "H_WAGES_97727_EARNED*12%", false, 200, null, 7, false),
                    Component("9772703", "H_EMPLOYER_97727", "Benefit", "Employer Benefit", "Formula", "H_WAGES_97727_EARNED*13%", false, 300, null, 8, false)] } };
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
    { await using var db = await F.OpenAsync(); return await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employee_it0008_basic_pay WHERE ClientId=97727 AND ActionType='Closed Excel Salary'"); }
    private async Task<string> WitnessAsync()
    {
        await using var db = await F.OpenAsync(); await using var tx = await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, isReadOnly: true);
        var state = await EmployeeRepository.ReadClosedSalaryHistoryStateAsync(db, tx, Client, [One, Two]);
        var sourceHash = await db.ExecuteScalarAsync<string>("SELECT CONCAT(SHA2(CAST(b.batch_json AS CHAR),256),SHA2(CAST(b.send_state_json AS CHAR),256),SHA2(CAST(s.source_json AS CHAR),256)) FROM excel_payslip_batches b JOIN excel_payslip_calculation_sources s ON s.client_id=b.client_id AND s.batch_id=b.id WHERE b.client_id=97727 AND b.id=@Id", new { Id = BatchId }, tx);
        var attendance = await db.QueryAsync("SELECT * FROM employee_monthly_attendance WHERE client_id=97727 ORDER BY employee_id,attendance_month", transaction: tx);
        await tx.RollbackAsync(); return EmployeeRepository.ClosedSalaryHash(new { state.CurrentMasterHash, Source = sourceHash, Attendance = attendance });
    }

    private const string NewTemplate="9772708";
    private HistoricalTemplateConfigurationRequest ConfigurationRequest()
    {
        var review=Request();review.Assignments=review.Assignments.Select(a=>a with{TemplateId=NewTemplate}).ToList();review.Setup.TemplateName="One reviewed reusable template";
        return new(){SourceReview=review,TemplateId=NewTemplate,ExpectedSourceHash=review.Setup.AttendanceReview!.SourceHash};
    }
    private async Task<HistoricalTemplateConfigurationPreview> ReadyConfigurationAsync()
    {
        var request=ConfigurationRequest();var discovery=await Repository.PreviewHistoricalTemplateConfigurationAsync(Client,request,Actor);
        Assert.False(discovery.CanCreate);Assert.Contains(discovery.Blockers,b=>b.Contains("setup hash"));request.ExpectedSetupHash=discovery.SetupHash;
        var ready=await Repository.PreviewHistoricalTemplateConfigurationAsync(Client,request,Actor);Assert.True(ready.CanCreate,string.Join(";",ready.Blockers));return ready;
    }
    private Task<HistoricalTemplateConfigurationReceipt> SaveConfigurationAsync(HistoricalTemplateConfigurationPreview preview,string? id=null)=>Repository.ApplyHistoricalTemplateConfigurationAsync(Client,new(preview.ReviewToken,id??Guid.NewGuid().ToString("N"),true),Actor);
    private async Task<string> OldConfigurationWitnessAsync()
    {
        await using var db=await F.OpenAsync();return EmployeeRepository.ClosedSalaryHash(new{
            Structures=await db.QueryAsync("SELECT * FROM salarystructures WHERE Id<>9772708 ORDER BY Id"),
            Lines=await db.QueryAsync("SELECT * FROM salarystructurelines WHERE StructureId<>9772708 ORDER BY Id"),
            Components=await db.QueryAsync("SELECT * FROM salarycomponents ORDER BY Id"),
            PayslipTemplates=await db.QueryAsync("SELECT * FROM paysliptemplates ORDER BY Id"),
            Setup=await db.QueryAsync("SELECT * FROM payrollsetups ORDER BY Id"),
            Pt=await db.QueryAsync("SELECT * FROM professionaltaxslabs ORDER BY Id")});
    }
    [IsolatedFact]
    public async Task Atomic_one_template_configuration_preserves_all_existing_SQL_rows_and_native_inputs_and_replays()
    {
        await using var db=await F.OpenAsync();await db.ExecuteAsync("INSERT INTO paysliptemplates(Id,ClientId,ClientRef,Name,Theme,Active) VALUES(9772708,97727,'97727','Retained legacy design','classic',TRUE)");
        await db.ExecuteAsync("CREATE TRIGGER codex_single_97727_no_payslip_update BEFORE UPDATE ON paysliptemplates FOR EACH ROW BEGIN IF OLD.Id=9772708 THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Unrelated payslip update forbidden'; END IF; END");
        try{
            var before=await WitnessAsync();var config=await OldConfigurationWitnessAsync();var preview=await ReadyConfigurationAsync();Assert.Equal(before,await WitnessAsync());Assert.Equal(config,await OldConfigurationWitnessAsync());
            var id=Guid.NewGuid().ToString("N");var saved=await SaveConfigurationAsync(preview,id);Assert.False(saved.Replayed);Assert.Equal(NewTemplate,saved.TemplateId);
            Assert.Equal(config,await OldConfigurationWitnessAsync());Assert.Equal(before,await WitnessAsync());Assert.Equal(0,await WrittenAsync());
            Assert.Equal(3,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM salarystructurelines WHERE StructureId=@NewTemplate",new{NewTemplate}));
            Assert.Equal(1,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM modulesettings WHERE client_id=@Client",new{Client}));
            Assert.True((await SaveConfigurationAsync(preview,id)).Replayed);Assert.Equal(config,await OldConfigurationWitnessAsync());
        }finally{await db.ExecuteAsync("DROP TRIGGER IF EXISTS codex_single_97727_no_payslip_update");}
    }
    [IsolatedTheory]
    [InlineData("source")][InlineData("sourceUnknown")][InlineData("component")][InlineData("templateCollision")][InlineData("retainedTemplate")]
    public async Task Stale_witness_or_global_ID_collision_cannot_overwrite_any_configuration(string change)
    {
        var preview=await ReadyConfigurationAsync();await using var db=await F.OpenAsync();
        await db.ExecuteAsync(change switch{
            "source"=>"UPDATE excel_payslip_batches SET batch_json=JSON_SET(batch_json,'$.rows[0].employeeName','Changed') WHERE client_id=97727",
            "sourceUnknown"=>"UPDATE excel_payslip_calculation_sources SET source_json=JSON_SET(source_json,'$.unknownWitness','changed') WHERE client_id=97727",
            "component"=>"UPDATE salarycomponents SET Name='Concurrent change' WHERE Id=9772701",
            "templateCollision"=>"INSERT INTO salarystructures(Id,ClientId,Name,AnnualCtc,Active) VALUES(9772708,0,'Concurrent shared owner',1,TRUE)",
            _=>"UPDATE salarystructures SET Name='Concurrent retained change' WHERE Id=9772709"});
        var before=await OldConfigurationWitnessAsync();await Assert.ThrowsAsync<SelectedPayRunConflictException>(()=>SaveConfigurationAsync(preview));
        Assert.Equal(before,await OldConfigurationWitnessAsync());Assert.Equal(0,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM modulesettings WHERE client_id=@Client",new{Client}));
        if(change=="templateCollision")Assert.Equal(0,await db.ExecuteScalarAsync<int>("SELECT ClientId FROM salarystructures WHERE Id=9772708"));
    }
    [IsolatedFact]
    public async Task Scope_acknowledgment_and_receipt_conflicts_fail_without_any_template_write()
    {
        var preview=await ReadyConfigurationAsync();await Assert.ThrowsAsync<InvalidOperationException>(()=>Repository.ApplyHistoricalTemplateConfigurationAsync(Client,new(preview.ReviewToken,Guid.NewGuid().ToString("N"),false),Actor));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>Repository.ApplyHistoricalTemplateConfigurationAsync(Client+1,new(preview.ReviewToken,Guid.NewGuid().ToString("N"),true),Actor));
        var id=Guid.NewGuid().ToString("N");await SaveConfigurationAsync(preview,id);var request=ConfigurationRequest();request.TemplateId="9772707";request.SourceReview.Assignments=request.SourceReview.Assignments.Select(a=>a with{TemplateId=request.TemplateId}).ToList();request.SourceReview.Setup.TemplateName="Another reviewed name";
        var discovery=await Repository.PreviewHistoricalTemplateConfigurationAsync(Client,request,Actor);request.ExpectedSetupHash=discovery.SetupHash;var other=await Repository.PreviewHistoricalTemplateConfigurationAsync(Client,request,Actor);Assert.True(other.CanCreate,string.Join(";",other.Blockers));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>SaveConfigurationAsync(other,id));
        await using var db=await F.OpenAsync();Assert.Equal(0,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM salarystructures WHERE Id=9772707"));
    }
    [IsolatedFact]
    public async Task Late_SQL_failure_rolls_back_every_new_template_line_and_receipt()
    {
        var preview=await ReadyConfigurationAsync();var before=await OldConfigurationWitnessAsync();await using var db=await F.OpenAsync();
        await db.ExecuteAsync("CREATE TRIGGER codex_single_97727_late BEFORE INSERT ON salarystructurelines FOR EACH ROW BEGIN IF NEW.StructureId=9772708 AND NEW.ComponentId='9772703' THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Synthetic late failure'; END IF; END");
        try{await Assert.ThrowsAsync<MySqlConnector.MySqlException>(()=>SaveConfigurationAsync(preview));Assert.Equal(before,await OldConfigurationWitnessAsync());Assert.Equal(0,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM salarystructures WHERE Id=9772708"));Assert.Equal(0,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM modulesettings WHERE client_id=97727"));}
        finally{await db.ExecuteAsync("DROP TRIGGER IF EXISTS codex_single_97727_late");}
    }
}