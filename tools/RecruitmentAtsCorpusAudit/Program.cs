using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: RecruitmentAtsCorpusAudit <API appsettings.json> <output.json>");
    return 2;
}
var configuration = new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(args[0])).Build();
if (Path.GetFullPath(args[0]).Equals(Path.GetFullPath(args[1]), StringComparison.OrdinalIgnoreCase))
    throw new ArgumentException("Choose a separate report path; configuration cannot be overwritten.");
await using var db = new MySqlConnection(configuration.GetConnectionString("Default"));
await db.OpenAsync();
await db.ExecuteAsync("START TRANSACTION READ ONLY");
List<ResumeRow> resumes;
List<JdRow> documents;
List<RecruitmentJdQualificationRequirement> qualifications;
List<RecruitmentJdCertificationRequirement> certifications;
List<ScoreRow> scores;
List<ApplicationRow> applications;
try
{
    resumes = (await db.QueryAsync<ResumeRow>(@"SELECT resume.Id,resume.IsPrimary,resume.ParsingStatus,COALESCE(resume.ParsedText,'') ParsedText,
facts.TotalExperienceMonths StoredMonths FROM recruitment_candidate_resumes resume
LEFT JOIN recruitment_resume_parse_facts facts ON facts.ResumeId=resume.Id ORDER BY resume.Id")).ToList();
    documents = (await db.QueryAsync<JdRow>(@"SELECT jd.Id,jd.Title,jd.Status,COALESCE(jd.Summary,'') Summary,COALESCE(jd.RolePurpose,'') RolePurpose,
COALESCE(r.ExperienceRange,'') ExperienceRange FROM recruitment_job_description_versions jd
JOIN recruitment_requisitions r ON r.Id=jd.RequisitionId AND r.ClientId=jd.ClientId ORDER BY jd.Id")).ToList();
    qualifications = (await db.QueryAsync<RecruitmentJdQualificationRequirement>("SELECT * FROM recruitment_jd_qualification_requirements")).ToList();
    certifications = (await db.QueryAsync<RecruitmentJdCertificationRequirement>("SELECT * FROM recruitment_jd_certification_requirements")).ToList();
    applications = (await db.QueryAsync<ApplicationRow>(@"SELECT a.Id,a.ResumeId,
COALESCE(posting.JobDescriptionVersionId,p.ApprovedJobDescriptionVersionId) JobDescriptionVersionId
FROM recruitment_candidate_applications a JOIN recruitment_open_positions p ON p.Id=a.PositionId
LEFT JOIN recruitment_job_postings posting ON posting.Id=a.JobPostingId AND posting.PositionId=a.PositionId")).ToList();
    scores = (await db.QueryAsync<ScoreRow>(@"SELECT ApplicationId,ResumeId,ScoreStatus,AiAnalysisStatus,AiProvider,AiConfidence
FROM recruitment_application_scores WHERE IsCurrent=TRUE")).ToList();
}
finally { await db.ExecuteAsync("ROLLBACK"); }

// No application host, worker, schema bootstrap, AI provider or write repository
// is started. Replay only local parsing against the complete stored source text.
var parser = new ResumeParsingService(null!, null!, NullLogger<ResumeParsingService>.Instance);
var reviews = new List<ResumeReview>();
foreach (var source in resumes)
{
    var bytes = Encoding.UTF8.GetBytes(source.ParsedText);
    var parsed = await parser.ParseAsync(new MemoryStream(bytes), "audit.txt", bytes.Length, CancellationToken.None);
    reviews.Add(new ResumeReview(source.Id, source.IsPrimary, source.ParsingStatus, source.ParsedText.Length,
        Convert.ToHexString(SHA256.HashData(bytes)), source.StoredMonths, parsed.Facts.TotalExperienceMonths,
        source.StoredMonths != parsed.Facts.TotalExperienceMonths,
        parsed.Status != "Parsed" || !RecruitmentDocumentTextQuality.IsReadable(source.ParsedText)));
}
var byResume = resumes.ToDictionary(row => row.Id);
var applicantReviews = applications.Select(application =>
{
    var jd = documents.FirstOrDefault(row => row.Id == application.JobDescriptionVersionId);
    var resume = application.ResumeId.HasValue ? byResume.GetValueOrDefault(application.ResumeId.Value) : null;
    var reasons = jd is null || resume is null ? new[] { "The selected JD or saved resume source is unavailable." }
        : RecruitmentMandatoryEvidence.ReviewReasons(resume.ParsedText, "",
            qualifications.Where(row => row.JobDescriptionVersionId == jd.Id && row.IsMandatory).Select(row => (row.QualificationName, row.Specialization)).ToList(),
            certifications.Where(row => row.JobDescriptionVersionId == jd.Id && row.IsMandatory).Select(row => row.CertificationName).ToList());
    return new { application.Id, application.ResumeId, JobDescriptionVersionId = jd?.Id, MandatoryEvidenceReview = reasons };
}).ToList();
var documentReviews = documents.Select(jd => new
{
    jd.Id, jd.Title, jd.Status, jd.ExperienceRange,
    DamagedSource = !RecruitmentDocumentTextQuality.IsReadable(jd.Summary) || !RecruitmentDocumentTextQuality.IsReadable(jd.RolePurpose),
    AmbiguousDegreeRows = qualifications.Count(row => row.JobDescriptionVersionId == jd.Id && row.IsMandatory) > 1,
    ExperienceInsideCertification = certifications.Any(row => row.JobDescriptionVersionId == jd.Id
        && row.CertificationName.Contains("Experience Requirements", StringComparison.OrdinalIgnoreCase))
}).ToList();
var report = new
{
    GeneratedAtUtc = DateTime.UtcNow, BusinessRecordsChanged = 0, ProviderCalls = 0,
    Scope = "All saved JD versions and resume text versions; replay differences require review, not automatic correction or a measured accuracy claim.",
    Summary = new { JobDescriptions = documents.Count, ResumeVersions = resumes.Count, CurrentResumes = resumes.Count(row => row.IsPrimary), Applications = applications.Count,
        ExperienceDifferences = reviews.Count(row => row.ExperienceNeedsReview), DamagedResumeSources = reviews.Count(row => row.SourceNeedsReview),
        AiStatuses = scores.GroupBy(row => row.AiAnalysisStatus).Select(group => new { Status = group.Key, Count = group.Count() }) },
    Documents = documentReviews, Resumes = reviews, Applications = applicantReviews
};
var output = Path.GetFullPath(args[1]);
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(new { report.Summary, BusinessRecordsChanged = 0, ProviderCalls = 0, Output = output }));
return 0;

sealed class ResumeRow
{
    public long Id { get; set; }
    public bool IsPrimary { get; set; }
    public string ParsingStatus { get; set; } = "";
    public string ParsedText { get; set; } = "";
    public int? StoredMonths { get; set; }
}
sealed class JdRow
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public string Status { get; set; } = "";
    public string Summary { get; set; } = "";
    public string RolePurpose { get; set; } = "";
    public string ExperienceRange { get; set; } = "";
}
sealed class ScoreRow
{
    public string AiAnalysisStatus { get; set; } = "";
}
sealed class ApplicationRow
{
    public long Id { get; set; }
    public long? ResumeId { get; set; }
    public long? JobDescriptionVersionId { get; set; }
}
sealed record ResumeReview(long Id, bool IsPrimary, string StoredStatus, int SourceCharacters, string SourceHash,
    int? StoredMonths, int? ParsedMonths, bool ExperienceNeedsReview, bool SourceNeedsReview);
