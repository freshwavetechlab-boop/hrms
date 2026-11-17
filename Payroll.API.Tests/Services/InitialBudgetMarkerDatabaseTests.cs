using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Tests.Fixtures;

namespace Payroll.API.Tests.Services;

[Collection("ReviewedPayroll")]
public sealed class InitialBudgetMarkerDatabaseTests:IAsyncLifetime
{
    private const int Client=97963,One=9796301;
    private const string BatchId="97963000000000000000000000000001";
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private readonly ReviewedPayrollFixture? fixture=Environment.GetEnvironmentVariable("FREVO_REVIEWED_PAYROLL_TEST_DB") is string c?new(c):null;
    private ReviewedPayrollFixture F=>fixture??throw new InvalidOperationException("The owned sentinel-guarded fixture is required.");
    private ExcelPayrollSetupRepository Repo=>new(F.Config,new EmployeeRepository(F.Config,null!,null!));
    private sealed class IsolatedFactAttribute:FactAttribute{public IsolatedFactAttribute(){if(Environment.GetEnvironmentVariable("FREVO_REVIEWED_PAYROLL_TEST_DB") is null)Skip="Owned loopback fixture required.";}}
    public async Task InitializeAsync()
    {
        if(fixture is null)return;await F.InitializeAsync();await using var db=await F.OpenAsync();
        await db.ExecuteAsync(ExcelPayrollSetupRepository.Schema);
        await db.ExecuteAsync(@"CREATE TABLE IF NOT EXISTS employee_tax_regime_selections (
id INT PRIMARY KEY AUTO_INCREMENT,employee_id INT NOT NULL,client_id INT NOT NULL DEFAULT 0,financial_year VARCHAR(10) NOT NULL,regime VARCHAR(20) NOT NULL DEFAULT 'New',
status VARCHAR(30) NOT NULL DEFAULT 'Draft',submitted_at DATETIME NULL,approved_at DATETIME NULL,created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
UNIQUE KEY UX_employee_tax_regime(employee_id,financial_year));");
        if(!await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='employeesalarycomponents' AND column_name='ComponentCode')"))
            await db.ExecuteAsync("ALTER TABLE employeesalarycomponents ADD COLUMN ComponentCode VARCHAR(80) NOT NULL DEFAULT ''");
        Assert.Equal(0,await db.ExecuteScalarAsync<int>(@"SELECT
(SELECT COUNT(*) FROM clients WHERE Id=97963)+(SELECT COUNT(*) FROM employees WHERE ClientId=97963 OR Id=9796301)
+(SELECT COUNT(*) FROM salarycomponents WHERE Id BETWEEN 9796301 AND 9796303)+(SELECT COUNT(*) FROM salarystructures WHERE ClientId=97963)
+(SELECT COUNT(*) FROM excel_payslip_batches WHERE client_id=97963)+(SELECT COUNT(*) FROM excel_payslip_calculation_sources WHERE client_id=97963)
+(SELECT COUNT(*) FROM employee_it0008_basic_pay WHERE ClientId=97963)+(SELECT COUNT(*) FROM modulesettings WHERE client_id=97963)"));
        await db.ExecuteAsync(@"
INSERT INTO clients(Id,Code,Name,IsActive) VALUES(97963,'INITIAL-BUDGET','Synthetic budget setup',TRUE);
INSERT INTO worklocations(Id,ClientId,Name,City,State) VALUES(97963,97963,'Synthetic budget office','Synthetic city','Synthetic state');
INSERT INTO salarycomponents(Id,Code,Name,Category,ComponentRole,StatutoryType,CalculationType,ValueText,Formula,ProRata,Taxable,Priority,PayType,Active) VALUES
(9796301,'IB_BASIC','Basic','Earning','Regular Earning','None','Fixed Amount','0','',TRUE,TRUE,10,'Fixed Pay',TRUE),
(9796302,'IB_DEDUCTION','Deduction','Deduction','Regular Deduction','None','Fixed Amount','1000','',FALSE,FALSE,20,'Fixed Pay',TRUE),
(9796303,'IB_EMPLOYER','Employer cost','Benefit','Employer Benefit','None','Fixed Amount','500','',FALSE,FALSE,30,'Fixed Pay',TRUE);
INSERT INTO employees(Id,ClientId,EmployeeCode,FirstName,LastName,DateOfJoining,AnnualCtc,SalaryStructureId,SalaryJson,PersonalJson,PaymentJson,WorkLocationId,Department,Designation,Grade,PortalAccess,IsActive)
VALUES(9796301,97963,'IB-ONE','Synthetic','Budget','2026-01-01',120000,'',JSON_OBJECT(),JSON_OBJECT(),JSON_OBJECT(),97963,'Testing','Operator','Test grade',FALSE,TRUE);
INSERT INTO employeepersonaldetails(EmployeeId,AadhaarNumber) VALUES(9796301,'823456789012');
INSERT INTO employee_it0008_basic_pay(EmployeeId,ClientId,ActionType,EffectiveFrom,Status,SalaryStructureId,AnnualCtc,SalaryJson,ChangeReason,CreatedBy)
VALUES(9796301,97963,'Master Update','2026-11-15','Active','',120000,JSON_OBJECT(),'Synthetic initial budget only','Synthetic Master creator');
INSERT INTO dropdownmasters(ClientId,Type,Value,IsActive) VALUES(97963,'Work Week','Sunday off',TRUE),(97963,'Department','Testing',TRUE),(97963,'Designation','Operator',TRUE),(97963,'Grade','Test grade',TRUE);
INSERT INTO attendance_groups(id,client_id,name,work_location_id,department,designation,work_week,attendance_cycle_start_day,attendance_cycle_end_day,payroll_report_generation_day,is_active)
VALUES(97963,97963,'Existing synthetic policy',97963,'All','All','Sunday off',1,31,3,TRUE);
INSERT INTO attendance_group_employees(attendance_group_id,employee_id) VALUES(97963,9796301);
INSERT INTO tax_client_settings(client_id,financial_year,enabled,project_monthly_tds,active) VALUES(97963,'2026-27',FALSE,FALSE,TRUE);");
        var(batch,source)=Source();await db.ExecuteAsync(@"
INSERT INTO excel_payslip_batches(client_id,id,batch_json,send_state_json,created_at) VALUES(97963,@Id,CAST(@Batch AS JSON),JSON_OBJECT('retainedHistory','synthetic-original'),UTC_TIMESTAMP());
INSERT INTO excel_payslip_calculation_sources(client_id,batch_id,source_json,created_at) VALUES(97963,@Id,CAST(@Source AS JSON),UTC_TIMESTAMP());",new{Id=BatchId,Batch=JsonSerializer.Serialize(batch,Json),Source=JsonSerializer.Serialize(source,Json)});
    }
    public async Task DisposeAsync(){if(fixture is not null)await CleanAsync();}
    private async Task CleanAsync()
    {
        await using var db=await F.OpenAsync();await db.ExecuteAsync(@"
DELETE FROM payroll_calculation_traces WHERE PayRunId IN(SELECT Id FROM payruns WHERE ClientId=97963);
DELETE FROM payrunemployeelines WHERE PayRunId IN(SELECT Id FROM payruns WHERE ClientId=97963);
DELETE FROM payrun_step_logs WHERE PayRunId IN(SELECT Id FROM payruns WHERE ClientId=97963);
DELETE FROM payroll_validation_issues WHERE PayRunId IN(SELECT Id FROM payruns WHERE ClientId=97963);
DELETE FROM payroll_reconciliation_results WHERE PayRunId IN(SELECT Id FROM payruns WHERE ClientId=97963);
DELETE FROM tax_computation_snapshots WHERE pay_run_id IN(SELECT Id FROM payruns WHERE ClientId=97963);
DELETE FROM payrunemployees WHERE ClientId=97963;DELETE FROM payruns WHERE ClientId=97963;
DELETE FROM payrolladjustments WHERE ClientId=97963;DELETE FROM employee_monthly_attendance WHERE client_id=97963;DELETE FROM employee_daily_attendance WHERE client_id=97963;
DELETE FROM employee_payroll_location_allocations WHERE client_id=97963;DELETE FROM employee_payroll_location_assignments WHERE client_id=97963;
DELETE FROM excel_payroll_setup_imports WHERE client_id=97963;DELETE FROM excel_payslip_calculation_sources WHERE client_id=97963;DELETE FROM excel_payslip_batches WHERE client_id=97963;
DELETE FROM modulesettings WHERE client_id=97963;DELETE FROM tax_client_settings WHERE client_id=97963;DELETE FROM employee_tax_regime_selections WHERE client_id=97963;
DELETE FROM attendance_group_employees WHERE employee_id=9796301;DELETE FROM attendance_groups WHERE client_id=97963;
DELETE FROM employee_audit_trail WHERE EmployeeId=9796301;DELETE FROM employee_it0008_basic_pay WHERE ClientId=97963;DELETE FROM employee_it0001_org_assignment WHERE ClientId=97963;
DELETE FROM employeesalarycomponents WHERE EmployeeId=9796301;DELETE FROM employeepersonaldetails WHERE EmployeeId=9796301;DELETE FROM employeepaymentdetails WHERE EmployeeId=9796301;
DELETE FROM employees WHERE ClientId=97963;DELETE FROM salarystructurelines WHERE StructureId IN(SELECT Id FROM salarystructures WHERE ClientId=97963);DELETE FROM salarystructures WHERE ClientId=97963;
DELETE FROM salarycomponents WHERE Id BETWEEN 9796301 AND 9796303;DELETE FROM worklocations WHERE ClientId=97963;DELETE FROM dropdownmasters WHERE ClientId=97963;DELETE FROM clients WHERE Id=97963;");
    }
    private static (ExcelPayslipBatch Batch,ExcelPayslipCalculationSource Source) Source()
    {
        var labels=new[]{"Employee code","Employee name","Monthly basic","Payroll days","Payable days","Basic","Deduction","Employer cost","Gross","Total deductions","Net pay","Aadhaar"};
        var kinds=new[]{"employeeCode","employeeName","info","info","info","earning","deduction","employer","grossTotal","deductionTotal","netPay","info"};
        var values=new[]{"IB-ONE","Synthetic Budget","10000","31","31","10000","1000","500","10000","1000","9000","823456789012"};
        var cells=values.Select((v,i)=>(ExcelPayslipCalculationCell?)new(){Value=v,DataType=i is 0 or 1 or 11?"Text":"Number"}).ToList();
        cells[5]!.FormulaText="C2/D2*E2";cells[8]!.FormulaText="F2";cells[9]!.FormulaText="G2";cells[10]!.FormulaText="I2-J2";
        var source=new ExcelPayslipCalculationSource{SheetName="Synthetic salary",HeaderRow=1,ColumnCount=12,
            Columns=labels.Select((label,i)=>new ExcelPayslipCalculationColumn{ColumnIndex=i,SourceHeader=label,Label=label,Kind=kinds[i]}).ToList(),
            Rows=[new(){SourceRow=1,Cells=labels.Select(label=>(ExcelPayslipCalculationCell?)new(){Value=label,DataType="Text"}).ToList()},new(){SourceRow=2,Cells=cells}]};
        var batch=new ExcelPayslipBatch{Id=BatchId,ClientId=Client,Month="2026-10",SheetName=source.SheetName,HeaderRow=1,SourceFileName="synthetic-salary.xlsx",
            Rows=[new(){Id="initial-budget-one",SourceRow=2,EmployeeCode=values[0],EmployeeName=values[1],Information=new[]{2,3,4,11}.Select(i=>new ExcelPayslipText{Label=labels[i],Value=values[i]}).ToList(),
                Earnings=[new(){Label="Basic",Amount=10000}],Deductions=[new(){Label="Deduction",Amount=1000}],EmployerContributions=[new(){Label="Employer cost",Amount=500}],DeclaredGross=10000,DeclaredDeductions=1000,NetPay=9000}]};
        return(batch,source);
    }
    private static ExcelPayrollSetupRequest Request()
    {
        var(batch,source)=Source();var request=new ExcelPayrollSetupRequest{BatchId=BatchId,RowIds=["initial-budget-one"],TemplateName="One reviewed initial salary template",EffectiveFrom="2026-10-01",
            UpdateExistingConfiguration=true,ReviewedFixedInputs=true,ReviewedPolicyScope=true,ImportAttendance=false,PreserveExistingAttendancePolicies=true,
            PayrollDaysColumnIndex=3,PayableDaysColumnIndex=4,WorkWeek="Sunday off",AttendanceCycleStartDay=1,AttendanceCycleEndDay=31,PayrollReportGenerationDay=3,
            DefaultDepartment="Testing",DefaultDesignation="Operator",DefaultGrade="Test grade",DefaultWorkLocationId=Client,CreateMissingDropdownValues=false,CreateMissingWorkLocations=false,
            TransferReviewVersion=1,TemplateScopeReviewVersion=1,TemplateScope=new(){Reviewed=true,OpenEnded=true,LocationMode="Listed",WorkLocationIds=[Client],CategoryKind="AllClientCategories",DivisorMeaning="CalendarDays",PayableMeaning="AlreadyPayable",PolicyBasis="SavedSourceRules"},
            SourceColumnRoles=source.Columns.ToDictionary(c=>c.ColumnIndex,c=>c.Kind),EmployeeDecisions=[new(){RowId="initial-budget-one",MatchedEmployeeId=One,AllowCreate=false}],
            Components=[
                new(){Key="basic",ExistingComponentId="9796301",Code="IB_BASIC",Name="Basic",Category="Earning",ComponentRole="Regular Earning",StatutoryType="None",CalculationType="Fixed Amount",PayType="Fixed Pay",Value="0",ProRata=true,Taxable=true,Priority=10,SourceColumnIndex=2,ExpectedColumnIndex=5},
                new(){Key="deduction",ExistingComponentId="9796302",Code="IB_DEDUCTION",Name="Deduction",Category="Deduction",ComponentRole="Regular Deduction",StatutoryType="None",CalculationType="Fixed Amount",PayType="Fixed Pay",Value="1000",ProRata=false,Taxable=false,Priority=20,ExpectedColumnIndex=6},
                new(){Key="employer",ExistingComponentId="9796303",Code="IB_EMPLOYER",Name="Employer cost",Category="Benefit",ComponentRole="Employer Benefit",StatutoryType="None",CalculationType="Fixed Amount",PayType="Fixed Pay",Value="500",ProRata=false,Taxable=false,Priority=30,ExpectedColumnIndex=7}]} ;
        request.AttendanceReview=new(){Mode="SourceSeed",SourceHash=ExcelPayslipRepository.EmploymentSourceHash(batch,source),SourceMonth=batch.Month,CycleStart="2026-10-01",CycleEnd="2026-10-31",PayableMeaning="AlreadyPayable",PresentMeaning="NotMapped",Reviewed=true};
        request.AttendanceReview.BoundSourceInputs=ExcelPayrollSetupRepository.AttendanceBinding(request);return request;
    }
    private static string Hash(object value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value,Json))));
    private static async Task<string> PreservedAsync(MySqlConnection db)=>Hash(new{
        Personal=await db.QueryAsync("SELECT * FROM employeepersonaldetails WHERE EmployeeId=9796301"),
        Bank=await db.QueryAsync("SELECT * FROM employeepaymentdetails WHERE EmployeeId=9796301"),
        Policy=await db.QueryAsync("SELECT * FROM attendance_groups WHERE client_id=97963"),Membership=await db.QueryAsync("SELECT * FROM attendance_group_employees WHERE employee_id=9796301"),
        Source=await db.QuerySingleAsync("SELECT CAST(batch_json AS CHAR) Batch,SHA2(CAST(send_state_json AS CHAR),256) Mail FROM excel_payslip_batches WHERE client_id=97963"),
        Calculation=await db.ExecuteScalarAsync<string>("SELECT CAST(source_json AS CHAR) FROM excel_payslip_calculation_sources WHERE client_id=97963")});
    [IsolatedFact]
    public async Task Normal_initial_budget_preview_apply_and_employee_payroll_preserve_budget_markers_without_attendance_writes()
    {
        await using var db=await F.OpenAsync();var preserved=await PreservedAsync(db);
        var original=await db.QuerySingleAsync<History>("SELECT * FROM employee_it0008_basic_pay WHERE EmployeeId=9796301");
        var request=Request();var first=await Repo.PreviewAsync(Client,request);var variant=Assert.Single(first.Transfer!.Templates);
        request.TemplateDecisions=[new(){VariantKey=variant.VariantKey,CreateVariant=true}];
        var preview=await Repo.PreviewAsync(Client,request);Assert.True(preview.CanApply,string.Join(";",preview.Issues.Select(i=>i.Message)));
        Assert.False(F.Config.GetValue<bool>("Payroll:PayableOnlyAttendanceEnabled"));Assert.Null(request.PresentDaysColumnIndex);Assert.False(request.ImportAttendance);
        Assert.Contains(preview.Issues,i=>i.Message.Contains("Reviewed matching initial salary-budget"));
        Assert.Equal(0,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employee_monthly_attendance WHERE client_id=97963"));
        await db.ExecuteAsync("UPDATE employee_it0008_basic_pay SET SalaryJson=JSON_OBJECT('9796301',0) WHERE Id=@Id",new{original.Id});
        var configured=await Repo.PreviewAsync(Client,request);Assert.False(configured.CanApply);Assert.Contains(configured.Issues,i=>i.Message.Contains("later configured basic-pay"));
        await db.ExecuteAsync("UPDATE employee_it0008_basic_pay SET SalaryJson=JSON_OBJECT() WHERE Id=@Id",new{original.Id});
        await db.ExecuteAsync("UPDATE employees SET AnnualCtc=120001 WHERE Id=9796301");
        var different=await Repo.PreviewAsync(Client,request);Assert.False(different.CanApply);Assert.Contains(different.Issues,i=>i.Message.Contains("later configured basic-pay"));
        await db.ExecuteAsync("UPDATE employees SET AnnualCtc=120000 WHERE Id=9796301");
        await db.ExecuteAsync("UPDATE employees SET SalaryJson=CAST('null' AS JSON) WHERE Id=9796301");
        var unknown=await Repo.PreviewAsync(Client,request);Assert.False(unknown.CanApply);Assert.Contains(unknown.Issues,i=>i.Message.Contains("later configured basic-pay"));
        await db.ExecuteAsync("UPDATE employees SET SalaryJson=JSON_OBJECT() WHERE Id=9796301");
        // A nonempty physical override cannot be authorized by an empty JSON cache.
        await db.ExecuteAsync("INSERT INTO employeesalarycomponents(EmployeeId,ComponentId,Amount) VALUES(9796301,9796301,0)");
        await using(var locked=await db.BeginTransactionAsync()){
            var planned=new Employee{Id=One,ClientId=Client,EmployeeCode="IB-ONE",FirstName="Synthetic",AnnualCtc=120000,SalaryStructureId="planned",SalaryJson="{\"9796301\":10000}",SalaryComponents=new(){{"9796301",10000}}};
            var blocked=await Assert.ThrowsAsync<InvalidOperationException>(()=>EmployeeRepository.SaveExcelPayrollSetupEmployeeAsync(db,locked,planned,new(2026,10,1),"Synthetic reviewer","Synthetic source",reviewedInitialBudgetAnnual:120000));
            Assert.Contains("unconfigured current Master",blocked.Message);await locked.RollbackAsync();}
        await db.ExecuteAsync("DELETE FROM employeesalarycomponents WHERE EmployeeId=9796301");
        var ready=await Repo.PreviewAsync(Client,request);Assert.True(ready.CanApply,string.Join(";",ready.Issues.Select(i=>i.Message)));
        var command=new ExcelPayrollSetupApplyRequest{Request=request,PlanHash=ready.PlanHash,RequestId=Guid.NewGuid().ToString("N")};
        var result=await Repo.ApplyAsync(Client,command,"Synthetic reviewer",Client);
        Assert.Equal(1,result.UpdatedCount);Assert.Equal(0,result.CreatedCount);Assert.Single(result.EmployeeIds);Assert.Single(result.TemplateIds);
        Assert.Equal(1,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM salarystructures WHERE ClientId=97963"));
        Assert.Equal("Applied",await db.ExecuteScalarAsync<string>("SELECT status FROM excel_payroll_setup_imports WHERE client_id=97963"));
        Assert.Equal(0,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employee_monthly_attendance WHERE client_id=97963"));
        Assert.Equal(0,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employee_daily_attendance WHERE client_id=97963"));
        Assert.Equal(preserved,await PreservedAsync(db));
        var retained=await db.QuerySingleAsync<History>("SELECT * FROM employee_it0008_basic_pay WHERE Id=@Id",new{original.Id});
        Assert.Equal(original.AnnualCtc,retained.AnnualCtc);Assert.Equal(original.SalaryStructureId,retained.SalaryStructureId);Assert.Equal(original.SalaryJson,retained.SalaryJson);
        Assert.Equal(original.EffectiveFrom,retained.EffectiveFrom);Assert.Equal(original.ChangeReason,retained.ChangeReason);Assert.Equal(original.CreatedBy,retained.CreatedBy);Assert.Equal(original.CreatedAt,retained.CreatedAt);
        Assert.Equal("Historical",retained.Status);Assert.Equal(original.EffectiveFrom,retained.EffectiveTo);
        var histories=(await db.QueryAsync<History>("SELECT * FROM employee_it0008_basic_pay WHERE EmployeeId=9796301 ORDER BY EffectiveFrom,Id")).ToArray();
        Assert.Equal(3,histories.Length);Assert.All(histories,h=>Assert.Equal(120000,h.AnnualCtc));
        Assert.Contains(histories,h=>h.Id!=original.Id&&h.EffectiveFrom==new DateTime(2026,10,1)&&h.SalaryStructureId==result.TemplateIds[0]);
        Assert.Contains(histories,h=>h.Id!=original.Id&&h.EffectiveFrom==original.EffectiveFrom&&h.SalaryStructureId==result.TemplateIds[0]);
        var replay=await Repo.ApplyAsync(Client,command,"Synthetic reviewer",Client);Assert.True(replay.Replayed);
        Assert.Equal(3,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employee_it0008_basic_pay WHERE EmployeeId=9796301"));
        // Independent, known synthetic native attendance is supplied AFTER the
        // configuration-only assertions. Source payable was never called present.
        await db.ExecuteAsync("INSERT INTO employee_monthly_attendance(client_id,employee_id,attendance_month,working_days,present_days,payable_days,source_type,remarks) VALUES(97963,9796301,'2026-10',31,31,31,'Manual','Independent known synthetic native attendance')");
        var native=await F.Runs.SimulateEmployeeAsync(new(){Mode="Employee",ClientId=Client,EmployeeId=One,PayPeriod="2026-10"},default);
        Assert.True(native.Complete,string.Join(";",native.Issues.Select(i=>i.Message)));Assert.Equal(10000,native.Gross);Assert.Equal(1000,native.Deductions);Assert.Equal(500,native.Benefits);Assert.Equal(9000,native.Net);
        Assert.Equal(120000,await db.ExecuteScalarAsync<decimal>("SELECT AnnualCtc FROM employees WHERE Id=9796301"));
        Assert.Equal(0,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM payruns WHERE ClientId=97963"));
    }
    private sealed class History{public long Id{get;set;}public DateTime EffectiveFrom{get;set;}public DateTime? EffectiveTo{get;set;}public string Status{get;set;}="";public string SalaryStructureId{get;set;}="";public decimal AnnualCtc{get;set;}public string SalaryJson{get;set;}="";public string ChangeReason{get;set;}="";public string CreatedBy{get;set;}="";public DateTime CreatedAt{get;set;}}
}
