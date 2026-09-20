using Dapper;
using Payroll.API.Models;
using static Payroll.API.Services.InternalInterviewPolicy;

namespace Payroll.API.Repositories;

public sealed partial class InternalInterviewRepository
{
    public async Task ResetUnstartedScheduleAsync(long id, long revision, AuthUser user)
    {
        Payroll.API.Services.InternalInterviewRuntimeState.Read(configuration).RequireAdmission();
        await using var db = Db(); await db.OpenAsync();
        var context = await ContextAsync(db, id); await AuthorizeAsync(db, context, user, true);
        await using var tx = await db.BeginTransactionAsync();
        var session = await LockedAsync(db, tx, id);
        Require(session.Revision == revision, "Session changed. Refresh before using the revised schedule.", 409);
        Require(CanResetUnstartedSchedule(context, session, Now), "Only an unstarted internal session with a changed, open schedule can be reset. Started sessions require another round.", 409);
        Require(!await db.ExecuteScalarAsync<bool>("SELECT COUNT(*) FROM recruitment_internal_interview_recordings WHERE InterviewId=@Id", new { Id = id }, tx), "Retained recordings cannot be replaced by rescheduling.", 409);
        var start = ScheduleUtc(context.ScheduledStart, context.TimeZoneId); var end = ScheduleUtc(context.ScheduledEnd, context.TimeZoneId);
        Require(end > start && end - start <= TimeSpan.FromHours(4), "Internal interviews support a positive duration up to four hours.");
        session.Status = "Scheduled"; session.EndedAtUtc = null; session.ConsentAtUtc = null;
        session.RecordingConsent = false; session.TranscriptionConsent = false; session.MediaError = "";
        session.LinkVersion = Guid.NewGuid().ToString("N"); session.LinkExpiresAtUtc = end.AddHours(2); session.RetainUntilUtc = end.AddDays(RetentionDays);
        await db.ExecuteAsync("UPDATE recruitment_internal_interview_sessions SET RoomId=@Room,ScheduledStartUtc=@Start,ScheduledEndUtc=@End WHERE InterviewId=@Id",
            new { Id = id, Room = Guid.NewGuid().ToString("N"), Start = start, End = end }, tx);
        await SaveSessionAsync(db, tx, session);
        await AppendAsync(db, tx, id, Guid.NewGuid().ToString(), "schedule-reset", $"panel:{user.Id}", "server", "Updated original schedule accepted before any interview started. Previous candidate links and consent revoked; prior audit history and hiring result retained.");
        await tx.CommitAsync();
    }
}
