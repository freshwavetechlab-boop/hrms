using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Payroll.API.Services;

// Only successful authenticated recruiter corrections enter this encrypted queue.
// The learning endpoint is independent of inference and never blocks a save.
public sealed class ResumeTrainingOutbox(IConfiguration configuration, IWebHostEnvironment environment,
    PortableIntegrationCredentialProtector protector, IHttpClientFactory clients, ILogger<ResumeTrainingOutbox> logger)
{
    private readonly object sync = new();
    private readonly string root = Path.Combine(environment.ContentRootPath, "App_Data", "Recruitment", "training-outbox");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public bool Enabled => configuration.GetValue("LocalLlmLearning:Enabled", false);
    internal static string Subject(long id) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("FrevoCandidate:v1:" + id))).ToLowerInvariant();

    internal static object? BuildExample(int clientId, long candidateId, string source,
        ResumeParsedFacts before, ResumeParsedFacts reviewed, int reviewer)
    {
        if (clientId < 0 || candidateId <= 0 || reviewer <= 0 || source.Length > 2_000_000) return null;
        // Keep professional sections only. Exclude identity/contact/salary and
        // protected-trait lines before both evidence selection and transmission.
        var lines = source.Replace("\r", "").Split('\n');
        var start = Array.FindIndex(lines, line => Regex.IsMatch(line,
            @"(?i)\b(experience|employment|career history|education|qualification|skills|current employer|current company|current designation|highest degree|primary toolkit)\b"));
        if (start < 0) return null;
        var personal = new[] { reviewed.FullName, reviewed.Email, reviewed.Phone }
            .Where(value => !string.IsNullOrWhiteSpace(value) && value.Length >= 3).ToArray();
        var evidence = string.Join("\n", lines.Skip(start).Where(line =>
            !personal.Any(value => line.Contains(value!, StringComparison.OrdinalIgnoreCase))
            && !Regex.IsMatch(line, @"(?i)@|https?://|\b(?:phone|mobile|contact|address|salary|ctc|date of birth|dob|age|sex|passport(?:\s*(?:number|no\.?))?|aadhaar|aadhar|religion|marital status|gender|nationality)\s*[:\-]|\+?\d[\d ()-]{8,}\d"))).Trim();
        if (Encoding.UTF8.GetByteCount(evidence) is < 60 or > 24000) return null;
        var fields = new Dictionary<string, object>();
        void Scalar(string jsonName, string field, string? oldValue, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value) && value.Length is >= 2 and <= 250
                && !string.Equals(oldValue, value, StringComparison.OrdinalIgnoreCase)
                && ResumeLearningMemory.Supported(evidence, field, value)) fields[jsonName] = value.Trim();
        }
        Scalar("currentCompany", "CurrentCompany", before.CurrentCompany, reviewed.CurrentCompany);
        Scalar("currentTitle", "CurrentTitle", before.CurrentTitle, reviewed.CurrentTitle);
        Scalar("highestQualification", "HighestQualification", before.HighestQualification, reviewed.HighestQualification);
        if (!reviewed.Skills.SequenceEqual(before.Skills, StringComparer.OrdinalIgnoreCase))
        {
            var skills = reviewed.Skills.Where(value => value.Length is >= 2 and <= 100
                && ResumeLearningMemory.Supported(evidence, "Skills", value)).Distinct(StringComparer.OrdinalIgnoreCase).Take(100).ToArray();
            if (skills.Length > 0) fields["skills"] = skills;
        }
        return fields.Count == 0 ? null : new { schemaVersion = 1, clientId, subjectRef = Subject(candidateId),
            sourceText = evidence, reviewedFields = fields, verified = true, verificationKind = "recruiter_review",
            verificationRef = $"FrevoReview:{reviewer}:{DateTimeOffset.UtcNow:O}" };
    }

    public void QueueReviewed(int clientId, long candidateId, string source, ResumeParsedFacts before, ResumeParsedFacts reviewed, int reviewer)
    {
        if (!Enabled) return;
        try
        {
            if (BuildExample(clientId, candidateId, source, before, reviewed, reviewer) is { } example)
                Save(new { action = "ingest", examples = new[] { example } }, Subject(candidateId), false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException)
        { logger.LogWarning("Verified resume training queue could not persist a correction; candidate save is unaffected."); }
    }

    public void QueuePurge(long candidateId)
    {
        if (!Enabled || candidateId <= 0) return;
        try
        {
            var subject = Subject(candidateId);
            lock (sync)
            {
                Directory.CreateDirectory(root);
                // Remove pending training examples before writing a priority purge.
                foreach (var path in Directory.EnumerateFiles(root, "1-" + subject + "-*.enc", SearchOption.AllDirectories)) File.Delete(path);
                Save(new { action = "purge_subject", clientId = -1, subjectRef = subject }, subject, true);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException)
        { logger.LogError("Resume training revocation could not be queued; check the protected learning outbox."); }
    }

    private void Save(object body, string subject, bool purge)
    {
        lock (sync)
        {
            Directory.CreateDirectory(root);
            if (Directory.EnumerateFiles(root, "*.enc").Take(10001).Count() >= 10000 && !purge) throw new IOException("Queue capacity");
            var path = Path.Combine(root, (purge ? "0-" : "1-") + subject + "-" + Guid.NewGuid().ToString("N") + ".enc");
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, protector.ProtectResumeTraining(JsonSerializer.Serialize(body, Json)));
            File.Move(temporary, path);
        }
    }

    public async Task<bool> FlushOneAsync(CancellationToken token)
    {
        if (!Enabled) return false;
        string? path; string? plaintext;
        lock (sync)
        {
            path = Directory.Exists(root) ? Directory.EnumerateFiles(root, "*.enc").Order(StringComparer.Ordinal).FirstOrDefault() : null;
            if (path is null) return false;
            plaintext = protector.UnprotectResumeTraining(File.ReadAllText(path));
        }
        if (plaintext is null) throw new CryptographicException("Training outbox cannot be decrypted");
        var keyPath = configuration["LocalLlmLearning:ApiKeyFile"];
        if (string.IsNullOrWhiteSpace(keyPath)) throw new InvalidOperationException("Learning credential file is not configured");
        var key = (await File.ReadAllTextAsync(keyPath, token)).Trim();
        if (!Regex.IsMatch(key, "^[a-f0-9]{64}$")) throw new InvalidOperationException("Invalid learning credential");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://eeslindia.org/llm-learning.php");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = new StringContent(plaintext, Encoding.UTF8, "application/json");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var client = clients.CreateClient("LocalLlmLearning");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (Path.GetFileName(path).StartsWith("1-", StringComparison.Ordinal) && ((int)response.StatusCode is 400 or 413 or 415))
        {
            lock (sync)
            {
                var rejected = Path.Combine(root, "rejected"); Directory.CreateDirectory(rejected);
                if (File.Exists(path)) File.Move(path, Path.Combine(rejected, Path.GetFileName(path)));
            }
            logger.LogWarning("A verified learning request was rejected by schema validation and retained encrypted for review.");
            return true;
        }
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        var buffer = new byte[4097]; var length = 0;
        while (length < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(length), deadline.Token);
            if (count == 0) break; length += count;
        }
        if (length > 4096) throw new InvalidOperationException("Invalid learning response");
        using var result = JsonDocument.Parse(buffer.AsMemory(0, length));
        if (result.RootElement.GetProperty("code").GetString() != "accepted") throw new InvalidOperationException("Learning not acknowledged");
        lock (sync) { File.Delete(path); }
        return true;
    }
}

public sealed class ResumeTrainingOutboxWorker(ResumeTrainingOutbox outbox, ILogger<ResumeTrainingOutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        var failures = 0;
        while (!token.IsCancellationRequested)
        {
            try
            {
                var sent = await outbox.FlushOneAsync(token); failures = 0;
                await Task.Delay(TimeSpan.FromSeconds(sent ? 2 : 30), token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception error) when (error is HttpRequestException or IOException or UnauthorizedAccessException
                or CryptographicException or InvalidOperationException or JsonException or OperationCanceledException or KeyNotFoundException)
            {
                failures = Math.Min(failures + 1, 6);
                logger.LogWarning("Verified learning delivery is pending; retry {Retry}. No resume content is logged.", failures);
                try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(900, 15 * Math.Pow(2, failures))), token); }
                catch (OperationCanceledException) { break; }
            }
        }
    }
}
