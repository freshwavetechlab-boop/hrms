using System.Text.Json;
using System.Text.Json.Nodes;
using System.Reflection;
using Dapper;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;
using Payroll.API.Tests.Fixtures;

namespace Payroll.API.Tests.Services;

[Collection("ReviewedPayroll")]
public sealed class EmployeeAllScopeLocationUpdateDatabaseTests : IAsyncLifetime
{
    private const int Client=97745,One=9774501,Two=9774502;private const string Batch="97745000000000000000000000000001";private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private readonly ReviewedPayrollFixture? fixture=Environment.GetEnvironmentVariable("FREVO_REVIEWED_PAYROLL_TEST_DB") is string value?new(value):null;
    private ReviewedPayrollFixture F=>fixture??throw new InvalidOperationException("Explicit sentinel-guarded synthetic database required.");
    private ExcelPayslipRepository Repository=>new(F.Config,new NotificationRepository(F.Config,null!,null!,NullLogger<NotificationRepository>.Instance),new(),new PortableIntegrationCredentialProtector(F.Config,new EphemeralDataProtectionProvider(),NullLogger<PortableIntegrationCredentialProtector>.Instance));
    private static AuthUser Actor=>new(){Id=Client,IsActive=true,Email="synthetic-location-reviewer@example.test",ClientAccessMode="All",Permissions=["employees.manage","reports.payroll.view"]};
    private sealed class IsolatedTheoryAttribute:TheoryAttribute{public IsolatedTheoryAttribute(){if(Environment.GetEnvironmentVariable("FREVO_REVIEWED_PAYROLL_TEST_DB") is null)Skip="Explicit sentinel-guarded synthetic database required.";}}
    public async Task InitializeAsync()
    {
        if(fixture is null)return;await F.InitializeAsync();await using var db=await F.OpenAsync();
        await db.ExecuteAsync(@"CREATE TABLE IF NOT EXISTS auditlogs(Id BIGINT PRIMARY KEY AUTO_INCREMENT,UserId INT NULL,UserEmail VARCHAR(190),Action VARCHAR(120) NOT NULL,Resource VARCHAR(190),Method VARCHAR(20),Path VARCHAR(500),StatusCode INT NOT NULL DEFAULT 0,IpAddress VARCHAR(80),UserAgent VARCHAR(500),DetailsJson JSON NOT NULL,CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP);");
        await CleanupAsync();
        await db.ExecuteAsync(@"INSERT INTO clients(Id,Code,Name,IsActive) VALUES(97745,'SYNTH-ALL-LOCATION','Synthetic All scope location update',TRUE);
INSERT INTO worklocations(Id,ClientId,Name,City,State,IsActive) VALUES(977451,97745,'Synthetic first office','First city','Synthetic state',TRUE),(977452,97745,'Synthetic second office','Second city','Synthetic state',TRUE),(977453,97745,'Unmatched third office','Third city','Synthetic state',TRUE);
INSERT INTO employees(Id,ClientId,EmployeeCode,FirstName,LastName,Department,Designation,WorkLocationId,AnnualCtc,SalaryJson,PersonalJson,PaymentJson,IsActive) VALUES
(9774501,97745,'SYNTH-ALL-1','Synthetic','First','','Manager',0,120000,JSON_OBJECT('opaqueSalary','retain byte-for-byte'),JSON_OBJECT('opaquePersonal','retain byte-for-byte'),JSON_OBJECT('opaqueBank','retain byte-for-byte'),TRUE),
(9774502,97745,'SYNTH-ALL-2','Synthetic','Second','','Graduate Engineer',0,120000,JSON_OBJECT('opaqueSalary','retain byte-for-byte'),JSON_OBJECT('opaquePersonal','retain byte-for-byte'),JSON_OBJECT('opaqueBank','retain byte-for-byte'),TRUE);
INSERT INTO employeepersonaldetails(EmployeeId,UanNumber,Mobile) VALUES(9774501,'101234577451','9000097741'),(9774502,'101234577452','9000097742');
INSERT INTO employeepaymentdetails(EmployeeId,BankAccountNo,IfscCode) VALUES(9774501,'0012345977451','SYNTH000001'),(9774502,'0012345977452','SYNTH000002');
INSERT INTO employeesalarycomponents(EmployeeId,ComponentId,Amount) VALUES(9774501,'synthetic-retained-input',777.25),(9774502,'synthetic-retained-input',888.50);
INSERT INTO employee_it0008_basic_pay(EmployeeId,ClientId,ActionType,EffectiveFrom,Status,AnnualCtc,SalaryJson,ChangeReason,CreatedBy) VALUES
(9774501,97745,'Existing salary','2026-01-01','Historical',120000,JSON_OBJECT('retained',10000),'Synthetic retained historical salary','synthetic'),
(9774501,97745,'Existing future salary','2027-01-01','Active',240000,JSON_OBJECT('retained',20000),'Synthetic retained future salary','synthetic'),
(9774502,97745,'Existing salary','2026-01-01','Historical',120000,JSON_OBJECT('retained',10000),'Synthetic retained historical salary','synthetic'),
(9774502,97745,'Existing future salary','2027-01-01','Active',240000,JSON_OBJECT('retained',20000),'Synthetic retained future salary','synthetic');
INSERT INTO attendance_groups(id,client_id,name,work_location_id,department,designation,is_active,attendance_mode,work_week,attendance_cycle_start_day,attendance_cycle_end_day) VALUES
(97745,97745,'Existing manual All policy',0,'All','All',TRUE,'Monthly','Sunday off',1,31),
(977451,97745,'Specific department does not match blank',0,'Operations','All',TRUE,'Monthly','Sunday off',1,31),
(977452,97745,'Specific designation does not match',0,'All','Driver',TRUE,'Monthly','Sunday off',1,31),
(977453,97745,'Other location does not match',977453,'All','All',TRUE,'Monthly','Sunday off',1,31),
(977454,97745,'Inactive All policy does not match',0,'All','All',FALSE,'Monthly','Sunday off',1,31);
INSERT INTO attendance_group_employees(attendance_group_id,employee_id) VALUES(97745,9774501),(97745,9774502);
INSERT INTO employee_monthly_attendance(client_id,employee_id,attendance_month,working_days,present_days,payable_days,source_type,remarks) VALUES(97745,9774501,'2026-10',31,26,31,'Monthly','Retained actual attendance'),(97745,9774502,'2026-10',31,26,31,'Monthly','Retained actual attendance');
INSERT INTO payruns(Id,ClientId,PayPeriod,Status,TotalWorkingDays) VALUES(9774500,97745,'2026-10','Draft',31);
INSERT INTO payrunemployees(PayRunId,EmployeeId,ClientId,EmployeeCode,EmployeeName,MonthlyGross,GrossPay,StatutoryDeductions,OneTimeEarnings,OneTimeDeductions,NetPay,PresentDays,PayableDays,DetailsJson) VALUES
(9774500,9774501,97745,'SYNTH-ALL-1','Synthetic First',10000,10000,1000,0,0,9000,26,31,JSON_ARRAY(JSON_OBJECT('Id','RETAINED','Category','Earning','amount',10000))),
(9774500,9774502,97745,'SYNTH-ALL-2','Synthetic Second',10000,10000,1000,0,0,9000,26,31,JSON_ARRAY(JSON_OBJECT('Id','RETAINED','Category','Earning','amount',10000)));
INSERT INTO excel_payslip_batches(client_id,id,batch_json,send_state_json,created_at) VALUES(97745,'97745000000000000000000000000001',JSON_OBJECT('source','original salary source'),JSON_OBJECT('mail','old receipt'),UTC_TIMESTAMP());
INSERT INTO excel_payslip_calculation_sources(client_id,batch_id,source_json,created_at) VALUES(97745,'97745000000000000000000000000001',JSON_OBJECT('formula','original retained formula'),UTC_TIMESTAMP());");
        var batch=new ExcelPayslipBatch{Id=Batch,ClientId=Client,Month="2026-10",Rows=Enumerable.Range(0,2).Select(i=>new ExcelPayslipRow{Id="location-source-"+i,SourceRow=2+i,EmployeeCode="SYNTH-ALL-"+(i+1),EmployeeName=i==0?"Synthetic First":"Synthetic Second",Information=[new(){Label="UAN",Value=i==0?"101234577451":"101234577452"}],Earnings=[new(){Label="Wages",Amount=10000}],Deductions=[new(){Label="PF",Amount=1000}],DeclaredGross=10000,DeclaredDeductions=1000,NetPay=9000}).ToList()};
        var source=new ExcelPayslipCalculationSource{ColumnCount=7,HeaderRow=1,Columns=new[]{("Employee code","employeeCode"),("Employee name","employeeName"),("UAN","info"),("Wages","earning"),("PF","deduction"),("Net pay","netPay"),("Original location","ignore")}.Select((x,i)=>new ExcelPayslipCalculationColumn{ColumnIndex=i,SourceHeader=x.Item1,Label=x.Item1,Kind=x.Item2}).ToList(),Rows=batch.Rows.Select(r=>new ExcelPayslipCalculationSourceRow{SourceRow=r.SourceRow,Cells=new[]{r.EmployeeCode,r.EmployeeName,r.Information[0].Value,"10000","1000","9000","Unchanged original source depot"}.Select((v,i)=>(ExcelPayslipCalculationCell?)new ExcelPayslipCalculationCell{Value=v,FormulaText=i==5?"D"+r.SourceRow+"-E"+r.SourceRow:null}).ToList()}).ToList()};
        batch.ProfileLinks=batch.Rows.Select((r,i)=>new ExcelPayslipProfileLink(r.Id,r.SourceRow,i==0?One:Two,ExcelPayslipMasterSynchronization.ProfileIdentityHash(batch,r,source))).ToList();
        var batchJson=JsonSerializer.SerializeToNode(batch,Json)!.AsObject();batchJson["opaqueSourceMetadata"]="retain Unicode ₹ 名称 and exact formula inputs";var sourceJson=JsonSerializer.SerializeToNode(source,Json)!.AsObject();sourceJson["opaqueSourceMetadata"]="retain original numeric/formula text";
        await db.ExecuteAsync("UPDATE excel_payslip_batches SET batch_json=CAST(@BatchJson AS JSON) WHERE client_id=@Client AND id=@Batch;UPDATE excel_payslip_calculation_sources SET source_json=CAST(@SourceJson AS JSON) WHERE client_id=@Client AND batch_id=@Batch",new{Client,Batch,BatchJson=batchJson.ToJsonString(),SourceJson=sourceJson.ToJsonString()});
    }
    public async Task DisposeAsync(){if(fixture is not null)await CleanupAsync();}
    private async Task CleanupAsync(){await using var db=await F.OpenAsync();await db.ExecuteAsync(@"DELETE FROM auditlogs WHERE UserId=97745 AND Resource='97745000000000000000000000000001';DELETE FROM modulesettings WHERE client_id=97745;DELETE FROM employee_audit_trail WHERE EmployeeId IN(9774501,9774502);DELETE FROM employee_it0001_org_assignment WHERE EmployeeId IN(9774501,9774502);
DELETE FROM employee_it0008_basic_pay WHERE ClientId=97745;DELETE FROM employeesalarycomponents WHERE EmployeeId IN(9774501,9774502);DELETE FROM employeepersonaldetails WHERE EmployeeId IN(9774501,9774502);DELETE FROM employeepaymentdetails WHERE EmployeeId IN(9774501,9774502);
DELETE FROM employee_monthly_attendance WHERE client_id=97745;DELETE FROM attendance_group_employees WHERE attendance_group_id IN(97745,977451,977452,977453,977454);DELETE FROM attendance_groups WHERE client_id=97745;
DELETE FROM payrunemployees WHERE ClientId=97745;DELETE FROM payruns WHERE ClientId=97745;DELETE FROM excel_payslip_calculation_sources WHERE client_id=97745;DELETE FROM excel_payslip_batches WHERE client_id=97745;
DELETE FROM employees WHERE ClientId=97745;DELETE FROM worklocations WHERE ClientId=97745;DELETE FROM clients WHERE Id=97745;");}
    private async Task<string> PreservationAsync(){await using var db=await F.OpenAsync();return JsonSerializer.Serialize(new{
        MasterCaches=await db.QueryAsync("SELECT Id,AnnualCtc,SalaryStructureId,SalaryJson,PersonalJson,PaymentJson,Department,Designation FROM employees WHERE ClientId=97745 ORDER BY Id"),
        Personal=await db.QueryAsync("SELECT * FROM employeepersonaldetails WHERE EmployeeId IN(9774501,9774502) ORDER BY EmployeeId"),Payment=await db.QueryAsync("SELECT * FROM employeepaymentdetails WHERE EmployeeId IN(9774501,9774502) ORDER BY EmployeeId"),
        Inputs=await db.QueryAsync("SELECT * FROM employeesalarycomponents WHERE EmployeeId IN(9774501,9774502) ORDER BY EmployeeId,ComponentId"),SalaryHistory=await db.QueryAsync("SELECT * FROM employee_it0008_basic_pay WHERE ClientId=97745 ORDER BY Id"),
        Attendance=await db.QueryAsync("SELECT * FROM employee_monthly_attendance WHERE client_id=97745 ORDER BY employee_id"),Membership=await db.QueryAsync("SELECT * FROM attendance_group_employees WHERE employee_id IN(9774501,9774502) ORDER BY employee_id,attendance_group_id"),
        PolicyRules=await db.QueryAsync("SELECT * FROM attendance_groups WHERE client_id=97745 ORDER BY id"),NativeHeader=await db.QueryAsync("SELECT * FROM payruns WHERE ClientId=97745 ORDER BY Id"),NativeRows=await db.QueryAsync("SELECT * FROM payrunemployees WHERE ClientId=97745 ORDER BY Id"),
        OriginalSourceAndMail=await db.QueryAsync("SELECT CAST(batch_json AS CHAR) Batch,CAST(send_state_json AS CHAR) Mail FROM excel_payslip_batches WHERE client_id=97745"),OriginalFormulas=await db.QueryAsync("SELECT CAST(source_json AS CHAR) Source FROM excel_payslip_calculation_sources WHERE client_id=97745")},Json);}

    [IsolatedTheory][InlineData(One,977451,"Synthetic first office")][InlineData(Two,977452,"Synthetic second office")]
    public async Task Reviewed_location_only_update_keeps_manual_All_policy_for_blank_department_and_preserves_salary_source_and_history(int employeeId,int locationId,string location)
    {
        var before=await PreservationAsync();var repository=Repository;await using(var db=await F.OpenAsync())await using(var tx=await db.BeginTransactionAsync()){
            // The real Apply locks are retained throughout the reused projection.
            await db.QueryAsync<string>("SELECT id FROM excel_payslip_batches WHERE client_id=@Client AND id=@Batch FOR UPDATE",new{Client,Batch},tx);
            await db.QueryAsync<string>("SELECT batch_id FROM excel_payslip_calculation_sources WHERE client_id=@Client AND batch_id=@Batch FOR UPDATE",new{Client,Batch},tx);
            var plain=await db.QuerySingleAsync<Plain>("SELECT CAST(b.batch_json AS CHAR) BatchJson,CAST(s.source_json AS CHAR) SourceJson,CAST(b.send_state_json AS CHAR) SendJson FROM excel_payslip_batches b JOIN excel_payslip_calculation_sources s ON s.client_id=b.client_id AND s.batch_id=b.id WHERE b.client_id=@Client AND b.id=@Batch",new{Client,Batch},tx);
            var load=typeof(ExcelPayslipRepository).GetMethod("LoadUpdateContextAsync",BindingFlags.Instance|BindingFlags.NonPublic)!;var loading=(Task)load.Invoke(repository,[db,tx,Client,Batch])!;await loading;var context=loading.GetType().GetProperty("Result")!.GetValue(loading)!;object Field(string name)=>context.GetType().GetField(name)!.GetValue(context)!;
            Assert.Equal(plain.BatchJson,Field("BatchJson"));Assert.Equal(plain.SourceJson,Field("SourceJson"));Assert.Equal(plain.SendJson,Field("SendJson"));
            var raw=(ExcelPayslipBatch)Field("Raw");var source=(ExcelPayslipCalculationSource)Field("Source");Assert.Equal(ExcelPayslipRepository.EmploymentSourceHash(JsonSerializer.Deserialize<ExcelPayslipBatch>(plain.BatchJson,Json)!,JsonSerializer.Deserialize<ExcelPayslipCalculationSource>(plain.SourceJson,Json)!),ExcelPayslipRepository.EmploymentSourceHash(raw,source));
            var read=typeof(ExcelPayslipRepository).GetMethod("ReadCorrectionBatchAsync",BindingFlags.Instance|BindingFlags.NonPublic)!;var oldRead=(Task)read.Invoke(repository,[db,Client,Batch,tx,null])!;await oldRead;var legacy=oldRead.GetType().GetProperty("Result")!.GetValue(oldRead)!;Assert.Equal(JsonSerializer.Serialize(legacy,Json),JsonSerializer.Serialize(Field("Projection"),Json));
            var snapshotType=typeof(ExcelPayslipRepository).GetNestedType("CorrectionReadSnapshot",BindingFlags.NonPublic)!;var foreign=Activator.CreateInstance(snapshotType,[db,tx,Client+1,Batch,raw,source])!;var wrong=(Task)read.Invoke(repository,[db,Client,Batch,tx,foreign])!;await Assert.ThrowsAsync<InvalidOperationException>(async()=>await wrong);
            await using var other=await F.OpenAsync();await using var otherTx=await other.BeginTransactionAsync();foreach(var sql in new[]{"SELECT id FROM excel_payslip_batches WHERE client_id=@Client AND id=@Batch FOR UPDATE NOWAIT","SELECT batch_id FROM excel_payslip_calculation_sources WHERE client_id=@Client AND batch_id=@Batch FOR UPDATE NOWAIT"}){var blocked=await Assert.ThrowsAsync<MySqlConnector.MySqlException>(()=>other.QuerySingleAsync<string>(sql,new{Client,Batch},otherTx));Assert.Equal(3572,blocked.Number);}await otherTx.RollbackAsync();await tx.RollbackAsync();}
        var catalog=await repository.GetBulkUpdateCatalogAsync(Client,Batch,Actor);Assert.Empty(catalog.Blockers);Assert.Equal(2,catalog.Rows.Count);var field=Assert.Single(catalog.Fields,f=>f.MasterField=="workLocation"&&f.Editable);Assert.Equal("Master only",field.Destination);
        var code=employeeId==One?"SYNTH-ALL-1":"SYNTH-ALL-2";var preview=await repository.PreviewBulkUpdateAsync(Client,Batch,new(){Month="2026-10",SourceFileName="Synthetic reviewed location fill.xlsx",Rows=[new(){EmployeeCode=code,Values=new(){{field.Id,location}}}]},Actor);Assert.Empty(preview.Blockers);var change=Assert.Single(preview.Changes);Assert.True(change.CanApply,change.Reason);Assert.Equal("Master only",change.Destination);
        var command=new ExcelPayslipUpdateApplyRequest{Token=preview.Token,RequestId=Guid.NewGuid().ToString("N"),Acknowledged=true,SelectedChangeIds=[change.Id]};var saved=await repository.ApplyBulkUpdateAsync(Client,Batch,command,Actor);Assert.Equal(1,saved.UpdatedEmployees);Assert.Equal(1,saved.AppliedChanges);Assert.True((await repository.ApplyBulkUpdateAsync(Client,Batch,command,Actor)).Replayed);
        Assert.Equal(before,await PreservationAsync());await using var check=await F.OpenAsync();Assert.Equal(locationId,await check.ExecuteScalarAsync<int>("SELECT WorkLocationId FROM employees WHERE Id=@employeeId AND ClientId=@Client",new{employeeId,Client}));
        Assert.Equal(0,await check.ExecuteScalarAsync<int>("SELECT WorkLocationId FROM employees WHERE ClientId=@Client AND Id<>@employeeId",new{employeeId,Client}));
        Assert.Equal(new[]{97745},(await check.QueryAsync<int>("SELECT attendance_group_id FROM attendance_group_employees WHERE employee_id=@employeeId ORDER BY attendance_group_id",new{employeeId})).ToArray());
        Assert.Equal(1,await check.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employee_it0001_org_assignment WHERE EmployeeId=@employeeId AND Status='Active' AND WorkLocationId=@locationId",new{employeeId,locationId}));
        Assert.Equal(1,await check.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employee_audit_trail WHERE EmployeeId=@employeeId AND InfotypeCode='0001' AND FieldName='Excel update: workLocation'",new{employeeId}));
        Assert.Equal(0,await check.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM excel_payroll_setup_imports WHERE client_id=@Client",new{Client}));
    }
    private sealed class Plain{public string BatchJson{get;set;}="";public string SourceJson{get;set;}="";public string SendJson{get;set;}="";}
}
