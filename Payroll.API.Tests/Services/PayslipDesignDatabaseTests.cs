using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using PdfSharp.Pdf.IO;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;
using Payroll.API.Tests.Fixtures;

namespace Payroll.API.Tests.Services;

[Collection("ReviewedPayroll")]
public sealed class PayslipDesignDatabaseTests : IAsyncLifetime
{
    private const int Client=97741,Employee=9774101;
    private const string Batch="97741000000000000000000000000001";
    private const long GlobalTemplate=9774100;
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private readonly ReviewedPayrollFixture? fixture=Environment.GetEnvironmentVariable("FREVO_REVIEWED_PAYROLL_TEST_DB") is string value?new(value):null;
    private ReviewedPayrollFixture F=>fixture??throw new InvalidOperationException("The owned synthetic database is required.");
    private PayslipDesignRepository Repo=>new(F.Config,new(F.Config,new EphemeralDataProtectionProvider(),NullLogger<PortableIntegrationCredentialProtector>.Instance),new());
    private static AuthUser Actor=>new(){Id=97741,IsActive=true,Permissions=["settings.manage"],ClientAccessMode="Selected",AssignedClientIds=[Client]};
    private sealed class IsolatedFactAttribute:FactAttribute { public IsolatedFactAttribute(){if(Environment.GetEnvironmentVariable("FREVO_REVIEWED_PAYROLL_TEST_DB") is null)Skip="Explicit sentinel-guarded synthetic database required.";} }
    private int runId;
    public async Task InitializeAsync()
    {
        if(fixture is null)return;await F.InitializeAsync();await CleanAsync();await using var db=await F.OpenAsync();
        await db.ExecuteAsync(@"INSERT INTO clients(Id,Code,Name,IsActive) VALUES(97741,'SYNTH-DESIGN','Synthetic design client',TRUE);
INSERT INTO worklocations(Id,ClientId,Name) VALUES(97741,97741,'Synthetic work location');
INSERT INTO employees(Id,ClientId,EmployeeCode,FirstName,LastName,Designation,AnnualCtc,WorkLocationId) VALUES(9774101,97741,'SYNTH-DESIGN-1','Synthetic','Employee','Current Master role',999999,97741);
INSERT INTO employeepersonaldetails(EmployeeId,UanNumber,Mobile) VALUES(9774101,'101234567899','9999999999');
INSERT INTO employeepaymentdetails(EmployeeId,BankAccountNo,IfscCode) VALUES(9774101,'001234567890','SYNTH000001');
INSERT INTO salarycomponents(Id,Code,Name,Category,Active) VALUES(9774101,'DESIGN_BASIC_97741','Synthetic design salary','Earning',TRUE),(9774102,'DESIGN_PF_97741','Synthetic design PF','Deduction',TRUE),(9774103,'DESIGN_EPF_97741','Synthetic design employer PF','Benefit',TRUE);
INSERT INTO paysliptemplates(Id,ClientId,Name,Theme,Active) VALUES(9774100,0,'Synthetic global unaffected','Classic',TRUE);");
        var batch=new ExcelPayslipBatch{Id=Batch,ClientId=Client,Month="2026-10",SourceFileName="Synthetic design.xlsx",Rows=[new(){Id="source-row",EmployeeName="Source must never supply identity",Earnings=[new(){Label="Salary",Amount=99999999}],NetPay=99999999}]};
        var source=new ExcelPayslipCalculationSource{Columns=[new(){ColumnIndex=0,Kind="info",Label="UAN"},new(){ColumnIndex=1,Kind="earning",Label="Salary"},new(){ColumnIndex=2,Kind="deduction",Label="Employee PF"},new(){ColumnIndex=3,Kind="employer",Label="Employer PF"}]};
        await db.ExecuteAsync("INSERT INTO excel_payslip_batches(client_id,id,batch_json,send_state_json,created_at) VALUES(@Client,@Batch,CAST(@BatchJson AS JSON),JSON_OBJECT('preserve','old mail receipt'),UTC_TIMESTAMP());INSERT INTO excel_payslip_calculation_sources(client_id,batch_id,source_json,created_at) VALUES(@Client,@Batch,CAST(@SourceJson AS JSON),UTC_TIMESTAMP())",new{Client,Batch,BatchJson=JsonSerializer.Serialize(batch,Json),SourceJson=JsonSerializer.Serialize(source,Json)});
        runId=await db.ExecuteScalarAsync<int>("INSERT INTO payruns(ClientId,ClientName,PayPeriod,Status,TotalWorkingDays) VALUES(97741,'Synthetic design client','2026-10','Approved',31);SELECT LAST_INSERT_ID();");
        var lines=JsonSerializer.Serialize(new[]{new{Id="DESIGN_BASIC_97741",Category="Earning",monthlyAmount=16000,amount=16000},new{Id="DESIGN_PF_97741",Category="Deduction",monthlyAmount=1800,amount=1800},new{Id="DESIGN_EPF_97741",Category="Benefit",monthlyAmount=1950,amount=1950}},Json);
        await db.ExecuteAsync(@"INSERT INTO payrunemployees(PayRunId,EmployeeId,ClientId,EmployeeCode,EmployeeName,MonthlyGross,GrossPay,StatutoryDeductions,OneTimeEarnings,OneTimeDeductions,NetPay,PresentDays,PayableDays,IsSkipped,DetailsJson)
VALUES(@RunId,9774101,97741,'OLD-CODE','Old source name',16000,16000,1800,0,0,14200,NULL,31,FALSE,CAST(@Lines AS JSON))",new{RunId=runId,Lines=lines});
    }
    public async Task DisposeAsync(){if(fixture is not null)await CleanAsync();}
    private async Task CleanAsync()
    {
        await using var db=await F.OpenAsync();await db.ExecuteAsync(@"DELETE FROM modulesettings WHERE client_id=97741;
DELETE FROM paysliptemplates WHERE ClientId=97741 OR Id=9774100;
DELETE p FROM payrunemployees p JOIN payruns r ON r.Id=p.PayRunId WHERE r.ClientId=97741;DELETE FROM payruns WHERE ClientId=97741;
DELETE FROM excel_payslip_batches WHERE client_id=97741;DELETE FROM excel_payslip_calculation_sources WHERE client_id=97741;
DELETE FROM employeepersonaldetails WHERE EmployeeId=9774101;DELETE FROM employeepaymentdetails WHERE EmployeeId=9774101;
DELETE FROM employees WHERE ClientId=97741;DELETE FROM worklocations WHERE ClientId=97741;DELETE FROM clients WHERE Id=97741;
DELETE FROM salarycomponents WHERE Id IN(9774101,9774102,9774103,9774104);");
    }
    private static PayslipDesignSaveRequest Request(PayslipDesignPreview preview)=>new(){RequestId=Guid.NewGuid().ToString(),Template=new(){ClientId=Client,Name="Synthetic reviewed design",ShowYtd=false},SourceBatchId=Batch,SourceHash=preview.SourceHash,Reviewed=true,Fields=preview.Fields.Select(field=>new PayslipDesignField{SourceColumn=field.SourceColumn,Label=field.Label,Kind=field.Kind,InfoToken=field.Kind=="info"?field.InfoToken:"",NativeCode=field.Kind switch{"earning"=>"DESIGN_BASIC_97741","deduction"=>"DESIGN_PF_97741","employer"=>"DESIGN_EPF_97741",_=>""}}).ToList()};
    private async Task<string> WitnessAsync()
    {await using var db=await F.OpenAsync();return await db.ExecuteScalarAsync<string>(@"SELECT CONCAT(SHA2(CAST(b.batch_json AS CHAR),256),SHA2(CAST(b.send_state_json AS CHAR),256),SHA2(CAST(s.source_json AS CHAR),256)) FROM excel_payslip_batches b JOIN excel_payslip_calculation_sources s ON s.client_id=b.client_id AND s.batch_id=b.id WHERE b.client_id=97741 AND b.id=@Batch",new{Batch});}
    private async Task<string> AllReadStateAsync()
    {
        await using var db=await F.OpenAsync();
        return JsonSerializer.Serialize(new{
            Sources=await db.QueryAsync("SELECT id,CAST(batch_json AS CHAR) Batch,CAST(send_state_json AS CHAR) Mail FROM excel_payslip_batches WHERE client_id=97741 ORDER BY id"),
            Calculations=await db.QueryAsync("SELECT batch_id,CAST(source_json AS CHAR) Source FROM excel_payslip_calculation_sources WHERE client_id=97741 ORDER BY batch_id"),
            Employees=await db.QueryAsync("SELECT * FROM employees WHERE ClientId=97741 ORDER BY Id"),
            Personal=await db.QueryAsync("SELECT * FROM employeepersonaldetails WHERE EmployeeId=9774101"),
            Payment=await db.QueryAsync("SELECT * FROM employeepaymentdetails WHERE EmployeeId=9774101"),
            Templates=await db.QueryAsync("SELECT * FROM paysliptemplates WHERE ClientId=97741 OR Id=9774100 ORDER BY Id"),
            Profiles=await db.QueryAsync("SELECT ModuleCode,IsEnabled,CAST(SettingsJson AS CHAR) Settings FROM modulesettings WHERE client_id=97741 ORDER BY ModuleCode"),
            Runs=await db.QueryAsync("SELECT * FROM payruns WHERE ClientId=97741 ORDER BY Id"),
            Payroll=await db.QueryAsync("SELECT * FROM payrunemployees WHERE ClientId=97741 ORDER BY Id")},Json);
    }
    private static PayslipDesignSaveRequest UnmappedPreview(PayslipDesignPreview preview)
    {
        var request=Request(preview);request.Reviewed=false;request.RequestId="";
        foreach(var field in request.Fields){field.NativeCode="";field.InfoToken="";}
        return request;
    }
    private static string PdfContent(PayslipDesignPdf result)
    {
        using var document=PdfReader.Open(new MemoryStream(result.Bytes),PdfDocumentOpenMode.Import);
        Assert.Equal(1,document.PageCount);return Encoding.Latin1.GetString(document.Pages[0].Contents.CreateSingleContent().Stream.UnfilteredValue);
    }
    private async Task LinkSourceProfileForSampleAsync()
    {
        await using var db=await F.OpenAsync();
        var batch=JsonSerializer.Deserialize<ExcelPayslipBatch>((await db.ExecuteScalarAsync<string>("SELECT CAST(batch_json AS CHAR) FROM excel_payslip_batches WHERE client_id=97741"))!,Json)!;
        var source=JsonSerializer.Deserialize<ExcelPayslipCalculationSource>((await db.ExecuteScalarAsync<string>("SELECT CAST(source_json AS CHAR) FROM excel_payslip_calculation_sources WHERE client_id=97741"))!,Json)!;
        var row=batch.Rows[0];batch.ProfileLinks=[new(row.Id,row.SourceRow,Employee,ExcelPayslipMasterSynchronization.ProfileIdentityHash(batch,row,source))];
        await db.ExecuteAsync("UPDATE excel_payslip_batches SET batch_json=CAST(@Json AS JSON) WHERE client_id=97741",new{Json=JsonSerializer.Serialize(batch,Json)});
    }

    [IsolatedFact]
    public async Task Native_currency_bridge_uses_authenticated_same_transaction_source_without_writes()
    {
        var repo=Repo;var preview=await repo.PreviewAsync(new(){ClientId=Client,SourceBatchId=Batch});
        var template=await repo.SaveAsync(Request(preview),Actor);var before=await AllReadStateAsync();
        await using(var db=await F.OpenAsync())
        await using(var tx=await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,isReadOnly:true))
        {
            var binding=await repo.ReadNativeSourceDesignBindingAsync(db,tx,Client);
            Assert.NotNull(binding);Assert.Equal(template.Id,binding.Template.Id);Assert.Equal(Client,binding.Profile.ClientId);
            Assert.Equal(Batch,binding.Batch.Id);Assert.Equal(preview.SourceHash,binding.DesignMetadataHash);
            Assert.Equal(binding.Profile.SourceHash,binding.DesignMetadataHash);Assert.Equal(4,binding.Source.Columns.Count);
            Assert.Equal(await db.ExecuteScalarAsync<string>("SELECT CAST(batch_json AS CHAR) FROM excel_payslip_batches WHERE client_id=97741 AND id=@Batch",new{Batch},tx),binding.RawBatchJson);
            Assert.Equal(await db.ExecuteScalarAsync<string>("SELECT CAST(source_json AS CHAR) FROM excel_payslip_calculation_sources WHERE client_id=97741 AND batch_id=@Batch",new{Batch},tx),binding.RawSourceJson);
            Assert.Equal(1,await db.ExecuteScalarAsync<int>("SELECT 1",transaction:tx));await tx.RollbackAsync();
        }
        Assert.Equal(before,await AllReadStateAsync());
    }

    [IsolatedFact]
    public async Task Native_currency_bridge_never_uses_global_or_foreign_client_design()
    {
        var repo=Repo;await using var db=await F.OpenAsync();await using var tx=await db.BeginTransactionAsync();
        Assert.Null(await repo.ReadNativeSourceDesignBindingAsync(db,tx,Client));
        Assert.Null(await repo.ReadNativeSourceDesignBindingAsync(db,tx,Client+1));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>repo.ReadNativeSourceDesignBindingAsync(db,tx,0));
        await tx.RollbackAsync();
    }

    [IsolatedFact]
    public async Task Native_currency_bridge_rejects_changed_source_and_rolls_back_caller_owned_changes()
    {
        var repo=Repo;var preview=await repo.PreviewAsync(new(){ClientId=Client,SourceBatchId=Batch});await repo.SaveAsync(Request(preview),Actor);
        var before=await AllReadStateAsync();
        await using(var db=await F.OpenAsync())await using(var tx=await db.BeginTransactionAsync())
        {
            await db.ExecuteAsync("UPDATE excel_payslip_calculation_sources SET source_json=JSON_SET(source_json,'$.metadataChanged',TRUE) WHERE client_id=97741",transaction:tx);
            await Assert.ThrowsAsync<InvalidOperationException>(()=>repo.ReadNativeSourceDesignBindingAsync(db,tx,Client,true));
            await tx.RollbackAsync();
        }
        Assert.Equal(before,await AllReadStateAsync());
    }

    [IsolatedFact]
    public async Task Native_currency_bridge_rejects_unprotected_profile_and_foreign_transaction()
    {
        var repo=Repo;var preview=await repo.PreviewAsync(new(){ClientId=Client,SourceBatchId=Batch});await repo.SaveAsync(Request(preview),Actor);
        var before=await AllReadStateAsync();
        await using(var db=await F.OpenAsync())await using(var foreign=await F.OpenAsync())await using(var tx=await db.BeginTransactionAsync())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(()=>repo.ReadNativeSourceDesignBindingAsync(foreign,tx,Client));
            await db.ExecuteAsync("UPDATE modulesettings SET SettingsJson=JSON_OBJECT('value','not-authenticated') WHERE client_id=97741 AND ModuleCode LIKE 'payslip.design:%'",transaction:tx);
            await Assert.ThrowsAsync<InvalidOperationException>(()=>repo.ReadNativeSourceDesignBindingAsync(db,tx,Client));
            await tx.RollbackAsync();
        }
        Assert.Equal(before,await AllReadStateAsync());
    }

    [IsolatedFact]
    public async Task Unsaved_unmapped_unreviewed_design_can_preview_shared_pdf_without_any_source_master_salary_template_or_mail_write()
    {
        var repo=Repo;var metadata=await repo.PreviewAsync(new(){ClientId=Client,SourceBatchId=Batch});var request=UnmappedPreview(metadata);
        Assert.Equal(0,request.Template.Id);Assert.False(request.Reviewed);Assert.All(request.Fields,field=>Assert.Empty(field.NativeCode));
        var before=await AllReadStateAsync();var result=await repo.PreviewPdfAsync(request);var content=PdfContent(result);
        Assert.Equal(before,await AllReadStateAsync());Assert.Equal("sample-payslip-design.pdf",result.FileName);
        Assert.Contains("Sample Employee",content);Assert.Contains("SALARY SLIP",content);Assert.Contains("Salary",content);Assert.Contains("Employer Contributions",content);
        Assert.DoesNotContain("Source must never supply identity",content);Assert.DoesNotContain("Current Master role",content);Assert.DoesNotContain("101234567899",content);
        var stale=UnmappedPreview(metadata);stale.SourceHash=new string('0',64);await Assert.ThrowsAsync<InvalidOperationException>(()=>repo.PreviewPdfAsync(stale));
        var foreign=UnmappedPreview(metadata);foreign.Template.Id=GlobalTemplate;await Assert.ThrowsAsync<InvalidOperationException>(()=>repo.PreviewPdfAsync(foreign));
        Assert.Equal(before,await AllReadStateAsync());
    }

    [IsolatedFact]
    public async Task Sample_pdf_depends_on_labels_and_presentation_only_after_source_and_native_private_values_change()
    {
        await LinkSourceProfileForSampleAsync();
        var repo=Repo;var metadata=await repo.PreviewAsync(new(){ClientId=Client,SourceBatchId=Batch});
        Assert.Contains(metadata.Fields,field=>field.SourceColumn>=1500 && field.InfoToken=="DESIGNATION");
        var before=await AllReadStateAsync();var baseline=PdfContent(await repo.PreviewPdfAsync(UnmappedPreview(metadata)));Assert.Equal(before,await AllReadStateAsync());
        await using(var db=await F.OpenAsync())
        {
            var batch=JsonNode.Parse((await db.ExecuteScalarAsync<string>("SELECT CAST(batch_json AS CHAR) FROM excel_payslip_batches WHERE client_id=97741"))!)!;
            batch["rows"]![0]!["employeeName"]="PRIVATE_SOURCE_NAME";batch["rows"]![0]!["employeeCode"]="PRIVATE_SOURCE_CODE";
            batch["rows"]![0]!["email"]="private-source@example.test";batch["rows"]![0]!["information"]=new JsonArray(new JsonObject{["label"]="UAN",["value"]="109999999991"});
            batch["rows"]![0]!["earnings"]![0]!["amount"]=87654321;batch["rows"]![0]!["netPay"]=87654321;
            var source=JsonSerializer.Deserialize<ExcelPayslipCalculationSource>((await db.ExecuteScalarAsync<string>("SELECT CAST(source_json AS CHAR) FROM excel_payslip_calculation_sources WHERE client_id=97741"))!,Json)!;
            source.Rows=[new(){SourceRow=1,Cells=[new(){DataType="Text",Value="109999999991"},new(){DataType="Number",Value="87654321"},new(){DataType="Number",Value="654321"},new(){DataType="Number",Value="123456"}]}];
            var changedBatch=batch.Deserialize<ExcelPayslipBatch>(Json)!;changedBatch.Rows[0].SourceRow=1;
            changedBatch.ProfileLinks=[new(changedBatch.Rows[0].Id,1,Employee,ExcelPayslipMasterSynchronization.ProfileIdentityHash(changedBatch,changedBatch.Rows[0],source))];
            await db.ExecuteAsync(@"UPDATE excel_payslip_batches SET batch_json=CAST(@Json AS JSON) WHERE client_id=97741;
UPDATE excel_payslip_calculation_sources SET source_json=CAST(@Source AS JSON) WHERE client_id=97741;
UPDATE employees SET EmployeeCode='PRIVATE_MASTER_CODE',FirstName='PRIVATE_MASTER',LastName='IDENTITY',Designation='PRIVATE_DESIGNATION',AnnualCtc=87654321 WHERE Id=9774101;
UPDATE employeepersonaldetails SET UanNumber='108888888882',Mobile='9888888888',Address='PRIVATE_ADDRESS' WHERE EmployeeId=9774101;
UPDATE employeepaymentdetails SET BankAccountNo='009999999999',BankName='PRIVATE_BANK' WHERE EmployeeId=9774101;
UPDATE payrunemployees SET GrossPay=65432.10,NetPay=60000,DetailsJson=JSON_ARRAY(JSON_OBJECT('Id','PRIVATE_SALARY','Category','Earning','amount',65432.10,'monthlyAmount',87654.32)) WHERE ClientId=97741",new{Json=JsonSerializer.Serialize(changedBatch,Json),Source=JsonSerializer.Serialize(source,Json)});
        }
        var changed=await AllReadStateAsync();Assert.NotEqual(before,changed);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>repo.PreviewPdfAsync(UnmappedPreview(metadata)));Assert.Equal(changed,await AllReadStateAsync());
        var refreshed=await repo.PreviewAsync(new(){ClientId=Client,SourceBatchId=Batch});Assert.NotEqual(metadata.SourceHash,refreshed.SourceHash);
        Assert.Equal(metadata.Fields.Select(field=>(field.SourceColumn,field.Label,field.Kind)),refreshed.Fields.Select(field=>(field.SourceColumn,field.Label,field.Kind)));
        var result=PdfContent(await repo.PreviewPdfAsync(UnmappedPreview(refreshed)));Assert.Equal(baseline,result);Assert.Equal(changed,await AllReadStateAsync());
        foreach(var value in new[]{"PRIVATE_SOURCE_NAME","PRIVATE_SOURCE_CODE","PRIVATE_MASTER","PRIVATE_DESIGNATION","109999999991","108888888882","PRIVATE_BANK","87654321","65432.10","private-source@example.test"})Assert.DoesNotContain(value,result);
    }

    [IsolatedFact]
    public async Task Atomic_design_save_replay_retains_global_source_salary_mail_and_current_same_client_master()
    {
        var before=await WitnessAsync();var repo=Repo;var preview=await repo.PreviewAsync(new(){ClientId=Client,SourceBatchId=Batch});
        var request=Request(preview);var template=await repo.SaveAsync(request,Actor);Assert.True(template.Id>0);Assert.Equal(0,request.Template.Id);
        Assert.Equal(template.Id,(await repo.SaveAsync(request,Actor)).Id);Assert.Equal(before,await WitnessAsync());
        await using(var db=await F.OpenAsync())
        {
            Assert.Equal("Classic",await db.ExecuteScalarAsync<string>("SELECT Theme FROM paysliptemplates WHERE Id=9774100 AND ClientId=0"));
            Assert.Equal(1,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM paysliptemplates WHERE ClientId=97741"));
            Assert.Equal(14200,await db.ExecuteScalarAsync<decimal>("SELECT NetPay FROM payrunemployees WHERE PayRunId=@Id",new{Id=runId}));
            await db.ExecuteAsync("UPDATE employees SET FirstName='Updated',LastName='Master' WHERE Id=9774101;");
        }
        var pdf=await repo.CreatePdfAsync(Client,runId,Employee);Assert.NotNull(pdf);
        using var document=PdfReader.Open(new MemoryStream(pdf!.Bytes),PdfDocumentOpenMode.Import);
        var content=Encoding.Latin1.GetString(document.Pages[0].Contents.CreateSingleContent().Stream.UnfilteredValue);
        Assert.Contains("Updated Master",content);Assert.Contains("14,200",content);Assert.Contains("Employer PF",content);
        Assert.DoesNotContain("99,999,999",content);Assert.DoesNotContain("Old source name",content);
        Assert.Null(await repo.CreatePdfAsync(Client+1,runId,Employee));Assert.Null(await repo.CreatePdfAsync(Client,runId,Employee+1));
        Assert.NotNull(await repo.GetAsync(Client,template.Id));Assert.Null(await repo.GetAsync(Client+1,template.Id));
    }

    [IsolatedFact]
    public async Task Stale_source_global_overwrite_cross_client_and_incomplete_review_cannot_save()
    {
        var repo=Repo;var preview=await repo.PreviewAsync(new(){ClientId=Client,SourceBatchId=Batch});
        var request=Request(preview);request.Template.Id=GlobalTemplate;
        await Assert.ThrowsAsync<InvalidOperationException>(()=>repo.SaveAsync(request,Actor));
        request=Request(preview);request.Reviewed=false;await Assert.ThrowsAsync<InvalidOperationException>(()=>repo.SaveAsync(request,Actor));
        request=Request(preview);request.Fields.RemoveAt(0);await Assert.ThrowsAsync<InvalidOperationException>(()=>repo.SaveAsync(request,Actor));
        request=Request(preview);request.Template.ClientId=Client+1;await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>repo.SaveAsync(request,Actor));
        await using(var db=await F.OpenAsync())await db.ExecuteAsync("UPDATE excel_payslip_calculation_sources SET source_json=JSON_SET(source_json,'$.metadataChanged',TRUE) WHERE client_id=97741");
        await Assert.ThrowsAsync<InvalidOperationException>(()=>repo.SaveAsync(Request(preview),Actor));
        await using(var db=await F.OpenAsync()){Assert.Equal(0,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM modulesettings WHERE client_id=97741"));Assert.Equal(0,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM paysliptemplates WHERE ClientId=97741"));}
    }

    [IsolatedFact]
    public async Task Tampered_profile_and_pending_payroll_never_produce_final_pdf()
    {
        var repo=Repo;var preview=await repo.PreviewAsync(new(){ClientId=Client,SourceBatchId=Batch});var template=await repo.SaveAsync(Request(preview),Actor);
        await using(var db=await F.OpenAsync())await db.ExecuteAsync("UPDATE payruns SET Status='Draft' WHERE Id=@Id",new{Id=runId});
        Assert.Null(await repo.CreatePdfAsync(Client,runId,Employee));
        await using(var db=await F.OpenAsync())await db.ExecuteAsync("UPDATE payruns SET Status='Approved' WHERE Id=@Id;UPDATE modulesettings SET SettingsJson=JSON_OBJECT('value','tampered') WHERE client_id=97741 AND ModuleCode=@Code",new{Id=runId,Code=$"payslip.design:{template.Id}"});
        await Assert.ThrowsAsync<InvalidOperationException>(()=>repo.CreatePdfAsync(Client,runId,Employee));
    }

    [IsolatedFact]
    public async Task Reviewed_metadata_order_kinds_and_existing_profile_hash_guard_against_lost_updates()
    {
        var repo=Repo;var preview=await repo.PreviewAsync(new(){ClientId=Client,SourceBatchId=Batch});
        Assert.Equal(new[]{"UAN","Salary","Employee PF","Employer PF"},preview.Fields.Select(f=>f.Label));
        Assert.Equal(new[]{"info","earning","deduction","employer"},preview.Fields.Select(f=>f.Kind));
        Assert.Contains(preview.NativeCodes,c=>c.Code=="DESIGN_EPF_97741" && c.Category=="Benefit");
        var created=await repo.SaveAsync(Request(preview),Actor);
        using var loaded=JsonDocument.Parse(JsonSerializer.Serialize(await repo.GetAsync(Client,created.Id),Json));
        var hash=loaded.RootElement.GetProperty("profileHash").GetString()!;
        var first=Request(preview);first.Template.Id=created.Id;first.ExistingProfileHash=hash;first.Template.Note="Reviewed first edit";
        var stale=Request(preview);stale.Template.Id=created.Id;stale.ExistingProfileHash=hash;stale.Template.Note="Stale second edit";
        await repo.SaveAsync(first,Actor);
        Assert.Contains("changed after",(await Assert.ThrowsAsync<InvalidOperationException>(()=>repo.SaveAsync(stale,Actor))).Message);
        await using var db=await F.OpenAsync();Assert.Equal("Reviewed first edit",await db.ExecuteScalarAsync<string>("SELECT Note FROM paysliptemplates WHERE ClientId=97741 AND Id=@Id",new{created.Id}));
        Assert.Equal(1,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM paysliptemplates WHERE ClientId=97741"));
    }

    [IsolatedFact]
    public async Task Effective_source_profile_additions_are_metadata_only_and_changes_require_review()
    {
        var repo=Repo;var initial=await repo.PreviewAsync(new(){ClientId=Client,SourceBatchId=Batch});
        await using(var db=await F.OpenAsync())
        {
            var raw=await db.ExecuteScalarAsync<string>("SELECT CAST(batch_json AS CHAR) FROM excel_payslip_batches WHERE client_id=97741");
            var batch=JsonSerializer.Deserialize<ExcelPayslipBatch>(raw!,Json)!;
            var source=JsonSerializer.Deserialize<ExcelPayslipCalculationSource>((await db.ExecuteScalarAsync<string>("SELECT CAST(source_json AS CHAR) FROM excel_payslip_calculation_sources WHERE client_id=97741"))!,Json)!;
            batch.ProfileLinks=[new(batch.Rows[0].Id,batch.Rows[0].SourceRow,Employee,ExcelPayslipMasterSynchronization.ProfileIdentityHash(batch,batch.Rows[0],source))];
            await db.ExecuteAsync("UPDATE excel_payslip_batches SET batch_json=CAST(@Json AS JSON) WHERE client_id=97741;UPDATE employees SET Department='Current department',Grade='Current grade',WorkEmail='synthetic@example.test' WHERE Id=9774101",new{Json=JsonSerializer.Serialize(batch,Json)});
        }
        var preview=await repo.PreviewAsync(new(){ClientId=Client,SourceBatchId=Batch});Assert.NotEqual(initial.SourceHash,preview.SourceHash);
        Assert.Equal(new[]{"Designation","Department","Grade","Work Location","Email","Phone"},preview.Fields.Where(f=>f.SourceColumn>=1500).Select(f=>f.Label));
        Assert.DoesNotContain("Current department",JsonSerializer.Serialize(preview.Fields,Json));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>repo.SaveAsync(Request(initial),Actor));
        var template=await repo.SaveAsync(Request(preview),Actor);Assert.NotNull(await repo.CreatePdfAsync(Client,runId,Employee));
        await using(var db=await F.OpenAsync())await db.ExecuteAsync("UPDATE employees SET Department='' WHERE Id=9774101");
        var changed=await repo.PreviewAsync(new(){ClientId=Client,SourceBatchId=Batch});Assert.NotEqual(preview.SourceHash,changed.SourceHash);
        Assert.NotNull(await repo.CreatePdfAsync(Client,runId,Employee));
    }

    [IsolatedFact]
    public async Task Simple_source_zero_line_choice_is_protected_and_old_profile_omission_retains_legacy_display()
    {
        await using(var db=await F.OpenAsync())
        {
            await db.ExecuteAsync("UPDATE excel_payslip_batches SET batch_json=JSON_SET(batch_json,'$.simpleLayout',CAST('true' AS JSON)) WHERE client_id=97741;INSERT INTO salarycomponents(Id,Code,Name,Category,Active) VALUES(9774104,'DESIGN_ZERO_97741','Synthetic zero deduction','Deduction',TRUE)");
            var source=JsonSerializer.Deserialize<ExcelPayslipCalculationSource>((await db.ExecuteScalarAsync<string>("SELECT CAST(source_json AS CHAR) FROM excel_payslip_calculation_sources WHERE client_id=97741"))!,Json)!;
            source.Columns.Add(new(){ColumnIndex=4,Kind="deduction",Label="Zero review deduction"});
            await db.ExecuteAsync("UPDATE excel_payslip_calculation_sources SET source_json=CAST(@Json AS JSON) WHERE client_id=97741",new{Json=JsonSerializer.Serialize(source,Json)});
        }
        var repo=Repo;var preview=await repo.PreviewAsync(new(){ClientId=Client,SourceBatchId=Batch}); Assert.True(preview.SimpleLayout);
        var request=Request(preview);request.Fields.Single(field=>field.SourceColumn==4).NativeCode="DESIGN_ZERO_97741";
        var before=await WitnessAsync();var template=await repo.SaveAsync(request,Actor);
        var content=PdfContent((await repo.CreatePdfAsync(Client,runId,Employee))!);
        Assert.DoesNotContain("Zero review deduction",content); Assert.Contains("14,200",content); Assert.Equal(before,await WitnessAsync());
        var protector=new PortableIntegrationCredentialProtector(F.Config,new EphemeralDataProtectionProvider(),NullLogger<PortableIntegrationCredentialProtector>.Instance);
        await using(var db=await F.OpenAsync())
        {
            var code=$"payslip.design:{template.Id}";
            var envelope=JsonNode.Parse((await db.ExecuteScalarAsync<string>("SELECT CAST(SettingsJson AS CHAR) FROM modulesettings WHERE client_id=97741 AND ModuleCode=@Code",new{Code=code}))!)!;
            var profile=JsonNode.Parse(protector.UnprotectPayslipDesign(envelope["value"]!.GetValue<string>())!)!.AsObject();
            Assert.False(profile["keepMappedZeroRows"]!.GetValue<bool>());
            // Reproduce an actual older protected version-one profile, where
            // this additive setting was absent rather than explicitly false.
            profile.Remove("keepMappedZeroRows"); envelope["value"]=protector.ProtectPayslipDesign(profile.ToJsonString());
            await db.ExecuteAsync("UPDATE modulesettings SET SettingsJson=CAST(@Json AS JSON) WHERE client_id=97741 AND ModuleCode=@Code",new{Json=envelope.ToJsonString(),Code=code});
        }
        var legacyContent=PdfContent((await repo.CreatePdfAsync(Client,runId,Employee))!);
        Assert.Contains("Zero review deduction",legacyContent); Assert.Contains("14,200",legacyContent);
        Assert.Equal(before,await WitnessAsync());
        await using var check=await F.OpenAsync();
        Assert.Equal(16000m,await check.ExecuteScalarAsync<decimal>("SELECT GrossPay FROM payrunemployees WHERE PayRunId=@Id",new{Id=runId}));
        Assert.Equal(1800m,await check.ExecuteScalarAsync<decimal>("SELECT StatutoryDeductions FROM payrunemployees WHERE PayRunId=@Id",new{Id=runId}));
        Assert.Equal(14200m,await check.ExecuteScalarAsync<decimal>("SELECT NetPay FROM payrunemployees WHERE PayRunId=@Id",new{Id=runId}));
    }
}
