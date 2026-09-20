using Payroll.API.Models;

namespace Payroll.API.Services;

// Non-blocking business hooks; bounded in-memory retry buffer and batched writes.
public sealed class EngineActivityBuffer(TimeProvider clock, IConfiguration configuration)
{
    public bool Enabled { get; } = configuration.GetValue("EngineActivity:Enabled", true);
    private readonly object gate = new();
    private readonly Dictionary<(string Code,long Token),EngineActivityRow> rows = [];
    private readonly Dictionary<string,long> saved = [];
    private long revision;
    public long DroppedRecords { get; private set; }
    internal const int Limit = 5000;

    public void Start(string code,long token,EngineActivityContext? context)
    {
        if (!Enabled || !EngineRuntimeMonitor.EngineNames.ContainsKey(code)) return;
        context=EngineActivityMetadata.Clean(context ?? new EngineActivityContext(EngineRuntimeMonitor.EngineNames[code]));
        lock(gate)
        {
            if(rows.ContainsKey((code,token))) return;
            if(rows.Count>=Limit) { DroppedRecords++; return; }
            var now=clock.GetUtcNow().UtcDateTime;
            rows[(code,token)]=new EngineActivityRow { EngineCode=code,Operation=context.Operation,
                ReferenceType=context.ReferenceType,ReferenceId=context.ReferenceId,ApplicationId=context.ApplicationId,
                CandidateId=context.CandidateId,PositionId=context.PositionId,ClientId=context.ClientId,
                QueueWaitMs=context.QueueWaitMs,Attempt=context.Attempt,StartedAtUtc=now,UpdatedAtUtc=now,Revision=++revision };
        }
    }
    public void Complete(string code,long token,double duration,bool failed,int? httpStatus,string? outcome,string? failureCode=null)
    {
        if(!Enabled) return;
        lock(gate)
        {
            if(!rows.TryGetValue((code,token),out var row) || row.CompletedAtUtc.HasValue) return;
            var now=clock.GetUtcNow().UtcDateTime;
            var status=outcome is "Retry" or "Cancelled" or "NeedsReview" ? outcome
                : failed && failureCode is not null ? "Failed"
                : httpStatus is >=400 and <500 ? "Rejected" : failed ? "Failed" : "Completed";
            rows[(code,token)]=row with { CompletedAtUtc=now,UpdatedAtUtc=now,DurationMs=Finite(duration),
                Status=status,HttpStatus=httpStatus,FailureCode=failed || httpStatus>=400 ? EngineFailureCatalog.Clean(failureCode ?? (httpStatus.HasValue ? $"HTTP {httpStatus}" : null)) : "",Revision=++revision };
        }
    }
    public void AiPhase(string code,long token,double duration,string status)
    {
        lock(gate)
            if(rows.TryGetValue((code,token),out var row))
                rows[(code,token)]=row with { AiDurationMs=Finite(duration),AiStatus=EngineActivityMetadata.SafeAiStatus(status),Revision=++revision };
    }
    public IReadOnlyList<EngineActivityRow> Checkpoint()
    {
        if(!Enabled) return [];
        lock(gate)
        {
            var now=clock.GetUtcNow().UtcDateTime;
            foreach(var key in rows.Keys.ToArray())
            {
                var row=rows[key];
                if(row.CompletedAtUtc<now.AddHours(-1)) { rows.Remove(key);saved.Remove(row.Id);DroppedRecords++; }
                else if(!row.CompletedAtUtc.HasValue) rows[key]=row with { UpdatedAtUtc=now,Revision=++revision };
            }
            return rows.Values.Where(r=>r.Revision>saved.GetValueOrDefault(r.Id)).OrderBy(r=>saved.GetValueOrDefault(r.Id)).ThenBy(r=>r.UpdatedAtUtc).Take(250).ToList();
        }
    }
    public void Acknowledge(IReadOnlyList<EngineActivityRow> checkpoint)
    {
        lock(gate)
        {
            var versions=checkpoint.ToDictionary(r=>r.Id,r=>r.Revision);
            foreach(var key in rows.Keys.ToArray())
            {
                var row=rows[key];
                if(!versions.TryGetValue(row.Id,out var version)) continue;
                saved[row.Id]=Math.Max(saved.GetValueOrDefault(row.Id),version);
                if(row.CompletedAtUtc.HasValue && row.Revision==version) { rows.Remove(key);saved.Remove(row.Id); }
            }
        }
    }
    private static double? Finite(double value)=>double.IsFinite(value) && value>=0 ? value : null;
}
