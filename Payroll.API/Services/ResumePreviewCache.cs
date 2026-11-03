using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace Payroll.API.Services;

/// <summary>Bounded, process-local receipts. Nothing supplied by the browser is a parsing receipt.</summary>
public sealed class ResumePreviewCache : IDisposable
{
    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 32 * 1024 * 1024 });
    private readonly TimeProvider clock;
    private readonly TimeSpan lifetime;
    private sealed record Entry(string Token, byte[] Json, DateTimeOffset Expires, string LearningRevision);
    internal sealed record Receipt(ResumeParseResult Local, ResumeParseResult Reviewed, string Token);

    public ResumePreviewCache() : this(TimeProvider.System, TimeSpan.FromHours(4)) { }
    internal ResumePreviewCache(TimeProvider clock, TimeSpan lifetime) { this.clock = clock; this.lifetime = lifetime; }

    internal static string Fingerprint(object? value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));

    internal static async Task<string> KeyAsync(IFormFile file, object scope, CancellationToken ct)
    {
        if (file.Length is <= 0 or > 10 * 1024 * 1024) return "";
        await using var stream = file.OpenReadStream();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long count = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            count += read;
            if (count > 10 * 1024 * 1024) return "";
            hash.AppendData(buffer, 0, read);
        }
        if (count != file.Length) return "";
        return Fingerprint(new { Scope = scope, file.FileName, ContentType = file.Headers?.ContentType.ToString() ?? "", Length = count,
            Content = Convert.ToHexString(hash.GetHashAndReset()) });
    }

    internal Receipt? Get(string key, string? requiredToken = null, string? expectedLearningRevision = null)
    {
        if (key.Length == 0 || !cache.TryGetValue<Entry>(key, out var entry) || entry is null) return null;
        if (entry.Expires <= clock.GetUtcNow()) { cache.Remove(key); return null; }
        if (expectedLearningRevision is not null && entry.LearningRevision != expectedLearningRevision) return null;
        if (requiredToken is not null && (requiredToken.Length != 64
            || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(entry.Token), Encoding.ASCII.GetBytes(requiredToken)))) return null;
        // Deserialize a private copy so a caller's edit cannot mutate another request's source facts.
        return JsonSerializer.Deserialize<Receipt>(entry.Json);
    }

    internal Receipt? Store(string key, ResumeParseResult local, ResumeParseResult reviewed, bool aiEnabled, string learningRevision = "")
    {
        if (key.Length == 0 || local.Status != "Parsed" || (aiEnabled && reviewed.AiReviewStatus != "Completed")) return null;
        var receipt = new Receipt(local, reviewed, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
        var json = JsonSerializer.SerializeToUtf8Bytes(receipt);
        if (json.Length > 1024 * 1024) return null;
        cache.Set(key, new Entry(receipt.Token, json, clock.GetUtcNow() + lifetime, learningRevision),
            new MemoryCacheEntryOptions { Size = json.Length + key.Length * 2 + 256, AbsoluteExpirationRelativeToNow = lifetime });
        return Get(key);
    }

    public void Dispose() => cache.Dispose();
}
