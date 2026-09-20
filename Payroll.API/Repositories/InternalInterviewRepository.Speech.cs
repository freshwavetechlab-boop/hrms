using Dapper;
using Payroll.API.Models;
using static Payroll.API.Services.InternalInterviewPolicy;

namespace Payroll.API.Repositories;

public sealed partial class InternalInterviewRepository
{
    internal const int AnswerGraceSeconds = 10;
    internal const int VoiceReviewSeconds = 120;
    internal static DateTime AnswerDeadline(DateTime questionAt, int seconds, DateTime? voiceAt)
    {
        var answerEnd = questionAt.AddSeconds(seconds + AnswerGraceSeconds);
        return voiceAt.HasValue && voiceAt.Value.AddSeconds(VoiceReviewSeconds) > answerEnd ? voiceAt.Value.AddSeconds(VoiceReviewSeconds) : answerEnd;
    }
    public async Task BeginTranscriptionAsync(InterviewAccess access, long questionId)
    {
        await using var db = Db(); await db.OpenAsync(); await using var tx = await db.BeginTransactionAsync();
        var session = await LockedAsync(db, tx, access.Context.InterviewId);
        Require(session.LinkVersion == access.Session.LinkVersion && session.Status == "Live" && session.TranscriptionConsent, "The live consented candidate session changed.", 409);
        RequireOpen(await ContextAsync(db, session.InterviewId, tx), session, Now);
        var question = await db.QuerySingleOrDefaultAsync<InternalInterviewEvent>("SELECT * FROM recruitment_internal_interview_events WHERE InterviewId=@Id AND Kind='question' ORDER BY Id DESC LIMIT 1", new { Id = session.InterviewId }, tx);
        Require(question?.Id == questionId, "The current question changed.", 409);
        var key = "voice:" + questionId;
        var started = await db.ExecuteScalarAsync<DateTime?>("SELECT CreatedAtUtc FROM recruitment_internal_interview_events WHERE InterviewId=@Id AND EventKey=@Key", new { Id = session.InterviewId, Key = key }, tx);
        Require(Now <= AnswerDeadline(question!.CreatedAtUtc, session.Configuration.AnswerSeconds, started), "Voice input arrived after the answer window closed.", 409);
        if (started is null) await AppendAsync(db, tx, session.InterviewId, key, "voice-parsing", "candidate", "server", "Voice processing requested. Up to 120 seconds for local parsing and draft review; interview end time still applies.", questionId);
        await tx.CommitAsync();
    }
    public async Task<InternalInterviewEvent?> CurrentQuestionAsync(InterviewAccess access)
    {
        await using var db = Db();
        return await db.QuerySingleOrDefaultAsync<InternalInterviewEvent>("SELECT * FROM recruitment_internal_interview_events WHERE InterviewId=@Id AND Kind='question' ORDER BY Id DESC LIMIT 1", new { Id = access.Context.InterviewId });
    }
    public async Task<InternalInterviewEvent> SpokenQuestionAsync(InterviewAccess access, long eventId)
    {
        await using var db = Db();
        var question = await db.QuerySingleOrDefaultAsync<InternalInterviewEvent>("SELECT * FROM recruitment_internal_interview_events WHERE InterviewId=@Id AND Id=@Event AND Kind IN ('question','introduction')", new { Id = access.Context.InterviewId, Event = eventId });
        Require(question is not null, "This spoken question is unavailable.", 404);
        if (question!.Kind == "question") Require((await CurrentQuestionAsync(access))?.Id == eventId, "Only the current question may be spoken.", 409);
        return question;
    }
}
