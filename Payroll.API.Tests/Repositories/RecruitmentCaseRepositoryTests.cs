using Payroll.API.Repositories;

namespace Payroll.API.Tests.Repositories;

public sealed class RecruitmentCaseRepositoryTests
{
    [Fact]
    public void OfferIssuance_WaitsForCandidateAcceptance()
    {
        Assert.False(RecruitmentCaseRepository.AreAllContinuingOffersAccepted(["Pending Candidate"]));
        Assert.False(RecruitmentCaseRepository.AreAllContinuingOffersAccepted(["Released"]));
        Assert.False(RecruitmentCaseRepository.AreAllContinuingOffersAccepted(["Negotiation"]));
        Assert.False(RecruitmentCaseRepository.AreAllContinuingOffersAccepted(["Accepted", "Pending Candidate"]));
        Assert.True(RecruitmentCaseRepository.AreAllContinuingOffersAccepted(["Accepted", "accepted"]));
    }
}
