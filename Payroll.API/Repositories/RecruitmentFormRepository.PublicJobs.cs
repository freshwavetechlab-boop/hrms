using Dapper;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public sealed partial class RecruitmentFormRepository
{
    public async Task<IReadOnlyList<PublicRecruitmentJobSummary>> ListPublicJobsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = Db();
        await db.OpenAsync(cancellationToken);
        // All-client access is intentional for this public projection only.
        // No employee, candidate, approval, budget or contact table is returned.
        var rows = (await db.QueryAsync<PublicJobDirectoryRow>(new CommandDefinition(@"
SELECT j.Id PostingId,j.PositionId,j.JobDescriptionVersionId,j.PublicSlug,j.PublicTitle,j.Status PostingStatus,
j.OpensAtUtc,j.ClosesAtUtc,j.PublishedAtUtc,j.MaximumApplications,j.ApplicationCount,c.IsActive ClientActive,
p.PositionCode,p.PositionTitle,c.Name ClientName,p.Department,p.EmploymentType,p.Status PositionStatus,
p.NumberOfPositions,p.RemainingPositions,r.Status RequisitionStatus,
COALESCE(NULLIF(r.JobLocation,''),p.JobLocation,'') JobLocation,COALESCE(r.WorkMode,'') WorkMode,
COALESCE(NULLIF(r.ExperienceRange,''),p.ExperienceRange,'') ExperienceRange,
COALESCE(NULLIF(r.SalaryMin,0),p.SalaryMin,0) SalaryMin,COALESCE(NULLIF(r.SalaryMax,0),p.SalaryMax,0) SalaryMax,
COALESCE(NULLIF(r.Currency,''),NULLIF(p.Currency,''),'INR') Currency,
d.Summary,d.RolePurpose,d.Status JobDescriptionStatus,
(d.ClientId=j.ClientId AND d.RequisitionId=p.RequisitionId AND p.ClientId=j.ClientId AND r.ClientId=j.ClientId) SourceScopeValid,
fv.Status FormStatus,fd.Status FormDefinitionStatus,(fd.ClientId IN (0,j.ClientId)) FormScopeValid
FROM recruitment_job_postings j
JOIN recruitment_open_positions p ON p.Id=j.PositionId
JOIN recruitment_requisitions r ON r.Id=p.RequisitionId
JOIN clients c ON c.Id=j.ClientId
JOIN recruitment_job_description_versions d ON d.Id=j.JobDescriptionVersionId
JOIN form_versions fv ON fv.Id=j.ApplicationFormVersionId
JOIN form_definitions fd ON fd.Id=fv.FormDefinitionId
WHERE j.Status='Published' AND c.IsActive=TRUE
ORDER BY j.PublishedAtUtc DESC,j.Id DESC", cancellationToken: cancellationToken))).ToList();
        var now = DateTime.UtcNow;
        var eligible = rows.Where(row => IsPublicDirectoryJob(row, now)).ToList();
        if (eligible.Count == 0) return [];

        // Pin the approved JD used by this particular public posting. Batch child
        // reads rather than loading application forms/candidate fields per card.
        var ids = eligible.Select(row => row.JobDescriptionVersionId).Distinct().ToArray();
        var details = (await db.QueryAsync<PublicJobDirectoryDetail>(new CommandDefinition(@"
SELECT JobDescriptionVersionId,'responsibilities' Kind,ResponsibilityText Content,DisplayOrder,FALSE RequiresProof FROM recruitment_jd_responsibilities WHERE JobDescriptionVersionId IN @Ids
UNION ALL SELECT JobDescriptionVersionId,'skills',SkillName,DisplayOrder,FALSE FROM recruitment_jd_skill_requirements WHERE JobDescriptionVersionId IN @Ids
UNION ALL SELECT JobDescriptionVersionId,'qualifications',CONCAT_WS(' - ',NULLIF(QualificationName,''),NULLIF(Specialization,'')),DisplayOrder,FALSE FROM recruitment_jd_qualification_requirements WHERE JobDescriptionVersionId IN @Ids
UNION ALL SELECT JobDescriptionVersionId,'certifications',CertificationName,DisplayOrder,(CandidateProofAttachmentFieldConfigurationId IS NOT NULL) FROM recruitment_jd_certification_requirements WHERE JobDescriptionVersionId IN @Ids
UNION ALL SELECT JobDescriptionVersionId,'languages',CONCAT_WS(' - ',NULLIF(LanguageName,''),NULLIF(Proficiency,'')),DisplayOrder,FALSE FROM recruitment_jd_language_requirements WHERE JobDescriptionVersionId IN @Ids
UNION ALL SELECT JobDescriptionVersionId,'benefits',CONCAT_WS(': ',NULLIF(BenefitName,''),NULLIF(Description,'')),DisplayOrder,FALSE FROM recruitment_jd_benefits WHERE JobDescriptionVersionId IN @Ids
ORDER BY JobDescriptionVersionId,Kind,DisplayOrder", new { Ids = ids }, cancellationToken: cancellationToken))).ToLookup(detail => detail.JobDescriptionVersionId);
        var result = new List<PublicRecruitmentJobSummary>();
        var baseUrl = publicPortalUrls.ResolveBaseUrl(configuration["PublicPortal:BaseUrl"]);
        foreach (var row in eligible)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Same gate as the existing anonymous application link. A stale or
            // misconfigured proof/form cannot become an actionable board advert.
            if (details[row.JobDescriptionVersionId].Any(detail => detail.RequiresProof)
                && (await pipelines.GetCandidateProofValidationErrorAsync(row.PostingId)).Length > 0) continue;
            var summary = ToPublicDirectoryJob(row, baseUrl);
            if (summary.PublicUrl.Length == 0) continue;
            List<string> Values(string kind) => details[row.JobDescriptionVersionId].Where(item => item.Kind == kind)
                .Select(item => SafePublicNarrative(item.Content)).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            summary.Responsibilities = Values("responsibilities"); summary.Skills = Values("skills");
            summary.Qualifications = Values("qualifications"); summary.Certifications = Values("certifications");
            summary.Languages = Values("languages"); summary.Benefits = Values("benefits");
            result.Add(summary);
        }
        return result;
    }

    internal static bool IsPublicDirectoryJob(PublicJobDirectoryRow row, DateTime now) => row.ClientActive
        && row.PostingStatus == "Published" && ValidPublicSlug(row.PublicSlug)
        && row.SourceScopeValid && row.FormScopeValid && row.RequisitionStatus == "Approved"
        && row.JobDescriptionStatus == "Approved" && row.FormDefinitionStatus == "Active"
        && row.FormStatus is "Published" or "Retired"
        && row.PositionStatus is not ("Closed" or "Cancelled" or "Filled" or "On Hold")
        && row.RemainingPositions > 0
        && (!row.OpensAtUtc.HasValue || row.OpensAtUtc <= now)
        && (!row.ClosesAtUtc.HasValue || row.ClosesAtUtc >= now)
        && (!row.MaximumApplications.HasValue || row.ApplicationCount < row.MaximumApplications);

    internal static PublicRecruitmentJobSummary ToPublicDirectoryJob(PublicJobDirectoryRow row, string baseUrl) => new()
    {
        PublicSlug = row.PublicSlug, PublicUrl = RecruitmentPublicUrls.BuildCareerUrl(baseUrl, row.PublicSlug),
        PublicTitle = row.PublicTitle, PositionCode = row.PositionCode, PositionTitle = row.PositionTitle,
        ClientName = row.ClientName, Department = row.Department, JobLocation = row.JobLocation,
        EmploymentType = row.EmploymentType, WorkMode = row.WorkMode, ExperienceRange = row.ExperienceRange,
        SalaryMin = row.SalaryMin, SalaryMax = row.SalaryMax, Currency = row.Currency,
        NumberOfPositions = row.NumberOfPositions, RemainingPositions = row.RemainingPositions,
        PublishedAtUtc = PublicDirectoryUtc(row.PublishedAtUtc), OpensAtUtc = PublicDirectoryUtc(row.OpensAtUtc), ClosesAtUtc = PublicDirectoryUtc(row.ClosesAtUtc),
        Summary = SafePublicNarrative(row.Summary) is { Length: > 0 } summary ? summary : SafePublicNarrative(row.RolePurpose),
        RolePurpose = SafePublicNarrative(row.RolePurpose),
    };

    private static DateTime? PublicDirectoryUtc(DateTime? value) => value.HasValue ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc) : null;

    internal sealed class PublicJobDirectoryRow
    {
        public long PostingId { get; set; }
        public long PositionId { get; set; }
        public long JobDescriptionVersionId { get; set; }
        public string PublicSlug { get; set; } = "";
        public string PublicTitle { get; set; } = "";
        public string PositionCode { get; set; } = "";
        public string PositionTitle { get; set; } = "";
        public string ClientName { get; set; } = "";
        public string Department { get; set; } = "";
        public string JobLocation { get; set; } = "";
        public string EmploymentType { get; set; } = "";
        public string WorkMode { get; set; } = "";
        public string ExperienceRange { get; set; } = "";
        public decimal SalaryMin { get; set; }
        public decimal SalaryMax { get; set; }
        public string Currency { get; set; } = "";
        public int NumberOfPositions { get; set; }
        public int RemainingPositions { get; set; }
        public DateTime? PublishedAtUtc { get; set; }
        public DateTime? OpensAtUtc { get; set; }
        public DateTime? ClosesAtUtc { get; set; }
        public string Summary { get; set; } = "";
        public string RolePurpose { get; set; } = "";
        public bool ClientActive { get; set; }
        public bool SourceScopeValid { get; set; }
        public bool FormScopeValid { get; set; }
        public string PostingStatus { get; set; } = "";
        public string PositionStatus { get; set; } = "";
        public string RequisitionStatus { get; set; } = "";
        public string JobDescriptionStatus { get; set; } = "";
        public string FormStatus { get; set; } = "";
        public string FormDefinitionStatus { get; set; } = "";
        public int? MaximumApplications { get; set; }
        public int ApplicationCount { get; set; }
    }
    private sealed class PublicJobDirectoryDetail
    {
        public long JobDescriptionVersionId { get; set; }
        public string Kind { get; set; } = "";
        public string Content { get; set; } = "";
        public int DisplayOrder { get; set; }
        public bool RequiresProof { get; set; }
    }
}
