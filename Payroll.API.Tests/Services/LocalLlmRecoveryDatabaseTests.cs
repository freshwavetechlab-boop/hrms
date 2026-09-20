using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class LocalLlmRecoveryDatabaseTests
{
    private sealed class RecoveryDatabaseFactAttribute : FactAttribute
    {
        public RecoveryDatabaseFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HRMS_RECOVERY_TEST_CONNECTION")))
                Skip = "Opt-in: requires a loopback MySQL server; creates/drops only a new GUID test database.";
        }
    }

    [RecoveryDatabaseFact]
    public async Task SeparateApiInstancesShareEncryptedSettingsAndAtomicAuditWithoutTouchingOtherModules()
    {
        var connection = new MySqlConnectionStringBuilder(Environment.GetEnvironmentVariable("HRMS_RECOVERY_TEST_CONNECTION")!);
        Assert.Contains(connection.Server, new[] { "127.0.0.1", "localhost", "::1" });
        var database = "hrms_recovery_" + Guid.NewGuid().ToString("N");
        connection.Database = "";
        await using var owner = new MySqlConnection(connection.ConnectionString); await owner.OpenAsync();
        await owner.ExecuteAsync($"CREATE DATABASE `{database}`");
        try
        {
            connection.Database = database;
            await using var db = new MySqlConnection(connection.ConnectionString); await db.OpenAsync();
            await db.ExecuteAsync("""
                CREATE TABLE modulesettings (Id INT PRIMARY KEY AUTO_INCREMENT,client_id INT NOT NULL,
                  ModuleCode VARCHAR(80) NOT NULL,IsEnabled BOOLEAN NOT NULL DEFAULT FALSE,SettingsJson JSON,
                  CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP,UpdatedAt DATETIME DEFAULT CURRENT_TIMESTAMP,
                  UNIQUE KEY UX_ModuleSettings_Client_Module(client_id,ModuleCode));
                CREATE TABLE auditlogs (Id BIGINT PRIMARY KEY AUTO_INCREMENT,UserId INT,UserEmail VARCHAR(190),
                  Action VARCHAR(120),Resource VARCHAR(190),Method VARCHAR(20),Path VARCHAR(500),StatusCode INT,
                  DetailsJson JSON);
                INSERT INTO modulesettings(client_id,ModuleCode,IsEnabled,SettingsJson)
                  VALUES(20,'leave_attendance',TRUE,'{"unchanged":true}');
                """);
            LocalLlmRecoverySettingsStore Store(string host, bool legacyEnabled)
            {
                var route = new MySqlConnectionStringBuilder(connection.ConnectionString) { Server = host };
                var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
                    ["ConnectionStrings:Default"] = route.ConnectionString,
                    ["LocalLlmRecovery:Enabled"] = legacyEnabled.ToString(),
                    ["LocalLlmRecovery:ApiKey"] = new string('b', 64),
                    ["LocalLlmRecovery:ControlEndpointUrl"] = "https://example.invalid/control",
                    ["LocalLlmRecovery:InferenceEndpointUrl"] = "https://example.invalid/inference",
                }).Build();
                return new(config, new PortableIntegrationCredentialProtector(config,
                    new EphemeralDataProtectionProvider(), NullLogger<PortableIntegrationCredentialProtector>.Instance));
            }
            var local = Store("127.0.0.1", false); var productionReplica = Store("localhost", true);
            var admin = new AuthUser { Id = 3, Email = "admin@example.invalid", Roles = ["super_admin"] };
            var model = new RecruitmentAiScoringSettings { Id = 5, ClientId = 0, ProviderCode = "LocalOpenAICompatible",
                EndpointUrl = "https://example.invalid/inference" };
            var request = new SaveLocalLlmRecoverySettings { Enabled = true, ApiKey = new string('a', 64),
                ControlEndpointUrl = "https://example.invalid/control", InferenceEndpointUrl = model.EndpointUrl };

            Assert.Equal("Server configuration", (await local.GetAsync()).Source);
            var saved = await local.SaveAsync(request, model, admin);
            Assert.Empty(saved.Error); Assert.NotNull(saved.Settings);
            var replica = await productionReplica.GetAsync();
            Assert.Equal("Database", replica.Source); Assert.True(replica.Enabled);
            Assert.Equal(request.ApiKey, replica.ApiKey); Assert.Equal("Ready", replica.CredentialStatus);
            var encryptedBefore = await db.ExecuteScalarAsync<string>("SELECT SettingsJson FROM modulesettings WHERE ModuleCode='local_llm_recovery:0'");
            Assert.DoesNotContain(request.ApiKey, encryptedBefore!);

            // An unchanged blank key preserves ciphertext; explicit off beats prod env=true.
            request.Version = replica.Version; request.ApiKey = ""; request.Enabled = false;
            var disabled = await productionReplica.SaveAsync(request, model, admin);
            Assert.Empty(disabled.Error); Assert.False((await local.GetAsync()).Enabled);
            var encryptedAfter = await db.ExecuteScalarAsync<string>("SELECT SettingsJson FROM modulesettings WHERE ModuleCode='local_llm_recovery:0'");
            using var b = JsonDocument.Parse(encryptedBefore!); using var a = JsonDocument.Parse(encryptedAfter!);
            Assert.Equal(b.RootElement.GetProperty("ApiKeyCipherText").GetString(), a.RootElement.GetProperty("ApiKeyCipherText").GetString());

            var stale = await local.SaveAsync(request, model, admin);
            Assert.Null(stale.Settings); Assert.Contains("changed", stale.Error);
            Assert.Equal(2, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM auditlogs"));
            var audit = string.Join("", await db.QueryAsync<string>("SELECT DetailsJson FROM auditlogs"));
            Assert.DoesNotContain(new string('a', 64), audit); Assert.DoesNotContain("ApiKeyCipherText", audit);
            Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM modulesettings WHERE client_id=20 AND IsEnabled=TRUE AND JSON_EXTRACT(SettingsJson,'$.unchanged')=TRUE"));

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => local.SaveAsync(request, model,
                new AuthUser { Id = 4, ClientId = 20, Roles = ["super_admin"] }));

            // Force audit insert failure and prove the settings update rolls back too.
            await db.ExecuteAsync("CREATE TRIGGER fail_audit BEFORE INSERT ON auditlogs FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Synthetic audit failure'");
            request.Version = disabled.Settings!.Version; request.Enabled = true;
            request.ApiKey = new string('c', 64);
            await Assert.ThrowsAsync<MySqlException>(() => local.SaveAsync(request, model, admin));
            var afterFailure = await productionReplica.GetAsync();
            Assert.False(afterFailure.Enabled); Assert.Equal(new string('a', 64), afterFailure.ApiKey);
            Assert.Equal(disabled.Settings.Version, afterFailure.Version);
        }
        finally
        {
            // This exact GUID name was created above on the asserted loopback server only.
            await owner.ExecuteAsync($"DROP DATABASE `{database}`");
        }
    }
}
