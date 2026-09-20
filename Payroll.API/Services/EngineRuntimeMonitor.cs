using System.Collections.Concurrent;
using System.Diagnostics;
using Payroll.API.Models;

namespace Payroll.API.Services;

public sealed class EngineRuntimeMonitor
{
    private sealed record Definition(string Code, string Name, string Category, string Description, string Coverage = "Request traffic");
    private sealed record Sample(DateTime RecordedAtUtc, double DurationMs, bool Failed, string Error);

    private sealed class State
    {
        public int ActiveRequests;
        public ConcurrentDictionary<long, DateTime> ActiveStarts { get; } = new();
        public ConcurrentQueue<Sample> Samples { get; } = new();
        public string LastError { get; set; } = string.Empty;
    }

    private static readonly Definition[] Definitions =
    [
        new("frevopilot", "FrevoPilot", "AI & analytics", "Dashboard workflow duration, including provider and database waits. Busy time is not CPU utilization.", "Dashboard workflows"),
        new("resume-parser", "Resume Parser", "Talent acquisition", "Resume preview and intake requests plus public-upload and background parsing on this API instance.", "Requests + public parser work"),
        new("jd-parser", "JD / Hiring Parser", "Talent acquisition", "Hiring-request and job-description source parsing workload."),
        new("ats-scoring", "ATS Scoring", "Talent acquisition", "Actual manual and automatic ATS scoring jobs, including background work.", "Scoring worker"),
        new("payroll", "Payroll Processor", "Payroll", "Payroll commands and actual queued payroll processing.", "Requests + payroll worker"),
        new("bulk-data", "Bulk Data Jobs", "Platform", "Bulk import/export commands and actual attendance batch processing.", "Requests + attendance batches"),
        new("notifications", "Notification Delivery", "Communication", "Notification/invite commands and actual queued email delivery.", "Requests + email delivery"),
        new("documents", "Documents & Storage", "Platform", "Attachment, document generation and storage mutation workload."),
        new("internal-interview", "Internal Interviews", "Talent acquisition", "Local interview speech and AI inference work. Busy time is not CPU utilization.", "Speech + local AI"),
    ];

    private readonly ConcurrentDictionary<string, State> states = new(StringComparer.OrdinalIgnoreCase);
    private readonly object processLock = new();
    private DateTime lastProcessSampleUtc = DateTime.UtcNow;
    private TimeSpan lastProcessorTime = Process.GetCurrentProcess().TotalProcessorTime;
    private decimal lastCpuPercent;
    private readonly SemaphoreSlim jobSnapshotLock = new(1, 1);
    private DateTime jobSnapshotAt;
    private List<EngineJobObservation> jobSnapshot = [];
    private readonly EngineHistoryCollector? history;
    private readonly EngineActivityBuffer? activity;
    public static IReadOnlyDictionary<string, string> EngineNames { get; } = Definitions.ToDictionary(d => d.Code, d => d.Name);

    public EngineRuntimeMonitor(EngineHistoryCollector? history = null, EngineActivityBuffer? activity = null)
    {
        this.history = history;
        this.activity = activity;
        foreach (var definition in Definitions) states.TryAdd(definition.Code, new State());
    }

    public long Start(string code, EngineActivityContext? context = null)
    {
        var state = states.GetOrAdd(code, _ => new State());
        var token = Stopwatch.GetTimestamp();
        while (!state.ActiveStarts.TryAdd(token, DateTime.UtcNow)) token++;
        Interlocked.Increment(ref state.ActiveRequests);
        try { history?.Start(code, token); } catch { /* History never gates work. */ }
        try { activity?.Start(code, token, context); } catch { /* Logs never gate work. */ }
        return token;
    }

    public Observation Observe(string code, EngineActivityContext? context = null) => new(this, code, context);

    // Use only for service work without an enclosing observation for this engine.
    // Completion describes the operation, not document quality or AI success.
    public async Task<T> ObserveAsync<T>(string code, Func<Task<T>> work, Func<T, bool>? succeeded = null, EngineActivityContext? context = null)
    {
        using var observation = Observe(code, context);
        var result = await work();
        try { observation.Succeeded = succeeded?.Invoke(result) ?? true; }
        catch { observation.Succeeded = true; } // Telemetry classification cannot replace the result.
        return result;
    }

    public sealed class Observation : IDisposable
    {
        private readonly EngineRuntimeMonitor monitor;
        private readonly string code;
        private readonly long? token;
        private int disposed;
        public bool Succeeded { get; set; }
        public string? Outcome { get; set; }
        public void RecordAiPhase(double durationMs, string status)
        {
            try { if(token.HasValue) monitor.activity?.AiPhase(code,token.Value,durationMs,status); } catch { /* Telemetry only. */ }
        }
        internal Observation(EngineRuntimeMonitor monitor, string code, EngineActivityContext? context)
        {
            this.monitor = monitor;
            this.code = code;
            try { token = monitor.Start(code, context); } catch { /* Never block business work. */ }
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0 || token is null) return;
            try { monitor.Complete(code, token.Value, !Succeeded, "Engine operation failed; check its result or job status.", outcome: Outcome); }
            catch { /* Never replace the underlying result. */ }
        }
    }

    public void Complete(string code, long startedAt, bool failed, string? error = null, int? httpStatus = null, string? outcome = null)
    {
        var state = states.GetOrAdd(code, _ => new State());
        if (!state.ActiveStarts.TryRemove(startedAt, out _)) return;
        Interlocked.Decrement(ref state.ActiveRequests);
        var sample = new Sample(DateTime.UtcNow, Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds, failed, failed ? (error ?? "Request failed.") : string.Empty);
        state.Samples.Enqueue(sample);
        try { history?.Complete(code, startedAt, sample.DurationMs, failed); } catch { /* History never gates work. */ }
        try { activity?.Complete(code, startedAt, sample.DurationMs, failed, httpStatus, outcome); } catch { /* Logs never gate work. */ }
        if (failed) state.LastError = sample.Error.Length > 300 ? sample.Error[..300] : sample.Error;
        Trim(state, sample.RecordedAtUtc.AddMinutes(-10));
    }

    public EngineMonitoringSnapshot Snapshot()
    {
        var now = DateTime.UtcNow;
        var snapshot = new EngineMonitoringSnapshot { GeneratedAtUtc = now, Process = ProcessSnapshot(now) };
        snapshot.Engines = Definitions.Select(definition => BuildMetric(definition, states[definition.Code], now)).ToList();
        return snapshot;
    }

    public async Task<EngineMonitoringSnapshot> SnapshotWithJobsAsync(Func<CancellationToken, Task<List<EngineJobObservation>>> loadJobs, CancellationToken cancellationToken)
    {
        var snapshot = Snapshot();
        try
        {
            await jobSnapshotLock.WaitAsync(cancellationToken);
            try
            {
                if (DateTime.UtcNow - jobSnapshotAt > TimeSpan.FromSeconds(1))
                {
                    jobSnapshot = await loadJobs(cancellationToken);
                    jobSnapshotAt = DateTime.UtcNow;
                }
                var state = new State();
                long index = 0;
                foreach (var job in jobSnapshot.OrderBy(job => job.CompletedAt ?? job.UpdatedAt))
                {
                    if (!job.StartedAt.HasValue) continue;
                    var start = DateTime.SpecifyKind(job.StartedAt.Value, DateTimeKind.Utc);
                    if (job.Status == "Processing") { state.ActiveStarts.TryAdd(++index, start); state.ActiveRequests++; }
                    else if (job.Status is "Completed" or "Failed" or "Retry")
                    {
                        var end = DateTime.SpecifyKind(job.CompletedAt ?? job.UpdatedAt, DateTimeKind.Utc);
                        var failed = job.Status != "Completed";
                        state.Samples.Enqueue(new Sample(end, Math.Max(0, (end - start).TotalMilliseconds), failed, ""));
                    }
                }
                // Database observations include work claimed by another API replica.
                var metric = BuildMetric(Definitions.Single(d => d.Code == "ats-scoring"), state, snapshot.GeneratedAtUtc);
                metric.Coverage = "Shared ATS queue · latest 2,000 jobs";
                metric.QueuedRequests = jobSnapshot.Count(j => j.Status is "Queued" or "Retry");
                metric.LastError = state.Samples.Any(s => s.Failed) ? "A scoring job needs attention. Check the application's ATS status." : "";
                snapshot.Engines[snapshot.Engines.FindIndex(e => e.Code == "ats-scoring")] = metric;
            }
            finally { jobSnapshotLock.Release(); }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            snapshot.Engines.Single(e => e.Code == "ats-scoring").Coverage = "Local worker only · shared queue unavailable";
        }
        return snapshot;
    }

    public static string? Classify(string method, string path)
    {
        var value = path.ToLowerInvariant();
        var mutation = !HttpMethods.IsGet(method) && !HttpMethods.IsHead(method) && !HttpMethods.IsOptions(method);
        // ExecuteAsync measures the full dashboard workflow. Counting its start
        // request or status polls again would dilute failures and job latency.
        if (value.StartsWith("/api/frevopilot")) return null;
        // These commands resume paused workflows; they do not parse a CV.
        if (value.EndsWith("/resume") && (value.StartsWith("/api/recruitment/hiring-cases/")
            || value.StartsWith("/api/recruitment-orchestration/applications/"))) return null;
        if (mutation && (value.Contains("/resume-intake") || value.EndsWith("/resume") || value.Contains("/public/") && value.Contains("resume"))) return "resume-parser";
        if (mutation && value.Contains("/parse-source")) return "jd-parser";
        // ATS is measured at the job boundary, not at the enqueue/wait HTTP request.
        if (mutation && value.StartsWith("/api/pay-runs")) return "payroll";
        if (mutation && (value.Contains("/import") || value.Contains("/bulk"))) return "bulk-data";
        if (mutation && (value.StartsWith("/api/notifications") || value.EndsWith("/invite"))) return "notifications";
        if (mutation && (value.Contains("attachment") || value.Contains("storage-server") || value.Contains("generate") && value.Contains("document"))) return "documents";
        return null;
    }

    private static EngineRuntimeMetric BuildMetric(Definition definition, State state, DateTime now)
    {
        Trim(state, now.AddMinutes(-10));
        var samples = state.Samples.ToArray();
        var recent = samples.Where(item => item.RecordedAtUtc >= now.AddMinutes(-5)).ToArray();
        var durations = recent.Select(item => item.DurationMs).OrderBy(value => value).ToArray();
        var failures = recent.Count(item => item.Failed);
        var active = Math.Max(0, Volatile.Read(ref state.ActiveRequests));
        var load = BusyPercent(samples, state.ActiveStarts.Values.ToArray(), now.AddSeconds(-30), now);
        var last = samples.LastOrDefault();
        return new EngineRuntimeMetric
        {
            Code = definition.Code,
            Name = definition.Name,
            Category = definition.Category,
            Description = definition.Description,
            Coverage = definition.Coverage,
            State = failures > 0 ? "Attention" : active > 0 ? "Active" : recent.Length > 0 ? "Observed" : "Idle",
            LoadPercent = load,
            ActiveRequests = active,
            RequestsLastFiveMinutes = recent.Length,
            SuccessRate = recent.Length == 0 ? 100 : Math.Round((decimal)(recent.Length - failures) * 100 / recent.Length, 1),
            AverageDurationMs = durations.Length == 0 ? 0 : Math.Round((decimal)durations.Average(), 1),
            P95DurationMs = durations.Length == 0 ? 0 : Math.Round((decimal)durations[Math.Min(durations.Length - 1, (int)Math.Ceiling(durations.Length * .95) - 1)], 1),
            LastActivityUtc = last?.RecordedAtUtc,
            LastError = state.LastError,
            Trend = BuildTrend(samples, state.ActiveStarts.Values.ToArray(), now),
        };
    }

    private static List<EngineTrendPoint> BuildTrend(Sample[] samples, DateTime[] activeStarts, DateTime now)
    {
        var start = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second / 30 * 30, DateTimeKind.Utc).AddMinutes(-9.5);
        return Enumerable.Range(0, 20).Select(index =>
        {
            var from = start.AddSeconds(index * 30);
            var bucket = samples.Where(item => item.RecordedAtUtc >= from && item.RecordedAtUtc < from.AddSeconds(30)).ToArray();
            return new EngineTrendPoint
            {
                TimeUtc = from,
                Requests = bucket.Length,
                Failures = bucket.Count(item => item.Failed),
                AverageDurationMs = bucket.Length == 0 ? 0 : Math.Round((decimal)bucket.Average(item => item.DurationMs), 1),
                LoadPercent = BusyPercent(samples, activeStarts, from, from.AddSeconds(30), now),
            };
        }).ToList();
    }

    private EngineProcessMetric ProcessSnapshot(DateTime now)
    {
        using var process = Process.GetCurrentProcess();
        lock (processLock)
        {
            var elapsed = (now - lastProcessSampleUtc).TotalMilliseconds;
            var processorDelta = (process.TotalProcessorTime - lastProcessorTime).TotalMilliseconds;
            if (elapsed > 250)
            {
                lastCpuPercent = Math.Round((decimal)Math.Clamp(processorDelta / (elapsed * Environment.ProcessorCount) * 100, 0, 100), 1);
                lastProcessSampleUtc = now;
                lastProcessorTime = process.TotalProcessorTime;
            }
            return new EngineProcessMetric
            {
                CpuPercent = lastCpuPercent,
                WorkingSetMb = Math.Round((decimal)process.WorkingSet64 / 1024 / 1024, 1),
                ManagedMemoryMb = Math.Round((decimal)GC.GetTotalMemory(false) / 1024 / 1024, 1),
                ThreadCount = process.Threads.Count,
                UptimeSeconds = Math.Max(0, (long)(now - process.StartTime.ToUniversalTime()).TotalSeconds),
            };
        }
    }

    private static int BusyPercent(Sample[] samples, DateTime[] activeStarts, DateTime from, DateTime until, DateTime? now = null)
    {
        var intervals = samples.Select(s => (Start: s.RecordedAtUtc.AddMilliseconds(-s.DurationMs), End: s.RecordedAtUtc))
            .Concat(activeStarts.Select(start => (Start: start, End: now ?? until)))
            .Where(i => i.End > from && i.Start < until).OrderBy(i => i.Start);
        var cursor = from;
        double busyMs = 0;
        foreach (var interval in intervals)
        {
            var start = interval.Start > cursor ? interval.Start : cursor;
            var end = interval.End < until ? interval.End : until;
            if (end <= start) continue;
            busyMs += (end - start).TotalMilliseconds;
            cursor = end;
        }
        return (int)Math.Round(Math.Clamp(busyMs / (until - from).TotalMilliseconds * 100, 0, 100));
    }

    private static void Trim(State state, DateTime cutoff)
    {
        while (state.Samples.TryPeek(out var sample) && (sample.RecordedAtUtc < cutoff || state.Samples.Count > 10000)) state.Samples.TryDequeue(out _);
    }
}
