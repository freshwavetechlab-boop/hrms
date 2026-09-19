using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Configuration;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class EngineActivityTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now=DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow()=>Now;
    }
    private static IConfiguration Config(bool enabled=true)=>new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["EngineActivity:Enabled"]=enabled.ToString() }).Build();

    [Fact]
    public void AllEightEnginesRetainMeasuredTaskDurations()
    {
        var clock=new Clock();var buffer=new EngineActivityBuffer(clock,Config());
        var index=0L;
        foreach(var code in EngineRuntimeMonitor.EngineNames.Keys)
        {
            buffer.Start(code,++index,new EngineActivityContext("Test operation"));
            clock.Now=clock.Now.AddSeconds(72);
            buffer.Complete(code,index,72000,false,null,null);
        }
        var rows=buffer.Checkpoint();Assert.Equal(8,rows.Count);
        Assert.All(rows,r=> { Assert.Equal("Completed",r.Status);Assert.Equal(72000,r.DurationMs);Assert.Equal(TimeSpan.FromSeconds(72),r.CompletedAtUtc-r.StartedAtUtc); });
    }
    [Fact]
    public void StartCheckpointLateAckCannotEraseCompletionOrAiTiming()
    {
        var buffer=new EngineActivityBuffer(new Clock(),Config());
        buffer.Start("ats-scoring",1,new EngineActivityContext("ATS",ApplicationId:73,QueueWaitMs:3000,Attempt:2));
        var first=buffer.Checkpoint();buffer.AiPhase("ats-scoring",1,72000,"Completed");buffer.Complete("ats-scoring",1,75000,false,null,null);
        buffer.Acknowledge(first);var completed=Assert.Single(buffer.Checkpoint());
        Assert.Equal(75000,completed.DurationMs);Assert.Equal(72000,completed.AiDurationMs);Assert.Equal(3000,completed.QueueWaitMs);Assert.Equal(73,completed.ApplicationId);
        Assert.Equal(first[0].Id,completed.Id);
        buffer.Acknowledge([completed]);Assert.Empty(buffer.Checkpoint());
        buffer.Complete("ats-scoring",1,75000,false,null,null);Assert.Empty(buffer.Checkpoint());
    }
    [Theory]
    [InlineData(400,false,null,"Rejected")]
    [InlineData(403,false,null,"Rejected")]
    [InlineData(500,true,null,"Failed")]
    [InlineData(null,true,"Cancelled","Cancelled")]
    [InlineData(null,true,"Retry","Retry")]
    public void OutcomesAreNotFalseSuccess(int? http,bool failed,string? outcome,string expected)
    {
        var buffer=new EngineActivityBuffer(new Clock(),Config());buffer.Start("documents",1,null);buffer.Complete("documents",1,5,failed,http,outcome);
        Assert.Equal(expected,Assert.Single(buffer.Checkpoint()).Status);
    }
    [Fact]
    public void PublicTokensQueryStringsAndRequestBodiesNeverEnterMetadata()
    {
        var context=new DefaultHttpContext();context.Request.Method="POST";context.Request.Path="/api/public/form/private-secret/resume";context.Request.QueryString=new QueryString("?key=secret-value");
        context.SetEndpoint(new RouteEndpoint(_=>Task.CompletedTask,RoutePatternFactory.Parse("/api/public/form/{token}/resume"),0,EndpointMetadataCollection.Empty,"test"));
        context.Request.RouteValues["token"]="private-secret";
        var metadata=EngineActivityMetadata.ForRequest(context);
        Assert.Equal("POST /api/public/form/{token}/resume",metadata.Operation);Assert.Empty(metadata.ReferenceId);
        Assert.DoesNotContain("private-secret",System.Text.Json.JsonSerializer.Serialize(metadata));
        Assert.DoesNotContain("secret-value",System.Text.Json.JsonSerializer.Serialize(metadata));
        Assert.Equal("Unavailable",EngineActivityMetadata.SafeAiStatus("HTTP 429 api_key=secret"));
    }
    [Fact]
    public void DisabledRecordingAndUnknownEnginesRemainEmpty()
    {
        var buffer=new EngineActivityBuffer(new Clock(),Config(false));buffer.Start("ats-scoring",1,null);Assert.Empty(buffer.Checkpoint());
        buffer=new EngineActivityBuffer(new Clock(),Config());buffer.Start("secret/arbitrary-engine",1,null);Assert.Empty(buffer.Checkpoint());
    }
    [Fact]
    public void BufferIsBoundedAndEveryActiveRowGetsAHeartbeatTurn()
    {
        var buffer=new EngineActivityBuffer(new Clock(),Config());
        for(var i=0;i<EngineActivityBuffer.Limit+1;i++) buffer.Start("bulk-data",i,null);
        Assert.Equal(1,buffer.DroppedRecords);var ids=new HashSet<string>();
        for(var i=0;i<20;i++) { var batch=buffer.Checkpoint();foreach(var row in batch) ids.Add(row.Id);buffer.Acknowledge(batch); }
        Assert.Equal(EngineActivityBuffer.Limit,ids.Count);
    }
    [Fact]
    public void StorageOutageCannotGrowCompletedBufferIndefinitely()
    {
        var clock=new Clock();var buffer=new EngineActivityBuffer(clock,Config());buffer.Start("jd-parser",1,null);buffer.Complete("jd-parser",1,5,false,null,null);
        clock.Now=clock.Now.AddHours(2);Assert.Empty(buffer.Checkpoint());Assert.Equal(1,buffer.DroppedRecords);
    }
    [Fact]
    public void SnapshotPollingNeverCreatesTaskRowsAndErrorsStaySanitized()
    {
        var buffer=new EngineActivityBuffer(new Clock(),Config());var monitor=new EngineRuntimeMonitor(activity:buffer);
        var token=monitor.Start("frevopilot");monitor.Complete("frevopilot",token,true,"secret prompt content");
        for(var i=0;i<100;i++) monitor.Snapshot();
        var row=Assert.Single(buffer.Checkpoint());Assert.DoesNotContain("secret",row.FailureCode);
    }
    [Fact]
    public async Task MalformedTelemetryCannotReplaceBusinessResultOrError()
    {
        var buffer=new EngineActivityBuffer(new Clock(),Config());
        var monitor=new EngineRuntimeMonitor(activity:buffer);
        var invalidMetadata=new EngineActivityContext(null!);
        Assert.Equal(42,await monitor.ObserveAsync("resume-parser",()=>Task.FromResult(42),context:invalidMetadata));
        var businessError=new InvalidOperationException("Business operation failed");
        var actual=await Assert.ThrowsAsync<InvalidOperationException>(()=>monitor.ObserveAsync<int>("resume-parser",()=>Task.FromException<int>(businessError),context:invalidMetadata));
        Assert.Same(businessError,actual);
        Assert.Equal(0,monitor.Snapshot().Engines.Single(e=>e.Code=="resume-parser").ActiveRequests);
    }
    [Fact]
    public void InvalidRangeEngineStatusAndCursorAreRejected()
    {
        var until=DateTimeOffset.UtcNow;var from=until.AddDays(-1);
        Assert.Throws<ArgumentException>(()=>EngineActivityStore.Validate(from,until,"injected","",null,null));
        Assert.Throws<ArgumentException>(()=>EngineActivityStore.Validate(from,until,"","MadeUp",null,null));
        Assert.Throws<ArgumentException>(()=>EngineActivityStore.Validate(from,until,"","",until,"not-a-guid"));
        Assert.Throws<ArgumentException>(()=>EngineActivityStore.Validate(until,from,"","",null,null));
    }
}
