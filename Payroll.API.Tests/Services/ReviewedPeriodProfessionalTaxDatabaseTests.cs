using System.Text.Json;
using Dapper;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;
using Payroll.API.Tests.Fixtures;

namespace Payroll.API.Tests.Services;

[CollectionDefinition("ReviewedPayroll",DisableParallelization=true)]
public sealed class ReviewedPayrollCollection { }

[Collection("ReviewedPayroll")]
public sealed class ReviewedPeriodProfessionalTaxDatabaseTests : IAsyncLifetime
{
    private const int Client=97711, Employee=9771101, OtherEmployee=9771102;
    private const string Batch="97711000000000000000000000000001";
    private readonly ReviewedPayrollFixture? fixture = Environment.GetEnvironmentVariable("FREVO_REVIEWED_PAYROLL_TEST_DB") is string connection ? new(connection) : null;
    private ReviewedPayrollFixture F => fixture ?? throw new InvalidOperationException("Explicit guarded synthetic database required.");
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private static AuthUser Actor => new() { Id=97711,Email="synthetic-pt-reviewer@example.test",IsActive=true,
        Roles=["super_admin"],Permissions=["payroll.run","employees.manage","settings.manage"] };
    private sealed class IsolatedFactAttribute : FactAttribute
    { public IsolatedFactAttribute(){if(Environment.GetEnvironmentVariable("FREVO_REVIEWED_PAYROLL_TEST_DB") is null)Skip="Explicit sentinel-guarded synthetic payroll database required.";} }
    private sealed class IsolatedTheoryAttribute : TheoryAttribute
    { public IsolatedTheoryAttribute(){if(Environment.GetEnvironmentVariable("FREVO_REVIEWED_PAYROLL_TEST_DB") is null)Skip="Explicit sentinel-guarded synthetic payroll database required.";} }

    public async Task InitializeAsync()
    {
        if(fixture is null)return;
        await F.InitializeAsync(); await CleanAsync();
        await using var db=await F.OpenAsync();
        await db.ExecuteAsync(@"
INSERT INTO clients(Id,Code,Name,IsActive) VALUES(97711,'SYNTH-PT','Synthetic reviewed PT',TRUE);
INSERT INTO worklocations(Id,ClientId,Name,City,State) VALUES(97711,97711,'Synthetic PT office','Synthetic city','PT synthetic state 97711');
INSERT INTO salarycomponents(Id,Code,Name,Category,CalculationType,ValueText,Formula,StatutoryType,ProRata,Priority) VALUES
(9771101,'PT_SYNTH_BASIC_97711','Synthetic basic','Earning','Fixed Amount','30000','','None',TRUE,100),
(9771102,'PT_SYNTH_DEDUCTION_97711','Synthetic PT','Deduction','Fixed Amount','0','','Professional Tax',FALSE,200);
INSERT INTO salarystructures(Id,ClientId,Name,AnnualCtc,Active) VALUES(9771100,97711,'Synthetic PT template',360000,TRUE);
INSERT INTO salarystructurelines(StructureId,ComponentId,ValueText,CalculationType,ProRataOverride,SortOrder) VALUES(9771100,'9771101','30000','Fixed Amount','true',1),(9771100,'9771102','0','Fixed Amount','false',2);
INSERT INTO professionaltaxslabs(State,SalaryFrom,DeductionAmount,Active) VALUES('PT synthetic state 97711',0,200,TRUE);
INSERT INTO tax_client_settings(client_id,financial_year,enabled,project_monthly_tds,active) VALUES(97711,'2026-27',FALSE,FALSE,TRUE);
INSERT INTO employees(Id,ClientId,EmployeeCode,FirstName,LastName,DateOfJoining,AnnualCtc,SalaryStructureId,SalaryJson,PersonalJson,PaymentJson,WorkLocationId) VALUES
(9771101,97711,'PT-SYN-A','Synthetic','One','2026-01-01',360000,'9771100',JSON_OBJECT('9771101',30000),JSON_OBJECT(),JSON_OBJECT(),97711),
(9771102,97711,'PT-SYN-B','Synthetic','Two','2026-01-01',360000,'9771100',JSON_OBJECT('9771101',30000),JSON_OBJECT(),JSON_OBJECT(),97711);
INSERT INTO employeepersonaldetails(EmployeeId,UanNumber) VALUES(9771101,'101234567891'),(9771102,'101234567892');
INSERT INTO employeesalarycomponents(EmployeeId,ComponentId,Amount) VALUES(9771101,'9771101',30000),(9771102,'9771101',30000);
INSERT INTO employee_monthly_attendance(client_id,employee_id,attendance_month,working_days,present_days,payable_days,source_type,remarks) VALUES
(97711,9771101,'2026-10',30,30,30,'Synthetic test',''),(97711,9771102,'2026-10',30,30,30,'Synthetic test',''),(97711,9771101,'2026-11',30,30,30,'Synthetic test','');");
        var rows=new List<ExcelPayslipRow>();var sourceRows=new List<ExcelPayslipCalculationSourceRow>();
        for(var index=0;index<2;index++)
        {
            var code=index==0?"PT-SYN-A":"PT-SYN-B";var name=index==0?"Synthetic One":"Synthetic Two";var uan=index==0?"101234567891":"101234567892";
            rows.Add(new(){Id="row-"+(index+2),SourceRow=index+2,EmployeeCode=code,EmployeeName=name,Information=[new(){Label="UAN",Value=uan}],Earnings=[new(){Label="Basic",Amount=30000}],DeclaredGross=30000,DeclaredDeductions=0,NetPay=30000});
            sourceRows.Add(new(){SourceRow=index+2,Cells=[new(){Value=code},new(){Value=name},new(){Value=uan},new(){Value="30000"},new(){Value="30000",FormulaText="D"+(index+2)}]});
        }
        var batch=new ExcelPayslipBatch{Id=Batch,ClientId=Client,Month="2026-10",ClientName="Synthetic reviewed PT",SourceFileName="Synthetic PT.xlsx",SheetName="Salary",HeaderRow=1,Rows=rows,CreatedBy="isolated test",CreatedAtUtc=new(2026,11,14)};
        var source=new ExcelPayslipCalculationSource{SheetName="Salary",HeaderRow=1,ColumnCount=5,Rows=sourceRows,
            Columns=[new(){ColumnIndex=0,Label="Employee code",SourceHeader="Employee code",Kind="code"},new(){ColumnIndex=1,Label="Employee name",SourceHeader="Employee name",Kind="name"},new(){ColumnIndex=2,Label="UAN",SourceHeader="UAN",Kind="info"},new(){ColumnIndex=3,Label="Basic",SourceHeader="Basic",Kind="earning"},new(){ColumnIndex=4,Label="Net",SourceHeader="Net",Kind="net"}]};
        await db.ExecuteAsync("INSERT INTO excel_payslip_batches(client_id,id,batch_json,send_state_json,created_at) VALUES(@Client,@Batch,CAST(@Json AS JSON),JSON_OBJECT(),UTC_TIMESTAMP());INSERT INTO excel_payslip_calculation_sources(client_id,batch_id,source_json,created_at) VALUES(@Client,@Batch,CAST(@Source AS JSON),UTC_TIMESTAMP())",new{Client,Batch,Json=JsonSerializer.Serialize(batch,Json),Source=JsonSerializer.Serialize(source,Json)});
    }
    public async Task DisposeAsync(){if(fixture is not null)await CleanAsync();}
    private async Task CleanAsync()
    {
        await using var db=await F.OpenAsync();
        await db.ExecuteAsync(@"
DELETE FROM modulesettings WHERE client_id=97711;
DELETE FROM tax_client_settings WHERE client_id=97711;
DELETE p FROM payrunemployees p JOIN payruns r ON r.Id=p.PayRunId WHERE r.ClientId=97711;
DELETE FROM payruns WHERE ClientId=97711;DELETE FROM payrolladjustments WHERE ClientId=97711;
DELETE FROM employee_monthly_attendance WHERE client_id=97711;
DELETE FROM excel_payslip_batches WHERE client_id=97711;DELETE FROM excel_payslip_calculation_sources WHERE client_id=97711;
DELETE FROM employee_it0008_basic_pay WHERE ClientId=97711;DELETE FROM employee_it0009_bank_details WHERE ClientId=97711;DELETE FROM employee_it0000_actions WHERE ClientId=97711;
DELETE FROM employeesalarycomponents WHERE EmployeeId IN (9771101,9771102);DELETE FROM employeepersonaldetails WHERE EmployeeId IN (9771101,9771102);DELETE FROM employeepaymentdetails WHERE EmployeeId IN (9771101,9771102);
DELETE FROM employees WHERE ClientId=97711;DELETE FROM worklocations WHERE ClientId=97711;DELETE FROM clients WHERE Id=97711;
DELETE FROM salarystructurelines WHERE StructureId=9771100;DELETE FROM salarystructures WHERE Id=9771100;DELETE FROM salarycomponents WHERE Id IN (9771101,9771102);
DELETE FROM professionaltaxslabs WHERE State='PT synthetic state 97711';");
    }
    private static ReviewedProfessionalTaxRequest Request(int employee=Employee,decimal? amount=0,string mode="ReviewedAmount")=>new()
    {ClientId=Client,PayPeriod="2026-10",SourceBatchId=Batch,Reason="Explicitly reviewed synthetic source-period PT",Rows=[new(){EmployeeId=employee,SourceRowId=employee==Employee?"row-2":"row-3",Mode=mode,Amount=amount}]};
    private async Task<ReviewedProfessionalTaxSaveResult> SaveAsync(ReviewedProfessionalTaxPreview preview,string? requestId=null)=>
        await F.Runs.SaveReviewedProfessionalTaxAsync(new(preview.Request,preview.PlanHash,requestId??Guid.NewGuid().ToString("N"),true),Actor);
    private async Task<PayrollSimulationResult> SimulateAsync(int employee=Employee,string period="2026-10")=>await F.Runs.SimulateEmployeeAsync(new(){Mode="Employee",ClientId=Client,EmployeeId=employee,PayPeriod=period},default);
    private async Task<string> SourceWitnessAsync()
    {
        await using var db=await F.OpenAsync();return await db.ExecuteScalarAsync<string>("SELECT CONCAT(SHA2(CAST(b.batch_json AS CHAR),256),SHA2(CAST(s.source_json AS CHAR),256)) FROM excel_payslip_batches b JOIN excel_payslip_calculation_sources s ON s.client_id=b.client_id AND s.batch_id=b.id WHERE b.client_id=@Client AND b.id=@Batch",new{Client,Batch})??"";
    }

    [IsolatedFact]
    public async Task Reviewed_zero_changes_only_exact_employee_period_and_replays_without_source_salary_mutation()
    {
        var before=await SourceWitnessAsync();var baseline=await SimulateAsync();Assert.True(baseline.Complete);Assert.Equal(200,baseline.Deductions);
        var preview=await F.Runs.PreviewReviewedProfessionalTaxAsync(Request(),Actor);
        Assert.True(preview.CanSave,string.Join(";",preview.Blockers.Concat(preview.Rows.SelectMany(row=>row.Blockers))));
        await using(var db=await F.OpenAsync())Assert.Equal(0,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM modulesettings WHERE client_id=97711"));
        var id=Guid.NewGuid().ToString("N");var saved=await SaveAsync(preview,id);Assert.False(saved.Replayed);Assert.Equal(1,saved.SavedEmployees);
        Assert.True((await SaveAsync(preview,id)).Replayed);
        var result=await SimulateAsync();Assert.True(result.Complete,string.Join(";",result.Issues.Select(row=>row.Message)));Assert.Equal(0,result.Deductions);Assert.Equal(30000,result.Net);
        Assert.Contains(result.Lines,line=>line.Category=="Deduction" && line.Earned==0 && line.Source.Contains("Reviewed source-bound"));
        Assert.Equal(200,(await SimulateAsync(OtherEmployee)).Deductions);Assert.Equal(200,(await SimulateAsync(period:"2026-11")).Deductions);
        Assert.Equal(before,await SourceWitnessAsync());
        await using(var db=await F.OpenAsync())
        {
            Assert.Equal(360000,await db.ExecuteScalarAsync<decimal>("SELECT AnnualCtc FROM employees WHERE Id=9771101"));
            Assert.Equal(30000,await db.ExecuteScalarAsync<decimal>("SELECT Amount FROM employeesalarycomponents WHERE EmployeeId=9771101"));
            Assert.Equal(0,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM payruns WHERE ClientId=97711"));
            Assert.Equal(2,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM modulesettings WHERE client_id=97711"));
        }
        var automatic=await F.Runs.PreviewReviewedProfessionalTaxAsync(Request(amount:null,mode:"Automatic"),Actor);Assert.True(automatic.CanSave);
        await SaveAsync(automatic);Assert.Equal(200,(await SimulateAsync()).Deductions);
        await using(var db=await F.OpenAsync())Assert.Equal(2,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM modulesettings WHERE client_id=97711 AND ModuleCode LIKE 'payroll.pt.receipt:%'"));
    }

    [IsolatedTheory]
    [InlineData("source")][InlineData("employee")][InlineData("template")][InlineData("seeded")][InlineData("payroll")]
    public async Task Stale_preflight_or_conflicting_native_input_cannot_commit_any_control(string change)
    {
        var preview=await F.Runs.PreviewReviewedProfessionalTaxAsync(Request(),Actor);Assert.True(preview.CanSave);
        await using(var db=await F.OpenAsync())await db.ExecuteAsync(change switch
        {
            "source"=>"UPDATE excel_payslip_calculation_sources SET source_json=JSON_SET(source_json,'$.notes','changed') WHERE client_id=97711",
            "employee"=>"UPDATE employees SET AnnualCtc=360012 WHERE Id=9771101",
            "template"=>"UPDATE salarystructurelines SET ValueText='208' WHERE StructureId=9771100 AND ComponentId='9771102'",
            "seeded"=>"UPDATE employeepersonaldetails SET PtLwfWorkmenComp=200 WHERE EmployeeId=9771101",
            _=>"INSERT INTO payruns(ClientId,PayPeriod,Status,RunType) VALUES(97711,'2026-10','AwaitingProcessing','Regular')"
        });
        await Assert.ThrowsAsync<InvalidOperationException>(()=>SaveAsync(preview));
        await using(var db=await F.OpenAsync())Assert.Equal(0,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM modulesettings WHERE client_id=97711"));
    }

    [IsolatedTheory]
    [InlineData("source")][InlineData("template")][InlineData("employee")][InlineData("tamper")][InlineData("disabled")]
    public async Task Saved_control_never_falls_back_after_source_configuration_or_evidence_changes(string change)
    {
        var preview=await F.Runs.PreviewReviewedProfessionalTaxAsync(Request(),Actor);Assert.True(preview.CanSave);await SaveAsync(preview);
        await using(var db=await F.OpenAsync())await db.ExecuteAsync(change switch
        {
            "source"=>"UPDATE excel_payslip_calculation_sources SET source_json=JSON_SET(source_json,'$.notes','changed') WHERE client_id=97711",
            "template"=>"UPDATE salarystructurelines SET ValueText='208' WHERE StructureId=9771100 AND ComponentId='9771102'",
            "employee"=>"UPDATE employees SET AnnualCtc=360012 WHERE Id=9771101",
            "disabled"=>"UPDATE modulesettings SET IsEnabled=FALSE WHERE client_id=97711 AND ModuleCode LIKE 'payroll.pt.reviewed:%'",
            _=>"UPDATE modulesettings SET SettingsJson=JSON_OBJECT('version',1,'kind','PeriodControl','payload','not-authenticated') WHERE client_id=97711 AND ModuleCode LIKE 'payroll.pt.reviewed:%'"
        });
        await Assert.ThrowsAsync<InvalidOperationException>(()=>SimulateAsync());
    }

    [IsolatedFact]
    public async Task Explicit_positive_review_is_not_prorated_and_other_state_rules_do_not_stale_it()
    {
        var preview=await F.Runs.PreviewReviewedProfessionalTaxAsync(Request(amount:175.25m),Actor);Assert.True(preview.CanSave);await SaveAsync(preview);
        await using(var db=await F.OpenAsync())
        {
            await db.ExecuteAsync("UPDATE employee_monthly_attendance SET present_days=15,payable_days=15 WHERE client_id=97711 AND employee_id=9771101 AND attendance_month='2026-10'");
            await db.ExecuteAsync("INSERT INTO professionaltaxslabs(State,SalaryFrom,DeductionAmount,Active) VALUES('PT unrelated state 97711',0,250,TRUE)");
        }
        try
        {
            var result=await SimulateAsync();Assert.True(result.Complete);Assert.Equal(15000,result.Gross);Assert.Equal(175.25m,result.Deductions);Assert.Equal(14824.75m,result.Net);
        }
        finally { await using var db=await F.OpenAsync();await db.ExecuteAsync("DELETE FROM professionaltaxslabs WHERE State='PT unrelated state 97711'"); }
    }

    [IsolatedFact]
    public async Task Current_Master_projection_reuses_reviewed_control_but_cannot_carry_it_to_a_newer_salary_basis()
    {
        await using(var db=await F.OpenAsync())await db.ExecuteAsync(@"INSERT INTO employee_it0008_basic_pay(EmployeeId,ClientId,ActionType,EffectiveFrom,SalaryStructureId,AnnualCtc,SalaryJson)
VALUES(9771101,97711,'Change','2026-10-01','9771100',360000,JSON_OBJECT('9771101',30000))");
        var original=await SourceWitnessAsync();var preview=await F.Runs.PreviewReviewedProfessionalTaxAsync(Request(),Actor);Assert.True(preview.CanSave);await SaveAsync(preview);
        async Task<ExcelPayslipCurrentSalary> Projection()
        {
            await using var db=await F.OpenAsync();await using var tx=await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,isReadOnly:true);
            var master=await db.QuerySingleAsync<Employee>("SELECT * FROM employees WHERE Id=9771101",transaction:tx);
            await PayrollDataTableStore.ApplyEmployeeTablesAsync(db,[master],tx);
            var batch=JsonSerializer.Deserialize<ExcelPayslipBatch>(await db.ExecuteScalarAsync<string>("SELECT CAST(batch_json AS CHAR) FROM excel_payslip_batches WHERE client_id=97711 AND id=@Batch",new{Batch},tx),Json)!;
            var row=batch.Rows.Single(value=>value.Id=="row-2");row.MasterDetails=new(Employee,""){Linked=true};
            var link=new ExcelPayslipMasterLink(row.Id,Employee,row.SourceRow,"identity","salary",30,30,30,false){SourceBatchId=Batch,SourceHash=preview.SourceHash};
            await F.Runs.RefreshExcelPayslipSalaryAsync(db,tx,batch,await PayrollDataTableStore.GetSetupJsonAsync(db,tx),[(row,link,master)]);
            await tx.RollbackAsync();return row.MasterDetails.Salary!;
        }
        var same=await Projection();Assert.True(same.Status=="Updated",same.Message);Assert.Equal(0,same.TotalDeductions);Assert.Equal(30000,same.NetPay);
        await using(var db=await F.OpenAsync())await db.ExecuteAsync("UPDATE employees SET AnnualCtc=720000 WHERE Id=9771101;UPDATE employeesalarycomponents SET Amount=60000 WHERE EmployeeId=9771101 AND ComponentId='9771101'");
        var native=await SimulateAsync();Assert.True(native.Complete);Assert.Equal(30000,native.Gross);Assert.Equal(0,native.Deductions);
        var changed=await Projection();Assert.Equal("Review required",changed.Status);Assert.Null(changed.NetPay);Assert.Contains("basis changed",changed.Message);
        Assert.Equal(original,await SourceWitnessAsync());
    }

    [IsolatedFact]
    public async Task Wrong_client_or_employee_identity_and_atomic_multirow_conflicts_are_blocked()
    {
        var request=Request();request.ClientId=97712;
        await Assert.ThrowsAsync<InvalidOperationException>(()=>F.Runs.PreviewReviewedProfessionalTaxAsync(request,Actor));
        request=Request();request.Rows[0].SourceRowId="row-3";
        var wrong=await F.Runs.PreviewReviewedProfessionalTaxAsync(request,Actor);Assert.False(wrong.CanSave);
        request=Request();request.Rows.Add(new(){EmployeeId=OtherEmployee,SourceRowId="row-3",Mode="ReviewedAmount",Amount=0});
        var preview=await F.Runs.PreviewReviewedProfessionalTaxAsync(request,Actor);Assert.True(preview.CanSave);
        await using(var db=await F.OpenAsync())await db.ExecuteAsync("UPDATE employeepersonaldetails SET PtLwfWorkmenComp=200 WHERE EmployeeId=9771102");
        await Assert.ThrowsAsync<InvalidOperationException>(()=>SaveAsync(preview));
        await using(var db=await F.OpenAsync())Assert.Equal(0,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM modulesettings WHERE client_id=97711"));
    }
}
