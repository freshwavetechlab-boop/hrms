using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Payroll.API.Models;
using static Payroll.API.Services.InternalInterviewPolicy;

namespace Payroll.API.Services;

internal sealed record InterviewMediaEvent(string EventId, string RoomId, string EventType, string Identity,
    string ParticipantSessionId, string Role, DateTime OccurredAtUtc);

// LiveKit's raw-body JWT + sha256 contract. No client JWT, cookie or candidate link can authorize callbacks.
internal static class InternalInterviewMediaWebhook
{
    internal const int MaxBodyBytes = 65536;
    internal static InterviewMediaEvent? Verify(byte[] body, string authorization, InternalInterviewOptions options, DateTimeOffset now)
    {
        Require(body.Length is > 0 and <= MaxBodyBytes, "Media callback exceeds its allowed size.", 413);
        Require(options.LiveKitWebhooksEnabled && options.LiveKitApiKey.Length > 0 && options.LiveKitApiSecret.Length >= 32,
            "Media callbacks are not configured.", 503);
        Require(authorization.Length is > 0 and <= 4096, "Invalid media callback authorization.", 401);
        var token = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? authorization[7..] : authorization;
        try
        {
            var parts = token.Split('.');
            Require(parts.Length == 3, "Invalid media callback authorization.", 401);
            using var header = JsonDocument.Parse(Decode(parts[0]));
            Require(header.RootElement.ValueKind == JsonValueKind.Object && header.RootElement.GetProperty("alg").GetString() == "HS256"
                && !header.RootElement.TryGetProperty("crit", out _), "Invalid media callback authorization.", 401);
            var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(options.LiveKitApiSecret), Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]));
            Require(CryptographicOperations.FixedTimeEquals(expected, Decode(parts[2])), "Invalid media callback authorization.", 401);
            using var jwt = JsonDocument.Parse(Decode(parts[1])); var claims = jwt.RootElement;
            Require(claims.ValueKind == JsonValueKind.Object && claims.GetProperty("iss").GetString() == options.LiveKitApiKey
                && claims.GetProperty("exp").TryGetInt64(out var expiry) && expiry > now.ToUnixTimeSeconds() - 10
                && (!claims.TryGetProperty("nbf", out var nbf) || nbf.TryGetInt64(out var before) && before <= now.ToUnixTimeSeconds() + 10),
                "Invalid or expired media callback authorization.", 401);
            var hash = Convert.FromBase64String(claims.GetProperty("sha256").GetString() ?? "");
            Require(CryptographicOperations.FixedTimeEquals(SHA256.HashData(body), hash), "Media callback body verification failed.", 401);
        }
        catch (Exception error) when (error is FormatException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        { throw new InternalInterviewException(401, "Invalid media callback authorization."); }

        try
        {
            using var json = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 24 }); var root = json.RootElement;
            var eventType = root.GetProperty("event").GetString() ?? "";
            // Other room/track/Egress events may share the server callback URL; acknowledge without storing raw payloads.
            if (eventType is not ("participant_joined" or "participant_left" or "participant_connection_aborted" or "room_started" or "room_finished")) return null;
            var room = root.GetProperty("room").GetProperty("name").GetString() ?? "";
            // MySQL's default collation is case-insensitive; LiveKit room names are not.
            // Only our canonical lower-case opaque room name may address this database row.
            if (!Guid.TryParseExact(room, "N", out var roomId) || room != roomId.ToString("N")) return null;
            var eventId = root.GetProperty("id").GetString() ?? "";
            Require(SafeId(eventId, 128), "Media callback has an invalid event ID.");
            var timestamp = root.TryGetProperty("createdAt", out var camel) ? camel : root.GetProperty("created_at");
            Require(long.TryParse(timestamp.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
                && seconds >= 1577836800 && seconds <= now.ToUnixTimeSeconds() + 30, "Media callback has an invalid event time.");
            var identity = ""; var sid = ""; var role = "Room";
            if (eventType.StartsWith("participant_", StringComparison.Ordinal))
            {
                var participant = root.GetProperty("participant");
                identity = participant.GetProperty("identity").GetString() ?? "";
                sid = participant.GetProperty("sid").GetString() ?? "";
                Require(identity.Length is > 0 and <= 256 && !identity.Any(char.IsControl) && SafeId(sid, 128), "Media callback has an invalid participant.");
                var recorder = participant.TryGetProperty("kind", out var kind) && kind.ToString() is "EGRESS" or "2";
                role = recorder ? "Recording service" : identity == "candidate" ? "Candidate"
                    : identity.StartsWith("panel-", StringComparison.Ordinal) && int.TryParse(identity.AsSpan(6), out var id) && id > 0 && identity == "panel-" + id ? "Panel user" : "Unrecognized participant";
            }
            return new(eventId, room, eventType, identity, sid, role, DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { throw new InternalInterviewException(400, "Invalid media callback payload."); }
    }

    internal static string EventKey(InterviewMediaEvent value) => "media:" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(value.EventId)))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static bool SafeId(string value, int max) => value.Length > 0 && value.Length <= max && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
    private static byte[] Decode(string value)
    {
        if (value.Length == 0 || !value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')) throw new FormatException();
        var normalized = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(normalized.PadRight((normalized.Length + 3) / 4 * 4, '='));
    }
}
