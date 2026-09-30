using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public class GoogleDriveOAuthRefreshTests
{
    [Theory]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task TemporaryHttpErrorsRetryThenCacheTheToken(int status)
    {
        using var handler = new Handler((call, _) => Task.FromResult(call < 3
            ? Failure(status, "server_error") : Token()));
        var (service, server, logs) = Setup(handler);
        await using var first = await service.OpenReadAsync(server, "file", default);
        await using var second = await service.OpenReadAsync(server, "file", default);
        Assert.Equal(3, handler.RefreshCalls);
        Assert.Equal(2, handler.ReadCalls);
        Assert.Equal(2, logs.Messages.Count);
        Assert.All(logs.Messages, message => Assert.Contains($"HTTP {status}", message));
    }

    [Theory]
    [InlineData("invalid_grant", "Reconnect the Google account")]
    [InlineData("invalid_client", "client configuration")]
    [InlineData("unauthorized_client", "client configuration")]
    [InlineData("admin_policy_enforced", "administrator policy")]
    [InlineData("access_denied", "permissions")]
    [InlineData("invalid_request", "Check the storage configuration")]
    [InlineData("unknown-private-value", "code unknown_error")]
    public async Task PermanentErrorsAreNotRetriedAndOnlyInvalidGrantRequestsReconnect(string code, string expected)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Failure(400, code)));
        var (service, server, logs) = Setup(handler);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.OpenReadAsync(server, "file", default));
        Assert.Contains(expected, error.Message);
        Assert.Equal(code == "invalid_grant", error.Message.Contains("Reconnect"));
        Assert.Equal(1, handler.RefreshCalls);
        Assert.Equal(0, handler.ReadCalls);
        AssertSafe(error.Message + string.Join("\n", logs.Messages));
    }

    [Fact]
    public async Task RetryLimitReturnsTemporaryFailureAndDoesNotEraseCredentials()
    {
        using var handler = new Handler((call, _) => Task.FromResult(call <= 3 ? Failure(503, "server_error") : Token()));
        var (service, server, logs) = Setup(handler);
        var savedCredential = server.Credential;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.OpenReadAsync(server, "file", default));
        Assert.Contains("temporarily unavailable", error.Message);
        Assert.DoesNotContain("Reconnect", error.Message);
        Assert.Equal(3, handler.RefreshCalls);
        Assert.Equal(savedCredential, server.Credential);
        await using var recovered = await service.OpenReadAsync(server, "file", default);
        Assert.Equal(4, handler.RefreshCalls);
        AssertSafe(error.Message + string.Join("\n", logs.Messages));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NetworkAndTimeoutFailuresRetryWithoutExposingExceptionText(bool timeout)
    {
        using var handler = new Handler((call, _) => call == 1
            ? Task.FromException<HttpResponseMessage>(timeout
                ? new OperationCanceledException("synthetic-secret")
                : new HttpRequestException("synthetic-secret"))
            : Task.FromResult(Token()));
        var (service, server, logs) = Setup(handler);
        await using var file = await service.OpenReadAsync(server, "file", default);
        Assert.Equal(2, handler.RefreshCalls);
        Assert.Contains(timeout ? "timeout" : "network_error", Assert.Single(logs.Messages));
        AssertSafe(string.Join("\n", logs.Messages));
    }

    [Theory]
    [InlineData("not-json synthetic-secret")]
    [InlineData("{}")]
    public async Task InvalidTokenResponseRetriesWithoutAskingForReconnect(string body)
    {
        using var handler = new Handler((call, _) => Task.FromResult(call == 1
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) } : Token()));
        var (service, server, logs) = Setup(handler);
        await using var file = await service.OpenReadAsync(server, "file", default);
        Assert.Equal(2, handler.RefreshCalls);
        Assert.Contains("invalid_token_response", Assert.Single(logs.Messages));
        AssertSafe(string.Join("\n", logs.Messages));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LongRetryAfterReturnsPromptlyForStorageFailover(bool dateHeader)
    {
        using var handler = new Handler((_, _) =>
        {
            var response = Failure(429, "temporarily_unavailable");
            response.Headers.RetryAfter = dateHeader
                ? new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddMinutes(2))
                : new RetryConditionHeaderValue(TimeSpan.FromMinutes(2));
            return Task.FromResult(response);
        });
        var (service, server, _) = Setup(handler);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.OpenReadAsync(server, "file", default).WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Contains("temporarily unavailable", error.Message);
        Assert.Equal(1, handler.RefreshCalls);
    }

    [Fact]
    public async Task CallerCancellationStopsRetriesAndReleasesRefreshLock()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new Handler((call, _) =>
        {
            if (call != 1) return Task.FromResult(Token());
            cancellation.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(cancellation.Token);
        });
        var (service, server, logs) = Setup(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.OpenReadAsync(server, "file", cancellation.Token));
        Assert.Equal(1, handler.RefreshCalls);
        Assert.Empty(logs.Messages);
        await using var recovered = await service.OpenReadAsync(server, "file", default);
        Assert.Equal(2, handler.RefreshCalls);
    }

    [Fact]
    public async Task ConcurrentReadsShareOneTokenRefresh()
    {
        using var handler = new Handler(async (_, cancellation) =>
        {
            await Task.Delay(30, cancellation);
            return Token();
        });
        var (service, server, _) = Setup(handler);
        await Task.WhenAll(Enumerable.Range(0, 5).Select(async _ =>
        {
            await using var file = await service.OpenReadAsync(server, "file", default);
        }));
        Assert.Equal(1, handler.RefreshCalls);
        Assert.Equal(5, handler.ReadCalls);
    }

    private static (GoogleDriveOAuthService, AttachmentStorageServer, CapturedLog) Setup(Handler handler)
    {
        var log = new CapturedLog();
        var service = new GoogleDriveOAuthService(new Factory(handler), new ConfigurationBuilder().Build(),
            null!, new EphemeralDataProtectionProvider(), log);
        var server = new AttachmentStorageServer
        {
            Credential = service.SerializeCredential(new GoogleDriveCredential
            {
                OAuthClientId = "synthetic-client", OAuthClientSecret = "synthetic-secret",
                RefreshToken = "synthetic-refresh", FolderId = "folder"
            })
        };
        return (service, server, log);
    }

    private static HttpResponseMessage Token() => new(HttpStatusCode.OK)
    { Content = new StringContent("{\"access_token\":\"synthetic-access\",\"expires_in\":3600}") };

    private static HttpResponseMessage Failure(int status, string code)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status)
        { Content = new StringContent($"{{\"error\":\"{code}\",\"error_description\":\"synthetic-secret synthetic-refresh\"}}") };
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
        return response;
    }

    private static void AssertSafe(string text)
    {
        foreach (var secret in new[] { "synthetic-secret", "synthetic-refresh", "synthetic-access", "unknown-private-value" })
            Assert.DoesNotContain(secret, text);
    }

    private sealed class Factory(Handler handler) : IHttpClientFactory
    { public HttpClient CreateClient(string name) => new(handler, disposeHandler: false); }

    private sealed class Handler(Func<int, CancellationToken, Task<HttpResponseMessage>> refresh) : HttpMessageHandler
    {
        private int refreshCalls, readCalls;
        public int RefreshCalls => refreshCalls;
        public int ReadCalls => readCalls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "oauth2.googleapis.com")
                return refresh(Interlocked.Increment(ref refreshCalls), cancellationToken);
            Assert.Equal("synthetic-access", request.Headers.Authorization?.Parameter);
            Interlocked.Increment(ref readCalls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("resume") });
        }
    }

    private sealed class CapturedLog : ILogger<GoogleDriveOAuthService>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
