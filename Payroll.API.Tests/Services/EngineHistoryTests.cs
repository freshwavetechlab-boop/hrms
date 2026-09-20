using Payroll.API.Services;
using Payroll.API.Models;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Payroll.API.Tests.Services;

public sealed class EngineHistoryTests
{
    [Theory]
    [InlineData(null,"super_admin",true,true)]
    [InlineData(null,"SUPER_ADMIN",true,true)]
    [InlineData(5,"super_admin",true,false)]
    [InlineData(5,"client_admin",true,false)]
    [InlineData(null,"system_admin",true,false)]
    [InlineData(null,"employee",true,false)]
    [InlineData(null,"super_admin",false,false)]
    public void HistoryCannotBroadenClientOrRoleAccess(int? client,string role,bool active,bool allowed)
    {
        Assert.Equal(allowed,EngineHistoryStore.CanRead(new AuthUser { ClientId=client,Roles=[role],IsActive=active,Permissions=["settings.manage","security.manage"] }));
    }
    [Fact]
    public void DisabledHistoryDoesNotCollectOrBufferWork()
    {
        var clock=new Clock();
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["EngineHistory:Enabled"]="false" }).Build();
        var history=new EngineHistoryCollector(clock,config);
        history.Start("resume-parser",1);clock.Advance(10);history.Complete("resume-parser",1,10000,false);
        Assert.Empty(history.Checkpoint());
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 9, 20, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(double seconds) => Now = Now.AddSeconds(seconds);
    }
    [Fact]
    public void ConcurrentWorkUsesUnionOccupancyAndCountsEachCompletionOnce()
    {
        var clock = new Clock(); var history = new EngineHistoryCollector(clock);
        history.Start("resume-parser", 1); clock.Advance(10);
        history.Start("resume-parser", 2); clock.Advance(10);
        history.Complete("resume-parser", 1, 20000, false); clock.Advance(10);
        history.Complete("resume-parser", 2, 20000, true);
        history.Complete("resume-parser", 2, 20000, true);
        var row = Assert.Single(history.Checkpoint(), r => r.EngineCode == "resume-parser");
        Assert.Equal(2, row.Completed); Assert.Equal(1, row.Failed);
        Assert.Equal(30000, row.BusyMs); Assert.Equal(30000, row.ObservedMs);
        Assert.Equal(40000, row.DurationMs); Assert.Equal(20000, row.MaxDurationMs);
    }
    [Fact]
    public void LongOperationSpansBucketsAndItsCompletionBelongsOnlyToLastBucket()
    {
        var clock = new Clock(); var history = new EngineHistoryCollector(clock);
        clock.Advance(290); history.Start("ats-scoring", 1); clock.Advance(30);
        history.Complete("ats-scoring", 1, 30000, false);
        var rows = history.Checkpoint().Where(r => r.EngineCode == "ats-scoring").OrderBy(r => r.BucketUtc).ToArray();
        Assert.Equal(2, rows.Length); Assert.Equal(10000, rows[0].BusyMs); Assert.Equal(20000, rows[1].BusyMs);
        Assert.Equal(0, rows[0].Completed); Assert.Equal(1, rows[1].Completed);
    }
    [Fact]
    public void RetriedCheckpointIsAbsoluteAndAckDoesNotLoseNewerWork()
    {
        var clock = new Clock(); var history = new EngineHistoryCollector(clock);
        history.Start("jd-parser", 1); clock.Advance(.104); history.Complete("jd-parser", 1,104,false);
        var first = history.Checkpoint();
        Assert.Equal(first, history.Checkpoint());
        history.Start("jd-parser",2); clock.Advance(1); history.Complete("jd-parser",2,1000,false);
        history.Acknowledge(first);
        var row = Assert.Single(history.Checkpoint(), r=>r.EngineCode=="jd-parser");
        Assert.Equal(2,row.Completed); Assert.Equal(1104,row.DurationMs);
    }
    [Fact]
    public void IdleTimeIsObservedButTimeBeforeProcessStartIsNotInvented()
    {
        var clock = new Clock(); clock.Advance(90); var history = new EngineHistoryCollector(clock);
        clock.Advance(10); var rows = history.Checkpoint();
        Assert.Equal(EngineRuntimeMonitor.EngineNames.Count,rows.Count); Assert.All(rows,r=> { Assert.Equal(10000,r.ObservedMs); Assert.Equal(0,r.BusyMs); Assert.Equal(0,r.Completed); });
        history.Acknowledge(rows); Assert.Empty(history.Checkpoint());
    }
    [Fact]
    public void DatabaseOutageBufferIsBoundedAndDroppedHistoryIsReported()
    {
        var clock = new Clock(); var history = new EngineHistoryCollector(clock);
        for(var i=0;i<180;i++) { clock.Advance(60); history.Checkpoint(); }
        Assert.InRange(history.Checkpoint().Count,1,EngineRuntimeMonitor.EngineNames.Count*13);
        Assert.True(history.DroppedBuckets>0);
    }
    [Fact]
    public void UnknownEngineAndDuplicateCompletionNeverEnterHistory()
    {
        var clock = new Clock(); var history = new EngineHistoryCollector(clock);
        history.Start("secret arbitrary payload",1); history.Complete("secret arbitrary payload",1,1,true);
        Assert.Empty(history.Checkpoint());
    }
    [Fact]
    public void MonitorRecordsActualOperationsNotStatusSnapshotPolls()
    {
        var clock = new Clock(); var history = new EngineHistoryCollector(clock);
        var monitor = new EngineRuntimeMonitor(history); var token=monitor.Start("ats-scoring");
        clock.Advance(1); monitor.Complete("ats-scoring",token,false);
        for(var i=0;i<300;i++) monitor.Snapshot();
        Assert.Equal(1,history.Checkpoint().Single(r=>r.EngineCode=="ats-scoring").Completed);
    }
}
