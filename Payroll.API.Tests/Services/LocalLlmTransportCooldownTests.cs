using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public class LocalLlmTransportCooldownTests
{
    private static readonly MethodInfo SendMethod = typeof(RecruitmentAiScoringService)
        .GetMethod("SendLocalProviderAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo Deadline = typeof(RecruitmentAiScoringService)
        .GetField("localRetryAfterUtc", BindingFlags.NonPublic | BindingFlags.Instance)!;

    [Fact]
    public async Task Gateway504PreventsNextSendUntilBoundedCooldownExpires()
    {
        using var handler = new Handler((call, _) => Task.FromResult(call == 1
            ? new HttpResponseMessage(HttpStatusCode.GatewayTimeout) { Content = new StringContent("synthetic-private-error") }
            : Success()));
        var service = Service(handler);
        var before = DateTime.UtcNow;
        var first = await Send(service);
        Assert.Equal("ProviderError", Property(first, "Status"));
        Assert.Contains("HTTP 504", (string)Property(first, "Error"));
        Assert.Contains("90-second", (string)Property(first, "Error"));
        Assert.DoesNotContain("synthetic-private-error", (string)Property(first, "Error"));
        Assert.InRange(((DateTime)Deadline.GetValue(service)! - before).TotalSeconds, 89, 91);
        var second = await Send(service);
        Assert.Equal("LocalBusy", Property(second, "Status"));
        Assert.Contains("backend can settle", (string)Property(second, "Error"));
        Assert.DoesNotContain("rate-limit", (string)Property(second, "Error"));
        Assert.Equal(1, handler.Calls);
        // Advance only the stored test deadline; no real wait or provider call.
        Deadline.SetValue(service, DateTime.UtcNow.AddSeconds(-1));
        Assert.True((bool)Property(await Send(service), "Success"));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task TransportDeadlinePreventsNextSendWithoutSleepingOrRetrying()
    {
        using var handler = new Handler((_, _) => Task.FromException<HttpResponseMessage>(new OperationCanceledException()));
        var service = Service(handler);
        Assert.Equal("TimedOut", Property(await Send(service), "Status"));
        Assert.Equal("LocalBusy", Property(await Send(service), "Status"));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task CallerCancellationInFlightPropagatesAndAlsoProtectsTheBackend()
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler(async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return Success();
        });
        var service = Service(handler);
        var pending = Send(service, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal("LocalBusy", Property(await Send(service), "Status"));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task CancellationBeforeAdmissionDoesNotCreateCooldownOrSendAnything()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var handler = new Handler((_, _) => Task.FromResult(Success()));
        var service = Service(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Send(service, cancellation.Token));
        Assert.Equal(0, handler.Calls);
        Assert.True((bool)Property(await Send(service), "Success"));
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(1000, 60)]
    public async Task RateLimitCooldownRetainsExistingFiveToSixtySecondBounds(int retrySeconds, int expectedSeconds)
    {
        using var handler = new Handler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("") };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(retrySeconds));
            return Task.FromResult(response);
        });
        var service = Service(handler);
        var before = DateTime.UtcNow;
        await Send(service);
        Assert.InRange(((DateTime)Deadline.GetValue(service)! - before).TotalSeconds, expectedSeconds - 1, expectedSeconds + 1);
        var second = await Send(service);
        Assert.Equal("LocalBusy", Property(second, "Status"));
        Assert.Contains("busy/rate-limit", (string)Property(second, "Error"));
        Assert.DoesNotContain("90-second", (string)Property(second, "Error"));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public void FrevoPilotPreservesGatewayCooldownCauseInsteadOfCallingItQuota()
    {
        var failure = RecruitmentAiScoringService.AnalyticsProviderFailure(
            "Local LLM is cooling down so the backend can settle.", "LocalBusy", true);
        Assert.Contains("gateway/request timeout", failure.Message);
        Assert.Contains("90 seconds", failure.Message);
        Assert.DoesNotContain("quota", failure.Message);
        Assert.DoesNotContain("rate-limit", failure.Message);
    }

    [Fact]
    public async Task NetworkFailureReturnsOnlyFrameworkClassificationWithoutReplay()
    {
        using var handler = new Handler((_, _) => Task.FromException<HttpResponseMessage>(
            new HttpRequestException(HttpRequestError.ConnectionError, "private-host secret-header")));
        var response = await Send(Service(handler));
        Assert.Equal("TransportError", Property(response, "Status"));
        Assert.Contains("(ConnectionError)", (string)Property(response, "Error"));
        Assert.DoesNotContain("private-host", (string)Property(response, "Error"));
        Assert.DoesNotContain("secret-header", (string)Property(response, "Error"));
        Assert.Equal(1, handler.Calls);
    }

    private static RecruitmentAiScoringService Service(Handler handler) =>
        new(null!, null!, new Factory(handler), NullLogger<RecruitmentAiScoringService>.Instance);

    private static HttpResponseMessage Success() => new(HttpStatusCode.OK)
    { Content = new StringContent("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"{}\"}}]}") };

    private static async Task<object> Send(RecruitmentAiScoringService service, CancellationToken cancellation = default)
    {
        var settings = new RecruitmentAiScoringSecretRow
        { ProviderCode = "LocalOpenAICompatible", ModelName = "hrms-local", EndpointUrl = "https://example.invalid/llm-api.php", RequestTimeoutSeconds = 210 };
        var task = (Task)SendMethod.Invoke(service, [settings, "synthetic-test-key", "Synthetic prompt", null, null, cancellation])!;
        await task;
        return task.GetType().GetProperty("Result")!.GetValue(task)!;
    }

    private static object Property(object result, string name) => result.GetType().GetProperty(name)!.GetValue(result)!;

    private sealed class Factory(Handler handler) : IHttpClientFactory
    { public HttpClient CreateClient(string name) => new(handler, disposeHandler: false); }

    private sealed class Handler(Func<int, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            response(++Calls, cancellationToken);
    }
}
