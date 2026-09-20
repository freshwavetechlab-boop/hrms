using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Payroll.API.Models;

namespace Payroll.API.Services;

public sealed record LocalLlmRuntimeStatus(string State, string Code, string Message, bool CanStart);

// Separate, opt-in control plane. Never uses a model's inference credential or changes its settings.
public sealed class LocalLlmRecoveryService(ILocalLlmRecoverySettingsStore settings, IHttpClientFactory clients)
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
        "not_configured" => new("Unavailable", code, "Open Recovery settings to configure and enable server recovery.", false),
        "credential_unreadable" => new("Unavailable", code, "The saved recovery key cannot be decrypted here. Check that local and production use the same integration encryption key/database credentials, or re-enter the control key in Recovery settings.", false),
        "settings_unavailable" => new("Unavailable", code, "Recovery settings could not be read from the database. No server action was sent; server environment settings were not used as a fallback.", false),
        "unsupported" => new("Unavailable", code, "Recovery is not configured for this model endpoint.", false),
        "unauthorized" => new("Unavailable", code, "Server recovery returned HTTP 401/403. Check the separate control key in Recovery settings and any gateway access restrictions.", false),
        "unreachable" => new("Unknown", code, "Server control could not be reached. If Start was clicked, its outcome is unknown; refresh status before retrying.", false),
        _ => new("Unavailable", "control_unavailable", "Server control is unavailable or returned an invalid response. Check its configuration and server logs.", false),
    };

    internal static LocalLlmRuntimeStatus? CheckConfiguration(RecruitmentAiScoringSettings model, LocalLlmRecoverySettings options)
    {
        if (model.ClientId != 0 || model.ProviderCode != "LocalOpenAICompatible") return Describe("unsupported");
        if (options.CredentialStatus == "Unreadable") return Describe("credential_unreadable");
        if (!options.Enabled || !LocalLlmRecoverySettingsStore.ValidEndpoints(options.ControlEndpointUrl, options.InferenceEndpointUrl)
            || !LocalLlmRecoverySettingsStore.ValidKey(options.ApiKey))
            return Describe("not_configured");
        if (!LocalLlmRecoverySettingsStore.SecureUri(model.EndpointUrl, out var saved) || saved != new Uri(options.InferenceEndpointUrl))
            return Describe("unsupported");
        return null;
    }

    public async Task<LocalLlmRuntimeStatus> SendAsync(RecruitmentAiScoringSettings model, AuthUser user,
        bool start, Guid requestId, CancellationToken cancellationToken = default)
    {
        if (!IsAllowed(user)) throw new UnauthorizedAccessException();
        LocalLlmRecoverySettings options;
        try { options = await settings.GetAsync(cancellationToken); }
        catch (Exception exception) when (exception is MySqlConnector.MySqlException or JsonException or InvalidOperationException)
        { return Describe("settings_unavailable"); }
        if (CheckConfiguration(model, options) is { } unavailable) return unavailable;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        using var request = new HttpRequestMessage(HttpMethod.Post, options.ControlEndpointUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
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

}
