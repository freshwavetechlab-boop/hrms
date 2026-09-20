using Payroll.API.Models;

namespace Payroll.API.Services;

public static class InternalInterviewPolicy
{
    public const string ConsentNoticeVersion = "internal-interview-v1";
    public const string InternalDestination = "Internal HRMS interview";
    public static bool UsesFrevoVideo(InternalInterviewContext context) => context.Mode == "Virtual" && context.LocationOrLink == InternalDestination;
    public static readonly string[] BrowserEvents = ["focus", "blur", "visibility-hidden", "visibility-visible", "fullscreen-exit", "camera-off", "camera-on", "microphone-off", "microphone-on", "device-disconnected", "connection-lost", "reconnected", "copy", "paste", "page-exit", "page-reload"];
    public static bool Has(AuthUser user, params string[] permissions) => user.IsActive && permissions.Any(p => user.Permissions.Contains(p, StringComparer.OrdinalIgnoreCase));
    public static bool CanManage(AuthUser user) => Has(user, "recruitment.interview.schedule", "recruitment.manage", "settings.manage");
    public static bool CanAccess(AuthUser user, InternalInterviewContext context) =>
        (!user.ClientId.HasValue || user.ClientId == context.ClientId)
        && (CanManage(user) || (Has(user, "recruitment.interview.panel") && context.PanelUserIds.Contains(user.Id)));
    public static bool Terminal(string status) => status is "Completed" or "Cancelled" or "No Show";

    public static bool CanResetUnstartedSchedule(InternalInterviewContext context, InternalInterviewSession session, DateTime now) =>
        session.StartedAtUtc is null && session.MediaState == "None" && session.Status != "Completed" && session.RetainUntilUtc > now
        && !Terminal(context.InterviewStatus) && ScheduleUtc(context.ScheduledEnd, context.TimeZoneId) > now
        && (session.ScheduledStartUtc != ScheduleUtc(context.ScheduledStart, context.TimeZoneId) || session.ScheduledEndUtc != ScheduleUtc(context.ScheduledEnd, context.TimeZoneId));

    public static void ValidateConfiguration(InternalInterviewConfiguration value)
    {
        Require(value.Mode is "Human" or "AI" or "Hybrid", "Select Human, AI or Hybrid interview mode.");
        Require(value.Difficulty is "Beginner" or "Intermediate" or "Advanced", "Select a supported difficulty.");
        Require(value.Language is "en" or "hi", "Select English or Hindi.");
        Require(value.MaxQuestions is >= 1 and <= 40, "Maximum questions must be between 1 and 40.");
        Require(value.AnswerSeconds is >= 15 and <= 900, "Answer duration must be between 15 and 900 seconds.");
        Require(value.FollowUpLimit is >= 0 and <= 3, "Follow-up limit must be between 0 and 3.");
        Require(value.QuestionIds is not null && value.QuestionIds.Count <= 40 && value.QuestionIds.All(id => id > 0)
            && value.QuestionIds.Distinct().Count() == value.QuestionIds.Count, "Choose at most 40 distinct questions.");
        Require(value.Mode == "Human" || (value.QuestionIds!.Count > 0 && value.TranscriptionEnabled), "AI/hybrid interviews require a question set and consent to saving answers for review.");
    }

    public static void ValidateQuestion(InterviewQuestion question)
    {
        Require(question.ClientId > 0 && question.PositionId > 0, "A client and position are required.");
        Require(!string.IsNullOrWhiteSpace(question.Skill) && question.Skill.Length <= 120, "Enter a skill of at most 120 characters.");
        Require(!string.IsNullOrWhiteSpace(question.Question) && question.Question.Length <= 2000, "Enter a question of at most 2000 characters.");
        Require(!string.IsNullOrWhiteSpace(question.EvaluationCriteria) && question.EvaluationCriteria.Length <= 3000, "Enter evaluation criteria of at most 3000 characters.");
        Require(question.FollowUpInstructions is not null && question.FollowUpInstructions.Length <= 1500, "Follow-up instructions are too long.");
        Require(question.FollowUpInstructions!.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Length <= 3, "Enter at most three approved follow-up questions, one per line.");
        Require(question.Difficulty is "Beginner" or "Intermediate" or "Advanced", "Select a supported difficulty.");
        Require(question.Language is "en" or "hi", "Select English or Hindi.");
    }

    public static DateTime ScheduleUtc(DateTime value, string timeZoneId)
    {
        try { return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(value, DateTimeKind.Unspecified), TimeZoneInfo.FindSystemTimeZoneById(timeZoneId)); }
        catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        { throw new InternalInterviewException(422, "The interview schedule time zone/time is invalid. Update the existing schedule first."); }
    }

    public static void RequireOpen(InternalInterviewContext context, InternalInterviewSession session, DateTime now)
    {
        Require(UsesFrevoVideo(context), "Interview with Frevo One is off for this schedule. Use the meeting details supplied by HR.", 409);
        Require(!Terminal(context.InterviewStatus) && !Terminal(session.Status), "This interview session is closed.", 409);
        Require(session.LinkExpiresAtUtc > now, "This interview access has expired. Ask HR for a new link.", 410);
        Require(now < ScheduleUtc(context.ScheduledEnd, context.TimeZoneId), "The scheduled interview duration has ended.", 409);
        // Any change to the original schedule invalidates the old consented room.
        Require(session.ScheduledEndUtc == default || (session.ScheduledStartUtc == ScheduleUtc(context.ScheduledStart, context.TimeZoneId)
            && session.ScheduledEndUtc == ScheduleUtc(context.ScheduledEnd, context.TimeZoneId)), "The schedule changed. HR must configure a new internal session/link.", 409);
    }

    public static void ValidateConsent(InternalInterviewSession session, InterviewConsent consent)
    {
        Require(consent.NoticeVersion == ConsentNoticeVersion, "Review the current consent notice before joining.");
        Require(!session.Configuration.RecordingEnabled || consent.Recording, "Recording consent is required for this configured session; contact HR for an alternative.");
        Require(!session.Configuration.TranscriptionEnabled || consent.Transcription, "Transcription consent is required for this configured session; contact HR for an alternative.");
    }

    public static string Transition(InternalInterviewContext context, InternalInterviewSession session, string action, DateTime now)
    {
        Require(!Terminal(session.Status), "This session has already ended.", 409);
        Require(!Terminal(context.InterviewStatus), "The original interview is already closed.", 409);
        if (action is "start" or "ai" or "human") RequireOpen(context, session, now);
        switch (action)
        {
            case "start":
                Require(session.Status == "Waiting" && session.ConsentAtUtc.HasValue, "The candidate must consent and enter the waiting room first.", 409);
                Require(now >= ScheduleUtc(context.ScheduledStart, context.TimeZoneId).AddMinutes(-30)
                    && now <= ScheduleUtc(context.ScheduledEnd, context.TimeZoneId), "The interview is outside its scheduled joining window.", 409);
                Require(session.LinkExpiresAtUtc > now, "Issue a new candidate link before starting.", 410);
                return "Live";
            case "complete": Require(session.Status == "Live", "Only a live session can be completed.", 409); return "Completed";
            case "cancel": return "Cancelled";
            case "no-show":
                Require(session.StartedAtUtc is null && now > ScheduleUtc(context.ScheduledEnd, context.TimeZoneId), "No Show is available only after the scheduled end, when no session started.", 409);
                return "No Show";
            case "ai":
                Require(session.Status == "Live" && session.Configuration.Mode is "AI" or "Hybrid", "AI control is unavailable for this session.", 409); return "Live";
            case "human": Require(session.Status == "Live", "Start the interview before changing control.", 409); return "Live";
            default: throw new InternalInterviewException(400, "Unknown session action.");
        }
    }

    public static void ValidateClientEvent(InterviewAccess access, InterviewEventInput input, DateTime now)
    {
        Require(Guid.TryParse(input.EventKey, out _), "A valid event idempotency key is required.");
        Require(input.Text is not null && input.Text.Length <= 8000, "Event text exceeds the 8000-character limit.");
        Require(input.OffsetMs is null or >= 0 and <= 86_400_000, "Invalid event timestamp.");
        RequireOpen(access.Context, access.Session, now);
        if (BrowserEvents.Contains(input.Kind, StringComparer.Ordinal))
        {
            Require(access.IsCandidate && access.Session.ConsentAtUtc.HasValue, "Only a consented candidate may submit browser events.", 403);
            Require(input.Text!.Length == 0 && input.QuestionEventId is null, "Browser evidence must not contain clipboard contents or arbitrary payloads.");
            return;
        }
        Require(access.Session.Status == "Live", "The interview is not live.", 409);
        Require(input.Kind is "answer" or "question" or "note", "Unsupported event type.");
        Require(!string.IsNullOrWhiteSpace(input.Text), "Enter event text.");
        if (access.IsCandidate)
            Require(input.Kind == "answer" && input.QuestionEventId is > 0, "Candidates may answer only an existing interview question.", 403);
        else
            Require(input.Kind is "question" or "note", "Panel members cannot impersonate a candidate answer.", 403);
    }

    public static void Require(bool condition, string message, int status = 400)
    {
        if (!condition) throw new InternalInterviewException(status, message);
    }
}
