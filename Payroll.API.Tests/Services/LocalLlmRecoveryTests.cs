using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class LocalLlmRecoveryTests
{
    private const string Key = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly AuthUser Admin = new() { Id = 1, Roles = ["super_admin"] };
    private static RecruitmentAiScoringSettings Model() => new()
        { Id = 6, ClientId = 0, ProviderCode = "LocalOpenAICompatible", EndpointUrl = "https://example.invalid/llm-api.php" };
    private static IConfiguration Configuration(string? setting = null, string? value = null)
    {
        var data = new Dictionary<string, string?> {
            ["LocalLlmRecovery:Enabled"] = "true", ["LocalLlmRecovery:ControlEndpointUrl"] = "https://example.invalid/llm-control.php",
            ["LocalLlmRecovery:InferenceEndpointUrl"] = "https://example.invalid/llm-api.php", ["LocalLlmRecovery:ApiKey"] = Key };
        if (setting is not null) data["LocalLlmRecovery:" + setting] = value;
        return new ConfigurationBuilder().AddInMemoryCollection(data).Build();
    }
    private sealed class Transport(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler, IHttpClientFactory
    {
        public int Calls;
        public string? Name;
        public HttpClient CreateClient(string name) { Name = name; return new(this, false); }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { Calls++; return send(request, ct); }
    }
    private static Transport Never() => new((_, _) => throw new InvalidOperationException("No network allowed."));
    private sealed class LegacyStore(IConfiguration configuration) : ILocalLlmRecoverySettingsStore
    {
        public Task<LocalLlmRecoverySettings> GetAsync(CancellationToken ct = default) =>
            Task.FromResult(LocalLlmRecoverySettingsStore.LegacySettings(configuration));
        public Task<(LocalLlmRecoverySettings? Settings, string Error)> SaveAsync(SaveLocalLlmRecoverySettings request,
            RecruitmentAiScoringSettings model, AuthUser user, CancellationToken ct = default) => throw new NotSupportedException();
    }
    private static LocalLlmRecoveryService Service(IConfiguration configuration, Transport transport) => new(new LegacyStore(configuration), transport);

    [Theory]
    [InlineData(null, "client_admin")]
    [InlineData(20, "super_admin")]
    [InlineData(0, "super_admin")]
    [InlineData(null, "admin")]
    public async Task OnlyExactGlobalSuperAdminMaySend(int? client, string role)
    {
        var transport = Never();
        var service = Service(Configuration(), transport);
        var user = new AuthUser { ClientId = client, Roles = [role], Permissions = ["settings.manage", "security.manage"] };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SendAsync(Model(), user, true, Guid.NewGuid()));
        Assert.Equal(0, transport.Calls);
    }

    [Theory]
    [InlineData("Enabled", "false")]
    [InlineData("ApiKey", "")]
    [InlineData("ControlEndpointUrl", "http://example.invalid/llm-control.php")]
    [InlineData("ControlEndpointUrl", "https://other.invalid/llm-control.php")]
    [InlineData("ControlEndpointUrl", "https://user:pass@example.invalid/llm-control.php")]
    [InlineData("ControlEndpointUrl", "https://example.invalid/llm-control.php?key=x")]
    [InlineData("ControlEndpointUrl", "https://example.invalid/llm-api.php")]
    public async Task InvalidOrDisabledConfigurationNeverDispatches(string setting, string value)
    {
        var transport = Never();
        var result = await Service(Configuration(setting, value), transport).SendAsync(Model(), Admin, true, Guid.NewGuid());
        Assert.Equal("not_configured", result.Code);
        Assert.False(result.CanStart);
        Assert.Equal(0, transport.Calls);
    }

    [Theory]
    [InlineData("Gemini", "https://example.invalid/llm-api.php", 0)]
    [InlineData("OpenAICompatible", "https://example.invalid/llm-api.php", 0)]
    [InlineData("LocalOpenAICompatible", "https://example.invalid/llm-api.php", 20)]
    [InlineData("LocalOpenAICompatible", "https://example.invalid/other.php", 0)]
    public async Task RecoveryCannotTargetCloudClientOrAnotherGateway(string provider, string endpoint, int client)
    {
        var model = Model(); model.ProviderCode = provider; model.EndpointUrl = endpoint; model.ClientId = client;
        var transport = Never();
        var result = await Service(Configuration(), transport).SendAsync(model, Admin, true, Guid.NewGuid());
        Assert.Equal("unsupported", result.Code);
        Assert.Equal(0, transport.Calls);
    }

    [Theory]
    [InlineData(true, "starting")]
    [InlineData(false, "running")]
    [InlineData(false, "stopped")]
    public async Task FixedAuthenticatedRequestReportsActualReplyWithoutChangingModel(bool start, string code)
    {
        var id = Guid.NewGuid(); var model = Model();
        var transport = new Transport(async (request, ct) => {
            Assert.Equal("https://example.invalid/llm-control.php", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer " + Key, request.Headers.Authorization!.ToString());
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal(start ? "start" : "status", body.RootElement.GetProperty("action").GetString());
            Assert.Equal(id.ToString("D"), body.RootElement.GetProperty("requestId").GetString());
            Assert.Equal(2, body.RootElement.EnumerateObject().Count());
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { code, requestId = id.ToString("D") })) };
        });
        var result = await Service(Configuration(), transport).SendAsync(model, Admin, start, id);
        Assert.Equal(code, result.Code); Assert.Equal("LocalLlmControl", transport.Name);
        Assert.False(model.EnableAiScoring); Assert.False(model.IsPrimary);
        Assert.Equal(code == "stopped", result.CanStart);
    }

    [Theory]
    [InlineData(302, "control_unavailable")]
    [InlineData(503, "control_unavailable")]
    [InlineData(401, "unauthorized")]
    [InlineData(403, "unauthorized")]
    public async Task HttpErrorsDoNotExposeRemoteBody(int status, string code)
    {
        var transport = new Transport((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("SECRET path trace") }));
        var result = await Service(Configuration(), transport).SendAsync(Model(), Admin, true, Guid.NewGuid());
        Assert.Equal(code, result.Code); Assert.DoesNotContain("SECRET", result.Message); Assert.Equal(1, transport.Calls);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"code\":\"running\",\"requestId\":\"wrong\"}")]
    [InlineData("{\"code\":17}")]
    public async Task InvalidOrUncorrelatedReplyNeverClaimsHealthy(string reply)
    {
        var transport = new Transport((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply) }));
        var result = await Service(Configuration(), transport).SendAsync(Model(), Admin, true, Guid.NewGuid());
        Assert.Equal("control_unavailable", result.Code); Assert.False(result.CanStart);
    }

    [Fact]
    public async Task OversizedStreamIsBounded()
    {
        var transport = new Transport((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 5000)))) }));
        var result = await Service(Configuration(), transport).SendAsync(Model(), Admin, false, Guid.NewGuid());
        Assert.Equal("control_unavailable", result.Code);
    }

    [Fact]
    public async Task TimeoutNeverAutomaticallyRetriesStart()
    {
        var transport = new Transport((_, _) => throw new TaskCanceledException());
        var result = await Service(Configuration(), transport).SendAsync(Model(), Admin, true, Guid.NewGuid());
        Assert.Equal("Unknown", result.State); Assert.False(result.CanStart); Assert.Equal(1, transport.Calls);
    }

    [Fact]
    public async Task ActualHttpRoutesDenyAnonymousGlobalNonAdminAndClientSuperAdminBeforeDatabaseAccess()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection();
        builder.Configuration["IntegrationCredentialEncryption:MasterKey"] = "synthetic-only";
        builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IHttpClientFactory>(Never());
        builder.Services.AddSingleton(new PortableIntegrationCredentialProtector(builder.Configuration,
            new EphemeralDataProtectionProvider(), NullLogger<PortableIntegrationCredentialProtector>.Instance));
        builder.Services.AddSingleton<RecruitmentAiScoringService>();
        builder.Services.AddSingleton<LocalLlmRecoveryService>();
        builder.Services.AddSingleton<ILocalLlmRecoverySettingsStore, LocalLlmRecoverySettingsStore>();
        builder.Services.AddSingleton<AuthRepository>();
        await using var app = builder.Build();
        app.Use(async (context, next) => {
            var actor = context.Request.Headers["X-Test-Actor"].ToString();
            if (actor != "anonymous") context.Items["User"] = new AuthUser {
                Id = 11, ClientId = actor == "client-super-admin" ? 20 : null,
                Roles = [actor == "client-super-admin" ? "super_admin" : "admin"], Permissions = ["settings.manage"] };
            await next(context);
        });
        app.MapLocalLlmRecovery();
        await app.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            foreach (var actor in new[] { "anonymous", "admin", "client-super-admin" })
            foreach (var (method, path) in new[] { (HttpMethod.Get, "runtime"), (HttpMethod.Post, "start-runtime"),
                (HttpMethod.Get, "recovery-settings"), (HttpMethod.Put, "recovery-settings") })
            {
                using var request = new HttpRequestMessage(method, "/api/integrations/ai/6/" + path);
                if (method == HttpMethod.Put) request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                request.Headers.Add("X-Test-Actor", actor);
                using var response = await http.SendAsync(request);
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                Assert.True(response.Headers.CacheControl?.NoStore);
            }
            // No connection string exists: reaching any model/audit query would fail this test.
        }
        finally { await app.StopAsync(); }
    }
}
