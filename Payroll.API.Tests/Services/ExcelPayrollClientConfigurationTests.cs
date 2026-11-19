using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayrollClientConfigurationTests
{
    private sealed class OwnedDatabaseFactAttribute:FactAttribute
    {
        public OwnedDatabaseFactAttribute()
        {
            if(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HRMS_INTERNAL_INTERVIEW_TEST_CONNECTION")))
                Skip="An explicitly owned loopback MySQL fixture is required.";
        }
    }

    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private static ExcelPayslipCalculationSource Source(string label="Basic")=>new()
    {
        ColumnCount=2,Columns=[new(){ColumnIndex=0,SourceHeader=label,Kind="earning"},new(){ColumnIndex=1,SourceHeader="Net paid",Kind="netPay"}],
        Rows=[new(){SourceRow=2,Cells=[new(){Value="1234.56789"},new(){Value="1234.56789"}]}]
    };

    [OwnedDatabaseFact]
    public async Task CompatibleLocationConfigurationIsFoundBeyondSixReceiptsAndPageBoundaryWithoutWrites()
    {
        await using var fixture=await Fixture.CreateAsync();var db=fixture.Db;
        await AddAsync(db,"old-compatible","old-source",42,Source(),0);
        for(var index=1;index<=40;index++)await AddAsync(db,$"new-{index:D2}",$"source-{index:D2}",42,Source($"Unrelated location earning {index}"),index);
        var before=await WitnessAsync(db);var source=Source();source.Rows[0].Cells[0].Value="9876.54321";
        var result=await ExcelPayrollSetupRepository.ReadClientConfigurationAsync(db,42,source);
        Assert.NotNull(result);Assert.Equal("old-source",result.SavedRequest.BatchId);Assert.Equal("2026-10",result.Month);
        Assert.Equal(42,result.ClientId);Assert.Equal("Basic",result.SourceColumns[0].SourceHeader);
        Assert.Equal(before,await WitnessAsync(db));Assert.Equal("9876.54321",source.Rows[0].Cells[0].Value);
    }

    [OwnedDatabaseFact]
    public async Task NewestEligibleCompatibleSetupWinsAndDifferentClientsOrDraftsCannotSupplyTheBasis()
    {
        await using var fixture=await Fixture.CreateAsync();var db=fixture.Db;
        await AddAsync(db,"old","old-source",42,Source(),0);
        await AddAsync(db,"latest","latest-source",42,Source(),1);
        await AddAsync(db,"foreign","foreign-source",43,Source(),2);
        await AddAsync(db,"draft","draft-source",42,Source(),3,"Draft");
        await AddAsync(db,"correction","correction-source",42,Source(),4,correction:true);
        await AddAsync(db,"allocation","allocation-source",42,Source(),5,allocation:true);
        var result=await ExcelPayrollSetupRepository.ReadClientConfigurationAsync(db,42,Source());
        Assert.NotNull(result);Assert.Equal("latest-source",result.SavedRequest.BatchId);
        Assert.Null(await ExcelPayrollSetupRepository.ReadClientConfigurationAsync(db,99,Source()));
    }

    [OwnedDatabaseFact]
    public async Task ChangedRoleBlankMeaningAndComponentIdentityStillRequireFreshLayoutReview()
    {
        await using var fixture=await Fixture.CreateAsync();var db=fixture.Db;
        await AddAsync(db,"compatible","source",42,Source(),0);
        foreach(var change in new[]{"role","blank","component","header"})
        {
            var source=Source();
            if(change=="role")source.Columns[0].Kind="info";
            if(change=="blank")source.Columns[0].BlankAsZero=true;
            if(change=="component")source.Columns[0].ComponentId="different";
            if(change=="header")source.Columns[0].SourceHeader="Daily rate";
            Assert.Null(await ExcelPayrollSetupRepository.ReadClientConfigurationAsync(db,42,source));
        }
    }

    private static async Task AddAsync(MySqlConnection db,string id,string batchId,int clientId,ExcelPayslipCalculationSource source,int order,
        string status="Applied",bool correction=false,bool allocation=false)
    {
        var request=new ExcelPayrollSetupRequest{BatchId=batchId,TransferReviewVersion=1};
        if(correction)request.CorrectionOfImportId="parent";
        if(allocation)request.LocationAllocations=new();
        await db.ExecuteAsync("""
INSERT INTO excel_payslip_batches(id,client_id,batch_json) VALUES(@BatchId,@ClientId,CAST(@Batch AS JSON));
INSERT INTO excel_payslip_calculation_sources(batch_id,client_id,source_json) VALUES(@BatchId,@ClientId,CAST(@Source AS JSON));
INSERT INTO excel_payroll_setup_imports(id,batch_id,client_id,status,updated_at,request_json)
VALUES(@Id,@BatchId,@ClientId,@Status,@UpdatedAt,CAST(@Request AS JSON));
""",new{Id=id,BatchId=batchId,ClientId=clientId,Status=status,UpdatedAt=new DateTime(2026,10,1).AddMinutes(order),
            Batch=JsonSerializer.Serialize(new{Id=batchId,ClientId=clientId,Month="2026-10"},Json),Source=JsonSerializer.Serialize(source,Json),Request=JsonSerializer.Serialize(request,Json)});
    }

    private static async Task<string> WitnessAsync(MySqlConnection db)=>JsonSerializer.Serialize(new
    {
        Requests=await db.QueryAsync("SELECT id,client_id,status,updated_at,CAST(request_json AS CHAR) request_json FROM excel_payroll_setup_imports ORDER BY id"),
        Sources=await db.QueryAsync("SELECT batch_id,client_id,CAST(source_json AS CHAR) source_json FROM excel_payslip_calculation_sources ORDER BY batch_id"),
        Batches=await db.QueryAsync("SELECT id,client_id,CAST(batch_json AS CHAR) batch_json FROM excel_payslip_batches ORDER BY id")
    });

    private sealed class Fixture(MySqlConnection owner,MySqlConnection db,string name):IAsyncDisposable
    {
        public MySqlConnection Db=>db;
        public static async Task<Fixture> CreateAsync()
        {
            var connection=new MySqlConnectionStringBuilder(Environment.GetEnvironmentVariable("HRMS_INTERNAL_INTERVIEW_TEST_CONNECTION"));
            Assert.Contains(connection.Server,new[]{"127.0.0.1","localhost","::1"});Assert.Equal(33330u,connection.Port);
            connection.Database="";var owner=new MySqlConnection(connection.ConnectionString);await owner.OpenAsync();
            var name="hrms_excel_client_basis_test_"+Guid.NewGuid().ToString("N");
            await owner.ExecuteAsync($"CREATE DATABASE `{name}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci");
            connection.Database=name;var db=new MySqlConnection(connection.ConnectionString);await db.OpenAsync();
            await db.ExecuteAsync("""
CREATE TABLE excel_payroll_setup_imports(id VARCHAR(64) PRIMARY KEY,client_id INT,batch_id VARCHAR(64),status VARCHAR(20),updated_at DATETIME,request_json JSON);
CREATE TABLE excel_payslip_batches(id VARCHAR(64),client_id INT,batch_json JSON,PRIMARY KEY(id,client_id));
CREATE TABLE excel_payslip_calculation_sources(batch_id VARCHAR(64),client_id INT,source_json JSON,PRIMARY KEY(batch_id,client_id));
""");
            return new(owner,db,name);
        }
        public async ValueTask DisposeAsync()
        {
            await db.DisposeAsync();
            if(!System.Text.RegularExpressions.Regex.IsMatch(name,"^hrms_excel_client_basis_test_[a-f0-9]{32}$"))throw new InvalidOperationException("Unexpected test database.");
            await owner.ExecuteAsync($"DROP DATABASE `{name}`");await owner.DisposeAsync();
        }
    }
}
