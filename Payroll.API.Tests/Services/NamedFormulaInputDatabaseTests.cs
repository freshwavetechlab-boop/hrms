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
public sealed class NamedFormulaInputDatabaseTests : IAsyncLifetime
{
    private const int Client=97724, Employee=9772401, OtherEmployee=9772402, Run=9772400;
    private const string Batch="97724000000000000000000000000001", Import="97724000000000000000000000000002", Assignment="97724000000000000000000000000003";
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private readonly ReviewedPayrollFixture? fixture=Environment.GetEnvironmentVariable("FREVO_REVIEWED_PAYROLL_TEST_DB") is string connection?new(connection):null;
    private ReviewedPayrollFixture F=>fixture??throw new InvalidOperationException("Explicit guarded synthetic DB required.");
    private ExcelPayrollSetupRepository Repository=>new(F.Config,new EmployeeRepository(F.Config,null!,null!),F.Runs);
    private static AuthUser Actor=>new(){Id=97724,IsActive=true,Roles=["super_admin"],Permissions=["payroll.run","employees.manage","settings.manage"]};
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
        f.Request.Components[0].Code="NI_ALLOWANCE";f.Request.Components[1].Code="NI_DEDUCTION";f.Request.Components[2].Code="NI_EMPLOYER";
        f.Batch.Id=Batch;f.Batch.ClientId=Client;
        f.Request.BatchId=Batch;f.Request.LocationAllocations!.Groups[0].EmployeeId=Employee;f.Request.LocationAllocations.Groups[0].ReferenceAssignmentId=Assignment;
        f.Request.LocationAllocations.Groups[0].Parts[0].TemplateId="9772400";
        f.Request.LocationAllocations.Groups[0].Parts[0].WorkLocationId=97724;
        f.Request.LocationAllocations.Groups[0].Parts[0].ComponentScopes=new(){["9772401"]="SourceFormulaPerPart",["9772402"]="SourceFormulaPerPart",["9772403"]="SourceFormulaPerPart"};
        for(var i=0;i<3;i++)f.Request.Components[i].ExistingComponentId=(9772401+i).ToString();
        f.Request.AttendanceReview!.BoundSourceInputs=ExcelPayrollSetupRepository.AttendanceBinding(f.Request);
        await db.ExecuteAsync(@"
INSERT INTO clients(Id,Code,Name,IsActive) VALUES(97724,'SYN-LOC','Synthetic location correction',TRUE);
INSERT INTO worklocations(Id,ClientId,Name,City,State) VALUES(97724,97724,'Synthetic office','Synthetic city','Synthetic state');
INSERT INTO salarycomponents(Id,Code,Name,Category,ComponentRole,StatutoryType,CalculationType,ValueText,Formula,ProRata,Taxable,Priority,PayType,Active) VALUES
(9772401,'NI_ALLOWANCE','Synthetic allowance','Earning','Regular Earning','None','Formula','0','',FALSE,TRUE,100,'Fixed Pay',TRUE),
(9772402,'NI_DEDUCTION','Synthetic deduction','Deduction','Regular Deduction','None','Fixed Amount','100','',FALSE,TRUE,200,'Fixed Pay',TRUE),
(9772403,'NI_EMPLOYER','Synthetic employer','Benefit','Employer Benefit','None','Fixed Amount','50','',FALSE,TRUE,300,'Fixed Pay',TRUE);
INSERT INTO salarystructures(Id,ClientId,Name,AnnualCtc,Active) VALUES(9772400,97724,'Synthetic existing template',129600,TRUE);
INSERT INTO salarystructurelines(StructureId,ComponentId,CalculationType,ValueText,Formula,ProRataOverride,SortOrder) VALUES(9772400,'9772401','Formula','0',@Formula,'false',1),(9772400,'9772402','Fixed Amount','100','','false',2),(9772400,'9772403','Fixed Amount','50','','false',3);
INSERT INTO employees(Id,ClientId,EmployeeCode,FirstName,LastName,DateOfJoining,AnnualCtc,SalaryStructureId,SalaryJson,PersonalJson,PaymentJson,WorkLocationId) VALUES
(9772401,97724,'SYN-LOC-1','Synthetic','Person','2026-01-01',129600,'9772400',JSON_OBJECT(),JSON_OBJECT(),JSON_OBJECT(),97724),
(9772402,97724,'SYN-LOC-2','Unchanged','Person','2026-01-01',129600,'9772400',JSON_OBJECT(),JSON_OBJECT(),JSON_OBJECT(),97724);
INSERT INTO employeepersonaldetails(EmployeeId,PanNumber) VALUES(9772401,'ABCDE1234F');
INSERT INTO employee_monthly_attendance(client_id,employee_id,attendance_month,working_days,present_days,payable_days,lop_days,source_type,remarks) VALUES
(97724,9772401,'2026-10',31,30,30,1,'Monthly',''),(97724,9772402,'2026-10',31,31,31,0,'Monthly','');
INSERT INTO tax_client_settings(client_id,financial_year,enabled,project_monthly_tds,active) VALUES(97724,'2026-27',FALSE,FALSE,TRUE);
INSERT INTO attendance_groups(id,client_id,name,policy_batch_id,attendance_mode,attendance_cycle_start_day,attendance_cycle_end_day,is_active) VALUES(97724,97724,'Synthetic source policy','synthetic-97724','Monthly',1,31,TRUE);
INSERT INTO attendance_group_employees(attendance_group_id,employee_id) VALUES(97724,9772401),(97724,9772402);
INSERT INTO payruns(Id,ClientId,ClientName,PayPeriod,RunCode,RunType,PayDate,TotalWorkingDays,Status,RequestJson,PayrollCost,NetPay) VALUES(9772400,97724,'Synthetic location correction','2026-10','TEST-97724','Regular','2026-11-01',31,'Draft',JSON_OBJECT(),1,1);
INSERT INTO payrunemployees(PayRunId,EmployeeId,ClientId,EmployeeCode,EmployeeName,PresentDays,PayableDays,MonthlyGross,GrossPay,StatutoryDeductions,OneTimeEarnings,OneTimeDeductions,NetPay,DetailsJson) VALUES
(9772400,9772401,97724,'SYN-LOC-1','Synthetic Person',30,30,10800,10452,100,0,0,10352,JSON_ARRAY()),(9772400,9772402,97724,'SYN-LOC-2','Unchanged Person',31,31,10800,10800,100,0,0,10700,JSON_ARRAY());
INSERT INTO payroll_calculation_traces(PayRunId,EmployeeId,EmployeeCode,ComponentCode,ComponentName,TraceOrder,RuleUsed,FormulaUsed,BaseAmount,Factor,CalculatedAmount,InputJson,OutputJson) VALUES(9772400,9772401,'SYN-LOC-1','ORIGINAL','Original audit',1,'original rule','original formula',1,1,1,JSON_OBJECT('original',TRUE),JSON_OBJECT('original',TRUE));
INSERT INTO excel_payslip_batches(client_id,id,batch_json,send_state_json,created_at) VALUES(97724,@Batch,CAST(@BatchJson AS JSON),JSON_OBJECT('oldHistory','retained'),UTC_TIMESTAMP());
INSERT INTO excel_payslip_calculation_sources(client_id,batch_id,source_json,created_at) VALUES(97724,@Batch,CAST(@SourceJson AS JSON),UTC_TIMESTAMP());",
            new{Formula=f.Request.Components[0].Formula,Batch,BatchJson=JsonSerializer.Serialize(f.Batch,Json),SourceJson=JsonSerializer.Serialize(f.Source,Json)});
        var setup=JsonNode.Parse(await PayrollDataTableStore.GetSetupJsonAsync(db))!;
        var template=setup["salaryStructures"]!.AsArray().OfType<JsonObject>().Single(t=>t["id"]!.ToString()=="9772400");
        var rulesHash=ExcelPayrollSetupRepository.LocationSettingsRulesHash(template,setup["salaryComponents"]!.AsArray().OfType<JsonObject>().ToArray());
        var sourceHash=ExcelPayslipRepository.EmploymentSourceHash(f.Batch,f.Source);
        f.Request.AttendanceReview.SourceHash=sourceHash;
        var receipt=new ExcelPayrollSetupResult{ImportId=Import,EmployeeIds=[Employee],LocationAllocations=new(),LocationAssignmentReceipts=[new("source-one",Employee,"synthetic-part",Assignment,97724,"9772400","2026-10","2026-11-14",rulesHash,sourceHash)]};
        await db.ExecuteAsync(@"
INSERT INTO employee_payroll_location_assignments(id,client_id,employee_id,work_location_id,work_state,salary_structure_id,annual_ctc,salary_inputs_json,component_scopes_json,rules_hash,source_hash,configuration_effective_from,setup_import_id,source_batch_id,source_row_id,created_by,status) VALUES(@Assignment,97724,9772401,97724,'Synthetic state','9772400',129600,JSON_OBJECT(),JSON_OBJECT('9772401','SourceFormulaPerPart','9772402','SourceFormulaPerPart','9772403','SourceFormulaPerPart'),@rulesHash,@sourceHash,'2026-11-14',@Import,@Batch,'source-one','Synthetic reviewer','Active');
INSERT INTO employee_payroll_location_allocations(client_id,employee_id,pay_period,source_part_key,assignment_id,payroll_days,present_days,payable_days,reference_assignment_id,setup_import_id,source_batch_id,source_row_id,source_hash) VALUES(97724,9772401,'2026-10','synthetic-part',@Assignment,31,30,30,@Assignment,@Import,@Batch,'source-one',@sourceHash);
INSERT INTO excel_payroll_setup_imports(id,client_id,batch_id,request_id,selection_hash,request_hash,plan_hash,status,request_json,result_json,created_by) VALUES(@Import,97724,@Batch,@Import,@Hash,@Hash,@Hash,'Applied',CAST(@Request AS JSON),CAST(@Receipt AS JSON),'Synthetic reviewer');",
            new{Assignment,Import,Batch,rulesHash,sourceHash,Hash=new string('a',64),Request=JsonSerializer.Serialize(f.Request,Json),Receipt=JsonSerializer.Serialize(receipt,Json)});
    }
    public async Task DisposeAsync(){if(fixture is not null)await CleanAsync();}
    private async Task CleanAsync()
    {
        await using var db=await F.OpenAsync();
        await db.ExecuteAsync(@"
DROP TRIGGER IF EXISTS codex_named_97724_fail;
DELETE l FROM salarystructurelines l JOIN salarystructures s ON s.Id=l.StructureId WHERE s.ClientId=97724;DELETE FROM salarystructures WHERE ClientId=97724;
DELETE FROM modulesettings WHERE client_id=97724;
DELETE FROM payroll_calculation_traces WHERE PayRunId=9772400;DELETE FROM payroll_reconciliation_results WHERE PayRunId=9772400;DELETE FROM tax_computation_snapshots WHERE pay_run_id=9772400;
DELETE FROM payrunemployeelines WHERE PayRunId=9772400;DELETE FROM payrunemployees WHERE PayRunId=9772400;DELETE FROM payruns WHERE Id=9772400;
DELETE FROM payrolladjustments WHERE ClientId=97724;DELETE FROM tax_client_settings WHERE client_id=97724;
DELETE FROM attendance_group_employees WHERE attendance_group_id=97724;DELETE FROM attendance_groups WHERE client_id=97724;
DELETE FROM employee_payroll_location_allocations WHERE client_id=97724;DELETE FROM employee_payroll_location_assignments WHERE client_id=97724;DELETE FROM excel_payroll_setup_imports WHERE client_id=97724;
DELETE FROM employee_monthly_attendance WHERE client_id=97724;DELETE FROM excel_payslip_batches WHERE client_id=97724;DELETE FROM excel_payslip_calculation_sources WHERE client_id=97724;
DELETE FROM employeesalarycomponents WHERE EmployeeId IN(9772401,9772402);DELETE FROM employeepersonaldetails WHERE EmployeeId IN(9772401,9772402);DELETE FROM employeepaymentdetails WHERE EmployeeId IN(9772401,9772402);
DELETE FROM employees WHERE ClientId=97724;DELETE FROM worklocations WHERE ClientId=97724;DELETE FROM clients WHERE Id=97724;
DELETE FROM salarystructurelines WHERE StructureId IN(9772400,9772404);DELETE FROM salarystructures WHERE Id IN(9772400,9772404);DELETE FROM salarycomponents WHERE Id IN(9772401,9772402,9772403);");
    }
    private async Task<string> PreservedAsync()
    {
        await using var db=await F.OpenAsync();return await RequiredAsync(db,@"SELECT SHA2(CONCAT(
(SELECT CAST(batch_json AS CHAR) FROM excel_payslip_batches WHERE client_id=97724),(SELECT CAST(source_json AS CHAR) FROM excel_payslip_calculation_sources WHERE client_id=97724),
(SELECT CAST(send_state_json AS CHAR) FROM excel_payslip_batches WHERE client_id=97724),(SELECT CAST(DetailsJson AS CHAR) FROM payrunemployees WHERE PayRunId=9772400 AND EmployeeId=9772402),
(SELECT CAST(request_json AS CHAR) FROM excel_payroll_setup_imports WHERE client_id=97724),(SELECT CAST(result_json AS CHAR) FROM excel_payroll_setup_imports WHERE client_id=97724),
(SELECT CONCAT(AnnualCtc,SalaryStructureId,SalaryJson) FROM employees WHERE Id=9772401)),256)");
    }






    private async Task<string> SourceAndMasterAsync()
    {
        await using var db=await F.OpenAsync();return await RequiredAsync(db,@"SELECT SHA2(CONCAT((SELECT CAST(batch_json AS CHAR) FROM excel_payslip_batches WHERE client_id=97724),(SELECT CAST(source_json AS CHAR) FROM excel_payslip_calculation_sources WHERE client_id=97724),(SELECT CONCAT(AnnualCtc,SalaryStructureId,SalaryJson) FROM employees WHERE Id=9772401),(SELECT CAST(send_state_json AS CHAR) FROM excel_payslip_batches WHERE client_id=97724)),256)");
    }
    private async Task<ExcelPayrollLocationCorrectionRequest> PrepareRowRebindingAsync()
    {
        await using var db=await F.OpenAsync();await using var tx=await db.BeginTransactionAsync();
        var batch=JsonSerializer.Deserialize<ExcelPayslipBatch>((await db.ExecuteScalarAsync<string>("SELECT CAST(batch_json AS CHAR) FROM excel_payslip_batches WHERE client_id=97724",transaction:tx))!,Json)!;
        var source=JsonSerializer.Deserialize<ExcelPayslipCalculationSource>((await db.ExecuteScalarAsync<string>("SELECT CAST(source_json AS CHAR) FROM excel_payslip_calculation_sources WHERE client_id=97724",transaction:tx))!,Json)!;
        var review=JsonSerializer.Deserialize<ExcelPayrollSetupRequest>((await db.ExecuteScalarAsync<string>("SELECT CAST(request_json AS CHAR) FROM excel_payroll_setup_imports WHERE client_id=97724",transaction:tx))!,Json)!;
        var storage=await ExcelPayrollSetupRepository.ReadLocationAllocationStateAsync(db,tx,Client,[Employee],batch.Month);
        var assignment=storage.Assignments.Single();var allocation=storage.Allocations.Single();
        var period=new PayrollLocationPartPeriod{ClientId=Client,EmployeeId=Employee,PayPeriod=batch.Month,ReferenceAssignmentId=assignment.Id,Parts=[new(){PartKey=allocation.SourcePartKey,AssignmentId=assignment.Id,
            WorkLocationId=assignment.WorkLocationId,WorkState=assignment.WorkState,SalaryStructureId=assignment.SalaryStructureId,AnnualCtc=assignment.AnnualCtc,
            SalaryInputs=JsonSerializer.Deserialize<Dictionary<string,decimal>>(assignment.SalaryInputsJson,Json)!,ComponentScopes=JsonSerializer.Deserialize<Dictionary<string,string>>(assignment.ComponentScopesJson,Json)!,
            PayrollDays=allocation.PayrollDays,PresentDays=allocation.PresentDays,PayableDays=allocation.PayableDays}]};
        var run=await db.QuerySingleAsync<PayRun>("SELECT * FROM payruns WHERE Id=9772400",transaction:tx);
        var original=await F.Runs.CalculateLocationCorrectionAsync(db,tx,run,period,await PayrollDataTableStore.GetSetupJsonAsync(db,tx),requirePersistedParts:true);
        await db.ExecuteAsync(@"UPDATE payrunemployees SET MonthlyGross=@MonthlyGross,GrossPay=@GrossPay,StatutoryDeductions=@StatutoryDeductions,
OneTimeEarnings=@OneTimeEarnings,OneTimeDeductions=@OneTimeDeductions,ManualTds=@ManualTds,NetPay=@NetPay,DetailsJson=CAST(@DetailsJson AS JSON) WHERE PayRunId=9772400 AND EmployeeId=9772401",original.Row,tx);
        var row=batch.Rows[0];var identity=ExcelPayslipMasterSynchronization.ProfileIdentityHash(batch,row,source);
        batch.ProfileLinks=[new(row.Id,row.SourceRow,Employee,identity)];
        review.CreateMissingDropdownValues=false;review.ImportAttendance=false;review.EmployeeDecisions=[new(){RowId=row.Id,MatchedEmployeeId=Employee,AllowCreate=false}];
        review.AttendanceReview!.BoundSourceInputs=ExcelPayrollSetupRepository.AttendanceBinding(review);
        review.TemplateScopeReviewVersion=1;review.TemplateScope=new(){Reviewed=true,OpenEnded=true,LocationMode="Listed",WorkLocationIds=[97724],CategoryKind="AllClientCategories",DivisorMeaning="CalendarDays",PayableMeaning="AlreadyPayable",PolicyBasis="SavedSourceRules"};
        var sourceHash=ExcelPayslipRepository.EmploymentSourceHash(batch,source);review.AttendanceReview!.SourceHash=sourceHash;
        await db.ExecuteAsync(@"UPDATE excel_payslip_batches SET batch_json=CAST(@Json AS JSON) WHERE client_id=97724;
DELETE FROM excel_payroll_setup_imports WHERE client_id=97724;
UPDATE employee_payroll_location_assignments SET source_row_id='captured-row-3',source_row=3,source_hash=@Legacy WHERE client_id=97724;
UPDATE employee_payroll_location_allocations SET source_row_id='captured-row-3',source_row=3,source_hash=@Legacy WHERE client_id=97724",new{Json=JsonSerializer.Serialize(batch,Json),Legacy=new string('e',64)},tx);
        await tx.CommitAsync();
        return new(){PayRunId=Run,BatchId=Batch,EmployeeIds=[Employee],RecoverOrphanedLineage=true,RecoverySourceHash=sourceHash,SourceReview=review,
            RowRebindReview=new(){Version=1,Reviewed=true,Rows=[new(Employee,Assignment,allocation.SourcePartKey,"captured-row-3",row.Id,row.SourceRow,identity)]}};
    }





    private async Task<ExcelPayrollLocationCorrectionRequest> PrepareNamedSourceAsync()
    {
        var scope=await PrepareRowRebindingAsync();await using var db=await F.OpenAsync();
        var batch=JsonSerializer.Deserialize<ExcelPayslipBatch>(await RequiredAsync(db,"SELECT CAST(batch_json AS CHAR) FROM excel_payslip_batches WHERE client_id=97724"),Json)!;
        var source=JsonSerializer.Deserialize<ExcelPayslipCalculationSource>(await RequiredAsync(db,"SELECT CAST(source_json AS CHAR) FROM excel_payslip_calculation_sources WHERE client_id=97724"),Json)!;
        var captured=source.Rows.Single(r=>r.SourceRow==3);captured.Cells[7]!.Value="25";captured.Cells[8]!.Value="13500";captured.Cells[11]!.Value="13400";
        source.Columns[6].SourceHeader=source.Columns[6].Label="Entry-level CTC";source.Columns[7].SourceHeader=source.Columns[7].Label="Hardship category %";
        source.Rows.Add(new(){SourceRow=1,Cells=Enumerable.Range(0,12).Select(i=>new ExcelPayslipCalculationCell{Value=i==6?"Entry-level CTC":i==7?"Hardship category %":"Synthetic header "+i}).ToList()});
        var row=batch.Rows.Single();row.Earnings[0].Amount=13500;row.DeclaredGross=13500;row.NetPay=13400;
        row.Information=source.Columns.Where(c=>c.Kind=="info").Select(c=>new ExcelPayslipText{Label=c.Label,Value=captured.Cells[c.ColumnIndex]!.Value}).ToList();
        var identity=ExcelPayslipMasterSynchronization.ProfileIdentityHash(batch,row,source);batch.ProfileLinks=[new(row.Id,row.SourceRow,Employee,identity)];
        scope.RowRebindReview!.Rows=[new(Employee,Assignment,"synthetic-part","captured-row-3",row.Id,row.SourceRow,identity)];
        scope.SourceReview!.EffectiveFrom="2026-10-01";scope.SourceReview.TemplateScope!.OpenEnded=false;scope.SourceReview.TemplateScope.EffectiveTo="2026-10-31";
        var hash=ExcelPayslipRepository.EmploymentSourceHash(batch,source);scope.RecoverySourceHash=hash;scope.SourceReview.AttendanceReview!.SourceHash=hash;
        await db.ExecuteAsync("UPDATE excel_payslip_batches SET batch_json=CAST(@Batch AS JSON) WHERE client_id=97724;UPDATE excel_payslip_calculation_sources SET source_json=CAST(@Source AS JSON) WHERE client_id=97724",new{Batch=JsonSerializer.Serialize(batch,Json),Source=JsonSerializer.Serialize(source,Json)});
        return scope;
    }
    private static List<NamedFormulaInputBinding> Bindings=>[new("HARDSHIP_BASE_INPUT",6,1,"Entry-level CTC"),new("HARDSHIP_RATE_PCT",7,1,"Hardship category %")];
    private NamedFormulaTemplateRequest TemplateRequest(ExcelPayrollLocationCorrectionRequest scope)=>new(){ClientId=Client,ExistingTemplateId="9772400",Name="One generic reviewed input family",SourceRowId="source-one",SourceHash=scope.RecoverySourceHash,SourceReview=scope.SourceReview!,Definitions=NamedFormulaInputTests.Definitions,Bindings=Bindings};
    private async Task<(ExcelPayrollLocationCorrectionRequest Scope,NamedFormulaTemplateResult Template)> ConfigureNamedAsync()
    {
        var scope=await PrepareNamedSourceAsync();var request=TemplateRequest(scope);
        var preview=await Repository.PreviewNamedFormulaTemplateAsync(request,Actor);Assert.True(preview.CanSave);
        var command=new NamedFormulaTemplateSaveRequest(request,preview.PlanHash,Guid.NewGuid().ToString("N"),true);
        var result=await Repository.SaveNamedFormulaTemplateAsync(command,Actor);Assert.True((await Repository.SaveNamedFormulaTemplateAsync(command,Actor)).Replayed);
        scope.NamedInputReview=new(){TemplateId=result.TemplateId,DeclarationHash=result.DeclarationHash,Bindings=Bindings};
        scope.SourceReview!.TemplateDecisions=[new(){ExistingTemplateId=result.TemplateId,VariantKey=result.TemplateId,ScopeAction="AttestLegacyScope",ScopeReason="Explicit synthetic current closed period generic operand template scope"}];
        return(scope,result);
    }
    [IsolatedFact]
    public async Task Real_atomic_template_manifest_apply_and_native_reload_preserve_source_flat_inputs_future_and_later_contact_updates()
    {
        var configured=await ConfigureNamedAsync();var before=await SourceAndMasterAsync();
        var preview=await Repository.PreviewLocationCorrectionAsync(Client,configured.Scope,Actor);Assert.True(preview.CanApply,string.Join(";",preview.Blockers));
        var command=new ExcelPayrollLocationCorrectionApplyRequest{Request=configured.Scope,PlanHash=preview.PlanHash,RequestId=Guid.NewGuid().ToString("N"),Acknowledged=true};
        await Repository.ApplyLocationCorrectionAsync(Client,command,Actor);Assert.True((await Repository.ApplyLocationCorrectionAsync(Client,command,Actor)).Replayed);
        Assert.Equal(before,await SourceAndMasterAsync());
        var current=await F.Runs.SimulateEmployeeAsync(new(){Mode="Employee",ClientId=Client,EmployeeId=Employee,PayPeriod="2026-10"},default);
        Assert.True(current.Complete,string.Join(";",current.Issues.Select(i=>i.Message)));Assert.Equal(13400,current.Net);
        Assert.Equal(30,Assert.Single(current.LocationAllocations!.Parts).PresentDays);Assert.Equal(31,Assert.Single(current.LocationAllocations.Parts).PayableDays);
        Assert.NotNull(current.Lines.Single(l=>l.Code=="NI_ALLOWANCE").NamedOperandParts);
        var attendanceContext=await new LeaveAttendanceRepository(F.Config).GetPayrollLocationAttendanceAsync(Client,"2026-10","synthetic-97724",[97724]);
        var attendanceRow=Assert.Single(attendanceContext.Rows,row=>row.EmployeeId==Employee);
        Assert.Equal("",attendanceRow.Issue);
        var workbook=await F.Runs.FormulaWorkbookAsync(new(Client,"2026-10",[Employee]){AttendancePolicyBatchId="synthetic-97724",AttendanceGroupIds=[97724],IncludeLocationParts=true,AttendanceContextVersion=attendanceContext.Version},default);
        var exported=Assert.Single(workbook);Assert.NotNull(exported.NamedInputs);Assert.Equal(25,exported.NamedInputs.Values["HARDSHIP_RATE_PCT"]);
        Assert.Equal(54000,exported.NamedInputs.Values["HARDSHIP_BASE_INPUT"]);Assert.Equal(attendanceRow.RulesHash,exported.Location!.RulesHash);
        Assert.Equal(current.Net,exported.Calculation.Net);Assert.Equal(before,await SourceAndMasterAsync());
        await using(var db=await F.OpenAsync())
        {
            Assert.Equal(3,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM salarycomponents WHERE Id BETWEEN 9772401 AND 9772403"));
            Assert.Equal(2,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM salarystructures WHERE ClientId=97724"));
            Assert.All(await db.QueryAsync<string>("SELECT CAST(salary_inputs_json AS CHAR) FROM employee_payroll_location_assignments WHERE client_id=97724"),value=>Assert.Equal("{}",value));
            Assert.Equal(1,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employee_payroll_location_assignments WHERE client_id=97724 AND status='Active'"));
            await db.ExecuteAsync("UPDATE employees SET FirstName='Updated legitimate name' WHERE Id=9772401;UPDATE employeepersonaldetails SET Mobile='9876543210' WHERE EmployeeId=9772401");
        }
        var afterContact=await F.Runs.SimulateEmployeeAsync(new(){Mode="Employee",ClientId=Client,EmployeeId=Employee,PayPeriod="2026-10"},default);
        Assert.True(afterContact.Complete);Assert.Equal(current.Net,afterContact.Net);
        var future=await new LeaveAttendanceRepository(F.Config).GetPayrollLocationAttendanceAsync(Client,"2026-11","synthetic-97724",[97724]);
        Assert.Equal(Assignment,Assert.Single(future.Rows,r=>r.EmployeeId==Employee).AssignmentId);
    }
    [IsolatedTheory]
    [InlineData("manifest")][InlineData("profile")][InlineData("approval")][InlineData("source")][InlineData("tampered")]
    public async Task Stable_core_loader_rejects_missing_or_stale_protected_period_evidence(string mutation)
    {
        var configured=await ConfigureNamedAsync();var preview=await Repository.PreviewLocationCorrectionAsync(Client,configured.Scope,Actor);Assert.True(preview.CanApply,string.Join(";",preview.Blockers));
        await Repository.ApplyLocationCorrectionAsync(Client,new(){Request=configured.Scope,PlanHash=preview.PlanHash,RequestId=Guid.NewGuid().ToString("N"),Acknowledged=true},Actor);
        await using(var db=await F.OpenAsync())await db.ExecuteAsync(mutation switch{
            "manifest"=>"DELETE FROM modulesettings WHERE client_id=97724 AND ModuleCode LIKE 'named-input-a:%'",
            "profile"=>"DELETE FROM modulesettings WHERE client_id=97724 AND ModuleCode LIKE 'named-input-t:%'",
            "approval"=>"DELETE FROM modulesettings WHERE client_id=97724 AND ModuleCode LIKE 'excel_location_correction:%'",
            "source"=>"UPDATE excel_payslip_calculation_sources SET source_json=JSON_SET(source_json,'$.rows[0].cells[7].value','20') WHERE client_id=97724",
            _=>"UPDATE modulesettings SET SettingsJson=JSON_OBJECT('version',1,'payload','invalid') WHERE client_id=97724 AND ModuleCode LIKE 'named-input-a:%'"});
        await Assert.ThrowsAsync<InvalidOperationException>(()=>F.Runs.SimulateEmployeeAsync(new(){Mode="Employee",ClientId=Client,EmployeeId=Employee,PayPeriod="2026-10"},default));
    }
    [IsolatedFact]
    public async Task Manifest_insert_failure_rolls_back_receipt_assignments_allocations_draft_and_traces()
    {
        var configured=await ConfigureNamedAsync();var before=await SourceAndMasterAsync();var preview=await Repository.PreviewLocationCorrectionAsync(Client,configured.Scope,Actor);Assert.True(preview.CanApply,string.Join(";",preview.Blockers));
        await using(var db=await F.OpenAsync())await db.ExecuteAsync("CREATE TRIGGER codex_named_97724_fail BEFORE INSERT ON modulesettings FOR EACH ROW BEGIN IF NEW.client_id=97724 AND NEW.ModuleCode LIKE 'named-input-a:%' THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='synthetic named manifest rollback'; END IF; END");
        await Assert.ThrowsAsync<MySqlException>(()=>Repository.ApplyLocationCorrectionAsync(Client,new(){Request=configured.Scope,PlanHash=preview.PlanHash,RequestId=Guid.NewGuid().ToString("N"),Acknowledged=true},Actor));
        Assert.Equal(before,await SourceAndMasterAsync());
        await using(var db=await F.OpenAsync())
        {
            Assert.Equal(1,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employee_payroll_location_assignments WHERE client_id=97724"));
            Assert.Equal(0,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM modulesettings WHERE client_id=97724 AND ModuleCode LIKE 'excel_location_correction:%'"));
            Assert.Equal(Assignment,await RequiredAsync(db,"SELECT assignment_id FROM employee_payroll_location_allocations WHERE client_id=97724"));
            Assert.Equal(30,await db.ExecuteScalarAsync<decimal>("SELECT payable_days FROM employee_monthly_attendance WHERE client_id=97724 AND employee_id=9772401"));
        }
    }

    [IsolatedTheory]
    [InlineData("ignored")][InlineData("component-input")]
    public async Task Named_source_operand_cannot_reuse_an_unreviewed_or_financial_component_input(string mutation)
    {
        var scope=await PrepareNamedSourceAsync();var request=TemplateRequest(scope);
        if(mutation=="ignored")request.SourceReview.SourceColumnRoles[6]="ignore";
        else request.SourceReview.Components[0].SourceColumnIndex=6;
        var before=await SourceAndMasterAsync();
        Assert.Contains(mutation=="ignored"?"Information columns":"monthly source inputs",(await Assert.ThrowsAsync<InvalidOperationException>(()=>Repository.PreviewNamedFormulaTemplateAsync(request,Actor))).Message);
        Assert.Equal(before,await SourceAndMasterAsync());
    }

    [IsolatedFact]
    public async Task A_new_review_with_different_operand_values_reuses_the_same_generic_template_family()
    {
        var configured=await ConfigureNamedAsync();
        await using(var db=await F.OpenAsync())
        {
            var batch=JsonSerializer.Deserialize<ExcelPayslipBatch>(await RequiredAsync(db,"SELECT CAST(batch_json AS CHAR) FROM excel_payslip_batches WHERE client_id=97724"),Json)!;
            var source=JsonSerializer.Deserialize<ExcelPayslipCalculationSource>(await RequiredAsync(db,"SELECT CAST(source_json AS CHAR) FROM excel_payslip_calculation_sources WHERE client_id=97724"),Json)!;
            var row=batch.Rows.Single();var captured=source.Rows.Single(record=>record.SourceRow==row.SourceRow);
            captured.Cells[7]!.Value="20";captured.Cells[8]!.Value="10800";captured.Cells[11]!.Value="10700";
            row.Earnings[0].Amount=10800;row.DeclaredGross=10800;row.NetPay=10700;
            row.Information=source.Columns.Where(column=>column.Kind=="info").Select(column=>new ExcelPayslipText{Label=column.Label,Value=captured.Cells[column.ColumnIndex]!.Value}).ToList();
            batch.ProfileLinks=[new(row.Id,row.SourceRow,Employee,ExcelPayslipMasterSynchronization.ProfileIdentityHash(batch,row,source))];
            configured.Scope.RecoverySourceHash=ExcelPayslipRepository.EmploymentSourceHash(batch,source);
            configured.Scope.SourceReview!.AttendanceReview!.SourceHash=configured.Scope.RecoverySourceHash;
            await db.ExecuteAsync("UPDATE excel_payslip_batches SET batch_json=CAST(@Batch AS JSON) WHERE client_id=97724;UPDATE excel_payslip_calculation_sources SET source_json=CAST(@Source AS JSON) WHERE client_id=97724",
                new{Batch=JsonSerializer.Serialize(batch,Json),Source=JsonSerializer.Serialize(source,Json)});
        }
        var request=TemplateRequest(configured.Scope);var before=await SourceAndMasterAsync();
        var preview=await Repository.PreviewNamedFormulaTemplateAsync(request,Actor);Assert.True(preview.CanSave);
        var result=await Repository.SaveNamedFormulaTemplateAsync(new(request,preview.PlanHash,Guid.NewGuid().ToString("N"),true),Actor);
        Assert.Equal(configured.Template.TemplateId,result.TemplateId);Assert.Equal(configured.Template.DeclarationHash,result.DeclarationHash);Assert.False(result.Replayed);
        Assert.Equal(before,await SourceAndMasterAsync());
        await using var check=await F.OpenAsync();
        Assert.Equal(2,await check.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM salarystructures WHERE ClientId=97724"));
        Assert.Equal(1,await check.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM modulesettings WHERE client_id=97724 AND ModuleCode LIKE 'named-input-t:%'"));
        Assert.Equal(1,await check.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employee_payroll_location_assignments WHERE client_id=97724 AND status='Active'"));
    }
}
