using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using MySqlConnector;

namespace Payroll.API.Services;

/// <summary>
/// Portable authenticated encryption for provider credentials shared by API
/// instances that use the same configured database. Legacy Data Protection
/// payloads remain readable only so they can be migrated automatically.
/// </summary>
public sealed class PortableIntegrationCredentialProtector
{
    private const string AiPrefix = "ai-credential:v2:";
    private const string StoragePrefix = "storage-credential:v2:";
    private const string PlanPrefix = "frevopilot-plan:v1:";
    private const string RecoveryPrefix = "llm-control-credential:v1:";
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static readonly byte[] AiPurpose = Encoding.UTF8.GetBytes("Payroll.API.RecruitmentAiCredentials.v2");
    private static readonly byte[] StoragePurpose = Encoding.UTF8.GetBytes("Payroll.API.AttachmentStorageCredentials.v2");
    private static readonly byte[] PlanPurpose = Encoding.UTF8.GetBytes("Payroll.API.FrevoPilotVerifiedPlans.v1");
    private static readonly byte[] RecoveryPurpose = Encoding.UTF8.GetBytes("Payroll.API.LocalLlmRecovery.v1");
    private readonly byte[] encryptionKey;
    private readonly IDataProtector legacyAiProtector;
    private readonly IDataProtector legacyStorageProtector;

    public PortableIntegrationCredentialProtector(
        IConfiguration configuration,
        IDataProtectionProvider dataProtectionProvider,
        ILogger<PortableIntegrationCredentialProtector> logger)
    {
        legacyAiProtector = dataProtectionProvider.CreateProtector("Payroll.API.RecruitmentAiScoringCredentials.v1");
        legacyStorageProtector = dataProtectionProvider.CreateProtector("Payroll.API.AttachmentStorageCredentials.v1");
        var configuredKey = configuration["IntegrationCredentialEncryption:MasterKey"]
            ?? configuration["AiCredentialEncryption:MasterKey"]
            ?? Environment.GetEnvironmentVariable("INTEGRATION_CREDENTIAL_MASTER_KEY")
            ?? Environment.GetEnvironmentVariable("AI_CREDENTIAL_MASTER_KEY");
        encryptionKey = string.IsNullOrWhiteSpace(configuredKey)
            ? DeriveDatabaseBoundKey(configuration.GetConnectionString("Default"))
            : SHA256.HashData(Encoding.UTF8.GetBytes(configuredKey.Trim()));
        if (string.IsNullOrWhiteSpace(configuredKey))
            logger.LogInformation("Integration credentials are using portable database-bound encryption.");
    }

    public bool IsPortableAi(string value) => IsPortable(value, AiPrefix);
    public string ProtectAi(string value) => Protect(value, AiPrefix, AiPurpose);
    public bool TryUnprotectAi(string value, out string plaintext) =>
        TryUnprotect(value, AiPrefix, AiPurpose, legacyAiProtector, out plaintext);

    public bool IsPortableStorage(string value) => IsPortable(value, StoragePrefix);
    public string ProtectStorage(string value) => Protect(value, StoragePrefix, StoragePurpose);
    public bool TryUnprotectStorage(string value, out string plaintext) =>
        TryUnprotect(value, StoragePrefix, StoragePurpose, legacyStorageProtector, out plaintext);

    // Reuse the existing portable key, with an isolated authenticated purpose.
    // Query memory never accepts credential ciphertext or legacy key-ring payloads.
    internal string ProtectVerifiedPlan(string value) => Protect(value, PlanPrefix, PlanPurpose);
    internal string? UnprotectVerifiedPlan(string value) =>
        IsPortable(value, PlanPrefix) && TryUnprotect(value, PlanPrefix, PlanPurpose, legacyAiProtector, out var plaintext)
            ? plaintext : null;

    internal string ProtectRecovery(string value) => Protect(value, RecoveryPrefix, RecoveryPurpose);
    internal string? UnprotectRecovery(string value) =>
        IsPortable(value, RecoveryPrefix) && TryUnprotect(value, RecoveryPrefix, RecoveryPurpose, legacyAiProtector, out var plaintext)
            ? plaintext : null;

    private static bool IsPortable(string value, string prefix) =>
        !string.IsNullOrWhiteSpace(value) && value.StartsWith(prefix, StringComparison.Ordinal);

    private string Protect(string value, string prefix, byte[] purpose)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var plaintext = Encoding.UTF8.GetBytes(value.Trim());
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        try
        {
            using var aes = new AesGcm(encryptionKey, TagSize);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, purpose);
            var payload = new byte[NonceSize + TagSize + ciphertext.Length];
            Buffer.BlockCopy(nonce, 0, payload, 0, NonceSize);
            Buffer.BlockCopy(tag, 0, payload, NonceSize, TagSize);
            Buffer.BlockCopy(ciphertext, 0, payload, NonceSize + TagSize, ciphertext.Length);
            return prefix + Convert.ToBase64String(payload);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(ciphertext);
        }
    }

    private bool TryUnprotect(
        string value,
        string prefix,
        byte[] purpose,
        IDataProtector legacyProtector,
        out string plaintext)
    {
        plaintext = "";
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (!IsPortable(value, prefix))
        {
            try
            {
                plaintext = legacyProtector.Unprotect(value);
                return !string.IsNullOrWhiteSpace(plaintext);
            }
            catch
            {
                return false;
            }
        }

        byte[]? decrypted = null;
        try
        {
            var payload = Convert.FromBase64String(value[prefix.Length..]);
            if (payload.Length <= NonceSize + TagSize) return false;
            var nonce = payload.AsSpan(0, NonceSize);
            var tag = payload.AsSpan(NonceSize, TagSize);
            var ciphertext = payload.AsSpan(NonceSize + TagSize);
            decrypted = new byte[ciphertext.Length];
            using var aes = new AesGcm(encryptionKey, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, decrypted, purpose);
            plaintext = Encoding.UTF8.GetString(decrypted);
            return !string.IsNullOrWhiteSpace(plaintext);
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            plaintext = "";
            return false;
        }
        finally
        {
            if (decrypted is not null) CryptographicOperations.ZeroMemory(decrypted);
        }
    }

    private static byte[] DeriveDatabaseBoundKey(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Integration credential encryption requires a database connection or INTEGRATION_CREDENTIAL_MASTER_KEY.");
        var settings = new MySqlConnectionStringBuilder(connectionString);
        // Local and production can reach the same database through different
        // hosts/ports. Bind to the logical database, not its network route, so a
        // credential migrated by production is immediately readable locally.
        var keyMaterial = string.Join('|',
            "Payroll.API.RecruitmentAiCredentials.v2",
            settings.Database.Trim().ToLowerInvariant(),
            settings.UserID,
            settings.Password);
        return SHA256.HashData(Encoding.UTF8.GetBytes(keyMaterial));
    }
}
