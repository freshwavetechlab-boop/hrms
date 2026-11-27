using System.Data;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Tests.Fixtures;

namespace Payroll.API.Tests.Services;

[Collection("ReviewedPayroll")]
public sealed class ReviewedSourceTdsDatabaseTests : IAsyncLifetime
{
    private const int Client = 97748, EmployeeId = 9774801, TaxVersion = 2_000_009_748;
    private const string Batch = "97748000000000000000000000000001", Import = "97748000000000000000000000000002", TaxCode = "SYNTH_TDS_97748";
    private readonly ReviewedPayrollFixture? fixture = Environment.GetEnvironmentVariable("FREVO_REVIEWED_PAYROLL_TEST_DB") is string value ? new(value) : null;
    private ReviewedPayrollFixture F => fixture ?? throw new InvalidOperationException("Guarded loopback fixture required.");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed class IsolatedTheoryAttribute : TheoryAttribute
    { public IsolatedTheoryAttribute() { if (Environment.GetEnvironmentVariable("FREVO_REVIEWED_PAYROLL_TEST_DB") is null) Skip = "Explicit sentinel-guarded synthetic payroll database required."; } }
    public async Task InitializeAsync()
    {
        if (fixture is null) return;
        await F.InitializeAsync(); await using var db = await F.OpenAsync();
        await db.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ReviewedSourceTds", "schema.sql")));
        foreach (var (table, column, definition) in new (string, string, string)[]
        {
            ("tax_client_settings", "default_regime", "VARCHAR(20) DEFAULT 'New'"), ("tax_client_settings", "poi_processing_month", "VARCHAR(7) NULL"),
            ("tax_rule_versions", "version_number", "INT DEFAULT 1"), ("tax_rule_versions", "effective_from", "DATE NULL"),
            ("tax_rule_versions", "effective_to", "DATE NULL"), ("tax_rule_versions", "is_published", "BOOLEAN DEFAULT FALSE"),
            ("tax_rule_versions", "source", "VARCHAR(250) DEFAULT ''"), ("tax_rule_versions", "notes", "VARCHAR(1000) DEFAULT ''"),
            ("tax_computation_snapshots", "client_id", "INT DEFAULT 0"), ("tax_computation_snapshots", "financial_year", "VARCHAR(10) DEFAULT ''"),
            ("tax_computation_snapshots", "pay_period", "VARCHAR(7) DEFAULT ''"), ("tax_computation_snapshots", "financial_year_id", "INT NULL"),
            ("tax_computation_snapshots", "rule_version_id", "INT DEFAULT 0"), ("tax_computation_snapshots", "regime_id", "INT NULL"),
            ("tax_computation_snapshots", "regime", "VARCHAR(20) DEFAULT ''"), ("tax_computation_snapshots", "gross_salary", "DECIMAL(14,2) DEFAULT 0"),
            ("tax_computation_snapshots", "exemptions_amount", "DECIMAL(14,2) DEFAULT 0"), ("tax_computation_snapshots", "deductions_amount", "DECIMAL(14,2) DEFAULT 0"),
            ("tax_computation_snapshots", "taxable_income", "DECIMAL(14,2) DEFAULT 0"), ("tax_computation_snapshots", "tax_before_rebate", "DECIMAL(14,2) DEFAULT 0"),
            ("tax_computation_snapshots", "rebate_amount", "DECIMAL(14,2) DEFAULT 0"), ("tax_computation_snapshots", "tax_after_rebate", "DECIMAL(14,2) DEFAULT 0"),
            ("tax_computation_snapshots", "surcharge_amount", "DECIMAL(14,2) DEFAULT 0"), ("tax_computation_snapshots", "cess_amount", "DECIMAL(14,2) DEFAULT 0"),
            ("tax_computation_snapshots", "total_annual_tax", "DECIMAL(14,2) DEFAULT 0"), ("tax_computation_snapshots", "tds_deducted_till_date", "DECIMAL(14,2) DEFAULT 0"),
            ("tax_computation_snapshots", "remaining_tax", "DECIMAL(14,2) DEFAULT 0"), ("tax_computation_snapshots", "annual_tax", "DECIMAL(14,2) DEFAULT 0"),
            ("tax_computation_snapshots", "snapshot_json", "JSON NULL"), ("tax_computation_snapshots", "rule_breakup_json", "JSON NULL"),
            ("tax_computation_snapshots", "declaration_json", "JSON NULL"), ("tax_computation_snapshots", "proof_json", "JSON NULL"),
            ("tax_computation_snapshots", "calculated_by", "INT NULL"), ("tax_computation_snapshots", "created_at", "DATETIME DEFAULT CURRENT_TIMESTAMP")
        })
            if (!await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name=@table AND column_name=@column)", new { table, column }))
                await db.ExecuteAsync($"ALTER TABLE `{table}` ADD COLUMN `{column}` {definition}");
        await db.ExecuteAsync(ExcelPayrollSetupRepository.Schema);
        await CleanAsync();
        await db.ExecuteAsync(@"
INSERT INTO clients(Id,Code,Name,IsActive) VALUES(97748,'SYNTH-TDS','Synthetic source withholding',TRUE);
INSERT INTO worklocations(Id,ClientId,Name,City,State) VALUES(97748,97748,'Synthetic office','Synthetic city','Synthetic state');
INSERT INTO salarycomponents(Id,Code,Name,Category,CalculationType,ValueText,Formula,StatutoryType,ProRata,Priority,Taxable) VALUES
(9774801,'SYNTH_BASIC_97748','Synthetic basic','Earning','Fixed Amount','30000','','None',TRUE,100,TRUE),
(9774802,'SYNTH_TDS_97748','Synthetic tax','Deduction','Manual / Variable','0','','TDS',FALSE,200,FALSE);
INSERT INTO salarystructures(Id,ClientId,Name,AnnualCtc,Active) VALUES(9774800,97748,'Synthetic source withholding',360000,TRUE);
INSERT INTO salarystructurelines(StructureId,ComponentId,ValueText,CalculationType,ProRataOverride,SortOrder) VALUES
(9774800,'9774801','30000','Fixed Amount','true',1),(9774800,'9774802','0','Fixed Amount','false',2);
INSERT INTO employees(Id,ClientId,EmployeeCode,FirstName,LastName,DateOfJoining,AnnualCtc,SalaryStructureId,SalaryJson,PersonalJson,PaymentJson,WorkLocationId)
VALUES(9774801,97748,'SYNTH-TDS-001','Synthetic','Person','2026-01-01',360000,'9774800',JSON_OBJECT('9774801',30000,'9774802',0),JSON_OBJECT(),JSON_OBJECT(),97748);
INSERT INTO employeesalarycomponents(EmployeeId,ComponentId,Amount) VALUES(9774801,'9774801',30000),(9774801,'9774802',0);
INSERT INTO tax_client_settings(client_id,financial_year,enabled,project_monthly_tds,tax_deduction_component_code,active,default_regime) VALUES(97748,'2026-27',TRUE,TRUE,'SYNTH_TDS_97748',TRUE,'New');
INSERT INTO tax_rule_versions(id,financial_year,version_number,effective_from,effective_to,active,is_published,source,notes) VALUES(2000009748,'2026-27',1,'2026-10-01','2027-03-31',TRUE,TRUE,'SYNTHETIC-REVIEWED-TDS','Isolated fixture only');
INSERT INTO tax_slabs(id,rule_version_id,financial_year,regime,income_from,income_to,rate_percent,effective_from,effective_to,active,source,notes)
VALUES(2000009748,2000009748,'2026-27','New',0,NULL,10,'2026-10-01','2027-03-31',TRUE,'SYNTHETIC-REVIEWED-TDS','Isolated fixture only');");
    }
    public async Task DisposeAsync() { if (fixture is not null) await CleanAsync(); }
    private async Task CleanAsync()
    {
        await using var db = await F.OpenAsync();
        await db.ExecuteAsync(@"
DELETE FROM tax_computation_snapshots WHERE client_id=97748;DELETE FROM tax_client_settings WHERE client_id=97748;
DELETE FROM tax_slabs WHERE id=2000009748 AND source='SYNTHETIC-REVIEWED-TDS';DELETE FROM tax_rule_versions WHERE id=2000009748 AND source='SYNTHETIC-REVIEWED-TDS';
DELETE FROM excel_payroll_setup_imports WHERE client_id=97748;DELETE FROM excel_payslip_batches WHERE client_id=97748;DELETE FROM excel_payslip_calculation_sources WHERE client_id=97748;
DELETE FROM employeesalarycomponents WHERE EmployeeId=9774801;DELETE FROM employeepersonaldetails WHERE EmployeeId=9774801;DELETE FROM employees WHERE Id=9774801 AND ClientId=97748;
DELETE FROM salarystructurelines WHERE StructureId=9774800;DELETE FROM salarystructures WHERE Id=9774800 AND ClientId=97748;DELETE FROM salarycomponents WHERE Id IN(9774801,9774802) AND Code IN('SYNTH_BASIC_97748','SYNTH_TDS_97748');
DELETE FROM worklocations WHERE Id=97748 AND ClientId=97748;DELETE FROM clients WHERE Id=97748 AND Code='SYNTH-TDS';");
    }

    private async Task SeedSourceAsync(decimal amount, bool blank, bool openEnded = false)
    {
        var value = ReviewedSourceTdsTests.Fixture(amount, blank);
        value.Employee.Id = EmployeeId; value.Employee.ClientId = Client; value.Employee.WorkLocationId = Client; value.Employee.SalaryStructureId = "9774800";
        value.Batch.Id = Batch; value.Batch.ClientId = Client; value.Source.SourceBatchId = Batch; value.Request.BatchId = Batch;
        value.Request.TemplateScope!.WorkLocationIds = [Client]; value.Request.TemplateScope.OpenEnded = openEnded;
        value.Request.Components[0].Code = TaxCode; value.Request.Components[0].ExistingComponentId = "9774802";
        value.Receipt.ImportId = Import; value.Receipt.EmployeeIds = [EmployeeId];
        value.Receipt.MasterLinks = [value.Receipt.MasterLinks[0] with { EmployeeId = EmployeeId }];
        value.Receipt.TransferRows = [value.Receipt.TransferRows[0] with { EmployeeId = EmployeeId, Lines = [new(TaxCode, amount, amount, 0)] }];
        var transfer = value.Receipt.Transfer!; transfer.SourceBatchId = Batch; transfer.Components = [new(TaxCode, "Deduction", "Reuse unchanged", "9774802")];
        transfer.Templates[0].TemplateId = "9774800";
        transfer.Templates[0].Applicability = transfer.Templates[0].Applicability! with { ClientId = Client, TemplateId = "9774800", SourceBatchId = Batch };
        ReviewedSourceTdsTests.Seal(value);
        await using var db = await F.OpenAsync();
        await db.ExecuteAsync(@"
UPDATE employeesalarycomponents SET Amount=@Amount WHERE EmployeeId=9774801 AND ComponentId='9774802';
UPDATE employees SET SalaryJson=JSON_OBJECT('9774801',30000,'9774802',@Amount) WHERE Id=9774801 AND ClientId=97748;
INSERT INTO excel_payslip_batches(client_id,id,batch_json,send_state_json,created_at) VALUES(97748,@Batch,CAST(@BatchJson AS JSON),JSON_OBJECT('syntheticHistory','preserve'),UTC_TIMESTAMP());
INSERT INTO excel_payslip_calculation_sources(client_id,batch_id,source_json,created_at) VALUES(97748,@Batch,CAST(@Source AS JSON),UTC_TIMESTAMP());
INSERT INTO excel_payroll_setup_imports(id,client_id,batch_id,request_id,selection_hash,request_hash,plan_hash,status,request_json,result_json,created_by)
VALUES(@Import,97748,@Batch,@Import,REPEAT('1',64),REPEAT('2',64),REPEAT('3',64),'Applied',CAST(@Request AS JSON),CAST(@Result AS JSON),'synthetic reviewer');",
            new { Batch, Import, Amount = amount, BatchJson = JsonSerializer.Serialize(value.Batch, Json), Source = JsonSerializer.Serialize(value.Source, Json), Request = JsonSerializer.Serialize(value.Request, Json), Result = JsonSerializer.Serialize(value.Receipt, Json) });
    }

    private async Task<(PayRunEmployee Row, object Tax)> CalculateAsync(MySqlConnection db, MySqlTransaction tx, bool persist, string period = "2026-10", bool? mappedTemplateIncludesTds = null)
    {
        var employee = await EmployeeRepository.LoadRecruitmentEmployeeAsync(db, EmployeeId, tx) ?? throw new Exception("Fixture employee missing.");
        var employeeType = typeof(PayRunRepository).GetNestedType("PayRunSourceEmployee", BindingFlags.NonPublic)!;
        var nativeEmployee = JsonSerializer.Deserialize(JsonSerializer.Serialize(employee, Json), employeeType, Json)!;
        var setup = JsonNode.Parse(await PayrollDataTableStore.GetSetupJsonAsync(db, tx))!;
        if (mappedTemplateIncludesTds is bool includeTds)
        {
            var structures = setup["salaryStructures"]!.AsArray();
            var mapped = structures.Single(s => s!["id"]!.ToString() == employee.SalaryStructureId)!.DeepClone();
            mapped["id"] = "9774899";
            if (!includeTds)
            {
                var lines = mapped["lines"]!.AsArray();
                lines.Remove(lines.Single(l => l!["componentId"]!.ToString() == "9774802"));
            }
            structures.Add(mapped);
            employeeType.GetProperty("SalaryInputTemplateId")!.SetValue(nativeEmployee, employee.SalaryStructureId);
            employeeType.GetProperty("SalaryStructureId")!.SetValue(nativeEmployee, "9774899");
        }
        setup["professionalTax"] ??= new JsonObject(); setup["professionalTax"]!["enabled"] = false;
        var setupJson = setup.ToJsonString(Json); var runId = persist ? 9774800 : 0;
        var build = typeof(PayRunRepository).GetMethod("BuildEmployee", BindingFlags.NonPublic | BindingFlags.Static)!;
        PayRunEmployee Build(decimal tds, string code) => (PayRunEmployee)build.Invoke(null, [runId, nativeEmployee, setupJson, period, 31, (decimal?)31, 31m, Array.Empty<PayrollAdjustment>(), 0m, 0m, tds, false, code])!;
        var first = Build(-1, TaxCode);
        var calculate = typeof(PayRunRepository).GetMethod("CalculateMonthlyTdsAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Task task;
        try { task = (Task)calculate.Invoke(F.Runs, [db, tx, nativeEmployee, first, period, persist, null, null, setupJson])!; }
        catch (TargetInvocationException error) { ExceptionDispatchInfo.Capture(error.InnerException!).Throw(); throw; }
        await task; var tax = task.GetType().GetProperty("Result")!.GetValue(task)!; var type = tax.GetType();
        var row = (bool)type.GetProperty("Calculated")!.GetValue(tax)! ? Build((decimal)type.GetProperty("Amount")!.GetValue(tax)!, (string)type.GetProperty("ComponentCode")!.GetValue(tax)!) : first;
        row = (PayRunEmployee)typeof(PayRunRepository).GetMethod("WithSourceTdsEvidence", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [row, tax])!;
        return (row, tax);
    }

    [IsolatedTheory]
    [InlineData(2500, false)]
    [InlineData(0, false)]
    [InlineData(0, true)]
    public async Task NativeTaxSnapshotUsesActualWithholdingAndRetainsUnchangedAnnualProjection(decimal amount, bool blank)
    {
        await SeedSourceAsync(amount, blank);
        await using var db = await F.OpenAsync(); await using var tx = await db.BeginTransactionAsync();
        var (row, _) = await CalculateAsync(db, tx, persist: true);
        Assert.Equal(30000 - amount, row.NetPay); Assert.Equal(amount, row.StatutoryDeductions);
        var saved = await db.QuerySingleAsync<(decimal Monthly, decimal Annual, string Snapshot)>("SELECT monthly_tds Monthly,annual_tax Annual,CAST(snapshot_json AS CHAR) Snapshot FROM tax_computation_snapshots WHERE client_id=97748 AND employee_id=9774801 AND pay_run_id=9774800", transaction: tx);
        Assert.Equal(amount, saved.Monthly); Assert.Equal(18000, saved.Annual);
        var snapshot = JsonNode.Parse(saved.Snapshot)!;
        Assert.Equal(3000, snapshot["result"]!["MonthlyTds"]!.GetValue<decimal>());
        Assert.Equal(amount, snapshot["payrollWithholding"]!["amount"]!.GetValue<decimal>());
        using var details = JsonDocument.Parse(row.DetailsJson);
        Assert.Single(details.RootElement.EnumerateArray(), line => line.GetProperty("Id").GetString() == "REVIEWED_TDS_INPUT");
        var nativeRowId = await db.ExecuteScalarAsync<int>(@"INSERT INTO payrunemployees(PayRunId,EmployeeId,ClientId,EmployeeCode,PresentDays,PayableDays,GrossPay,StatutoryDeductions,NetPay,IsSkipped,DetailsJson)
VALUES(9774800,9774801,97748,'SYNTH-TDS-001',31,31,30000,@Amount,@Net,FALSE,CAST(@Details AS JSON));SELECT LAST_INSERT_ID();",
            new { Amount = amount, Net = row.NetPay, Details = row.DetailsJson }, tx);
        await db.ExecuteAsync("INSERT INTO payrunemployeelines(PayRunEmployeeId,PayRunId,EmployeeId,ComponentCode,StatutoryType,Amount) VALUES(@Id,9774800,9774801,@Code,'TDS',@Amount)", new { Id = nativeRowId, Code = TaxCode, Amount = amount }, tx);
        var reconciliations = new List<PayrollReconciliationResult>();
        await (Task)typeof(PayRunRepository).GetMethod("AddStatutoryReconciliationsAsync", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [db, tx, 9774800, reconciliations])!;
        var withholding = Assert.Single(reconciliations, value => value.CheckName.StartsWith("TDS reviewed withholding"));
        Assert.Equal("Passed", withholding.Status); Assert.Equal(amount, withholding.ExpectedAmount); Assert.Equal(amount, withholding.ActualAmount);
        Assert.Equal(3000, JsonNode.Parse(withholding.DetailsJson)!["ProjectedMonthlyTds"]!.GetValue<decimal>());
        await tx.RollbackAsync();
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tax_computation_snapshots WHERE client_id=97748"));
    }

    [IsolatedTheory]
    [InlineData(9905, true)]
    [InlineData(0, true)]
    [InlineData(9905, false)]
    public async Task Employee_template_policy_retains_months_reviewed_tds_or_reports_the_missing_rule(decimal amount, bool includeTds)
    {
        await SeedSourceAsync(amount, false);
        await using var db = await F.OpenAsync();
        await using var tx = await db.BeginTransactionAsync(IsolationLevel.RepeatableRead, isReadOnly: true);
        if (includeTds)
        {
            var (row, _) = await CalculateAsync(db, tx, false, mappedTemplateIncludesTds: true);
            Assert.Equal(30000 - amount, row.NetPay);
            Assert.Equal(amount, row.StatutoryDeductions);
        }
        else
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => CalculateAsync(db, tx, false, mappedTemplateIncludesTds: false));
            Assert.Contains("matching employee TDS input", error.Message);
        }
    }

    [IsolatedTheory]
    [InlineData(false, "2026-10", 0)]
    [InlineData(true, "2026-10", 3000)]
    [InlineData(false, "2026-11", 3000)]
    public async Task ReadOnlySimulationUsesSourceOnlyWithinReviewedMonthAndNeverWritesHistory(bool openEnded, string period, decimal expected)
    {
        await SeedSourceAsync(0, true, openEnded);
        await using var db = await F.OpenAsync();
        const string fingerprint = "SELECT CONCAT(SHA2(CAST(b.batch_json AS CHAR),256),SHA2(CAST(s.source_json AS CHAR),256),SHA2(CAST(b.send_state_json AS CHAR),256)) FROM excel_payslip_batches b JOIN excel_payslip_calculation_sources s ON s.client_id=b.client_id AND s.batch_id=b.id WHERE b.client_id=97748";
        var before = await db.ExecuteScalarAsync<string>(fingerprint);
        await using var tx = await db.BeginTransactionAsync(IsolationLevel.RepeatableRead, isReadOnly: true);
        var (row, _) = await CalculateAsync(db, tx, persist: false, period);
        Assert.Equal(expected, row.StatutoryDeductions); Assert.Equal(30000 - expected, row.NetPay);
        await tx.RollbackAsync();
        Assert.Equal(before, await db.ExecuteScalarAsync<string>(fingerprint));
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tax_computation_snapshots WHERE client_id=97748"));
    }
}
