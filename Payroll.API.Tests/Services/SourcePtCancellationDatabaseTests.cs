using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;
using Payroll.API.Tests.Fixtures;
using Xunit;

namespace Payroll.API.Tests.Services;

[Collection("ReviewedPayroll")]
public sealed class SourcePtCancellationDatabaseTests : IAsyncLifetime
{
    private const int Client=97939,Employee=9793901,Structure=9793900,Basic=9793901,Pt=9793902,SourceExtra=9793903,ManualExtra=9793904;
    private const string Batch="97939000000000000000000000000001";
    private readonly ReviewedPayrollFixture? fixture=Environment.GetEnvironmentVariable("FREVO_REVIEWED_PAYROLL_TEST_DB") is string connection?new(connection):null;
    private ReviewedPayrollFixture F=>fixture??throw new InvalidOperationException("Explicit owned synthetic payroll database required.");
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private sealed class IsolatedFactAttribute:FactAttribute
    {public IsolatedFactAttribute(){if(Environment.GetEnvironmentVariable("FREVO_REVIEWED_PAYROLL_TEST_DB") is null)Skip="Explicit sentinel-guarded loopback synthetic payroll fixture required.";}}
    public async Task InitializeAsync()
    {
        if(fixture is null)return;
        await F.InitializeAsync();await using var db=await F.OpenAsync();
        foreach(var field in new[]{"CreatedAt","UpdatedAt"})
            if(!await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='payrolladjustments' AND column_name=@field)",new{field}))
                await db.ExecuteAsync($"ALTER TABLE payrolladjustments ADD COLUMN `{field}` DATETIME DEFAULT CURRENT_TIMESTAMP");
        await Clean(db);
        await db.ExecuteAsync(@"
INSERT INTO clients(Id,Code,Name,IsActive) VALUES(97939,'SYNTH-PT-LIFECYCLE','Synthetic fee lifecycle',TRUE);
INSERT INTO salarycomponents(Id,Code,Name,Category,CalculationType,ValueText,Formula,StatutoryType,ProRata,Priority,Taxable) VALUES
(9793901,'SYNTH_BASIC','Synthetic basic','Earning','Fixed Amount','30000','','None',TRUE,100,TRUE),
(9793902,'PT','Synthetic existing fee','Deduction','Fixed Amount','100','','Professional Tax',FALSE,200,FALSE);
INSERT INTO salarystructures(Id,ClientId,Name,AnnualCtc,Active) VALUES(9793900,97939,'Synthetic fee template',360000,TRUE);
INSERT INTO salarystructurelines(StructureId,ComponentId,ValueText,CalculationType,ProRataOverride,SortOrder) VALUES
(9793900,'9793901','30000','Fixed Amount','true',1),(9793900,'9793902','100','Fixed Amount','false',2);
INSERT INTO payrolladjustments(Id,ClientId,EmployeeId,EmployeeCode,ComponentId,ComponentCode,ComponentName,AdjustmentType,Amount,PayPeriod,PayRunType,ReasonCode,Notes,Taxable,Status)
VALUES(9793903,97939,9793901,'SYNTH-PT-001',9793902,'PT','Synthetic imported fee','Deduction',100,'2026-10','Regular','EXCEL_SETUP',
'Reviewed salary Excel 97939000000000000000000000000001; import 97939000000000000000000000000002; source row 8',FALSE,'Approved');");
    }
    private static Task Clean(MySqlConnection db)=>db.ExecuteAsync(@"
DELETE FROM payrolladjustments WHERE ClientId=97939 AND Id IN(9793903,9793904);
DELETE FROM salarystructurelines WHERE StructureId=9793900;
DELETE FROM salarystructures WHERE Id=9793900 AND ClientId=97939;
DELETE FROM salarycomponents WHERE Id IN(9793901,9793902);
DELETE FROM clients WHERE Id=97939;");
    public async Task DisposeAsync(){if(fixture is not null){await using var db=await F.OpenAsync();await Clean(db);}}
    private static string Stable(PayrollAdjustment row)
    {var node=JsonSerializer.SerializeToNode(row,Json)!.AsObject();node.Remove("status");node.Remove("updatedAt");return node.ToJsonString();}
    private async Task<PayRunEmployee> Native()
    {
        await using var db=await F.OpenAsync();await using var tx=await db.BeginTransactionAsync();
        var setup=await PayrollDataTableStore.GetSetupJsonAsync(db,tx);
        var repository=typeof(PayRunRepository);var t=repository.GetNestedType("PayRunSourceEmployee",BindingFlags.NonPublic)!;var employee=Activator.CreateInstance(t)!;
        void Set(string key,object value)=>t.GetProperty(key)!.SetValue(employee,value);
        Set("Id",Employee);Set("ClientId",Client);Set("EmployeeCode","SYNTH-PT-001");Set("FirstName","Synthetic");Set("LastName","Person");
        Set("DateOfJoining","2026-01-01");Set("AnnualCtc",360000m);Set("SalaryStructureId",Structure.ToString());
        Set("SalaryJson",JsonSerializer.Serialize(new Dictionary<string,decimal>{{Basic.ToString(),30000},{Pt.ToString(),100}}));
        Set("SalaryComponents",new Dictionary<string,decimal>{{Basic.ToString(),30000},{Pt.ToString(),100}});
        var get=repository.GetMethod("GetApplicableAdjustmentsAsync",BindingFlags.NonPublic|BindingFlags.Static)!;
        var adjustments=await (Task<List<PayrollAdjustment>>)get.Invoke(null,[db,tx,Client,"2026-10","Regular",Array.Empty<int>(),Employee])!;
        var build=repository.GetMethod("BuildEmployee",BindingFlags.NonPublic|BindingFlags.Static)!;
        var result=(PayRunEmployee)build.Invoke(null,[0,employee,setup,"2026-10",30,(decimal?)30,30m,adjustments,0m,0m,-1m,false,"TDS"])!;
        await tx.RollbackAsync();return result;
    }
    [IsolatedFact]
    public async Task ExactSourceExtraCanBeSoftCancelledWhileNativeFeeAndOriginalRecordRemain()
    {
        await using var db=await F.OpenAsync();var old=await db.QuerySingleAsync<PayrollAdjustment>("SELECT * FROM payrolladjustments WHERE Id=@Id",new{Id=SourceExtra});
        Assert.Equal(29800m,(await Native()).NetPay);
        Assert.True(await F.Runs.CancelAdjustmentAsync(SourceExtra));
        var current=await db.QuerySingleAsync<PayrollAdjustment>("SELECT * FROM payrolladjustments WHERE Id=@Id",new{Id=SourceExtra});
        Assert.Equal("Cancelled",current.Status);Assert.Equal(100m,current.Amount);Assert.Equal(Stable(old),Stable(current));
        Assert.Equal(29900m,(await Native()).NetPay);
        Assert.Throws<SelectedPayRunConflictException>(()=>ExcelPayrollSetupRepository.FindReusableSourceAdjustment(Client,"2026-10",Batch,Employee,8,
            new(){Code="PT",Category="Deduction",IsPeriodAdjustment=true,Taxable=false},100,[current]));
    }
    [IsolatedFact]
    public async Task EqualCodeAndAmountDoNotAutomaticallyRemoveAnUnselectedManualExtra()
    {
        await using var db=await F.OpenAsync();await db.ExecuteAsync(@"INSERT INTO payrolladjustments(Id,ClientId,EmployeeId,ComponentId,ComponentCode,ComponentName,AdjustmentType,Amount,PayPeriod,PayRunType,ReasonCode,Notes,Taxable,Status)
VALUES(9793904,97939,9793901,9793902,'PT','Synthetic intentional extra','Deduction',100,'2026-10','Regular','MANUAL','Deliberate separate source charge',FALSE,'Approved')");
        Assert.Equal(29700m,(await Native()).NetPay);
        Assert.True(await F.Runs.CancelAdjustmentAsync(SourceExtra));
        var manual=await db.QuerySingleAsync<PayrollAdjustment>("SELECT * FROM payrolladjustments WHERE Id=@Id",new{Id=ManualExtra});
        Assert.Equal("Approved",manual.Status);Assert.Equal(100m,manual.Amount);Assert.Equal(29800m,(await Native()).NetPay);
    }
    [IsolatedFact]
    public async Task ExplicitReviewedReapprovalKeepsOriginalSourceRecordAndAppliedRecordStillCannotChange()
    {
        await using var db=await F.OpenAsync();
        await db.ExecuteAsync(@"INSERT INTO employees(Id,ClientId,EmployeeCode,FirstName,LastName,DateOfJoining,AnnualCtc,SalaryStructureId,SalaryJson,PersonalJson,PaymentJson,IsActive)
VALUES(9793901,97939,'SYNTH-PT-001','Synthetic','Person','2026-01-01',360000,'9793900',JSON_OBJECT(),JSON_OBJECT(),JSON_OBJECT(),TRUE);
UPDATE payrolladjustments SET EmployeeName='Synthetic Person' WHERE ClientId=97939 AND Id=9793903;");
        try
        {
            var original=await db.QuerySingleAsync<PayrollAdjustment>("SELECT * FROM payrolladjustments WHERE Id=@Id",new{Id=SourceExtra});
            Assert.True(await F.Runs.CancelAdjustmentAsync(SourceExtra));
            var cancelled=await db.QuerySingleAsync<PayrollAdjustment>("SELECT * FROM payrolladjustments WHERE Id=@Id",new{Id=SourceExtra});
            Assert.Throws<SelectedPayRunConflictException>(()=>ExcelPayrollSetupRepository.FindReusableSourceAdjustment(Client,"2026-10",Batch,Employee,8,
                new(){Code="PT",Category="Deduction",IsPeriodAdjustment=true,Taxable=false},100,[cancelled]));
            // This is an explicit operator action on the reviewed existing ID,
            // separate from automatic source-import reuse or creating a charge.
            cancelled.Status="Approved";
            var saved=await F.Runs.SaveAdjustmentAsync(cancelled);
            Assert.NotNull(saved);Assert.Equal(SourceExtra,saved!.Id);
            var restored=await db.QuerySingleAsync<PayrollAdjustment>("SELECT * FROM payrolladjustments WHERE Id=@Id",new{Id=SourceExtra});
            Assert.Equal("Approved",restored.Status);Assert.Equal(Stable(original),Stable(restored));
            Assert.Equal(1,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM payrolladjustments WHERE ClientId=@Client AND EmployeeId=@Employee AND ComponentCode='PT'",new{Client,Employee}));
            Assert.Equal(29800m,(await Native()).NetPay);
            await db.ExecuteAsync("UPDATE payrolladjustments SET Status='Applied' WHERE Id=@Id",new{Id=SourceExtra});
            var applied=await db.QuerySingleAsync<PayrollAdjustment>("SELECT * FROM payrolladjustments WHERE Id=@Id",new{Id=SourceExtra});
            Assert.Null(await F.Runs.SaveAdjustmentAsync(restored));
            var after=await db.QuerySingleAsync<PayrollAdjustment>("SELECT * FROM payrolladjustments WHERE Id=@Id",new{Id=SourceExtra});
            Assert.Equal(JsonSerializer.Serialize(applied,Json),JsonSerializer.Serialize(after,Json));
        }
        finally
        {
            await db.ExecuteAsync("DELETE FROM employees WHERE Id=@Employee AND ClientId=@Client",new{Employee,Client});
        }
    }

    [IsolatedFact]
    public async Task AppliedSourceAdjustmentCannotBeCancelledOrRewritten()
    {
        await using var db=await F.OpenAsync();await db.ExecuteAsync("UPDATE payrolladjustments SET Status='Applied' WHERE Id=@Id",new{Id=SourceExtra});
        var before=await db.QuerySingleAsync<PayrollAdjustment>("SELECT * FROM payrolladjustments WHERE Id=@Id",new{Id=SourceExtra});
        Assert.False(await F.Runs.CancelAdjustmentAsync(SourceExtra));
        var after=await db.QuerySingleAsync<PayrollAdjustment>("SELECT * FROM payrolladjustments WHERE Id=@Id",new{Id=SourceExtra});
        Assert.Equal(JsonSerializer.Serialize(before,Json),JsonSerializer.Serialize(after,Json));
    }
}
