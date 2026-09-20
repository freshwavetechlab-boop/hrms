using System.Text.Json;
using System.Text.RegularExpressions;
using MySqlConnector;

namespace Payroll.API.Services;

// Persist only bounded, host-owned codes in the existing FailureCode column.
// Never store provider error prose, SQL values, URLs, credentials or document content.
public static class EngineFailureCatalog
{
    private static readonly IReadOnlyDictionary<string, string> Reasons = new Dictionary<string, string>
    {
        ["ENGINE_FAILED"] = "Operation failed. No additional safe diagnostic was supplied; check the source task/server log.",
        ["REQUEST_CANCELLED"] = "The caller or application cancelled this operation.",
        ["REQUEST_TIMEOUT"] = "The configured request deadline expired; completion on the remote server is not confirmed.",
        ["TRANSPORT_FAILED"] = "The provider could not be reached or its connection failed. Check connectivity/TLS and provider health.",
        ["DB_VALUE_TOO_LONG"] = "Save failed: a field exceeds its database column length. Shorten the field or review its storage contract.",
        ["DB_QUALIFICATION_TOO_LONG"] = "Save failed: Qualification exceeds its database column length. The current HRMS schema allows 250 characters; parser/form validation must use the same limit.",
        ["DB_DUPLICATE"] = "Save failed because a record conflicts with an existing unique value.",
        ["DB_UNAVAILABLE"] = "The database operation failed. Check the source task/server log; no SQL or field values are included here.",
        ["LOCAL_LLM_STOPPED"] = "Gateway reports that the local LLM is stopped. The shutdown reason was not supplied; check the server watchdog log or recovery status.",
        ["LOCAL_LLM_NOT_CONFIGURED"] = "Gateway reports that the local LLM endpoint is not configured.",
        ["LOCAL_LLM_BACKEND_UNAVAILABLE"] = "Gateway reports that its local backend transport or limiter is unavailable.",
        ["LOCAL_LLM_INFERENCE_UNAVAILABLE"] = "Gateway could not complete local inference. Its response did not provide a verified shutdown cause.",
        ["AI_BUSY"] = "No inference was sent: local admission is busy or in a safety cooldown. Retry after the current work settles.",
        ["AI_INPUT_TOO_LARGE"] = "The input exceeds the provider's configured size/context limit. Reduce the input or review those limits.",
        ["AI_UNSUPPORTED_INPUT"] = "This model cannot process the supplied input type. Extract text/OCR or use manual review.",
        ["AI_INVALID_RESPONSE"] = "The provider returned an invalid/incomplete response. Existing parser data must be reviewed; no missing facts are invented.",
        ["AI_OUTPUT_TRUNCATED"] = "Provider output reached its token limit before a complete result was returned.",
        ["AI_CONFIGURATION"] = "The AI model/provider configuration is missing or invalid.",
        ["AI_USAGE_LIMIT"] = "The configured AI usage limit has been reached.",
    };

    public static string Clean(string? code) => code is not null && (Reasons.ContainsKey(code)
        || Regex.IsMatch(code, @"\AHTTP [45][0-9]{2}\z")) ? code : "ENGINE_FAILED";

    public static string Describe(string? code)
    {
        if (string.IsNullOrEmpty(code)) return "";
        code = Clean(code);
        if (Reasons.TryGetValue(code, out var reason)) return reason;
        return code switch {
            "HTTP 401" or "HTTP 403" => "Access/authentication was rejected. Check the relevant account, permissions or provider API key.",
            "HTTP 429" => "The provider/service rate or quota limit was reached. This response alone does not distinguish billing quota from request throttling.",
            "HTTP 503" => "The provider/service is unavailable. No verified shutdown reason was included in the response.",
            "HTTP 504" => "The gateway deadline expired; remote processing may still be settling.",
            "HTTP 400" or "HTTP 413" or "HTTP 422" => "The service rejected the request or its size. Review the source task's validation details.",
            _ => "The service returned " + code + ". Check the source task/server log for further details."
        };
    }

    public static string FromException(Exception error) => error switch
    {
        OperationCanceledException => "REQUEST_CANCELLED",
        TimeoutException => "REQUEST_TIMEOUT",
        HttpRequestException http when http.StatusCode is not null => Clean($"HTTP {(int)http.StatusCode}"),
        HttpRequestException or IOException => "TRANSPORT_FAILED",
        MySqlException sql => FromDatabase(sql.Number, sql.Message),
        _ => "ENGINE_FAILED"
    };

    internal static string FromDatabase(int number, string message) => number switch
    {
        1406 when message.Contains("column 'Qualification'", StringComparison.OrdinalIgnoreCase) => "DB_QUALIFICATION_TOO_LONG",
        1406 => "DB_VALUE_TOO_LONG",
        1062 => "DB_DUPLICATE",
        _ => "DB_UNAVAILABLE"
    };

    public static string FromAiStatus(string status) => status switch {
        "TimedOut" => "REQUEST_TIMEOUT", "TransportError" => "TRANSPORT_FAILED",
        "LocalBusy" => "AI_BUSY", "InputTooLarge" => "AI_INPUT_TOO_LARGE",
        "UnsupportedInput" => "AI_UNSUPPORTED_INPUT", "OutputTruncated" => "AI_OUTPUT_TRUNCATED",
        "InvalidResponse" => "AI_INVALID_RESPONSE", "ConfigurationError" => "AI_CONFIGURATION",
        "UsageLimitReached" => "AI_USAGE_LIMIT", _ => "ENGINE_FAILED"
    };

    public static string FromProviderHttp(int status, bool local, string? body)
    {
        if (local && status == 503 && body is { Length: <= 131072 })
        {
            try
            {
                using var json = JsonDocument.Parse(body);
                var code = json.RootElement.GetProperty("error").GetProperty("code").GetString();
                return code switch {
                    "unavailable" => "LOCAL_LLM_STOPPED", "not_configured" => "LOCAL_LLM_NOT_CONFIGURED",
                    "backend_unavailable" => "LOCAL_LLM_BACKEND_UNAVAILABLE",
                    "inference_unavailable" or "inference_failed" => "LOCAL_LLM_INFERENCE_UNAVAILABLE",
                    _ => "HTTP 503"
                };
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException) { }
        }
        return Clean($"HTTP {status}");
    }
}
