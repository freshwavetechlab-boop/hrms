using System.Text.Json;

namespace Payroll.API.Services;

public sealed partial class RecruitmentAiScoringService
{
    // Uses the configured provider order, autoswitch, credentials, admission and usage ledger.
    // Unlike analytics, mapping must not be rewritten as a SQL planning prompt for local models.
    internal async Task<(JsonElement Payload, string Model)> GenerateExcelMappingAsync(
        string prompt, string instruction, CancellationToken ct)
    {
        var (models, autoSwitch) = await LoadProviderCandidatesAsync(GlobalClientId, null, ct);
        if (models.Count == 0) throw new InvalidOperationException("No mapping provider is configured.");
        if (!await EnterInferenceAsync(models, ct)) throw new InvalidOperationException("AI admission is busy.");
        try
        {
            foreach (var model in models)
            {
                if (MonthlyLimitReached(model) || string.IsNullOrWhiteSpace(TryUnprotect(model.ApiKeyCipherText)))
                { if (autoSwitch) continue; break; }
                // Leave time for another provider if autoswitch is configured.
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attempt.CancelAfter(TimeSpan.FromSeconds(autoSwitch ? 25 : 50));
                try
                {
                    var sent = await SendProviderAsync(model, TryUnprotect(model.ApiKeyCipherText)!, prompt, instruction, 4096, null, "", attempt.Token);
                    if (ProviderRequestWasSent(sent.Status))
                        await RecordProviderUsageAsync(model, sent.Body, sent.Success, true,
                            sent.Success ? "" : "Excel column mapping suggestion failed.", ct);
                    if (sent.Success)
                    {
                        using var json = JsonDocument.Parse(ExtractResponseJson(sent.Body, model.ProviderCode));
                        if (json.RootElement.ValueKind == JsonValueKind.Object
                            && json.RootElement.TryGetProperty("columns", out var columns) && columns.ValueKind == JsonValueKind.Array)
                            return (json.RootElement.Clone(), model.ModelName);
                    }
                }
                catch (JsonException) { /* Try the next configured provider, never apply malformed output. */ }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                if (!autoSwitch) break;
            }
            throw new InvalidOperationException("AI mapping is unavailable.");
        }
        finally { inferenceGate.Release(); }
    }
}
