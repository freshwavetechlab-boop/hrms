using System.Data;
using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;
using static Payroll.API.Services.InternalInterviewPolicy;

namespace Payroll.API.Repositories;

public sealed partial class InternalInterviewRepository(IConfiguration configuration, TimeProvider time)
{
    private MySqlConnection Db() => new(configuration.GetConnectionString("Default"));
    private DateTime Now => time.GetUtcNow().UtcDateTime;
    private int RetentionDays => Math.Clamp(configuration.GetValue("InternalInterviews:RetentionDays", 30), 1, 365);
    private static string Json<T>(T value) => JsonSerializer.Serialize(value, InternalInterviewSession.Json);
    private const string ContextSql = """
        SELECT i.Id InterviewId,i.ApplicationId,a.CandidateId,a.ClientId,a.PositionId,
        TRIM(CONCAT(COALESCE(c.FirstName,''),' ',COALESCE(c.LastName,''))) CandidateName,
        p.PositionTitle,COALESCE(p.JobLocation,'') JobLocation,i.RoundCode,i.Status InterviewStatus,i.Result,
        i.TimeZoneId,i.ScheduledStart,i.ScheduledEnd,i.Mode,COALESCE(i.LocationOrLink,'') LocationOrLink
        FROM recruitment_interviews i
        JOIN recruitment_candidate_applications a ON a.Id=i.ApplicationId
        JOIN recruitment_candidates c ON c.Id=a.CandidateId
        JOIN recruitment_open_positions p ON p.Id=a.PositionId WHERE i.Id=@Id
        """;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var db = Db();
        await db.OpenAsync(ct);
        await using var resource = typeof(InternalInterviewRepository).Assembly.GetManifestResourceStream("Payroll.API.Database.InternalInterviews.sql")
            ?? throw new InvalidOperationException("Internal interview migration resource is missing.");
        using var reader = new StreamReader(resource);
        await db.ExecuteAsync(new CommandDefinition(await reader.ReadToEndAsync(ct), cancellationToken: ct));
        // Upgrade only this feature's additive tables; also supports early local preview installs.
        foreach (var column in new[] {
            ("recruitment_internal_interview_sessions", "ScheduledStartUtc", "DATETIME(6) NULL"),
            ("recruitment_internal_interview_sessions", "ScheduledEndUtc", "DATETIME(6) NULL"),
            ("recruitment_internal_interview_sessions", "MediaState", "VARCHAR(24) NOT NULL DEFAULT 'None'"),
            ("recruitment_internal_interview_sessions", "MediaError", "VARCHAR(600) NOT NULL DEFAULT ''"),
            ("recruitment_internal_interview_recordings", "EgressId", "VARCHAR(100) NOT NULL DEFAULT ''") })
            if (!await db.ExecuteScalarAsync<bool>("SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME=@Table AND COLUMN_NAME=@Column", new { Table = column.Item1, Column = column.Item2 }))
                await db.ExecuteAsync($"ALTER TABLE `{column.Item1}` ADD COLUMN `{column.Item2}` {column.Item3}");
    }

    private static async Task<InternalInterviewContext> ContextAsync(MySqlConnection db, long id, IDbTransaction? transaction = null)
    {
        var context = await db.QuerySingleOrDefaultAsync<InternalInterviewContext>(ContextSql, new { Id = id }, transaction)
            ?? throw new InternalInterviewException(404, "Interview was not found in your permitted scope.");
        context.PanelUserIds = (await db.QueryAsync<int>("SELECT PanelUserId FROM recruitment_interview_panel_members WHERE InterviewId=@Id", new { Id = id }, transaction)).ToList();
        return context;
    }

    private static async Task AuthorizeAsync(MySqlConnection db, InternalInterviewContext context, AuthUser user, bool manage = false)
    {
        Require(CanAccess(user, context) && (!manage || CanManage(user)), "Interview was not found in your permitted scope.", 404);
        Require(await RecruitmentAccessScope.CanAccessLocationAsync(db, user, context.ClientId, context.JobLocation), "Interview was not found in your permitted scope.", 404);
    }

    public async Task<InternalInterviewContext> GetContextAsync(long id, AuthUser user, bool manage = false)
    {
        await using var db = Db(); await db.OpenAsync();
        var context = await ContextAsync(db, id);
        await AuthorizeAsync(db, context, user, manage);
        return context;
    }

    public async Task<InterviewAccess> AccessAsync(long id, AuthUser? user, InterviewCandidateTicket? ticket = null)
    {
        await using var db = Db(); await db.OpenAsync();
        var context = await ContextAsync(db, id);
        if (user is not null) await AuthorizeAsync(db, context, user);
        else Require(ticket is not null && ticket.InterviewId == id && ticket.ExpiresAtUtc > Now, "The interview link is invalid or expired.", 401);
        var session = await db.QuerySingleOrDefaultAsync<InternalInterviewSession>("SELECT * FROM recruitment_internal_interview_sessions WHERE InterviewId=@Id", new { Id = id })
            ?? throw new InternalInterviewException(404, "Internal interview has not been configured.");
        if (user is null)
        {
            Require(session.LinkVersion == ticket!.LinkVersion && session.LinkExpiresAtUtc > Now, "The interview link has expired or been replaced.", 410);
            Require(UsesFrevoVideo(context), "Interview with Frevo One is off for this schedule. Use the meeting details supplied by HR.", 410);
            // Read-only status remains available after completion; writes and room grants stay closed.
        }
        Require(session.RetainUntilUtc > Now && session.MediaState != "Purged", "Session evidence retention has expired.", 410);
        return new(context, session, user);
    }

    public async Task<bool> HasInternalSessionAsync(long id, AuthUser user)
    {
        await using var db = Db(); await db.OpenAsync();
        // Absence may fall back to an external invite; scope denial must never take that path.
        var context = await ContextAsync(db, id);
        await AuthorizeAsync(db, context, user, true);
        if (!UsesFrevoVideo(context)) return false;
        return await db.ExecuteScalarAsync<bool>("SELECT COUNT(*) FROM recruitment_internal_interview_sessions WHERE InterviewId=@Id", new { Id = id });
    }

    // Called only when the existing schedule switches away from the internal marker.
    // Keep evidence, revoke the old candidate credential, and let maintenance close any room.
    public async Task DisableForExternalDeliveryAsync(long id, AuthUser user)
    {
        await using var db = Db(); await db.OpenAsync();
        if (!await db.ExecuteScalarAsync<bool>("SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='recruitment_internal_interview_sessions'")) return;
        await using var tx = await db.BeginTransactionAsync();
        var session = await db.QuerySingleOrDefaultAsync<InternalInterviewSession>("SELECT * FROM recruitment_internal_interview_sessions WHERE InterviewId=@Id FOR UPDATE", new { Id = id }, tx);
        if (session is null) return;
        var context = await ContextAsync(db, id, tx);
        await AuthorizeAsync(db, context, user, true);
        if (UsesFrevoVideo(context)) return;
        session.LinkVersion = Guid.NewGuid().ToString("N"); session.LinkExpiresAtUtc = Now;
        if (!Terminal(session.Status)) { session.Status = "Cancelled"; session.EndedAtUtc = Now; }
        if (session.MediaState is "Starting" or "Ready") session.MediaState = "Closing";
        await SaveSessionAsync(db, tx, session);
        await AppendAsync(db, tx, id, Guid.NewGuid().ToString(), "external-delivery-selected", $"panel:{user.Id}", "server",
            "Interview with Frevo One switched off. Prior candidate link revoked; external meeting details and retained evidence unchanged.");
        await tx.CommitAsync();
    }

    private static async Task<int> PositionScopeAsync(MySqlConnection db, long positionId, AuthUser user)
    {
        var position = await db.QuerySingleOrDefaultAsync<PositionScope>("SELECT ClientId,COALESCE(JobLocation,'') JobLocation FROM recruitment_open_positions WHERE Id=@Id", new { Id = positionId });
        Require(position is not null && (!user.ClientId.HasValue || user.ClientId == position.ClientId), "Position was not found in your permitted scope.", 404);
        Require(await RecruitmentAccessScope.CanAccessLocationAsync(db, user, position!.ClientId, position.JobLocation), "Position was not found in your permitted scope.", 404);
        return position.ClientId;
    }

    public async Task<List<InterviewQuestion>> QuestionsAsync(long positionId, AuthUser user)
    {
        Require(CanManage(user), "Question-bank management permission is required.", 403);
        await using var db = Db(); await db.OpenAsync();
        var clientId = await PositionScopeAsync(db, positionId, user);
        return (await db.QueryAsync<InterviewQuestion>("SELECT * FROM recruitment_interview_questions WHERE ClientId=@ClientId AND PositionId=@PositionId ORDER BY IsActive DESC,Skill,Id LIMIT 500", new { ClientId = clientId, PositionId = positionId })).ToList();
    }

    public async Task<List<InternalInterviewSession>> ScopedDashboardAsync(IEnumerable<long> authorizedInterviewIds, AuthUser user)
    {
        Require(user.IsActive, "Interview access is not enabled for this user.", 403);
        var ids = authorizedInterviewIds.Distinct().Take(1000).ToArray();
        if (ids.Length == 0) return [];
        await using var db = Db();
        await db.OpenAsync();
        var candidates = await db.QueryAsync<DashboardScope>("""
            SELECT i.Id InterviewId,a.ClientId,COALESCE(p.JobLocation,'') JobLocation FROM recruitment_interviews i
            JOIN recruitment_candidate_applications a ON a.Id=i.ApplicationId JOIN recruitment_open_positions p ON p.Id=a.PositionId
            WHERE i.Id IN @Ids AND (@ClientId IS NULL OR a.ClientId=@ClientId)
            AND (@Manage OR EXISTS(SELECT 1 FROM recruitment_interview_panel_members pm WHERE pm.InterviewId=i.Id AND pm.PanelUserId=@UserId))
            """, new { Ids = ids, user.ClientId, Manage = CanManage(user), UserId = user.Id });
        var locationDecisions = new Dictionary<(int, string), bool>();
        var visible = new List<long>();
        foreach (var row in candidates)
        {
            var key = (row.ClientId, row.JobLocation);
            if (!locationDecisions.TryGetValue(key, out var allowed)) locationDecisions[key] = allowed = await RecruitmentAccessScope.CanAccessLocationAsync(db, user, row.ClientId, row.JobLocation);
            if (allowed) visible.Add(row.InterviewId);
        }
        if (visible.Count == 0) return [];
        return (await db.QueryAsync<InternalInterviewSession>("SELECT * FROM recruitment_internal_interview_sessions WHERE InterviewId IN @Ids AND RetainUntilUtc>@Now AND MediaState<>'Purged' ORDER BY ScheduledStartUtc DESC LIMIT 1000", new { Ids = visible, Now })).ToList();
    }

    public async Task<InterviewQuestion> SaveQuestionAsync(InterviewQuestion question, AuthUser user)
    {
        Require(CanManage(user), "Question-bank management permission is required.", 403);
        await using var db = Db(); await db.OpenAsync();
        var clientId = await PositionScopeAsync(db, question.PositionId, user);
        Require(question.ClientId == clientId, "The question client must match its position.", 403);
        ValidateQuestion(question);
        if (question.Id > 0)
        {
            var affected = await db.ExecuteAsync("""
                UPDATE recruitment_interview_questions SET Skill=@Skill,Difficulty=@Difficulty,Language=@Language,
                Question=@Question,EvaluationCriteria=@EvaluationCriteria,FollowUpInstructions=@FollowUpInstructions,
                IsActive=@IsActive,UpdatedAtUtc=UTC_TIMESTAMP(6) WHERE Id=@Id AND ClientId=@ClientId AND PositionId=@PositionId
                """, question);
            Require(affected > 0, "Question was not found in your permitted scope.", 404);
        }
        else question.Id = await db.ExecuteScalarAsync<long>("""
            INSERT INTO recruitment_interview_questions
            (ClientId,PositionId,Skill,Difficulty,Language,Question,EvaluationCriteria,FollowUpInstructions,IsActive,CreatedByUserId,UpdatedAtUtc)
            VALUES (@ClientId,@PositionId,@Skill,@Difficulty,@Language,@Question,@EvaluationCriteria,@FollowUpInstructions,@IsActive,@UserId,UTC_TIMESTAMP(6));
            SELECT LAST_INSERT_ID();
            """, new { question.ClientId, question.PositionId, question.Skill, question.Difficulty, question.Language, question.Question,
                question.EvaluationCriteria, question.FollowUpInstructions, question.IsActive, UserId = user.Id });
        return question;
    }

    public async Task<InternalInterviewSession> ConfigureAsync(long id, InternalInterviewConfiguration request, AuthUser user)
    {
        InternalInterviewRuntimeState.Read(configuration).RequireAdmission();
        ValidateConfiguration(request);
        await using var db = Db(); await db.OpenAsync();
        var context = await ContextAsync(db, id);
        await AuthorizeAsync(db, context, user, true);
        Require(!Terminal(context.InterviewStatus), "A closed interview cannot be configured.", 409);
        var end = ScheduleUtc(context.ScheduledEnd, context.TimeZoneId);
        var start = ScheduleUtc(context.ScheduledStart, context.TimeZoneId);
        Require(end > start && end - start <= TimeSpan.FromHours(4), "Internal interviews support a positive duration up to four hours.");
        Require(end > Now, "Reschedule the interview before enabling an expired session.", 409);
        List<InterviewQuestion> questions = request.QuestionIds.Count == 0 ? [] : (await db.QueryAsync<InterviewQuestion>("""
            SELECT * FROM recruitment_interview_questions WHERE Id IN @Ids AND ClientId=@ClientId AND PositionId=@PositionId AND IsActive=TRUE
            """, new { Ids = request.QuestionIds, context.ClientId, context.PositionId })).ToList();
        Require(questions.Count == request.QuestionIds.Count, "Some questions are inactive or belong to a different job.");
        Require(questions.All(q => q.Language == request.Language && q.Difficulty == request.Difficulty), "Selected questions must match the session language and difficulty.");
        questions = request.QuestionIds.Select(questionId => questions.Single(q => q.Id == questionId)).ToList();
        await using var tx = await db.BeginTransactionAsync();
        var session = await db.QuerySingleOrDefaultAsync<InternalInterviewSession>("SELECT * FROM recruitment_internal_interview_sessions WHERE InterviewId=@Id FOR UPDATE", new { Id = id }, tx);
        var restartUnstarted = session is { Status: "Cancelled", StartedAtUtc: null, MediaState: "None" };
        Require(session is null || restartUnstarted || (session.Status == "Scheduled" && session.ConsentAtUtc is null), "Configuration is locked after candidate consent/start; schedule another round for changes.", 409);
        if (session is null)
        {
            await db.ExecuteAsync("""
                INSERT INTO recruitment_internal_interview_sessions
                (InterviewId,RoomId,LinkVersion,LinkExpiresAtUtc,ConfigurationJson,QuestionsJson,CreatedByUserId,RetainUntilUtc,Control,ScheduledStartUtc,ScheduledEndUtc,CreatedAtUtc,UpdatedAtUtc)
                VALUES (@Id,@RoomId,@Version,@Expiry,@Config,@Questions,@UserId,@RetainUntil,@Control,@Start,@End,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6))
                """, new { Id = id, RoomId = Guid.NewGuid().ToString("N"), Version = Guid.NewGuid().ToString("N"), Expiry = end.AddHours(2),
                    Config = Json(request), Questions = Json(questions), UserId = user.Id, RetainUntil = end.AddDays(RetentionDays), Control = request.Mode == "AI" ? "AI" : "Human", Start = start, End = end }, tx);
        }
        else await db.ExecuteAsync("""
            UPDATE recruitment_internal_interview_sessions SET ConfigurationJson=@Config,QuestionsJson=@Questions,
            LinkVersion=@Version,LinkExpiresAtUtc=@Expiry,Control=@Control,RetainUntilUtc=@RetainUntil,
            ScheduledStartUtc=@Start,ScheduledEndUtc=@End,Status='Scheduled',ConsentAtUtc=NULL,RecordingConsent=FALSE,
            TranscriptionConsent=FALSE,EndedAtUtc=NULL,Revision=Revision+1,UpdatedAtUtc=UTC_TIMESTAMP(6) WHERE InterviewId=@Id
            """, new { Id = id, Config = Json(request), Questions = Json(questions), Version = Guid.NewGuid().ToString("N"),
                Expiry = end.AddHours(2), Control = request.Mode == "AI" ? "AI" : "Human", RetainUntil = end.AddDays(RetentionDays), Start = start, End = end }, tx);
        await db.ExecuteAsync("UPDATE recruitment_interviews SET Mode='Virtual',LocationOrLink=@Destination WHERE Id=@Id",
            new { Id = id, Destination = InternalDestination }, tx);
        await AppendAsync(db, tx, id, Guid.NewGuid().ToString(), "configured", $"panel:{user.Id}", "server", "Interview with Frevo One configured; prior links revoked.");
        await tx.CommitAsync();
        return (await AccessAsync(id, user)).Session;
    }

    public async Task<InternalInterviewSession> RotateLinkAsync(long id, AuthUser user)
    {
        InternalInterviewRuntimeState.Read(configuration).RequireAdmission();
        await using var db = Db(); await db.OpenAsync();
        var context = await ContextAsync(db, id); await AuthorizeAsync(db, context, user, true);
        Require(UsesFrevoVideo(context), "Enable Interview with Frevo One before issuing an internal link.", 409);
        Require(!Terminal(context.InterviewStatus), "The original interview is closed.", 409);
        var expiry = ScheduleUtc(context.ScheduledEnd, context.TimeZoneId).AddHours(2);
        Require(expiry > Now, "Reschedule the interview before issuing a new link.", 409);
        await using var tx = await db.BeginTransactionAsync();
        var session = await LockedAsync(db, tx, id);
        Require(!Terminal(session.Status) && session.Status != "Live", "Candidate links can be replaced only before the session starts.", 409);
        session.LinkVersion = Guid.NewGuid().ToString("N"); session.LinkExpiresAtUtc = expiry;
        session.ConsentAtUtc = null; session.RecordingConsent = false; session.TranscriptionConsent = false; session.Status = "Scheduled";
        await SaveSessionAsync(db, tx, session);
        await AppendAsync(db, tx, id, Guid.NewGuid().ToString(), "link-issued", $"panel:{user.Id}", "server", "Previous candidate link revoked.");
        await tx.CommitAsync(); return session;
    }

    public async Task ConsentAsync(InterviewAccess access, InterviewConsent consent)
    {
        InternalInterviewRuntimeState.Read(configuration).RequireAdmission();
        Require(access.IsCandidate, "Only the candidate may give candidate consent.", 403);
        await using var db = Db(); await db.OpenAsync(); await using var tx = await db.BeginTransactionAsync();
        var session = await LockedAsync(db, tx, access.Context.InterviewId);
        Require(session.LinkVersion == access.Session.LinkVersion, "The link has been revoked.", 410);
        var context = await ContextAsync(db, access.Context.InterviewId, tx);
        RequireOpen(context, session, Now); ValidateConsent(session, consent);
        Require(Now >= ScheduleUtc(context.ScheduledStart, context.TimeZoneId).AddMinutes(-30), "The waiting room opens 30 minutes before the scheduled start.", 409);
        if (session.ConsentAtUtc is null)
        {
            session.ConsentAtUtc = Now; session.RecordingConsent = consent.Recording; session.TranscriptionConsent = consent.Transcription;
            session.Status = "Waiting";
            await SaveSessionAsync(db, tx, session);
            await AppendAsync(db, tx, session.InterviewId, Guid.NewGuid().ToString(), "consent", "candidate", "server", Json(consent));
        }
        await tx.CommitAsync();
    }

    public async Task<InternalInterviewSession> CommandAsync(long id, InterviewCommand command, AuthUser user)
    {
        if (command.Action == "start") InternalInterviewRuntimeState.Read(configuration).RequireAdmission();
        await using var db = Db(); await db.OpenAsync();
        var context = await ContextAsync(db, id); await AuthorizeAsync(db, context, user);
        await using var tx = await db.BeginTransactionAsync();
        var session = await LockedAsync(db, tx, id);
        Require(session.Revision == command.Revision, "Session changed. Refresh before retrying this action.", 409);
        Require(command.SectionSkill is null || (command.Action == "ai" && session.Questions.Any(q => q.Skill == command.SectionSkill)), "Choose an AI section from this interview's approved question snapshot.");
        session.Status = Transition(context, session, command.Action, Now);
        if (command.Action == "start") { session.StartedAtUtc = Now; session.MediaState = "Starting"; }
        if (command.Action == "ai") session.Control = "AI";
        if (command.Action == "human") session.Control = "Human";
        if (Terminal(session.Status)) { session.EndedAtUtc = Now; session.RetainUntilUtc = Now.AddDays(RetentionDays); if (session.MediaState != "None") session.MediaState = "Closing"; }
        await SaveSessionAsync(db, tx, session);
        await AppendAsync(db, tx, id, Guid.NewGuid().ToString(), "session-" + command.Action, $"panel:{user.Id}",
            command.SectionSkill is null ? "server" : "panel-section", command.SectionSkill ?? "Session action only; hiring result unchanged.");
        await tx.CommitAsync(); return session;
    }

    public async Task<List<InternalInterviewEvent>> EventsAsync(InterviewAccess access, long after = 0)
    {
        Require(after >= 0, "Invalid timeline cursor.");
        await using var db = Db(); await db.OpenAsync();
        return (await db.QueryAsync<InternalInterviewEvent>("""
            SELECT * FROM recruitment_internal_interview_events WHERE InterviewId=@Id AND Id>@After
            AND (@Candidate=FALSE OR Kind IN ('question','answer','transcript','introduction','answer-timeout','questions-finished','session-start','session-complete','session-ai','session-human','session-closed-by-server'))
            ORDER BY Id LIMIT 500
            """, new { Id = access.Context.InterviewId, After = after, Candidate = access.IsCandidate })).ToList();
    }

    public async Task<InternalInterviewEvent> AddEventAsync(InterviewAccess access, InterviewEventInput input)
    {
        await using var db = Db(); await db.OpenAsync(); await using var tx = await db.BeginTransactionAsync();
        var session = await LockedAsync(db, tx, access.Context.InterviewId);
        if (access.IsCandidate) Require(session.LinkVersion == access.Session.LinkVersion, "The candidate link has been revoked.", 410);
        var context = await ContextAsync(db, session.InterviewId, tx);
        var currentAccess = new InterviewAccess(context, session, access.User);
        ValidateClientEvent(currentAccess, input, Now);
        var existing = await db.QuerySingleOrDefaultAsync<InternalInterviewEvent>("SELECT * FROM recruitment_internal_interview_events WHERE InterviewId=@Id AND EventKey=@Key", new { Id = session.InterviewId, Key = input.EventKey }, tx);
        if (existing is not null)
        {
            Require(existing.Actor == access.Actor && existing.Kind == input.Kind && existing.Text == input.Text && existing.QuestionEventId == input.QuestionEventId,
                "The event key was already used for a different event.", 409);
            await tx.CommitAsync(); return existing;
        }
        if (input.Kind == "answer")
        {
            Require(session.TranscriptionConsent && session.Configuration.TranscriptionEnabled, "Answer storage requires transcription consent.", 403);
            var questionId = await db.ExecuteScalarAsync<long?>("SELECT MAX(Id) FROM recruitment_internal_interview_events WHERE InterviewId=@Id AND Kind='question'", new { Id = session.InterviewId }, tx);
            Require(questionId == input.QuestionEventId, "Only the current question may receive an answer.", 409);
            Require(!await db.ExecuteScalarAsync<bool>("SELECT COUNT(*) FROM recruitment_internal_interview_events WHERE InterviewId=@Id AND Kind='answer' AND QuestionEventId=@Question", new { Id = session.InterviewId, Question = questionId }, tx),
                "An answer is already saved for this question. Wait for the next question.", 409);
            var askedAt = await db.ExecuteScalarAsync<DateTime>("SELECT CreatedAtUtc FROM recruitment_internal_interview_events WHERE Id=@Question", new { Question = questionId }, tx);
            var voiceAt = await db.ExecuteScalarAsync<DateTime?>("SELECT MIN(CreatedAtUtc) FROM recruitment_internal_interview_events WHERE InterviewId=@Id AND Kind='voice-parsing' AND QuestionEventId=@Question", new { Id = session.InterviewId, Question = questionId }, tx);
            Require(Now <= AnswerDeadline(askedAt, session.Configuration.AnswerSeconds, voiceAt), "The answer window has ended. Ask the panel to allow another question.", 409);
        }
        if (input.Kind == "question")
        {
            Require(session.Control == "Human", "Resume human control before asking an instant question.", 409);
            Require(input.Text.Length <= 2000, "Questions may contain at most 2000 characters.");
            Require(await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_internal_interview_events WHERE InterviewId=@Id AND Kind='question' AND QuestionEventId IS NULL", new { Id = session.InterviewId }, tx) < session.Configuration.MaxQuestions,
                "The configured question limit has been reached.", 409);
        }
        // Serialize admission per interview. Prevent unbounded self-reported event/PII storage.
        var count = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_internal_interview_events WHERE InterviewId=@Id", new { Id = session.InterviewId }, tx);
        Require(count < 10000, "The session event limit has been reached. Contact HR.", 429);
        var recent = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_internal_interview_events WHERE InterviewId=@Id AND Actor=@Actor AND CreatedAtUtc>=@Since", new { Id = session.InterviewId, access.Actor, Since = Now.AddMinutes(-1) }, tx);
        Require(recent < 120, "Too many session events; retry shortly.", 429);
        var eventId = await AppendAsync(db, tx, session.InterviewId, input.EventKey, input.Kind, access.Actor, access.IsCandidate ? "candidate-reported" : "panel", input.Text, input.QuestionEventId, input.OffsetMs);
        var result = await db.QuerySingleAsync<InternalInterviewEvent>("SELECT * FROM recruitment_internal_interview_events WHERE Id=@Id", new { Id = eventId }, tx);
        await tx.CommitAsync(); return result;
    }

    internal static Task<InternalInterviewSession> LockedAsync(MySqlConnection db, IDbTransaction tx, long id) =>
        db.QuerySingleAsync<InternalInterviewSession>("SELECT * FROM recruitment_internal_interview_sessions WHERE InterviewId=@Id FOR UPDATE", new { Id = id }, tx);
    internal static async Task SaveSessionAsync(MySqlConnection db, IDbTransaction tx, InternalInterviewSession session)
    {
        session.Revision++;
        await db.ExecuteAsync("""
            UPDATE recruitment_internal_interview_sessions SET Status=@Status,Control=@Control,LinkVersion=@LinkVersion,
            LinkExpiresAtUtc=@LinkExpiresAtUtc,ConsentAtUtc=@ConsentAtUtc,RecordingConsent=@RecordingConsent,
            TranscriptionConsent=@TranscriptionConsent,StartedAtUtc=@StartedAtUtc,EndedAtUtc=@EndedAtUtc,
            RetainUntilUtc=@RetainUntilUtc,MediaState=@MediaState,MediaError=@MediaError,
            Revision=@Revision,UpdatedAtUtc=UTC_TIMESTAMP(6) WHERE InterviewId=@InterviewId
            """, session, tx);
    }
    internal static Task<long> AppendAsync(MySqlConnection db, IDbTransaction tx, long id, string key, string kind, string actor, string source, string text, long? question = null, long? offset = null) =>
        db.ExecuteScalarAsync<long>("""
            INSERT INTO recruitment_internal_interview_events (InterviewId,EventKey,Kind,Actor,Source,Text,QuestionEventId,OffsetMs,CreatedAtUtc)
            VALUES (@Id,@Key,@Kind,@Actor,@Source,@Text,@Question,@Offset,UTC_TIMESTAMP(6)); SELECT LAST_INSERT_ID();
            """, new { Id = id, Key = key, Kind = kind, Actor = actor, Source = source, Text = text, Question = question, Offset = offset }, tx);

    private sealed class PositionScope
    {
        public int ClientId { get; set; }
        public string JobLocation { get; set; } = "";
    }
    private sealed class DashboardScope
    {
        public long InterviewId { get; set; }
        public int ClientId { get; set; }
        public string JobLocation { get; set; } = "";
    }
}
