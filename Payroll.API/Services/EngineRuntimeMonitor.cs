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
        public ConcurrentQueue<Sample> Samples { get; } = new();
        public string LastError { get; set; } = string.Empty;
    }

    private static readonly Definition[] Definitions =
    [
        new("frevopilot", "FrevoPilot", "AI & analytics", "Chat storage/API activity. Dashboard analytics remains external until production integration.", "Partial · API traffic"),
        new("resume-parser", "Resume Parser", "Talent acquisition", "Resume preview, extraction and candidate intake workload."),
        new("jd-parser", "JD / Hiring Parser", "Talent acquisition", "Hiring-request and job-description source parsing workload."),
        new("ats-scoring", "ATS Scoring", "Talent acquisition", "Manual and automatic ATS evaluation requests."),
        new("payroll", "Payroll Processor", "Payroll", "Payroll creation, calculation and lifecycle command workload."),
        new("bulk-data", "Bulk Data Jobs", "Platform", "Bulk import and export request workload."),
        new("notifications", "Notification Delivery", "Communication", "Notification, invite and delivery request workload."),
        new("documents", "Documents & Storage", "Platform", "Attachment, document generation and storage mutation workload."),
    ];

    private readonly ConcurrentDictionary<string, State> states = new(StringComparer.OrdinalIgnoreCase);
    private readonly object processLock = new();
    private DateTime lastProcessSampleUtc = DateTime.UtcNow;
    private TimeSpan lastProcessorTime = Process.GetCurrentProcess().TotalProcessorTime;
    private decimal lastCpuPercent;

    public EngineRuntimeMonitor()
    {
        foreach (var definition in Definitions) states.TryAdd(definition.Code, new State());
    }

    public long Start(string code)
    {
        Interlocked.Increment(ref states.GetOrAdd(code, _ => new State()).ActiveRequests);
        return Stopwatch.GetTimestamp();
    }

    public void Complete(string code, long startedAt, bool failed, string? error = null)
    {
        var state = states.GetOrAdd(code, _ => new State());
        Interlocked.Decrement(ref state.ActiveRequests);
        var sample = new Sample(DateTime.UtcNow, Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds, failed, failed ? (error ?? "Request failed.") : string.Empty);
        state.Samples.Enqueue(sample);
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

    public static string? Classify(string method, string path)
    {
        var value = path.ToLowerInvariant();
        var mutation = !HttpMethods.IsGet(method) && !HttpMethods.IsHead(method) && !HttpMethods.IsOptions(method);
        if (value.StartsWith("/api/frevopilot")) return "frevopilot";
        if (mutation && (value.Contains("/resume-intake") || value.EndsWith("/resume") || value.Contains("/public/") && value.Contains("resume"))) return "resume-parser";
        if (mutation && value.Contains("/parse-source")) return "jd-parser";
        if (mutation && (value.Contains("evaluate-ats") || value.Contains("auto-run-ats") || value.Contains("/ats-score")
            || value.Contains("/application-scores/") || value.Contains("/applications/") && value.EndsWith("/score"))) return "ats-scoring";
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
        var load = Load(active, recent.Where(item => item.RecordedAtUtc >= now.AddSeconds(-30)).ToArray());
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
            Trend = BuildTrend(samples, now),
        };
    }

    private static List<EngineTrendPoint> BuildTrend(Sample[] samples, DateTime now)
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
                LoadPercent = Load(0, bucket),
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

    private static int Load(int active, IReadOnlyCollection<Sample> samples)
    {
        if (active == 0 && samples.Count == 0) return 0;
        var averageDuration = samples.Count == 0 ? 0 : samples.Average(item => item.DurationMs);
        return (int)Math.Round(Math.Clamp(active * 25 + samples.Count * 6 + averageDuration / 250, 1, 100));
    }

    private static void Trim(State state, DateTime cutoff)
    {
        while (state.Samples.TryPeek(out var sample) && sample.RecordedAtUtc < cutoff) state.Samples.TryDequeue(out _);
    }
}
