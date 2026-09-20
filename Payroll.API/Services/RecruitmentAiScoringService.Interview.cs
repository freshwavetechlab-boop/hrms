using System.Text.Json;
using Payroll.API.Models;

namespace Payroll.API.Services;

public sealed partial class RecruitmentAiScoringService
{
    // Internal interview caller supplies only an operator-configured ID; candidates cannot select models.
    // Reuse the existing encrypted key, local admission/cooldown and usage ledger. No cloud fallbacks.
    internal async Task<JsonElement> GenerateInterviewJsonAsync(long? modelId, string prompt, string instruction, CancellationToken ct)
    {
        var (models, _) = await LoadProviderCandidatesAsync(GlobalClientId, modelId, ct);
        var model = models.FirstOrDefault();
        if (model is null || !LocalLlmProtocol.IsLocal(model.ProviderCode))
            throw new InternalInterviewException(503, "Choose a saved Local LLM model for internal interviews. Cloud providers are not used.");
        if (!model.IsActive || !model.EnableAiScoring)
            throw new InternalInterviewException(503, "The configured local model is disabled in AI Integrations.");
        if (MonthlyLimitReached(model)) throw new InternalInterviewException(429, "The local model's configured usage limit has been reached.");
        var key = TryUnprotect(model.ApiKeyCipherText);
        if (string.IsNullOrWhiteSpace(key)) throw new InternalInterviewException(503, "The saved local AI credential is unavailable. Check AI Integrations.");
        var response = await SendLocalProviderAsync(model, key, prompt, instruction, null, ct);
        if (ProviderRequestWasSent(response.Status))
            await RecordProviderUsageAsync(model, response.Body, response.Success, true, response.Success ? "" : "Internal interview local inference failed.", ct);
        if (!response.Success) throw new InternalInterviewException(502, response.Error);
        try
        {
            using var json = JsonDocument.Parse(LocalLlmProtocol.ReadResponse(response.Body));
            if (json.RootElement.ValueKind != JsonValueKind.Object)
                throw new InternalInterviewException(502, "The local model returned a non-object interview response. No draft was applied.");
            return json.RootElement.Clone();
        }
        catch (LocalLlmException error) { throw new InternalInterviewException(502, error.Message); }
        catch (JsonException) { throw new InternalInterviewException(502, "The local model returned malformed interview JSON. No draft was applied."); }
    }
}
