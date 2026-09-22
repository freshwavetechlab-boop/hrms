using Dapper;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

internal static class RecruitmentProfileReadiness
{
    // Use the application's pinned form. A profile-update form also covers imported
    // candidates who were added without a public application submission.
    internal static async Task<string> MissingAsync(MySqlConnection db, long applicationId, MySqlTransaction? transaction = null)
    {
        var source = await db.QueryFirstOrDefaultAsync<Source>(@"SELECT a.ClientId,a.CandidateId,
COALESCE(posting.ApplicationFormVersionId,
 (SELECT s.FormVersionId FROM form_submissions s
  JOIN form_versions v ON v.Id=s.FormVersionId
  JOIN form_definitions d ON d.Id=v.FormDefinitionId AND d.ClientId IN (0,a.ClientId)
  WHERE s.ApplicationId=a.Id AND s.CandidateId=a.CandidateId AND s.ClientId=a.ClientId
   AND d.PurposeCode IN ('CANDIDATE_APPLICATION','CANDIDATE_INFORMATION_UPDATE','PROFILE_UPDATE') ORDER BY s.Id DESC LIMIT 1),
 (SELECT CASE WHEN COUNT(DISTINCT d.CurrentPublishedVersionId)=1 THEN MAX(d.CurrentPublishedVersionId) END
  FROM form_definitions d JOIN form_versions v ON v.Id=d.CurrentPublishedVersionId AND v.Status='Published'
  WHERE d.ClientId IN (0,a.ClientId) AND d.Status='Active' AND d.PurposeCode IN ('CANDIDATE_APPLICATION','CANDIDATE_INFORMATION_UPDATE','PROFILE_UPDATE'))) FormVersionId
FROM recruitment_candidate_applications a
LEFT JOIN recruitment_job_postings posting ON posting.Id=COALESCE(a.JobPostingId,
 (SELECT job.Id FROM recruitment_job_postings job WHERE job.PositionId=a.PositionId AND job.ClientId=a.ClientId
  AND job.Status='Published' ORDER BY job.Id DESC LIMIT 1)) AND posting.ClientId=a.ClientId
WHERE a.Id=@Id", new { Id = applicationId }, transaction);
        if (source is null) return "Candidate application was not found.";
        if (source.FormVersionId is null) return "Configure a candidate application/profile form for this job.";
        var candidate = await db.QueryFirstOrDefaultAsync<RecruitmentCandidate>(
            "SELECT * FROM recruitment_candidates WHERE Id=@CandidateId AND ClientId IN (0,@ClientId)", source, transaction);
        if (candidate is null) return "Candidate profile was not found in this client.";
        var submissionId = await db.ExecuteScalarAsync<long>(@"SELECT COALESCE(MAX(Id),0) FROM form_submissions
WHERE ApplicationId=@ApplicationId AND CandidateId=@CandidateId AND ClientId=@ClientId
AND FormVersionId=@FormVersionId AND Status='Submitted'", new { ApplicationId = applicationId, source.CandidateId, source.ClientId, source.FormVersionId }, transaction);
        var missing = await RecruitmentFormRepository.RequiredSubmissionMissingFieldsAsync(db, transaction, submissionId, source.FormVersionId.Value);
        var hasCertification = await db.ExecuteScalarAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM recruitment_candidate_certifications WHERE CandidateId=@CandidateId AND TRIM(CertificationName)<>'')", source, transaction);
        var uploads = (await db.QueryAsync<(long ConfigurationId, int Count)>(@"SELECT attachment.field_configuration_id ConfigurationId,COUNT(DISTINCT attachment.id) Count
FROM entity_attachments attachment
JOIN attachment_field_configurations configuration ON configuration.id=attachment.field_configuration_id
WHERE attachment.entity_type='CANDIDATE' AND attachment.entity_id=@CandidateId
 AND attachment.is_current=TRUE AND attachment.is_deleted=FALSE AND configuration.client_id IN (0,@ClientId)
GROUP BY attachment.field_configuration_id", source, transaction)).ToDictionary(row => row.ConfigurationId, row => row.Count);
        // Reuse field-level required validation, including secure uploads and custom fields.
        // Current profile values may complete mapped fields after an administrator edit;
        // the original submitted answers remain untouched.
        return string.Join(", ", missing.Where(field => !ProfileSupplies(field, candidate, hasCertification)
            && !(field.FieldTypeCode == "UPLOAD" && field.AttachmentFieldConfigurationId is long configurationId
                && uploads.GetValueOrDefault(configurationId) >= field.MinimumFileCount)).Select(field => field.Label));
    }

    internal static bool ProfileSupplies(RecruitmentFormRepository.RequiredProfileField field, RecruitmentCandidate row, bool hasCertification)
    {
        if (field.FieldTypeCode is "UPLOAD" or "RADIO" or "MULTI_SELECT" or "SEARCH_SELECT") return false;
        return field.SemanticCode.ToUpperInvariant() switch
        {
            "FIRST_NAME" => !string.IsNullOrWhiteSpace(row.FirstName),
            "LAST_NAME" => !string.IsNullOrWhiteSpace(row.LastName),
            "EMAIL" => !string.IsNullOrWhiteSpace(row.Email),
            "PHONE" => !string.IsNullOrWhiteSpace(row.Phone),
            "CURRENT_COMPANY" => !string.IsNullOrWhiteSpace(row.CurrentCompany),
            "CURRENT_DESIGNATION" => !string.IsNullOrWhiteSpace(row.CurrentTitle),
            "CURRENT_LOCATION" => !string.IsNullOrWhiteSpace(row.CurrentLocation),
            "HIGHEST_QUALIFICATION" => !string.IsNullOrWhiteSpace(row.HighestQualification),
            // Imported profiles default to zero; an explicitly submitted zero is
            // already accepted by the form validator before reaching this fallback.
            "TOTAL_EXPERIENCE_MONTHS" or "TOTAL_EXPERIENCE_YEARS" => row.TotalExperienceMonths > 0,
            "CURRENT_CTC" => row.CurrentCtc.HasValue,
            "EXPECTED_CTC" => row.ExpectedCtc.HasValue,
            "NOTICE_PERIOD_DAYS" => row.NoticePeriodDays.HasValue,
            "CERTIFICATIONS" => hasCertification,
            "CONSENT" => row.ConsentStatus.Equals("Granted", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private sealed class Source
    {
        public int ClientId { get; set; }
        public long CandidateId { get; set; }
        public long? FormVersionId { get; set; }
    }
}
