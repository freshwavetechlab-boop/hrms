using Dapper;

namespace Payroll.API.Services;

public sealed partial class RecruitmentAiScoringService
{
    internal async Task<string> ResumeConfigurationFingerprintAsync(int clientId, CancellationToken ct)
    {
        await using var db = Db();
        await db.OpenAsync(ct);
        // Usage/health timestamps change after every inference. Hash configuration only,
        // including credential ciphertext, without returning or logging any credential.
        var models = (await db.QueryAsync(new CommandDefinition(@"
SELECT Id,ClientId,EnableAiScoring,ProviderCode,ModelName,EndpointUrl,MinimumConfidence,
MaximumResumeCharacters,RequestTimeoutSeconds,ApiKeyCipherText,IsActive,IsPrimary,Priority,MonthlyRequestLimit
FROM recruitment_ai_scoring_settings WHERE ClientId IN (@ClientId,0) ORDER BY ClientId,Id",
            new { ClientId = clientId }, cancellationToken: ct))).ToArray();
        var switching = (await db.QueryAsync(new CommandDefinition(@"
SELECT ScopeClientId,AutoSwitchEnabled FROM recruitment_ai_runtime_settings
WHERE ScopeClientId IN (@ClientId,0) ORDER BY ScopeClientId", new { ClientId = clientId }, cancellationToken: ct))).ToArray();
        return ResumePreviewCache.Fingerprint(new { models, switching, LocalLlmProtocol.ResumePromptVersion });
    }
}
