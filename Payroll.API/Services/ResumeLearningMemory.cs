using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Payroll.API.Services;

/// <summary>
/// Supervised, source-grounded parser memory. Parsing/model responses cannot
/// promote themselves: only a successful authenticated recruiter save calls Learn.
/// Exact-document corrections stay in their client; new documents reuse labels,
/// never another person's values. No weights or full resumes are stored here.
/// </summary>
public sealed class ResumeLearningMemory
{
    internal const int MaximumEntries = 512;
    private const int MaximumPlaintextBytes = 1024 * 1024;
    private const int MaximumFileBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(180);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] Fields = ["CurrentCompany", "CurrentTitle", "CurrentLocation", "HighestQualification", "Skills"];
    private readonly object sync = new();
    private readonly List<Entry> entries = [];
    private readonly string? path;
    private readonly Func<string, string>? protect;
    private readonly Func<string, string?>? unprotect;
    private readonly ILogger? logger;
    private readonly bool enabled;

    public ResumeLearningMemory(IConfiguration configuration, IWebHostEnvironment environment,
        PortableIntegrationCredentialProtector protector, ILogger<ResumeLearningMemory> logger)
        : this(Path.Combine(environment.ContentRootPath, "App_Data", "Recruitment", "resume-learning.enc"),
            protector.ProtectResumeLearning, protector.UnprotectResumeLearning,
            configuration.GetValue("Recruitment:ResumeLearningEnabled", true), logger) { }

    internal ResumeLearningMemory(string? path = null, Func<string, string>? protect = null,
        Func<string, string?>? unprotect = null, bool enabled = true, ILogger? logger = null)
    {
        this.enabled = enabled;
        this.logger = logger;
        // A filename alone must never enable plaintext persistence.
        if (path is null || protect is null || unprotect is null) return;
        this.path = Path.GetFullPath(path);
        this.protect = protect;
        this.unprotect = unprotect;
        Load();
    }

    internal sealed record Label(string Field, string Text);
    internal sealed record Entry(int ClientId, long CandidateId, string SourceHash,
        Dictionary<string, string[]> Values, List<Label> Labels, int ReviewedBy, DateTimeOffset ReviewedAt);
    private sealed record Saved(int Version, List<Entry> Entries);

    public ResumeLearningStats Stats(int clientId)
    {
        lock (sync)
        {
            Prune();
            var scoped = entries.Where(row => row.ClientId == clientId).ToArray();
            return new(enabled, scoped.Length, scoped.Sum(row => row.Values.Count),
                scoped.SelectMany(row => row.Labels).Distinct().Count(),
                scoped.Select(row => (DateTimeOffset?)row.ReviewedAt).Max(),
                "Verified recruiter corrections and source labels; model weights are unchanged.");
        }
    }

    public int Learn(int clientId, long candidateId, string source, ResumeParsedFacts before,
        ResumeParsedFacts reviewed, int reviewedBy)
    {
        if (!enabled || clientId < 0 || candidateId <= 0 || reviewedBy <= 0 || source.Length is < 100 or > 2_000_000
            || !ResumeParsingService.LooksLikeUsefulResumeText(source)) return 0;
        var values = new Dictionary<string, string[]>();
        foreach (var field in Fields)
        {
            var supplied = Values(reviewed, field).Select(value => value.Trim()).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (supplied.Length == 0 || supplied.Length > 100
                || supplied.SequenceEqual(Values(before, field), StringComparer.OrdinalIgnoreCase)
                || supplied.Any(value => !Supported(source, field, value))) continue;
            values[field] = supplied;
        }
        if (values.Count == 0) return 0;
        var labels = FindLabels(source, values).Distinct().Take(12).ToList();
        lock (sync)
        {
            Prune();
            var hash = Hash(source);
            var previous = entries.FirstOrDefault(row => row.ClientId == clientId && row.SourceHash == hash);
            if (previous is not null)
            {
                // A later explicit correction replaces the old value, not a new version.
                foreach (var pair in previous.Values) values.TryAdd(pair.Key, pair.Value);
                labels = labels.Concat(previous.Labels).Distinct().Take(12).ToList();
                entries.Remove(previous);
            }
            entries.Add(new(clientId, candidateId, hash, values, labels, reviewedBy, DateTimeOffset.UtcNow));
            Prune();
            Persist();
        }
        return values.Count;
    }

    public ResumeParseResult Apply(int clientId, ResumeParseResult parse)
    {
        if (!enabled || clientId < 0 || parse.Status != "Parsed") return parse;
        Entry? exact;
        Label[] labels;
        lock (sync)
        {
            Prune();
            exact = entries.FirstOrDefault(row => row.ClientId == clientId && row.SourceHash == Hash(parse.Text));
            labels = entries.Where(row => row.ClientId == clientId).OrderByDescending(row => row.ReviewedAt)
                .SelectMany(row => row.Labels).Distinct().Take(64).ToArray();
        }
        var facts = parse.Facts;
        var applied = 0;
        if (exact is not null)
            foreach (var pair in exact.Values)
                if (pair.Value.Length > 0 && pair.Value.All(value => Supported(parse.Text, pair.Key, value)))
                { facts = Set(facts, pair.Key, pair.Value); applied++; }
        // A reusable label is a parsing instruction, not a remembered candidate fact.
        foreach (var label in labels)
        {
            if (Values(facts, label.Field).Any(value => !string.IsNullOrWhiteSpace(value))) continue;
            var matches = Regex.Matches(parse.Text, @"(?im)^\s*" + Regex.Escape(label.Text) + @"\s*[:|]\s*(?<value>[^\r\n]+)$");
            if (matches.Count != 1) continue; // Repeated employment labels are ambiguous.
            var raw = matches[0].Groups["value"].Value.Trim();
            var found = label.Field == "Skills" ? Regex.Split(raw, @"\s*[,;|]\s*") : [raw];
            if (found.Length == 0 || found.Any(value => !Supported(parse.Text, label.Field, value))) continue;
            facts = Set(facts, label.Field, found);
            applied++;
        }
        return parse with { Facts = facts, LearnedFieldCount = Math.Max(parse.LearnedFieldCount, applied) };
    }

    // Only field names and verified label names enter a model prompt. Names,
    // companies, qualifications, contacts and raw CV text never leave this store.
    public string Guidance(int clientId, string source)
    {
        if (!enabled || clientId < 0) return "";
        lock (sync)
        {
            Prune();
            var labels = entries.Where(row => row.ClientId == clientId).OrderByDescending(row => row.ReviewedAt)
                .SelectMany(row => row.Labels).Distinct()
                .Where(label => Regex.IsMatch(source, @"(?im)^\s*" + Regex.Escape(label.Text) + @"\s*[:|]"))
                .Take(3).Select(label => label.Field + " uses source label " + JsonSerializer.Serialize(label.Text)).ToArray();
            return labels.Length == 0 ? "" : "Verified layout hints (copy values only from this resume): " + string.Join("; ", labels) + ".";
        }
    }

    public void ForgetCandidate(int clientId, long candidateId)
    {
        lock (sync)
            // Candidate IDs are globally unique. An authorised profile deletion or
            // consent revocation also removes examples reviewed under its applications.
            if (clientId >= 0 && entries.RemoveAll(row => row.CandidateId == candidateId) > 0) Persist();
    }

    public void Clear(int clientId)
    {
        lock (sync)
            if (entries.RemoveAll(row => row.ClientId == clientId) > 0) Persist();
    }

    private static IEnumerable<string> Values(ResumeParsedFacts facts, string field) => field switch
    {
        "CurrentCompany" => [facts.CurrentCompany], "CurrentTitle" => [facts.CurrentTitle],
        "CurrentLocation" => [facts.CurrentLocation], "HighestQualification" => [facts.HighestQualification],
        "Skills" => facts.Skills, _ => []
    };

    private static ResumeParsedFacts Set(ResumeParsedFacts facts, string field, string[] values) => field switch
    {
        "CurrentCompany" => facts with { CurrentCompany = values[0] },
        "CurrentTitle" => facts with { CurrentTitle = values[0] },
        "CurrentLocation" => facts with { CurrentLocation = values[0] },
        "HighestQualification" => facts with { HighestQualification = values[0] },
        "Skills" => facts with { Skills = values }, _ => facts
    };

    internal static bool Supported(string source, string field, string value)
    {
        if (!Fields.Contains(field) || value.Length is < 2 or > 250 || !value.Any(char.IsLetter)
            || Regex.IsMatch(value, @"[\r\n<>@]|https?://|\b(?:ignore\s+(?:all\s+)?(?:system\s+)?instructions?|system\s+(?:prompt|instructions?)|unknown|not supplied|not mentioned|N/A)\b", RegexOptions.IgnoreCase)
            || !Regex.IsMatch(Normalize(source), @"(?<![\p{L}\p{N}])" + Regex.Escape(Normalize(value)) + @"(?![\p{L}\p{N}])")) return false;
        if (field == "HighestQualification") return ResumeParsingService.IsVerifiedDegree(value);
        if (field == "CurrentTitle") return Regex.IsMatch(value, @"\b(?:chief|head|lead|director|manager|engineer|architect|officer|president|consultant|analyst|developer|specialist|founder|cto|cio|cpo|ceo|vp)\b", RegexOptions.IgnoreCase);
        if (field == "Skills") return value.Length <= 100 && value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 8;
        return !Regex.IsMatch(value, @"^(?:led|built|managed|developed|responsible|seeking|skills|education|summary)\b", RegexOptions.IgnoreCase);
    }

    private static IEnumerable<Label> FindLabels(string source, Dictionary<string, string[]> values)
    {
        foreach (var line in source.Split('\n'))
        {
            var match = Regex.Match(line, @"^\s*(?<label>[A-Za-z][A-Za-z /&-]{1,44})\s*[:|]\s*(?<value>.+)$");
            if (!match.Success) continue;
            var label = match.Groups["label"].Value.Trim();
            foreach (var pair in values)
            {
                var semantic = pair.Key switch
                {
                    "CurrentCompany" => @"^(?:current\s+(?:company|employer|organi[sz]ation)|employer)$",
                    "CurrentTitle" => @"^(?:(?:current|present)\s+)?(?:designation|job title|role)$",
                    "CurrentLocation" => @"^(?:current|present)\s+(?:location|city)$",
                    "HighestQualification" => @"^(?:(?:highest|academic|educational)\s+)?(?:qualification|degree)$",
                    "Skills" => @"^(?:(?:technical|core|key|professional|primary|additional|technology|domain|functional)\s+)?(?:skills?|skill sets?|competencies|toolkit|technologies|expertise|strengths)(?:\s+(?:and|&|/)\s+(?:tools|technologies|frameworks))?$", _ => "(?!)"
                };
                if (Regex.IsMatch(label, semantic, RegexOptions.IgnoreCase)
                    && pair.Value.All(value => Normalize(match.Groups["value"].Value).Contains(Normalize(value), StringComparison.Ordinal)))
                    yield return new(pair.Key, label);
            }
        }
    }

    private static string Normalize(string value) => Regex.Replace(value, @"\s+", " ").Trim().ToLowerInvariant();
    private static string Hash(string source) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(source))));

    private void Prune()
    {
        var count = entries.Count;
        entries.RemoveAll(row => row.ReviewedAt > DateTimeOffset.UtcNow || DateTimeOffset.UtcNow - row.ReviewedAt >= Lifetime);
        while (entries.Count > MaximumEntries || Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(entries, Json)) > MaximumPlaintextBytes)
            entries.Remove(entries.MinBy(row => row.ReviewedAt)!);
        if (entries.Count != count) Persist();
    }

    private void Load()
    {
        if (path is null || unprotect is null) return;
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > MaximumFileBytes) return;
            var encrypted = File.ReadAllText(path);
            var plaintext = unprotect(encrypted);
            if (plaintext is null || plaintext == encrypted || Encoding.UTF8.GetByteCount(plaintext) > MaximumPlaintextBytes) return;
            var saved = JsonSerializer.Deserialize<Saved>(plaintext, Json);
            if (saved?.Version != 1 || saved.Entries is null || saved.Entries.Count > MaximumEntries) return;
            entries.AddRange(saved.Entries.Where(row => row.ClientId >= 0 && row.CandidateId > 0 && row.ReviewedBy > 0
                && row.SourceHash.Length == 64 && row.Values.Count is > 0 and <= 5 && row.Labels.Count <= 12
                && row.Values.All(pair => Fields.Contains(pair.Key) && pair.Value.Length is > 0 and <= 100
                    && pair.Value.All(value => value.Length is > 0 and <= 250))));
            Prune();
        }
        catch { entries.Clear(); logger?.LogWarning("Resume learning memory could not be read; normal parsing remains available."); }
    }

    private void Persist()
    {
        if (path is null || protect is null) return;
        string? temporary = null;
        try
        {
            var plaintext = JsonSerializer.Serialize(new Saved(1, entries), Json);
            var encrypted = protect(plaintext);
            if (encrypted == plaintext || string.IsNullOrWhiteSpace(encrypted) || Encoding.UTF8.GetByteCount(encrypted) > MaximumFileBytes) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporary, encrypted, new UTF8Encoding(false));
            File.Move(temporary, path, true);
        }
        catch { logger?.LogWarning("Resume learning memory could not be saved; the recruiter save remains successful."); }
        finally { if (temporary is not null) try { File.Delete(temporary); } catch { } }
    }
}

public sealed record ResumeLearningStats(bool Enabled, int VerifiedDocuments, int VerifiedFields,
    int ReusableLabels, DateTimeOffset? LastReviewedAt, string Mode);
