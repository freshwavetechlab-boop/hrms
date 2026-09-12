using System.Text.RegularExpressions;
using Dapper;
using MySqlConnector;

namespace Payroll.API.Services;

/// <summary>
/// Retrieves a small, tenant-scoped recruitment context with the existing local
/// BGE embedding engine. The source document remains authoritative: retrieved
/// records are examples for terminology and structure, never facts to copy.
/// </summary>
public sealed class RecruitmentDocumentRagService(
    IConfiguration configuration,
    RecruitmentSemanticScoringService semanticScoring,
    ILogger<RecruitmentDocumentRagService> logger)
{
    public const string EngineName = "bge-micro-v2";
    public const string HiringDocumentKind = "HiringDocument";
    public const string ResumeDocumentKind = "Resume";
    private const int MaximumCandidates = 100;
    private const int MaximumReferences = 4;
    private const decimal MinimumReferenceSimilarity = .62m;
    private static readonly string[] VocabularyTypes =
    [
        "Department", "Position Category", "Experience Range", "Hiring Type",
        "Employment Type", "Work Mode", "Work Location", "Qualification", "Certification", "Skill"
    ];

    private MySqlConnection Db() => new(configuration.GetConnectionString("Default"));

    public async Task<RecruitmentDocumentRagContext> RetrieveAsync(
        int clientId,
        string documentKind,
        IEnumerable<string> queryParts,
        CancellationToken cancellationToken = default)
    {
        var query = Fingerprint(queryParts);
        if (clientId <= 0 || string.IsNullOrWhiteSpace(query))
            return RecruitmentDocumentRagContext.Empty(clientId, documentKind);

        try
        {
            await using var db = Db();
            await db.OpenAsync(cancellationToken);
            var candidates = (await db.QueryAsync<RecruitmentRagCandidate>(new CommandDefinition(ApprovedJdSql,
                new { ClientId = clientId, Limit = MaximumCandidates }, cancellationToken: cancellationToken))).ToList();
            if (candidates.Count < MaximumCandidates)
            {
                var remaining = MaximumCandidates - candidates.Count;
                candidates.AddRange(await db.QueryAsync<RecruitmentRagCandidate>(new CommandDefinition(ApprovedRequisitionSql,
                    new { ClientId = clientId, Limit = remaining }, cancellationToken: cancellationToken)));
            }

            var sourceDocument = semanticScoring.CreateDocument(query);
            var references = candidates
                .Select(candidate => Rank(candidate, query, sourceDocument))
                .Where(reference => reference.Similarity >= MinimumReferenceSimilarity)
                .OrderByDescending(reference => reference.Similarity)
                .ThenByDescending(reference => reference.UpdatedAtUtc)
                .Take(MaximumReferences)
                .ToList();

            var vocabularyRows = await db.QueryAsync<RecruitmentRagVocabularyRow>(new CommandDefinition(@"
SELECT Type,Value,ClientId
FROM dropdownmasters
WHERE IsActive=TRUE AND (ClientId=0 OR ClientId=@ClientId OR ClientId IS NULL) AND Type IN @Types
ORDER BY (ClientId=@ClientId) DESC,Type,Value", new { ClientId = clientId, Types = VocabularyTypes }, cancellationToken: cancellationToken));
            var vocabulary = vocabularyRows
                .Where(row => !string.IsNullOrWhiteSpace(row.Value))
                .GroupBy(row => row.Type, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => (IReadOnlyList<string>)group
                    .GroupBy(row => row.Value.Trim(), StringComparer.OrdinalIgnoreCase)
                    .Select(rows => rows.First().Value.Trim())
                    .Select(value => new { Value = value, Score = semanticScoring.Compare(query, VocabularyMeaning(group.Key, value)).Similarity })
                    .OrderByDescending(row => row.Score)
                    .ThenBy(row => row.Value)
                    .Take(group.Key.Equals("Department", StringComparison.OrdinalIgnoreCase) ? 12 : 24)
                    .Select(row => row.Value)
                    .ToList(), StringComparer.OrdinalIgnoreCase);

            var clientContext = (await db.QueryAsync<RecruitmentClientContextRow>(new CommandDefinition(@"
SELECT JobLocation,WorkMode
FROM recruitment_requisitions
WHERE ClientId=@ClientId AND Status NOT IN ('Rejected','Withdrawn')
  AND (TRIM(COALESCE(JobLocation,''))<>'' OR TRIM(COALESCE(WorkMode,''))<>'')
ORDER BY (Status='Approved') DESC,UpdatedAt DESC
LIMIT 100", new { ClientId = clientId }, cancellationToken: cancellationToken))).ToList();
            var defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            AddConsensusDefault(defaults, "JobLocation", clientContext.Select(row => row.JobLocation));
            AddConsensusDefault(defaults, "WorkMode", clientContext.Select(row => row.WorkMode));

            return new RecruitmentDocumentRagContext(EngineName, clientId, NormalizeKind(documentKind), references, vocabulary, defaults);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Recruitment document RAG retrieval failed for client {ClientId}; extraction will continue without retrieved context.", clientId);
            return RecruitmentDocumentRagContext.Empty(clientId, documentKind);
        }
    }

    public RecruitmentRagVocabularyMatch? MatchVocabulary(
        RecruitmentDocumentRagContext context,
        string vocabularyType,
        string currentValue,
        IEnumerable<string> evidenceParts)
    {
        if (!context.Vocabulary.TryGetValue(vocabularyType, out var options) || options.Count == 0)
            return null;
        var current = Clean(currentValue, 240);
        var evidenceValues = evidenceParts.Select(value => Clean(value, 240)).Where(value => value.Length > 0).ToList();
        var evidence = Fingerprint(evidenceValues.Prepend(current));
        if (string.IsNullOrWhiteSpace(evidence)) return null;

        var exact = options.FirstOrDefault(option => VocabularyEquivalent(current, option));
        if (!string.IsNullOrWhiteSpace(exact))
            return new RecruitmentRagVocabularyMatch(vocabularyType, current, exact, 1m, "ExactOrAlias");
        var supportingExact = options.FirstOrDefault(option => evidenceValues.Any(value => VocabularyEquivalent(value, option)));
        if (!string.IsNullOrWhiteSpace(supportingExact))
            return new RecruitmentRagVocabularyMatch(vocabularyType, current, supportingExact, .9m, "SupportingFieldExact");
        // Numeric dropdowns (experience, grades, bands, amounts, etc.) are facts.
        // Semantic similarity must never turn an explicit 10 into 15 or 5 into 7.
        if (Regex.IsMatch(current, @"\d")) return null;

        var ranked = options.Select(option =>
            {
                var comparison = semanticScoring.Compare(VocabularyMeaning(vocabularyType, option), evidence);
                return new { Option = option, comparison.Similarity, comparison.CalibratedRatio };
            })
            .OrderByDescending(row => row.CalibratedRatio)
            .ThenByDescending(row => row.Similarity)
            .ToList();
        if (ranked.Count == 0 || ranked[0].CalibratedRatio < .48m) return null;
        if (ranked.Count > 1 && ranked[0].CalibratedRatio - ranked[1].CalibratedRatio < .08m) return null;
        return new RecruitmentRagVocabularyMatch(vocabularyType, current, ranked[0].Option,
            ranked[0].CalibratedRatio, "Semantic");
    }

    private RecruitmentDocumentRagReference Rank(RecruitmentRagCandidate candidate, string query, SemanticDocument sourceDocument)
    {
        var candidateFingerprint = Fingerprint([
            candidate.Title, candidate.Department, candidate.PositionCategory, candidate.Skills,
            candidate.Qualifications, candidate.Certifications, candidate.Languages,
            candidate.ExperienceRange, candidate.Summary, candidate.RolePurpose
        ]);
        var wholeDocument = semanticScoring.Compare(query, candidateFingerprint).Similarity;
        var titleEvidence = semanticScoring.FindBest(candidate.Title, sourceDocument);
        var similarity = Math.Clamp((wholeDocument * .7m) + (titleEvidence.Similarity * .3m), 0m, 1m);
        return new RecruitmentDocumentRagReference(
            candidate.ReferenceId,
            candidate.SourceType,
            Clean(candidate.Title, 180),
            Clean(candidate.Department, 120),
            candidate.Status,
            similarity,
            Clean(titleEvidence.Evidence, 300),
            candidate.UpdatedAtUtc,
            PromptContext(candidate));
    }

    private static string PromptContext(RecruitmentRagCandidate row) => string.Join('\n', new[]
    {
        $"Title: {Clean(row.Title, 180)}",
        $"Department: {Clean(row.Department, 120)}",
        $"Category: {Clean(row.PositionCategory, 120)}",
        $"Experience: {Clean(row.ExperienceRange, 120)}",
        $"Skills: {Clean(row.Skills, 700)}",
        $"Qualifications: {Clean(row.Qualifications, 500)}",
        $"Certifications: {Clean(row.Certifications, 350)}",
        $"Languages: {Clean(row.Languages, 250)}",
        $"Summary: {Clean(row.Summary, 600)}",
        $"Purpose: {Clean(row.RolePurpose, 500)}"
    }.Where(value => !value.EndsWith(": ", StringComparison.Ordinal)));

    private static string Fingerprint(IEnumerable<string> values)
    {
        var joined = string.Join(" | ", values.Select(value => Clean(value, 1_200)).Where(value => value.Length > 0));
        return joined.Length <= 6_000 ? joined : joined[..6_000];
    }

    private static string Clean(string value, int maximumLength)
    {
        var clean = Regex.Replace(value ?? "", @"\s+", " ").Trim();
        return clean.Length <= maximumLength ? clean : clean[..maximumLength];
    }

    private static string NormalizeKind(string value) => value.Equals(ResumeDocumentKind, StringComparison.OrdinalIgnoreCase)
        ? ResumeDocumentKind
            : HiringDocumentKind;

    private static void AddConsensusDefault(IDictionary<string, string> target, string key, IEnumerable<string> values)
    {
        var ranked = values.Select(value => Clean(value, 240)).Where(value => value.Length > 0)
            .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Select(group => new { Value = group.First(), Count = group.Count() })
            .OrderByDescending(row => row.Count).ThenBy(row => row.Value).ToList();
        if (ranked.Count == 0) return;
        if (ranked.Count > 1 && ranked[0].Count == ranked[1].Count) return;
        target[key] = ranked[0].Value;
    }

    private static bool VocabularyEquivalent(string left, string right)
    {
        static string Key(string value) => Regex.Replace((value ?? "").ToLowerInvariant(), @"[^a-z0-9]+", "");
        var leftKey = Key(left);
        var rightKey = Key(right);
        if (leftKey.Length == 0 || rightKey.Length == 0) return false;
        if (leftKey == rightKey) return true;
        if (Regex.IsMatch(left, @"\d") || Regex.IsMatch(right, @"\d")) return false;
        return leftKey.Length >= 5 && rightKey.Length >= 5
            && (leftKey.StartsWith(rightKey, StringComparison.Ordinal) || rightKey.StartsWith(leftKey, StringComparison.Ordinal));
    }

    private static string VocabularyMeaning(string vocabularyType, string value)
    {
        var key = Regex.Replace(value.Trim().ToUpperInvariant(), @"[^A-Z0-9]+", " ").Trim();
        var aliases = key switch
        {
            "IT" => "information technology software applications systems data cloud digital engineering",
            "CSE" => "computer science software systems engineering information technology",
            "HR" => "human resources people recruitment talent employee",
            "ICT" or "ICTB" => "information communication technology software systems digital",
            "AI" or "AIC" => "artificial intelligence machine learning data analytics",
            _ when key.Contains("INFORMATION SECURITY", StringComparison.Ordinal) => "cyber security information security fraud risk identity access",
            _ when key.Contains("TECHNOLOGY CENTRE", StringComparison.Ordinal) => "technology centre software systems platform data engineering digital",
            _ => ""
        };
        return string.IsNullOrWhiteSpace(aliases) ? $"{vocabularyType}: {value}" : $"{vocabularyType}: {value}. {aliases}";
    }

    private const string ApprovedJdSql = @"
SELECT jd.Id ReferenceId,'ApprovedJobDescription' SourceType,jd.Title,
COALESCE(r.Department,'') Department,COALESCE(r.PositionCategory,'') PositionCategory,
COALESCE(r.ExperienceRange,'') ExperienceRange,COALESCE(jd.Summary,'') Summary,COALESCE(jd.RolePurpose,'') RolePurpose,
COALESCE((SELECT GROUP_CONCAT(CONCAT(IF(s.IsRequired,'Required: ','Preferred: '),s.SkillName) ORDER BY s.IsRequired DESC,s.DisplayOrder SEPARATOR '; ') FROM recruitment_jd_skill_requirements s WHERE s.JobDescriptionVersionId=jd.Id),'') Skills,
COALESCE((SELECT GROUP_CONCAT(CONCAT(q.QualificationName,IF(q.Specialization='', '', CONCAT(' - ',q.Specialization))) ORDER BY q.DisplayOrder SEPARATOR '; ') FROM recruitment_jd_qualification_requirements q WHERE q.JobDescriptionVersionId=jd.Id),'') Qualifications,
COALESCE((SELECT GROUP_CONCAT(c.CertificationName ORDER BY c.DisplayOrder SEPARATOR '; ') FROM recruitment_jd_certification_requirements c WHERE c.JobDescriptionVersionId=jd.Id),'') Certifications,
COALESCE((SELECT GROUP_CONCAT(l.LanguageName ORDER BY l.DisplayOrder SEPARATOR '; ') FROM recruitment_jd_language_requirements l WHERE l.JobDescriptionVersionId=jd.Id),'') Languages,
jd.Status,jd.UpdatedAtUtc
FROM recruitment_job_description_versions jd
JOIN recruitment_requisitions r ON r.Id=jd.RequisitionId AND r.ClientId=jd.ClientId
WHERE jd.ClientId=@ClientId AND jd.Status='Approved'
ORDER BY jd.UpdatedAtUtc DESC
LIMIT @Limit";

    private const string ApprovedRequisitionSql = @"
SELECT r.Id ReferenceId,'ApprovedHiringRequest' SourceType,r.PositionTitle Title,
COALESCE(r.Department,'') Department,COALESCE(r.PositionCategory,'') PositionCategory,
COALESCE(r.ExperienceRange,'') ExperienceRange,COALESCE(r.BusinessJustification,'') Summary,
COALESCE(r.ReasonForHiring,'') RolePurpose,
CONCAT_WS('; ',NULLIF(r.RequiredSkills,''),NULLIF(r.PreferredSkills,'')) Skills,
COALESCE(r.Qualification,'') Qualifications,COALESCE(r.Certifications,'') Certifications,
COALESCE(r.Languages,'') Languages,r.Status,r.UpdatedAt UpdatedAtUtc
FROM recruitment_requisitions r
WHERE r.ClientId=@ClientId AND r.Status='Approved'
AND NOT EXISTS (
    SELECT 1 FROM recruitment_job_description_versions jd
    WHERE jd.RequisitionId=r.Id AND jd.ClientId=r.ClientId AND jd.Status='Approved'
)
ORDER BY r.UpdatedAt DESC
LIMIT @Limit";

    private sealed class RecruitmentRagCandidate
    {
        public long ReferenceId { get; set; }
        public string SourceType { get; set; } = "";
        public string Title { get; set; } = "";
        public string Department { get; set; } = "";
        public string PositionCategory { get; set; } = "";
        public string ExperienceRange { get; set; } = "";
        public string Summary { get; set; } = "";
        public string RolePurpose { get; set; } = "";
        public string Skills { get; set; } = "";
        public string Qualifications { get; set; } = "";
        public string Certifications { get; set; } = "";
        public string Languages { get; set; } = "";
        public string Status { get; set; } = "";
        public DateTime UpdatedAtUtc { get; set; }
    }

    private sealed class RecruitmentRagVocabularyRow
    {
        public string Type { get; set; } = "";
        public string Value { get; set; } = "";
        public int ClientId { get; set; }
    }

    private sealed class RecruitmentClientContextRow
    {
        public string JobLocation { get; set; } = "";
        public string WorkMode { get; set; } = "";
    }
}

public sealed record RecruitmentDocumentRagContext(
    string Engine,
    int ScopeClientId,
    string DocumentKind,
    IReadOnlyList<RecruitmentDocumentRagReference> References,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Vocabulary,
    IReadOnlyDictionary<string, string> Defaults)
{
    public bool HasContext => References.Count > 0 || Vocabulary.Any(row => row.Value.Count > 0);

    public string ToPromptContext()
    {
        if (!HasContext) return "";
        var lines = new List<string>
        {
            "RETRIEVED CLIENT CONTEXT (REFERENCE ONLY; NEVER OVERRIDE THE SOURCE DOCUMENT)",
            "Use this context only to normalize terminology, categorize skills and resolve safe labels. Never copy openings, dates, codes, salary, budget, authority or other administrative facts from a reference."
        };
        foreach (var reference in References)
        {
            lines.Add($"<reference source=\"{reference.SourceType}\" similarity=\"{reference.Similarity:0.000}\">");
            lines.Add(reference.PromptContext);
            lines.Add("</reference>");
        }
        if (Vocabulary.Count > 0)
        {
            lines.Add("<configured-vocabulary>");
            lines.AddRange(Vocabulary.OrderBy(row => row.Key).Select(row => $"{row.Key}: {string.Join("; ", row.Value)}"));
            lines.Add("</configured-vocabulary>");
        }
        var value = string.Join('\n', lines);
        return value.Length <= 8_000 ? value : value[..8_000];
    }

    public static RecruitmentDocumentRagContext Empty(int clientId, string documentKind) =>
        new(RecruitmentDocumentRagService.EngineName, clientId,
            documentKind.Equals(RecruitmentDocumentRagService.ResumeDocumentKind, StringComparison.OrdinalIgnoreCase)
                ? RecruitmentDocumentRagService.ResumeDocumentKind
                : RecruitmentDocumentRagService.HiringDocumentKind,
            [], new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
}

public sealed record RecruitmentDocumentRagReference(
    long ReferenceId,
    string SourceType,
    string Title,
    string Department,
    string Status,
    decimal Similarity,
    string MatchedEvidence,
    DateTime UpdatedAtUtc,
    string PromptContext);

public sealed record RecruitmentRagVocabularyMatch(
    string VocabularyType,
    string OriginalValue,
    string SelectedValue,
    decimal Confidence,
    string MatchMode);
