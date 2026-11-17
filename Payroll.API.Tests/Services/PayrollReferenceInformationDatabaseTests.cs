using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;
using Payroll.API.Tests.Fixtures;

namespace Payroll.API.Tests.Services;

[Collection("ReviewedPayroll")]
public sealed partial class PayrollReferenceInformationDatabaseTests:IAsyncLifetime
{
    private const int Client=97790,One=9779001,Two=9779002;
    private const string Batch="97790000000000000000000000000001",OtherBatch="97790000000000000000000000000002",ReceiptId="97790000000000000000000000000003";
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private readonly ReviewedPayrollFixture? fixture=Environment.GetEnvironmentVariable("FREVO_REVIEWED_PAYROLL_TEST_DB") is string value?new(value):null;
    private ReviewedPayrollFixture F=>fixture??throw new InvalidOperationException("The explicitly owned sentinel-guarded fixture is required.");
    private sealed class LocalFactAttribute:FactAttribute{public LocalFactAttribute(){if(Environment.GetEnvironmentVariable("FREVO_REVIEWED_PAYROLL_TEST_DB") is null)Skip="Explicit sentinel-guarded fixture required.";}}
    private sealed class LocalTheoryAttribute:TheoryAttribute{public LocalTheoryAttribute(){if(Environment.GetEnvironmentVariable("FREVO_REVIEWED_PAYROLL_TEST_DB") is null)Skip="Explicit sentinel-guarded fixture required.";}}
    private PortableIntegrationCredentialProtector Protector=>new(F.Config,new EphemeralDataProtectionProvider(),NullLogger<PortableIntegrationCredentialProtector>.Instance);
    private IConfiguration Config(bool enabled=true)=>new ConfigurationBuilder().AddConfiguration(F.Config).AddInMemoryCollection(new Dictionary<string,string?>{["PayrollReferenceInformation:Enabled"]=enabled.ToString()}).Build();
    private PayslipDesignRepository Repo(bool enabled=true)=>new(Config(enabled),Protector,new());
    private static AuthUser Actor=>new(){Id=Client,IsActive=true,Permissions=["settings.manage","reports.payroll.view"],ClientAccessMode="Selected",AssignedClientIds=[Client]};
    private int runId,octoberRunId;private long designId;private string sourceHash="";
    private PayrollReferenceInformationReviewRequest Request=>new(Client,runId,"2026-09",designId,Batch,ReceiptId,3);
    private ExcelPayslipBatch batch=new();private ExcelPayslipCalculationSource source=new();

    public async Task InitializeAsync()
    {
        if(fixture is null)return;await F.InitializeAsync();await CleanAsync();await using var db=await F.OpenAsync();
        // Additive fixture bootstrap only, under the exclusive sentinel-guarded
        // lease. No normal API startup/migration or production connection.
        foreach(var (column,definition)in new[]{("ManualTds","DECIMAL(18,2) NOT NULL DEFAULT 0"),("PaymentStatus","VARCHAR(30) NOT NULL DEFAULT 'Pending'"),("PaymentDate","DATE NULL")})
            if(!await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='payrunemployees' AND column_name=@column)",new{column}))
                await db.ExecuteAsync($"ALTER TABLE payrunemployees ADD COLUMN `{column}` {definition}");
        await db.ExecuteAsync("""
CREATE TABLE IF NOT EXISTS workflow_action_rules(Id BIGINT PRIMARY KEY AUTO_INCREMENT,ActivityCode VARCHAR(120) NOT NULL,HttpMethod VARCHAR(12) NOT NULL,PathPattern VARCHAR(300) NOT NULL,ResourceType VARCHAR(100) NOT NULL,IsActive BOOLEAN NOT NULL DEFAULT TRUE);
INSERT INTO clients(Id,Code,Name,IsActive) VALUES(97790,'SYN-REFERENCE','Synthetic reference client',TRUE);
INSERT INTO employees(Id,ClientId,EmployeeCode,FirstName,LastName,AnnualCtc,SalaryStructureId) VALUES
(9779001,97790,'SYN-REF-1','Synthetic','One',999999,'97790'),(9779002,97790,'SYN-REF-2','Synthetic','Two',999999,'97790');
INSERT INTO salarycomponents(Id,Code,Name,Category,Active) VALUES(9779001,'REFERENCE_WAGE_97790','Synthetic reference salary','Earning',TRUE);
""");
        batch=new(){Id=Batch,ClientId=Client,ClientName="Synthetic reference client",Month="2026-09",SourceFileName="Synthetic reference September.xlsx",Rows=new[]{One,Two}.Select((id,index)=>new ExcelPayslipRow
        {Id=$"synthetic-ref-row-{index+1}",SourceRow=index+2,EmployeeCode=$"SYN-REF-{index+1}",EmployeeName=index==0?"Synthetic One":"Synthetic Two",Information=[new(){Label="Current CTC (source)",Value=index==0?"30000":"25000"}],Earnings=[new(){Label="Salary",Amount=index==0?30000:22500}],NetPay=index==0?30000:22500}).ToList()};
        source=new(){ColumnCount=5,Columns=[new(){ColumnIndex=3,Kind="info",Label="Current CTC (source)"},new(){ColumnIndex=4,Kind="earning",Label="Salary"}],
            Rows=batch.Rows.Select((row,index)=>new ExcelPayslipCalculationSourceRow{SourceRow=row.SourceRow,Cells=[null,null,null,new(){DataType="Number",Value=index==0?"30000":"25000"},new(){DataType="Number",Value=index==0?"30000":"22500"}]}).ToList()};
        sourceHash=PayrollReferenceInformation.Hash(new{Batch=batch,Source=source});
        await StoreSource(db,batch,source);
        var october=JsonSerializer.Deserialize<ExcelPayslipBatch>(JsonSerializer.Serialize(batch,Json),Json)!;october.Id=OtherBatch;october.Month="2026-10";october.SourceFileName="Synthetic reference October.xlsx";
        await StoreSource(db,october,source);
        var design=Repo();var metadata=await design.PreviewAsync(new(){ClientId=Client,SourceBatchId=OtherBatch});
        var saved=await design.SaveAsync(new(){RequestId=Guid.NewGuid().ToString(),Reviewed=true,AmountDecimalPlaces=2,SourceBatchId=OtherBatch,SourceHash=metadata.SourceHash,
            Template=new(){ClientId=Client,Name="Synthetic shared month design"},Fields=metadata.Fields.Select(field=>new PayslipDesignField{SourceColumn=field.SourceColumn,Label=field.Label,Kind=field.Kind,
                InfoToken=field.Kind=="info"?"SAVED_MONTHLY_GROSS":"",NativeCode=field.Kind=="earning"?"REFERENCE_WAGE_97790":""}).ToList()},Actor);designId=saved.Id;
        runId=await InsertRun(db,"2026-09");octoberRunId=await InsertRun(db,"2026-10");
        foreach(var (employee,amount,payable)in new[]{(One,30000m,30m),(Two,22500m,27m)})
        foreach(var id in new[]{runId,octoberRunId})
            await db.ExecuteAsync("INSERT INTO payrunemployees(PayRunId,EmployeeId,ClientId,EmployeeCode,EmployeeName,MonthlyGross,GrossPay,StatutoryDeductions,OneTimeEarnings,OneTimeDeductions,ManualTds,NetPay,PresentDays,PayableDays,DetailsJson,IsSkipped,PaymentStatus) VALUES(@RunId,@Employee,97790,'SYN-REF','Synthetic',@Amount,@Amount,0,0,0,0,@Amount,NULL,@Payable,CAST(@Lines AS JSON),FALSE,'Pending')",
                new{RunId=id,Employee=employee,Amount=amount,Payable=payable,Lines=JsonSerializer.Serialize(new[]{new{Id="REFERENCE_WAGE_97790",Category="Earning",monthlyAmount=amount,amount}},Json)});
        var historyIds=new Dictionary<int,long>();
        foreach(var (employee,annual)in new[]{(One,360000m),(Two,300000m)})
            historyIds[employee]=await db.ExecuteScalarAsync<long>("INSERT INTO employee_it0008_basic_pay(EmployeeId,ClientId,ActionType,EffectiveFrom,EffectiveTo,Status,SalaryStructureId,AnnualCtc,SalaryJson) VALUES(@Employee,97790,'Closed Excel Salary','2026-09-01','2026-09-30','Historical','97790',@Annual,JSON_OBJECT('retained','salary inputs'));SELECT LAST_INSERT_ID();",new{Employee=employee,Annual=annual});
        await db.ExecuteAsync("INSERT INTO employee_it0008_basic_pay(EmployeeId,ClientId,ActionType,EffectiveFrom,Status,SalaryStructureId,AnnualCtc,SalaryJson) VALUES(9779001,97790,'Future basis','2027-01-01','Active','future-retained',777777,JSON_OBJECT('future','retained'))");
        var identity=JsonSerializer.SerializeToNode(new{clientId=Client,employeeId=One,rowId=batch.Rows[0].Id,sourceRow=2,sourceHash,sourceCellHash=new string('a',64),bankOwnershipHash=new string('b',64),reviewedNamesHash=new string('c',64)})!;
        var historical=new{actorId=Client,sourceHash,receipt=new{clientId=Client,batchId=Batch,payPeriod="2026-09",requestId=ReceiptId,fingerprint=new string('d',64),
            rows=batch.Rows.Select((row,index)=>new{employeeId=index==0?One:Two,rowId=row.Id,historyId=historyIds[index==0?One:Two],templateId="97790"}).ToArray(),
            initialSourceTargets=new[]{(One,30000m),(Two,25000m)}.Select(pair=>new{employeeId=pair.Item1,monthlyCtcColumnIndex=3,sourceMonthlyBudget=pair.Item2,annualSalaryTarget=pair.Item2*12,sourceHash,meaning="MonthlyPayrollEarningsBudget",actualJoiningDateNotEstablished=true}).ToArray(),sourceIdentities=new[]{identity}},
            reviewedRequest=new{salaryTargetMode="ReviewedInitialSourceTarget",setup=new{batchId=Batch,rowIds=batch.Rows.Select(row=>row.Id).ToArray(),employeeDecisions=batch.Rows.Select((row,index)=>new{rowId=row.Id,matchedEmployeeId=index==0?One:Two,allowCreate=false}).ToArray()},
                assignments=batch.Rows.Select((row,index)=>new{rowId=row.Id,employeeId=index==0?One:Two,templateId="97790"}).ToArray(),
                initialSourceTargetReview=new{version=1,reviewed=true,sourceHash,monthlyCtcColumnIndex=3,meaning="MonthlyPayrollEarningsBudget",actualJoiningDateNotEstablished=true}}};
        await db.ExecuteAsync("INSERT INTO modulesettings(client_id,ModuleCode,IsEnabled,SettingsJson) VALUES(97790,@Code,TRUE,CAST(@Envelope AS JSON))",
            new{Code=$"closed_salary_receipt:{Client}:{ReceiptId}",Envelope=JsonSerializer.Serialize(new{purpose="ClosedHistoricalSalaryReceipt",payload=Protector.ProtectHistoricalSalary(JsonSerializer.Serialize(historical,Json))},Json)});
    }
    private static Task StoreSource(MySqlConnection db,ExcelPayslipBatch value,ExcelPayslipCalculationSource calculation)=>db.ExecuteAsync("INSERT INTO excel_payslip_batches(client_id,id,batch_json,send_state_json,created_at) VALUES(97790,@Id,CAST(@Batch AS JSON),JSON_OBJECT('preserve','old SMTP and corrected history'),UTC_TIMESTAMP());INSERT INTO excel_payslip_calculation_sources(client_id,batch_id,source_json,created_at) VALUES(97790,@Id,CAST(@Source AS JSON),UTC_TIMESTAMP())",new{value.Id,Batch=JsonSerializer.Serialize(value,Json),Source=JsonSerializer.Serialize(calculation,Json)});
    private static Task<int> InsertRun(MySqlConnection db,string period)=>db.ExecuteScalarAsync<int>("INSERT INTO payruns(ClientId,ClientName,PayPeriod,RunType,Status,TotalWorkingDays,RequestJson) VALUES(97790,'Synthetic reference',@Period,'Regular','Draft',30,CAST(@Request AS JSON));SELECT LAST_INSERT_ID();",new{Period=period,Request=JsonSerializer.Serialize(new CreatePayRunRequest{ClientId=Client,PayPeriod=period,IncludedEmployeeIds=[One,Two],TotalWorkingDays=30},Json)});
    public async Task DisposeAsync(){if(fixture is not null)await CleanAsync();}
    private async Task CleanAsync()
    {
        await using var db=await F.OpenAsync();
        if(await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name='workflow_action_rules')"))
            await db.ExecuteAsync("DELETE FROM workflow_action_rules WHERE ActivityCode='ReferenceInformation97790'");
        await db.ExecuteAsync("""
DROP TRIGGER IF EXISTS codex_prfi_fail_97790;
DELETE FROM modulesettings WHERE client_id=97790;
DELETE FROM paysliptemplates WHERE ClientId=97790;
DELETE FROM payrunemployees WHERE ClientId=97790;DELETE FROM payruns WHERE ClientId=97790;
DELETE FROM employee_it0008_basic_pay WHERE ClientId=97790;
DELETE FROM excel_payslip_calculation_sources WHERE client_id=97790;DELETE FROM excel_payslip_batches WHERE client_id=97790;
DELETE FROM employeepersonaldetails WHERE EmployeeId IN (9779001,9779002);
DELETE FROM employees WHERE ClientId=97790;DELETE FROM salarycomponents WHERE Id=9779001;DELETE FROM clients WHERE Id=97790;
""");
    }
    private async Task<string> WitnessAsync(params string[] permittedNewCodes)
    {
        await using var db=await F.OpenAsync();var witness=new SortedDictionary<string,string>();
        var tables=await db.QueryAsync<string>("SELECT table_name FROM information_schema.tables WHERE table_schema=DATABASE() AND table_type='BASE TABLE' ORDER BY table_name");
        foreach(var table in tables)
        {
            var rows=table=="modulesettings"&&permittedNewCodes.Length>0
                ?await db.QueryAsync("SELECT * FROM modulesettings WHERE NOT(client_id=97790 AND ModuleCode IN @Codes)",new{Codes=permittedNewCodes})
                :await db.QueryAsync($"SELECT * FROM `{table.Replace("`","``")}`");
            witness[table]=PayrollReferenceInformation.Hash(rows.Select(row=>JsonSerializer.Serialize((IDictionary<string,object>)row,Json)).Order(StringComparer.Ordinal).ToArray());
        }
        return JsonSerializer.Serialize(witness,Json);
    }
    private async Task<(PayslipDesignProfile Profile,PayslipDesignTemplate Template)> DesignAsync(MySqlConnection db,MySqlTransaction tx)
    {
        var envelope=JsonNode.Parse((await db.ExecuteScalarAsync<string>("SELECT CAST(SettingsJson AS CHAR) FROM modulesettings WHERE client_id=97790 AND ModuleCode=@Code",new{Code=$"payslip.design:{designId}"},tx))!)!;
        var profile=JsonSerializer.Deserialize<PayslipDesignProfile>(Protector.UnprotectPayslipDesign(envelope["value"]!.ToString())!,Json)!;
        var template=await db.QuerySingleAsync<PayslipDesignTemplate>("SELECT * FROM paysliptemplates WHERE Id=@Id AND ClientId=97790",new{Id=designId},tx);return(profile,template);
    }
    private async Task<PayrollReferenceInformationOverride> ReadAsync(bool gate=true,int employee=Two,int? selectedRun=null,string period="2026-09")
    {
        await using var db=await F.OpenAsync();await using var tx=await db.BeginTransactionAsync(IsolationLevel.RepeatableRead,isReadOnly:true);
        var design=await DesignAsync(db,tx);var result=await Repo(gate).ReadReferenceInformationOverrideAsync(db,tx,design.Profile,design.Template,selectedRun??runId,period,employee);await tx.RollbackAsync();return result;
    }
    [LocalFact]
    public async Task Preview_is_enforced_read_only_compact_and_derives_complete_two_modes_without_master_or_source_money_copy()
    {
        var before=await WitnessAsync();var review=await Repo().PreviewReferenceInformationAsync(Request,Actor);Assert.Equal(before,await WitnessAsync());
        Assert.Equal(2,review.Rows.Count);Assert.Equal(25000,review.Rows.Single(row=>row.EmployeeId==Two).MonthlyBudget);Assert.True(review.ReviewToken.Length<16000);
        var token=Protector.UnprotectPayrollReferenceInformation(review.ReviewToken)!;Assert.DoesNotContain("monthlyBudget",token);Assert.DoesNotContain("employees",token);Assert.Contains("actorId",token);
        Assert.Equal(PayrollReferenceStatus.NotApplicable,(await ReadAsync()).Status);
    }
    [LocalFact]
    public async Task Apply_writes_exactly_two_metadata_rows_replays_stably_and_retains_all_old_rows_then_reader_ignores_disabled_writer_gate()
    {
        var repo=Repo();var review=await repo.PreviewReferenceInformationAsync(Request,Actor);var before=await WitnessAsync();var id=Guid.NewGuid().ToString("N");var command=new PayrollReferenceInformationApplyRequest(review.ReviewToken,id,true);
        var receipt=await repo.ApplyReferenceInformationAsync(command,Actor);
        Assert.Equal(before,await WitnessAsync($"prfi:run:{runId}",$"prfi:req:{id}"));Assert.Equal(2,receipt.EmployeeCount);
        var after=await WitnessAsync();Assert.Equal(receipt,await repo.ApplyReferenceInformationAsync(command,Actor));Assert.Equal(after,await WitnessAsync());
        var differentToken=Protector.ProtectPayrollReferenceInformation(Protector.UnprotectPayrollReferenceInformation(command.ReviewToken)!);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>repo.ApplyReferenceInformationAsync(command with{ReviewToken=differentToken},Actor));Assert.Equal(after,await WitnessAsync());
        var result=await ReadAsync(false);Assert.Equal(PayrollReferenceStatus.Resolved,result.Status);Assert.Equal(25000,result.Value);Assert.Equal(3,result.SourceColumn);
        Assert.Equal(PayrollReferenceStatus.NotApplicable,(await ReadAsync(false,selectedRun:octoberRunId,period:"2026-10")).Status);Assert.Equal(after,await WitnessAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(()=>repo.PreviewReferenceInformationAsync(Request,Actor));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>repo.ApplyReferenceInformationAsync(command with{RequestId=Guid.NewGuid().ToString()},Actor));Assert.Equal(after,await WitnessAsync());
    }
    [LocalTheory][InlineData("profile")][InlineData("source")][InlineData("history")][InlineData("native")][InlineData("roster")][InlineData("status")][InlineData("payment")]
    public async Task Apply_holds_every_stale_review_without_any_partial_metadata_or_other_write(string changed)
    {
        var repo=Repo();var review=await repo.PreviewReferenceInformationAsync(Request,Actor);
        await MutateAsync(changed);var before=await WitnessAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>repo.ApplyReferenceInformationAsync(new(review.ReviewToken,Guid.NewGuid().ToString(),true),Actor));Assert.Equal(before,await WitnessAsync());
    }
    private async Task MutateAsync(string changed)
    {
        await using var db=await F.OpenAsync();
        if(changed=="profile")
        {
            var code=$"payslip.design:{designId}";var envelope=JsonNode.Parse((await db.ExecuteScalarAsync<string>("SELECT CAST(SettingsJson AS CHAR) FROM modulesettings WHERE client_id=97790 AND ModuleCode=@Code",new{Code=code}))!)!;
            var profile=JsonNode.Parse(Protector.UnprotectPayslipDesign(envelope["value"]!.ToString())!)!;
            profile["fields"]!.AsArray().Single(field=>field!["sourceColumn"]!.GetValue<int>()==3)!["label"]="Reviewed monthly earnings budget";
            envelope["value"]=Protector.ProtectPayslipDesign(profile.ToJsonString());await db.ExecuteAsync("UPDATE modulesettings SET SettingsJson=CAST(@Json AS JSON) WHERE client_id=97790 AND ModuleCode=@Code",new{Code=code,Json=envelope.ToJsonString()});
        }
        else await db.ExecuteAsync(changed switch{
            "source"=>"UPDATE excel_payslip_calculation_sources SET source_json=JSON_SET(source_json,'$.columns[0].label','Changed source') WHERE client_id=97790 AND batch_id='97790000000000000000000000000001'",
            "history"=>"UPDATE employee_it0008_basic_pay SET AnnualCtc=1 WHERE ClientId=97790 AND EmployeeId=9779002",
            "native"=>"UPDATE payrunemployees SET NetPay=1 WHERE ClientId=97790 AND EmployeeId=9779002",
            "roster"=>"DELETE FROM payrunemployees WHERE ClientId=97790 AND EmployeeId=9779002",
            "status"=>"UPDATE payruns SET Status='Approved' WHERE ClientId=97790",
            _=>"UPDATE payrunemployees SET PaymentStatus='Paid' WHERE ClientId=97790 AND EmployeeId=9779002"});
    }
    [LocalTheory][InlineData("profile")][InlineData("source")][InlineData("history")][InlineData("native")][InlineData("roster")]
    public async Task Same_run_manifest_stale_state_never_silently_falls_back_to_legacy(string changed)
    {
        var review=await Repo().PreviewReferenceInformationAsync(Request,Actor);await Repo().ApplyReferenceInformationAsync(new(review.ReviewToken,Guid.NewGuid().ToString(),true),Actor);
        await MutateAsync(changed);var before=await WitnessAsync();await Assert.ThrowsAsync<InvalidOperationException>(()=>ReadAsync(false));Assert.Equal(before,await WitnessAsync());
    }
    [LocalFact]
    public async Task Purpose_actor_scope_confirmation_expiry_and_guid_tampering_are_rejected_without_writes()
    {
        var repo=Repo();var review=await repo.PreviewReferenceInformationAsync(Request,Actor);var before=await WitnessAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Repo(false).PreviewReferenceInformationAsync(Request,Actor));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>repo.ApplyReferenceInformationAsync(new(review.ReviewToken,Guid.NewGuid().ToString(),false),Actor));
        var other=Actor;other.Id++;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>repo.ApplyReferenceInformationAsync(new(review.ReviewToken,Guid.NewGuid().ToString(),true),other));
        var revoked=Actor;revoked.AssignedClientIds=[];
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>repo.ApplyReferenceInformationAsync(new(review.ReviewToken,Guid.NewGuid().ToString(),true),revoked));
        var plain=JsonNode.Parse(Protector.UnprotectPayrollReferenceInformation(review.ReviewToken)!)!;plain["expiresAtUtc"]=DateTime.UtcNow.AddMinutes(-1);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>repo.ApplyReferenceInformationAsync(new(Protector.ProtectPayrollReferenceInformation(plain.ToJsonString()),Guid.NewGuid().ToString(),true),Actor));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>repo.ApplyReferenceInformationAsync(new(Protector.ProtectHistoricalSalary(plain.ToJsonString()),Guid.NewGuid().ToString(),true),Actor));Assert.Equal(before,await WitnessAsync());
    }
    [LocalFact]
    public async Task Last_receipt_insert_failure_rolls_back_first_manifest_and_preserves_every_old_row()
    {
        var repo=Repo();var review=await repo.PreviewReferenceInformationAsync(Request,Actor);
        await using(var db=await F.OpenAsync())await db.ExecuteAsync("CREATE TRIGGER codex_prfi_fail_97790 BEFORE INSERT ON modulesettings FOR EACH ROW BEGIN IF NEW.client_id=97790 AND NEW.ModuleCode LIKE 'prfi:req:%' THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='owned reference receipt rollback'; END IF; END");
        var before=await WitnessAsync();await Assert.ThrowsAsync<MySqlException>(()=>repo.ApplyReferenceInformationAsync(new(review.ReviewToken,Guid.NewGuid().ToString(),true),Actor));Assert.Equal(before,await WitnessAsync());
    }
    [LocalFact]
    public async Task Exact_source_column_and_authentication_are_required_no_arbitrary_field_or_amount_override()
    {
        var repo=Repo();var before=await WitnessAsync();await Assert.ThrowsAsync<InvalidOperationException>(()=>repo.PreviewReferenceInformationAsync(Request with{SourceInfoColumn=4},Actor));
        await using(var db=await F.OpenAsync())await db.ExecuteAsync("UPDATE modulesettings SET SettingsJson=JSON_OBJECT('purpose','ClosedHistoricalSalaryReceipt','payload','not-authenticated') WHERE client_id=97790 AND ModuleCode LIKE 'closed_salary_receipt:%'");
        var changed=await WitnessAsync();await Assert.ThrowsAsync<InvalidOperationException>(()=>repo.PreviewReferenceInformationAsync(Request,Actor));Assert.Equal(changed,await WitnessAsync());Assert.NotEqual(before,changed);
    }
    [LocalFact]
    public async Task Approved_transition_keeps_frozen_budget_while_disabled_or_corrupt_manifest_never_bypasses_it()
    {
        var review=await Repo().PreviewReferenceInformationAsync(Request,Actor);await Repo().ApplyReferenceInformationAsync(new(review.ReviewToken,Guid.NewGuid().ToString(),true),Actor);
        await using(var db=await F.OpenAsync())await db.ExecuteAsync("UPDATE payruns SET Status='Approved' WHERE ClientId=97790 AND Id=@Id",new{Id=runId});
        var before=await WitnessAsync();Assert.Equal(25000,(await ReadAsync(false)).Value);Assert.Equal(before,await WitnessAsync());
        await using(var db=await F.OpenAsync())await db.ExecuteAsync("UPDATE modulesettings SET IsEnabled=FALSE WHERE client_id=97790 AND ModuleCode=@Code",new{Code=$"prfi:run:{runId}"});
        var disabled=await WitnessAsync();await Assert.ThrowsAsync<InvalidOperationException>(()=>ReadAsync(false));Assert.Equal(disabled,await WitnessAsync());
        await using(var db=await F.OpenAsync())await db.ExecuteAsync("UPDATE modulesettings SET IsEnabled=TRUE,SettingsJson=JSON_OBJECT('version',1,'purpose','PayrollReferenceInformation','value','not-authenticated') WHERE client_id=97790 AND ModuleCode=@Code",new{Code=$"prfi:run:{runId}"});
        var corrupt=await WitnessAsync();await Assert.ThrowsAsync<InvalidOperationException>(()=>ReadAsync(false));Assert.Equal(corrupt,await WitnessAsync());
    }
    [LocalFact]
    public async Task Concurrent_same_guid_apply_is_one_atomic_manifest_and_one_stable_receipt()
    {
        var review=await Repo().PreviewReferenceInformationAsync(Request,Actor);var id=Guid.NewGuid().ToString("N");var command=new PayrollReferenceInformationApplyRequest(review.ReviewToken,id,true);
        var before=await WitnessAsync();var results=await Task.WhenAll(Repo().ApplyReferenceInformationAsync(command,Actor),Repo().ApplyReferenceInformationAsync(command,Actor));
        Assert.Equal(results[0],results[1]);Assert.Equal(before,await WitnessAsync($"prfi:run:{runId}",$"prfi:req:{id}"));
    }
}
