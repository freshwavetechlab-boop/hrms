namespace Payroll.API.Services;

internal static class RecruitmentEmployeeApplicationState
{
    internal static bool PreserveOfferResponse(int? employeeId, string? stage, string? status) =>
        employeeId is > 0 || IsJoined(stage) || IsJoined(status);

    private static bool IsJoined(string? value) => value?.Trim().ToUpperInvariant() is "JOINED" or "HIRED";

    // Recheck at write time as an employee import may finish after the offer
    // handler reads its application snapshot. Offer response/history still save.
    internal const string MutableApplicationSql = """
UPPER(TRIM(COALESCE(CurrentStage,''))) NOT IN ('JOINED','HIRED')
AND UPPER(TRIM(COALESCE(CurrentStatus,''))) NOT IN ('JOINED','HIRED')
AND NOT EXISTS(SELECT 1 FROM recruitment_candidates joinedCandidate
 JOIN employees joinedEmployee ON joinedEmployee.Id=COALESCE(recruitment_candidate_applications.JoinedEmployeeId,joinedCandidate.EmployeeId)
 AND joinedEmployee.ClientId=recruitment_candidate_applications.ClientId
 WHERE joinedCandidate.Id=recruitment_candidate_applications.CandidateId AND joinedCandidate.ClientId=recruitment_candidate_applications.ClientId)
""";
}
