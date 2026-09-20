using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class EngineFailureCatalogTests
{
    [Theory]
    [InlineData("unavailable", "LOCAL_LLM_STOPPED")]
    [InlineData("not_configured", "LOCAL_LLM_NOT_CONFIGURED")]
    [InlineData("backend_unavailable", "LOCAL_LLM_BACKEND_UNAVAILABLE")]
    [InlineData("inference_failed", "LOCAL_LLM_INFERENCE_UNAVAILABLE")]
    [InlineData("secret-key-value", "HTTP 503")]
    public void GatewayReasonsAreAllowListedNotCopied(string remoteCode, string expected)
    {
        var body = JsonSerializer.Serialize(new { error = new { code = remoteCode, message = "private-resume-secret" } });
        var code = EngineFailureCatalog.FromProviderHttp(503, true, body);
        Assert.Equal(expected, code);
        Assert.DoesNotContain("private-resume-secret", EngineFailureCatalog.Describe(code));
        Assert.DoesNotContain("CPU", EngineFailureCatalog.Describe(code));
        Assert.Equal("HTTP 503", EngineFailureCatalog.FromProviderHttp(503, false, body));
    }
    [Theory]
    [InlineData("<html>secret-502-page</html>")]
    [InlineData("{\"error\":null}")]
    [InlineData("{\"error\":{\"code\":[\"unavailable\"]}}")]
    [InlineData("null")]
    public void BadResponsesStayGeneric(string body) => Assert.Equal("HTTP 503", EngineFailureCatalog.FromProviderHttp(503, true, body));

    [Fact]
    public void DatabaseErrorNamesOnlyKnownFieldAndNeverCopiesValues()
    {
        var known = EngineFailureCatalog.FromDatabase(1406, "Data too long for column 'Qualification' at row 1 private-value");
        Assert.Equal("DB_QUALIFICATION_TOO_LONG", known);
        Assert.Contains("250 characters", EngineFailureCatalog.Describe(known));
        Assert.Equal("DB_VALUE_TOO_LONG", EngineFailureCatalog.FromDatabase(1406, "Data too long for column 'private-field'"));
        Assert.Equal("DB_DUPLICATE", EngineFailureCatalog.FromDatabase(1062, "Duplicate entry private-email"));
        Assert.Equal("ENGINE_FAILED", EngineFailureCatalog.Clean("HTTP 503 secret-token"));
    }

    [Fact]
    public async Task Local503IsRecordedEvenWhenItsCallerHandlesTheErrorAndReturns400()
    {
        var (monitor, buffer) = Monitoring();
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) {
            Content = new StringContent("{\"error\":{\"code\":\"unavailable\",\"message\":\"private-raw-detail\"}}") }));
        var service = new RecruitmentAiScoringService(null!, null!, new Factory(handler), NullLogger<RecruitmentAiScoringService>.Instance, monitor);
        var task = (Task)typeof(RecruitmentAiScoringService).GetMethod("SendLocalProviderAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [Settings(), "private-test-key", "private-document", null, null, CancellationToken.None])!;
        await task;
        var row = Assert.Single(buffer.Checkpoint());
        Assert.Equal("ai-provider", row.EngineCode); Assert.Equal("AI model", row.ReferenceType); Assert.Equal("6", row.ReferenceId);
        Assert.Equal("Failed", row.Status); Assert.Equal(503, row.HttpStatus); Assert.Equal("LOCAL_LLM_STOPPED", row.FailureCode);
        Assert.True(row.DurationMs >= 0); Assert.NotNull(row.CompletedAtUtc);
        Assert.DoesNotContain("private-", JsonSerializer.Serialize(new { row, snapshot = monitor.Snapshot() }));
        Assert.Contains("stopped", monitor.Snapshot().Engines.Single(e => e.Code == "ai-provider").LastError);
        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        Assert.Contains("Gateway reports", (string)result.GetType().GetProperty("Error")!.GetValue(result)!);
    }

    [Fact]
    public async Task CloudRetryFailureRemainsVisibleAfterTheNextAttemptSucceeds()
    {
        var (monitor, buffer) = Monitoring();
        using var handler = new Handler((attempt, _) => Task.FromResult(new HttpResponseMessage(attempt == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK) {
            Content = new StringContent(attempt == 1 ? "private-provider-error" : "{\"choices\":[]}") }));
        var service = new RecruitmentAiScoringService(null!, null!, new Factory(handler), NullLogger<RecruitmentAiScoringService>.Instance, monitor);
        var settings = Settings(); settings.ProviderCode = "Groq"; settings.ModelName = "test-model";
        var task = (Task)typeof(RecruitmentAiScoringService).GetMethod("SendProviderAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [settings, "private-key", "private-prompt", null, 256, null, "", CancellationToken.None])!;
        await task;
        var rows = buffer.Checkpoint(); Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Attempt == 1 && r.Status == "Failed" && r.HttpStatus == 503);
        Assert.Contains(rows, r => r.Attempt == 2 && r.Status == "Completed" && r.HttpStatus == 200);
        Assert.DoesNotContain("private-", JsonSerializer.Serialize(rows));
    }

    [Fact]
    public async Task EngineExceptionAndCancellationKeepTheirReasonWithoutChangingThrownError()
    {
        var (monitor, buffer) = Monitoring();
        var error = new HttpRequestException(HttpRequestError.ConnectionError, "private-endpoint");
        Assert.Same(error, await Assert.ThrowsAsync<HttpRequestException>(() => monitor.ObserveAsync<int>("documents", () => Task.FromException<int>(error))));
        await Assert.ThrowsAsync<OperationCanceledException>(() => monitor.ObserveAsync<int>("payroll", () => Task.FromException<int>(new OperationCanceledException())));
        Assert.Contains(buffer.Checkpoint(), r => r.EngineCode == "documents" && r.FailureCode == "TRANSPORT_FAILED");
        Assert.Contains(buffer.Checkpoint(), r => r.EngineCode == "payroll" && r.Status == "Cancelled" && r.FailureCode == "REQUEST_CANCELLED");
        Assert.DoesNotContain("private-", JsonSerializer.Serialize(monitor.Snapshot()));
    }

    [Fact]
    public void ExistingFailureCodeColumnStillStoresOnlyBoundedCodesNoMigration()
    {
        var (_, buffer) = Monitoring();
        buffer.Start("jd-parser", 1, new EngineActivityContext("Save hiring request"));
        buffer.Complete("jd-parser", 1, 39, true, 500, null, "DB_QUALIFICATION_TOO_LONG");
        var row = Assert.Single(buffer.Checkpoint());
        Assert.Equal("DB_QUALIFICATION_TOO_LONG", row.FailureCode); Assert.True(row.FailureCode.Length <= 80);
        Assert.Contains("FailureCode VARCHAR(80)", EngineActivityStore.SchemaSql);
        Assert.DoesNotContain("FailureReason", EngineActivityStore.SchemaSql);
    }

    private static (EngineRuntimeMonitor, EngineActivityBuffer) Monitoring()
    {
        var buffer = new EngineActivityBuffer(TimeProvider.System, new ConfigurationBuilder().Build());
        return (new EngineRuntimeMonitor(activity: buffer), buffer);
    }
    private static RecruitmentAiScoringSecretRow Settings() => new() { Id = 6, ProviderCode = "LocalOpenAICompatible",
        ModelName = "hrms-local", EndpointUrl = "https://example.invalid/llm-api.php", RequestTimeoutSeconds = 210 };
    private sealed class Factory(Handler handler) : IHttpClientFactory { public HttpClient CreateClient(string name) => new(handler, false); }
    private sealed class Handler(Func<int, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        private int calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(++calls, cancellationToken);
    }
}
