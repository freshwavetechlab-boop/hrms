using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class LocalLlmRecoverySettingsTests
{
    private const string Key = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly AuthUser Admin = new() { Id = 1, Roles = ["super_admin"] };
    private static RecruitmentAiScoringSettings Model() => new() { Id = 1, ClientId = 0,
        ProviderCode = "LocalOpenAICompatible", EndpointUrl = "https://example.invalid/llm-api.php" };
    private static IConfiguration Config(string host = "localhost", int port = 3306, string password = "synthetic", string? master = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["ConnectionStrings:Default"] = $"Server={host};Port={port};Database=recovery_test;User ID=test;Password={password}",
            ["IntegrationCredentialEncryption:MasterKey"] = master,
            ["LocalLlmRecovery:Enabled"] = "true", ["LocalLlmRecovery:ApiKey"] = new string('b', 64),
            ["LocalLlmRecovery:ControlEndpointUrl"] = "https://old.invalid/control",
            ["LocalLlmRecovery:InferenceEndpointUrl"] = "https://old.invalid/inference",
        }).Build();
    private static PortableIntegrationCredentialProtector Protector(IConfiguration config) => new(config,
        new EphemeralDataProtectionProvider(), NullLogger<PortableIntegrationCredentialProtector>.Instance);
    private static LocalLlmRecoverySettingsStore Store(IConfiguration? config = null) => new(config ?? Config(), Protector(config ?? Config()));
    private static LocalLlmRecoverySettings Ready() => new() { Enabled = true, ApiKey = Key,
        ControlEndpointUrl = "https://example.invalid/llm-control.php", InferenceEndpointUrl = Model().EndpointUrl,
        Source = "Database", CredentialStatus = "Ready", Version = "v1" };
    private static SaveLocalLlmRecoverySettings Request() => new() { Enabled = true,
        ControlEndpointUrl = Ready().ControlEndpointUrl, InferenceEndpointUrl = Ready().InferenceEndpointUrl, Version = "v1" };
    private static string Json(PortableIntegrationCredentialProtector protector, string key = Key) =>
        JsonSerializer.Serialize(new LocalLlmRecoverySettingsStore.Stored { ControlEndpointUrl = Ready().ControlEndpointUrl,
            InferenceEndpointUrl = Ready().InferenceEndpointUrl, ApiKeyCipherText = protector.ProtectRecovery(key), Version = "v1" });

    [Fact]
    public void LocalProductionAndRestartReadSameCiphertextDespiteDifferentHostPortAndKeyRings()
    {
        var local = Protector(Config());
        var deployed = Protector(Config("db-production.internal", 3307));
        var restarted = Protector(Config("another-route.internal", 3308));
        var encrypted = local.ProtectRecovery(Key);
        Assert.NotEqual(encrypted, local.ProtectRecovery(Key)); // randomized authenticated ciphertext
        Assert.Equal(Key, deployed.UnprotectRecovery(encrypted));
        Assert.Equal(Key, restarted.UnprotectRecovery(encrypted));
        Assert.DoesNotContain(Key, encrypted);
    }

    [Fact]
    public void SharedExplicitMasterSupportsDifferentDatabaseCredentials()
    {
        var local = Protector(Config(master: "synthetic-shared-master"));
        var prod = Protector(Config("prod", 3307, "different-db-password", "synthetic-shared-master"));
        Assert.Equal(Key, prod.UnprotectRecovery(local.ProtectRecovery(Key)));
    }

    [Fact]
    public void RecoveryIsPurposeIsolatedFromInferenceStoragePlansAndLegacyKeyRings()
    {
        var p = Protector(Config()); var encrypted = p.ProtectRecovery(Key);
        Assert.False(p.TryUnprotectAi(encrypted, out _));
        Assert.False(p.TryUnprotectStorage(encrypted, out _));
        Assert.Null(p.UnprotectVerifiedPlan(encrypted));
        Assert.Null(p.UnprotectRecovery(p.ProtectAi(Key)));
        Assert.Null(p.UnprotectRecovery(p.ProtectStorage(Key)));
        Assert.Null(p.UnprotectRecovery(p.ProtectVerifiedPlan(Key)));
        Assert.Null(p.UnprotectRecovery(Key));
    }

    [Fact]
    public void ApiAndAuditSerializationNeverExposeSecretOrCiphertext()
    {
        var settings = Store().Decode(true, Json(Protector(Config())));
        Assert.Equal(Key, settings.ApiKey);
        var body = JsonSerializer.Serialize(new { before = settings, after = settings, keyChanged = true });
        Assert.DoesNotContain(Key, body); Assert.DoesNotContain("ApiKey", body);
        Assert.DoesNotContain("credential:v1", body);
        Assert.Contains("Ready", body);
    }

    [Fact]
    public void DatabaseOverridesConflictingEnvironmentAndExplicitOffRemainsOff()
    {
        var store = Store(); var json = Json(Protector(Config()));
        var settings = store.Decode(false, json);
        Assert.False(settings.Enabled); Assert.Equal(Key, settings.ApiKey);
        Assert.Equal(Ready().ControlEndpointUrl, settings.ControlEndpointUrl);
        Assert.Equal("not_configured", LocalLlmRecoveryService.CheckConfiguration(Model(), settings)!.Code);
    }

    [Fact]
    public void WrongEncryptionKeyFailsClosedInsteadOfFallingBackToValidEnvironmentKey()
    {
        var settings = Store(Config(password: "different-credentials")).Decode(true, Json(Protector(Config())));
        Assert.Equal("Database", settings.Source); Assert.Equal("Unreadable", settings.CredentialStatus);
        Assert.Empty(settings.ApiKey);
        Assert.Equal("credential_unreadable", LocalLlmRecoveryService.CheckConfiguration(Model(), settings)!.Code);
    }

    [Fact]
    public void TamperingOrCorruptJsonDoesNotUseEnvironmentFallback()
    {
        var json = Json(Protector(Config()));
        var stored = JsonSerializer.Deserialize<LocalLlmRecoverySettingsStore.Stored>(json)!;
        stored.ApiKeyCipherText = "llm-control-credential:v1:" + Convert.ToBase64String(new byte[120]);
        Assert.Equal("Unreadable", Store().Decode(true, JsonSerializer.Serialize(stored)).CredentialStatus);
        Assert.Throws<JsonException>(() => Store().Decode(true, "not-json"));
    }

    [Theory]
    [InlineData("Bearer abc")]
    [InlineData("abc")]
    [InlineData("\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"")]
    public void InvalidReplacementKeyIsRejected(string key)
    {
        var request = Request(); request.ApiKey = key;
        Assert.Contains("64-character", Store().ValidateSave(request, Model(), Ready()));
    }

    [Fact]
    public void BlankKeepsReadableKeyButCannotEnableWithUnreadableKey()
    {
        Assert.Empty(Store().ValidateSave(Request(), Model(), Ready()));
        var before = Ready(); before.CredentialStatus = "Unreadable"; before.ApiKey = "";
        Assert.Contains("Re-enter", Store().ValidateSave(Request(), Model(), before));
        var request = Request(); request.Enabled = false;
        Assert.Empty(Store().ValidateSave(request, Model(), before));
        request.Enabled = true; request.ApiKey = Key;
        Assert.Empty(Store().ValidateSave(request, Model(), before));
    }

    [Fact]
    public void EndpointChangesRequireExplicitKeyAndStaleEditorsCannotOverwrite()
    {
        var request = Request(); request.ControlEndpointUrl = "https://example.invalid/new-control";
        Assert.Contains("Re-enter", Store().ValidateSave(request, Model(), Ready()));
        request.ApiKey = Key; Assert.Empty(Store().ValidateSave(request, Model(), Ready()));
        request.Version = "old-version";
        Assert.Contains("changed", Store().ValidateSave(request, Model(), Ready()));
    }

    [Theory]
    [InlineData("http://example.invalid/control")]
    [InlineData("https://other.invalid/control")]
    [InlineData("https://example.invalid/llm-api.php")]
    [InlineData("https://user:pass@example.invalid/control")]
    [InlineData("https://example.invalid/control?key=secret")]
    [InlineData("https://example.invalid/control#secret")]
    public void UnsafeControlEndpointsNeverSave(string endpoint)
    {
        var request = Request(); request.ControlEndpointUrl = endpoint; request.ApiKey = Key;
        Assert.Contains("HTTPS", Store().ValidateSave(request, Model(), Ready()));
    }

    private sealed class MutableStore : ILocalLlmRecoverySettingsStore
    {
        public LocalLlmRecoverySettings Current = Ready(); public bool Fail;
        public Task<LocalLlmRecoverySettings> GetAsync(CancellationToken ct = default) =>
            Fail ? throw new InvalidOperationException("Secret exception body") : Task.FromResult(Current);
        public Task<(LocalLlmRecoverySettings? Settings, string Error)> SaveAsync(SaveLocalLlmRecoverySettings request,
            RecruitmentAiScoringSettings model, AuthUser user, CancellationToken ct = default) => throw new NotSupportedException();
    }
    private sealed class Transport : HttpMessageHandler, IHttpClientFactory
    {
        public int Calls;
        public HttpClient CreateClient(string name) => new(this, false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Assert.Equal("Bearer " + Key, request.Headers.Authorization!.ToString());
            Assert.Equal(Ready().ControlEndpointUrl, request.RequestUri!.AbsoluteUri);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new {
                code = "stopped", requestId = json.RootElement.GetProperty("requestId").GetString() })) };
        }
    }

    [Fact]
    public async Task RuntimeReadsLatestDbSettingsEachTimeWithoutRestartAndFailsClosedOnOutage()
    {
        var store = new MutableStore(); var http = new Transport(); var service = new LocalLlmRecoveryService(store, http);
        Assert.Equal("stopped", (await service.SendAsync(Model(), Admin, false, Guid.NewGuid())).Code);
        store.Current.Enabled = false;
        Assert.Equal("not_configured", (await service.SendAsync(Model(), Admin, true, Guid.NewGuid())).Code);
        store.Fail = true;
        var failed = await service.SendAsync(Model(), Admin, true, Guid.NewGuid());
        Assert.Equal("settings_unavailable", failed.Code); Assert.DoesNotContain("Secret", failed.Message);
        Assert.Equal(1, http.Calls);
    }
}
