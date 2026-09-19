using System.Text;
using Payroll.API.Models;

namespace Payroll.API.Services;

public sealed partial class RecruitmentAiScoringService
{
    // Separate bounded admission for the single CPU-only generation slot. Never grow an unbounded queue.
    private readonly SemaphoreSlim localInferenceGate = new(1, 1);
    // Both cooldown fields are read/written only while localInferenceGate is held.
    private DateTime localRetryAfterUtc;
    private bool localCoolingAfterTimeout;
    private const int LocalBackendSettlementSeconds = 90;

    private void CoolDownLocalBackendAfterTimeout()
    {
        localRetryAfterUtc = DateTime.UtcNow.AddSeconds(LocalBackendSettlementSeconds);
        localCoolingAfterTimeout = true;
    }

    private async Task<bool> EnterInferenceAsync(List<RecruitmentAiScoringSecretRow> models, CancellationToken cancellationToken)
    {
        if (models.Count > 0 && LocalLlmProtocol.IsLocal(models[0].ProviderCode))
            return await inferenceGate.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        await inferenceGate.WaitAsync(cancellationToken);
        return true;
    }

    internal async Task<int> AnalyticsRunBudgetSecondsAsync(long? modelId, CancellationToken cancellationToken)
    {
        var (models, _) = await LoadProviderCandidatesAsync(GlobalClientId, modelId, cancellationToken);
        var local = models.Where(model => LocalLlmProtocol.IsLocal(model.ProviderCode)).ToList();
        return local.Count == 0 ? 240 : Math.Clamp(local.Max(model => model.RequestTimeoutSeconds) * 3 + 60, 240, 900);
    }

    private async Task<ProviderSendResult> SendLocalProviderAsync(RecruitmentAiScoringSecretRow settings, string key,
        string prompt, string? instruction, byte[]? document, CancellationToken cancellationToken)
    {
        if (document is { Length: > 0 }) return new(false, "UnsupportedInput", "Local LLM accepts extracted text only; use OCR/manual review for scanned PDFs.", "");
        HttpRequestMessage message;
        try { message = LocalLlmProtocol.CreateRequest(settings, key, prompt, instruction); }
        catch (LocalLlmException error) { return new(false, error.Status, error.Message, ""); }
        using (message)
        {
            if (!await localInferenceGate.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken))
                return new(false, "LocalBusy", "Local LLM is busy. No request was sent; retry when the current generation finishes.", "");
            var requestStarted = false;
            try
            {
                if (localRetryAfterUtc > DateTime.UtcNow)
                    return new(false, "LocalBusy", localCoolingAfterTimeout
                        ? "Local LLM is in a 90-second cooldown after a gateway/request timeout or cancellation so the backend can settle. No request was sent; retry shortly."
                        : "Local LLM is cooling down after a busy/rate-limit response; retry shortly.", "");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.RequestTimeoutSeconds, 60, 600)));
                using var client = httpClientFactory.CreateClient("LocalLlm");
                client.Timeout = Timeout.InfiniteTimeSpan; // linked cancellation, not HttpClient's implicit 100-second cap
                requestStarted = true;
                using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                // A public gateway deadline does not prove CPU prefill stopped. Apply
                // a bounded shared cooldown before another ATS/JD/analytics call can send.
                if ((int)response.StatusCode == 504) CoolDownLocalBackendAfterTimeout();
                // Bound the response before materializing it; never log provider body or document text.
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var bodyBuffer = new MemoryStream();
                var buffer = new byte[8192];
                int read;
                while ((read = await stream.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    if (bodyBuffer.Length + read > 131072) return new(false, "InvalidResponse", "Local LLM response exceeded its safe size limit.", "");
                    bodyBuffer.Write(buffer, 0, read);
                }
                var body = Encoding.UTF8.GetString(bodyBuffer.ToArray());
                if (!response.IsSuccessStatusCode)
                {
                    if ((int)response.StatusCode == 429)
                    {
                        localRetryAfterUtc = DateTime.UtcNow.AddSeconds(Math.Clamp(response.Headers.RetryAfter?.Delta?.TotalSeconds ?? 10, 5, 60));
                        localCoolingAfterTimeout = false;
                    }
                    return new(false, "ProviderError", (int)response.StatusCode switch
                    {
                        429 => "Local LLM returned HTTP 429: its single generation slot or rate limit is busy. Retry shortly.",
                        504 => "Local LLM returned HTTP 504: the gateway deadline expired. A 90-second cooldown is active while the backend settles; align gateway and HRMS timeouts or shorten the request.",
                        401 or 403 => "Local LLM authentication failed. Check the public gateway API key.",
                        400 or 413 => "Local LLM rejected the input/context size or request configuration. Check the gateway limits.",
                        _ => $"Local LLM returned HTTP {(int)response.StatusCode}. Check the gateway health."
                    }, "");
                }
                try { LocalLlmProtocol.ReadResponse(body); }
                catch (LocalLlmException error) { return new(false, error.Status, error.Message, body); }
                return new(true, "Completed", "", body);
            }
            catch (OperationCanceledException)
            {
                // Also protect against an aborted browser/request while remote prefill
                // is still winding down. Cancellation before admission never gets here.
                if (requestStarted) CoolDownLocalBackendAfterTimeout();
                if (cancellationToken.IsCancellationRequested) throw;
                return new(false, "TimedOut", "Local LLM exceeded its configured HRMS request timeout. A 90-second cooldown is active while the backend settles; existing parser results were retained.", "");
            }
            catch (HttpRequestException exception)
            {
                // Expose only the framework classification, never exception text,
                // URLs, headers or the provider body. No automatic POST replay.
                return new(false, "TransportError",
                    $"Local LLM transport failed ({exception.HttpRequestError}). Check endpoint connectivity and TLS.", "");
            }
            finally { localInferenceGate.Release(); }
        }
    }
}
