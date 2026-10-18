using Dapper;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public sealed partial class RecruitmentTalentRepository
{
    private static async Task<Dictionary<long, IReadOnlyList<string>>> MandatoryApplicationReviewAsync(MySqlConnection db, long[] ids)
    {
        if (ids.Length == 0) return [];
        var contexts = (await db.QueryAsync<MandatoryEvidenceContext>(@"SELECT applicationRow.Id Id,
COALESCE(posting.JobDescriptionVersionId,positionRow.ApprovedJobDescriptionVersionId) JobDescriptionVersionId,
COALESCE(resume.ParsedText,'') ResumeText,candidate.HighestQualification QualificationEvidence
FROM recruitment_candidate_applications applicationRow
JOIN recruitment_candidates candidate ON candidate.Id=applicationRow.CandidateId
JOIN recruitment_open_positions positionRow ON positionRow.Id=applicationRow.PositionId
LEFT JOIN recruitment_job_postings posting ON posting.Id=COALESCE(applicationRow.JobPostingId,CASE WHEN applicationRow.SourceType='Public Job' THEN applicationRow.SourceReferenceId END)
 AND posting.PositionId=applicationRow.PositionId
LEFT JOIN recruitment_candidate_resumes resume ON resume.Id=applicationRow.ResumeId AND resume.CandidateId=candidate.Id
WHERE applicationRow.Id IN @Ids", new { Ids = ids })).ToList();
        return await MandatoryReviewAsync(db, contexts);
    }

    private static async Task<Dictionary<long, IReadOnlyList<string>>> MandatoryReviewAsync(MySqlConnection db, IReadOnlyCollection<MandatoryEvidenceContext> contexts)
    {
        var ids = contexts.Where(x => x.JobDescriptionVersionId.HasValue).Select(x => x.JobDescriptionVersionId!.Value).Distinct().ToArray();
        if (ids.Length == 0) return [];
        var qualifications = (await db.QueryAsync<MandatoryQualificationRow>(@"SELECT JobDescriptionVersionId,QualificationName,Specialization
FROM recruitment_jd_qualification_requirements WHERE JobDescriptionVersionId IN @Ids AND IsMandatory=TRUE ORDER BY DisplayOrder,Id", new { Ids = ids })).ToLookup(x => x.JobDescriptionVersionId);
        var certifications = (await db.QueryAsync<MandatoryCertificationRow>(@"SELECT JobDescriptionVersionId,CertificationName
FROM recruitment_jd_certification_requirements WHERE JobDescriptionVersionId IN @Ids AND IsMandatory=TRUE ORDER BY DisplayOrder,Id", new { Ids = ids })).ToLookup(x => x.JobDescriptionVersionId);
        return contexts.Where(x => x.JobDescriptionVersionId.HasValue).ToDictionary(x => x.Id, x =>
            RecruitmentMandatoryEvidence.ReviewReasons(x.ResumeText, x.QualificationEvidence,
                qualifications[x.JobDescriptionVersionId!.Value].Select(q => (q.QualificationName, q.Specialization)).ToList(),
                certifications[x.JobDescriptionVersionId.Value].Select(c => c.CertificationName).ToList()));
    }

    private sealed class MandatoryEvidenceContext
    {
        public long Id { get; set; }
        public long? JobDescriptionVersionId { get; set; }
        public string ResumeText { get; set; } = "";
        public string QualificationEvidence { get; set; } = "";
    }
    private sealed class MandatoryQualificationRow
    {
        public long JobDescriptionVersionId { get; set; }
        public string QualificationName { get; set; } = "";
        public string Specialization { get; set; } = "";
    }
    private sealed class MandatoryCertificationRow
    {
        public long JobDescriptionVersionId { get; set; }
        public string CertificationName { get; set; } = "";
    }
}
