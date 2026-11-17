using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;
using Payroll.API.Tests.Fixtures;
using MySqlConnector;

namespace Payroll.API.Tests.Services;

[Collection("ReviewedPayroll")]
public sealed class ExcelPayrollLocationCorrectionDatabaseTests : IAsyncLifetime
{
    private const int Client=97713, Employee=9771301, OtherEmployee=9771302, Run=9771300;
    private const string Batch="97713000000000000000000000000001", Import="97713000000000000000000000000002", Assignment="97713000000000000000000000000003";
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private readonly ReviewedPayrollFixture? fixture=Environment.GetEnvironmentVariable("FREVO_REVIEWED_PAYROLL_TEST_DB") is string connection?new(connection):null;
    private ReviewedPayrollFixture F=>fixture??throw new InvalidOperationException("Explicit guarded synthetic DB required.");
    private ExcelPayrollSetupRepository Repository=>new(F.Config,new EmployeeRepository(F.Config,null!,null!),F.Runs);
    private static AuthUser Actor=>new(){Id=97713,IsActive=true,Roles=["super_admin"],Permissions=["payroll.run","employees.manage","settings.manage"]};
    private sealed class IsolatedFactAttribute:FactAttribute{public IsolatedFactAttribute(){if(Environment.GetEnvironmentVariable("FREVO_REVIEWED_PAYROLL_TEST_DB") is null)Skip="Explicit sentinel-guarded synthetic database required.";}}
    private sealed class IsolatedTheoryAttribute:TheoryAttribute{public IsolatedTheoryAttribute(){if(Environment.GetEnvironmentVariable("FREVO_REVIEWED_PAYROLL_TEST_DB") is null)Skip="Explicit sentinel-guarded synthetic database required.";}}
    private static ExcelPayrollLocationCorrectionRequest Scope()=>new(){PayRunId=Run,BatchId=Batch,EmployeeIds=[Employee]};
    private async Task<ExcelPayrollLocationCorrectionResult> Apply(ExcelPayrollLocationCorrectionPreview preview,string? requestId=null)=>await Repository.ApplyLocationCorrectionAsync(Client,
        new(){Request=Scope(),PlanHash=preview.PlanHash,RequestId=requestId??Guid.NewGuid().ToString("N"),Acknowledged=true},Actor);
    private static async Task<string> RequiredAsync(MySqlConnection db,string sql,object? args=null)=>
        await db.ExecuteScalarAsync<string>(sql,args)??throw new InvalidOperationException("Expected synthetic evidence row is unavailable.");

    public async Task InitializeAsync()
    {
        if(fixture is null)return;
        await F.InitializeAsync();await using var db=await F.OpenAsync();
        Assert.Contains("STRICT_TRANS_TABLES",await db.ExecuteScalarAsync<string>("SELECT @@GLOBAL.sql_mode"));
        await db.ExecuteAsync(ExcelPayrollSetupRepository.Schema);
        await db.ExecuteAsync(@"
CREATE TABLE IF NOT EXISTS employee_payroll_location_assignments(id CHAR(32) PRIMARY KEY,client_id INT,employee_id INT,work_location_id INT,work_state VARCHAR(100),salary_structure_id VARCHAR(40),annual_ctc DECIMAL(18,4),salary_inputs_json JSON,component_scopes_json JSON,rules_hash CHAR(64),source_hash CHAR(64),configuration_effective_from DATE,setup_import_id CHAR(32),source_batch_id CHAR(32),source_row_id VARCHAR(100),created_by VARCHAR(190),status VARCHAR(20) NOT NULL DEFAULT 'Active',created_at DATETIME DEFAULT CURRENT_TIMESTAMP);
CREATE TABLE IF NOT EXISTS employee_payroll_location_allocations(client_id INT,employee_id INT,pay_period CHAR(7),source_part_key VARCHAR(100),assignment_id CHAR(32),payroll_days INT,present_days DECIMAL(8,4),payable_days DECIMAL(8,4),reference_assignment_id CHAR(32),setup_import_id CHAR(32),source_batch_id CHAR(32),source_row_id VARCHAR(100),source_hash CHAR(64),created_at DATETIME DEFAULT CURRENT_TIMESTAMP,PRIMARY KEY(client_id,employee_id,pay_period,source_part_key));
CREATE TABLE IF NOT EXISTS attendance_groups(id INT PRIMARY KEY,client_id INT,name VARCHAR(100),policy_batch_id VARCHAR(100),attendance_mode VARCHAR(80),attendance_cycle_start_day INT DEFAULT 1,attendance_cycle_end_day INT DEFAULT 31,is_active BOOLEAN DEFAULT TRUE);
CREATE TABLE IF NOT EXISTS attendance_group_employees(attendance_group_id INT,employee_id INT,PRIMARY KEY(attendance_group_id,employee_id));
CREATE TABLE IF NOT EXISTS payroll_calculation_traces(Id BIGINT PRIMARY KEY AUTO_INCREMENT,PayRunId INT,EmployeeId INT,EmployeeCode VARCHAR(50),ComponentCode VARCHAR(80),ComponentName VARCHAR(180),ParentComponentCode VARCHAR(80),TraceOrder INT,RuleUsed VARCHAR(250),FormulaUsed VARCHAR(500),BaseAmount DECIMAL(18,2),Factor DECIMAL(18,6),CalculatedAmount DECIMAL(18,2),InputJson JSON,OutputJson JSON,CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP);
CREATE TABLE IF NOT EXISTS payroll_reconciliation_results(Id BIGINT PRIMARY KEY AUTO_INCREMENT,PayRunId INT,CheckName VARCHAR(160),ExpectedAmount DECIMAL(18,2),ActualAmount DECIMAL(18,2),DifferenceAmount DECIMAL(18,2),Status VARCHAR(30),DetailsJson JSON,CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP);
CREATE TABLE IF NOT EXISTS tax_computation_snapshots(id BIGINT PRIMARY KEY AUTO_INCREMENT,pay_run_id INT,employee_id INT,monthly_tds DECIMAL(18,2));");
        foreach(var(table,column,definition)in new[]{("employee_monthly_attendance","lop_days","DECIMAL(8,2) DEFAULT 0"),("payrunemployees","ClientName","VARCHAR(250) DEFAULT ''"),
            ("payrunemployees","ManualTds","DECIMAL(18,2) DEFAULT 0"),("payrunemployees","PaymentStatus","VARCHAR(30) DEFAULT 'Pending'"),("payrunemployees","PaymentDate","DATE NULL"),("payruns","UpdatedAt","DATETIME DEFAULT CURRENT_TIMESTAMP"),
            ("payruns","CreatedAt","DATETIME DEFAULT CURRENT_TIMESTAMP"),("payruns","AttendancePolicyBatchId","VARCHAR(100) DEFAULT ''")})
            if(!await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name=@table AND column_name=@column)",new{table,column}))
                await db.ExecuteAsync($"ALTER TABLE `{table}` ADD COLUMN `{column}` {definition}");
        foreach(var table in new[]{"employee_payroll_location_assignments","employee_payroll_location_allocations"})
            if(!await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name=@table AND column_name='source_row')",new{table}))
                await db.ExecuteAsync($"ALTER TABLE `{table}` ADD COLUMN source_row INT NULL");
        if(!await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM information_schema.statistics WHERE table_schema=DATABASE() AND table_name='payrunemployees' AND non_unique=0 AND index_name<>'PRIMARY')"))
            await db.ExecuteAsync("ALTER TABLE payrunemployees ADD UNIQUE KEY UX_LocationCorrection_Run_Employee(PayRunId,EmployeeId)");
        await CleanAsync();
        Assert.Equal(20,await db.ExecuteScalarAsync<int>("SELECT CHARACTER_MAXIMUM_LENGTH FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='employee_payroll_location_assignments' AND COLUMN_NAME='status'"));
        var f=ExcelPayrollLocationCorrectionTests.Create();
        f.Batch.Id=Batch;f.Batch.ClientId=Client;
        f.Request.BatchId=Batch;f.Request.LocationAllocations!.Groups[0].EmployeeId=Employee;f.Request.LocationAllocations.Groups[0].ReferenceAssignmentId=Assignment;
        f.Request.LocationAllocations.Groups[0].Parts[0].TemplateId="9771300";
        f.Request.LocationAllocations.Groups[0].Parts[0].WorkLocationId=97713;
        f.Request.LocationAllocations.Groups[0].Parts[0].ComponentScopes=new(){["9771301"]="SourceFormulaPerPart",["9771302"]="SourceFormulaPerPart",["9771303"]="SourceFormulaPerPart"};
        for(var i=0;i<3;i++)f.Request.Components[i].ExistingComponentId=(9771301+i).ToString();
        f.Request.AttendanceReview!.BoundSourceInputs=ExcelPayrollSetupRepository.AttendanceBinding(f.Request);
        await db.ExecuteAsync(@"
INSERT INTO clients(Id,Code,Name,IsActive) VALUES(97713,'SYN-LOC','Synthetic location correction',TRUE);
INSERT INTO worklocations(Id,ClientId,Name,City,State) VALUES(97713,97713,'Synthetic office','Synthetic city','Synthetic state');
INSERT INTO salarycomponents(Id,Code,Name,Category,ComponentRole,StatutoryType,CalculationType,ValueText,Formula,ProRata,Taxable,Priority,PayType,Active) VALUES
(9771301,'SYN_ALLOWANCE','Synthetic allowance','Earning','Regular Earning','None','Formula','0','',FALSE,TRUE,100,'Fixed Pay',TRUE),
(9771302,'SYN_DEDUCTION','Synthetic deduction','Deduction','Regular Deduction','None','Fixed Amount','100','',FALSE,TRUE,200,'Fixed Pay',TRUE),
(9771303,'SYN_EMPLOYER','Synthetic employer','Benefit','Employer Benefit','None','Fixed Amount','50','',FALSE,TRUE,300,'Fixed Pay',TRUE);
INSERT INTO salarystructures(Id,ClientId,Name,AnnualCtc,Active) VALUES(9771300,97713,'Synthetic existing template',129600,TRUE);
INSERT INTO salarystructurelines(StructureId,ComponentId,CalculationType,ValueText,Formula,ProRataOverride,SortOrder) VALUES(9771300,'9771301','Formula','0',@Formula,'false',1),(9771300,'9771302','Fixed Amount','100','','false',2),(9771300,'9771303','Fixed Amount','50','','false',3);
INSERT INTO employees(Id,ClientId,EmployeeCode,FirstName,LastName,DateOfJoining,AnnualCtc,SalaryStructureId,SalaryJson,PersonalJson,PaymentJson,WorkLocationId) VALUES
(9771301,97713,'SYN-LOC-1','Synthetic','Person','2026-01-01',129600,'9771300',JSON_OBJECT(),JSON_OBJECT(),JSON_OBJECT(),97713),
(9771302,97713,'SYN-LOC-2','Unchanged','Person','2026-01-01',129600,'9771300',JSON_OBJECT(),JSON_OBJECT(),JSON_OBJECT(),97713);
INSERT INTO employeepersonaldetails(EmployeeId,PanNumber) VALUES(9771301,'ABCDE1234F');
INSERT INTO employee_monthly_attendance(client_id,employee_id,attendance_month,working_days,present_days,payable_days,lop_days,source_type,remarks) VALUES
(97713,9771301,'2026-10',31,30,30,1,'Monthly',''),(97713,9771302,'2026-10',31,31,31,0,'Monthly','');
INSERT INTO tax_client_settings(client_id,financial_year,enabled,project_monthly_tds,active) VALUES(97713,'2026-27',FALSE,FALSE,TRUE);
INSERT INTO attendance_groups(id,client_id,name,policy_batch_id,attendance_mode,attendance_cycle_start_day,attendance_cycle_end_day,is_active) VALUES(97713,97713,'Synthetic source policy','synthetic-97713','Monthly',1,31,TRUE);
INSERT INTO attendance_group_employees(attendance_group_id,employee_id) VALUES(97713,9771301),(97713,9771302);
INSERT INTO payruns(Id,ClientId,ClientName,PayPeriod,RunCode,RunType,PayDate,TotalWorkingDays,Status,RequestJson,PayrollCost,NetPay) VALUES(9771300,97713,'Synthetic location correction','2026-10','TEST-97713','Regular','2026-11-01',31,'Draft',JSON_OBJECT(),1,1);
INSERT INTO payrunemployees(PayRunId,EmployeeId,ClientId,EmployeeCode,EmployeeName,PresentDays,PayableDays,MonthlyGross,GrossPay,StatutoryDeductions,OneTimeEarnings,OneTimeDeductions,NetPay,DetailsJson) VALUES
(9771300,9771301,97713,'SYN-LOC-1','Synthetic Person',30,30,10800,10452,100,0,0,10352,JSON_ARRAY()),(9771300,9771302,97713,'SYN-LOC-2','Unchanged Person',31,31,10800,10800,100,0,0,10700,JSON_ARRAY());
INSERT INTO payroll_calculation_traces(PayRunId,EmployeeId,EmployeeCode,ComponentCode,ComponentName,TraceOrder,RuleUsed,FormulaUsed,BaseAmount,Factor,CalculatedAmount,InputJson,OutputJson) VALUES(9771300,9771301,'SYN-LOC-1','ORIGINAL','Original audit',1,'original rule','original formula',1,1,1,JSON_OBJECT('original',TRUE),JSON_OBJECT('original',TRUE));
INSERT INTO excel_payslip_batches(client_id,id,batch_json,send_state_json,created_at) VALUES(97713,@Batch,CAST(@BatchJson AS JSON),JSON_OBJECT('oldHistory','retained'),UTC_TIMESTAMP());
INSERT INTO excel_payslip_calculation_sources(client_id,batch_id,source_json,created_at) VALUES(97713,@Batch,CAST(@SourceJson AS JSON),UTC_TIMESTAMP());",
            new{Formula=f.Request.Components[0].Formula,Batch,BatchJson=JsonSerializer.Serialize(f.Batch,Json),SourceJson=JsonSerializer.Serialize(f.Source,Json)});
        var setup=JsonNode.Parse(await PayrollDataTableStore.GetSetupJsonAsync(db))!;
        var template=setup["salaryStructures"]!.AsArray().OfType<JsonObject>().Single(t=>t["id"]!.ToString()=="9771300");
        var rulesHash=ExcelPayrollSetupRepository.LocationSettingsRulesHash(template,setup["salaryComponents"]!.AsArray().OfType<JsonObject>().ToArray());
        var sourceHash=ExcelPayslipRepository.EmploymentSourceHash(f.Batch,f.Source);
        f.Request.AttendanceReview.SourceHash=sourceHash;
        var receipt=new ExcelPayrollSetupResult{ImportId=Import,EmployeeIds=[Employee],LocationAllocations=new(),LocationAssignmentReceipts=[new("source-one",Employee,"synthetic-part",Assignment,97713,"9771300","2026-10","2026-11-14",rulesHash,sourceHash)]};
        await db.ExecuteAsync(@"
INSERT INTO employee_payroll_location_assignments(id,client_id,employee_id,work_location_id,work_state,salary_structure_id,annual_ctc,salary_inputs_json,component_scopes_json,rules_hash,source_hash,configuration_effective_from,setup_import_id,source_batch_id,source_row_id,created_by,status) VALUES(@Assignment,97713,9771301,97713,'Synthetic state','9771300',129600,JSON_OBJECT(),JSON_OBJECT('9771301','SourceFormulaPerPart','9771302','SourceFormulaPerPart','9771303','SourceFormulaPerPart'),@rulesHash,@sourceHash,'2026-11-14',@Import,@Batch,'source-one','Synthetic reviewer','Active');
INSERT INTO employee_payroll_location_allocations(client_id,employee_id,pay_period,source_part_key,assignment_id,payroll_days,present_days,payable_days,reference_assignment_id,setup_import_id,source_batch_id,source_row_id,source_hash) VALUES(97713,9771301,'2026-10','synthetic-part',@Assignment,31,30,30,@Assignment,@Import,@Batch,'source-one',@sourceHash);
INSERT INTO excel_payroll_setup_imports(id,client_id,batch_id,request_id,selection_hash,request_hash,plan_hash,status,request_json,result_json,created_by) VALUES(@Import,97713,@Batch,@Import,@Hash,@Hash,@Hash,'Applied',CAST(@Request AS JSON),CAST(@Receipt AS JSON),'Synthetic reviewer');",
            new{Assignment,Import,Batch,rulesHash,sourceHash,Hash=new string('a',64),Request=JsonSerializer.Serialize(f.Request,Json),Receipt=JsonSerializer.Serialize(receipt,Json)});
    }
    public async Task DisposeAsync(){if(fixture is not null)await CleanAsync();}
    private async Task CleanAsync()
    {
        await using var db=await F.OpenAsync();
        await db.ExecuteAsync(@"
DROP TRIGGER IF EXISTS codex_location_97713_fail;
DELETE FROM modulesettings WHERE client_id=97713;
DELETE FROM payroll_calculation_traces WHERE PayRunId=9771300;DELETE FROM payroll_reconciliation_results WHERE PayRunId=9771300;DELETE FROM tax_computation_snapshots WHERE pay_run_id=9771300;
DELETE FROM payrunemployeelines WHERE PayRunId=9771300;DELETE FROM payrunemployees WHERE PayRunId=9771300;DELETE FROM payruns WHERE Id=9771300;
DELETE FROM payrolladjustments WHERE ClientId=97713;DELETE FROM tax_client_settings WHERE client_id=97713;
DELETE FROM attendance_group_employees WHERE attendance_group_id=97713;DELETE FROM attendance_groups WHERE client_id=97713;
DELETE FROM employee_payroll_location_allocations WHERE client_id=97713;DELETE FROM employee_payroll_location_assignments WHERE client_id=97713;DELETE FROM excel_payroll_setup_imports WHERE client_id=97713;
DELETE FROM employee_monthly_attendance WHERE client_id=97713;DELETE FROM excel_payslip_batches WHERE client_id=97713;DELETE FROM excel_payslip_calculation_sources WHERE client_id=97713;
DELETE FROM employeesalarycomponents WHERE EmployeeId IN(9771301,9771302);DELETE FROM employeepersonaldetails WHERE EmployeeId IN(9771301,9771302);DELETE FROM employeepaymentdetails WHERE EmployeeId IN(9771301,9771302);
DELETE FROM employees WHERE ClientId=97713;DELETE FROM worklocations WHERE ClientId=97713;DELETE FROM clients WHERE Id=97713;
DELETE FROM salarystructurelines WHERE StructureId IN(9771300,9771304);DELETE FROM salarystructures WHERE Id IN(9771300,9771304);DELETE FROM salarycomponents WHERE Id IN(9771301,9771302,9771303);");
    }
    private async Task<string> PreservedAsync()
    {
        await using var db=await F.OpenAsync();return await RequiredAsync(db,@"SELECT SHA2(CONCAT(
(SELECT CAST(batch_json AS CHAR) FROM excel_payslip_batches WHERE client_id=97713),(SELECT CAST(source_json AS CHAR) FROM excel_payslip_calculation_sources WHERE client_id=97713),
(SELECT CAST(send_state_json AS CHAR) FROM excel_payslip_batches WHERE client_id=97713),(SELECT CAST(DetailsJson AS CHAR) FROM payrunemployees WHERE PayRunId=9771300 AND EmployeeId=9771302),
(SELECT CAST(request_json AS CHAR) FROM excel_payroll_setup_imports WHERE client_id=97713),(SELECT CAST(result_json AS CHAR) FROM excel_payroll_setup_imports WHERE client_id=97713),
(SELECT CONCAT(AnnualCtc,SalaryStructureId,SalaryJson) FROM employees WHERE Id=9771301)),256)");
    }
    [IsolatedFact]
    public async Task Same_draft_rows_refresh_atomically_once_preserving_sources_master_history_and_other_employee()
    {
        var before=await PreservedAsync();var preview=await Repository.PreviewLocationCorrectionAsync(Client,Scope(),Actor);
        Assert.True(preview.CanApply,string.Join(";",preview.Blockers));var employee=Assert.Single(preview.Employees);Assert.Equal(10700,employee.Net);
        var id=Guid.NewGuid().ToString("N");var saved=await Apply(preview,id);Assert.False(saved.Replayed);Assert.True((await Apply(preview,id)).Replayed);
        Assert.Equal(before,await PreservedAsync());
        await using var db=await F.OpenAsync();
        Assert.Equal(employee.PayRunEmployeeId,await db.ExecuteScalarAsync<int>("SELECT Id FROM payrunemployees WHERE PayRunId=9771300 AND EmployeeId=9771301"));
        Assert.Equal(10700,await db.ExecuteScalarAsync<decimal>("SELECT NetPay FROM payrunemployees WHERE PayRunId=9771300 AND EmployeeId=9771301"));
        Assert.Equal(30,await db.ExecuteScalarAsync<decimal>("SELECT present_days FROM employee_monthly_attendance WHERE client_id=97713 AND employee_id=9771301"));
        Assert.Equal(31,await db.ExecuteScalarAsync<decimal>("SELECT payable_days FROM employee_monthly_attendance WHERE client_id=97713 AND employee_id=9771301"));
        Assert.Equal(1,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employee_payroll_location_assignments WHERE client_id=97713"));
        Assert.Equal("Draft",await RequiredAsync(db,"SELECT Status FROM payruns WHERE Id=9771300"));
        var json=await RequiredAsync(db,"SELECT CAST(SettingsJson AS CHAR) FROM modulesettings WHERE client_id=97713 AND ModuleCode=@Code",new{Code=ExcelPayrollSetupRepository.LocationCorrectionReceiptPrefix+id});
        var encrypted=JsonNode.Parse(json)!["payload"]!.ToString();Assert.DoesNotContain("Original audit",json);
        var protector=new PortableIntegrationCredentialProtector(F.Config,new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider(),Microsoft.Extensions.Logging.Abstractions.NullLogger<PortableIntegrationCredentialProtector>.Instance);
        Assert.Contains("Original audit",protector.UnprotectLocationCorrection(encrypted));
    }
    [IsolatedFact]
    public async Task Structural_template_match_runs_real_core_preview_and_apply_without_rewriting_settings_or_source()
    {
        const string configured="ROUND(((54000)*(20)%)/(PAYROLL_DAYS)*(PAYABLE_DAYS))";
        await using(var db=await F.OpenAsync())
        {
            // Seed one consistently reviewed formatting variant, rather than
            // changing a previously frozen Settings rule behind its assignment.
            await db.ExecuteAsync("UPDATE salarystructurelines SET Formula=@Formula WHERE StructureId=9771300 AND ComponentId='9771301'",new{Formula=configured});
            var setup=JsonNode.Parse(await PayrollDataTableStore.GetSetupJsonAsync(db))!;
            var template=setup["salaryStructures"]!.AsArray().OfType<JsonObject>().Single(row=>row["id"]!.ToString()=="9771300");
            var reviewedHash=ExcelPayrollSetupRepository.LocationSettingsRulesHash(template,setup["salaryComponents"]!.AsArray().OfType<JsonObject>().ToArray());
            await db.ExecuteAsync("UPDATE employee_payroll_location_assignments SET rules_hash=@Hash WHERE client_id=97713;UPDATE excel_payroll_setup_imports SET result_json=JSON_SET(result_json,'$.locationAssignmentReceipts[0].rulesHash',@Hash) WHERE client_id=97713",new{Hash=reviewedHash});
        }
        var before=await PreservedAsync();string previousHash;
        await using(var db=await F.OpenAsync())previousHash=await RequiredAsync(db,"SELECT rules_hash FROM employee_payroll_location_assignments WHERE id=@Id",new{Id=Assignment});
        var preview=await Repository.PreviewLocationCorrectionAsync(Client,Scope(),Actor);Assert.True(preview.CanApply,string.Join(";",preview.Blockers));
        var employee=Assert.Single(preview.Employees);Assert.Equal(30,employee.PresentDays);Assert.Equal(31,employee.PayableDays);Assert.Equal(10700,employee.Net);
        Assert.Equal(before,await PreservedAsync());await Apply(preview);Assert.Equal(before,await PreservedAsync());
        await using(var db=await F.OpenAsync())
        {
            Assert.Equal(configured,await RequiredAsync(db,"SELECT Formula FROM salarystructurelines WHERE StructureId=9771300 AND ComponentId='9771301'"));
            Assert.Equal(previousHash,await RequiredAsync(db,"SELECT rules_hash FROM employee_payroll_location_assignments WHERE id=@Id",new{Id=Assignment}));
            Assert.Equal(1,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employee_payroll_location_assignments WHERE client_id=97713"));
            Assert.Equal(10700,await db.ExecuteScalarAsync<decimal>("SELECT NetPay FROM payrunemployees WHERE PayRunId=9771300 AND EmployeeId=9771301"));
            Assert.Equal(30,await db.ExecuteScalarAsync<decimal>("SELECT present_days FROM employee_monthly_attendance WHERE client_id=97713 AND employee_id=9771301"));
            Assert.Equal(31,await db.ExecuteScalarAsync<decimal>("SELECT payable_days FROM employee_monthly_attendance WHERE client_id=97713 AND employee_id=9771301"));
        }
    }
    [IsolatedTheory]
    [InlineData("ROUND(((54000)*(25)%)/(PAYROLL_DAYS)*(PAYABLE_DAYS))")]
    [InlineData("ROUND(((54000)*(20)%)/(30)*(PAYABLE_DAYS))")]
    [InlineData("ROUND(((54000)*(20)%)/(PAYROLL_DAYS)*(PRESENT_DAYS))")]
    public async Task Structural_location_match_rejects_changed_rate_divisor_or_salary_units_without_writes(string configured)
    {
        await using(var db=await F.OpenAsync())await db.ExecuteAsync("UPDATE salarystructurelines SET Formula=@Formula WHERE StructureId=9771300 AND ComponentId='9771301'",new{Formula=configured});
        var before=await PreservedAsync();var preview=await Repository.PreviewLocationCorrectionAsync(Client,Scope(),Actor);
        Assert.False(preview.CanApply);Assert.Contains(preview.Blockers,b=>b.Contains("No existing Settings template"));Assert.Equal(before,await PreservedAsync());
        await using(var db=await F.OpenAsync())Assert.Equal(0,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM modulesettings WHERE client_id=97713 AND ModuleCode LIKE 'excel_location_correction:%'"));
    }
    [IsolatedTheory]
    [InlineData("source")][InlineData("monthly")][InlineData("assignment")][InlineData("rules")][InlineData("draft")][InlineData("policy")]
    public async Task Changed_fingerprint_or_protected_state_rolls_back_without_new_receipt(string mode)
    {
        var preview=await Repository.PreviewLocationCorrectionAsync(Client,Scope(),Actor);Assert.True(preview.CanApply,string.Join(";",preview.Blockers));
        await using(var db=await F.OpenAsync())await db.ExecuteAsync(mode switch{
            "source"=>"UPDATE excel_payslip_calculation_sources SET source_json=JSON_SET(source_json,'$.notes','changed') WHERE client_id=97713",
            "monthly"=>"UPDATE employee_monthly_attendance SET present_days=29 WHERE client_id=97713 AND employee_id=9771301",
            "assignment"=>"UPDATE employee_payroll_location_assignments SET annual_ctc=129612 WHERE client_id=97713",
            "rules"=>"UPDATE salarystructurelines SET ValueText='101' WHERE StructureId=9771300 AND ComponentId='9771302'",
            "draft"=>"UPDATE payruns SET Status='Approved' WHERE Id=9771300",
            _=>"INSERT INTO modulesettings(client_id,ModuleCode,IsEnabled,SettingsJson) VALUES(97713,'leave_attendance',TRUE,JSON_OBJECT('changed',TRUE))"});
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Apply(preview));
        await using(var db=await F.OpenAsync())Assert.Equal(0,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM modulesettings WHERE client_id=97713 AND ModuleCode LIKE 'excel_location_correction:%'"));
    }
    [IsolatedFact]
    public async Task Failure_after_monthly_and_allocation_writes_restores_audit_and_all_original_data()
    {
        var preview=await Repository.PreviewLocationCorrectionAsync(Client,Scope(),Actor);Assert.True(preview.CanApply,string.Join(";",preview.Blockers));var before=await PreservedAsync();
        await using(var db=await F.OpenAsync())await db.ExecuteAsync("CREATE TRIGGER codex_location_97713_fail BEFORE UPDATE ON payrunemployees FOR EACH ROW BEGIN IF NEW.ClientId=97713 THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='synthetic rollback proof'; END IF; END");
        await Assert.ThrowsAnyAsync<Exception>(()=>Apply(preview));Assert.Equal(before,await PreservedAsync());
        await using(var db=await F.OpenAsync())
        {
            Assert.Equal(30,await db.ExecuteScalarAsync<decimal>("SELECT present_days FROM employee_monthly_attendance WHERE client_id=97713 AND employee_id=9771301"));
            Assert.Equal(30,await db.ExecuteScalarAsync<decimal>("SELECT present_days FROM employee_payroll_location_allocations WHERE client_id=97713"));
            Assert.Equal(0,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM modulesettings WHERE client_id=97713"));
            Assert.Equal("Original audit",await RequiredAsync(db,"SELECT ComponentName FROM payroll_calculation_traces WHERE PayRunId=9771300"));
        }
    }
    [IsolatedFact]
    public async Task Orphan_recovery_retains_missing_parent_fact_without_recreating_applied_history()
    {
        var scope=Scope();scope.RecoverOrphanedLineage=true;
        await using(var db=await F.OpenAsync())
        {
            var batch=JsonSerializer.Deserialize<ExcelPayslipBatch>(await RequiredAsync(db,"SELECT CAST(batch_json AS CHAR) FROM excel_payslip_batches WHERE client_id=97713"),Json)!;
            var source=JsonSerializer.Deserialize<ExcelPayslipCalculationSource>(await RequiredAsync(db,"SELECT CAST(source_json AS CHAR) FROM excel_payslip_calculation_sources WHERE client_id=97713"),Json)!;
            scope.SourceReview=JsonSerializer.Deserialize<ExcelPayrollSetupRequest>(await RequiredAsync(db,"SELECT CAST(request_json AS CHAR) FROM excel_payroll_setup_imports WHERE client_id=97713"),Json)!;
            scope.SourceReview.CreateMissingDropdownValues=false;scope.SourceReview.EmployeeDecisions=[new(){RowId="source-one",MatchedEmployeeId=Employee}];
            scope.SourceReview.TemplateScopeReviewVersion=1;scope.SourceReview.TemplateScope=new(){Reviewed=true,OpenEnded=true,LocationMode="Listed",WorkLocationIds=[97713],CategoryKind="AllClientCategories",DivisorMeaning="CalendarDays",PayableMeaning="AlreadyPayable",PolicyBasis="SavedSourceRules"};
            var row=batch.Rows[0];batch.ProfileLinks=[new(row.Id,row.SourceRow,Employee,ExcelPayslipMasterSynchronization.ProfileIdentityHash(batch,row,source))];
            scope.RecoverySourceHash=ExcelPayslipRepository.EmploymentSourceHash(batch,source);
            await db.ExecuteAsync("UPDATE excel_payslip_batches SET batch_json=CAST(@Json AS JSON) WHERE client_id=97713;DELETE FROM excel_payroll_setup_imports WHERE client_id=97713;UPDATE employee_payroll_location_assignments SET source_hash=@Legacy WHERE client_id=97713;UPDATE employee_payroll_location_allocations SET source_hash=@Legacy WHERE client_id=97713",new{Json=JsonSerializer.Serialize(batch,Json),Legacy=new string('e',64)});
        }
        var before=await SourceAndMasterAsync();
        var preview=await Repository.PreviewLocationCorrectionAsync(Client,scope,Actor);Assert.True(preview.CanApply,string.Join(";",preview.Blockers));
        Assert.True(Assert.Single(preview.RecoveryEvidence).SourceHashDiffers);Assert.Contains(preview.Changes,change=>change.Field=="Source proof hash");
        var id=Guid.NewGuid().ToString("N");var command=new ExcelPayrollLocationCorrectionApplyRequest{Request=scope,PlanHash=preview.PlanHash,RequestId=id,Acknowledged=true};
        await Repository.ApplyLocationCorrectionAsync(Client,command,Actor);Assert.True((await Repository.ApplyLocationCorrectionAsync(Client,command,Actor)).Replayed);
        Assert.Equal(before,await SourceAndMasterAsync());
        await using(var db=await F.OpenAsync())Assert.Equal(0,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM excel_payroll_setup_imports WHERE client_id=97713"));
    }
    private async Task<string> SourceAndMasterAsync()
    {
        await using var db=await F.OpenAsync();return await RequiredAsync(db,@"SELECT SHA2(CONCAT((SELECT CAST(batch_json AS CHAR) FROM excel_payslip_batches WHERE client_id=97713),(SELECT CAST(source_json AS CHAR) FROM excel_payslip_calculation_sources WHERE client_id=97713),(SELECT CONCAT(AnnualCtc,SalaryStructureId,SalaryJson) FROM employees WHERE Id=9771301),(SELECT CAST(send_state_json AS CHAR) FROM excel_payslip_batches WHERE client_id=97713)),256)");
    }
    private async Task<ExcelPayrollLocationCorrectionRequest> PrepareRowRebindingAsync()
    {
        await using var db=await F.OpenAsync();await using var tx=await db.BeginTransactionAsync();
        var batch=JsonSerializer.Deserialize<ExcelPayslipBatch>((await db.ExecuteScalarAsync<string>("SELECT CAST(batch_json AS CHAR) FROM excel_payslip_batches WHERE client_id=97713",transaction:tx))!,Json)!;
        var source=JsonSerializer.Deserialize<ExcelPayslipCalculationSource>((await db.ExecuteScalarAsync<string>("SELECT CAST(source_json AS CHAR) FROM excel_payslip_calculation_sources WHERE client_id=97713",transaction:tx))!,Json)!;
        var review=JsonSerializer.Deserialize<ExcelPayrollSetupRequest>((await db.ExecuteScalarAsync<string>("SELECT CAST(request_json AS CHAR) FROM excel_payroll_setup_imports WHERE client_id=97713",transaction:tx))!,Json)!;
        var storage=await ExcelPayrollSetupRepository.ReadLocationAllocationStateAsync(db,tx,Client,[Employee],batch.Month);
        var assignment=storage.Assignments.Single();var allocation=storage.Allocations.Single();
        var period=new PayrollLocationPartPeriod{ClientId=Client,EmployeeId=Employee,PayPeriod=batch.Month,ReferenceAssignmentId=assignment.Id,Parts=[new(){PartKey=allocation.SourcePartKey,AssignmentId=assignment.Id,
            WorkLocationId=assignment.WorkLocationId,WorkState=assignment.WorkState,SalaryStructureId=assignment.SalaryStructureId,AnnualCtc=assignment.AnnualCtc,
            SalaryInputs=JsonSerializer.Deserialize<Dictionary<string,decimal>>(assignment.SalaryInputsJson,Json)!,ComponentScopes=JsonSerializer.Deserialize<Dictionary<string,string>>(assignment.ComponentScopesJson,Json)!,
            PayrollDays=allocation.PayrollDays,PresentDays=allocation.PresentDays,PayableDays=allocation.PayableDays}]};
        var run=await db.QuerySingleAsync<PayRun>("SELECT * FROM payruns WHERE Id=9771300",transaction:tx);
        var original=await F.Runs.CalculateLocationCorrectionAsync(db,tx,run,period,await PayrollDataTableStore.GetSetupJsonAsync(db,tx),requirePersistedParts:true);
        await db.ExecuteAsync(@"UPDATE payrunemployees SET MonthlyGross=@MonthlyGross,GrossPay=@GrossPay,StatutoryDeductions=@StatutoryDeductions,
OneTimeEarnings=@OneTimeEarnings,OneTimeDeductions=@OneTimeDeductions,ManualTds=@ManualTds,NetPay=@NetPay,DetailsJson=CAST(@DetailsJson AS JSON) WHERE PayRunId=9771300 AND EmployeeId=9771301",original.Row,tx);
        var row=batch.Rows[0];var identity=ExcelPayslipMasterSynchronization.ProfileIdentityHash(batch,row,source);
        batch.ProfileLinks=[new(row.Id,row.SourceRow,Employee,identity)];
        review.CreateMissingDropdownValues=false;review.ImportAttendance=false;review.EmployeeDecisions=[new(){RowId=row.Id,MatchedEmployeeId=Employee,AllowCreate=false}];
        review.AttendanceReview!.BoundSourceInputs=ExcelPayrollSetupRepository.AttendanceBinding(review);
        review.TemplateScopeReviewVersion=1;review.TemplateScope=new(){Reviewed=true,OpenEnded=true,LocationMode="Listed",WorkLocationIds=[97713],CategoryKind="AllClientCategories",DivisorMeaning="CalendarDays",PayableMeaning="AlreadyPayable",PolicyBasis="SavedSourceRules"};
        var sourceHash=ExcelPayslipRepository.EmploymentSourceHash(batch,source);review.AttendanceReview!.SourceHash=sourceHash;
        await db.ExecuteAsync(@"UPDATE excel_payslip_batches SET batch_json=CAST(@Json AS JSON) WHERE client_id=97713;
DELETE FROM excel_payroll_setup_imports WHERE client_id=97713;
UPDATE employee_payroll_location_assignments SET source_row_id='captured-row-3',source_row=3,source_hash=@Legacy WHERE client_id=97713;
UPDATE employee_payroll_location_allocations SET source_row_id='captured-row-3',source_row=3,source_hash=@Legacy WHERE client_id=97713",new{Json=JsonSerializer.Serialize(batch,Json),Legacy=new string('e',64)},tx);
        await tx.CommitAsync();
        return new(){PayRunId=Run,BatchId=Batch,EmployeeIds=[Employee],RecoverOrphanedLineage=true,RecoverySourceHash=sourceHash,SourceReview=review,
            RowRebindReview=new(){Version=1,Reviewed=true,Rows=[new(Employee,Assignment,allocation.SourcePartKey,"captured-row-3",row.Id,row.SourceRow,identity)]}};
    }
    [IsolatedFact]
    public async Task Explicit_row_rebinding_replays_old_frozen_finances_then_corrects_same_draft_and_keeps_legacy_missing_parent_facts()
    {
        var scope=await PrepareRowRebindingAsync();var before=await SourceAndMasterAsync();
        var preview=await Repository.PreviewLocationCorrectionAsync(Client,scope,Actor);Assert.True(preview.CanApply,string.Join(";",preview.Blockers));
        var binding=Assert.Single(preview.RowRebindings!);Assert.Equal("captured-row-3",binding.StoredSourceRowId);Assert.Equal("source-one",binding.CurrentRowId);Assert.Equal(30,binding.ActualPresentDays);
        Assert.Equal(30,binding.PreviousPayableDays);Assert.Equal(64,binding.OldFinancialProofHash.Length);Assert.Equal(31,Assert.Single(preview.Employees).PayableDays);
        var id=Guid.NewGuid().ToString("N");var command=new ExcelPayrollLocationCorrectionApplyRequest{Request=scope,PlanHash=preview.PlanHash,RequestId=id,Acknowledged=true};
        await Repository.ApplyLocationCorrectionAsync(Client,command,Actor);Assert.True((await Repository.ApplyLocationCorrectionAsync(Client,command,Actor)).Replayed);
        Assert.Equal(before,await SourceAndMasterAsync());
        await using(var db=await F.OpenAsync())
        {
            Assert.Equal(0,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM excel_payroll_setup_imports WHERE client_id=97713"));
            Assert.Equal("captured-row-3",await RequiredAsync(db,"SELECT source_row_id FROM employee_payroll_location_assignments WHERE id=@Id",new{Id=Assignment}));
            Assert.Equal("Active",await RequiredAsync(db,"SELECT status FROM employee_payroll_location_assignments WHERE id=@Id",new{Id=Assignment}));
            Assert.Equal("source-one",await RequiredAsync(db,"SELECT source_row_id FROM employee_payroll_location_allocations WHERE client_id=97713"));
            Assert.Equal(1,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employee_payroll_location_assignments WHERE client_id=97713 AND status=@Status",new{Status=ReviewedLocationCorrectionOrigins.Status}));
            Assert.Equal(30,await db.ExecuteScalarAsync<decimal>("SELECT present_days FROM employee_monthly_attendance WHERE client_id=97713 AND employee_id=9771301"));
            Assert.Equal(31,await db.ExecuteScalarAsync<decimal>("SELECT payable_days FROM employee_monthly_attendance WHERE client_id=97713 AND employee_id=9771301"));
            var envelope=JsonNode.Parse(await RequiredAsync(db,"SELECT CAST(SettingsJson AS CHAR) FROM modulesettings WHERE client_id=97713 AND ModuleCode=@Code",new{Code=ExcelPayrollSetupRepository.LocationCorrectionReceiptPrefix+id}))!;
            var protector=new PortableIntegrationCredentialProtector(F.Config,new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider(),Microsoft.Extensions.Logging.Abstractions.NullLogger<PortableIntegrationCredentialProtector>.Instance);
            var receipt=JsonNode.Parse(protector.UnprotectLocationCorrection(envelope["payload"]!.ToString())!)!;
            Assert.Equal("captured-row-3",receipt["previous"]!["storage"]!["allocations"]![0]!["sourceRowId"]!.ToString());
            Assert.Equal("source-one",receipt["changes"]!["allocations"]![0]!["sourceRowId"]!.ToString());
            Assert.Equal("Unavailable — no parent row exists",receipt["recoveryEvidence"]![0]!["parentStatus"]!.ToString());
        }
        var simulation=await F.Runs.SimulateEmployeeAsync(new(){Mode="Employee",ClientId=Client,EmployeeId=Employee,PayPeriod="2026-10"},default);
        Assert.True(simulation.Complete);Assert.Equal(10700,simulation.Net);
        await using(var db=await F.OpenAsync())await db.ExecuteAsync("DELETE FROM modulesettings WHERE client_id=97713 AND ModuleCode LIKE 'excel_location_origin:%'");
        await Assert.ThrowsAsync<InvalidOperationException>(()=>F.Runs.SimulateEmployeeAsync(new(){Mode="Employee",ClientId=Client,EmployeeId=Employee,PayPeriod="2026-10"},default));
    }
    [IsolatedTheory]
    [InlineData("numeric")][InlineData("storedId")][InlineData("assignment")][InlineData("currentRow")][InlineData("identity")][InlineData("partial")][InlineData("foreignStrong")][InlineData("missingStrong")][InlineData("oldMoney")][InlineData("oldLines")]
    public async Task Unproven_partial_swapped_or_changed_row_binding_cannot_create_receipt_or_change_business_records(string mode)
    {
        var scope=await PrepareRowRebindingAsync();var binding=scope.RowRebindReview!.Rows[0];
        switch(mode)
        {
            case "storedId":scope.RowRebindReview.Rows[0]=binding with{StoredSourceRowId="captured-row-4"};break;
            case "assignment":scope.RowRebindReview.Rows[0]=binding with{AssignmentId=new string('f',32)};break;
            case "currentRow":scope.RowRebindReview.Rows[0]=binding with{CurrentRowId="other-row"};break;
            case "identity":scope.RowRebindReview.Rows[0]=binding with{SourceIdentityHash=new string('f',64)};break;
            case "partial":scope.RowRebindReview.Rows[0]=binding with{SourcePartKey="other-part"};break;
        }
        await using(var db=await F.OpenAsync())
            if(mode=="numeric")await db.ExecuteAsync("UPDATE employee_payroll_location_assignments SET source_row=4 WHERE client_id=97713");
            else if(mode=="foreignStrong")await db.ExecuteAsync("UPDATE employeepersonaldetails SET PanNumber='FGHIJ6789K' WHERE EmployeeId=9771301;INSERT INTO employeepersonaldetails(EmployeeId,PanNumber) VALUES(9771302,'ABCDE1234F')");
            else if(mode=="missingStrong")await db.ExecuteAsync("UPDATE employeepersonaldetails SET PanNumber='' WHERE EmployeeId=9771301");
            else if(mode=="oldMoney")await db.ExecuteAsync("UPDATE payrunemployees SET NetPay=NetPay+1 WHERE PayRunId=9771300 AND EmployeeId=9771301");
            else if(mode=="oldLines")await db.ExecuteAsync("UPDATE payrunemployees SET DetailsJson=JSON_ARRAY() WHERE PayRunId=9771300 AND EmployeeId=9771301");
        var before=await SourceAndMasterAsync();var preview=await Repository.PreviewLocationCorrectionAsync(Client,scope,Actor);Assert.False(preview.CanApply);
        Assert.Equal(before,await SourceAndMasterAsync());
        await using(var db=await F.OpenAsync())
        {
            Assert.Equal(0,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM modulesettings WHERE client_id=97713"));
            Assert.Equal("captured-row-3",await RequiredAsync(db,"SELECT source_row_id FROM employee_payroll_location_allocations WHERE client_id=97713"));
            Assert.Equal(1,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employee_payroll_location_assignments WHERE client_id=97713"));
            Assert.Equal(30,await db.ExecuteScalarAsync<decimal>("SELECT payable_days FROM employee_monthly_attendance WHERE client_id=97713 AND employee_id=9771301"));
        }
    }
    [IsolatedTheory]
    [InlineData("source")][InlineData("numeric")][InlineData("draft")]
    public async Task Changed_row_binding_plan_cannot_apply_a_stale_preview(string change)
    {
        var scope=await PrepareRowRebindingAsync();var preview=await Repository.PreviewLocationCorrectionAsync(Client,scope,Actor);Assert.True(preview.CanApply,string.Join(";",preview.Blockers));
        await using(var db=await F.OpenAsync())await db.ExecuteAsync(change switch{
            "source"=>"UPDATE excel_payslip_calculation_sources SET source_json=JSON_SET(source_json,'$.notes','changed') WHERE client_id=97713",
            "numeric"=>"UPDATE employee_payroll_location_allocations SET source_row=4 WHERE client_id=97713",
            _=>"UPDATE payrunemployees SET DetailsJson=JSON_ARRAY() WHERE PayRunId=9771300 AND EmployeeId=9771301"});
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Repository.ApplyLocationCorrectionAsync(Client,new(){Request=scope,PlanHash=preview.PlanHash,RequestId=Guid.NewGuid().ToString("N"),Acknowledged=true},Actor));
        await using(var db=await F.OpenAsync())Assert.Equal(0,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM modulesettings WHERE client_id=97713"));
    }
    [IsolatedFact]
    public async Task Row_rebinding_failure_after_period_writes_restores_legacy_row_identity_and_audit()
    {
        var scope=await PrepareRowRebindingAsync();var before=await SourceAndMasterAsync();var preview=await Repository.PreviewLocationCorrectionAsync(Client,scope,Actor);Assert.True(preview.CanApply,string.Join(";",preview.Blockers));
        await using(var db=await F.OpenAsync())await db.ExecuteAsync("CREATE TRIGGER codex_location_97713_fail BEFORE UPDATE ON payrunemployees FOR EACH ROW BEGIN IF NEW.ClientId=97713 THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='synthetic rebind rollback proof'; END IF; END");
        await Assert.ThrowsAnyAsync<Exception>(()=>Repository.ApplyLocationCorrectionAsync(Client,new(){Request=scope,PlanHash=preview.PlanHash,RequestId=Guid.NewGuid().ToString("N"),Acknowledged=true},Actor));
        Assert.Equal(before,await SourceAndMasterAsync());
        await using(var db=await F.OpenAsync())
        {
            Assert.Equal(0,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM modulesettings WHERE client_id=97713"));Assert.Equal(1,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employee_payroll_location_assignments WHERE client_id=97713"));
            Assert.Equal("captured-row-3",await RequiredAsync(db,"SELECT source_row_id FROM employee_payroll_location_allocations WHERE client_id=97713"));
            Assert.Equal(30,await db.ExecuteScalarAsync<decimal>("SELECT payable_days FROM employee_monthly_attendance WHERE client_id=97713 AND employee_id=9771301"));
            Assert.Equal("Original audit",await RequiredAsync(db,"SELECT ComponentName FROM payroll_calculation_traces WHERE PayRunId=9771300"));
        }
    }
    private async Task<ExcelPayrollLocationCorrectionRequest> PrepareRateRecoveryAsync()
    {
        await using var db=await F.OpenAsync();
        var batch=JsonSerializer.Deserialize<ExcelPayslipBatch>(await RequiredAsync(db,"SELECT CAST(batch_json AS CHAR) FROM excel_payslip_batches WHERE client_id=97713"),Json)!;
        var source=JsonSerializer.Deserialize<ExcelPayslipCalculationSource>(await RequiredAsync(db,"SELECT CAST(source_json AS CHAR) FROM excel_payslip_calculation_sources WHERE client_id=97713"),Json)!;
        var review=JsonSerializer.Deserialize<ExcelPayrollSetupRequest>(await RequiredAsync(db,"SELECT CAST(request_json AS CHAR) FROM excel_payroll_setup_imports WHERE client_id=97713"),Json)!;
        var row=batch.Rows[0];source.Rows[0].Cells[7]!.Value="25";source.Rows[0].Cells[8]!.Value="13500";source.Rows[0].Cells[11]!.Value="13400";
        row.Earnings[0].Amount=13500;row.DeclaredGross=13500;row.NetPay=13400;
        row.Information=source.Columns.Where(column=>column.Kind=="info").Select(column=>new ExcelPayslipText{Label=column.Label,Value=source.Rows[0].Cells[column.ColumnIndex]!.Value}).ToList();
        batch.ProfileLinks=[new(row.Id,row.SourceRow,Employee,ExcelPayslipMasterSynchronization.ProfileIdentityHash(batch,row,source))];
        review.CreateMissingDropdownValues=false;review.EmployeeDecisions=[new(){RowId=row.Id,MatchedEmployeeId=Employee}];
        review.TemplateScopeReviewVersion=1;review.TemplateScope=new(){Reviewed=true,OpenEnded=true,LocationMode="Listed",WorkLocationIds=[97713],CategoryKind="AllClientCategories",DivisorMeaning="CalendarDays",PayableMeaning="AlreadyPayable",PolicyBasis="SavedSourceRules"};
        review.TemplateDecisions=[new(){VariantKey="9771304",ExistingTemplateId="9771304",ScopeAction="AttestLegacyScope",ScopeReason="Explicit synthetic period-only exact existing rule scope"}];
        var hash=ExcelPayslipRepository.EmploymentSourceHash(batch,source);review.AttendanceReview!.SourceHash=hash;
        var formula=ExcelPayrollSetupRepository.TranslateTransferFormula(source.Rows[0].Cells[8]!.FormulaText!,review,source,row);
        await db.ExecuteAsync(@"INSERT INTO salarystructures(Id,ClientId,Name,AnnualCtc,Active) VALUES(9771304,97713,'Explicit existing twenty five percent',129600,TRUE);
INSERT INTO salarystructurelines(StructureId,ComponentId,CalculationType,ValueText,Formula,BaseComponent,ProRataOverride,RoundingMode,SortOrder)
SELECT 9771304,ComponentId,CalculationType,ValueText,CASE WHEN ComponentId='9771301' THEN @Formula ELSE Formula END,BaseComponent,ProRataOverride,RoundingMode,SortOrder FROM salarystructurelines WHERE StructureId=9771300;
UPDATE excel_payslip_batches SET batch_json=CAST(@BatchJson AS JSON) WHERE client_id=97713;UPDATE excel_payslip_calculation_sources SET source_json=CAST(@SourceJson AS JSON) WHERE client_id=97713;
DELETE FROM excel_payroll_setup_imports WHERE client_id=97713;",new{Formula=formula,BatchJson=JsonSerializer.Serialize(batch,Json),SourceJson=JsonSerializer.Serialize(source,Json)});
        return new(){PayRunId=Run,BatchId=Batch,EmployeeIds=[Employee],RecoverOrphanedLineage=true,RecoverySourceHash=hash,SourceReview=review};
    }
    [IsolatedTheory]
    [InlineData("missingOrigin")][InlineData("missingReceipt")][InlineData("corruptReceipt")][InlineData("sourceChanged")]
    public async Task Current_reader_requires_authenticated_new_period_approval_and_future_keeps_original_assignment(string invalid)
    {
        var scope=await PrepareRateRecoveryAsync();var before=await SourceAndMasterAsync();
        var preview=await Repository.PreviewLocationCorrectionAsync(Client,scope,Actor);Assert.True(preview.CanApply,string.Join(";",preview.Blockers));
        var id=Guid.NewGuid().ToString("N");await Repository.ApplyLocationCorrectionAsync(Client,new(){Request=scope,PlanHash=preview.PlanHash,RequestId=id,Acknowledged=true},Actor);
        Assert.Equal(before,await SourceAndMasterAsync());
        var current=await F.Runs.SimulateEmployeeAsync(new(){Mode="Employee",ClientId=Client,EmployeeId=Employee,PayPeriod="2026-10"},default);
        Assert.True(current.Complete,string.Join(";",current.Issues.Select(issue=>issue.Message)));Assert.Equal(13400,current.Net);
        Assert.Equal(30,Assert.Single(current.LocationAllocations!.Parts).PresentDays);Assert.Equal(31,Assert.Single(current.LocationAllocations.Parts).PayableDays);
        var future=await new LeaveAttendanceRepository(F.Config).GetPayrollLocationAttendanceAsync(Client,"2026-11","synthetic-97713",[97713]);
        Assert.Equal(Assignment,Assert.Single(future.Rows,row=>row.EmployeeId==Employee).AssignmentId);
        await using(var db=await F.OpenAsync())
        {
            Assert.Equal(2,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employee_payroll_location_assignments WHERE client_id=97713"));
            Assert.Equal(2,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM salarystructures WHERE ClientId=97713"));
            await db.ExecuteAsync(invalid switch{
                "missingOrigin"=>"DELETE FROM modulesettings WHERE client_id=97713 AND ModuleCode LIKE 'excel_location_origin:%'",
                "missingReceipt"=>"DELETE FROM modulesettings WHERE client_id=97713 AND ModuleCode LIKE 'excel_location_correction:%'",
                "corruptReceipt"=>"UPDATE modulesettings SET SettingsJson=JSON_OBJECT('version',1,'payload','invalid') WHERE client_id=97713 AND ModuleCode LIKE 'excel_location_correction:%'",
                _=>"UPDATE excel_payslip_calculation_sources SET source_json=JSON_SET(source_json,'$.rows[0].cells[7].value','30') WHERE client_id=97713"});
        }
        await Assert.ThrowsAsync<InvalidOperationException>(()=>F.Runs.SimulateEmployeeAsync(new(){Mode="Employee",ClientId=Client,EmployeeId=Employee,PayPeriod="2026-10"},default));
    }
}
