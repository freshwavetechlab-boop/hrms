using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class RecruitmentTalentRepository
{
    private async Task RememberRecruiterProfileAsync(MySqlConnection db, RecruitmentCandidate before,
        SaveRecruitmentCandidate reviewed, AuthUser user)
    {
        // This hook runs after access checks and a successful existing-profile save,
        // never when a parser creates a new candidate or a public applicant submits.
        try
        {
            if (reviewed.ProfileStatus == "Archived" || reviewed.ConsentStatus == "Revoked")
            { resumeParser.ForgetLearning(before.ClientId, before.Id); return; }
            var source = await db.QueryFirstOrDefaultAsync<string>(@"SELECT ParsedText
FROM recruitment_candidate_resumes WHERE CandidateId=@Id AND IsPrimary=TRUE AND ParsingStatus='Parsed'
ORDER BY Id DESC LIMIT 1", new { before.Id });
            if (string.IsNullOrWhiteSpace(source)) return;
            resumeParser.LearnReviewed(before.ClientId, before.Id, source,
                ResumeParsedFacts.Empty with { CurrentCompany = before.CurrentCompany, CurrentTitle = before.CurrentTitle,
                    CurrentLocation = before.CurrentLocation, HighestQualification = before.HighestQualification },
                ResumeParsedFacts.Empty with { CurrentCompany = reviewed.CurrentCompany, CurrentTitle = reviewed.CurrentTitle,
                    CurrentLocation = reviewed.CurrentLocation, HighestQualification = reviewed.HighestQualification }, user.Id);
        }
        catch (Exception exception) { logger.LogWarning(exception, "Recruiter changes were saved; resume learning was unavailable."); }
    }

}
