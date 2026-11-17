using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;
using Payroll.API.Tests.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class HistoricalInitialTargetDatabaseTests
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private static AuthUser Actor=>new(){Id=31,IsActive=true,Email="initial-review@example.test",Roles=["super_admin"],Permissions=["payroll.run","employees.manage","settings.manage"]};
    [LocalWorkflowScopeDatabaseFact]
    public async Task Initial_closed_insert_preserves_current_future_actions_source_and_attendance_bytes()
    {
        await using var f=await Fixture.CreateAsync();var before=await f.WitnessAsync();var preview=await f.ReadyAsync();
        var row=Assert.Single(preview.Rows);Assert.Equal(0,row.RetainedAnnualSalaryTarget);Assert.Equal(120000,row.InitialSourceAnnualSalaryTarget);
        Assert.Contains("No retained prior-period",row.SalaryTargetMeaning);Assert.Equal(0,await f.WrittenAsync());
        var requestId=Guid.NewGuid().ToString("N");var receipt=await f.ApplyAsync(preview,requestId);
        Assert.Single(receipt.Rows);var provenance=Assert.Single(receipt.InitialSourceTargets!);Assert.Equal(10000,provenance.SourceMonthlyBudget);Assert.True(provenance.ActualJoiningDateNotEstablished);
        Assert.Equal(before,await f.WitnessAsync());
        var saved=await f.Db.QuerySingleAsync<EmployeeRepository.ClosedSalaryHistoryRow>("SELECT * FROM employee_it0008_basic_pay WHERE ActionType='Closed Excel Salary'");
        Assert.Equal(new DateTime(2026,9,1),saved.EffectiveFrom);Assert.Equal(new DateTime(2026,9,30),saved.EffectiveTo);Assert.Equal("Historical",saved.Status);
        Assert.Equal(120000,saved.AnnualCtc);Assert.Equal(0,JsonSerializer.Deserialize<Dictionary<string,decimal>>(saved.SalaryJson)!["1012"]);
        var actual=await f.Runs.SimulateEmployeeAsync(new(){Mode="Employee",ClientId=31,EmployeeId=41,PayPeriod="2026-09"},default);
        Assert.True(actual.Complete);Assert.Equal(10000,actual.Gross);Assert.Equal(8800,actual.Net);Assert.Equal(1300,actual.Benefits);
        var replay=await f.ApplyAsync(preview,requestId);Assert.True(replay.Replayed);Assert.Equal(receipt.Rows,replay.Rows);
        Assert.Equal(1,await f.WrittenAsync());
    }
    [LocalWorkflowScopeDatabaseFact]
    public async Task Default_mode_does_not_fill_a_missing_basis_and_initial_mode_does_not_replace_a_prior_one()
    {
        await using var f=await Fixture.CreateAsync();var q=f.Request();q.SalaryTargetMode="PreserveEffectiveHistoricalTarget";q.InitialSourceTargetReview=null;q.Setup.MonthlyCtcColumnIndex=null;
        var missing=await f.Repository.PreviewHistoricalSalaryAsync(31,q,Actor);Assert.False(missing.CanApply);Assert.Contains(missing.Blockers,b=>b.Contains("positive effective historical"));
        await f.Db.ExecuteAsync("INSERT INTO employee_it0008_basic_pay(EmployeeId,ClientId,ActionType,EffectiveFrom,EffectiveTo,Status,SalaryStructureId,AnnualCtc,SalaryJson) VALUES(41,31,'Superseded old basis','2026-08-01','2026-08-31','Historical','72',120000,JSON_OBJECT('1012',777))");
        var prior=await f.Repository.PreviewHistoricalSalaryAsync(31,f.Request(),Actor);Assert.False(prior.CanApply);Assert.Contains(prior.Blockers,b=>b.Contains("superseded basic-pay"));Assert.Equal(0,await f.WrittenAsync());
    }
    [LocalWorkflowScopeDatabaseFact]
    public async Task Existing_preserve_mode_still_saves_only_a_closed_history_using_its_retained_target()
    {
        await using var f=await Fixture.CreateAsync();
        await f.Db.ExecuteAsync("INSERT INTO employee_it0008_basic_pay(EmployeeId,ClientId,ActionType,EffectiveFrom,EffectiveTo,Status,SalaryStructureId,AnnualCtc,SalaryJson) VALUES(41,31,'Retained prior basis','2026-08-01','2026-10-31','Historical','72',120000,JSON_OBJECT('1012',777));UPDATE salarystructurelines SET Formula='10000/PAYROLL_DAYS*PAYABLE_DAYS-I_BONUS_31_EARNED' WHERE StructureId=71 AND ComponentId='1011'");
        var q=f.Request();q.SalaryTargetMode="PreserveEffectiveHistoricalTarget";q.InitialSourceTargetReview=null;q.Setup.MonthlyCtcColumnIndex=null;
        q.Setup.Components.Single(c=>c.Code=="I_BASIC_31").Formula="10000/PAYROLL_DAYS*PAYABLE_DAYS-I_BONUS_31_EARNED";
        var before=await f.WitnessAsync();var preview=await f.Repository.PreviewHistoricalSalaryAsync(31,q,Actor);
        Assert.True(preview.CanApply,string.Join(";",preview.Blockers));Assert.Equal(120000,Assert.Single(preview.Rows).RetainedAnnualSalaryTarget);Assert.Null(preview.Rows[0].InitialSourceAnnualSalaryTarget);
        var receipt=await f.ApplyAsync(preview);Assert.Null(receipt.InitialSourceTargets);Assert.Equal(before,await f.WitnessAsync());Assert.Equal(1,await f.WrittenAsync());
    }
    [LocalWorkflowScopeDatabaseFact]
    public async Task Stale_source_current_future_and_template_state_cannot_apply_an_initial_review()
    {
        foreach(var change in new[]{"current","future","source","unknownSource","template","attendance"})
        {
            await using var f=await Fixture.CreateAsync();var review=await f.ReadyAsync();
            await f.Db.ExecuteAsync(change switch {
                "current"=>"UPDATE employees SET AnnualCtc=120001 WHERE Id=41",
                "future"=>"UPDATE employee_it0008_basic_pay SET AnnualCtc=AnnualCtc+1 WHERE EffectiveFrom='2027-01-01'",
                "source"=>"UPDATE excel_payslip_batches SET batch_json=JSON_SET(batch_json,'$.rows[0].employeeName','Changed source')",
                "unknownSource"=>"UPDATE excel_payslip_calculation_sources SET source_json=JSON_SET(source_json,'$.unknownWitness','changed')",
                "template"=>"UPDATE salarystructurelines SET Formula='I_BASIC_31_EARNED*14%' WHERE StructureId=71 AND ComponentId='1014'",
                _=>"UPDATE employee_monthly_attendance SET payable_days=29 WHERE employee_id=41"});
            await Assert.ThrowsAsync<SelectedPayRunConflictException>(()=>f.ApplyAsync(review));Assert.Equal(0,await f.WrittenAsync());
        }
    }
    [LocalWorkflowScopeDatabaseFact]
    public async Task False_ack_foreign_owner_missing_policy_and_explicit_zero_never_fall_back()
    {
        await using var f=await Fixture.CreateAsync();var q=f.Request();q.InitialSourceTargetReview!.Reviewed=false;
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Repository.PreviewHistoricalSalaryAsync(31,q,Actor));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Repository.PreviewHistoricalSalaryAsync(32,f.Request(),Actor));
        var review=await f.ReadyAsync();await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Repository.ApplyHistoricalSalaryAsync(31,new(){ReviewToken=review.ReviewToken,RequestId=Guid.NewGuid().ToString("N"),Acknowledged=false},Actor));
        await f.Db.ExecuteAsync("DELETE FROM attendance_group_employees");var noPolicy=await f.Repository.PreviewHistoricalSalaryAsync(31,f.Request(),Actor);Assert.False(noPolicy.CanApply);Assert.Contains(noPolicy.Blockers,b=>b.Contains("native actual presence"));
        await f.Db.ExecuteAsync("INSERT INTO attendance_group_employees VALUES(51,41);UPDATE employees SET AnnualCtc=0 WHERE Id=41");
        f.Source.Rows[0].Cells[3]!.Value="0";f.Batch.Rows[0].Information.Single(v=>v.Label=="Current CTC").Value="0";await f.SaveSourceAsync();var zero=await f.Repository.PreviewHistoricalSalaryAsync(31,f.Request(),Actor);
        Assert.False(zero.CanApply);Assert.Equal(0,Assert.Single(zero.Rows).InitialSourceAnnualSalaryTarget);Assert.Contains(zero.Blockers,b=>b.Contains("explicit zero"));Assert.Equal(0,await f.WrittenAsync());
    }
    [LocalWorkflowScopeDatabaseFact]
    public async Task Initial_mode_rejects_current_future_and_wrong_shared_divisor_without_any_writes()
    {
        await using var f=await Fixture.CreateAsync();
        foreach(var period in new[]{"2026-11","2026-12"})
        {
            f.Batch.Month=period;await f.SaveSourceAsync();var q=f.Request();q.Setup.EffectiveFrom=period+"-01";
            await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Repository.PreviewHistoricalSalaryAsync(31,q,Actor));
        }
        f.Batch.Month="2026-09";await f.SaveSourceAsync();var wrong=f.Request();wrong.Setup.PayrollDaysColumnIndex=-1;wrong.Setup.FixedPayrollDays=30;wrong.Setup.PayrollDaysCell="E2";
        wrong.Setup.AttendanceReview!.BoundSourceInputs=ExcelPayrollSetupRepository.AttendanceBinding(wrong.Setup);
        var result=await f.Repository.PreviewHistoricalSalaryAsync(31,wrong,Actor);Assert.False(result.CanApply);Assert.Contains(result.Blockers,b=>b.Contains("captured shared divisor"));Assert.Equal(0,await f.WrittenAsync());
    }
    [LocalWorkflowScopeDatabaseFact]
    public async Task An_equal_valued_unselected_salary_cell_cannot_replace_the_reviewed_CTC_dependency()
    {
        await using var f=await Fixture.CreateAsync();Assert.True((await f.ReadyAsync()).CanApply);
        f.Source.ColumnCount=14;f.Source.Columns.Add(new(){ColumnIndex=13,SourceHeader="Last month salary",Label="Last month salary",Kind="info"});
        f.Source.Rows[0].Cells.Add(new(){Value="10000"});f.Batch.Rows[0].Information.Add(new(){Label="Last month salary",Value="10000"});
        f.Source.Rows[0].Cells[8]!.FormulaText="N2/E2*F2-H2";await f.SaveSourceAsync();
        var witness=await f.WitnessAsync();var preview=await f.Repository.PreviewHistoricalSalaryAsync(31,f.Request(),Actor);
        Assert.False(preview.CanApply);Assert.Contains(preview.Blockers,b=>b.Contains("Captured formula translation differs"));
        Assert.Equal(0,await f.WrittenAsync());Assert.Equal(witness,await f.WitnessAsync());
    }
    [LocalWorkflowScopeDatabaseFact]
    public async Task Explicit_literal_self_input_saves_only_its_own_zero_or_positive_closed_period_value_and_replays()
    {
        foreach(var literal in new[]{0m,200m})
        {
            await using var f=await Fixture.CreateAsync();await f.SetLiteralSelfInputAsync(literal);var q=f.SelfInputRequest();
            var before=await f.WitnessAsync();var preview=await f.Repository.PreviewHistoricalSalaryAsync(31,q,Actor);
            Assert.True(preview.CanApply,string.Join(";",preview.Blockers));var evidence=Assert.Single(Assert.Single(preview.Rows).ReviewedLiteralSelfInputs!);
            Assert.Equal(literal,evidence.LiteralValue);Assert.Equal("1012",evidence.InputKey);Assert.Equal(7,evidence.ColumnIndex);
            Assert.Equal(literal,preview.Rows[0].SourceMonthlyInputs["1012"]);Assert.Equal(before,await f.WitnessAsync());Assert.Equal(0,await f.WrittenAsync());
            var id=Guid.NewGuid().ToString("N");var receipt=await f.ApplyAsync(preview,id);Assert.Equal(literal,Assert.Single(receipt.LiteralSelfInputs!).LiteralValue);
            var salary=await f.Db.ExecuteScalarAsync<string>("SELECT CAST(SalaryJson AS CHAR) FROM employee_it0008_basic_pay WHERE ActionType='Closed Excel Salary'")??throw new InvalidOperationException("Expected closed salary was not saved.");
            Assert.Equal(before,await f.WitnessAsync());Assert.Equal(literal,JsonSerializer.Deserialize<Dictionary<string,decimal>>(salary)!["1012"]);
            var actual=await f.Runs.SimulateEmployeeAsync(new(){Mode="Employee",ClientId=31,EmployeeId=41,PayPeriod="2026-09"},default);
            Assert.True(actual.Complete);Assert.Equal(10000,actual.Gross);Assert.Equal(10000-(10000-literal)*.12m,actual.Net);
            Assert.True((await f.ApplyAsync(preview,id)).Replayed);Assert.Equal(1,await f.WrittenAsync());
        }
    }
    [LocalWorkflowScopeDatabaseFact]
    public async Task Literal_self_input_review_never_relaxes_false_ambiguous_or_reclassified_bindings()
    {
        await using var f=await Fixture.CreateAsync();await f.SetLiteralSelfInputAsync(0);
        foreach(var change in new[]{"absent","false","version","hash","duplicate","missingRow","foreignRow","inputAlias","componentAlias","otherColumn","role","prorata","formula","blankZero","ctc"})
        {
            var q=f.SelfInputRequest();var review=q.LiteralSelfInputReview!;var binding=review.Bindings[0];
            switch(change)
            {
                case "absent":q.LiteralSelfInputReview=null;break;
                case "false":review.Reviewed=false;break;case "version":review.Version=0;break;case "hash":review.SourceHash=new('b',64);break;
                case "duplicate":review.Bindings.Add(binding);break;case "missingRow":binding.RowIds.Clear();break;case "foreignRow":binding.RowIds.Add("not-selected");break;
                case "inputAlias":review.Bindings[0]=binding with{InputKey="01012"};break;case "componentAlias":review.Bindings[0]=binding with{ComponentKey="i_bonus_31"};break;
                case "otherColumn":review.Bindings[0]=binding with{ColumnIndex=6};break;case "role":q.Setup.SourceColumnRoles[7]="info";break;
                case "prorata":q.Setup.Components.Single(c=>c.Key==binding.ComponentKey).ProRata=true;break;
                case "formula":q.Setup.Components.Single(c=>c.Key==binding.ComponentKey).Formula="0";break;
                case "blankZero":q.Setup.Components.Single(c=>c.Key==binding.ComponentKey).BlankInputAsZero=true;break;
                case "ctc":q.Setup.MonthlyCtcColumnIndex=7;q.InitialSourceTargetReview!.MonthlyCtcColumnIndex=7;break;
            }
            await f.RequireRejectedWithoutWritesAsync(q);
        }
    }
    [LocalWorkflowScopeDatabaseFact]
    public async Task Literal_self_input_cannot_freeze_a_formula_cache_or_waive_native_policy_and_financial_proof()
    {
        await using var f=await Fixture.CreateAsync();await f.SetLiteralSelfInputAsync(0);
        foreach(var change in new[]{"formulaCache","missingCache","text","blank","error","wrongFinancialRule","netPaise"})
        {
            await f.SetLiteralSelfInputAsync(0);var cell=f.Source.Rows[0].Cells[7]!;
            if(change=="formulaCache")cell.FormulaText="0";if(change=="missingCache")cell.MissingCachedValue=true;
            if(change=="text")cell.DataType="Text";if(change=="blank")cell.Value="";if(change=="error")cell.Error="#VALUE!";
            if(change=="wrongFinancialRule")f.Source.Rows[0].Cells[9]!.FormulaText="I2*11%";
            if(change=="netPaise"){f.Source.Rows[0].Cells[11]!.Value="8800.01";f.Batch.Rows[0].NetPay=8800.01m;}
            await f.SaveSourceAsync();await f.RequireRejectedWithoutWritesAsync(f.SelfInputRequest());
        }
        await f.SetLiteralSelfInputAsync(0);await f.Db.ExecuteAsync("DELETE FROM attendance_group_employees");await f.RequireRejectedWithoutWritesAsync(f.SelfInputRequest());
    }
    [LocalWorkflowScopeDatabaseFact]
    public async Task Reviewed_self_input_source_and_current_master_changes_invalidate_apply_without_any_salary_insert()
    {
        foreach(var change in new[]{"source","current"})
        {
            await using var f=await Fixture.CreateAsync();await f.SetLiteralSelfInputAsync(200);var preview=await f.Repository.PreviewHistoricalSalaryAsync(31,f.SelfInputRequest(),Actor);Assert.True(preview.CanApply,string.Join(";",preview.Blockers));
            if(change=="source")await f.SetLiteralSelfInputAsync(0);else await f.Db.ExecuteAsync("UPDATE employees SET AnnualCtc=120001 WHERE Id=41");
            var before=await f.WitnessAsync();await Assert.ThrowsAsync<SelectedPayRunConflictException>(()=>f.ApplyAsync(preview));Assert.Equal(before,await f.WitnessAsync());Assert.Equal(0,await f.WrittenAsync());
        }
    }

    private sealed class Fixture(MySqlConnection root,MySqlConnection db,string name,IConfiguration config):IAsyncDisposable
    {
        internal const string BatchId="00000000000000000000000000000031";
        internal MySqlConnection Db{get;}=db;
        internal PayRunRepository Runs{get;}=new(config,new TaxEngineRepository(config));
        internal ExcelPayrollSetupRepository Repository=>new(config,new EmployeeRepository(config,null!,null!),Runs);
        internal ExcelPayslipBatch Batch{get;set;}=new();internal ExcelPayslipCalculationSource Source{get;set;}=new();
        internal static async Task<Fixture> CreateAsync()
        {
            var target=new MySqlConnectionStringBuilder(Environment.GetEnvironmentVariable("FREVO_WORKFLOW_SCOPE_TEST_DB")??throw new InvalidOperationException("Owned loopback fixture required."));
            Assert.Contains(target.Server,new[]{"localhost","127.0.0.1","::1"});Assert.Equal(33331u,target.Port);target.Database="";
            var root=new MySqlConnection(target.ConnectionString);await root.OpenAsync();Assert.Equal("frevo-workflow-scope-test-20261114",await root.ExecuteScalarAsync<string>("SELECT @@hostname"));
            var name="hrms_initial_salary_test_"+Guid.NewGuid().ToString("N");await root.ExecuteAsync($"CREATE DATABASE `{name}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci");target.Database=name;
            var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["ConnectionStrings:Default"]=target.ConnectionString,["Database:AutoMigrate"]="false",["BackgroundWorkers:Enabled"]="false",["OutboundDelivery:Suppressed"]="true",["IntegrationCredentialEncryption:MasterKey"]="isolated-initial-history-key"}).Build();
            var db=new MySqlConnection(target.ConnectionString);await db.OpenAsync();
            await db.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,"Fixtures","EssProfile","profile-schema.sql")));
            await db.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,"Fixtures","EssProfile","payslip-schema.sql")));
            await PayrollDataTableStore.EnsureAsync(db);await new EmployeeRepository(config,null!,null!).InitializeAsync();
            await db.ExecuteAsync("""
CREATE TABLE modulesettings(client_id INT,ModuleCode VARCHAR(120),IsEnabled BOOLEAN,SettingsJson JSON,PRIMARY KEY(client_id,ModuleCode));
CREATE TABLE payrollsetups(Id INT PRIMARY KEY AUTO_INCREMENT,SetupJson JSON);
CREATE TABLE tax_client_settings(id INT PRIMARY KEY AUTO_INCREMENT,client_id INT,financial_year VARCHAR(10),enabled BOOLEAN,project_monthly_tds BOOLEAN,tax_deduction_component_code VARCHAR(80) DEFAULT 'TDS',active BOOLEAN);
CREATE TABLE tax_rule_versions(id INT PRIMARY KEY AUTO_INCREMENT,financial_year VARCHAR(10),active BOOLEAN);
CREATE TABLE employee_monthly_attendance(id INT PRIMARY KEY AUTO_INCREMENT,client_id INT,employee_id INT,attendance_month CHAR(7),working_days DECIMAL(8,2),present_days DECIMAL(8,2),payable_days DECIMAL(8,2),source_type VARCHAR(80),remarks TEXT);
CREATE TABLE employee_daily_attendance(id INT PRIMARY KEY AUTO_INCREMENT,client_id INT,employee_id INT,attendance_date DATE,status VARCHAR(30),payable_value DECIMAL(8,2));
CREATE TABLE attendance_groups(id INT PRIMARY KEY,client_id INT,policy_batch_id VARCHAR(64),name VARCHAR(160),work_location_id INT,department VARCHAR(100),designation VARCHAR(100),work_week VARCHAR(80),attendance_cycle_start_day INT,attendance_cycle_end_day INT,payroll_report_generation_day INT,is_active BOOLEAN,shift_id INT,attendance_mode VARCHAR(30),allowed_shift_ids_json JSON);
CREATE TABLE attendance_group_employees(attendance_group_id INT,employee_id INT,PRIMARY KEY(attendance_group_id,employee_id));
CREATE TABLE payruns(Id INT PRIMARY KEY AUTO_INCREMENT,ClientId INT,ClientName VARCHAR(180),PayPeriod CHAR(7),RunCode VARCHAR(50),RunType VARCHAR(30),Status VARCHAR(30),RequestJson JSON);
CREATE TABLE payrunemployees(Id INT PRIMARY KEY AUTO_INCREMENT,PayRunId INT,EmployeeId INT,ClientId INT,IsSkipped BOOLEAN,DetailsJson JSON);
CREATE TABLE payrolladjustments(Id INT PRIMARY KEY AUTO_INCREMENT,ClientId INT,EmployeeId INT,ComponentId INT,ComponentCode VARCHAR(80),ComponentName VARCHAR(150),AdjustmentType VARCHAR(30),Amount DECIMAL(18,2),PayPeriod CHAR(7),PayRunType VARCHAR(30),Taxable BOOLEAN,Status VARCHAR(30),PayRunId INT);
ALTER TABLE clients ADD COLUMN Code VARCHAR(80) DEFAULT '';
ALTER TABLE worklocations ADD COLUMN ClientId INT,ADD COLUMN State VARCHAR(100),ADD COLUMN City VARCHAR(100);
ALTER TABLE employees ADD COLUMN PersonalJson JSON,ADD COLUMN PaymentJson JSON,ADD COLUMN Grade VARCHAR(80) DEFAULT '',ADD COLUMN PortalAccess BOOLEAN DEFAULT FALSE;
INSERT INTO clients(Id,Code,Name,IsActive) VALUES(31,'INIT','Synthetic initial source',TRUE);
INSERT INTO worklocations VALUES(31,'Synthetic office',31,'Synthetic state','Synthetic city');
INSERT INTO salarycomponents(Id,Code,Name,Category,ComponentRole,StatutoryType,CalculationType,Formula,ProRata,Taxable,PayType,Priority) VALUES
(1011,'I_BASIC_31','Initial basic','Earning','Regular Earning','None','Formula','MONTHLY_CTC/PAYROLL_DAYS*PAYABLE_DAYS-I_BONUS_31_EARNED',FALSE,TRUE,'Fixed Pay',10),
(1012,'I_BONUS_31','Initial bonus','Earning','Regular Earning','None','Fixed Amount','',FALSE,TRUE,'Fixed Pay',20),
(1013,'I_DED_31','Initial deduction','Deduction','Regular Deduction','None','Formula','I_BASIC_31_EARNED*12%',FALSE,FALSE,'Fixed Pay',30),
(1014,'I_ER_31','Initial employer cost','Benefit','Employer Benefit','None','Formula','I_BASIC_31_EARNED*13%',FALSE,FALSE,'Fixed Pay',40);
INSERT INTO salarystructures(Id,ClientId,Name,AnnualCtc,Active) VALUES(71,31,'Closed source template',0,TRUE),(72,31,'Current future template',120000,TRUE);
INSERT INTO salarystructurelines(StructureId,ComponentId,ValueText,CalculationType,Formula,ProRataOverride,SortOrder) VALUES
(71,'1011','0','Formula','MONTHLY_CTC/PAYROLL_DAYS*PAYABLE_DAYS-I_BONUS_31_EARNED','false',1),(71,'1012','0','Fixed Amount','','false',2),(71,'1013','0','Formula','I_BASIC_31_EARNED*12%','false',3),(71,'1014','0','Formula','I_BASIC_31_EARNED*13%','false',4),
(72,'1011','0','Formula','MONTHLY_CTC/PAYROLL_DAYS*PAYABLE_DAYS-I_BONUS_31_EARNED','false',1),(72,'1012','0','Fixed Amount','','false',2),(72,'1013','0','Formula','I_BASIC_31_EARNED*12%','false',3),(72,'1014','0','Formula','0','false',4);
INSERT INTO employees(Id,ClientId,EmployeeCode,FirstName,LastName,DateOfJoining,AnnualCtc,SalaryStructureId,SalaryJson,PersonalJson,PaymentJson,WorkLocationId) VALUES(41,31,'INIT41','Synthetic','One','',120000,'72',JSON_OBJECT('1012',777),JSON_OBJECT(),JSON_OBJECT(),31);
INSERT INTO employeepersonaldetails(EmployeeId,UanNumber) VALUES(41,'101234567891');
INSERT INTO employeesalarycomponents VALUES(41,'1012',777);
INSERT INTO employee_it0008_basic_pay(EmployeeId,ClientId,ActionType,EffectiveFrom,Status,SalaryStructureId,AnnualCtc,SalaryJson,ChangeReason,CreatedBy) VALUES(41,31,'Hire','2026-11-10','Active','72',120000,JSON_OBJECT('1012',777),'Recorded at import, no actual DOJ established','synthetic'),(41,31,'Future salary','2027-01-01','Active','72',144000,JSON_OBJECT('1012',888),'Retain future','synthetic');
INSERT INTO employee_it0000_actions(EmployeeId,ClientId,ActionType,EffectiveFrom,Status) VALUES(41,31,'Hire','2026-11-10','Active');
INSERT INTO employee_monthly_attendance VALUES(1,31,41,'2026-09',30,30,30,'Monthly','Reviewed month-only source inputs');
INSERT INTO attendance_groups VALUES(51,31,'initial-source','Synthetic monthly group',31,'','','Sunday off',1,31,3,TRUE,NULL,'Monthly',NULL);INSERT INTO attendance_group_employees VALUES(51,41);
INSERT INTO tax_client_settings(client_id,financial_year,enabled,project_monthly_tds,active) VALUES(31,'2026-27',FALSE,FALSE,TRUE);
""");
            foreach(var (column,type) in new[]{("State","VARCHAR(100) DEFAULT ''"),("EsicEmployee","DECIMAL(18,4) DEFAULT 0"),("PtLwfWorkmenComp","DECIMAL(18,4) DEFAULT 0"),("Tds","DECIMAL(18,4) DEFAULT 0"),("Recovery","DECIMAL(18,4) DEFAULT 0")})
                if(!await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='employeepersonaldetails' AND column_name=@Column)",new{Column=column}))await db.ExecuteAsync($"ALTER TABLE employeepersonaldetails ADD COLUMN `{column}` {type}");
            var f=new Fixture(root,db,name,config);var row=new ExcelPayslipRow{Id="initial-one",SourceRow=2,EmployeeCode="INIT41",EmployeeName="Synthetic One",Earnings=[new(){Label="Basic",Amount=10000},new(){Label="Bonus",Amount=0}],Deductions=[new(){Label="Deduction",Amount=1200}],EmployerContributions=[new(){Label="Employer cost",Amount=1300}],DeclaredGross=10000,DeclaredDeductions=1200,NetPay=8800};
            f.Batch=new(){Id=BatchId,ClientId=31,Month="2026-09",SheetName="Salary",HeaderRow=1,Rows=[row]};
            var labels=new[]{"Employee Code","Employee Name","UAN","Current CTC","Payroll days","Payable days","Monthly bonus input","Bonus","Basic","Deduction","Employer cost","Net pay","Actual present days"};
            var kinds=new[]{"employeeCode","employeeName","info","info","info","info","info","earning","earning","deduction","employer","netPay","info"};
            f.Source=new(){SheetName="Salary",HeaderRow=1,ColumnCount=13,Columns=labels.Select((label,i)=>new ExcelPayslipCalculationColumn{ColumnIndex=i,SourceHeader=label,Label=label,Kind=kinds[i]}).ToList(),Rows=[new(){SourceRow=2,Cells=new[]{"INIT41","Synthetic One","101234567891","10000","30","30","0","0","10000","1200","1300","8800","30"}.Select((v,i)=>(ExcelPayslipCalculationCell?)new ExcelPayslipCalculationCell{Value=v,FormulaText=i switch{8=>"D2/E2*F2-H2",9=>"I2*12%",10=>"I2*13%",11=>"I2+H2-J2",_=>null}}).ToList()}]};
            row.Information=f.Source.Columns.Where(c=>c.Kind=="info").Select(c=>new ExcelPayslipText{Label=c.Label,Value=f.Source.Rows[0].Cells[c.ColumnIndex]!.Value}).ToList();
            row.Earnings=[new(){Label="Bonus",Amount=0},new(){Label="Basic",Amount=10000}];
            await f.SaveSourceAsync();return f;
        }
        internal string SourceHash()=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{Batch,Source},Json)))).ToLowerInvariant();
        internal async Task SetLiteralSelfInputAsync(decimal literal)
        {
            var value=(10000-literal);var cells=Source.Rows[0].Cells;
            cells[7]=new(){Value=literal.ToString(System.Globalization.CultureInfo.InvariantCulture)};
            cells[8]!.Value=value.ToString(System.Globalization.CultureInfo.InvariantCulture);cells[9]!.Value=(value*.12m).ToString(System.Globalization.CultureInfo.InvariantCulture);cells[9]!.FormulaText="I2*12%";
            cells[10]!.Value=(value*.13m).ToString(System.Globalization.CultureInfo.InvariantCulture);cells[11]!.Value=(10000-value*.12m).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var row=Batch.Rows[0];row.Earnings[0].Amount=literal;row.Earnings[1].Amount=value;row.Deductions[0].Amount=value*.12m;row.DeclaredDeductions=value*.12m;row.EmployerContributions[0].Amount=value*.13m;row.NetPay=10000-value*.12m;
            await SaveSourceAsync();
            // Source hashes use the actual persisted JSON round trip, including
            // MySQL's numeric representation, rather than the pre-save decimal scale.
            Batch=JsonSerializer.Deserialize<ExcelPayslipBatch>(await Db.ExecuteScalarAsync<string>("SELECT CAST(batch_json AS CHAR) FROM excel_payslip_batches WHERE client_id=31 AND id=@Id",new{Id=BatchId})??throw new InvalidOperationException("Saved batch missing."),Json)!;
            Source=JsonSerializer.Deserialize<ExcelPayslipCalculationSource>(await Db.ExecuteScalarAsync<string>("SELECT CAST(source_json AS CHAR) FROM excel_payslip_calculation_sources WHERE client_id=31 AND batch_id=@Id",new{Id=BatchId})??throw new InvalidOperationException("Saved source missing."),Json)!;
        }
        internal HistoricalPeriodSalaryRequest SelfInputRequest()
        {
            var q=Request();var component=q.Setup.Components.Single(c=>c.Code=="I_BONUS_31");component.SourceColumnIndex=7;
            q.LiteralSelfInputReview=new(){Version=1,Reviewed=true,SourceHash=SourceHash(),Bindings=[new(component.Key,component.ExistingComponentId!,7,["initial-one"])]};return q;
        }
        internal async Task RequireRejectedWithoutWritesAsync(HistoricalPeriodSalaryRequest q)
        {
            var before=await WitnessAsync();var error=await Record.ExceptionAsync(async()=>{var p=await Repository.PreviewHistoricalSalaryAsync(31,q,Actor);Assert.False(p.CanApply);});
            if(error is not null)Assert.IsAssignableFrom<InvalidOperationException>(error);
            Assert.Equal(before,await WitnessAsync());Assert.Equal(0,await WrittenAsync());
        }
        internal async Task SaveSourceAsync()=>await Db.ExecuteAsync("INSERT INTO excel_payslip_batches(client_id,id,batch_json,send_state_json,created_at) VALUES(31,@Id,CAST(@Batch AS JSON),JSON_OBJECT('oldMail','retained'),UTC_TIMESTAMP()) ON DUPLICATE KEY UPDATE batch_json=VALUES(batch_json);INSERT INTO excel_payslip_calculation_sources(client_id,batch_id,source_json,created_at) VALUES(31,@Id,CAST(@Source AS JSON),UTC_TIMESTAMP()) ON DUPLICATE KEY UPDATE source_json=VALUES(source_json)",new{Id=BatchId,Batch=JsonSerializer.Serialize(Batch,Json),Source=JsonSerializer.Serialize(Source,Json)});
        internal HistoricalPeriodSalaryRequest Request()
        {
            ExcelPayrollSetupComponent C(string id,string code,string category,string role,string formula,int output,int priority,int? input=null)=>new(){Key=code,Code=code,Name=code,ExistingComponentId=id,Category=category,ComponentRole=role,StatutoryType="None",CalculationType=input.HasValue?"Fixed Amount":"Formula",Formula=formula,Value="0",ProRata=false,Taxable=category=="Earning",PayType="Fixed Pay",Priority=priority,SourceColumnIndex=input,ExpectedColumnIndex=output};
            var q=new HistoricalPeriodSalaryRequest{SalaryTargetMode="ReviewedInitialSourceTarget",InitialSourceTargetReview=new(){Version=1,Reviewed=true,MonthlyCtcColumnIndex=3,SourceHash=SourceHash(),Meaning="MonthlyPayrollEarningsBudget",ActualJoiningDateNotEstablished=true},Assignments=[new("initial-one",41,"71")],Setup=new(){BatchId=BatchId,RowIds=["initial-one"],TemplateName="Explicit initial source",EffectiveFrom="2026-09-01",PayrollDaysColumnIndex=4,PayableDaysColumnIndex=5,PresentDaysColumnIndex=12,MonthlyCtcColumnIndex=3,WorkWeek="Sunday off",AttendanceCycleStartDay=1,AttendanceCycleEndDay=31,PayrollReportGenerationDay=3,ImportAttendance=false,TransferReviewVersion=1,TemplateScopeReviewVersion=1,ReviewedFixedInputs=true,ReviewedPolicyScope=true,TemplateScope=new(){Reviewed=true,EffectiveTo="2026-09-30",LocationMode="Listed",WorkLocationIds=[31],CategoryKind="AllClientCategories",DivisorMeaning="CalendarDays",PayableMeaning="AlreadyPayable",PolicyBasis="SavedSourceRules"},TemplateDecisions=[new(){VariantKey="71",ExistingTemplateId="71",ScopeAction="AttestLegacyScope",ScopeReason="Explicit closed period initial source rules"}],SourceColumnRoles=Source.Columns.ToDictionary(c=>c.ColumnIndex,c=>c.Kind),AttendanceReview=new(){Mode="VerifyExistingNative",Reviewed=true,SourceHash=SourceHash(),SourceMonth=Batch.Month,CycleStart="2026-09-01",CycleEnd="2026-09-30",PayableMeaning="AlreadyPayable",PresentMeaning="ActualWorked"},Components=[C("1011","I_BASIC_31","Earning","Regular Earning","MONTHLY_CTC/PAYROLL_DAYS*PAYABLE_DAYS-I_BONUS_31_EARNED",8,10),C("1012","I_BONUS_31","Earning","Regular Earning","",7,20,6),C("1013","I_DED_31","Deduction","Regular Deduction","I_BASIC_31_EARNED*12%",9,30),C("1014","I_ER_31","Benefit","Employer Benefit","I_BASIC_31_EARNED*13%",10,40)]}};
            q.Setup.AttendanceReview!.BoundSourceInputs=ExcelPayrollSetupRepository.AttendanceBinding(q.Setup);return q;
        }
        internal async Task<HistoricalPeriodSalaryPreview> ReadyAsync(){var p=await Repository.PreviewHistoricalSalaryAsync(31,Request(),Actor);Assert.True(p.CanApply,string.Join(";",p.Blockers));return p;}
        internal Task<HistoricalPeriodSalaryReceipt> ApplyAsync(HistoricalPeriodSalaryPreview p,string? id=null)=>Repository.ApplyHistoricalSalaryAsync(31,new(){ReviewToken=p.ReviewToken,RequestId=id??Guid.NewGuid().ToString("N"),Acknowledged=true},Actor);
        internal Task<int> WrittenAsync()=>Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employee_it0008_basic_pay WHERE ActionType='Closed Excel Salary'");
        internal async Task<string> WitnessAsync()=>JsonSerializer.Serialize(new{Master=await Db.QueryAsync("SELECT * FROM employees ORDER BY Id"),Personal=await Db.QueryAsync("SELECT * FROM employeepersonaldetails"),Bank=await Db.QueryAsync("SELECT * FROM employeepaymentdetails"),Inputs=await Db.QueryAsync("SELECT * FROM employeesalarycomponents"),History=await Db.QueryAsync("SELECT * FROM employee_it0008_basic_pay WHERE ActionType<>'Closed Excel Salary' ORDER BY Id"),Actions=await Db.QueryAsync("SELECT * FROM employee_it0000_actions"),Batch=await Db.QueryAsync("SELECT * FROM excel_payslip_batches"),Source=await Db.QueryAsync("SELECT * FROM excel_payslip_calculation_sources"),Attendance=await Db.QueryAsync("SELECT * FROM employee_monthly_attendance"),Groups=await Db.QueryAsync("SELECT * FROM attendance_groups"),Members=await Db.QueryAsync("SELECT * FROM attendance_group_employees")});
        public async ValueTask DisposeAsync(){await Db.DisposeAsync();MySqlConnection.ClearAllPools();Assert.Matches("^hrms_initial_salary_test_[a-f0-9]{32}$",name);await root.ExecuteAsync($"DROP DATABASE `{name}`");await root.DisposeAsync();}
    }
}

