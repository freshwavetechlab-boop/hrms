using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Payroll.API.Models;
using static Payroll.API.Services.InternalInterviewPolicy;

namespace Payroll.API.Services;

// Uses only the operator's self-hosted LiveKit. No cloud fallback or credentials in UI responses.
public sealed class InternalInterviewTransport(IConfiguration configuration, IHttpClientFactory clients, TimeProvider time)
{
    private InternalInterviewOptions Options => configuration.GetSection("InternalInterviews").Get<InternalInterviewOptions>() ?? new();
    public object Join(InterviewAccess access)
    {
        RequireOpen(access.Context, access.Session, time.GetUtcNow().UtcDateTime);
        Require(access.Session.Status == "Live" && access.Session.ConsentAtUtc.HasValue, "Wait for the panel to start the consented session.", 409);
        Require(access.Session.MediaState == "Ready", "The media room is not ready. Check the visible media status and retry.", 409);
        Require(time.GetUtcNow().UtcDateTime < ScheduleUtc(access.Context.ScheduledEnd, access.Context.TimeZoneId), "The scheduled interview duration has ended.", 409);
        var options = Options;
        ValidateConfiguration(options);
        var identity = access.IsCandidate ? "candidate" : $"panel-{access.User!.Id}";
        var token = CreateToken(options.LiveKitApiKey, options.LiveKitApiSecret, access.Session.RoomId, identity,
            time.GetUtcNow(), TimeSpan.FromMinutes(2), false);
        return new { url = options.LiveKitUrl, token, identity, expiresInSeconds = 120 };
    }

    public Task<JsonElement> CreateRoomAsync(InternalInterviewSession session, int panelCount, CancellationToken ct)
    {
        Require(session.Status == "Live" && session.ConsentAtUtc.HasValue, "Only a consented live interview may open a room.", 409);
        Require(session.ScheduledEndUtc > time.GetUtcNow().UtcDateTime, "The media room cannot start after its scheduled end.", 409);
        // Keep the room through its permitted schedule so a five-minute network outage does not
        // expire it and tempt a second recording overwrite. The lifecycle worker closes it at end.
        var roomLifetime = Math.Clamp((int)Math.Ceiling((session.ScheduledEndUtc - time.GetUtcNow().UtcDateTime).TotalSeconds) + 120, 120, 16_320);
        var body = new Dictionary<string, object> { ["name"] = session.RoomId, ["empty_timeout"] = roomLifetime,
            ["departure_timeout"] = roomLifetime, ["max_participants"] = Math.Clamp(panelCount + 3, 3, 30) };
        if (session.Configuration.RecordingEnabled)
        {
            Require(session.RecordingConsent, "Candidate recording consent is missing.", 403);
            Require(Path.IsPathFullyQualified(Options.RecordingDirectory), "Configure a private shared recording volume before enabling recording.", 503);
            var folder = Options.EgressRecordingDirectory;
            Require(folder.StartsWith('/') && !folder.Contains("..") && !folder.Contains('{') && !folder.Contains('}'), "Configure an absolute private Egress recording directory.", 503);
            body["egress"] = new { room = new { layout = "grid", preset = "H264_720P_30",
                file_outputs = new[] { new { file_type = "MP4", filepath = folder.TrimEnd('/') + "/" + session.RoomId + ".mp4", disable_manifest = true } } } };
        }
        return RpcAsync("RoomService/CreateRoom", session.RoomId, body, false, ct);
    }

    public Task<JsonElement> CloseRoomAsync(string room, CancellationToken ct) =>
        RpcAsync("RoomService/DeleteRoom", room, new { room }, false, ct, allowMissing: true);

    public Task<JsonElement> RecordingsAsync(string room, CancellationToken ct) =>
        RpcAsync("Egress/ListEgress", room, new { room_name = room }, true, ct);

    public Task<JsonElement> ParticipantsAsync(string room, CancellationToken ct) =>
        RpcAsync("RoomService/ListParticipants", room, new { room }, false, ct);

    public Task<JsonElement> RemoveParticipantAsync(string room, string identity, CancellationToken ct) =>
        RpcAsync("RoomService/RemoveParticipant", room, new { room, identity }, false, ct, allowMissing: true);

    private async Task<JsonElement> RpcAsync(string method, string room, object body, bool recording, CancellationToken ct, bool allowMissing = false)
    {
        var options = Options; ValidateConfiguration(options);
        Require(Uri.TryCreate(options.LiveKitApiUrl, UriKind.Absolute, out var endpoint) && endpoint.UserInfo == ""
            && endpoint.Scheme is "https" or "http", "Configure the self-hosted LiveKit API endpoint.", 503);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint!, "/twirp/livekit." + method));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(options.LiveKitApiKey, options.LiveKitApiSecret,
            room, "hrms-service", time.GetUtcNow(), TimeSpan.FromMinutes(1), true, recording,
            method is "RoomService/ListParticipants" or "RoomService/RemoveParticipant"));
        request.Content = JsonContent.Create(body);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var response = await clients.CreateClient("InternalInterview").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var missing = allowMissing && response.StatusCode == System.Net.HttpStatusCode.NotFound;
            Require(response.IsSuccessStatusCode || missing, $"Self-hosted media service returned HTTP {(int)response.StatusCode}. Media work will retry; no successful recording is assumed.", 502);
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var output = new MemoryStream(); var buffer = new byte[8192]; int read;
            while ((read = await stream.ReadAsync(buffer, timeout.Token)) > 0)
            { Require(output.Length + read <= 262144, "Media response exceeded its bounded size.", 502); output.Write(buffer, 0, read); }
            using var json = JsonDocument.Parse(output.ToArray());
            // A proxy's generic 404 is not evidence that a private room was removed.
            if (missing) Require(json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("code", out var code) && code.GetString() == "not_found", "The media service did not confirm that the room is absent; closure will retry.", 502);
            return json.RootElement.Clone();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new InternalInterviewException(504, "Self-hosted media service timed out; the server will retry pending room work."); }
        catch (HttpRequestException) { throw new InternalInterviewException(502, "Self-hosted media service is unreachable. Pending room work will retry."); }
        catch (JsonException) { throw new InternalInterviewException(502, "Self-hosted media service returned invalid JSON."); }
    }

    private static void ValidateConfiguration(InternalInterviewOptions options)
    {
        Require(Uri.TryCreate(options.LiveKitUrl, UriKind.Absolute, out var url) && url.UserInfo == ""
            && (url.Scheme == "wss" || (url.Scheme == "ws" && url.IsLoopback)), "Configure a self-hosted LiveKit secure WebSocket URL.", 503);
        Require(!string.IsNullOrWhiteSpace(options.LiveKitApiKey) && options.LiveKitApiSecret.Length >= 32, "LiveKit signing credentials are not configured.", 503);
    }

    internal static string CreateToken(string key, string secret, string room, string identity, DateTimeOffset now, TimeSpan lifetime, bool service, bool recording = false, bool manageParticipants = false)
    {
        Require(Guid.TryParseExact(room, "N", out _), "Invalid interview room.");
        Require(lifetime > TimeSpan.Zero && lifetime <= TimeSpan.FromMinutes(2), "Invalid room token lifetime.");
        var video = service
            ? new Dictionary<string, object> { ["room"] = room, [recording ? "roomRecord" : manageParticipants ? "roomAdmin" : "roomCreate"] = true }
            : new Dictionary<string, object> { ["room"] = room, ["roomJoin"] = true, ["canPublish"] = true,
                ["canSubscribe"] = true, ["canPublishData"] = false, ["canUpdateOwnMetadata"] = false,
                ["canPublishSources"] = new[] { "camera", "microphone" } };
        var payload = new { iss = key, sub = identity, nbf = now.AddSeconds(-5).ToUnixTimeSeconds(), exp = now.Add(lifetime).ToUnixTimeSeconds(), video };
        static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var unsigned = Encode(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"typ\":\"JWT\"}")) + "." + Encode(JsonSerializer.SerializeToUtf8Bytes(payload));
        return unsigned + "." + Encode(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(unsigned)));
    }
}
