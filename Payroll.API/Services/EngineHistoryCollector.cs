using Payroll.API.Models;

namespace Payroll.API.Services;

// O(engines) memory normally; bounded to one hour during a storage outage.
// Advance splits wall-clock occupancy across buckets and unions concurrent work.
public sealed class EngineHistoryCollector(TimeProvider clock, IConfiguration? configuration = null)
{
    private readonly bool enabled = configuration?.GetValue("EngineHistory:Enabled", true) ?? true;
    private sealed class State(DateTime now)
    {
        public DateTime Cursor = now;
        public HashSet<long> Active = [];
    }
    private readonly object gate = new();
    private readonly Dictionary<string, State> states = EngineRuntimeMonitor.EngineNames.Keys.ToDictionary(code => code, _ => new State(clock.GetUtcNow().UtcDateTime));
    private readonly Dictionary<(string Code, DateTime Time), EngineHistoryBucket> buckets = [];
    private readonly Dictionary<(string Code, DateTime Time), long> saved = [];
    private long revision;
    public long DroppedBuckets { get; private set; }
    public static DateTime Floor(DateTime value, int minutes = 5) => new(value.Ticks / TimeSpan.FromMinutes(minutes).Ticks * TimeSpan.FromMinutes(minutes).Ticks, DateTimeKind.Utc);

    public void Start(string code, long token)
    {
        if (!enabled) return;
        lock (gate)
        {
            if (!states.TryGetValue(code, out var state)) return;
            Advance(code, state, clock.GetUtcNow().UtcDateTime);
            state.Active.Add(token);
        }
    }
    public void Complete(string code, long token, double durationMs, bool failed)
    {
        if (!enabled) return;
        lock (gate)
        {
            if (!states.TryGetValue(code, out var state) || !state.Active.Contains(token)) return;
            var now = clock.GetUtcNow().UtcDateTime;
            Advance(code, state, now);
            state.Active.Remove(token);
            var key = (code, Floor(state.Cursor));
            var row = Row(key);
            var duration = double.IsFinite(durationMs) ? Math.Max(0, durationMs) : 0;
            buckets[key] = row with { Completed = row.Completed + 1, Failed = row.Failed + (failed ? 1 : 0),
                DurationMs = row.DurationMs + duration, MaxDurationMs = Math.Max(row.MaxDurationMs, duration), Revision = ++revision };
        }
    }
    public List<EngineHistoryBucket> Checkpoint()
    {
        if (!enabled) return [];
        lock (gate)
        {
            var now = clock.GetUtcNow().UtcDateTime;
            foreach (var (code, state) in states) Advance(code, state, now);
            foreach (var key in buckets.Keys.Where(k => k.Time < Floor(now.AddHours(-1))).ToArray())
            {
                if (buckets[key].Revision > saved.GetValueOrDefault(key)) DroppedBuckets++;
                buckets.Remove(key); saved.Remove(key);
            }
            return buckets.Where(pair => pair.Value.Revision > saved.GetValueOrDefault(pair.Key)).Select(pair => pair.Value).ToList();
        }
    }
    public void Acknowledge(IEnumerable<EngineHistoryBucket> persisted)
    {
        lock (gate)
        {
            var current = Floor(clock.GetUtcNow().UtcDateTime);
            foreach (var row in persisted)
            {
                var key = (row.EngineCode, row.BucketUtc);
                if (!buckets.TryGetValue(key, out var actual)) continue;
                saved[key] = Math.Max(saved.GetValueOrDefault(key), row.Revision);
                if (row.BucketUtc < current && actual.Revision == row.Revision) { buckets.Remove(key); saved.Remove(key); }
            }
        }
    }
    private EngineHistoryBucket Row((string Code, DateTime Time) key) => buckets.GetValueOrDefault(key)
        ?? new(key.Time, key.Code, 0, 0, 0, 0, 0, 0, 0);
    private void Advance(string code, State state, DateTime now)
    {
        if (now <= state.Cursor) return; // Clock rollback never creates negative occupancy.
        if (state.Cursor < now.AddHours(-1)) { DroppedBuckets++; state.Cursor = now.AddHours(-1); }
        while (state.Cursor < now)
        {
            var key = (code, Floor(state.Cursor));
            var end = key.Item2.AddMinutes(5) < now ? key.Item2.AddMinutes(5) : now;
            var elapsed = (end - state.Cursor).TotalMilliseconds;
            var row = Row(key);
            buckets[key] = row with { ObservedMs = row.ObservedMs + elapsed,
                BusyMs = row.BusyMs + (state.Active.Count > 0 ? elapsed : 0), Revision = ++revision };
            state.Cursor = end;
        }
    }
}
