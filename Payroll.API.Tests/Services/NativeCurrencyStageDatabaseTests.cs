using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;
using Payroll.API.Tests.Fixtures;

namespace Payroll.API.Tests.Services;

[Collection("ReviewedPayroll")]
public sealed partial class NativeCurrencyStageDatabaseTests : IAsyncLifetime
{
    private const int Client = 97728, One = 9772801, Two = 9772802, Group = 977280;
    private const string BatchId = "97728000000000000000000000000001";
    private const string ImportId = "97728000000000000000000000000002";
    private const string Wage = "CURRENCY_WAGE_97728", Pf = "CURRENCY_PF_97728", Esi = "CURRENCY_ESI_97728";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ReviewedPayrollFixture? fixture = Environment.GetEnvironmentVariable("FREVO_REVIEWED_PAYROLL_TEST_DB") is string value ? new(value) : null;
    private ReviewedPayrollFixture F => fixture ?? throw new InvalidOperationException("Guarded owned fixture required.");
    private sealed class LocalFactAttribute : FactAttribute { public LocalFactAttribute() { if (Environment.GetEnvironmentVariable("FREVO_REVIEWED_PAYROLL_TEST_DB") is null) Skip = "Explicit sentinel-guarded fixture required."; } }
    private PortableIntegrationCredentialProtector Protector => new(F.Config, new EphemeralDataProtectionProvider(), NullLogger<PortableIntegrationCredentialProtector>.Instance);
    private IConfiguration Config(bool enabled) => new ConfigurationBuilder().AddConfiguration(F.Config)
        .AddInMemoryCollection(new Dictionary<string, string?> { [PayRunRepository.NativeCurrencyFlag] = enabled.ToString() }).Build();
    private PayRunRepository Runs(bool enabled) { var cfg = Config(enabled); return new(cfg, new TaxEngineRepository(cfg), null, Protector); }
    private PayslipDesignRepository Designs => new(F.Config, Protector, new());
    private static AuthUser Actor => new() { Id = Client, IsActive = true, Permissions = ["settings.manage"], ClientAccessMode = "Selected", AssignedClientIds = [Client] };
    private ExcelPayslipBatch batch = new(); private ExcelPayslipCalculationSource source = new();
    private long designId;

    public async Task InitializeAsync()
    {
        if (fixture is null) return;
        await F.InitializeAsync(); await using var db = await F.OpenAsync();
        await db.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "NativeCurrency", "pipeline-schema.sql")));
        await db.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "NativeCurrency", "mail-history-schema.sql")));
        await db.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "NativeCurrency", "organization-schema.sql")));
        foreach (var (table, column, definition) in new[]
        {
            ("payruns", "AttendancePolicyBatchId", "VARCHAR(64) NOT NULL DEFAULT ''"),
            ("payruns", "ProcessingStartedAt", "DATETIME NULL"), ("payruns", "ProcessingCompletedAt", "DATETIME NULL"),
            ("payruns", "ProcessingError", "VARCHAR(1000) NOT NULL DEFAULT ''"),
            ("payrunemployees", "ClientName", "VARCHAR(250) NULL"), ("payrunemployees", "ManualTds", "DECIMAL(18,2) NOT NULL DEFAULT 0"),
            ("payrunemployees", "PaymentStatus", "VARCHAR(30) NOT NULL DEFAULT 'Pending'"), ("payrunemployees", "PaymentDate", "DATE NULL"),
            ("leave_types", "name", "VARCHAR(100) NOT NULL DEFAULT ''"), ("employee_daily_attendance", "remarks", "TEXT NULL")
        })
            if (await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name=@table)", new { table })
                && !await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name=@table AND column_name=@column)", new { table, column }))
                await db.ExecuteAsync($"ALTER TABLE `{table}` ADD COLUMN `{column}` {definition}");
        await db.ExecuteAsync("""
CREATE TABLE IF NOT EXISTS tax_computation_snapshots(id BIGINT PRIMARY KEY AUTO_INCREMENT,pay_run_id INT NOT NULL,employee_id INT,financial_year VARCHAR(10),snapshot_json JSON);
CREATE TABLE IF NOT EXISTS leave_types(id INT PRIMARY KEY AUTO_INCREMENT,client_id INT,code VARCHAR(50),name VARCHAR(100),type VARCHAR(30));
CREATE TABLE IF NOT EXISTS leave_attendance_preferences(id INT PRIMARY KEY AUTO_INCREMENT,client_id INT,work_location_id INT,work_week VARCHAR(100),attendance_cycle_start_day INT,attendance_cycle_end_day INT);
CREATE TABLE IF NOT EXISTS dropdownmasters(Id INT PRIMARY KEY AUTO_INCREMENT,ClientId INT,Type VARCHAR(80),Value VARCHAR(200),ConfigJson JSON,IsActive BOOLEAN);
CREATE TABLE IF NOT EXISTS workflow_action_rules (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,ActivityCode VARCHAR(120) NOT NULL,HttpMethod VARCHAR(12) NOT NULL,
    PathPattern VARCHAR(300) NOT NULL,ResourceType VARCHAR(100) NOT NULL,
    ResourceIdSource VARCHAR(120) NOT NULL DEFAULT 'route.id',ResourceIdRouteKey VARCHAR(80) NOT NULL DEFAULT 'id',
    ClientIdSource VARCHAR(120) NOT NULL DEFAULT '',ClientIdSql VARCHAR(1000) NOT NULL DEFAULT '',
    ClientLookupTable VARCHAR(120) NOT NULL DEFAULT '',ClientLookupKeyColumn VARCHAR(120) NOT NULL DEFAULT '',
    ClientLookupClientColumn VARCHAR(120) NOT NULL DEFAULT '',WorkflowId INT NULL,
    TriggerMode VARCHAR(40) NOT NULL DEFAULT 'AfterSuccess',IsActive BOOLEAN NOT NULL DEFAULT TRUE,
    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    UNIQUE KEY UX_WorkflowActionRule(HttpMethod,PathPattern,ActivityCode));
CREATE TABLE IF NOT EXISTS ResourceStates (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,ResourceType VARCHAR(100) NOT NULL,ResourceId VARCHAR(120) NOT NULL,
    CurrentState VARCHAR(60) NOT NULL,WorkflowInstanceId BIGINT NULL,CreatedBy INT NOT NULL DEFAULT 0,
    CreatedOn DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,ModifiedBy INT NOT NULL DEFAULT 0,
    ModifiedOn DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    UNIQUE KEY UX_ResourceStates_Resource(ResourceType,ResourceId));
""");
        if (!await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM information_schema.statistics WHERE table_schema=DATABASE() AND table_name='payrunemployees' AND index_name='UX_Currency_Run_Employee')"))
            await db.ExecuteAsync("CREATE UNIQUE INDEX UX_Currency_Run_Employee ON payrunemployees(PayRunId,EmployeeId)");
        await CleanAsync();
        await db.ExecuteAsync("""
INSERT INTO clients(Id,Code,Name,IsActive) VALUES(97728,'SYN-CURRENCY','Synthetic currency client',TRUE);
INSERT INTO worklocations(Id,ClientId,Name,State) VALUES(97728,97728,'Synthetic currency site','Currency test state');
INSERT INTO employees(Id,ClientId,EmployeeCode,FirstName,LastName,DateOfJoining,Department,WorkLocationId,SalaryStructureId,AnnualCtc,IsActive)
VALUES(9772801,97728,'CURR-SYN-1','Person','One','2026-01-01','Synthetic',97728,'97728',184968,TRUE),
(9772802,97728,'CURR-SYN-2','Person','Two','2026-01-01','Synthetic',97728,'97728',184968,TRUE);
INSERT INTO employeesalarycomponents(EmployeeId,ComponentId,Amount) VALUES(9772801,'9772801',15414),(9772802,'9772801',15414);
INSERT INTO salarycomponents(Id,Code,Name,Category,ComponentRole,StatutoryType,CalculationType,ValueText,Formula,BaseComponent,ProRata,Taxable,Active,Priority,PayType)
VALUES(9772801,'CURRENCY_WAGE_97728','Synthetic currency salary','Earning','Regular Earning','None','Fixed Amount','0','','',TRUE,TRUE,TRUE,10,'Fixed Pay'),
(9772802,'CURRENCY_PF_97728','Synthetic currency PF','Deduction','Statutory Deduction','PF Employee','Formula','0','CURRENCY_WAGE_97728_EARNED*12%','',FALSE,FALSE,TRUE,20,'Fixed Pay'),
(9772803,'CURRENCY_ESI_97728','Synthetic currency ESI','Deduction','Statutory Deduction','ESI Employee','Formula','0','CURRENCY_WAGE_97728_EARNED*0.75%','',FALSE,FALSE,TRUE,30,'Fixed Pay');
INSERT INTO salarystructures(Id,ClientId,ClientRef,Name,AnnualCtc,Active) VALUES(97728,97728,'97728','Synthetic currency structure',184968,TRUE);
INSERT INTO salarystructurelines(StructureId,ComponentId,ValueText,CalculationType,Formula,BaseComponent,ProRataOverride,RoundingMode,SortOrder)
VALUES(97728,'9772801','0','Fixed Amount','','','true','',0),(97728,'9772802','0','Formula','CURRENCY_WAGE_97728_EARNED*12%','','false','',1),
(97728,'9772803','0','Formula','CURRENCY_WAGE_97728_EARNED*0.75%','','false','',2);
INSERT INTO attendance_groups(id,client_id,policy_batch_id,name,work_week,attendance_cycle_start_day,attendance_cycle_end_day,is_active,attendance_mode)
VALUES(977280,97728,'currency-legacy','Synthetic currency attendance','Sunday off',1,31,TRUE,'Legacy');
INSERT INTO attendance_group_employees(attendance_group_id,employee_id) VALUES(977280,9772801),(977280,9772802);
INSERT INTO employee_monthly_attendance(client_id,employee_id,attendance_month,working_days,present_days,payable_days,source_type,remarks)
VALUES(97728,9772801,'2026-09',30,16,16,'Date-wise',''),(97728,9772802,'2026-09',30,16,16,'Date-wise','');
INSERT INTO tax_client_settings(client_id,financial_year,enabled,project_monthly_tds,active) VALUES(97728,'2026-27',FALSE,FALSE,TRUE);
""");
        await db.ExecuteAsync("INSERT INTO employee_daily_attendance(client_id,employee_id,attendance_date,status,payable_value,remarks) VALUES(97728,@Employee,@Date,@Status,@Value,'')",
            new[] { One, Two }.SelectMany(id => Enumerable.Range(1, 30).Select(day => new { Employee = id, Date = new DateTime(2026, 9, day), Status = day <= 16 ? "Present" : "Absent", Value = day <= 16 ? 1 : 0 })));
        batch = new() { Id = BatchId, ClientId = Client, Month = "2026-09", SheetName = "Salary", HeaderRow = 5, SourceFileName = "Synthetic currency.xlsx",
            Rows = new[] { One, Two }.Select((id, index) => new ExcelPayslipRow
            {
                Id = new Guid(index + 1, 0, 0, new byte[8]).ToString(), SourceRow = index + 6,
                EmployeeCode = "CURR-SYN-" + (index + 1), EmployeeName = "Person " + (index == 0 ? "One" : "Two"),
                Information = [new() { Label = "Payroll days", Value = "30" }, new() { Label = "Payable days", Value = "16" },
                    new() { Label = "Monthly wages", Value = "15414" }, new() { Label = "Actual present", Value = "16" }],
                Earnings = [new() { Label = "Salary", Amount = 8220.8m }], Deductions = [new() { Label = "PF", Amount = 986.496m },new(){Label="ESI",Amount=61.656m}],
                DeclaredGross = 8220.8m, DeclaredDeductions = 1048.152m, NetPay = 7172.648m
            }).ToList() };
        source = new() { SheetName = "Salary", HeaderRow = 5, ColumnCount = 10,
            Columns = new[] { ("Payroll days", "info"), ("Payable days", "info"), ("Monthly wages", "info"), ("Salary", "earning"),
                ("PF", "deduction"),("ESI","deduction"), ("Net", "netPay"), ("Employee name", "employeeName"), ("Employee code", "employeeCode"), ("Actual present", "info") }
                .Select((c, index) => new ExcelPayslipCalculationColumn { ColumnIndex = index, SourceHeader = c.Item1, Label = c.Item1, Kind = c.Item2 }).ToList(),
            Rows = batch.Rows.Select(row => new ExcelPayslipCalculationSourceRow
            {
                SourceRow = row.SourceRow, Cells = [Cell("30"), Cell("16"), Cell("15414"), Cell("8220.8", $"C{row.SourceRow}/A{row.SourceRow}*B{row.SourceRow}"),
                    Cell("986.49599999999987", $"D{row.SourceRow}*12%"),Cell("61.655999999999999", $"D{row.SourceRow}*0.75%"), Cell("7172.6480000000001", $"D{row.SourceRow}-E{row.SourceRow}-F{row.SourceRow}"),
                    new() { DataType = "Text", Value = row.EmployeeName }, new() { DataType = "Text", Value = row.EmployeeCode }, Cell("16")]
            }).ToList() };
        var hash = ExcelPayslipRepository.EmploymentSourceHash(batch, source);
        var request = new ExcelPayrollSetupRequest
        {
            BatchId = BatchId, TransferReviewVersion = 1, RowIds = batch.Rows.Select(r => r.Id).ToList(), TemplateName = "Synthetic currency structure",
            EffectiveFrom = "2026-09-01", WorkWeek = "Sunday off", PayrollDaysColumnIndex = 0, PayableDaysColumnIndex = 1,
            PresentDaysColumnIndex = 9, ReviewedFixedInputs = true, ReviewedPolicyScope = true,
            EmployeeDecisions = batch.Rows.Select((r, i) => new ExcelPayrollSetupEmployeeDecision { RowId = r.Id, MatchedEmployeeId = i == 0 ? One : Two }).ToList(),
            AttendanceReview = new() { Mode = "VerifyExistingNative", Reviewed = true, SourceMonth = "2026-09", SourceHash = hash,
                PresentMeaning = "ActualWorked", PayableMeaning = "AlreadyPayable", CycleStart = "2026-09-01", CycleEnd = "2026-09-30" },
            Components = [new() { Key = "salary", ExistingComponentId = "9772801", Code = Wage, Name = "Synthetic currency salary", Category = "Earning",
                    ComponentRole = "Regular Earning", StatutoryType = "None", CalculationType = "Fixed Amount", Value = "0", SourceColumnIndex = 2,
                    ExpectedColumnIndex = 3, ProRata = true, Taxable = true, Priority = 10, PayType = "Fixed Pay" },
                new() { Key = "pf", ExistingComponentId = "9772802", Code = Pf, Name = "Synthetic currency PF", Category = "Deduction", ComponentRole = "Statutory Deduction",
                    StatutoryType = "PF Employee", CalculationType = "Formula", Formula = Wage + "_EARNED*12%", Value = "0", ExpectedColumnIndex = 4,
                    ProRata = false, Taxable = false, Priority = 20, PayType = "Fixed Pay" },
                new() { Key = "esi", ExistingComponentId = "9772803", Code = Esi, Name = "Synthetic currency ESI", Category = "Deduction", ComponentRole = "Statutory Deduction",
                    StatutoryType = "ESI Employee", CalculationType = "Formula", Formula = Wage + "_EARNED*0.75%", Value = "0", ExpectedColumnIndex = 5,
                    ProRata = false, Taxable = false, Priority = 30, PayType = "Fixed Pay" }]
        };
        var receipt = new ExcelPayrollSetupResult { ImportId = ImportId, EmployeeIds = [One, Two], TemplateId = "97728", TemplateIds = ["97728"],
            Transfer = new() { SourceBatchId = BatchId, SourceMonth = "2026-09", SourceHash = hash },
            TransferRows = batch.Rows.Select((r, i) => new ExcelPayrollTransferRowReceipt(r.Id, i == 0 ? One : Two, 30, 16, 16, r.NetPay, [])).ToList() };
        await db.ExecuteAsync("""
INSERT INTO excel_payslip_batches(client_id,id,batch_json,send_state_json,created_at) VALUES(@Client,@Batch,CAST(@BatchJson AS JSON),JSON_OBJECT('retained','old mail'),UTC_TIMESTAMP());
INSERT INTO excel_payslip_calculation_sources(client_id,batch_id,source_json,created_at) VALUES(@Client,@Batch,CAST(@SourceJson AS JSON),UTC_TIMESTAMP());
INSERT INTO excel_payroll_setup_imports(id,client_id,batch_id,request_id,selection_hash,request_hash,plan_hash,request_json,result_json,status,created_by)
VALUES(@Import,@Client,@Batch,@Import,@Hash,@Hash,@Hash,CAST(@Request AS JSON),CAST(@Receipt AS JSON),'Applied','Synthetic reviewer');
""", new { Client, Batch = BatchId, Import = ImportId, Hash = hash, BatchJson = JsonSerializer.Serialize(batch, Json), SourceJson = JsonSerializer.Serialize(source, Json), Request = JsonSerializer.Serialize(request, Json), Receipt = JsonSerializer.Serialize(receipt, Json) });
        await SaveDesignAsync();
    }

    private async Task SaveDesignAsync()
    {
        var preview = await Designs.PreviewAsync(new() { ClientId = Client, SourceBatchId = BatchId });
        var design = await Designs.SaveAsync(new() { RequestId = Guid.NewGuid().ToString(), Reviewed = true,
            Template = new() { ClientId = Client, Name = "Synthetic native currency source", Theme = NativePayslipDesignService.Theme },
            SourceBatchId = BatchId, SourceHash = preview.SourceHash, AmountDecimalPlaces = 0,
            Fields = preview.Fields.Select(f => new PayslipDesignField { SourceColumn = f.SourceColumn, Label = f.Label, Kind = f.Kind,
                InfoToken = f.InfoToken, Visible = f.Kind != "info" || f.InfoToken.Length > 0,
                NativeCode = f.Kind == "earning" ? Wage : f.Kind == "deduction" ? f.Label=="ESI"?Esi:Pf : "" }).ToList() }, Actor);
        designId = design.Id;
    }

    private static ExcelPayslipCalculationCell Cell(string value, string? formula = null) => new() { DataType = "Number", Value = value, FormulaText = formula };
    private static CreatePayRunRequest Request() => new() { ClientId = Client, PayPeriod = "2026-09", PayDate = new(2026, 10, 1), TotalWorkingDays = 30,
        AttendanceGroupId = Group, AttendancePolicyBatchId = "currency-legacy", IncludedEmployeeIds = [One, Two] };
    public async Task DisposeAsync() { if (fixture is not null) await CleanAsync(); }
    private async Task CleanAsync()
    {
        await using var db = await F.OpenAsync();
        await db.ExecuteAsync("DROP TRIGGER IF EXISTS currency_97728_fail_trace");
        await db.ExecuteAsync("DROP TRIGGER IF EXISTS currency_97728_fail_recapture");
        await db.ExecuteAsync("DROP TRIGGER IF EXISTS currency_97728_fail_receipt");
        await db.ExecuteAsync("DELETE FROM workflow_action_rules WHERE ActivityCode='CURRENCY_RECAPTURE_97728'");
        await db.ExecuteAsync("""
DELETE a FROM notification_queue_attachments a JOIN notification_queue q ON q.Id=a.QueueId WHERE q.ClientId=97728;
DELETE l FROM notification_logs l JOIN notification_queue q ON q.Id=l.QueueId WHERE q.ClientId=97728;
DELETE FROM notification_queue WHERE ClientId=97728;
DELETE FROM entity_attachments WHERE client_id=97728;
""");
        var ids = (await db.QueryAsync<int>("SELECT Id FROM payruns WHERE ClientId=97728")).ToArray();
        if (ids.Length > 0)
        {
            await db.ExecuteAsync("DELETE FROM ResourceStates WHERE ResourceType='PayRun' AND ResourceId IN @Ids", new { Ids = ids.Select(id => id.ToString()).ToArray() });
            foreach (var table in new[] { "payrunemployeelines", "payrun_step_logs", "payroll_validation_issues", "payroll_calculation_traces", "payroll_reconciliation_results" })
                await db.ExecuteAsync($"DELETE FROM `{table}` WHERE PayRunId IN @Ids", new { Ids = ids });
            await db.ExecuteAsync("DELETE FROM tax_computation_snapshots WHERE pay_run_id IN @Ids", new { Ids = ids });
        }
        await db.ExecuteAsync("""
DELETE FROM payrunemployees WHERE ClientId=97728;DELETE FROM payruns WHERE ClientId=97728;
DELETE FROM modulesettings WHERE client_id=97728;DELETE FROM paysliptemplates WHERE ClientId=97728;
DELETE FROM excel_payroll_setup_imports WHERE client_id=97728;DELETE FROM excel_payslip_batches WHERE client_id=97728;DELETE FROM excel_payslip_calculation_sources WHERE client_id=97728;
DELETE FROM employee_daily_attendance WHERE client_id=97728;DELETE FROM employee_monthly_attendance WHERE client_id=97728;
DELETE FROM attendance_group_employees WHERE attendance_group_id=977280;DELETE FROM attendance_groups WHERE client_id=97728;
DELETE FROM tax_client_settings WHERE client_id=97728;DELETE FROM payrolladjustments WHERE ClientId=97728;
DELETE FROM employeesalarycomponents WHERE EmployeeId IN(9772801,9772802);DELETE FROM employeepersonaldetails WHERE EmployeeId IN(9772801,9772802);DELETE FROM employeepaymentdetails WHERE EmployeeId IN(9772801,9772802);
DELETE FROM employee_it0000_actions WHERE ClientId=97728;DELETE FROM employee_it0008_basic_pay WHERE ClientId=97728;
DELETE FROM employees WHERE ClientId=97728;DELETE FROM worklocations WHERE ClientId=97728;
DELETE FROM salarystructurelines WHERE StructureId=97728;DELETE FROM salarystructures WHERE Id=97728;DELETE FROM salarycomponents WHERE Id IN(9772801,9772802,9772803);
DELETE FROM clients WHERE Id=97728;
""");
    }

    private async Task<string> SourceWitnessAsync()
    {
        await using var db = await F.OpenAsync();
        return await db.ExecuteScalarAsync<string>("SELECT CONCAT(SHA2(CAST(batch_json AS CHAR),256),SHA2(CAST(send_state_json AS CHAR),256)) FROM excel_payslip_batches WHERE client_id=97728 AND id=@Batch", new { Batch = BatchId });
    }
    private async Task<NativeCurrencyFrozenRow?> ReadAsync(PayRunRepository repo, int runId, int employee = One)
    {
        await using var db = await F.OpenAsync(); await using var tx = await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, isReadOnly: true);
        var result = await repo.ReadNativeCurrencyStageAsync(db, tx, Client, runId, employee); await tx.RollbackAsync(); return result;
    }

    [LocalFact]
    public async Task Actual_regular_processing_captures_same_epoch_and_does_not_change_source_or_money()
    {
        var before = await SourceWitnessAsync(); var repo = Runs(true); var run = await repo.CreateAsync(Request(), "Synthetic currency test");
        Assert.NotNull(run); Assert.Equal("Draft", run.Status); Assert.Equal(2, run.Employees.Count);
        var row = await ReadAsync(repo, run.Id); Assert.NotNull(row);
        var pf = Assert.Single(row.Lines, l => l.Code == Pf); Assert.Equal(986.496m, pf.UnroundedEarned); Assert.Equal(986.50m, pf.AccountedEarned);
        Assert.All(run.Employees, employee => { Assert.Equal(8220.80m, employee.GrossPay); Assert.Equal(1048.16m, employee.StatutoryDeductions); Assert.Equal(7172.64m, employee.NetPay); });
        Assert.Equal(before, await SourceWitnessAsync());
        await using var db = await F.OpenAsync(); Assert.Equal(2, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM modulesettings WHERE client_id=97728 AND ModuleCode LIKE 'pcst%'"));
        Assert.True(await db.ExecuteScalarAsync<bool>("SELECT COUNT(*)>0 FROM payroll_calculation_traces WHERE PayRunId=@Id AND JSON_EXTRACT(OutputJson,'$.nativeCurrencyStage') IS NOT NULL", new { run.Id }));
    }

    [LocalFact]
    public async Task Actual_selected_queue_processing_uses_the_same_capture_pipeline()
    {
        var repo = Runs(true); var queued = await repo.QueueAsync(Request(), "Synthetic selected currency"); Assert.NotNull(queued);
        var preview = await repo.GetSelectedProcessingPreviewAsync(queued.Id, Client, "2026-09");
        Assert.NotNull(preview); Assert.True(preview.CanProcess, string.Join("; ", preview.Blockers));
        var run = await repo.ProcessSelectedRunAsync(queued.Id, new() { ClientId = Client, PayPeriod = "2026-09",
            Confirmed = true, ExpectedFingerprint = preview.Fingerprint }, "Synthetic selected currency");
        Assert.NotNull(run); Assert.Equal("Draft", run.Status); Assert.NotNull(await ReadAsync(repo, run.Id));
        Assert.Equal(2, run.Employees.Count); Assert.All(run.Employees, row => Assert.Equal(7172.64m, row.NetPay));
    }

    [LocalFact]
    public async Task Disabled_capture_keeps_old_trace_JSON_bytes_and_writes_no_receipt()
    {
        var repo = Runs(false); var run = await repo.CreateAsync(Request()); Assert.NotNull(run);
        Assert.Null(await ReadAsync(repo, run.Id));
        await using var db = await F.OpenAsync();
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM modulesettings WHERE client_id=97728 AND ModuleCode LIKE 'pcst%'"));
        async Task<string> Traces() => JsonSerializer.Serialize(await db.QueryAsync(@"SELECT EmployeeId,ComponentCode,ComponentName,ParentComponentCode,
TraceOrder,RuleUsed,FormulaUsed,BaseAmount,Factor,CalculatedAmount,CAST(InputJson AS CHAR) Inputs,CAST(OutputJson AS CHAR) Outputs
FROM payroll_calculation_traces WHERE PayRunId=@Id ORDER BY EmployeeId,TraceOrder", new { run.Id }), Json);
        var original = await Traces(); Assert.DoesNotContain("nativeCurrency", original);
        await using (var tx = await db.BeginTransactionAsync())
        {
            var rows = (await db.QueryAsync<PayRunEmployee>("SELECT * FROM payrunemployees WHERE PayRunId=@Id ORDER BY EmployeeId", new { run.Id }, tx)).ToArray();
            await db.ExecuteAsync("DELETE FROM payroll_calculation_traces WHERE PayRunId=@Id", new { run.Id }, tx);
            var oracle = typeof(PayRunRepository).GetMethod("WriteCalculationTracesAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            foreach (var row in rows) await (Task)oracle.Invoke(null, [db, tx, row, 30])!;
            await tx.CommitAsync();
        }
        Assert.Equal(original, await Traces());
    }

    [LocalFact]
    public async Task Actual_second_employee_trace_failure_rolls_back_all_financial_and_currency_rows()
    {
        var before = await SourceWitnessAsync(); await using var db = await F.OpenAsync();
        await db.ExecuteAsync("""
CREATE TRIGGER currency_97728_fail_trace BEFORE INSERT ON payroll_calculation_traces FOR EACH ROW
BEGIN IF NEW.EmployeeId=9772802 AND JSON_EXTRACT(NEW.OutputJson,'$.nativeCurrencyStage') IS NOT NULL
THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Synthetic currency rollback'; END IF; END
""");
        await Assert.ThrowsAsync<MySqlConnector.MySqlException>(() => Runs(true).CreateAsync(Request()));
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM payruns WHERE ClientId=97728"));
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM payrunemployees WHERE ClientId=97728"));
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM payroll_calculation_traces WHERE EmployeeId IN(9772801,9772802)"));
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM modulesettings WHERE client_id=97728 AND ModuleCode LIKE 'pcst%'"));
        Assert.Equal(before, await SourceWitnessAsync());
    }

    [LocalFact]
    public async Task Stored_currency_receipt_rejects_changed_trace_and_native_money_without_writing_on_read()
    {
        var repo = Runs(true); var run = await repo.CreateAsync(Request()); Assert.NotNull(run);
        await using var db = await F.OpenAsync();
        var traces = await db.QueryAsync("SELECT Id,InputJson,OutputJson FROM payroll_calculation_traces WHERE PayRunId=@Id ORDER BY Id", new { run.Id });
        await db.ExecuteAsync("UPDATE payroll_calculation_traces SET OutputJson=JSON_SET(OutputJson,'$.changed',TRUE) WHERE PayRunId=@Id AND ComponentCode=@Pf", new { run.Id, Pf });
        var before = await SourceWitnessAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync(repo, run.Id));
        Assert.Equal(before, await SourceWitnessAsync());
        foreach (var trace in traces) await db.ExecuteAsync("UPDATE payroll_calculation_traces SET InputJson=@InputJson,OutputJson=@OutputJson WHERE Id=@Id", (object)trace);
        Assert.NotNull(await ReadAsync(repo, run.Id));
        await db.ExecuteAsync("UPDATE payrunemployees SET NetPay=NetPay+0.01 WHERE PayRunId=@Id AND EmployeeId=9772801", new { run.Id });
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync(repo, run.Id));
    }

    [LocalFact]
    public async Task Changed_trace_epoch_invalidates_old_receipt_even_when_all_saved_money_is_unchanged()
    {
        var repo = Runs(true); var run = await repo.CreateAsync(Request()); Assert.NotNull(run);
        await using var db = await F.OpenAsync();
        var before = await SourceWitnessAsync();
        // Fault injection into owned synthetic trace evidence, not a run-status
        // transition or an authorized legacy-Draft recapture workflow.
        var first = await db.ExecuteScalarAsync<long>("SELECT MIN(Id) FROM payroll_calculation_traces WHERE PayRunId=@Id", new { run.Id });
        await db.ExecuteAsync("UPDATE payroll_calculation_traces SET Id=Id+100000000 WHERE PayRunId=@Id", new { run.Id });
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync(repo, run.Id));
        Assert.Equal(2, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM payrunemployees WHERE PayRunId=@Id AND NetPay=7172.64", new { run.Id }));
        Assert.True(await db.ExecuteScalarAsync<long>("SELECT MIN(Id) FROM payroll_calculation_traces WHERE PayRunId=@Id", new { run.Id }) > first);
        Assert.Equal(before, await SourceWitnessAsync());
    }

    [LocalFact]
    public async Task Protected_currency_receipt_rejects_cross_purpose_ciphertext_and_foreign_client_copy()
    {
        var repo = Runs(true); var run = await repo.CreateAsync(Request()); Assert.NotNull(run);
        await using var db = await F.OpenAsync();
        var code = "pcst.active:" + run.Id;
        var pointer = await db.ExecuteScalarAsync<string>("SELECT CAST(SettingsJson AS CHAR) FROM modulesettings WHERE client_id=97728 AND ModuleCode=@Code", new { Code = code });
        var profileCipher = await db.ExecuteScalarAsync<string>("SELECT CAST(SettingsJson AS CHAR) FROM modulesettings WHERE client_id=97728 AND ModuleCode=@Code", new { Code = "payslip.design:" + designId });
        await db.ExecuteAsync("UPDATE modulesettings SET SettingsJson=CAST(@Value AS JSON) WHERE client_id=97728 AND ModuleCode=@Code", new { Value = profileCipher, Code = code });
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync(repo, run.Id));
        await db.ExecuteAsync("UPDATE modulesettings SET SettingsJson=CAST(@Value AS JSON) WHERE client_id=97728 AND ModuleCode=@Code", new { Value = pointer, Code = code });
        await db.ExecuteAsync("INSERT INTO modulesettings(client_id,ModuleCode,IsEnabled,SettingsJson) VALUES(197728,@Code,TRUE,CAST(@Value AS JSON))", new { Code = code, Value = pointer });
        try
        {
            await using var tx = await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, isReadOnly: true);
            await Assert.ThrowsAsync<InvalidOperationException>(() => repo.ReadNativeCurrencyStageAsync(db, tx, 197728, run.Id, One));
            await tx.RollbackAsync();
        }
        finally { await db.ExecuteAsync("DELETE FROM modulesettings WHERE client_id=197728 AND ModuleCode=@Code", new { Code = code }); }
        Assert.NotNull(await ReadAsync(repo, run.Id));
    }

    [LocalFact]
    public async Task Whole_total_mismatch_is_not_hidden_by_matching_each_native_raw_component()
    {
        await using var db = await F.OpenAsync();
        foreach (var row in batch.Rows)
        {
            row.Deductions.Single(l => l.Label == "ESI").Amount = 0;
            row.DeclaredDeductions = 986.496m; row.NetPay = 7234.304m;
        }
        foreach (var row in source.Rows)
        {
            row.Cells[5]!.Value = "0"; row.Cells[5]!.FormulaText = "0";
            row.Cells[6]!.Value = "7234.30400000000013";
        }
        var hash = ExcelPayslipRepository.EmploymentSourceHash(batch, source);
        var savedRequest = JsonSerializer.Deserialize<ExcelPayrollSetupRequest>((await db.ExecuteScalarAsync<string>("SELECT CAST(request_json AS CHAR) FROM excel_payroll_setup_imports WHERE id=@ImportId", new { ImportId }))!, Json)!;
        var receipt = JsonSerializer.Deserialize<ExcelPayrollSetupResult>((await db.ExecuteScalarAsync<string>("SELECT CAST(result_json AS CHAR) FROM excel_payroll_setup_imports WHERE id=@ImportId", new { ImportId }))!, Json)!;
        savedRequest.Components.Single(c => c.Code == Esi).Formula = "0";
        savedRequest.AttendanceReview!.SourceHash = hash; receipt.Transfer!.SourceHash = hash;
        await db.ExecuteAsync("""
UPDATE salarystructurelines SET Formula='0' WHERE StructureId=97728 AND ComponentId='9772803';
UPDATE excel_payslip_batches SET batch_json=CAST(@BatchJson AS JSON) WHERE client_id=97728 AND id=@BatchId;
UPDATE excel_payslip_calculation_sources SET source_json=CAST(@SourceJson AS JSON) WHERE client_id=97728 AND batch_id=@BatchId;
UPDATE excel_payroll_setup_imports SET request_json=CAST(@Request AS JSON),result_json=CAST(@Receipt AS JSON) WHERE id=@ImportId;
""", new { BatchId, ImportId, BatchJson = JsonSerializer.Serialize(batch, Json), SourceJson = JsonSerializer.Serialize(source, Json),
            Request = JsonSerializer.Serialize(savedRequest, Json), Receipt = JsonSerializer.Serialize(receipt, Json) });
        await SaveDesignAsync();
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => Runs(true).CreateAsync(Request()));
        Assert.Contains("whole source totals", failure.Message);
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM payrunemployees WHERE ClientId=97728"));
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM modulesettings WHERE client_id=97728 AND ModuleCode LIKE 'pcst%'"));
    }
}
