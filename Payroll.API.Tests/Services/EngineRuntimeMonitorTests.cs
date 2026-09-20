using Payroll.API.Models;
using Payroll.API.Services;
using Xunit;

namespace Payroll.API.Tests.Services;

public sealed class EngineRuntimeMonitorTests
{
    [Theory]
    [InlineData("POST", "/api/recruitment/requisitions/parse-source", "jd-parser")]
    [InlineData("POST", "/api/recruitment/requisitions", "jd-parser")]
    [InlineData("GET", "/api/recruitment/requisitions", null)]
    [InlineData("OPTIONS", "/api/recruitment/requisitions", null)]
    [InlineData("POST", "/api/integrations/ai/6/test", null)] // Provider service owns this observation.
    [InlineData("POST", "/api/recruitment/resume-intake/preview", "resume-parser")]
    [InlineData("POST", "/api/recruitment/resume-intake", "resume-parser")]
    [InlineData("POST", "/api/recruitment/candidates/7/resume", "resume-parser")]
    [InlineData("POST", "/api/ess/recruitment/referrals/7/resume", "resume-parser")]
    [InlineData("GET", "/api/recruitment/resume-intake/preview", null)]
    [InlineData("POST", "/api/recruitment/hiring-cases/7/resume", null)]
    [InlineData("POST", "/api/recruitment-orchestration/applications/7/resume", null)]
    [InlineData("POST", "/api/public/recruitment/sessions/example/files/7", null)]
    [InlineData("POST", "/api/public/recruitment/sessions/example/submit", null)]
    [InlineData("GET", "/api/public/recruitment/sessions/example/processing-status", null)]
    [InlineData("POST", "/api/recruitment/talent-pool/match", null)]
    [InlineData("GET", "/api/recruitment/applications/7/score-jobs/12", null)]
    public void RealParserRoutesAreTrackedWithoutConfusingWorkflowResumeOrQueueTraffic(string method, string path, string? expected)
    {
        Assert.Equal(expected, EngineRuntimeMonitor.Classify(method, path));
    }

    [Fact]
    public async Task PublicParserHookTracksOnlyActualWorkAndPreservesTheReturnedDraft()
    {
        var monitor = new EngineRuntimeMonitor();
        // An arbitrary public attachment is not necessarily a resume.
        Assert.Null(BeginHttpObservation(monitor, "POST", "/api/public/recruitment/sessions/example/files/7"));
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = monitor.ObserveAsync("resume-parser", () => completion.Task);
        var active = monitor.Snapshot().Engines.Single(engine => engine.Code == "resume-parser");
        Assert.Equal(1, active.ActiveRequests);
        Assert.Equal(0, active.RequestsLastFiveMinutes);
        completion.SetResult("NeedsReview");
        Assert.Equal("NeedsReview", await work);
        var completed = monitor.Snapshot().Engines.Single(engine => engine.Code == "resume-parser");
        Assert.Equal(0, completed.ActiveRequests);
        Assert.Equal(1, completed.RequestsLastFiveMinutes);
        Assert.Equal(1, completed.Trend.Sum(point => point.Requests));
        Assert.Equal(100m, completed.SuccessRate); // Work completed; quality status remains in its result.
        Assert.Equal("Observed", completed.State);
        Assert.Contains("this API instance", completed.Description);
        Assert.Equal(0, monitor.Snapshot().Engines.Single(engine => engine.Code == "ats-scoring").RequestsLastFiveMinutes);
    }

    [Fact]
    public async Task PublicParserFailureAndCancellationAreCompletedOnceWithoutLeakingContent()
    {
        var monitor = new EngineRuntimeMonitor();
        var failure = new InvalidOperationException("private resume and provider content");
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => monitor.ObserveAsync<string>("resume-parser", () => Task.FromException<string>(failure)));
        Assert.Same(failure, actual);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => monitor.ObserveAsync("resume-parser", () => Task.FromCanceled<string>(cancellation.Token)));
        var snapshot = monitor.Snapshot();
        var metric = snapshot.Engines.Single(engine => engine.Code == "resume-parser");
        Assert.Equal(0, metric.ActiveRequests);
        Assert.Equal(2, metric.RequestsLastFiveMinutes);
        Assert.Equal(0m, metric.SuccessRate);
        Assert.Equal(2, metric.Trend.Sum(point => point.Failures));
        Assert.DoesNotContain("private resume", System.Text.Json.JsonSerializer.Serialize(snapshot));
        Assert.DoesNotContain("Scoring attempt", metric.LastError);
    }

    [Fact]
    public async Task ExplicitFailedParseResultIsObservedButClassifierFailureCannotChangeBusinessResult()
    {
        var monitor = new EngineRuntimeMonitor();
        var result = await monitor.ObserveAsync("resume-parser", () => Task.FromResult("Failed"), status => status != "Failed");
        Assert.Equal("Failed", result);
        var failed = monitor.Snapshot().Engines.Single(engine => engine.Code == "resume-parser");
        Assert.Equal(1, failed.RequestsLastFiveMinutes);
        Assert.Equal(0m, failed.SuccessRate);

        var preserved = await monitor.ObserveAsync("resume-parser", () => Task.FromResult("Parsed"), _ => throw new InvalidOperationException("private telemetry error"));
        Assert.Equal("Parsed", preserved);
        var snapshot = monitor.Snapshot();
        Assert.Equal(50m, snapshot.Engines.Single(engine => engine.Code == "resume-parser").SuccessRate);
        Assert.DoesNotContain("private telemetry error", System.Text.Json.JsonSerializer.Serialize(snapshot));
    }

    [Fact]
    public async Task VeryShortWorkRetainsItsCompletionCountEvenWhenBusyTimeRoundsToZero()
    {
        // Shared timestamps give an exact 104ms sample, without sleeping or
        // demanding a made-up nonzero busy percentage from fast operations.
        var monitor = new EngineRuntimeMonitor();
        var end = DateTime.UtcNow.AddSeconds(-1);
        var snapshot = await monitor.SnapshotWithJobsAsync(_ => Task.FromResult(new List<EngineJobObservation>
        {
            new() { Status = "Completed", StartedAt = end.AddMilliseconds(-104), CompletedAt = end, UpdatedAt = end },
        }), CancellationToken.None);
        var metric = snapshot.Engines.Single(engine => engine.Code == "ats-scoring");
        Assert.Equal(0, metric.ActiveRequests);
        Assert.Equal(0, metric.LoadPercent);
        Assert.Equal(1, metric.RequestsLastFiveMinutes);
        Assert.Equal(104m, metric.AverageDurationMs);
        Assert.Equal(1, metric.Trend.Sum(point => point.Requests));
    }

    [Theory]
    [InlineData("GET", "/api/frevopilot/analytics/status")]
    [InlineData("GET", "/api/frevopilot/analytics/runs/00000000-0000-0000-0000-000000000001")]
    [InlineData("POST", "/api/frevopilot/analytics/runs")]
    [InlineData("HEAD", "/api/frevopilot/analytics/status")]
    [InlineData("OPTIONS", "/api/frevopilot/analytics/runs")]
    [InlineData("GET", "/api/frevopilot/chat-threads/status")]
    [InlineData("POST", "/api/frevopilot/chat-threads")]
    [InlineData("PUT", "/api/frevopilot/chat-threads/00000000-0000-0000-0000-000000000001")]
    [InlineData("DELETE", "/api/frevopilot/chat-threads/00000000-0000-0000-0000-000000000001")]
    public void FrevoPilotHttpTrafficDoesNotCountAsDashboardWork(string method, string path)
    {
        Assert.Null(EngineRuntimeMonitor.Classify(method, path));
    }

    [Fact]
    public void ActiveDashboardIsCountedOnceWhileStartAndStatusRequestsAreInFlight()
    {
        var monitor = new EngineRuntimeMonitor();
        var workflow = monitor.Start("frevopilot");
        foreach (var (method, path) in new[]
        {
            ("POST", "/api/frevopilot/analytics/runs"),
            ("GET", "/api/frevopilot/analytics/status"),
            ("GET", "/api/frevopilot/analytics/runs/00000000-0000-0000-0000-000000000001"),
        })
        {
            var http = BeginHttpObservation(monitor, method, path);
            var metric = FrevoPilot(monitor.Snapshot());
            Assert.Equal(1, metric.ActiveRequests);
            Assert.Equal(0, metric.RequestsLastFiveMinutes);
            CompleteHttpObservation(monitor, http);
        }
        monitor.Complete("frevopilot", workflow, false);
        var completed = FrevoPilot(monitor.Snapshot());
        Assert.Equal(0, completed.ActiveRequests);
        Assert.Equal(1, completed.RequestsLastFiveMinutes);
        Assert.Equal(100m, completed.SuccessRate);
        Assert.Equal("Dashboard workflows", completed.Coverage);
        Assert.Contains("not CPU", completed.Description);
    }

    [Fact]
    public async Task SuccessfulPollsCannotDiluteDashboardFailureOrWorkflowLatency()
    {
        var monitor = new EngineRuntimeMonitor();
        var failed = monitor.Start("frevopilot");
        await Task.Delay(30);
        monitor.Complete("frevopilot", failed, true, "Dashboard query failed; see FrevoPilot status.");
        var beforePolling = FrevoPilot(monitor.Snapshot());
        Assert.True(beforePolling.AverageDurationMs >= 20m);
        Assert.Equal(beforePolling.AverageDurationMs, beforePolling.P95DurationMs);

        for (var index = 0; index < 295; index++)
        {
            var request = BeginHttpObservation(monitor, "GET", "/api/frevopilot/analytics/runs/00000000-0000-0000-0000-000000000001");
            CompleteHttpObservation(monitor, request);
        }
        var afterPolling = FrevoPilot(monitor.Snapshot());
        Assert.Equal(0, afterPolling.ActiveRequests);
        Assert.Equal(1, afterPolling.RequestsLastFiveMinutes);
        Assert.Equal(0m, afterPolling.SuccessRate);
        Assert.Equal("Attention", afterPolling.State);
        Assert.Equal(beforePolling.AverageDurationMs, afterPolling.AverageDurationMs);
        Assert.Equal(beforePolling.P95DurationMs, afterPolling.P95DurationMs);
        Assert.Equal(beforePolling.LastActivityUtc, afterPolling.LastActivityUtc);
        Assert.Equal(beforePolling.LastError, afterPolling.LastError);

        var succeeded = monitor.Start("frevopilot");
        monitor.Complete("frevopilot", succeeded, false);
        monitor.Complete("frevopilot", succeeded, false);
        var mixed = FrevoPilot(monitor.Snapshot());
        Assert.Equal(0, mixed.ActiveRequests);
        Assert.Equal(2, mixed.RequestsLastFiveMinutes);
        Assert.Equal(50m, mixed.SuccessRate);
        Assert.Equal(2, mixed.Trend.Sum(point => point.Requests));
        Assert.Equal(1, mixed.Trend.Sum(point => point.Failures));
    }

    [Fact]
    public async Task SharedAtsQueueObservationsRemainIndependentOfDashboardAndPollActivity()
    {
        var monitor = new EngineRuntimeMonitor();
        var workflow = monitor.Start("frevopilot");
        var localAts = monitor.Start("ats-scoring");
        var now = DateTime.UtcNow;
        var snapshot = await monitor.SnapshotWithJobsAsync(_ => Task.FromResult(new List<EngineJobObservation>
        {
            new() { Status = "Processing", StartedAt = now.AddSeconds(-10), UpdatedAt = now },
            new() { Status = "Queued", UpdatedAt = now },
            new() { Status = "Completed", StartedAt = now.AddSeconds(-30), CompletedAt = now.AddSeconds(-20), UpdatedAt = now.AddSeconds(-20) },
            new() { Status = "Failed", StartedAt = now.AddSeconds(-15), CompletedAt = now.AddSeconds(-10), UpdatedAt = now.AddSeconds(-10) },
        }), CancellationToken.None);

        var ats = snapshot.Engines.Single(engine => engine.Code == "ats-scoring");
        Assert.Equal(1, ats.ActiveRequests);
        Assert.Equal(1, ats.QueuedRequests);
        Assert.Equal(2, ats.RequestsLastFiveMinutes);
        Assert.Equal(50m, ats.SuccessRate);
        Assert.Equal(7500m, ats.AverageDurationMs);
        Assert.Equal(10000m, ats.P95DurationMs);
        Assert.StartsWith("Shared ATS queue", ats.Coverage);
        Assert.Equal(1, FrevoPilot(snapshot).ActiveRequests);
        Assert.Equal(0, FrevoPilot(snapshot).RequestsLastFiveMinutes);
        monitor.Complete("ats-scoring", localAts, false);
        monitor.Complete("frevopilot", workflow, false);
    }

    [Theory]
    [InlineData("POST", "/api/recruitment/applications/1/resume-intake", "resume-parser")]
    [InlineData("POST", "/api/recruitment/hiring-requests/parse-source", "jd-parser")]
    [InlineData("POST", "/api/pay-runs", "payroll")]
    [InlineData("POST", "/api/employees/bulk-import", "bulk-data")]
    [InlineData("POST", "/api/notifications/send", "notifications")]
    [InlineData("POST", "/api/documents/generate", "documents")]
    [InlineData("POST", "/api/recruitment/applications/1/score", null)]
    [InlineData("GET", "/api/pay-runs", null)]
    public void OtherEngineClassificationRemainsUnchanged(string method, string path, string? expected)
    {
        Assert.Equal(expected, EngineRuntimeMonitor.Classify(method, path));
    }

    private static EngineRuntimeMetric FrevoPilot(EngineMonitoringSnapshot snapshot) => snapshot.Engines.Single(engine => engine.Code == "frevopilot");

    private static (string Code, long Token)? BeginHttpObservation(EngineRuntimeMonitor monitor, string method, string path)
    {
        var code = EngineRuntimeMonitor.Classify(method, path);
        return code is null ? null : (code, monitor.Start(code));
    }

    private static void CompleteHttpObservation(EngineRuntimeMonitor monitor, (string Code, long Token)? observation)
    {
        if (observation is { } request) monitor.Complete(request.Code, request.Token, false);
    }
}
