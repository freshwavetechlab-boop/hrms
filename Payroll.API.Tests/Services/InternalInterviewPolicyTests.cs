using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class InternalInterviewPolicyTests
{
    private static readonly DateTime Now = new(2026, 9, 20, 10, 0, 0, DateTimeKind.Utc);
    private static InternalInterviewContext Context() => new()
    {
        InterviewId = 7, ClientId = 20, PanelUserIds = [4], InterviewStatus = "Scheduled", Result = "Pending",
        Mode = "Virtual", LocationOrLink = InternalInterviewPolicy.InternalDestination,
        TimeZoneId = "UTC", ScheduledStart = Now.AddMinutes(-5), ScheduledEnd = Now.AddMinutes(55)
    };
    private static InternalInterviewSession Session(string status = "Waiting", string mode = "Human") => new()
    {
        InterviewId = 7, RoomId = Guid.NewGuid().ToString("N"), LinkVersion = Guid.NewGuid().ToString("N"),
        LinkExpiresAtUtc = Now.AddHours(2), ConsentAtUtc = Now.AddMinutes(-1), Status = status,
        ConfigurationJson = JsonSerializer.Serialize(new InternalInterviewConfiguration { Mode = mode }),
        TranscriptionConsent = true
    };
    private static AuthUser User(int id, int? clientId, params string[] permissions) => new() { Id = id, ClientId = clientId, IsActive = true, Permissions = [.. permissions] };

    [Theory]
    [InlineData("Virtual", "https://meet.google.com/test-interview")]
    [InlineData("Virtual", "")]
    [InlineData("Face-to-Face", "Internal HRMS interview")]
    public void External_delivery_disables_old_internal_room_actions(string mode, string destination)
    {
        var context = Context(); context.Mode = mode; context.LocationOrLink = destination;
        Assert.False(InternalInterviewPolicy.UsesFrevoVideo(context));
        var error = Assert.Throws<InternalInterviewException>(() => InternalInterviewPolicy.RequireOpen(context, Session("Live"), Now));
        Assert.Equal(409, error.StatusCode);
        Assert.Contains("Frevo One is off", error.Message);
        foreach (var action in new[] { "start", "ai", "human" })
            Assert.Throws<InternalInterviewException>(() => InternalInterviewPolicy.Transition(context, Session("Live", "Hybrid"), action, Now));
        Assert.Equal("Cancelled", InternalInterviewPolicy.Transition(context, Session("Live"), "cancel", Now));
    }

    [Fact]
    public void Disabled_user_cannot_reuse_existing_interview_permissions()
    {
        var user = User(4, 20, "recruitment.interview.schedule", "recruitment.interview.panel"); user.IsActive = false;
        Assert.False(InternalInterviewPolicy.CanAccess(user, Context())); Assert.False(InternalInterviewPolicy.CanManage(user));
    }

    [Theory]
    [InlineData(4, 20, "recruitment.interview.panel", true)]
    [InlineData(5, 20, "recruitment.interview.panel", false)]
    [InlineData(4, 21, "recruitment.interview.panel", false)]
    [InlineData(4, 21, "recruitment.manage", false)]
    [InlineData(4, 20, "client.users.manage", false)]
    [InlineData(4, 20, "payroll.run", false)]
    [InlineData(5, 20, "recruitment.interview.schedule", true)]
    public void Access_requires_domain_permission_panel_assignment_and_same_client(int id, int client, string permission, bool expected) =>
        Assert.Equal(expected, InternalInterviewPolicy.CanAccess(User(id, client, permission), Context()));

    [Fact]
    public void Global_scheduler_can_access_without_becoming_a_candidate_or_skipping_scope_for_client_users() =>
        Assert.True(InternalInterviewPolicy.CanAccess(User(9, null, "recruitment.interview.schedule"), Context()));

    [Fact]
    public void Start_requires_consent_and_a_waiting_candidate()
    {
        Assert.Throws<InternalInterviewException>(() => InternalInterviewPolicy.Transition(Context(), Session("Scheduled"), "start", Now));
        var session = Session(); session.ConsentAtUtc = null;
        Assert.Throws<InternalInterviewException>(() => InternalInterviewPolicy.Transition(Context(), session, "start", Now));
        Assert.Equal("Live", InternalInterviewPolicy.Transition(Context(), Session(), "start", Now));
    }

    [Fact]
    public void Session_completion_never_changes_hiring_result_or_scheduled_interview()
    {
        var context = Context();
        Assert.Equal("Completed", InternalInterviewPolicy.Transition(context, Session("Live"), "complete", Now));
        Assert.Equal("Pending", context.Result); Assert.Equal("Scheduled", context.InterviewStatus);
    }

    [Theory]
    [InlineData("Completed")]
    [InlineData("Cancelled")]
    [InlineData("No Show")]
    public void Ended_sessions_cannot_restart_or_take_commands(string status) =>
        Assert.Throws<InternalInterviewException>(() => InternalInterviewPolicy.Transition(Context(), Session(status), "start", Now));

    [Fact]
    public void No_show_is_not_rejection_and_requires_elapsed_schedule()
    {
        Assert.Throws<InternalInterviewException>(() => InternalInterviewPolicy.Transition(Context(), Session(), "no-show", Now));
        Assert.Equal("No Show", InternalInterviewPolicy.Transition(Context(), Session(), "no-show", Now.AddHours(2)));
        var live = Session(); live.StartedAtUtc = Now;
        Assert.Throws<InternalInterviewException>(() => InternalInterviewPolicy.Transition(Context(), live, "no-show", Now.AddHours(2)));
    }

    [Fact]
    public void Hybrid_control_is_explicit_and_does_not_enable_ai_for_human_only()
    {
        Assert.Equal("Live", InternalInterviewPolicy.Transition(Context(), Session("Live", "Hybrid"), "ai", Now));
        Assert.Equal("Live", InternalInterviewPolicy.Transition(Context(), Session("Live", "Hybrid"), "human", Now));
        Assert.Throws<InternalInterviewException>(() => InternalInterviewPolicy.Transition(Context(), Session("Live"), "ai", Now));
    }

    [Fact]
    public void Joining_uses_interview_timezone_not_server_local_time()
    {
        var context = Context(); context.TimeZoneId = "Asia/Kolkata";
        context.ScheduledStart = new(2026, 9, 20, 15, 30, 0); context.ScheduledEnd = context.ScheduledStart.AddHours(1);
        Assert.Equal("Live", InternalInterviewPolicy.Transition(context, Session(), "start", Now));
        Assert.Throws<InternalInterviewException>(() => InternalInterviewPolicy.Transition(context, Session(), "start", Now.AddHours(-1)));
        Assert.Throws<InternalInterviewException>(() => InternalInterviewPolicy.ScheduleUtc(Now, "not-a-zone"));
    }

    [Fact]
    public void Recording_and_transcription_have_separate_explicit_versioned_consent()
    {
        var session = Session(); session.ConfigurationJson = "{\"recordingEnabled\":true,\"transcriptionEnabled\":true}";
        Assert.Throws<InternalInterviewException>(() => InternalInterviewPolicy.ValidateConsent(session, new(false, true, InternalInterviewPolicy.ConsentNoticeVersion)));
        Assert.Throws<InternalInterviewException>(() => InternalInterviewPolicy.ValidateConsent(session, new(true, false, InternalInterviewPolicy.ConsentNoticeVersion)));
        Assert.Throws<InternalInterviewException>(() => InternalInterviewPolicy.ValidateConsent(session, new(true, true, "old-notice")));
        InternalInterviewPolicy.ValidateConsent(session, new(true, true, InternalInterviewPolicy.ConsentNoticeVersion));
    }

    [Fact]
    public void Ai_configuration_requires_question_bank_and_transcription()
    {
        Assert.Throws<InternalInterviewException>(() => InternalInterviewPolicy.ValidateConfiguration(new() { Mode = "AI" }));
        InternalInterviewPolicy.ValidateConfiguration(new() { Mode = "AI", TranscriptionEnabled = true, QuestionIds = [1] });
        Assert.Throws<InternalInterviewException>(() => InternalInterviewPolicy.ValidateConfiguration(new() { QuestionIds = [1, 1] }));
        Assert.Throws<InternalInterviewException>(() => InternalInterviewPolicy.ValidateConfiguration(new() { MaxQuestions = 1000 }));
    }

    [Fact]
    public void Candidate_cannot_add_review_notes_questions_or_automated_decisions()
    {
        var access = new InterviewAccess(Context(), Session("Live"), null);
        foreach (var kind in new[] { "note", "question", "analysis", "reject", "score", "honesty", "gaze" })
            Assert.Throws<InternalInterviewException>(() => InternalInterviewPolicy.ValidateClientEvent(access, new(Guid.NewGuid().ToString(), kind, "text", 1), Now));
        InternalInterviewPolicy.ValidateClientEvent(access, new(Guid.NewGuid().ToString(), "answer", "Work evidence", 1), Now);
    }

    [Fact]
    public void Browser_events_are_allowlisted_payload_free_self_reported_evidence()
    {
        var access = new InterviewAccess(Context(), Session("Live"), null);
        foreach (var kind in InternalInterviewPolicy.BrowserEvents)
            InternalInterviewPolicy.ValidateClientEvent(access, new(Guid.NewGuid().ToString(), kind, ""), Now);
        Assert.Throws<InternalInterviewException>(() => InternalInterviewPolicy.ValidateClientEvent(access, new(Guid.NewGuid().ToString(), "paste", "private clipboard contents"), Now));
        var panel = access with { User = User(4, 20, "recruitment.interview.panel") };
        Assert.Throws<InternalInterviewException>(() => InternalInterviewPolicy.ValidateClientEvent(panel, new(Guid.NewGuid().ToString(), "blur", ""), Now));
        Assert.Throws<InternalInterviewException>(() => InternalInterviewPolicy.ValidateClientEvent(panel, new(Guid.NewGuid().ToString(), "answer", "impersonated", 1), Now));
    }

    [Fact]
    public void Candidate_projection_excludes_future_questions_rubrics_revocation_state_and_signing_secrets()
    {
        var session = Session(); session.QuestionsJson = "[{\"question\":\"private next question\",\"evaluationCriteria\":\"private rubric\"}]";
        var view = JsonSerializer.Serialize(InternalInterviewEndpoints.View(new(Context(), session, null)));
        Assert.DoesNotContain("private", view); Assert.DoesNotContain(session.LinkVersion, view); Assert.DoesNotContain(session.RoomId, view);
    }

    [Fact]
    public void Candidate_link_is_bound_expiring_tamper_resistant_and_kept_out_of_querystring()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["InternalInterviews:PublicPortalBaseUrl"] = "https://hrms.example.invalid" }).Build();
        var links = new InternalInterviewLinks(new EphemeralDataProtectionProvider(), config, new TestTime(Now));
        var session = Session(); var link = new Uri(links.Create(session).Url);
        Assert.Equal("", link.Query); Assert.Equal("/interview/7", link.AbsolutePath);
        var token = Uri.UnescapeDataString(link.Fragment["#access=".Length..]);
        Assert.Equal(7, links.Read(token).InterviewId);
        Assert.Equal(session.LinkVersion, links.Read(token).LinkVersion);
        Assert.Throws<InternalInterviewException>(() => links.Read(token[..^8] + "changed"));
        session.LinkExpiresAtUtc = Now.AddMinutes(-1);
        var expired = new Uri(links.Create(session).Url);
        Assert.Throws<InternalInterviewException>(() => links.Read(Uri.UnescapeDataString(expired.Fragment["#access=".Length..])));
    }

    [Fact]
    public void Livekit_grant_is_room_scoped_short_lived_and_has_no_admin_or_record_permission()
    {
        var room = Guid.NewGuid().ToString("N");
        var token = InternalInterviewTransport.CreateToken("test-key", "synthetic-test-secret-not-for-deployment", room, "candidate", new(Now), TimeSpan.FromMinutes(2), false);
        var payload = token.Split('.')[1].Replace('-', '+').Replace('_', '/');
        using var json = JsonDocument.Parse(Convert.FromBase64String(payload.PadRight((payload.Length + 3) / 4 * 4, '=')));
        var root = json.RootElement; var grants = root.GetProperty("video");
        Assert.Equal(room, grants.GetProperty("room").GetString());
        Assert.True(grants.GetProperty("roomJoin").GetBoolean());
        Assert.False(grants.TryGetProperty("roomAdmin", out _)); Assert.False(grants.TryGetProperty("roomCreate", out _));
        Assert.False(grants.TryGetProperty("roomRecord", out _)); Assert.False(grants.GetProperty("canPublishData").GetBoolean());
        Assert.Equal(new DateTimeOffset(Now).AddMinutes(2).ToUnixTimeSeconds(), root.GetProperty("exp").GetInt64());
    }

    private sealed class TestTime(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now);
    }
}
