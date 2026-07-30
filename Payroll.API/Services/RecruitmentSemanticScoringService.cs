using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SmartComponents.LocalEmbeddings;

namespace Payroll.API.Services;

public sealed class RecruitmentSemanticScoringService(LocalEmbedder embedder, ILogger<RecruitmentSemanticScoringService> logger)
{
    private const int MaximumCacheEntries = 12_000;
    private const int MaximumChunks = 140;
    private const int MaximumChunkCharacters = 420;
    private readonly ConcurrentDictionary<string, EmbeddingF32> embeddingCache = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> cacheOrder = new();

    public SemanticDocument CreateDocument(string resumeText, params string[] profileFacts)
    {
        var chunks = new List<string>();
        chunks.AddRange(profileFacts.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => NormalizeChunk(value, MaximumChunkCharacters)));

        var text = resumeText ?? "";
        foreach (var paragraph in Regex.Split(text, @"(?:\r?\n){1,}|(?<=[.!?;])\s+"))
        {
            var normalized = NormalizeChunk(paragraph, MaximumChunkCharacters);
            if (normalized.Length < 3) continue;
            chunks.Add(normalized);
            if (chunks.Count >= MaximumChunks) break;
        }

        if (chunks.Count == 0 && !string.IsNullOrWhiteSpace(text))
            chunks.Add(NormalizeChunk(text, MaximumChunkCharacters));

        return new SemanticDocument(chunks.Distinct(StringComparer.OrdinalIgnoreCase).Take(MaximumChunks).ToArray());
    }

    public SemanticComparison FindBest(string expected, SemanticDocument document)
    {
        if (string.IsNullOrWhiteSpace(expected) || document.Chunks.Count == 0)
            return SemanticComparison.Empty;

        try
        {
            var expectedEmbedding = Embed(expected);
            var bestScore = decimal.MinValue;
            var bestChunk = "";
            foreach (var chunk in document.Chunks)
            {
                var similarity = ClampSimilarity(expectedEmbedding.Similarity(Embed(chunk)));
                if (similarity <= bestScore) continue;
                bestScore = similarity;
                bestChunk = chunk;
            }

            return bestScore == decimal.MinValue
                ? SemanticComparison.Empty
                : new SemanticComparison(bestScore, bestChunk, Calibrate(bestScore));
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Local ATS semantic comparison failed; deterministic scoring will continue.");
            return SemanticComparison.Empty;
        }
    }

    public SemanticComparison Compare(string expected, string actual)
    {
        if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(actual))
            return SemanticComparison.Empty;
        return FindBest(expected, new SemanticDocument([NormalizeChunk(actual, MaximumChunkCharacters)]));
    }

    private EmbeddingF32 Embed(string value)
    {
        var normalized = NormalizeChunk(value, MaximumChunkCharacters);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        if (embeddingCache.TryGetValue(key, out var cached)) return cached;

        var generated = embedder.Embed(normalized);
        if (embeddingCache.TryAdd(key, generated))
        {
            cacheOrder.Enqueue(key);
            TrimCache();
        }
        return generated;
    }

    private void TrimCache()
    {
        while (embeddingCache.Count > MaximumCacheEntries && cacheOrder.TryDequeue(out var key))
            embeddingCache.TryRemove(key, out _);
    }

    private static string NormalizeChunk(string value, int maximumCharacters)
    {
        var normalized = Regex.Replace((value ?? "").Trim(), @"\s+", " ");
        return normalized.Length <= maximumCharacters ? normalized : normalized[..maximumCharacters];
    }

    private static decimal ClampSimilarity(float similarity) =>
        Math.Clamp(Convert.ToDecimal(similarity), 0m, 1m);

    // BGE cosine values for unrelated English text are commonly above zero. This maps the
    // useful 0.35-0.90 range into a score ratio while preserving the raw value as evidence.
    private static decimal Calibrate(decimal similarity) =>
        Math.Clamp((similarity - .35m) / .55m, 0m, 1m);
}

public sealed record SemanticDocument(IReadOnlyList<string> Chunks);

public sealed record SemanticComparison(decimal Similarity, string Evidence, decimal CalibratedRatio)
{
    public static SemanticComparison Empty { get; } = new(0m, "", 0m);
}
