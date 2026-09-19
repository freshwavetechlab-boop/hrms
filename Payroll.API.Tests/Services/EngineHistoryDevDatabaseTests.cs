using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;
using Xunit;

namespace Payroll.API.Tests.Services;

public sealed class EngineHistoryDevDatabaseTests
{
    private sealed class DevFactAttribute : FactAttribute
    {
        public DevFactAttribute() { if(Environment.GetEnvironmentVariable("ENGINE_HISTORY_DEV_DB_TEST") != "1") Skip="Explicit isolated DEV database opt-in required."; }
    }
    private sealed class Host : IHostEnvironment
    {
        public string EnvironmentName {get;set;}="Development";
        public string ApplicationName {get;set;}="EngineHistoryTest";
        public string ContentRootPath {get;set;}="";
        public IFileProvider ContentRootFileProvider {get;set;}=new NullFileProvider();
    }
    [DevFact]
    public async Task ActivityLogRetryPaginationRestartAndDeploymentIsolation()
    {
        var root=Environment.GetEnvironmentVariable("HRMS_SOURCE_ROOT") ?? throw new InvalidOperationException("Source root required.");
        var scope="test-activity-"+Guid.NewGuid().ToString("N");
        var config=new ConfigurationBuilder().SetBasePath(Path.Combine(root,"Payroll.API")).AddJsonFile("appsettings.json")
            .AddJsonFile("appsettings.Development.json").AddInMemoryCollection(new Dictionary<string,string?> { ["EngineHistory:Deployment"]=scope }).Build();
        var target=new MySqlConnectionStringBuilder(config.GetConnectionString("Default")!);
        Assert.Contains(target.Server,new[]{"localhost","127.0.0.1"});
        var store=new EngineActivityStore(config,new EngineHistoryStore(config,new Host()));
        var start=DateTime.UtcNow.AddMinutes(-20);
        var rows=Enumerable.Range(0,55).Select(i=>new EngineActivityRow { EngineCode="ats-scoring",Operation="Synthetic DEV telemetry only",
            StartedAtUtc=start.AddSeconds(i),CompletedAtUtc=start.AddSeconds(i+72),UpdatedAtUtc=start.AddSeconds(i+72),DurationMs=72000,
            AiDurationMs=71000,Status="Completed",Revision=2 }).ToList();
        await using var db=new MySqlConnection(config.GetConnectionString("Default"));await db.OpenAsync();
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            await store.SaveAsync(rows,timeout.Token);await store.SaveAsync(rows,timeout.Token);
            // Even a stale retry arriving after completion cannot resurrect Running.
            await store.SaveAsync([rows[0] with { Status="Running",CompletedAtUtc=null,DurationMs=null,Revision=1 }],timeout.Token);
            var reloaded=new EngineActivityStore(config,new EngineHistoryStore(config,new Host()));
            var first=await reloaded.ReadAsync(start.AddMinutes(-1),DateTime.UtcNow,"ats-scoring","Completed",null,null,timeout.Token);
            Assert.Equal(50,first.Items.Count);Assert.True(first.HasMore);
            var last=first.Items[^1];
            var second=await reloaded.ReadAsync(start.AddMinutes(-1),DateTime.UtcNow,"ats-scoring","Completed",last.StartedAtUtc,last.Id,timeout.Token);
            Assert.Equal(5,second.Items.Count);Assert.False(second.HasMore);
            Assert.Equal(55,first.Items.Concat(second.Items).Select(r=>r.Id).Distinct().Count());
            Assert.All(first.Items.Concat(second.Items),r=> { Assert.Equal(72000,r.DurationMs);Assert.Equal(71000,r.AiDurationMs);Assert.Equal(DateTimeKind.Utc,r.StartedAtUtc.Kind); });
            var stale=rows[0] with { Id=Guid.NewGuid().ToString("N"),Status="Running",CompletedAtUtc=null,DurationMs=null,Revision=1 };
            await store.SaveAsync([stale],timeout.Token);
            var interrupted=await reloaded.ReadAsync(start.AddMinutes(-1),DateTime.UtcNow,"","Interrupted",null,null,timeout.Token);
            Assert.Equal(stale.Id,Assert.Single(interrupted.Items).Id);Assert.Null(interrupted.Items[0].DurationMs);
            var isolatedConfig=new ConfigurationBuilder().AddConfiguration(config).AddInMemoryCollection(new Dictionary<string,string?>{["EngineHistory:Deployment"]=scope+"-other"}).Build();
            var isolated=new EngineActivityStore(isolatedConfig,new EngineHistoryStore(isolatedConfig,new Host()));
            Assert.Empty((await isolated.ReadAsync(start.AddMinutes(-1),DateTime.UtcNow,"","",null,null,timeout.Token)).Items);
        }
        finally { await db.ExecuteAsync("DELETE FROM engine_activity_log WHERE Deployment=@scope",new{scope}); }
    }
    [DevFact]
    public async Task RealDatabaseCheckpointRetriesRestartAndDateGroupingPreserveTotals()
    {
        var root=Environment.GetEnvironmentVariable("HRMS_SOURCE_ROOT") ?? throw new InvalidOperationException("Source root required.");
        var scope="test-history-"+Guid.NewGuid().ToString("N");
        var config=new ConfigurationBuilder().SetBasePath(Path.Combine(root,"Payroll.API"))
            .AddJsonFile("appsettings.json").AddJsonFile("appsettings.Development.json")
            .AddInMemoryCollection(new Dictionary<string,string?>{["EngineHistory:Deployment"]=scope}).Build();
        var prod=new ConfigurationBuilder().SetBasePath(Path.Combine(root,"Payroll.API")).AddJsonFile("appsettings.json").AddJsonFile("appsettings.Production.json",true).Build();
        var devTarget=new MySqlConnectionStringBuilder(config.GetConnectionString("Default")!);
        var prodTarget=new MySqlConnectionStringBuilder(prod.GetConnectionString("Default")!);
        Assert.NotEqual($"{prodTarget.Server}:{prodTarget.Port}/{prodTarget.Database}", $"{devTarget.Server}:{devTarget.Port}/{devTarget.Database}");
        Assert.Contains(devTarget.Server,new[]{"localhost","127.0.0.1"});
        var store=new EngineHistoryStore(config,new Host());
        var time=DateTime.UtcNow.Date.AddDays(-3);
        var rows=new[]{new EngineHistoryBucket(time,"resume-parser",2,1,40000,30000,30000,300000,1)};
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(40));
        await using var db=new MySqlConnection(config.GetConnectionString("Default"));
        await db.OpenAsync(timeout.Token);
        try
        {
            await store.SaveAsync(rows,timeout.Token); await store.SaveAsync(rows,timeout.Token);
            var restarted=new EngineHistoryStore(config,new Host());
            var history=await restarted.ReadAsync(time,time.AddDays(1),timeout.Token);
            var point=history.Engines.Single(e=>e.Code=="resume-parser").Trend[0];
            Assert.Equal(2,point.Requests); Assert.Equal(1,point.Failures);
            Assert.Equal(20000,point.AverageDurationMs); Assert.Equal(10,point.LoadPercent);
            Assert.Null(history.Engines.Single(e=>e.Code=="resume-parser").Trend[1].Requests);
            // A restarted worker has a new writer ID; its real work adds, without replaying old samples.
            await restarted.SaveAsync(new[]{rows[0] with {Completed=1,Failed=0,DurationMs=10000,MaxDurationMs=10000,BusyMs=10000}},timeout.Token);
            var wide=await store.ReadAsync(time.AddDays(-3),time.AddDays(1),timeout.Token);
            Assert.Equal(60,wide.BucketMinutes);
            var observed=Assert.Single(wide.Engines.Single(e=>e.Code=="resume-parser").Trend,p=>p.Requests!=null);
            Assert.Equal(3,observed.Requests); Assert.Equal(16666.7,observed.AverageDurationMs);
            Assert.Equal(6.67,observed.LoadPercent);
            // Hourly grouping must not leak records before a half-hour timezone boundary.
            var excluded=await store.ReadAsync(time.AddMinutes(30),time.AddDays(3),timeout.Token);
            Assert.All(excluded.Engines.SelectMany(e=>e.Trend),p=>Assert.Null(p.Requests));
        }
        finally
        {
            // Only the validated unique test deployment, never real telemetry/business data.
            await db.ExecuteAsync("DELETE FROM engine_metric_history WHERE Deployment=@scope",new{scope});
        }
    }
}
