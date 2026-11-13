using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Fixtures;

public sealed class EssPayslipFlowFixture
{
    public const string BatchId="eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
    public const int ClientId=907,EmployeeId=107;
    public static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    public IConfiguration Config {get;}
    public string Connection {get;}
    public EssMssRepository Ess {get;}
    public ExcelPayslipRepository Slips {get;}
    public string OriginalBatchHash {get;private set;}="";
    public string OriginalSourceHash {get;private set;}="";
    public EssPayslipFlowFixture(string connection)
    {
        var target=new MySqlConnectionStringBuilder(connection);
        if(target.Server!="127.0.0.1"||target.Port!=33330||target.Database!="attendance_location_test")throw new InvalidOperationException("Explicit isolated ESS/payslip test database required.");
        Connection=connection;Config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["ConnectionStrings:Default"]=connection}).Build();
        Ess=new(Config);Slips=new(Config,null!,new ExcelPayslipPdfService());
    }
    public async Task ResetAsync(string schemaFolder)
    {
        await using var db=new MySqlConnection(Connection);await db.OpenAsync();
        await db.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(schemaFolder,"profile-schema.sql")));
        await db.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(schemaFolder,"payslip-schema.sql")));
        foreach(var (table,column,type) in new[]{("clients","Code","VARCHAR(60) NOT NULL DEFAULT ''"),("worklocations","ClientId","INT NOT NULL DEFAULT 0"),("worklocations","City","VARCHAR(100) NOT NULL DEFAULT ''")})
            if(!await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name=@table AND column_name=@column)",new{table,column}))
                await db.ExecuteAsync($"ALTER TABLE {table} ADD COLUMN {column} {type}");
        await Ess.GetProfileAsync(-1,ClientId);
        await db.ExecuteAsync("DELETE FROM ess_profile_update_audit WHERE EmployeeId=107;DELETE FROM ResourceStates WHERE ResourceType='EmployeeProfileEdit' AND ResourceId='107';DELETE FROM ResourceStates WHERE ResourceType='EmployeeProfileFirstEdit' AND ResourceId='907';DELETE FROM employee_it0002_personal_data WHERE EmployeeId=107;DELETE FROM employee_it0006_addresses WHERE EmployeeId=107;DELETE FROM employee_it0009_bank_details WHERE EmployeeId=107;DELETE FROM employeepersonaldetails WHERE EmployeeId=107;DELETE FROM employeepaymentdetails WHERE EmployeeId=107;DELETE FROM employeesalarycomponents WHERE EmployeeId=107;DELETE FROM employee_it0000_actions WHERE EmployeeId=107;DELETE FROM employees WHERE Id=107;DELETE FROM authusers WHERE Id=9907;DELETE FROM worklocations WHERE Id=907;DELETE FROM clients WHERE Id=907;DELETE FROM excel_payslip_batches WHERE client_id=907;DELETE FROM excel_payslip_calculation_sources WHERE client_id=907");
        await db.ExecuteAsync("INSERT INTO clients(Id,Code,Name,IsActive) VALUES(907,'PROFILE','Synthetic ESS Profile',TRUE);INSERT INTO worklocations(Id,ClientId,Name,City) VALUES(907,907,'Synthetic period office','Synthetic city');INSERT INTO employees(Id,ClientId,EmployeeCode,FirstName,LastName,AnnualCtc,SalaryStructureId,DateOfJoining,SalaryJson,WorkEmail,Designation,Department,WorkLocationId) VALUES(107,907,'PROFILE-107','Synthetic','Employee',480000,'Synthetic template','2026-01-02','{}','source@example.test','Analyst','Operations',907);INSERT INTO authusers(Id,ClientId,EmployeeId,IsActive) VALUES(9907,907,107,TRUE);INSERT INTO employeepersonaldetails(EmployeeId,PanNumber,Mobile) VALUES(107,'ABCDE1234F','9000000107');INSERT INTO employeepaymentdetails(EmployeeId,BankName,BankAccountNo,IfscCode,PaymentMode) VALUES(107,'Synthetic Bank','0012345678901','ABCD0000123','Bank Transfer')");
        var source=new ExcelPayslipCalculationSource{SheetName="Salary",HeaderRow=1,ColumnCount=6,Columns=[new(){ColumnIndex=0,Label="Employee code",SourceHeader="Employee code",Kind="code"},new(){ColumnIndex=1,Label="Employee name",SourceHeader="Employee name",Kind="name"},new(){ColumnIndex=2,Label="PAN",SourceHeader="PAN",Kind="info"},new(){ColumnIndex=3,Label="UAN",SourceHeader="UAN",Kind="info"},new(){ColumnIndex=4,Label="Basic",SourceHeader="Basic",Kind="earning"},new(){ColumnIndex=5,Label="Net",SourceHeader="Net",Kind="net"}],Rows=[new(){SourceRow=2,Cells=[new(){Value="PROFILE-107"},new(){Value="Synthetic Employee"},new(){Value="ABCDE1234F"},new(){Value=""},new(){Value="1244.5678",FormulaText="1000+244.5678"},new(){Value="1234.5678",FormulaText="E2-10"}]}]};
        var row=new ExcelPayslipRow{Id="e2e-row",SourceRow=2,EmployeeCode="PROFILE-107",EmployeeName="Synthetic Employee",Email="source@example.test",Information=[new(){Label="PAN",Value="ABCDE1234F"},new(){Label="UAN",Value=""},new(){Label="Mobile No",Value="9000000107"},new(){Label="Account No",Value="0012345678901"},new(){Label="IFSC Code",Value="ABCD0000123"},new(){Label="Work Location",Value="Synthetic period office"}],Earnings=[new(){Label="Basic",Amount=1244.5678m}],Deductions=[new(){Label="PF",Amount=10}],EmployerContributions=[new(){Label="Employer PF",Amount=12.3456m}],NetPay=1234.5678m,DeclaredGross=1244.5678m,DeclaredDeductions=10};
        var batch=new ExcelPayslipBatch{Id=BatchId,ClientId=ClientId,ClientName="Synthetic ESS Profile",Month="2026-10",SourceFileName="Synthetic salary.xlsx",SheetName="Salary",HeaderRow=1,Rows=[row],CreatedAtUtc=new DateTime(2026,11,1,0,0,0,DateTimeKind.Utc),CreatedBy="isolated-test"};
        batch.ProfileLinks=[new(row.Id,row.SourceRow,EmployeeId,ExcelPayslipMasterSynchronization.ProfileIdentityHash(batch,row,source))];
        await db.ExecuteAsync("INSERT INTO excel_payslip_batches(client_id,id,batch_json,send_state_json,created_at) VALUES(907,@Id,CAST(@Batch AS JSON),JSON_OBJECT(),UTC_TIMESTAMP());INSERT INTO excel_payslip_calculation_sources(client_id,batch_id,source_json,created_at) VALUES(907,@Id,CAST(@Source AS JSON),UTC_TIMESTAMP())",new{Id=BatchId,Batch=JsonSerializer.Serialize(batch,Json),Source=JsonSerializer.Serialize(source,Json)});
        OriginalBatchHash=await db.ExecuteScalarAsync<string>("SELECT SHA2(CAST(batch_json AS CHAR),256) FROM excel_payslip_batches WHERE client_id=907 AND id=@Id",new{Id=BatchId});
        OriginalSourceHash=await db.ExecuteScalarAsync<string>("SELECT SHA2(CAST(source_json AS CHAR),256) FROM excel_payslip_calculation_sources WHERE client_id=907 AND batch_id=@Id",new{Id=BatchId});
    }
    public async Task<object> StateAsync()
    {
        await using var db=new MySqlConnection(Connection);await db.OpenAsync();
        var profile=(await Ess.GetProfileAsync(EmployeeId,ClientId))!;var batch=(await Slips.GetAsync(ClientId,BatchId))!;var row=ExcelPayslipPresentation.Current(batch.Rows.Single());
        return new{isolated=true,employeeCode=profile.EmployeeCode,masterUan=profile.UanNumber,payslipUan=row.Information.Single(f=>f.Label=="UAN").Value,masterName=profile.FirstName+" "+profile.LastName,payslipName=row.EmployeeName,profile.CanEdit,profile.EditStatus,profile.Mobile,netPay=row.NetPay,earnings=row.Earnings.Sum(x=>x.Amount),deductions=row.Deductions.Sum(x=>x.Amount),employer=row.EmployerContributions.Sum(x=>x.Amount),salaryUnchanged=row.NetPay==1234.5678m&&row.Earnings.Single().Amount==1244.5678m&&row.Deductions.Single().Amount==10&&row.EmployerContributions.Single().Amount==12.3456m,batchJsonUnchanged=OriginalBatchHash==await db.ExecuteScalarAsync<string>("SELECT SHA2(CAST(batch_json AS CHAR),256) FROM excel_payslip_batches WHERE client_id=907 AND id=@Id",new{Id=BatchId}),sourceJsonUnchanged=OriginalSourceHash==await db.ExecuteScalarAsync<string>("SELECT SHA2(CAST(source_json AS CHAR),256) FROM excel_payslip_calculation_sources WHERE client_id=907 AND batch_id=@Id",new{Id=BatchId}),saves=await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM ess_profile_update_audit WHERE EmployeeId=107")};
    }
}
