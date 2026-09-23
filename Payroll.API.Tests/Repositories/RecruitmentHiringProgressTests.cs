using Payroll.API.Repositories;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Candidate = Payroll.API.Repositories.RecruitmentHiringProgress.Candidate;
using Stage = Payroll.API.Repositories.RecruitmentHiringProgress.PositionStage;

namespace Payroll.API.Tests.Repositories;

public sealed class RecruitmentHiringProgressTests
{
    private static Candidate Interview() => new() { HasInterview = true, CandidateStageType = "Interview", CandidateStageName = "Interview / Panel Assessment" };
    private static Candidate Hr() => new() { HasInterview = true, HasCompletedInterviewDecision = true, CandidateStageType = "HR", CandidateStageName = "Selection & HR Review", LatestInterviewResult = "Selected" };
    private static Candidate Offer(string status = "Released") => new() { HasInterview = true, HasCompletedInterviewDecision = true, CandidateStageType = "Offer", CandidateStageName = "Offer Issuance & Acceptance", LatestInterviewResult = "Selected", LatestOfferStatus = status, TermsConfirmed = true, CandidateMomSigned = true, CandidateMomApproved = true };
    private static readonly Stage[] Stages = [
        new() { Id = 1, DisplayOrder = 1, StageCode = "ORDER_FOR_HIRING", StageName = "Order for Hiring", StageType = "Screening" },
        new() { Id = 2, DisplayOrder = 2, StageCode = "SHARING_PROFILES", StageName = "Sharing of Profiles for Interview", StageType = "Screening" },
        new() { Id = 3, DisplayOrder = 3, StageCode = "PANEL_ASSESSMENT", StageName = "Interview / Panel Assessment", StageType = "Interview" },
        new() { Id = 4, DisplayOrder = 5, StageCode = "SIGNING_MOM", StageName = "Signing of MOM", StageType = "Approval" },
        new() { Id = 5, DisplayOrder = 4, StageCode = "NEGOTIATION_AND_MOM_TO_HR", StageName = "Negotiation with Selected Candidates", StageType = "HR" },
        new() { Id = 6, DisplayOrder = 6, StageCode = "HR_DIVISION_APPROVAL", StageName = "Conveying of Approval by HR Division", StageType = "Approval" },
        new() { Id = 7, DisplayOrder = 7, StageCode = "OFFER_ISSUANCE", StageName = "Issuance of Offer", StageType = "Offer" },
    ];

    [Fact]
    public void Required_two_does_not_move_with_only_one_but_ignores_extra_pending_candidates()
    {
        Assert.False(RecruitmentHiringProgress.Enough(2, [Interview()], RecruitmentHiringProgress.InterviewsReady));
        Assert.True(RecruitmentHiringProgress.Enough(2, [Interview(), Interview(), new() { CandidateStageType = "ATS" }], RecruitmentHiringProgress.InterviewsReady));
        Assert.True(RecruitmentHiringProgress.Enough(1, [Interview(), new() { CandidateStageType = "ATS" }], RecruitmentHiringProgress.InterviewsReady));
        Assert.False(RecruitmentHiringProgress.Enough(0, [Interview()], RecruitmentHiringProgress.InterviewsReady));
    }

    [Fact]
    public void Selected_candidates_support_negotiation_while_unconfirmed_terms_hold_mom()
    {
        Candidate[] rows = [Offer(), Hr()];
        Assert.False(RecruitmentHiringProgress.Enough(2, rows, RecruitmentHiringProgress.ReviewComplete));
        Assert.Equal(5, RecruitmentHiringProgress.SupportedInitialStage(Stages, 2, rows)?.Id);
        Assert.Equal(5, RecruitmentHiringProgress.SupportedRollbackStage(Stages.Where(stage => stage.Id < 7), 2, rows)?.Id);
    }

    [Fact]
    public void Rejected_candidate_uses_replacement_progress_without_resetting_to_intake()
    {
        var rejected = Offer();
        rejected.LatestOfferStatus = "Rejected";
        Candidate[] rows = [Offer(), rejected, Hr(), new() { CandidateStageType = "ATS" }];
        Assert.Equal(5, RecruitmentHiringProgress.SupportedRollbackStage(Stages.Where(stage => stage.Id < 7), 2, rows)?.Id);
        Assert.Equal(2, RecruitmentHiringProgress.SupportedRollbackStage(Stages.Where(stage => stage.Id < 7), 2, [Offer(), rejected])?.Id);
    }

    [Fact]
    public void Rejection_of_an_extra_candidate_does_not_reduce_sufficient_cohort()
    {
        var rejected = Offer(); rejected.LatestInterviewResult = "Rejected";
        var gate = RecruitmentHiringProgress.EntryGate("OFFER_ISSUANCE", "Issuance of Offer", "Offer")!;
        Assert.True(RecruitmentHiringProgress.Enough(2, [Offer(), Offer(), rejected], gate));
    }

    [Fact]
    public void Required_acceptances_do_not_wait_for_extra_unaccepted_offers()
    {
        Assert.True(RecruitmentHiringProgress.Enough(2, [Offer("Accepted"), Offer("Accepted"), Offer("Released")], row => RecruitmentHiringProgress.OfferAt(row, "Accepted")));
        Assert.False(RecruitmentHiringProgress.Enough(2, [Offer("Accepted"), Offer("Released")], row => RecruitmentHiringProgress.OfferAt(row, "Accepted")));
    }

    [Fact]
    public void Missing_case_projection_stops_at_document_gate_and_does_not_skip_mom()
    {
        Assert.Equal(4, RecruitmentHiringProgress.SupportedInitialStage(Stages, 1, [Offer("Accepted")])?.Id);
    }

    [Fact]
    public void Summary_counts_every_journey_but_pending_reason_uses_required_count()
    {
        Candidate[] rows = [Interview(), Interview(), Interview(), Hr(), new() { CandidateStageType = "ATS", CandidateStageName = "ATS Screening & JD Match" }];
        var progress = RecruitmentHiringProgress.Describe(1, rows, "Interview / Panel Assessment");
        Assert.Equal(3, progress.CandidateStages.Single(stage => stage.Stage == "Interview / Panel Assessment").Count);
        Assert.Equal(1, progress.CandidateStages.Single(stage => stage.Stage == "Selection & HR Review").Count);
        Assert.Equal(1, progress.CandidateStages.Single(stage => stage.Stage == "ATS Screening & JD Match").Count);
        Assert.Contains("Candidate requirement met", progress.PendingReason);
        Assert.Equal("Selection & HR Review", rows[3].CandidateStageName);
    }

    [HiringProgressReadOnlyDatabaseFact]
    public async Task Progress_query_rejects_cross_client_position_identifiers()
    {
        var connection = Environment.GetEnvironmentVariable("HRMS_HIRING_PROGRESS_READONLY_CONNECTION") ?? throw new InvalidOperationException("Explicit read-only test connection required.");
        await using var db = new MySqlConnection(connection);
        await db.OpenAsync();
        // Read-only verification against an existing position: no fixture writes or schema changes.
        var position = await db.QueryFirstAsync<(long Id, int ClientId)>(@"SELECT p.Id,p.ClientId FROM recruitment_open_positions p
WHERE EXISTS(SELECT 1 FROM recruitment_candidate_applications a WHERE a.PositionId=p.Id AND a.ApplicationType='Application') ORDER BY p.Id LIMIT 1");
        var own = await RecruitmentHiringProgress.ReadCandidatesAsync(db, [position.Id], new AuthUser { Id = 1, ClientId = position.ClientId, RecruitmentScopeMode = "Client" });
        Assert.NotEmpty(own);
        Assert.All(own, candidate => Assert.Equal(position.ClientId, candidate.ClientId));
        var other = new AuthUser { Id = 2, ClientId = position.ClientId == 1 ? 2 : 1, RecruitmentScopeMode = "Client" };
        Assert.Empty(await RecruitmentHiringProgress.ReadCandidatesAsync(db, [position.Id], other));
        Assert.Empty(await RecruitmentHiringProgress.ReadCandidatesAsync(db, [], other));
    }
}

public sealed class HiringProgressReadOnlyDatabaseFactAttribute : FactAttribute
{
    public HiringProgressReadOnlyDatabaseFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HRMS_HIRING_PROGRESS_READONLY_CONNECTION")))
            Skip = "Set HRMS_HIRING_PROGRESS_READONLY_CONNECTION to verify the read query against existing records.";
    }
}
