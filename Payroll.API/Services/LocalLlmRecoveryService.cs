using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Payroll.API.Models;

namespace Payroll.API.Services;

public sealed record LocalLlmRuntimeStatus(string State, string Code, string Message, bool CanStart);

// Separate, opt-in control plane. Never uses a model's inference credential or changes its settings.
public sealed class LocalLlmRecoveryService(IConfiguration configuration, IHttpClientFactory clients)
{
    public static bool IsAllowed(AuthUser user) => user.ClientId is null
        && user.Roles.Contains("super_admin", StringComparer.OrdinalIgnoreCase);

    public static LocalLlmRuntimeStatus Describe(string code) => code switch
    {
        "stopped" => new("Stopped", code, "Local LLM is stopped. Start it manually; watchdog safety limits still apply.", true),
        "starting" => new("Starting", code, "Start requested. The model is not ready yet; refresh status shortly.", false),
        "running" => new("Running", code, "Model health and gateway admission verified. You can now use Test.", false),
        "cpu_busy" => new("Blocked", code, "Server CPU is above the safety limit. Wait, then refresh status.", false),
        "memory_low" => new("Blocked", code, "Server free memory is below the safety limit. Start was refused.", false),
        "website_unhealthy" => new("Blocked", code, "Website health checks failed. Start was refused.", false),
        "task_disabled" => new("Blocked", code, "The scheduled task is disabled. Ask the server administrator.", false),
        "cooldown" => new("Blocked", code, "A start was recently attempted. Wait five minutes before another attempt.", false),
        "busy" => new("Blocked", code, "Another control request is running. Refresh status shortly.", false),
        "not_configured" => new("Unavailable", code, "Server recovery control is not configured/enabled on this API.", false),
        "unsupported" => new("Unavailable", code, "Recovery is not configured for this model endpoint.", false),
        "unauthorized" => new("Unavailable", code, "Server recovery authentication failed. Check the separate control credential.", false),
        "unreachable" => new("Unknown", code, "Server control could not be reached. If Start was clicked, its outcome is unknown; refresh status before retrying.", false),
        _ => new("Unavailable", "control_unavailable", "Server control is unavailable or returned an invalid response. Check its configuration and server logs.", false),
    };

    public LocalLlmRuntimeStatus? CheckConfiguration(RecruitmentAiScoringSettings model)
    {
        if (model.ClientId != 0 || model.ProviderCode != "LocalOpenAICompatible") return Describe("unsupported");
        if (!configuration.GetValue<bool>("LocalLlmRecovery:Enabled")
            || !SecureUri(configuration["LocalLlmRecovery:ControlEndpointUrl"], out var control)
            || !SecureUri(configuration["LocalLlmRecovery:InferenceEndpointUrl"], out var inference)
            || control!.Authority != inference!.Authority || control == inference
            || !Regex.IsMatch(configuration["LocalLlmRecovery:ApiKey"] ?? "", "\\A[a-fA-F0-9]{64}\\z"))
            return Describe("not_configured");
        if (!SecureUri(model.EndpointUrl, out var saved) || saved != inference) return Describe("unsupported");
        return null;
    }

    public async Task<LocalLlmRuntimeStatus> SendAsync(RecruitmentAiScoringSettings model, AuthUser user,
        bool start, Guid requestId, CancellationToken cancellationToken = default)
    {
        if (!IsAllowed(user)) throw new UnauthorizedAccessException();
        if (CheckConfiguration(model) is { } unavailable) return unavailable;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        using var request = new HttpRequestMessage(HttpMethod.Post, configuration["LocalLlmRecovery:ControlEndpointUrl"]);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", configuration["LocalLlmRecovery:ApiKey"]);
        request.Content = JsonContent.Create(new { action = start ? "start" : "status", requestId = requestId.ToString("D") });
        try
        {
            using var client = clients.CreateClient("LocalLlmControl");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return Describe("unauthorized");
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > 4096) return Describe("control_unavailable");
            // Bound even a chunked/lying response; never relay remote error text, paths or secrets.
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            var bytes = new byte[4097];
            var length = 0;
            while (length < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(length), deadline.Token);
                if (read == 0) break;
                length += read;
            }
            if (length > 4096) return Describe("control_unavailable");
            using var json = JsonDocument.Parse(bytes.AsMemory(0, length));
            if (!json.RootElement.TryGetProperty("requestId", out var echoed)
                || echoed.GetString() != requestId.ToString("D")) return Describe("control_unavailable");
            return Describe(json.RootElement.GetProperty("code").GetString() ?? "");
        }
        catch (OperationCanceledException) { return Describe("unreachable"); }
        catch (HttpRequestException) { return Describe("unreachable"); }
        catch (IOException) { return Describe("unreachable"); }
        catch (JsonException) { return Describe("control_unavailable"); }
        catch (InvalidOperationException) { return Describe("control_unavailable"); }
        catch (KeyNotFoundException) { return Describe("control_unavailable"); }
    }

    private static bool SecureUri(string? value, out Uri? uri) => Uri.TryCreate(value, UriKind.Absolute, out uri)
        && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);
}
