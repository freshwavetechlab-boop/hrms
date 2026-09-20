using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Payroll.API.Models;
using static Payroll.API.Services.InternalInterviewPolicy;

namespace Payroll.API.Services;

public sealed class InternalInterviewSpeechService(IConfiguration configuration, IHttpClientFactory clients, EngineRuntimeMonitor monitor) : IDisposable
{
    private readonly SemaphoreSlim admission = new(2, 2);
    private readonly MemoryCache rateWindows = new(new MemoryCacheOptions { SizeLimit = 1024 });
    private readonly object gate = new();
    public const int MaxAudioBytes = 12 * 1024 * 1024;
    public async Task<byte[]> SendAsync(long interviewId, string operation, HttpContent content, string language, CancellationToken ct)
    {
        Require(operation is "transcribe" or "synthesize" && language is "en" or "hi", "Unsupported speech request.");
        var options = configuration.GetSection("InternalInterviews").Get<InternalInterviewOptions>() ?? new();
        Require(Uri.TryCreate(options.SpeechBaseUrl, UriKind.Absolute, out var endpoint) && endpoint.Scheme is "http" or "https"
            && endpoint.UserInfo.Length == 0 && options.SpeechApiKey.Length >= 32, "The private local speech service is not configured. Typed answers remain available.", 503);
        lock (gate)
        {
            var key = $"{interviewId}:{operation}";
            rateWindows.TryGetValue<(DateTime Start, int Count)>(key, out var window);
            if (window.Start < DateTime.UtcNow.AddMinutes(-1)) window = (DateTime.UtcNow, 0);
            Require(window.Count < 8, "Speech request limit reached for this interview. Wait a minute and retry.", 429);
            rateWindows.Set(key, (window.Start, window.Count + 1), new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2) });
        }
        Require(await admission.WaitAsync(TimeSpan.FromSeconds(1), ct), "Local speech is busy. Retry shortly or type your answer.", 429);
        using var observation = monitor.Observe("internal-interview");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(90));
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint!.ToString().TrimEnd('/') + "/" + operation));
            request.Headers.Add("X-Speech-Key", options.SpeechApiKey); request.Headers.Add("X-Language", language); request.Content = content;
            using var response = await clients.CreateClient("InternalInterview").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            Require(response.IsSuccessStatusCode, $"Local speech returned HTTP {(int)response.StatusCode}. No transcript was saved; retry or enter a typed answer.", 502);
            var bytes = await ReadBoundedAsync(await response.Content.ReadAsStreamAsync(timeout.Token), operation == "synthesize" ? MaxAudioBytes : 65536, timeout.Token);
            if (operation == "synthesize") Require(bytes.Length >= 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8), "The local voice service returned invalid audio.", 502);
            observation.Succeeded = true; return bytes;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new InternalInterviewException(504, "Local speech timed out. No answer was submitted; you can retry or type it."); }
        catch (HttpRequestException) { throw new InternalInterviewException(502, "The local speech service is unreachable. You can still type your answer."); }
        finally { admission.Release(); }
    }

    internal static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximum, CancellationToken ct)
    {
        using var output = new MemoryStream(); var buffer = new byte[8192]; int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        { Require(output.Length + read <= maximum, "Speech payload exceeds the permitted size.", 413); output.Write(buffer, 0, read); }
        return output.ToArray();
    }
    public static string ParseTranscript(byte[] json)
    {
        try
        {
            using var data = JsonDocument.Parse(json);
            Require(data.RootElement.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String
                && text.GetString()!.Length is > 0 and <= 8000, "No usable speech transcript was detected. Type the answer or try recording again.", 422);
            return data.RootElement.GetProperty("text").GetString()!;
        }
        catch (JsonException) { throw new InternalInterviewException(502, "The local speech service returned invalid transcript JSON."); }
    }
    public void Dispose() { admission.Dispose(); rateWindows.Dispose(); }
}
