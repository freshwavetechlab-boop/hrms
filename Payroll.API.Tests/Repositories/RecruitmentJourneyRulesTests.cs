using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;
using Candidate = Payroll.API.Repositories.RecruitmentHiringProgress.Candidate;

namespace Payroll.API.Tests.Repositories;

public class RecruitmentJourneyRulesTests
{
    [Theory]
    [InlineData(80.97, "Ineligible", "Completed")]
    [InlineData(76.91, "Ineligible", "Completed")]
    [InlineData(75, "NeedsReview", "Completed")]
    [InlineData(74.99, "Ineligible", "Ineligible")]
    [InlineData(74.99, "NeedsReview", "NeedsReview")]
    [InlineData(80, "CompletedWithAiFallback", "CompletedWithAiFallback")]
    public void Passing_cutoff_qualifies_legacy_scores_without_discarding_evidence(double score, string storedStatus, string expectedStatus)
    {
        var effectiveStatus = RecruitmentAtsDomainRules.EffectiveScoreStatus((decimal)score, 75, storedStatus);
        Assert.Equal(expectedStatus, effectiveStatus);
        if (score >= 75)
        {
            Assert.False(RecruitmentPipelineRepository.AtsNeedsHumanReview(effectiveStatus, false, false, false));
            Assert.True(RecruitmentPipelineRepository.AtsNeedsHumanReview(effectiveStatus, true, false, false));
        }
        Assert.Equal(RecruitmentAtsEligibilityStatus.Ineligible, RecruitmentAtsDomainRules.EvaluateMustHaveGate(["Missing"]));
    }

    [Fact]
    public void Missing_score_cannot_qualify_a_candidate() =>
        Assert.Equal("NeedsReview", RecruitmentAtsDomainRules.EffectiveScoreStatus(null, 75, "NeedsReview"));

    [Fact]
    public void Budget_rule_is_default_but_explicit_candidate_choice_wins()
    {
        var terms = new RecruitmentNegotiation { ExpectedCtc = 1200000, ApprovedBudget = 1000000 };
        Assert.True(terms.Required);
        terms.NegotiationOverride = false;
        Assert.False(terms.Required);
        terms.ExpectedCtc = 900000;
        terms.NegotiationOverride = true;
        Assert.True(terms.Required);
        terms.NegotiationOverride = null;
        Assert.False(terms.Required);
    }

    [Fact]
    public void Selection_allows_negotiation_but_mom_waits_for_agreed_terms()
    {
        var candidate = new Candidate { HasCompletedInterviewDecision = true, CandidateStageType = "HR", LatestInterviewResult = "Selected" };
        Assert.True(RecruitmentHiringProgress.SelectionComplete(candidate));
        Assert.False(RecruitmentHiringProgress.MoMReady(candidate));
        candidate.TermsConfirmed = true;
        Assert.True(RecruitmentHiringProgress.MoMReady(candidate));
        candidate.LatestInterviewResult = "Rejected";
        Assert.False(RecruitmentHiringProgress.MoMReady(candidate));
    }

    [Fact]
    public void Individual_terms_can_complete_while_position_waits_for_vacancies()
    {
        var candidate = new Candidate { HasCompletedInterviewDecision = true, TermsConfirmed = true };
        Assert.True(RecruitmentHiringProgress.MoMReady(candidate));
        Assert.False(RecruitmentHiringProgress.Enough(3, [candidate], RecruitmentHiringProgress.MoMReady));
        Assert.True(RecruitmentHiringProgress.Enough(1, [candidate], RecruitmentHiringProgress.MoMReady));
    }

    [Fact]
    public void Hr_waits_for_candidate_signature_and_offer_waits_for_hr_approval()
    {
        var candidate = new Candidate { HasCompletedInterviewDecision = true, TermsConfirmed = true };
        var hr = RecruitmentHiringProgress.EntryGate("HR_DIVISION_APPROVAL", "HR Division approval", "Approval")!;
        var offer = RecruitmentHiringProgress.EntryGate("OFFER_ISSUANCE", "Offer issuance", "Offer")!;
        Assert.False(hr(candidate));
        candidate.CandidateMomSigned = true;
        Assert.True(hr(candidate));
        Assert.False(offer(candidate));
        candidate.CandidateMomApproved = true;
        Assert.True(offer(candidate));
    }

    [Fact]
    public void Accepted_offer_does_not_count_as_joining()
    {
        var candidate = new Candidate { LatestOfferStatus = "Accepted" };
        var joining = RecruitmentHiringProgress.EntryGate("JOINING_DATE", "Joining", "Completed")!;
        Assert.False(joining(candidate));
        candidate.IsJoined = true;
        Assert.True(joining(candidate));
    }

    [Fact]
    public void Admin_is_allowed_by_default_and_other_users_need_assigned_permission()
    {
        Assert.True(RecruitmentPermissions.Has(new AuthUser { Permissions = ["settings.manage"] }, "recruitment.offer.manage"));
        Assert.True(RecruitmentPermissions.Has(new AuthUser { Roles = ["super_admin"] }, "recruitment.offer.manage"));
        Assert.True(RecruitmentPermissions.Has(new AuthUser { Permissions = ["recruitment.proposal.manage"] }, "recruitment.proposal.manage"));
        Assert.False(RecruitmentPermissions.Has(new AuthUser { Permissions = ["recruitment.interview.panel"] }, "recruitment.proposal.manage"));
    }
}
