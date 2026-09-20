using System.Text.Json;
using Dapper;
using Payroll.API.Models;
using Payroll.API.Services;
using static Payroll.API.Services.InternalInterviewPolicy;

namespace Payroll.API.Repositories;

public sealed partial class InternalInterviewRepository
{
    internal async Task<List<long>> PendingAiAsync()
    {
        await using var db = Db();
        return (await db.QueryAsync<long>("""
            SELECT s.InterviewId FROM recruitment_internal_interview_sessions s
            LEFT JOIN recruitment_internal_interview_events q ON q.Id=(SELECT MAX(lq.Id) FROM recruitment_internal_interview_events lq WHERE lq.InterviewId=s.InterviewId AND lq.Kind='question')
            LEFT JOIN recruitment_internal_interview_events h ON h.Id=(SELECT MAX(lh.Id) FROM recruitment_internal_interview_events lh WHERE lh.InterviewId=s.InterviewId AND lh.Kind='session-ai')
            LEFT JOIN recruitment_internal_interview_events v ON v.InterviewId=s.InterviewId AND v.EventKey=CONCAT('voice:',q.Id)
            WHERE s.TranscriptionConsent=1 AND s.RetainUntilUtc>@Now AND s.MediaState<>'Purged' AND
            (s.Status='Live' AND s.Control='AI' AND s.ScheduledEndUtc>@Now
             AND (q.Id IS NULL OR NOT EXISTS(SELECT 1 FROM recruitment_internal_interview_events i WHERE i.InterviewId=s.InterviewId AND i.Kind='introduction')
                  OR EXISTS(SELECT 1 FROM recruitment_internal_interview_events a WHERE a.InterviewId=s.InterviewId AND a.Kind='answer' AND a.QuestionEventId=q.Id)
                  OR @Now>GREATEST(DATE_ADD(q.CreatedAtUtc, INTERVAL (COALESCE(CAST(JSON_UNQUOTE(JSON_EXTRACT(s.ConfigurationJson,'$.answerSeconds')) AS UNSIGNED),120)+@Grace) SECOND),
                      COALESCE(DATE_ADD(v.CreatedAtUtc, INTERVAL @VoiceGrace SECOND),q.CreatedAtUtc)))
             AND NOT EXISTS
                (SELECT 1 FROM recruitment_internal_interview_events e WHERE e.InterviewId=s.InterviewId AND e.Kind='questions-finished'
                 AND e.Id>COALESCE(h.Id,0))
             AND NOT EXISTS
                (SELECT 1 FROM recruitment_internal_interview_events f WHERE f.InterviewId=s.InterviewId AND f.Kind='ai-error'
                 AND f.EventKey LIKE CONCAT('turn:',COALESCE(q.Id,0),':section:',COALESCE(h.Id,0),':failed:%')
                 AND f.Id>COALESCE((SELECT MAX(t.Id) FROM recruitment_internal_interview_events t WHERE t.InterviewId=s.InterviewId AND t.Kind='ai-retry'),0))
             OR s.Status='Completed' AND (EXISTS
                (SELECT 1 FROM recruitment_internal_interview_events a WHERE a.InterviewId=s.InterviewId AND a.Kind='answer'
                 AND NOT EXISTS(SELECT 1 FROM recruitment_internal_interview_events r WHERE r.InterviewId=s.InterviewId AND r.EventKey=CONCAT('review:',a.Id))
                 AND NOT EXISTS(SELECT 1 FROM recruitment_internal_interview_events f WHERE f.InterviewId=s.InterviewId AND f.EventKey LIKE CONCAT('review:',a.Id,':failed:%')
                   AND f.Id>COALESCE((SELECT MAX(t.Id) FROM recruitment_internal_interview_events t WHERE t.InterviewId=s.InterviewId AND t.Kind='ai-retry'),0)))
                OR (NOT EXISTS(SELECT 1 FROM recruitment_internal_interview_events r WHERE r.InterviewId=s.InterviewId AND r.EventKey='review:summary')
                    AND NOT EXISTS(SELECT 1 FROM recruitment_internal_interview_events f WHERE f.InterviewId=s.InterviewId AND f.EventKey LIKE 'review:summary:failed:%'
                        AND f.Id>COALESCE((SELECT MAX(t.Id) FROM recruitment_internal_interview_events t WHERE t.InterviewId=s.InterviewId AND t.Kind='ai-retry'),0)))))
            ORDER BY (s.Status='Live') DESC,s.UpdatedAtUtc,s.InterviewId LIMIT 20
            """, new { Now, Grace = AnswerGraceSeconds, VoiceGrace = VoiceReviewSeconds })).ToList();
    }

    public async Task RetryAiAsync(long id, AuthUser user)
    {
        var access = await AccessAsync(id, user);
        Require(access.Session.TranscriptionConsent, "AI assistance requires transcription consent.", 403);
        await using var db = Db(); await db.OpenAsync(); await using var tx = await db.BeginTransactionAsync();
        var session = await LockedAsync(db, tx, id);
        var last = await db.ExecuteScalarAsync<DateTime?>("SELECT MAX(CreatedAtUtc) FROM recruitment_internal_interview_events WHERE InterviewId=@Id AND Kind='ai-retry'", new { Id = id }, tx);
        Require(last is null || last < Now.AddMinutes(-1), "Wait one minute before requesting another AI retry.", 429);
        await AppendAsync(db, tx, id, Guid.NewGuid().ToString(), "ai-retry", access.Actor, "panel", "Retry requested; previously saved answers and drafts remain unchanged.");
        await tx.CommitAsync();
    }

    internal async Task ProcessAiAsync(long id, InternalInterviewAiService ai, CancellationToken ct)
    {
        await using var db = Db(); await db.OpenAsync(ct);
        var lease = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"hrms-interview-ai:{db.Database}:{id}")));
        if (!await db.ExecuteScalarAsync<bool>("SELECT GET_LOCK(@Lease,0)", new { Lease = lease })) return;
        long? workQuestion = null; string workKey = ""; long retry = 0;
        try
        {
            var session = await db.QuerySingleAsync<InternalInterviewSession>("SELECT * FROM recruitment_internal_interview_sessions WHERE InterviewId=@Id", new { Id = id });
            if (!session.TranscriptionConsent || session.RetainUntilUtc <= Now || session.MediaState == "Purged") return;
            // Browser/participant noise must not push review state out of the bounded worker view.
            var events = (await db.QueryAsync<InternalInterviewEvent>("""
                SELECT * FROM recruitment_internal_interview_events WHERE InterviewId=@Id
                AND Kind IN ('question','answer','session-ai','ai-retry','ai-error','questions-finished','introduction',
                    'voice-parsing','answer-timeout','ai-review','ai-summary') ORDER BY Id LIMIT 10000
                """, new { Id = id })).ToList();
            retry = events.LastOrDefault(e => e.Kind == "ai-retry")?.Id ?? 0;
            var latest = events.LastOrDefault(e => e.Kind == "question");
            var handoff = events.LastOrDefault(e => e.Kind == "session-ai");
            var section = handoff?.Source == "panel-section" ? handoff.Text : null;
            var context = await ContextAsync(db, id);
            if (session.Status == "Live" && session.Control == "AI")
            {
                RequireOpen(context, session, Now);
                workQuestion = latest?.Id; workKey = "turn:" + (latest?.Id ?? 0) + ":section:" + (handoff?.Id ?? 0);
                if (events.Any(e => e.Kind == "ai-error" && e.EventKey.StartsWith(workKey + ":failed:", StringComparison.Ordinal) && e.Id > retry)) return;
                if (events.Any(e => e.Kind == "questions-finished" && e.Id > (handoff?.Id ?? 0))) return;
                if (!events.Any(e => e.Kind == "introduction"))
                {
                    await SaveAiEventAsync(db, session, "introduction", "introduction", session.Configuration.Language == "hi"
                        ? "नमस्ते। हम चुने गए विषयों पर प्रश्न पूछेंगे। प्रत्येक उत्तर को जाँचकर जमा करें। आवश्यकता हो तो मानव पैनल से सहायता लें। अंतिम निर्णय मानव पैनल लेगा।"
                        : "Welcome. We will discuss the selected interview topics. Review and submit each answer. Ask the human panel for help when needed. Only the human panel makes the final hiring decision.", null, ct, "configured-flow");
                    return;
                }
                var answer = latest is null ? null : events.LastOrDefault(e => e.Kind == "answer" && e.QuestionEventId == latest.Id);
                var voiceAt = latest is null ? null : events.FirstOrDefault(e => e.Kind == "voice-parsing" && e.QuestionEventId == latest.Id)?.CreatedAtUtc;
                if (latest is not null && answer is null && Now <= AnswerDeadline(latest.CreatedAtUtc, session.Configuration.AnswerSeconds, voiceAt)) return;
                if (latest is not null && answer is null && !events.Any(e => e.Kind == "answer-timeout" && e.QuestionEventId == latest.Id))
                {
                    await SaveAiEventAsync(db, session, $"timeout:{latest.Id}", "answer-timeout", "No submitted answer was received within the configured response window. Human review may allow another question.", latest.Id, ct, "server"); return;
                }
                var parent = latest?.QuestionEventId is > 0 ? events.SingleOrDefault(e => e.Id == latest.QuestionEventId && e.Kind == "question") : latest;
                var bank = parent is null ? null : session.Questions.FirstOrDefault(q => parent.Source == "bank:" + q.Id);
                if (bank is not null && answer is not null && (section is null || bank.Skill == section))
                {
                    var asked = events.Where(e => e.Kind == "question" && e.QuestionEventId == parent!.Id).ToList();
                    var allowed = ApprovedFollowUps(bank).Where(q => !AlreadyAsked(events, q)).ToArray();
                    if (asked.Count < session.Configuration.FollowUpLimit && allowed.Length > 0)
                    {
                        var selected = await ai.ChooseFollowUpAsync(bank, latest!, answer, allowed, ct);
                        if (selected.HasValue) { await SaveAiEventAsync(db, session, workKey, "question", allowed[selected.Value], parent!.Id, ct, "ai-bank-followup"); return; }
                    }
                }
                var next = session.Questions.FirstOrDefault(q => (section is null || q.Skill == section)
                    && !AlreadyAsked(events, q.Question) && !events.Any(e => e.Kind == "question" && e.Source == "bank:" + q.Id));
                var questionCount = events.Count(e => e.Kind == "question" && e.QuestionEventId is null);
                if (next is null || questionCount >= session.Configuration.MaxQuestions)
                { await SaveAiEventAsync(db, session, "questions-finished:" + (handoff?.Id ?? 0), "questions-finished", "The selected question section is complete or the overall question limit was reached. The panel may choose another section or complete the session; the hiring outcome remains a human decision.", null, ct, "configured-flow"); return; }
                await SaveAiEventAsync(db, session, workKey, "question", next.Question, null, ct, "bank:" + next.Id);
            }
            else if (session.Status == "Completed")
            {
                // One answer per worker pass; fair to live interviews and bounded local-model capacity.
                var answer = events.FirstOrDefault(e => e.Kind == "answer" && !events.Any(r => r.EventKey == $"review:{e.Id}")
                    && !events.Any(r => r.EventKey.StartsWith($"review:{e.Id}:failed:", StringComparison.Ordinal) && r.Id > retry));
                if (answer is null)
                {
                    workKey = "review:summary";
                    if (events.Any(e => e.EventKey == workKey || e.EventKey.StartsWith(workKey + ":failed:", StringComparison.Ordinal) && e.Id > retry)) return;
                    // A separate durable pass: summary errors do not erase individual drafts,
                    // and retries never duplicate a saved combined review.
                    // Fetch the full submitted Q/A set independently of the operational audit cap.
                    // Question/answer counts are already bounded by the session configuration.
                    var evidence = (await db.QueryAsync<InternalInterviewEvent>("SELECT * FROM recruitment_internal_interview_events WHERE InterviewId=@Id AND Kind IN ('question','answer') ORDER BY Id", new { Id = id })).ToArray();
                    var summary = await ai.ReviewInterviewAsync(evidence.Where(e => e.Kind == "question").ToArray(),
                        evidence.Where(e => e.Kind == "answer").ToArray(), ct);
                    await SaveAiEventAsync(db, session, workKey, "ai-summary", Json(summary), null, ct);
                    return;
                }
                var question = events.SingleOrDefault(e => e.Kind == "question" && e.Id == answer.QuestionEventId);
                if (question is null) return;
                workQuestion = question.Id; workKey = "review:" + answer.Id;
                var bankQuestion = question.QuestionEventId is > 0 ? events.Single(e => e.Id == question.QuestionEventId) : question;
                var criteria = session.Questions.FirstOrDefault(q => bankQuestion.Source == "bank:" + q.Id)?.EvaluationCriteria ?? "Review only the explicit question and answer. No assumed rubric.";
                var observations = await ai.ReviewAnswerAsync(question, answer, criteria, ct);
                await SaveAiEventAsync(db, session, workKey, "ai-review", Json(observations), question.Id, ct);
            }
        }
        catch (InternalInterviewException error)
        {
            if (workKey.Length > 0)
            {
                var session = await db.QuerySingleAsync<InternalInterviewSession>("SELECT * FROM recruitment_internal_interview_sessions WHERE InterviewId=@Id", new { Id = id });
                await SaveAiEventAsync(db, session, workKey + ":failed:" + retry, "ai-error", error.Message[..Math.Min(error.Message.Length, 600)], workQuestion, ct);
            }
        }
        finally { await db.ExecuteAsync("SELECT RELEASE_LOCK(@Lease)", new { Lease = lease }); }
    }

    // A human may already have asked the bank question before handing over. Match literal
    // wording across the whole session, not just bank IDs or one parent's follow-up IDs.
    // Do not use a model/semantic guess to collapse genuinely different technical questions.
    internal static string QuestionIdentity(string text) => string.Join(" ", text.Normalize(System.Text.NormalizationForm.FormKC)
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
    internal static bool AlreadyAsked(IEnumerable<InternalInterviewEvent> events, string text) =>
        events.Any(e => e.Kind == "question" && QuestionIdentity(e.Text) == QuestionIdentity(text));
    internal static string[] ApprovedFollowUps(InterviewQuestion bank) => bank.FollowUpInstructions
        .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).DistinctBy(QuestionIdentity).Take(3).ToArray();

    private async Task SaveAiEventAsync(MySqlConnector.MySqlConnection db, InternalInterviewSession snapshot, string key, string kind, string text,
        long? question, CancellationToken ct, string source = "local-ai")
    {
        await using var tx = await db.BeginTransactionAsync(ct);
        var session = await LockedAsync(db, tx, snapshot.InterviewId);
        // Late inference cannot resurrect an ended/purged session or override a human takeover.
        if (session.Status != snapshot.Status || session.Control != snapshot.Control || session.Revision != snapshot.Revision
            || session.RetainUntilUtc <= Now || session.MediaState == "Purged") return;
        if (session.Status == "Live" && kind != "ai-error") RequireOpen(await ContextAsync(db, session.InterviewId, tx), session, Now);
        if (await db.ExecuteScalarAsync<bool>("SELECT COUNT(*) FROM recruitment_internal_interview_events WHERE InterviewId=@Id AND EventKey=@Key", new { Id = session.InterviewId, Key = key }, tx)) return;
        await AppendAsync(db, tx, session.InterviewId, key, kind, "ai-assistant", source, text, question);
        await db.ExecuteAsync("UPDATE recruitment_internal_interview_sessions SET UpdatedAtUtc=UTC_TIMESTAMP(6) WHERE InterviewId=@Id", new { Id = session.InterviewId }, tx);
        await tx.CommitAsync(ct);
    }
}
