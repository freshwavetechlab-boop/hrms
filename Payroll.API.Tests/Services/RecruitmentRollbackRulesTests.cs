using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class RecruitmentRollbackRulesTests
{
    [Fact]
    public void Empty_cohort_and_any_remaining_or_joined_candidate_prevent_rollback()
    {
        Assert.False(RecruitmentRollbackRules.AllRejected([]));
        var rejected = new RecruitmentHiringProgress.Candidate { CandidateStageType = "Rejected" };
        Assert.True(RecruitmentRollbackRules.AllRejected([rejected]));
        Assert.False(RecruitmentRollbackRules.AllRejected([rejected, new() { CandidateStageType = "ATS" }]));
        Assert.False(RecruitmentRollbackRules.AllRejected([rejected, new() { CandidateStageType = "Rejected", IsJoined = true }]));
        Assert.False(RecruitmentRollbackRules.AllRejected([new() { LatestInterviewResult = "Selected" }]));
    }

    [Theory]
    [InlineData("Rejected", "", "")]
    [InlineData("Withdrawn", "", "")]
    [InlineData("Interview", "Rejected", "")]
    [InlineData("Interview", "No Show", "")]
    [InlineData("Offer", "Selected", "Rejected")]
    [InlineData("Offer", "Selected", "Withdrawn")]
    [InlineData("Offer", "Selected", "Expired")]
    public void Final_unsuccessful_outcomes_allow_return(string stage, string interview, string offer) =>
        Assert.True(RecruitmentRollbackRules.AllRejected([new() { CandidateStageType = stage, LatestInterviewResult = interview, LatestOfferStatus = offer }]));

    [Fact]
    public void Rule_is_opt_in_and_target_must_be_a_hiring_stage_with_later_work()
    {
        var source = new RecruitmentPipelineStage { StageCode = "SOURCE", CardScope = "Position", DisplayOrder = 1 };
        var interview = new RecruitmentPipelineStage { StageCode = "INTERVIEW", CardScope = "Position", DisplayOrder = 2 };
        Assert.False(new RecruitmentPipelineVersion().AllRejectedRollback.Enabled);
        Assert.Empty(RecruitmentRollbackRules.Validate(new(), []));
        var rule = new RecruitmentAllRejectedRollback { Enabled = true, TargetStageCode = "SOURCE" };
        Assert.Empty(RecruitmentRollbackRules.Validate(rule, [source, interview]));
        Assert.NotEmpty(RecruitmentRollbackRules.Validate(rule, [interview]));
        source.CardScope = "Application";
        Assert.NotEmpty(RecruitmentRollbackRules.Validate(rule, [source, interview]));
        source.CardScope = "Position"; source.IsActive = false;
        Assert.NotEmpty(RecruitmentRollbackRules.Validate(rule, [source, interview]));
        source.IsActive = true; source.IsTerminal = true;
        Assert.NotEmpty(RecruitmentRollbackRules.Validate(rule, [source, interview]));
        source.IsTerminal = false;
        Assert.NotEmpty(RecruitmentRollbackRules.Validate(rule, [source]));
    }
}
