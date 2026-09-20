using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class InternalInterviewMediaWebhookTests
{
    internal const string Key = "synthetic-media-key";
    internal const string Secret = "synthetic-not-for-live-use-media-secret-1234567890";
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 1, 0, 0, TimeSpan.Zero);
    private static InternalInterviewOptions Options => new() { LiveKitWebhooksEnabled = true, LiveKitApiKey = Key, LiveKitApiSecret = Secret };
    internal static byte[] Body(string room, string eventId, string identity, string eventType, DateTimeOffset now, string sid = "PA_synthetic") =>
        JsonSerializer.SerializeToUtf8Bytes(new { id = eventId, @event = eventType, room = new { name = room },
            participant = new { identity, sid, metadata = "must-not-be-stored", attributes = new { privateValue = "must-not-be-stored" } }, createdAt = now.ToUnixTimeSeconds().ToString() });
    internal static string Sign(byte[] body, DateTimeOffset now, string? secret = null, string? issuer = null, string algorithm = "HS256", bool hash = true, int expiryOffset = 60)
    {
        static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var header = Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = algorithm }));
        var payload = new Dictionary<string, object> { ["iss"] = issuer ?? Key, ["nbf"] = now.ToUnixTimeSeconds() - 5, ["exp"] = now.ToUnixTimeSeconds() + expiryOffset };
        if (hash) payload["sha256"] = Convert.ToBase64String(SHA256.HashData(body));
        var input = header + "." + Encode(JsonSerializer.SerializeToUtf8Bytes(payload));
        return input + "." + Encode(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret ?? Secret), Encoding.ASCII.GetBytes(input)));
    }
    [Theory]
    [InlineData("participant_joined", "candidate", "Candidate")]
    [InlineData("participant_left", "panel-4", "Panel user")]
    [InlineData("participant_connection_aborted", "unknown", "Unrecognized participant")]
    public void Signed_events_keep_only_room_bound_connection_evidence(string type, string identity, string role)
    {
        var room = Guid.NewGuid().ToString("N"); var body = Body(room, "EV_synthetic", identity, type, Now);
        var result = InternalInterviewMediaWebhook.Verify(body, Sign(body, Now), Options, Now)!;
        Assert.Equal(room, result.RoomId); Assert.Equal(identity, result.Identity); Assert.Equal(type, result.EventType); Assert.Equal(role, result.Role);
        Assert.Equal(Now.UtcDateTime, result.OccurredAtUtc);
        Assert.DoesNotContain("must-not-be-stored", JsonSerializer.Serialize(result));
        Assert.InRange(InternalInterviewMediaWebhook.EventKey(result).Length, 1, 64);
    }
    [Theory]
    [InlineData("secret")]
    [InlineData("issuer")]
    [InlineData("algorithm")]
    [InlineData("expired")]
    [InlineData("body")]
    [InlineData("candidate-token")]
    [InlineData("missing")]
    public void Forged_expired_substituted_or_non_callback_tokens_are_rejected(string mutation)
    {
        var body = Body(Guid.NewGuid().ToString("N"), "EV_test", "candidate", "participant_joined", Now);
        var token = Sign(body, Now, secret: mutation == "secret" ? "wrong-secret" : null, issuer: mutation == "issuer" ? "other-key" : null,
            algorithm: mutation == "algorithm" ? "none" : "HS256", hash: mutation != "candidate-token", expiryOffset: mutation == "expired" ? -60 : 60);
        if (mutation == "body") body = [..body, 32];
        if (mutation == "missing") token = "";
        Assert.Equal(401, Assert.Throws<InternalInterviewException>(() => InternalInterviewMediaWebhook.Verify(body, token, Options, Now)).StatusCode);
    }
    [Fact]
    public void Unrelated_rooms_and_track_events_are_acknowledged_without_persisting_payload()
    {
        foreach (var (room, kind) in new[] { ("another-app-room", "participant_joined"), ("ABCDEF0123456789ABCDEF0123456789AB", "participant_joined"), (Guid.NewGuid().ToString("N"), "track_published") })
        {
            var body = Body(room, "EV_test", "candidate", kind, Now);
            Assert.Null(InternalInterviewMediaWebhook.Verify(body, Sign(body, Now), Options, Now));
        }
    }
    [Fact]
    public void Oversize_and_invalid_signed_payloads_fail_without_loosening_verification()
    {
        var bytes = new byte[InternalInterviewMediaWebhook.MaxBodyBytes + 1];
        Assert.Equal(413, Assert.Throws<InternalInterviewException>(() => InternalInterviewMediaWebhook.Verify(bytes, "x", Options, Now)).StatusCode);
        foreach (var body in new[] { Encoding.UTF8.GetBytes("[]"), Body(Guid.NewGuid().ToString("N"), "EV_test", "candidate", "participant_joined", Now.AddMinutes(2)),
            Body(Guid.NewGuid().ToString("N"), "EV_test", "candidate", "participant_joined", Now, "") })
            Assert.Equal(400, Assert.Throws<InternalInterviewException>(() => InternalInterviewMediaWebhook.Verify(body, Sign(body, Now), Options, Now)).StatusCode);
    }
}
