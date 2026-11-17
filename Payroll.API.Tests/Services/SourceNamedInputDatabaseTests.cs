using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;
using Payroll.API.Tests.Fixtures;

namespace Payroll.API.Tests.Services;

[Collection("ReviewedPayroll")]
public sealed partial class SourceNamedInputDatabaseTests : IAsyncLifetime
{
    private const int Client = 97746, One = 9774601, Two = 9774602;
    private const string Base = "9774600", BatchId = "97746000000000000000000000000001";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ReviewedPayrollFixture? fixture = Environment.GetEnvironmentVariable("FREVO_REVIEWED_PAYROLL_TEST_DB") is string c ? new(c) : null;
    private ReviewedPayrollFixture F => fixture ?? throw new InvalidOperationException("An owned sentinel-guarded fixture is required.");
    private IConfiguration Config(bool enabled = true) => new ConfigurationBuilder().AddConfiguration(F.Config)
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Payroll:HistoricalNamedInputsEnabled"] = enabled.ToString() }).Build();
    private ExcelPayrollSetupRepository Repo(bool enabled = true) => new(Config(enabled), new EmployeeRepository(Config(enabled), null!, null!), new(Config(enabled), new(Config(enabled))));
    private static AuthUser Actor => new() { Id = Client, IsActive = true, Roles = ["super_admin"], Permissions = ["settings.manage", "employees.manage", "payroll.run"] };
    private sealed class IsolatedFactAttribute : FactAttribute { public IsolatedFactAttribute() { if (Environment.GetEnvironmentVariable("FREVO_REVIEWED_PAYROLL_TEST_DB") is null) Skip = "Explicit sentinel-guarded fixture required."; } }
    private sealed class IsolatedTheoryAttribute : TheoryAttribute { public IsolatedTheoryAttribute() { if (Environment.GetEnvironmentVariable("FREVO_REVIEWED_PAYROLL_TEST_DB") is null) Skip = "Explicit sentinel-guarded fixture required."; } }

    public async Task InitializeAsync()
    {
        if (fixture is null) return;
        await F.InitializeAsync();
        await using var db = await F.OpenAsync();
        await db.ExecuteAsync(ExcelPayrollSetupRepository.Schema);
        await db.ExecuteAsync(@"
CREATE TABLE IF NOT EXISTS excel_payslip_originals(client_id INT NOT NULL,id CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,attachment_id BIGINT NOT NULL,sha256_hash CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,PRIMARY KEY(client_id,id),UNIQUE KEY ux_excel_original_hash(client_id,sha256_hash),UNIQUE KEY ux_excel_original_attachment(attachment_id)) ENGINE=InnoDB;
CREATE TABLE IF NOT EXISTS excel_payslip_batch_originals(client_id INT NOT NULL,batch_id CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,original_id CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,original_file_name VARCHAR(255) NOT NULL,PRIMARY KEY(client_id,batch_id,original_id),INDEX ix_excel_original_refs(client_id,original_id)) ENGINE=InnoDB;
CREATE TABLE IF NOT EXISTS entity_attachments(
id BIGINT PRIMARY KEY AUTO_INCREMENT,public_id CHAR(36) NOT NULL,client_id INT NOT NULL,attachment_attribute_id BIGINT NOT NULL,field_configuration_id BIGINT NOT NULL,storage_server_id BIGINT NOT NULL,entity_type VARCHAR(60) NOT NULL,entity_id BIGINT NOT NULL,
original_file_name VARCHAR(255) NOT NULL,stored_file_name VARCHAR(120) NOT NULL,storage_key VARCHAR(700) NOT NULL,file_extension VARCHAR(20) NOT NULL,declared_mime_type VARCHAR(150) NOT NULL DEFAULT '',detected_mime_type VARCHAR(150) NOT NULL,file_size_bytes BIGINT NOT NULL,sha256_hash CHAR(64) NOT NULL,
version_number INT NOT NULL DEFAULT 1,document_number VARCHAR(180) NOT NULL DEFAULT '',issue_date DATE NULL,expiry_date DATE NULL,verification_status VARCHAR(30) NOT NULL DEFAULT 'NotRequired',verified_by_user_id INT NULL,verified_at_utc DATETIME(6) NULL,rejection_reason VARCHAR(500) NOT NULL DEFAULT '',malware_scan_status VARCHAR(30) NOT NULL DEFAULT 'NotScanned',malware_scanned_at_utc DATETIME(6) NULL,
is_current BOOLEAN NOT NULL DEFAULT TRUE,is_deleted BOOLEAN NOT NULL DEFAULT FALSE,uploaded_by_user_id INT NOT NULL,uploaded_by_external_subject_id BIGINT NULL,uploaded_at_utc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),deleted_by_user_id INT NULL,deleted_at_utc DATETIME(6) NULL,
UNIQUE KEY UX_entity_attachments_public_id(public_id),INDEX IX_entity_attachments_entity(client_id,entity_type,entity_id,is_current,is_deleted),INDEX IX_entity_attachments_field(field_configuration_id,entity_type,entity_id),INDEX IX_entity_attachments_storage(storage_server_id),INDEX IX_entity_attachments_expiry(expiry_date,is_current,is_deleted),INDEX IX_entity_attachments_hash(sha256_hash));");
        if (!await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='modulesettings' AND column_name='Id')"))
            await db.ExecuteAsync("ALTER TABLE modulesettings ADD COLUMN Id BIGINT NOT NULL AUTO_INCREMENT UNIQUE");
        await CleanAsync();
        await db.ExecuteAsync(@"
INSERT INTO clients(Id,Code,Name,IsActive) VALUES(97746,'SOURCE-NAMED','Synthetic first source',TRUE);
INSERT INTO worklocations(Id,ClientId,Name,City,State) VALUES(97746,97746,'Synthetic office','Synthetic city','Synthetic state');
INSERT INTO salarycomponents(Id,Code,Name,Category,ComponentRole,StatutoryType,CalculationType,ValueText,Formula,ProRata,Taxable,Priority,PayType,Active) VALUES
(9774601,'FS_BASIC','Basic','Earning','Regular Earning','None','Fixed Amount','0','',TRUE,TRUE,10,'Fixed Pay',TRUE),
(9774602,'FS_ALLOWANCE','Allowance','Earning','Regular Earning','None','Fixed Amount','0','',TRUE,TRUE,20,'Fixed Pay',TRUE),
(9774603,'FS_OVERTIME','Overtime','Earning','Regular Earning','None','Formula','0','',FALSE,TRUE,30,'Variable Pay',TRUE),
(9774604,'FS_DEDUCTION','Deduction','Deduction','Regular Deduction','None','Fixed Amount','100','',FALSE,TRUE,40,'Fixed Pay',TRUE),
(9774605,'FS_EMPLOYER','Employer','Benefit','Employer Benefit','None','Fixed Amount','50','',FALSE,TRUE,50,'Fixed Pay',TRUE);
INSERT INTO salarystructures(Id,ClientId,ClientRef,Name,AnnualCtc,Active) VALUES(9774600,97746,'97746','Unassigned exact base',0,TRUE);
INSERT INTO salarystructurelines(StructureId,ComponentId,CalculationType,ValueText,Formula,ProRataOverride,SortOrder) VALUES
(9774600,'9774601','Fixed Amount','0','','true',0),(9774600,'9774602','Fixed Amount','0','','true',1),
(9774600,'9774603','Formula','0','250*OT_HOURS','false',2),(9774600,'9774604','Fixed Amount','100','','false',3),(9774600,'9774605','Fixed Amount','50','','false',4);
INSERT INTO employees(Id,ClientId,EmployeeCode,FirstName,LastName,DateOfJoining,AnnualCtc,SalaryStructureId,SalaryJson,PersonalJson,PaymentJson,WorkLocationId) VALUES
(9774601,97746,'FIRST-ONE','First','Person','2026-01-01',446400,'',JSON_OBJECT(),JSON_OBJECT(),JSON_OBJECT(),97746),
(9774602,97746,'FIRST-TWO','Second','Person','2026-01-01',223200,'',JSON_OBJECT(),JSON_OBJECT(),JSON_OBJECT(),97746);
INSERT INTO employeepersonaldetails(EmployeeId,AadhaarNumber) VALUES(9774601,'234567890123'),(9774602,'345678901234');
INSERT INTO tax_client_settings(client_id,financial_year,enabled,project_monthly_tds,active) VALUES(97746,'2026-27',FALSE,FALSE,TRUE);"
        );
        var (batch, source) = Source();
        Assert.Null(batch.ProfileLinks); // The normal protected setup approval must supply the durable binding.
        await db.ExecuteAsync(@"INSERT INTO excel_payslip_batches(client_id,id,batch_json,send_state_json,created_at) VALUES(97746,@Id,CAST(@Batch AS JSON),JSON_OBJECT('retainedHistory','original'),UTC_TIMESTAMP());
INSERT INTO excel_payslip_calculation_sources(client_id,batch_id,source_json,created_at) VALUES(97746,@Id,CAST(@Source AS JSON),UTC_TIMESTAMP())",
            new { Id = BatchId, Batch = JsonSerializer.Serialize(batch, Json), Source = JsonSerializer.Serialize(source, Json) });
    }

    public async Task DisposeAsync() { if (fixture is not null) await CleanAsync(); }
    private async Task CleanAsync()
    {
        await using var db = await F.OpenAsync();
        await db.ExecuteAsync(@"
DELETE FROM payroll_calculation_traces WHERE PayRunId IN(SELECT Id FROM payruns WHERE ClientId=97746);
DELETE FROM payrunemployeelines WHERE PayRunId IN(SELECT Id FROM payruns WHERE ClientId=97746);
DELETE FROM payrun_step_logs WHERE PayRunId IN(SELECT Id FROM payruns WHERE ClientId=97746);
DELETE FROM payroll_validation_issues WHERE PayRunId IN(SELECT Id FROM payruns WHERE ClientId=97746);
DELETE FROM payroll_reconciliation_results WHERE PayRunId IN(SELECT Id FROM payruns WHERE ClientId=97746);
DELETE FROM tax_computation_snapshots WHERE pay_run_id IN(SELECT Id FROM payruns WHERE ClientId=97746);
DELETE FROM payrunemployees WHERE ClientId=97746;DELETE FROM payruns WHERE ClientId=97746;
DELETE FROM modulesettings WHERE client_id=97746;DELETE FROM tax_client_settings WHERE client_id=97746;
DELETE FROM attendance_group_employees WHERE employee_id IN(9774601,9774602);DELETE FROM attendance_groups WHERE client_id=97746;
DELETE FROM employee_payroll_location_allocations WHERE client_id=97746;DELETE FROM employee_payroll_location_assignments WHERE client_id=97746;
DELETE FROM excel_payroll_setup_imports WHERE client_id=97746;DELETE FROM employee_monthly_attendance WHERE client_id=97746;
DELETE FROM employee_daily_attendance WHERE client_id=97746;DELETE FROM excel_payslip_batches WHERE client_id=97746;DELETE FROM excel_payslip_calculation_sources WHERE client_id=97746;
DELETE FROM employee_it0008_basic_pay WHERE ClientId=97746;DELETE FROM employee_it0001_org_assignment WHERE ClientId=97746;
DELETE FROM employeesalarycomponents WHERE EmployeeId IN(9774601,9774602);DELETE FROM employeepersonaldetails WHERE EmployeeId IN(9774601,9774602);
DELETE FROM employeepaymentdetails WHERE EmployeeId IN(9774601,9774602);DELETE FROM employees WHERE ClientId=97746 OR Id IN(9774601,9774602);
DELETE FROM salarystructurelines WHERE StructureId IN(SELECT Id FROM salarystructures WHERE ClientId=97746);DELETE FROM salarystructures WHERE ClientId=97746;
DELETE FROM salarycomponents WHERE Id BETWEEN 9774601 AND 9774605;DELETE FROM worklocations WHERE ClientId=97746;DELETE FROM clients WHERE Id IN(97746,97747);");
    }

    private static (ExcelPayslipBatch Batch, ExcelPayslipCalculationSource Source) Source()
    {
        var labels = new[] { "Employee code", "Employee name", "Present days", "Payroll days", "Monthly basic rate", "Monthly allowance rate", "Overtime hours", "Basic", "Allowance", "Overtime", "Deduction", "Employer", "Gross", "Total deductions", "Net pay", "Aadhaar" };
        var kinds = new[] { "employeeCode", "employeeName", "info", "info", "info", "info", "info", "earning", "earning", "earning", "deduction", "employer", "grossTotal", "deductionTotal", "netPay", "info" };
        var source = new ExcelPayslipCalculationSource { SheetName = "Source", HeaderRow = 1, ColumnCount = 16,
            Columns = labels.Select((label, index) => new ExcelPayslipCalculationColumn { ColumnIndex = index, Label = label, SourceHeader = label, Kind = kinds[index] }).ToList(),
            Rows = [new() { SourceRow = 1, Cells = labels.Select(l => (ExcelPayslipCalculationCell?)new() { Value = l, DataType = "Text" }).ToList() }] };
        var batch = new ExcelPayslipBatch { Id = BatchId, ClientId = Client, Month = "2026-10", ClientName = "Synthetic first source", SheetName = "Source", HeaderRow = 1 };
        foreach (var (index, code, name, basic, allowance, days, hours) in new[] { (2, "FIRST-ONE", "First Person", 31000m, 6200m, 30m, 0m), (3, "FIRST-TWO", "Second Person", 15500m, 3100m, 29m, 0.5m) })
        {
            var earnedBasic = basic / 31 * days; var earnedAllowance = allowance / 31 * days; var ot = 250 * hours; var gross = earnedBasic + earnedAllowance + ot;
            var values = new[] { code, name, days.ToString(CultureInfo.InvariantCulture), "31", basic.ToString(CultureInfo.InvariantCulture), allowance.ToString(CultureInfo.InvariantCulture), hours.ToString(CultureInfo.InvariantCulture), earnedBasic.ToString(CultureInfo.InvariantCulture), earnedAllowance.ToString(CultureInfo.InvariantCulture), ot.ToString(CultureInfo.InvariantCulture), "100", "50", gross.ToString(CultureInfo.InvariantCulture), "100", (gross - 100).ToString(CultureInfo.InvariantCulture), index == 2 ? "2345  6789 0123" : "345678901234" };
            var cells = values.Select(v => (ExcelPayslipCalculationCell?)new() { Value = v, DataType = "Number" }).ToList();
            cells[0]!.DataType = cells[1]!.DataType = cells[15]!.DataType = "Text";
            cells[7]!.FormulaText = $"E{index}/D{index}*C{index}"; cells[8]!.FormulaText = $"F{index}/D{index}*C{index}"; cells[9]!.FormulaText = $"250*G{index}";
            cells[12]!.FormulaText = $"H{index}+I{index}+J{index}"; cells[13]!.FormulaText = $"K{index}"; cells[14]!.FormulaText = $"M{index}-N{index}";
            source.Rows.Add(new() { SourceRow = index, Cells = cells });
            batch.Rows.Add(new() { Id = "first-" + index, SourceRow = index, EmployeeCode = code, EmployeeName = name, NetPay = gross - 100, DeclaredGross = gross, DeclaredDeductions = 100,
                Information = Enumerable.Range(2, 5).Append(15).Select(i => new ExcelPayslipText { Label = labels[i], Value = values[i] }).ToList(),
                Earnings = [new() { Label = "Basic", Amount = earnedBasic }, new() { Label = "Allowance", Amount = earnedAllowance }, new() { Label = "Overtime", Amount = ot }],
                Deductions = [new() { Label = "Deduction", Amount = 100 }], EmployerContributions = [new() { Label = "Employer", Amount = 50 }] });
        }
        return (batch, source);
    }

    private static ExcelPayrollSetupRequest Request() => new()
    {
        BatchId = BatchId, RowIds = ["first-2", "first-3"], TemplateName = "One reusable source template", EffectiveFrom = DateTime.UtcNow.ToString("yyyy-MM-dd"),
        PayrollDaysColumnIndex = 3, PayableDaysColumnIndex = 2, PresentDaysColumnIndex = 2, WorkWeek = "Monday,Tuesday,Wednesday,Thursday,Friday,Saturday,Sunday",
        AttendanceCycleStartDay = 1, AttendanceCycleEndDay = 31, PayrollReportGenerationDay = 4, TransferReviewVersion = 1, ReviewedPolicyScope = true, ReviewedFixedInputs = true,
        CreateMissingDropdownValues = false, ImportAttendance = true, TemplateScopeReviewVersion = 1,
        TemplateScope = new() { Reviewed = true, OpenEnded = true, LocationMode = "Listed", WorkLocationIds = [Client], CategoryKind = "AllClientCategories", DivisorMeaning = "CalendarDays", PayableMeaning = "AlreadyPayable", PolicyBasis = "SavedSourceRules" },
        EmployeeDecisions = [new() { RowId = "first-2", MatchedEmployeeId = One }, new() { RowId = "first-3", MatchedEmployeeId = Two }],
        Components = [
            new() { Key = "basic", ExistingComponentId = "9774601", Code = "FS_BASIC", Name = "Basic", SourceColumnIndex = 4, ExpectedColumnIndex = 7, Priority = 10, PayType = "Fixed Pay" },
            new() { Key = "allowance", ExistingComponentId = "9774602", Code = "FS_ALLOWANCE", Name = "Allowance", SourceColumnIndex = 5, ExpectedColumnIndex = 8, Priority = 20, PayType = "Fixed Pay" },
            new() { Key = "overtime", ExistingComponentId = "9774603", Code = "FS_OVERTIME", Name = "Overtime", CalculationType = "Formula", Formula = "250*OT_HOURS", ProRata = false, ExpectedColumnIndex = 9, Priority = 30, PayType = "Variable Pay" },
            new() { Key = "deduction", ExistingComponentId = "9774604", Code = "FS_DEDUCTION", Name = "Deduction", Category = "Deduction", ComponentRole = "Regular Deduction", Value = "100", ProRata = false, ExpectedColumnIndex = 10, Priority = 40, PayType = "Fixed Pay" },
            new() { Key = "employer", ExistingComponentId = "9774605", Code = "FS_EMPLOYER", Name = "Employer", Category = "Benefit", ComponentRole = "Employer Benefit", Value = "50", ProRata = false, ExpectedColumnIndex = 11, Priority = 50, PayType = "Fixed Pay" }],
        LocationAllocations = new() { PayrollPeriod = "2026-10", ConfigurationEffectiveFrom = DateTime.UtcNow.ToString("yyyy-MM-dd"), Reviewed = true, ReviewedSettingsApplicability = true,
            Groups = [new() { EmployeeId = One, Parts = [new() { RowId = "first-2", WorkLocationId = Client, TemplateId = Base, ComponentScopes = Scopes() }] },
                new() { EmployeeId = Two, Parts = [new() { RowId = "first-3", WorkLocationId = Client, TemplateId = Base, ComponentScopes = Scopes() }] }] }
    };
    private static Dictionary<string, string> Scopes() => Enumerable.Range(9774601, 5).ToDictionary(i => i.ToString(), _ => "SourceFormulaPerPart");
    private static List<NamedFormulaInputBinding> Bindings => [new("OT_HOURS", 6, 1, "Overtime hours")];

    private async Task<ExcelPayrollSetupRequest> ConfigureAsync()
    {
        await using var db = await F.OpenAsync();
        var batch = JsonSerializer.Deserialize<ExcelPayslipBatch>(await db.ExecuteScalarAsync<string>("SELECT CAST(batch_json AS CHAR) FROM excel_payslip_batches WHERE client_id=97746"), Json)!;
        var source = JsonSerializer.Deserialize<ExcelPayslipCalculationSource>(await db.ExecuteScalarAsync<string>("SELECT CAST(source_json AS CHAR) FROM excel_payslip_calculation_sources WHERE client_id=97746"), Json)!;
        var request = Request();
        request.AttendanceReview = new() { Mode = "SourceSeed", SourceHash = ExcelPayslipRepository.EmploymentSourceHash(batch, source), SourceMonth = batch.Month,
            CycleStart = "2026-10-01", CycleEnd = "2026-10-31", PayableMeaning = "AlreadyPayable", PresentMeaning = "ActualWorked", Reviewed = true };
        request.AttendanceReview.BoundSourceInputs = ExcelPayrollSetupRepository.AttendanceBinding(request);
        var one = JsonSerializer.Deserialize<ExcelPayrollSetupRequest>(JsonSerializer.Serialize(request, Json), Json)!;
        one.RowIds = ["first-2"]; one.EmployeeDecisions.RemoveAll(d => d.RowId != "first-2"); one.LocationAllocations = null;
        var declaration = new NamedFormulaTemplateRequest { ClientId = Client, ExistingTemplateId = Base, Name = "One reusable source template", SourceRowId = "first-2",
            SourceHash = request.AttendanceReview.SourceHash, SourceReview = one, Definitions = [new("OT_HOURS", "Overtime hours", "Hours", 2, 0, 1000)], Bindings = Bindings, HistoricalSalaryUse = true };
        var preview = await Repo().PreviewNamedFormulaTemplateAsync(declaration, Actor);
        Assert.True(preview.CanSave, string.Join(";", preview.Blockers));
        var managed = await Repo().SaveNamedFormulaTemplateAsync(new(declaration, preview.PlanHash, Guid.NewGuid().ToString("N"), true), Actor);
        request.NamedInputReview = new() { TemplateId = managed.TemplateId, DeclarationHash = managed.DeclarationHash, Bindings = Bindings };
        foreach (var part in request.LocationAllocations!.Groups.SelectMany(g => g.Parts)) part.TemplateId = managed.TemplateId;
        // Ordinary removal of a never-assigned base leaves exactly one record.
        await using var tx = await db.BeginTransactionAsync();
        var before = JsonNode.Parse(await PayrollDataTableStore.GetSetupJsonAsync(db, tx))!.AsObject(); var after = before.DeepClone().AsObject();
        var structures = after["salaryStructures"]!.AsArray(); structures.Remove(structures.Single(n => n!["id"]!.ToString() == Base));
        await PayrollDataTableStore.SaveSelectedClientSetupJsonAsync(db, tx, Client, before, after); await tx.CommitAsync();
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM salarystructures WHERE ClientId=97746"));
        return request;
    }

    private async Task<(ExcelPayrollSetupRequest Request, ExcelPayrollSetupResult Result, ExcelPayrollSetupApplyRequest Command)> ApplyAsync()
    {
        var request = await ConfigureAsync(); var preview = await Repo().PreviewAsync(Client, request);
        Assert.True(preview.CanApply, string.Join(";", preview.Issues.Select(i => i.Message)));
        var command = new ExcelPayrollSetupApplyRequest { Request = request, PlanHash = preview.PlanHash, RequestId = Guid.NewGuid().ToString("N") };
        var result = await Repo().ApplyAsync(Client, command, "Synthetic reviewer", Actor.Id);
        return (request, result, command);
    }

    [IsolatedFact]
    public async Task Normal_first_source_creates_atomic_signed_assignments_without_profile_links_or_seed_run_and_native_reader_uses_each_rows_hours()
    {
        var applied = await ApplyAsync();
        Assert.Equal(2, applied.Result.LocationAssignmentReceipts.Count);
        Assert.Equal(2, applied.Result.LocationAssignmentReceipts.Select(r => r.AssignmentId).Distinct().Count());
        Assert.True((await Repo(false).ApplyAsync(Client, applied.Command, "Synthetic reviewer", Actor.Id)).Replayed);
        var runs = new PayRunRepository(Config(false), new(Config(false)));
        var first = await runs.SimulateEmployeeAsync(new() { Mode = "Employee", ClientId = Client, EmployeeId = One, PayPeriod = "2026-10" }, default);
        var second = await runs.SimulateEmployeeAsync(new() { Mode = "Employee", ClientId = Client, EmployeeId = Two, PayPeriod = "2026-10" }, default);
        Assert.True(first.Complete, string.Join(";", first.Issues.Select(i => i.Message))); Assert.True(second.Complete, string.Join(";", second.Issues.Select(i => i.Message)));
        Assert.Equal(35900m, first.Net); Assert.Equal(17425m, second.Net);
        Assert.Equal(0m, Assert.Single(first.LocationAllocations!.Parts).NamedInputs!.Values["OT_HOURS"]);
        Assert.Equal(0.5m, Assert.Single(second.LocationAllocations!.Parts).NamedInputs!.Values["OT_HOURS"]);
        Assert.Equal(125m, second.Lines.Single(l => l.Code == "FS_OVERTIME").Earned);
        await using var db = await F.OpenAsync();
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM payruns WHERE ClientId=97746"));
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM salarystructures WHERE ClientId=97746"));
        Assert.Equal(2, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employees WHERE ClientId=97746 AND SalaryStructureId=''"));
        Assert.Equal(2, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM modulesettings WHERE client_id=97746 AND ModuleCode LIKE 'named-input-a:%'"));
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM modulesettings WHERE client_id=97746 AND ModuleCode LIKE 'named-input-setup:%'"));
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM modulesettings WHERE client_id=97746 AND ModuleCode LIKE 'excel_location_correction:%'"));
        Assert.Null(JsonNode.Parse(await db.ExecuteScalarAsync<string>("SELECT CAST(batch_json AS CHAR) FROM excel_payslip_batches WHERE client_id=97746"))!["profileLinks"]);
        // The first payroll is created only after the exact configuration.
        var draft = await runs.CreateAsync(new() { ClientId = Client, PayPeriod = "2026-10", PayDate = new(2026, 11, 1),
            TotalWorkingDays = 31, IncludedEmployeeIds = [One, Two], AttendanceGroupId = applied.Result.AttendanceGroupId }, "Synthetic first-source payroll");
        var native = (await db.QueryAsync<(int EmployeeId, decimal NetPay)>("SELECT EmployeeId,NetPay FROM payrunemployees WHERE PayRunId=@Id ORDER BY EmployeeId", new { draft.Id })).ToArray();
        Assert.Equal(2, native.Length); Assert.Equal(35900m, native.Single(r => r.EmployeeId == One).NetPay); Assert.Equal(17425m, native.Single(r => r.EmployeeId == Two).NetPay);
    }

    [IsolatedTheory]
    [InlineData("approval-disabled")][InlineData("approval-tampered")][InlineData("request-stale")][InlineData("result-stale")]
    [InlineData("source-stale")][InlineData("current-owner")][InlineData("manifest-reviewer")][InlineData("competing-origin")]
    [InlineData("manifest-disabled")][InlineData("assignment-source-owner")][InlineData("foreign-government")][InlineData("manifest-request")]
    public async Task First_source_readers_fail_closed_for_tamper_staleness_and_changed_current_owner(string mutation)
    {
        var applied = await ApplyAsync(); await using var db = await F.OpenAsync();
        var id = applied.Command.RequestId;
        if (mutation == "approval-disabled") await db.ExecuteAsync("UPDATE modulesettings SET IsEnabled=FALSE WHERE client_id=97746 AND ModuleCode=@code", new { code = NamedFormulaInputStore.SourceSetupApprovalPrefix + id });
        if (mutation == "approval-tampered") await db.ExecuteAsync("UPDATE modulesettings SET SettingsJson=JSON_OBJECT('version',1,'payload','invalid') WHERE client_id=97746 AND ModuleCode=@code", new { code = NamedFormulaInputStore.SourceSetupApprovalPrefix + id });
        if (mutation == "request-stale") await db.ExecuteAsync("UPDATE excel_payroll_setup_imports SET request_json=JSON_SET(request_json,'$.templateName','Changed') WHERE client_id=97746");
        if (mutation == "result-stale") await db.ExecuteAsync("UPDATE excel_payroll_setup_imports SET result_json=JSON_SET(result_json,'$.locationAssignmentReceipts[0].employeeId',9774602) WHERE client_id=97746");
        if (mutation == "source-stale") await db.ExecuteAsync("UPDATE excel_payslip_calculation_sources SET source_json=JSON_SET(source_json,'$.rows[1].cells[6].value','2') WHERE client_id=97746");
        if (mutation == "current-owner") await db.ExecuteAsync("UPDATE employees SET EmployeeCode='MOVED-CODE' WHERE Id=9774601");
        if (mutation == "foreign-government") await db.ExecuteAsync("INSERT INTO clients(Id,Code,Name,IsActive) VALUES(97747,'FOREIGN-SOURCE','Synthetic foreign owner',TRUE);UPDATE employees SET ClientId=97747 WHERE Id=9774602;UPDATE employeepersonaldetails SET AadhaarNumber='234567890123' WHERE EmployeeId=9774602");
        if (mutation == "manifest-disabled") await db.ExecuteAsync("UPDATE modulesettings SET IsEnabled=FALSE WHERE client_id=97746 AND ModuleCode=@code", new { code = ReviewedNamedFormulaInputs.AssignmentKey(applied.Result.LocationAssignmentReceipts.Single(r => r.EmployeeId == One).AssignmentId) });
        if (mutation == "assignment-source-owner") await db.ExecuteAsync("UPDATE employee_payroll_location_assignments SET source_row_id='first-3' WHERE client_id=97746 AND employee_id=9774601");
        if (mutation == "competing-origin") await db.ExecuteAsync("INSERT INTO modulesettings(client_id,ModuleCode,IsEnabled,SettingsJson) VALUES(97746,@code,FALSE,JSON_OBJECT())", new { code = ExcelPayrollSetupRepository.LocationCorrectionReceiptPrefix + id });
        if (mutation is "manifest-reviewer" or "manifest-request")
        {
            var assignment = applied.Result.LocationAssignmentReceipts.Single(r => r.EmployeeId == One).AssignmentId;
            var manifest = await NamedFormulaInputStore.ReadManifestAsync(db, null, Config(), Client, assignment);
            var forged = mutation == "manifest-reviewer" ? manifest! with { ReviewerId = 123 } : manifest! with { RequestId = Guid.NewGuid().ToString("N") };
            await db.ExecuteAsync("UPDATE modulesettings SET SettingsJson=CAST(@json AS JSON) WHERE client_id=97746 AND ModuleCode=@code",
                new { json = NamedFormulaInputStore.Protect(forged, Config(), "Assignment"), code = ReviewedNamedFormulaInputs.AssignmentKey(assignment) });
        }
        var runs = new PayRunRepository(Config(false), new(Config(false)));
        var failed = await Assert.ThrowsAsync<InvalidOperationException>(() => runs.SimulateEmployeeAsync(new() { Mode = "Employee", ClientId = Client, EmployeeId = One, PayPeriod = "2026-10" }, default));
        var expected = mutation switch
        {
            "approval-disabled" => "The authenticated first-source named approval is missing or disabled.",
            "approval-tampered" => "The named operand record cannot be authenticated.",
            "request-stale" or "result-stale" => "The protected first-source named approval no longer matches the exact Applied receipt.",
            "source-stale" => "The approved named-operand source or durable identity changed. Current contact/name snapshots are not used as immutable salary authority.",
            "current-owner" or "foreign-government" => "A first-source named assignment requires the same unique strong existing client employee and source-month employment proof.",
            "manifest-reviewer" => "The exact immutable operands are not retained by the first-source approval.",
            "competing-origin" => "The named assignment has ambiguous approval origins.",
            "manifest-disabled" => "The period named operand manifest is disabled.",
            "assignment-source-owner" => "The exact period named operands/declaration are missing or out of scope; no legacy or zero fallback is allowed.",
            "manifest-request" => "The immutable named operands have no enabled authenticated approval.",
            _ => throw new InvalidOperationException("Unknown synthetic mutation")
        };
        Assert.Equal(expected, failed.Message);
    }

    [IsolatedFact]
    public async Task New_source_writes_require_compatibility_gate_and_trusted_reviewer_without_partial_storage()
    {
        var request = await ConfigureAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Repo(false).PreviewAsync(Client, request));
        var preview = await Repo().PreviewAsync(Client, request); Assert.True(preview.CanApply, string.Join(";", preview.Issues.Select(i => i.Message)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Repo().ApplyAsync(Client, new() { Request = request, PlanHash = preview.PlanHash, RequestId = Guid.NewGuid().ToString("N") }, "Untrusted string only"));
        await using var db = await F.OpenAsync(); Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employee_payroll_location_assignments WHERE client_id=97746"));
        request.NamedInputReview = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Repo().PreviewAsync(Client, request));
    }

    [IsolatedFact]
    public async Task Late_setup_receipt_failure_rolls_back_assignments_manifests_attendance_and_approval_together()
    {
        var request = await ConfigureAsync(); var preview = await Repo().PreviewAsync(Client, request);
        Assert.True(preview.CanApply, string.Join(";", preview.Issues.Select(i => i.Message)));
        await using var db = await F.OpenAsync();
        await db.ExecuteAsync("CREATE TRIGGER source_named_late_failure BEFORE INSERT ON excel_payroll_setup_imports FOR EACH ROW BEGIN IF NEW.client_id=97746 THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='synthetic late failure'; END IF; END");
        try
        {
            await Assert.ThrowsAsync<MySqlException>(() => Repo().ApplyAsync(Client, new() { Request = request, PlanHash = preview.PlanHash, RequestId = Guid.NewGuid().ToString("N") }, "Synthetic reviewer", Actor.Id));
            Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employee_payroll_location_assignments WHERE client_id=97746"));
            Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM modulesettings WHERE client_id=97746 AND (ModuleCode LIKE 'named-input-a:%' OR ModuleCode LIKE 'named-input-setup:%')"));
            Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employee_monthly_attendance WHERE client_id=97746"));
        }
        finally { await db.ExecuteAsync("DROP TRIGGER source_named_late_failure"); }
    }
}
