using Dapper;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Json;
using Payroll.API.Tests.Services;

namespace Payroll.API.Tests.Repositories;

// Explicit opt-in, loopback-only, disposable test database. No production fixture access.
public sealed class InternalInterviewRepositoryTests
{
    [LocalInterviewDatabaseFact]
    public async Task Migration_and_real_repository_flow_preserve_scope_consent_idempotency_and_human_decision()
    {
        var supplied = new MySqlConnectionStringBuilder(Environment.GetEnvironmentVariable("HRMS_INTERNAL_INTERVIEW_TEST_CONNECTION") ?? throw new InvalidOperationException("An explicit loopback test connection is required."));
        Assert.Contains(supplied.Server, new[] { "localhost", "127.0.0.1", "::1" });
        var testDatabase = "hrms_interview_test_" + Guid.NewGuid().ToString("N");
        supplied.Database = "";
        await using var root = new MySqlConnection(supplied.ConnectionString); await root.OpenAsync();
        await root.ExecuteAsync($"CREATE DATABASE `{testDatabase}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci");
        supplied.Database = testDatabase;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = supplied.ConnectionString, ["InternalInterviews:Enabled"] = "true" }).Build();
        var repository = new InternalInterviewRepository(config, TimeProvider.System);
        try
        {
            await repository.InitializeAsync(); await repository.InitializeAsync(); // repeatability
            await using var db = new MySqlConnection(supplied.ConnectionString); await db.OpenAsync();
            // DATETIME defaults follow the DB session timezone. Evidence/deadlines must not.
            await db.ExecuteAsync("SET time_zone='+05:30'");
            await using (var clockTransaction = await db.BeginTransactionAsync())
            {
                var clockEvent = await InternalInterviewRepository.AppendAsync(db, clockTransaction, 999, Guid.NewGuid().ToString(), "note", "server", "server", "Synthetic clock check");
                var savedUtc = await db.ExecuteScalarAsync<DateTime>("SELECT CreatedAtUtc FROM recruitment_internal_interview_events WHERE Id=@Id", new { Id = clockEvent }, clockTransaction);
                Assert.InRange(Math.Abs((DateTime.UtcNow - savedUtc).TotalSeconds), 0, 5);
                await clockTransaction.RollbackAsync();
            }
            await db.ExecuteAsync("SET time_zone='+00:00'");
            await db.ExecuteAsync("""
                CREATE TABLE recruitment_candidates (Id BIGINT PRIMARY KEY,FirstName VARCHAR(100),LastName VARCHAR(100));
                CREATE TABLE recruitment_open_positions (Id BIGINT PRIMARY KEY,ClientId INT,PositionTitle VARCHAR(150),JobLocation VARCHAR(150));
                CREATE TABLE recruitment_candidate_applications (Id BIGINT PRIMARY KEY,CandidateId BIGINT,ClientId INT,PositionId BIGINT);
                CREATE TABLE recruitment_interviews (Id BIGINT PRIMARY KEY,ApplicationId BIGINT,RoundCode VARCHAR(50),Status VARCHAR(25),Result VARCHAR(25),TimeZoneId VARCHAR(50),ScheduledStart DATETIME,ScheduledEnd DATETIME,Mode VARCHAR(30) DEFAULT 'Virtual',LocationOrLink VARCHAR(500) DEFAULT 'Internal HRMS interview');
                CREATE TABLE recruitment_interview_panel_members (InterviewId BIGINT,PanelUserId INT);
                CREATE TABLE worklocations (Id INT PRIMARY KEY,ClientId INT,Name VARCHAR(150),City VARCHAR(100),State VARCHAR(100),IsActive BOOLEAN);
                INSERT INTO recruitment_candidates VALUES (1,'Synthetic','Candidate');
                INSERT INTO recruitment_open_positions VALUES (10,20,'Test Engineer','Test Campus'),(11,21,'Other Client Engineer','Other Campus');
                INSERT INTO recruitment_candidate_applications VALUES (100,1,20,10),(101,1,21,11);
                INSERT INTO recruitment_interviews (Id,ApplicationId,RoundCode,Status,Result,TimeZoneId,ScheduledStart,ScheduledEnd) VALUES (7,100,'Technical','Scheduled','Pending','UTC',UTC_TIMESTAMP()-INTERVAL 5 MINUTE,UTC_TIMESTAMP()+INTERVAL 55 MINUTE),(8,101,'Technical','Scheduled','Pending','UTC',UTC_TIMESTAMP()-INTERVAL 5 MINUTE,UTC_TIMESTAMP()+INTERVAL 55 MINUTE);
                INSERT INTO recruitment_interview_panel_members VALUES (7,4),(8,6);
                INSERT INTO worklocations VALUES (1,20,'Test Campus','','',TRUE),(2,20,'Excluded Campus','','',TRUE);
                """);
            var scheduler = new AuthUser { Id = 3, ClientId = 20, IsActive = true, Permissions = ["recruitment.interview.schedule"] };
            var panel = new AuthUser { Id = 4, ClientId = 20, IsActive = true, Permissions = ["recruitment.interview.panel"] };
            var stranger = new AuthUser { Id = 5, ClientId = 20, IsActive = true, Permissions = ["recruitment.interview.panel"] };
            var question = await repository.SaveQuestionAsync(new() { ClientId = 20, PositionId = 10, Skill = "Testing", Question = "Explain an integration test.", EvaluationCriteria = "Describe boundaries and evidence." }, scheduler);
            Assert.True(question.Id > 0);
            await Assert.ThrowsAsync<InternalInterviewException>(() => repository.QuestionsAsync(11, scheduler));
            await Assert.ThrowsAsync<InternalInterviewException>(() => repository.QuestionsAsync(10, panel));
            var session = await repository.ConfigureAsync(7, new() { Mode = "Hybrid", TranscriptionEnabled = true, QuestionIds = [question.Id] }, scheduler);
            Assert.Equal("Scheduled", session.Status);
            Assert.Single(session.Questions);
            question.Question = "Updated bank wording";
            await repository.SaveQuestionAsync(question, scheduler);
            Assert.Equal("Explain an integration test.", (await repository.AccessAsync(7, scheduler)).Session.Questions[0].Question);
            await Assert.ThrowsAsync<InternalInterviewException>(() => repository.AccessAsync(7, stranger));
            await Assert.ThrowsAsync<InternalInterviewException>(() => repository.AccessAsync(8, scheduler));
            var locationRestricted = new AuthUser { Id = 3, ClientId = 20, IsActive = true, Permissions = scheduler.Permissions, RecruitmentScopeMode = "SelectedLocations", RecruitmentLocationIds = [2] };
            await Assert.ThrowsAsync<InternalInterviewException>(() => repository.AccessAsync(7, locationRestricted));
            locationRestricted.RecruitmentLocationIds = [1];
            Assert.NotNull(await repository.AccessAsync(7, locationRestricted));
            Assert.Single(await repository.ScopedDashboardAsync([7, 8], locationRestricted));
            locationRestricted.RecruitmentLocationIds = [2];
            Assert.Empty(await repository.ScopedDashboardAsync([7, 8], locationRestricted));
            Assert.Empty(await repository.ScopedDashboardAsync([7, 8], stranger));
            Assert.True(await repository.HasInternalSessionAsync(7, scheduler));
            await Assert.ThrowsAsync<InternalInterviewException>(() => repository.HasInternalSessionAsync(7, locationRestricted));
            await VerifyHttpBoundaryAsync(session, scheduler, locationRestricted, db, config);

            var link = new InterviewCandidateTicket(7, session.LinkVersion, session.LinkExpiresAtUtc);
            await Assert.ThrowsAsync<InternalInterviewException>(() => repository.AccessAsync(8, null, link));
            var candidate = await repository.AccessAsync(7, null, link);
            await Assert.ThrowsAsync<InternalInterviewException>(() => repository.CommandAsync(7, new("start", session.Revision), panel));
            await repository.ConsentAsync(candidate, new(false, true, InternalInterviewPolicy.ConsentNoticeVersion));
            var waiting = (await repository.AccessAsync(7, panel)).Session;
            Assert.Equal("Waiting", waiting.Status);
            await Assert.ThrowsAsync<InternalInterviewException>(() => repository.ConfigureAsync(7, new(), scheduler));
            var live = await repository.CommandAsync(7, new("start", waiting.Revision), panel);
            Assert.Equal("Live", live.Status);
            await VerifyMediaEventsBoundaryAsync(live, config, db);
            await Assert.ThrowsAsync<InternalInterviewException>(() => repository.CommandAsync(7, new("human", waiting.Revision), panel));
            var panelAccess = await repository.AccessAsync(7, panel);
            var asked = await repository.AddEventAsync(panelAccess, new(Guid.NewGuid().ToString(), "question", "Describe integration testing."));
            candidate = await repository.AccessAsync(7, null, link);
            var answerRequest = new InterviewEventInput(Guid.NewGuid().ToString(), "answer", "I verify component boundaries.", asked.Id);
            var answer = await repository.AddEventAsync(candidate, answerRequest);
            var repeated = await repository.AddEventAsync(candidate, answerRequest);
            Assert.Equal(answer.Id, repeated.Id);
            await Assert.ThrowsAsync<InternalInterviewException>(() => repository.AddEventAsync(candidate, answerRequest with { Text = "different" }));
            await repository.AddEventAsync(panelAccess, new(Guid.NewGuid().ToString(), "note", "Private panel note."));
            await repository.AddEventAsync(candidate, new(Guid.NewGuid().ToString(), "blur", ""));
            var candidateTimeline = await repository.EventsAsync(candidate);
            Assert.DoesNotContain(candidateTimeline, e => e.Kind is "note" or "blur" or "configured" or "media-presence");
            Assert.Contains(await repository.EventsAsync(panelAccess), e => e.Kind == "blur" && e.Source == "candidate-reported");
            await repository.CommandAsync(7, new("complete", live.Revision), panel);
            Assert.Equal("Pending", await db.ExecuteScalarAsync<string>("SELECT Result FROM recruitment_interviews WHERE Id=7"));

            // Turning Frevo video off must route invitations externally and permanently revoke the old link.
            await db.ExecuteAsync("INSERT INTO recruitment_interviews (Id,ApplicationId,RoundCode,Status,Result,TimeZoneId,ScheduledStart,ScheduledEnd) SELECT 16,ApplicationId,RoundCode,'Scheduled','Pending',TimeZoneId,ScheduledStart,ScheduledEnd FROM recruitment_interviews WHERE Id=7");
            var optionalSession = await repository.ConfigureAsync(16, new(), scheduler);
            var optionalTicket = new InterviewCandidateTicket(16, optionalSession.LinkVersion, optionalSession.LinkExpiresAtUtc);
            Assert.True(await repository.HasInternalSessionAsync(16, scheduler));
            await db.ExecuteAsync("UPDATE recruitment_interviews SET LocationOrLink='https://meet.google.com/synthetic-test' WHERE Id=16");
            Assert.False(await repository.HasInternalSessionAsync(16, scheduler));
            await repository.DisableForExternalDeliveryAsync(16, scheduler);
            Assert.Equal("Cancelled", (await repository.AccessAsync(16, scheduler)).Session.Status);
            Assert.Equal(410, (await Assert.ThrowsAsync<InternalInterviewException>(() => repository.AccessAsync(16, null, optionalTicket))).StatusCode);
            Assert.Equal("https://meet.google.com/synthetic-test", (await repository.GetContextAsync(16, scheduler)).LocationOrLink);
            var restarted = await repository.ConfigureAsync(16, new(), scheduler);
            Assert.NotEqual(optionalTicket.LinkVersion, restarted.LinkVersion);
            Assert.Null(restarted.ConsentAtUtc);
            Assert.Equal("Scheduled", restarted.Status);
            await Assert.ThrowsAsync<InternalInterviewException>(() => repository.AccessAsync(16, null, optionalTicket));
            Assert.Contains(await repository.EventsAsync(await repository.AccessAsync(16, scheduler)), e => e.Kind == "external-delivery-selected");
            // Finish this fixture so it does not join the later lifecycle queue-order scenario.
            await repository.CommandAsync(16, new("cancel", restarted.Revision), scheduler);
            Assert.Equal("Scheduled", await db.ExecuteScalarAsync<string>("SELECT Status FROM recruitment_interviews WHERE Id=7"));
            var ended = await repository.AccessAsync(7, null, link);
            Assert.Equal("Completed", ended.Session.Status);
            await Assert.ThrowsAsync<InternalInterviewException>(() => repository.AddEventAsync(ended, answerRequest with { EventKey = Guid.NewGuid().ToString() }));

            // Actual durable AI orchestration against the same disposable database; only the model is mocked.
            await db.ExecuteAsync("INSERT INTO recruitment_interviews (Id,ApplicationId,RoundCode,Status,Result,TimeZoneId,ScheduledStart,ScheduledEnd) SELECT 9,ApplicationId,RoundCode,'Scheduled','Pending',TimeZoneId,ScheduledStart,ScheduledEnd FROM recruitment_interviews WHERE Id=7; INSERT INTO recruitment_interview_panel_members VALUES (9,4)");
            question.Question = "Explain an integration test."; question.FollowUpInstructions = "Which components did you verify?";
            await repository.SaveQuestionAsync(question, scheduler);
            var aiSession = await repository.ConfigureAsync(9, new() { Mode = "AI", TranscriptionEnabled = true, QuestionIds = [question.Id], FollowUpLimit = 1, MaxQuestions = 1 }, scheduler);
            var aiLink = new InterviewCandidateTicket(9, aiSession.LinkVersion, aiSession.LinkExpiresAtUtc);
            await repository.ConsentAsync(await repository.AccessAsync(9, null, aiLink), new(false, true, InternalInterviewPolicy.ConsentNoticeVersion));
            aiSession = await repository.CommandAsync(9, new("start", (await repository.AccessAsync(9, panel)).Session.Revision), panel);
            var model = new MockInterviewModel(); var ai = new InternalInterviewAiService(model);
            await repository.ProcessAiAsync(9, ai, CancellationToken.None); // introduction
            await repository.ProcessAiAsync(9, ai, CancellationToken.None); // bank question, no inference
            Assert.Equal(0, model.Calls);
            var aiCandidate = await repository.AccessAsync(9, null, aiLink);
            var first = (await repository.EventsAsync(aiCandidate)).Last(e => e.Kind == "question");
            Assert.Equal(question.Question, first.Text);
            await Assert.ThrowsAsync<InternalInterviewException>(() => repository.AddEventAsync(awaitAccess(), new(Guid.NewGuid().ToString(), "question", "Uncontrolled question")));
            InterviewAccess awaitAccess() => new(aiCandidate.Context, aiSession, panel);
            await repository.AddEventAsync(aiCandidate, new(Guid.NewGuid().ToString(), "answer", "I verify component boundaries.", first.Id));
            await repository.ProcessAiAsync(9, ai, CancellationToken.None); // approved follow-up selected
            var followup = (await repository.EventsAsync(aiCandidate)).Last(e => e.Kind == "question");
            Assert.Equal("Which components did you verify?", followup.Text); Assert.Equal(first.Id, followup.QuestionEventId);
            await repository.AddEventAsync(aiCandidate, new(Guid.NewGuid().ToString(), "answer", "I verify component boundaries.", followup.Id));
            await repository.ProcessAiAsync(9, ai, CancellationToken.None);
            Assert.Single(await repository.EventsAsync(aiCandidate), e => e.Kind == "questions-finished");
            await repository.CommandAsync(9, new("complete", aiSession.Revision), panel);
            await repository.ProcessAiAsync(9, ai, CancellationToken.None); await repository.ProcessAiAsync(9, ai, CancellationToken.None);
            Assert.Contains(9L, await repository.PendingAiAsync());
            await repository.ProcessAiAsync(9, ai, CancellationToken.None); // malformed combined reply is a durable failure
            Assert.DoesNotContain(9L, await repository.PendingAiAsync());
            Assert.Equal(4, model.Calls);
            await repository.ProcessAiAsync(9, ai, CancellationToken.None); // no automatic retry storm
            Assert.Equal(4, model.Calls);
            Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_internal_interview_events WHERE InterviewId=9 AND EventKey='review:summary:failed:0'"));
            await repository.RetryAiAsync(9, panel);
            Assert.Contains(9L, await repository.PendingAiAsync());
            await repository.ProcessAiAsync(9, ai, CancellationToken.None);
            await repository.ProcessAiAsync(9, ai, CancellationToken.None); // idempotent after recovery
            Assert.DoesNotContain(9L, await repository.PendingAiAsync());
            Assert.Equal(5, model.Calls);
            Assert.Equal(2, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_internal_interview_events WHERE InterviewId=9 AND Kind='ai-review'"));
            Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_internal_interview_events WHERE InterviewId=9 AND Kind='ai-summary'"));
            Assert.DoesNotContain(await repository.EventsAsync(await repository.AccessAsync(9, null, aiLink)), e => e.Kind is "ai-review" or "ai-summary");
            Assert.Equal("Pending", await db.ExecuteScalarAsync<string>("SELECT Result FROM recruitment_interviews WHERE Id=9"));

            var mediaConfig = new ConfigurationBuilder().AddConfiguration(config).AddInMemoryCollection(new Dictionary<string, string?> {
                ["InternalInterviews:LiveKitUrl"] = "ws://127.0.0.1:7880", ["InternalInterviews:LiveKitApiUrl"] = "http://127.0.0.1:7880",
                ["InternalInterviews:LiveKitApiKey"] = "synthetic", ["InternalInterviews:LiveKitApiSecret"] = "synthetic-secret-not-for-live-use-1234567890"
            }).Build();
            var mediaFactory = new MockMediaFactory();
            var transport = new InternalInterviewTransport(mediaConfig, mediaFactory, TimeProvider.System);
            var media = new InternalInterviewMediaStore(mediaConfig, null!); // recording disabled; no file operation allowed
            await repository.ReconcileMediaAsync(7, transport, media, CancellationToken.None);
            var failedClose = (await repository.AccessAsync(7, panel)).Session;
            Assert.Equal("Closing", failedClose.MediaState); Assert.NotEmpty(failedClose.MediaError);
            mediaFactory.Fail = false;
            await repository.ReconcileMediaAsync(7, transport, media, CancellationToken.None);
            Assert.Equal("Closed", (await repository.AccessAsync(7, panel)).Session.MediaState);
            await repository.ReconcileMediaAsync(7, transport, media, CancellationToken.None);
            Assert.Equal(2, mediaFactory.Calls); // confirmed closure is not resent or silently reopened

            // Hybrid section choices are from the frozen bank, never arbitrary model/panel instructions.
            await db.ExecuteAsync("INSERT INTO recruitment_interviews (Id,ApplicationId,RoundCode,Status,Result,TimeZoneId,ScheduledStart,ScheduledEnd) SELECT 10,ApplicationId,RoundCode,'Scheduled','Pending',TimeZoneId,ScheduledStart,ScheduledEnd FROM recruitment_interviews WHERE Id=7; INSERT INTO recruitment_interview_panel_members VALUES (10,4)");
            var second = await repository.SaveQuestionAsync(new() { ClientId = 20, PositionId = 10, Skill = "Security", Question = "Explain tenant isolation.", EvaluationCriteria = "Describe cross-client access denial." }, scheduler);
            var hybrid = await repository.ConfigureAsync(10, new() { Mode = "Hybrid", TranscriptionEnabled = true, QuestionIds = [question.Id, second.Id] }, scheduler);
            var hybridLink = new InterviewCandidateTicket(10, hybrid.LinkVersion, hybrid.LinkExpiresAtUtc);
            await repository.ConsentAsync(await repository.AccessAsync(10, null, hybridLink), new(false, true, InternalInterviewPolicy.ConsentNoticeVersion));
            hybrid = await repository.CommandAsync(10, new("start", (await repository.AccessAsync(10, panel)).Session.Revision), panel);
            await Assert.ThrowsAsync<InternalInterviewException>(() => repository.CommandAsync(10, new("ai", hybrid.Revision, "Unapproved topic"), panel));
            hybrid = await repository.CommandAsync(10, new("ai", hybrid.Revision, "Security"), panel);
            await repository.ProcessAiAsync(10, ai, default); await repository.ProcessAiAsync(10, ai, default);
            var securityQuestion = Assert.Single(await repository.EventsAsync(await repository.AccessAsync(10, null, hybridLink)), e => e.Kind == "question");
            Assert.Equal(second.Question, securityQuestion.Text);
            await repository.AddEventAsync(await repository.AccessAsync(10, null, hybridLink), new(Guid.NewGuid().ToString(), "answer", "I deny cross-client data access.", securityQuestion.Id));
            await repository.ProcessAiAsync(10, ai, default);
            Assert.Single(await repository.EventsAsync(await repository.AccessAsync(10, panel)), e => e.Kind == "questions-finished");
            hybrid = await repository.CommandAsync(10, new("human", hybrid.Revision), panel);
            hybrid = await repository.CommandAsync(10, new("ai", hybrid.Revision, "Testing"), panel);
            await repository.ProcessAiAsync(10, ai, default);
            Assert.Contains(await repository.EventsAsync(await repository.AccessAsync(10, panel)), e => e.Kind == "question" && e.Text == question.Question);
            // The closure lane wins over ordinary maintenance and healthy sessions rotate out of the first batch.
            await repository.CommandAsync(10, new("complete", hybrid.Revision), panel);
            await db.ExecuteAsync("INSERT INTO recruitment_interviews (Id,ApplicationId,RoundCode,Status,Result,TimeZoneId,ScheduledStart,ScheduledEnd) SELECT 11,ApplicationId,RoundCode,'Scheduled','Pending',TimeZoneId,ScheduledStart,ScheduledEnd FROM recruitment_interviews WHERE Id=7");
            await repository.ConfigureAsync(11, new(), scheduler);
            await db.ExecuteAsync("UPDATE recruitment_internal_interview_sessions SET UpdatedAtUtc='2000-01-01' WHERE InterviewId=11");
            Assert.Equal(new long[] { 9, 10, 11 }, (await repository.PendingLifecycleAsync()).ToArray());

            var beforeReschedule = (await repository.AccessAsync(11, scheduler)).Session;
            var oldTicket = new InterviewCandidateTicket(11, beforeReschedule.LinkVersion, beforeReschedule.LinkExpiresAtUtc);
            await repository.ConsentAsync(await repository.AccessAsync(11, null, oldTicket), new(false, false, InternalInterviewPolicy.ConsentNoticeVersion));
            await db.ExecuteAsync("UPDATE recruitment_interviews SET ScheduledStart=ScheduledStart+INTERVAL 10 MINUTE,ScheduledEnd=ScheduledEnd+INTERVAL 10 MINUTE,Status='Rescheduled' WHERE Id=11");
            await repository.ReconcileMediaAsync(11, transport, media, default);
            var resetAccess = await repository.AccessAsync(11, scheduler);
            Assert.Equal("Cancelled", resetAccess.Session.Status);
            Assert.True(InternalInterviewPolicy.CanResetUnstartedSchedule(resetAccess.Context, resetAccess.Session, DateTime.UtcNow));
            await Assert.ThrowsAsync<InternalInterviewException>(() => repository.ResetUnstartedScheduleAsync(11, beforeReschedule.Revision, scheduler));
            await Assert.ThrowsAsync<InternalInterviewException>(() => repository.ResetUnstartedScheduleAsync(11, resetAccess.Session.Revision, panel));
            await repository.ResetUnstartedScheduleAsync(11, resetAccess.Session.Revision, scheduler);
            var rescheduled = (await repository.AccessAsync(11, scheduler)).Session;
            Assert.Equal("Scheduled", rescheduled.Status); Assert.Null(rescheduled.ConsentAtUtc); Assert.Null(rescheduled.EndedAtUtc);
            Assert.NotEqual(beforeReschedule.RoomId, rescheduled.RoomId); Assert.NotEqual(beforeReschedule.LinkVersion, rescheduled.LinkVersion);
            await Assert.ThrowsAsync<InternalInterviewException>(() => repository.AccessAsync(11, null, oldTicket));
            Assert.Contains(await repository.EventsAsync(await repository.AccessAsync(11, scheduler)), e => e.Kind == "consent");
            Assert.Contains(await repository.EventsAsync(await repository.AccessAsync(11, scheduler)), e => e.Kind == "schedule-reset");
            var completedSession = (await repository.AccessAsync(10, scheduler)).Session;
            await Assert.ThrowsAsync<InternalInterviewException>(() => repository.ResetUnstartedScheduleAsync(10, completedSession.Revision, scheduler));
            Assert.Equal("Pending", await db.ExecuteScalarAsync<string>("SELECT Result FROM recruitment_interviews WHERE Id=11"));

            await db.ExecuteAsync("INSERT INTO recruitment_interviews (Id,ApplicationId,RoundCode,Status,Result,TimeZoneId,ScheduledStart,ScheduledEnd) SELECT 12,ApplicationId,RoundCode,'Scheduled','Pending',TimeZoneId,ScheduledStart,ScheduledEnd FROM recruitment_interviews WHERE Id=7; INSERT INTO recruitment_interview_panel_members VALUES (12,4)");
            var membership = await repository.ConfigureAsync(12, new() { RecordingEnabled = true }, scheduler);
            var membershipLink = new InterviewCandidateTicket(12, membership.LinkVersion, membership.LinkExpiresAtUtc);
            await repository.ConsentAsync(await repository.AccessAsync(12, null, membershipLink), new(true, false, InternalInterviewPolicy.ConsentNoticeVersion));
            await repository.CommandAsync(12, new("start", (await repository.AccessAsync(12, scheduler)).Session.Revision), scheduler);
            await db.ExecuteAsync("UPDATE recruitment_internal_interview_sessions SET MediaState='Ready' WHERE InterviewId=12");
            var participantFactory = new MockParticipantFactory();
            var participantTransport = new InternalInterviewTransport(mediaConfig, participantFactory, TimeProvider.System);
            var users = new Dictionary<int, AuthUser> { [3] = scheduler, [4] = panel, [5] = stranger,
                [6] = new() { Id = 6, ClientId = 20, IsActive = false, Permissions = scheduler.Permissions },
                [7] = new() { Id = 7, ClientId = 21, IsActive = true, Permissions = scheduler.Permissions },
                [8] = new() { Id = 8, ClientId = 20, IsActive = true, Permissions = scheduler.Permissions, RecruitmentScopeMode = "SelectedLocations", RecruitmentLocationIds = [2] } };
            await repository.ReconcileParticipantsAsync(12, participantTransport, id => Task.FromResult(users.GetValueOrDefault(id)), default);
            Assert.Equal(new[] { "panel-5", "panel-6", "panel-7", "panel-8", "unknown" }, participantFactory.Removed.Order().ToArray());
            Assert.Equal(5, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_internal_interview_events WHERE InterviewId=12 AND Kind='participant-access-removed'"));
            panel.IsActive = false;
            await Assert.ThrowsAsync<InternalInterviewException>(() => repository.AccessAsync(12, panel));
            await repository.ReconcileParticipantsAsync(12, participantTransport, id => Task.FromResult(users.GetValueOrDefault(id)), default);
            Assert.Contains("panel-4", participantFactory.Removed); Assert.DoesNotContain("candidate", participantFactory.Removed); Assert.DoesNotContain("recorder", participantFactory.Removed);
            panel.IsActive = true;

            // Waiting/failed live turns must not occupy the first queue page and starve completed reviews.
            await db.ExecuteAsync("INSERT INTO recruitment_interviews (Id,ApplicationId,RoundCode,Status,Result,TimeZoneId,ScheduledStart,ScheduledEnd) SELECT 14,ApplicationId,RoundCode,'Scheduled','Pending',TimeZoneId,ScheduledStart,ScheduledEnd FROM recruitment_interviews WHERE Id=7; INSERT INTO recruitment_interview_panel_members VALUES (14,4)");
            var queued = await repository.ConfigureAsync(14, new() { Mode = "AI", TranscriptionEnabled = true, QuestionIds = [question.Id], FollowUpLimit = 1 }, scheduler);
            var queuedLink = new InterviewCandidateTicket(14, queued.LinkVersion, queued.LinkExpiresAtUtc);
            await repository.ConsentAsync(await repository.AccessAsync(14, null, queuedLink), new(false, true, InternalInterviewPolicy.ConsentNoticeVersion));
            queued = await repository.CommandAsync(14, new("start", (await repository.AccessAsync(14, panel)).Session.Revision), panel);
            var unavailable = new InternalInterviewAiService(new UnavailableInterviewModel());
            Assert.Contains(14L, await repository.PendingAiAsync());
            await repository.ProcessAiAsync(14, unavailable, default); await repository.ProcessAiAsync(14, unavailable, default);
            Assert.DoesNotContain(14L, await repository.PendingAiAsync()); // no answer yet
            var queuedQuestion = (await repository.EventsAsync(await repository.AccessAsync(14, panel))).Last(e => e.Kind == "question");
            await repository.AddEventAsync(await repository.AccessAsync(14, null, queuedLink), new(Guid.NewGuid().ToString(), "answer", "I verify component boundaries.", queuedQuestion.Id));
            Assert.Contains(14L, await repository.PendingAiAsync());
            await repository.ProcessAiAsync(14, unavailable, default);
            Assert.DoesNotContain(14L, await repository.PendingAiAsync()); // durable failure, not a busy loop
            await repository.RetryAiAsync(14, panel);
            Assert.Contains(14L, await repository.PendingAiAsync());
            await repository.ProcessAiAsync(14, ai, default);
            Assert.DoesNotContain(14L, await repository.PendingAiAsync()); // follow-up is waiting for an answer
            var waitingQuestion = (await repository.EventsAsync(await repository.AccessAsync(14, panel))).Last(e => e.Kind == "question");
            var afterNormalAnswer = new InternalInterviewRepository(config, new FixedInterviewClock(new DateTimeOffset(DateTime.SpecifyKind(waitingQuestion.CreatedAtUtc.AddSeconds(150), DateTimeKind.Utc))));
            Assert.Contains(14L, await afterNormalAnswer.PendingAiAsync());
            await db.ExecuteAsync("""
                INSERT INTO recruitment_internal_interview_events (InterviewId,EventKey,Kind,Actor,Source,Text,QuestionEventId,CreatedAtUtc)
                VALUES (14,@Key,'voice-parsing','candidate','server','Synthetic voice grace',@Question,@At)
                """, new { Key = "voice:" + waitingQuestion.Id, Question = waitingQuestion.Id, At = waitingQuestion.CreatedAtUtc.AddSeconds(110) });
            Assert.DoesNotContain(14L, await afterNormalAnswer.PendingAiAsync()); // same bounded voice deadline as answer admission
            var afterVoiceAnswer = new InternalInterviewRepository(config, new FixedInterviewClock(new DateTimeOffset(DateTime.SpecifyKind(waitingQuestion.CreatedAtUtc.AddSeconds(240), DateTimeKind.Utc))));
            Assert.Contains(14L, await afterVoiceAnswer.PendingAiAsync());
            await repository.CommandAsync(14, new("complete", queued.Revision), panel);

            // Human -> AI must not repeat wording already asked under another bank ID/source.
            // Nor should it spend a model call choosing a follow-up already asked by the panel.
            await db.ExecuteAsync("INSERT INTO recruitment_interviews (Id,ApplicationId,RoundCode,Status,Result,TimeZoneId,ScheduledStart,ScheduledEnd) SELECT 15,ApplicationId,RoundCode,'Scheduled','Pending',TimeZoneId,ScheduledStart,ScheduledEnd FROM recruitment_interviews WHERE Id=7; INSERT INTO recruitment_interview_panel_members VALUES (15,4)");
            var duplicate = await repository.SaveQuestionAsync(new() { ClientId = 20, PositionId = 10, Skill = "Testing", Question = "EXPLAIN  AN INTEGRATION TEST.", EvaluationCriteria = "Same wording under another ID." }, scheduler);
            second.FollowUpInstructions = question.Question;
            second = await repository.SaveQuestionAsync(second, scheduler);
            var dedup = await repository.ConfigureAsync(15, new() { Mode = "Hybrid", TranscriptionEnabled = true, QuestionIds = [question.Id, duplicate.Id, second.Id], FollowUpLimit = 1 }, scheduler);
            var dedupLink = new InterviewCandidateTicket(15, dedup.LinkVersion, dedup.LinkExpiresAtUtc);
            await repository.ConsentAsync(await repository.AccessAsync(15, null, dedupLink), new(false, true, InternalInterviewPolicy.ConsentNoticeVersion));
            dedup = await repository.CommandAsync(15, new("start", (await repository.AccessAsync(15, panel)).Session.Revision), panel);
            var manualQuestion = await repository.AddEventAsync(await repository.AccessAsync(15, panel), new(Guid.NewGuid().ToString(), "question", "Explain\t an integration test."));
            await repository.AddEventAsync(await repository.AccessAsync(15, null, dedupLink), new(Guid.NewGuid().ToString(), "answer", "I verify component boundaries.", manualQuestion.Id));
            dedup = await repository.CommandAsync(15, new("ai", dedup.Revision), panel);
            await repository.ProcessAiAsync(15, unavailable, default); await repository.ProcessAiAsync(15, unavailable, default);
            var nextQuestion = (await repository.EventsAsync(await repository.AccessAsync(15, panel))).Last(e => e.Kind == "question");
            Assert.Equal(second.Question, nextQuestion.Text);
            await repository.AddEventAsync(await repository.AccessAsync(15, null, dedupLink), new(Guid.NewGuid().ToString(), "answer", "I deny cross-client access.", nextQuestion.Id));
            await repository.ProcessAiAsync(15, unavailable, default);
            var dedupEvents = await repository.EventsAsync(await repository.AccessAsync(15, panel));
            Assert.Equal(2, dedupEvents.Count(e => e.Kind == "question"));
            Assert.Single(dedupEvents, e => e.Kind == "questions-finished");
            Assert.DoesNotContain(dedupEvents, e => e.Kind == "ai-error");
            await repository.CommandAsync(15, new("complete", dedup.Revision), panel);

            // Drain pauses admission only. Existing live evidence and human completion remain usable.
            config["InternalInterviews:DrainMode"] = "true";
            Assert.Equal(503, (await Assert.ThrowsAsync<InternalInterviewException>(() => repository.ConfigureAsync(11, new(), scheduler))).StatusCode);
            Assert.Equal(503, (await Assert.ThrowsAsync<InternalInterviewException>(() => repository.RotateLinkAsync(11, scheduler))).StatusCode);
            Assert.Equal(503, (await Assert.ThrowsAsync<InternalInterviewException>(() => repository.ResetUnstartedScheduleAsync(11, rescheduled.Revision, scheduler))).StatusCode);
            var drainingCandidate = new InterviewAccess(await repository.GetContextAsync(11, scheduler), rescheduled, null);
            Assert.Equal(503, (await Assert.ThrowsAsync<InternalInterviewException>(() => repository.ConsentAsync(
                drainingCandidate, new(false, false, InternalInterviewPolicy.ConsentNoticeVersion)))).StatusCode);
            Assert.Equal(503, (await Assert.ThrowsAsync<InternalInterviewException>(() => repository.CommandAsync(11, new("start", rescheduled.Revision), scheduler))).StatusCode);
            var drainingLive = await repository.AccessAsync(12, panel);
            await repository.AddEventAsync(drainingLive, new(Guid.NewGuid().ToString(), "note", "Human evidence can still be saved while draining."));
            await repository.CommandAsync(12, new("complete", drainingLive.Session.Revision), panel);
            await repository.ReconcileMediaAsync(12, transport, media, default);
            Assert.Equal("Closed", (await repository.AccessAsync(12, scheduler)).Session.MediaState);
            Assert.Equal("Pending", await db.ExecuteScalarAsync<string>("SELECT Result FROM recruitment_interviews WHERE Id=12"));
            var afterWindow = new InternalInterviewRepository(config, new FixedInterviewClock(DateTimeOffset.UtcNow.AddHours(4)));
            await afterWindow.ReconcileMediaAsync(11, transport, media, default);
            var expiredDuringDrain = await repository.AccessAsync(11, scheduler);
            Assert.Equal("Cancelled", expiredDuringDrain.Session.Status);
            Assert.Contains(await repository.EventsAsync(expiredDuringDrain), e => e.Text.Contains("not marked candidate No Show"));

            config["InternalInterviews:DrainMode"] = "false";
            await db.ExecuteAsync("INSERT INTO recruitment_interviews (Id,ApplicationId,RoundCode,Status,Result,TimeZoneId,ScheduledStart,ScheduledEnd) SELECT 13,ApplicationId,RoundCode,'Scheduled','Pending',TimeZoneId,ScheduledStart,ScheduledEnd FROM recruitment_interviews WHERE Id=7");
            var shutdown = await repository.ConfigureAsync(13, new(), scheduler);
            await repository.ConsentAsync(new(await repository.GetContextAsync(13, scheduler), shutdown, null), new(false, false, InternalInterviewPolicy.ConsentNoticeVersion));
            await repository.CommandAsync(13, new("start", (await repository.AccessAsync(13, scheduler)).Session.Revision), scheduler);
            config["InternalInterviews:Enabled"] = "false";
            var callsBeforeShutdown = mediaFactory.Calls;
            await repository.ReconcileMediaAsync(13, transport, media, default);
            Assert.Equal(callsBeforeShutdown, mediaFactory.Calls); // no implicit work on disabled installations
            Assert.Equal("Starting", (await repository.AccessAsync(13, scheduler)).Session.MediaState);
            config["InternalInterviews:MaintenanceEnabled"] = "true";
            await repository.ReconcileMediaAsync(13, transport, media, default);
            var shut = await repository.AccessAsync(13, scheduler);
            Assert.Equal("Cancelled", shut.Session.Status); Assert.Equal("Closed", shut.Session.MediaState);
            Assert.Equal(callsBeforeShutdown + 1, mediaFactory.Calls); // mock accepts DeleteRoom only, never CreateRoom
            Assert.Contains(await repository.EventsAsync(shut), e => e.Kind == "session-closed-by-server" && e.Text.Contains("disabled by deployment settings"));
            Assert.Equal("Pending", await db.ExecuteScalarAsync<string>("SELECT Result FROM recruitment_interviews WHERE Id=13"));
            await db.ExecuteAsync("UPDATE recruitment_internal_interview_sessions SET RetainUntilUtc=UTC_TIMESTAMP()-INTERVAL 1 DAY WHERE InterviewId=13");
            await repository.ReconcileMediaAsync(13, transport, media, default);
            Assert.Equal("Purged", await db.ExecuteScalarAsync<string>("SELECT MediaState FROM recruitment_internal_interview_sessions WHERE InterviewId=13"));
            Assert.Equal("retention-purged", await db.ExecuteScalarAsync<string>("SELECT Kind FROM recruitment_internal_interview_events WHERE InterviewId=13"));
            Assert.Equal(callsBeforeShutdown + 1, mediaFactory.Calls);
        }
        finally
        {
            // Only this test-created GUID database is removable; never the configured source database.
            Assert.StartsWith("hrms_interview_test_", testDatabase);
            MySqlConnection.ClearAllPools();
            await root.ExecuteAsync($"DROP DATABASE `{testDatabase}`");
        }
    }

    private static async Task VerifyMediaEventsBoundaryAsync(InternalInterviewSession session, IConfiguration configuration, MySqlConnection db)
    {
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(configuration.AsEnumerable()).AddInMemoryCollection(new Dictionary<string, string?> {
            ["InternalInterviews:LiveKitWebhooksEnabled"] = "true", ["InternalInterviews:LiveKitApiKey"] = InternalInterviewMediaWebhookTests.Key,
            ["InternalInterviews:LiveKitApiSecret"] = InternalInterviewMediaWebhookTests.Secret });
        builder.Services.AddSingleton(new InternalInterviewRepository(builder.Configuration, TimeProvider.System));
        await using var app = builder.Build(); app.MapInternalInterviewMediaEvents(); await app.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            async Task<HttpStatusCode> Send(byte[] body, string? token)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "/api/public/internal-interviews/media-events");
                request.Content = new ByteArrayContent(body); request.Content.Headers.ContentType = new("application/webhook+json");
                if (token is not null) request.Headers.TryAddWithoutValidation("Authorization", token);
                using var response = await http.SendAsync(request); Assert.True(response.Headers.CacheControl?.NoStore); return response.StatusCode;
            }
            var now = DateTimeOffset.UtcNow;
            var joined = InternalInterviewMediaWebhookTests.Body(session.RoomId, "EV_joined", "candidate", "participant_joined", now);
            Assert.Equal(HttpStatusCode.Unauthorized, await Send(joined, null));
            Assert.Equal(HttpStatusCode.Unauthorized, await Send(joined, InternalInterviewMediaWebhookTests.Sign(joined, now, hash: false)));
            Assert.Equal(HttpStatusCode.Unauthorized, await Send([..joined, 32], InternalInterviewMediaWebhookTests.Sign(joined, now)));
            var replies = await Task.WhenAll(Send(joined, InternalInterviewMediaWebhookTests.Sign(joined, now)), Send(joined, InternalInterviewMediaWebhookTests.Sign(joined, now)));
            Assert.All(replies, status => Assert.Equal(HttpStatusCode.NoContent, status));
            Assert.Equal(1, await Count());
            var changed = InternalInterviewMediaWebhookTests.Body(session.RoomId, "EV_joined", "panel-4", "participant_joined", now);
            Assert.Equal(HttpStatusCode.Conflict, await Send(changed, InternalInterviewMediaWebhookTests.Sign(changed, now)));
            var left = InternalInterviewMediaWebhookTests.Body(session.RoomId, "EV_left", "candidate", "participant_left", now);
            Assert.Equal(HttpStatusCode.NoContent, await Send(left, InternalInterviewMediaWebhookTests.Sign(left, now)));
            Assert.Equal(2, await Count());
            var body = await db.ExecuteScalarAsync<string>("SELECT Text FROM recruitment_internal_interview_events WHERE InterviewId=@Id AND Kind='media-presence' ORDER BY Id LIMIT 1", new { Id = session.InterviewId });
            Assert.DoesNotContain("must-not-be-stored", body); Assert.Contains("occurredAtUtc", body);
            var other = InternalInterviewMediaWebhookTests.Body(Guid.NewGuid().ToString("N"), "EV_unknown", "candidate", "participant_joined", now);
            Assert.Equal(HttpStatusCode.NoContent, await Send(other, InternalInterviewMediaWebhookTests.Sign(other, now)));
            Assert.Equal(2, await Count());
            var late = InternalInterviewMediaWebhookTests.Body(session.RoomId, "EV_late", "panel-4", "participant_joined", now);
            await db.ExecuteAsync("UPDATE recruitment_internal_interview_sessions SET RetainUntilUtc=NULL WHERE InterviewId=@Id", new { Id = session.InterviewId });
            Assert.Equal(HttpStatusCode.NoContent, await Send(late, InternalInterviewMediaWebhookTests.Sign(late, now)));
            Assert.Equal(2, await Count());
            await db.ExecuteAsync("UPDATE recruitment_internal_interview_sessions SET RetainUntilUtc=UTC_TIMESTAMP()-INTERVAL 1 DAY WHERE InterviewId=@Id", new { Id = session.InterviewId });
            Assert.Equal(HttpStatusCode.NoContent, await Send(late, InternalInterviewMediaWebhookTests.Sign(late, now)));
            Assert.Equal(2, await Count());
            await db.ExecuteAsync("UPDATE recruitment_internal_interview_sessions SET RetainUntilUtc=@RetainUntilUtc,MediaState='Purged' WHERE InterviewId=@InterviewId", session);
            Assert.Equal(HttpStatusCode.NoContent, await Send(late, InternalInterviewMediaWebhookTests.Sign(late, now)));
            Assert.Equal(2, await Count());
            await db.ExecuteAsync("UPDATE recruitment_internal_interview_sessions SET MediaState=@MediaState,ConsentAtUtc=NULL WHERE InterviewId=@InterviewId", session);
            Assert.Equal(HttpStatusCode.NoContent, await Send(late, InternalInterviewMediaWebhookTests.Sign(late, now)));
            Assert.Equal(2, await Count());
            await db.ExecuteAsync("UPDATE recruitment_internal_interview_sessions SET ConsentAtUtc=@ConsentAtUtc WHERE InterviewId=@InterviewId", session);
            builder.Configuration["InternalInterviews:Enabled"] = "false";
            Assert.Equal(HttpStatusCode.NotFound, await Send(late, InternalInterviewMediaWebhookTests.Sign(late, now)));
            builder.Configuration["InternalInterviews:MaintenanceEnabled"] = "true";
            Assert.Equal(HttpStatusCode.NoContent, await Send(late, InternalInterviewMediaWebhookTests.Sign(late, now)));
            Assert.Equal(3, await Count());
            builder.Configuration["InternalInterviews:LiveKitWebhooksEnabled"] = "false";
            Assert.Equal(HttpStatusCode.ServiceUnavailable, await Send(late, InternalInterviewMediaWebhookTests.Sign(late, now)));
            Assert.Equal("Pending", await db.ExecuteScalarAsync<string>("SELECT Result FROM recruitment_interviews WHERE Id=@Id", new { Id = session.InterviewId }));
            Task<int> Count() => db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_internal_interview_events WHERE InterviewId=@Id AND Kind='media-presence'", new { Id = session.InterviewId });
        }
        finally { await app.StopAsync(); }
    }

    private static async Task VerifyHttpBoundaryAsync(InternalInterviewSession session, AuthUser user, AuthUser denied, MySqlConnection db, IConfiguration configuration)
    {
        var mediaRoot = Directory.CreateTempSubdirectory("hrms-interview-range-test-");
        var mediaPath = Path.Combine(mediaRoot.FullName, session.RoomId + ".mp4");
        var mediaBytes = Enumerable.Range(0, 2048).Select(i => (byte)(i % 256)).ToArray();
        await File.WriteAllBytesAsync(mediaPath, mediaBytes); // synthetic byte-range fixture, not a playable/real Egress recording
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(configuration.AsEnumerable()); // isolate mutable flag tests from the outer fixture
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["InternalInterviews:Enabled"] = "true", ["InternalInterviews:PublicPortalBaseUrl"] = "http://127.0.0.1:5184",
            ["InternalInterviews:RecordingDirectory"] = mediaRoot.FullName, ["AttachmentStorage:DataRootPath"] = Path.Combine(mediaRoot.FullName, "different-api-data-root") });
        var links = new InternalInterviewLinks(new EphemeralDataProtectionProvider(), builder.Configuration, TimeProvider.System);
        builder.Services.AddSingleton(new InternalInterviewRepository(builder.Configuration, TimeProvider.System)); builder.Services.AddSingleton(links);
        // Transport is present for endpoint binding, but any accidental call goes to a strict mock.
        builder.Services.AddSingleton(new InternalInterviewTransport(builder.Configuration, new MockMediaFactory(), TimeProvider.System));
        var storage = new AttachmentStorageService(null!, builder.Environment, builder.Configuration, null!);
        builder.Services.AddSingleton(new InternalInterviewMediaStore(builder.Configuration, storage));
        builder.Services.AddSingleton<InternalInterviewSpeechService>(_ => null!); builder.Services.AddSingleton<RecruitmentTalentRepository>(_ => null!);
        await using var app = builder.Build();
        app.Use(async (context, next) => { if (context.Request.Headers["X-Test-Actor"] == "allowed") context.Items["User"] = user;
            if (context.Request.Headers["X-Test-Actor"] == "denied") context.Items["User"] = denied; await next(context); });
        app.MapInternalInterviews(); await app.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            using var anonymous = await http.GetAsync("/api/recruitment/internal-interviews/7"); Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            http.DefaultRequestHeaders.Add("X-Test-Actor", "denied");
            using var wrongLocation = await http.GetAsync("/api/recruitment/internal-interviews/7"); Assert.Equal(HttpStatusCode.NotFound, wrongLocation.StatusCode);
            http.DefaultRequestHeaders.Remove("X-Test-Actor"); http.DefaultRequestHeaders.Add("X-Test-Actor", "allowed");
            using var admin = await http.GetAsync("/api/recruitment/internal-interviews/7"); Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
            Assert.True(admin.Headers.CacheControl?.NoStore);
            await db.ExecuteAsync("""
                INSERT INTO recruitment_internal_interview_recordings(Id,InterviewId,Status,StorageKey,ContentType,SizeBytes,StartedAtUtc,CreatedByUserId)
                VALUES (@Id,7,'Ready',@Key,'video/mp4',2048,UTC_TIMESTAMP(),@UserId)
                """, new { Id = session.RoomId, Key = session.RoomId + ".mp4", UserId = user.Id });
            using var range = new HttpRequestMessage(HttpMethod.Get, $"/api/recruitment/internal-interviews/7/recordings/{session.RoomId}");
            range.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(8, 15);
            using var replay = await http.SendAsync(range);
            Assert.Equal(HttpStatusCode.PartialContent, replay.StatusCode); Assert.Equal("bytes 8-15/2048", replay.Content.Headers.ContentRange?.ToString());
            Assert.Equal(mediaBytes[8..16], await replay.Content.ReadAsByteArrayAsync()); Assert.True(replay.Headers.CacheControl?.NoStore);
            using var invalidRange = new HttpRequestMessage(HttpMethod.Get, $"/api/recruitment/internal-interviews/7/recordings/{session.RoomId}");
            invalidRange.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(5000, null);
            using var unsatisfiable = await http.SendAsync(invalidRange); Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, unsatisfiable.StatusCode);
            http.DefaultRequestHeaders.Remove("X-Test-Actor");
            using var missing = await http.GetAsync("/api/public/internal-interviews/7"); Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
            var token = Uri.UnescapeDataString(new Uri(links.Create(session).Url).Fragment.Split('=', 2)[1]);
            http.DefaultRequestHeaders.Add("X-Interview-Token", token);
            using var candidate = await http.GetAsync("/api/public/internal-interviews/7"); Assert.Equal(HttpStatusCode.OK, candidate.StatusCode);
            var json = await candidate.Content.ReadAsStringAsync(); Assert.DoesNotContain("EvaluationCriteria", json, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain(session.LinkVersion, json);
            using var wrongInterview = await http.GetAsync("/api/public/internal-interviews/8"); Assert.Equal(HttpStatusCode.Unauthorized, wrongInterview.StatusCode);
            using var badConsent = await http.PostAsJsonAsync("/api/public/internal-interviews/7/consent", new { recording = false, transcription = true, noticeVersion = "wrong" }); Assert.Equal(HttpStatusCode.BadRequest, badConsent.StatusCode);
            using var noRecording = await http.GetAsync("/api/recruitment/internal-interviews/7/recordings"); Assert.Equal(HttpStatusCode.Unauthorized, noRecording.StatusCode);
            builder.Configuration["InternalInterviews:DrainMode"] = "true";
            using var drainView = await http.GetAsync("/api/public/internal-interviews/7"); Assert.Equal(HttpStatusCode.OK, drainView.StatusCode);
            Assert.Contains("\"draining\":true", await drainView.Content.ReadAsStringAsync());
            using var drainConsent = await http.PostAsJsonAsync("/api/public/internal-interviews/7/consent", new InterviewConsent(false, true, InternalInterviewPolicy.ConsentNoticeVersion));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, drainConsent.StatusCode);
            http.DefaultRequestHeaders.Add("X-Test-Actor", "allowed");
            using var drainLink = await http.PostAsJsonAsync("/api/recruitment/internal-interviews/7/link", new { }); Assert.Equal(HttpStatusCode.ServiceUnavailable, drainLink.StatusCode);
            using var drainStart = await http.PostAsJsonAsync("/api/recruitment/internal-interviews/7/command", new InterviewCommand("start", session.Revision)); Assert.Equal(HttpStatusCode.ServiceUnavailable, drainStart.StatusCode);
            using var drainReplay = await http.GetAsync("/api/recruitment/internal-interviews/7/recordings"); Assert.Equal(HttpStatusCode.OK, drainReplay.StatusCode);
            builder.Configuration["InternalInterviews:DrainMode"] = "false";
            builder.Configuration["InternalInterviews:Enabled"] = "false";
            using var off = await http.GetAsync("/api/public/internal-interviews/7"); Assert.Equal(HttpStatusCode.ServiceUnavailable, off.StatusCode);
        }
        finally
        {
            await app.StopAsync();
            await db.ExecuteAsync("DELETE FROM recruitment_internal_interview_recordings WHERE Id=@Id AND InterviewId=7", new { Id = session.RoomId });
            File.Delete(mediaPath); mediaRoot.Delete(); // exactly this test-owned file and its empty temp folder
        }
    }

    private sealed class FixedInterviewClock(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
    }

    private sealed class UnavailableInterviewModel : IInternalInterviewLanguageModel
    {
        public Task<System.Text.Json.JsonElement> GenerateAsync(string prompt, string instruction, CancellationToken ct)
            => throw new InternalInterviewException(503, "Synthetic local model unavailable.");
    }
    private sealed class MockInterviewModel : IInternalInterviewLanguageModel
    {
        public int Calls { get; private set; }
        private int summaries;
        public Task<System.Text.Json.JsonElement> GenerateAsync(string prompt, string instruction, CancellationToken ct)
        {
            Calls++;
            using var json = System.Text.Json.JsonDocument.Parse(prompt.Contains("followUps", StringComparison.Ordinal)
                ? "{\"index\":0}" : prompt.Contains("\"answers\"", StringComparison.Ordinal)
                ? ++summaries == 1 ? "[]" : "{\"observations\":[{\"kind\":\"interviewSummary\",\"note\":\"Describes component boundaries.\",\"evidence\":[{\"answer\":0,\"quote\":\"component boundaries\"}]}]}"
                : "{\"observations\":[{\"kind\":\"skillEvidence\",\"note\":\"Describes component boundaries.\",\"evidenceQuote\":\"component boundaries\"}]}");
            return Task.FromResult(json.RootElement.Clone());
        }
    }
    private sealed class MockMediaFactory : HttpMessageHandler, IHttpClientFactory
    {
        public bool Fail { get; set; } = true;
        public int Calls { get; private set; }
        public HttpClient CreateClient(string name) => new(this, false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; Assert.EndsWith("/RoomService/DeleteRoom", request.RequestUri!.AbsolutePath.Replace("livekit.", ""));
            return Task.FromResult(new HttpResponseMessage(Fail ? System.Net.HttpStatusCode.ServiceUnavailable : System.Net.HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }

    private sealed class MockParticipantFactory : HttpMessageHandler, IHttpClientFactory
    {
        public HashSet<string> Removed { get; } = [];
        public HttpClient CreateClient(string name) => new(this, false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var payload = request.Headers.Authorization!.Parameter!.Split('.')[1].Replace('-', '+').Replace('_', '/');
            using var token = System.Text.Json.JsonDocument.Parse(Convert.FromBase64String(payload.PadRight((payload.Length + 3) / 4 * 4, '=')));
            Assert.True(token.RootElement.GetProperty("video").GetProperty("roomAdmin").GetBoolean());
            Assert.False(token.RootElement.GetProperty("video").TryGetProperty("roomCreate", out _));
            object response;
            if (request.RequestUri!.AbsolutePath.EndsWith("/ListParticipants"))
                response = new { participants = new[] { "candidate", "panel-3", "panel-4", "panel-5", "panel-6", "panel-7", "panel-8", "recorder", "unknown" }
                    .Where(i => !Removed.Contains(i)).Select(identity => new { identity, kind = identity == "recorder" ? "EGRESS" : "STANDARD" }) };
            else
            {
                Assert.EndsWith("/RemoveParticipant", request.RequestUri.AbsolutePath);
                using var body = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                Removed.Add(body.RootElement.GetProperty("identity").GetString()!); response = new { };
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(response) };
        }
    }
}

public sealed class LocalInterviewDatabaseFactAttribute : FactAttribute
{
    public LocalInterviewDatabaseFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HRMS_INTERNAL_INTERVIEW_TEST_CONNECTION")))
            Skip = "Set an explicit loopback HRMS_INTERNAL_INTERVIEW_TEST_CONNECTION to run the disposable database regression.";
    }
}
