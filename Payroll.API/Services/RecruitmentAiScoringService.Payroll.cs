using System.Text.Json;

namespace Payroll.API.Services;

public sealed partial class RecruitmentAiScoringService
{
    // Payroll uses the saved global provider; it never changes ATS settings or promotes a model.
    internal async Task<(JsonElement Payload, string Model, string Provider)> GeneratePayrollReviewAsync(string prompt, string instruction, CancellationToken ct)
    {
        var (models, _) = await LoadProviderCandidatesAsync(GlobalClientId, null, ct);
        var model = models.FirstOrDefault() ?? throw new InvalidOperationException("Configure an active provider in AI Integrations before requesting AI review.");
        if (MonthlyLimitReached(model)) throw new InvalidOperationException("The configured AI provider has reached its usage limit. No approval was issued.");
        var key = TryUnprotect(model.ApiKeyCipherText);
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("The AI credential is unavailable. Check AI Integrations.");
        if (!await EnterInferenceAsync([model], ct)) throw new InvalidOperationException("AI is busy. Request another review after the current inference finishes.");
        try
        {
            var response = await SendProviderAsync(model, key, prompt, instruction, 2500, null, "", ct);
            if (ProviderRequestWasSent(response.Status))
                await RecordProviderUsageAsync(model, response.Body, response.Success, true, response.Success ? "" : "Payroll advisor review failed.", ct);
            if (!response.Success) throw new InvalidOperationException("AI review could not finish. Check provider availability or usage limits in AI Integrations. Payroll settings are unchanged.");
            using var json = JsonDocument.Parse(ExtractResponseJson(response.Body, model.ProviderCode));
            return (json.RootElement.Clone(), model.ModelName, model.ProviderCode);
        }
        finally { inferenceGate.Release(); }
    }
}
