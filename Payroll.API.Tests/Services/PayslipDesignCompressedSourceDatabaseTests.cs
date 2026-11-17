using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;
using Payroll.API.Tests.Fixtures;

namespace Payroll.API.Tests.Services;

[Collection("ReviewedPayroll")]
public sealed class PayslipDesignCompressedSourceDatabaseTests : IAsyncLifetime
{
    private const int Client=97744;private const string Batch="97744000000000000000000000000001",Code="DESIGN_COMPRESS_97744";
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private readonly ReviewedPayrollFixture? fixture=Environment.GetEnvironmentVariable("FREVO_REVIEWED_PAYROLL_TEST_DB") is string value?new(value):null;
    private ReviewedPayrollFixture F=>fixture??throw new InvalidOperationException("Explicit sentinel-guarded synthetic database required.");
    private PayslipDesignRepository Repository=>new(F.Config,new(F.Config,new EphemeralDataProtectionProvider(),NullLogger<PortableIntegrationCredentialProtector>.Instance),new());
    private static AuthUser Actor=>new(){Id=Client,IsActive=true,ClientAccessMode="Selected",AssignedClientIds=[Client],Permissions=["settings.manage"]};
    private sealed class IsolatedFactAttribute:FactAttribute{public IsolatedFactAttribute(){if(Environment.GetEnvironmentVariable("FREVO_REVIEWED_PAYROLL_TEST_DB") is null)Skip="Explicit sentinel-guarded synthetic database required.";}}

    public async Task InitializeAsync()
    {
        if(fixture is null)return;await F.InitializeAsync();await CleanupAsync();await using var db=await F.OpenAsync();
        var batch=JsonSerializer.SerializeToNode(new ExcelPayslipBatch{Id=Batch,ClientId=Client,Month="2026-10",Rows=[new(){Id="compression-row",SourceRow=2,EmployeeName="Synthetic source",Earnings=[new(){Label="給与 ₹ वेतन",Amount=12345.6789012345m}],NetPay=12345.6789012345m}]},Json)!.AsObject();
        batch["unicodeWitness"]="₹ हिन्दी 名称 é \"quoted\" \\ escaped";batch["decimalWitness"]=0.123456789123456789m;
        var source=JsonSerializer.SerializeToNode(new ExcelPayslipCalculationSource{ColumnCount=1,HeaderRow=1,Columns=[new(){ColumnIndex=0,Kind="earning",Label="給与 ₹ वेतन",SourceHeader="給与 ₹ वेतन"}],Rows=[new(){SourceRow=2,Cells=[new(){Value="12345.6789012345",FormulaText="ROUND(12345.6789012345,8)"}]}]},Json)!.AsObject();
        source["transportPreservationWitness"]=string.Concat(Enumerable.Repeat("Unicode ₹ 名称 résumé; literal scientific identifier 9.87654321012E11; ",32000));
        await db.ExecuteAsync(@"INSERT INTO clients(Id,Code,Name,IsActive) VALUES(97744,'SYNTH-COMPRESS','Synthetic compressed design',TRUE);
INSERT INTO salarycomponents(Id,Code,Name,Category,Active) VALUES(9774401,'DESIGN_COMPRESS_97744','Synthetic design earning','Earning',TRUE);
INSERT INTO excel_payslip_batches(client_id,id,batch_json,send_state_json,created_at) VALUES(@Client,@Batch,CAST(@BatchJson AS JSON),JSON_OBJECT('retained','old mail history'),UTC_TIMESTAMP());
INSERT INTO excel_payslip_calculation_sources(client_id,batch_id,source_json,created_at) VALUES(@Client,@Batch,CAST(@SourceJson AS JSON),UTC_TIMESTAMP())",new{Client,Batch,BatchJson=batch.ToJsonString(),SourceJson=source.ToJsonString()});
    }
    public async Task DisposeAsync(){if(fixture is not null)await CleanupAsync();}
    private async Task CleanupAsync(){await using var db=await F.OpenAsync();await db.ExecuteAsync(@"DELETE FROM modulesettings WHERE client_id=97744;DELETE FROM paysliptemplates WHERE ClientId=97744;
DELETE FROM excel_payslip_calculation_sources WHERE client_id=97744;DELETE FROM excel_payslip_batches WHERE client_id=97744;DELETE FROM salarycomponents WHERE Id=9774401;DELETE FROM clients WHERE Id=97744;");}
    private static async Task<(string Batch,string? Source)> ReadAsync(MySqlConnection db,MySqlTransaction? tx=null,bool locked=false,string id=Batch)
    {
        var method=typeof(PayslipDesignRepository).GetMethod("SourceAsync",BindingFlags.Static|BindingFlags.NonPublic)!;
        var task=(Task)method.Invoke(null,[db,Client,id,tx,locked])!;await task;var result=task.GetType().GetProperty("Result")!.GetValue(task)!;
        return((string)result.GetType().GetProperty("BatchJson")!.GetValue(result)!, (string?)result.GetType().GetProperty("SourceJson")!.GetValue(result));
    }
    private async Task<string> WitnessAsync(){await using var db=await F.OpenAsync();return(await db.ExecuteScalarAsync<string>(@"SELECT CONCAT(SHA2(CAST(b.batch_json AS CHAR),256),SHA2(CAST(s.source_json AS CHAR),256),SHA2(CAST(b.send_state_json AS CHAR),256)) FROM excel_payslip_batches b JOIN excel_payslip_calculation_sources s ON s.client_id=b.client_id AND s.batch_id=b.id WHERE b.client_id=@Client AND b.id=@Batch",new{Client,Batch}))!;}

    [IsolatedFact]
    public async Task Compressed_design_source_retains_exact_database_JSON_text_numbers_Unicode_and_current_row_locks()
    {
        await using var db=await F.OpenAsync();var raw=await db.QuerySingleAsync<Plain>("SELECT CAST(b.batch_json AS CHAR) BatchJson,CAST(s.source_json AS CHAR) SourceJson FROM excel_payslip_batches b JOIN excel_payslip_calculation_sources s ON s.client_id=b.client_id AND s.batch_id=b.id WHERE b.client_id=@Client AND b.id=@Batch",new{Client,Batch});
        await using var tx=await db.BeginTransactionAsync();var decoded=await ReadAsync(db,tx,locked:true);
        Assert.Equal(raw.BatchJson,decoded.Batch);Assert.Equal(raw.SourceJson,decoded.Source);Assert.Equal(Encoding.UTF8.GetBytes(raw.SourceJson!),Encoding.UTF8.GetBytes(decoded.Source!));Assert.Contains("名称",decoded.Source);Assert.Contains("9.87654321012E11",decoded.Source);
        Assert.Equal(ExcelPayslipLegacyReview.SnapshotHash(raw.BatchJson,raw.SourceJson),ExcelPayslipLegacyReview.SnapshotHash(decoded.Batch,decoded.Source));
        await using var competing=await F.OpenAsync();await using var other=await competing.BeginTransactionAsync();
        foreach(var query in new[]{"SELECT id FROM excel_payslip_batches WHERE client_id=@Client AND id=@Batch FOR UPDATE NOWAIT","SELECT batch_id FROM excel_payslip_calculation_sources WHERE client_id=@Client AND batch_id=@Batch FOR UPDATE NOWAIT"})
        {var failure=await Assert.ThrowsAsync<MySqlException>(()=>competing.QuerySingleAsync<string>(query,new{Client,Batch},other));Assert.Equal(3572,failure.Number);}
        await other.RollbackAsync();await tx.RollbackAsync();
        Assert.Equal(Batch,await competing.QuerySingleAsync<string>("SELECT id FROM excel_payslip_batches WHERE client_id=@Client AND id=@Batch FOR UPDATE NOWAIT",new{Client,Batch}));
    }

    [IsolatedFact]
    public async Task Compressed_design_source_normal_preview_PDF_save_and_same_reference_replay_preserve_every_source_byte_and_mail()
    {
        var before=await WitnessAsync();var metadata=await Repository.PreviewAsync(new(){ClientId=Client,SourceBatchId=Batch});Assert.Single(metadata.Fields);Assert.Equal("給与 ₹ वेतन",metadata.Fields[0].Label);
        var request=new PayslipDesignSaveRequest{RequestId=Guid.NewGuid().ToString("N"),Template=new(){ClientId=Client,Name="Synthetic compressed transport",ShowYtd=false},SourceBatchId=Batch,SourceHash=metadata.SourceHash,Reviewed=true,Fields=metadata.Fields.Select(f=>new PayslipDesignField{SourceColumn=f.SourceColumn,Label=f.Label,Kind=f.Kind,NativeCode=Code}).ToList()};
        var sample=await Repository.PreviewPdfAsync(request);Assert.True(sample.Bytes.Length>1000);Assert.Equal(before,await WitnessAsync());
        var saved=await Repository.SaveAsync(request,Actor);Assert.True(saved.Id>0);Assert.Equal(0,request.Template.Id);Assert.Equal(saved.Id,(await Repository.SaveAsync(request,Actor)).Id);Assert.Equal(before,await WitnessAsync());
        var current=await Repository.PreviewAsync(new(){ClientId=Client,SourceBatchId=Batch});Assert.Equal(metadata.SourceHash,current.SourceHash);
        await using var db=await F.OpenAsync();Assert.Equal(1,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM paysliptemplates WHERE ClientId=@Client",new{Client}));Assert.Equal(2,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM modulesettings WHERE client_id=@Client",new{Client}));Assert.Equal(0,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employees WHERE ClientId=@Client",new{Client}));Assert.Equal(0,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM payruns WHERE ClientId=@Client",new{Client}));
    }

    [IsolatedFact]
    public async Task Compressed_design_source_preserves_optional_left_join_and_missing_client_batch_guards()
    {
        await using var db=await F.OpenAsync();await db.ExecuteAsync("DELETE FROM excel_payslip_calculation_sources WHERE client_id=@Client AND batch_id=@Batch",new{Client,Batch});
        var source=await ReadAsync(db);Assert.NotEmpty(source.Batch);Assert.Null(source.Source);
        var missing=await Assert.ThrowsAsync<InvalidOperationException>(()=>ReadAsync(db,id:"97744000000000000000000000000099"));Assert.Contains("does not belong",missing.Message);
        var optional=await Assert.ThrowsAsync<InvalidOperationException>(()=>Repository.PreviewAsync(new(){ClientId=Client,SourceBatchId=Batch}));Assert.Contains("no reviewed column mapping",optional.Message);
    }
    private sealed class Plain{public string BatchJson{get;set;}="";public string? SourceJson{get;set;}}
}
