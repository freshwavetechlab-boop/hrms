namespace Payroll.API.Repositories;

// Server-owned aliases only. Counts represent distinct applications, not interview rounds.
internal static class RecruitmentHiringProgress
{
    internal static string SelectFor(string positionAlias) => $@"
GREATEST(0,COALESCE({positionAlias}.NumberOfPositions,0)-COALESCE({positionAlias}.CancelledPositions,0)-COALESCE({positionAlias}.OnHoldPositions,0)) RequiredCandidateCount,
(SELECT COUNT(*) FROM recruitment_candidate_applications progressApplication
 JOIN recruitment_interviews latestInterview ON latestInterview.Id=(SELECT MAX(roundRow.Id)
 FROM recruitment_interviews roundRow WHERE roundRow.ApplicationId=progressApplication.Id)
 WHERE progressApplication.PositionId={positionAlias}.Id AND progressApplication.ApplicationType='Application'
 AND latestInterview.Status IN ('Scheduled','Rescheduled')
 AND progressApplication.CurrentStage NOT IN ('Rejected','Withdrawn','Joined','Offer Rejected')) InPanelCount,
(SELECT COUNT(*) FROM recruitment_candidate_applications progressApplication
 JOIN recruitment_interviews latestInterview ON latestInterview.Id=(SELECT MAX(roundRow.Id)
 FROM recruitment_interviews roundRow WHERE roundRow.ApplicationId=progressApplication.Id)
 WHERE progressApplication.PositionId={positionAlias}.Id AND progressApplication.ApplicationType='Application'
 AND latestInterview.Status='Completed' AND latestInterview.Result='Selected'
 AND progressApplication.CurrentStage NOT IN ('Rejected','Withdrawn','Offer Rejected')
 AND COALESCE((SELECT latestOffer.Status FROM recruitment_offers latestOffer WHERE latestOffer.ApplicationId=progressApplication.Id
 ORDER BY latestOffer.UpdatedAt DESC,latestOffer.Id DESC LIMIT 1),'') NOT IN ('Rejected','Withdrawn','Expired')) SelectedCandidateCount,
";
}
