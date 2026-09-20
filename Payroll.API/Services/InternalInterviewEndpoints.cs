using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Services;

public static class InternalInterviewEndpoints
{
    internal static void MapInternalInterviewMediaEvents(this WebApplication app)
    {
        // Separate machine-authenticated endpoint. Never use candidate-link/admin-cookie authorization here.
        app.MapPost("/api/public/internal-interviews/media-events", async (InternalInterviewRepository repository, HttpContext context, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!InternalInterviewRuntimeState.Read(app.Configuration).Maintenance) return Results.NotFound();
            try
            {
                var options = app.Configuration.GetSection("InternalInterviews").Get<InternalInterviewOptions>() ?? new();
                InternalInterviewPolicy.Require(options.LiveKitWebhooksEnabled, "Media callbacks are disabled.", 503);
                InternalInterviewPolicy.Require(context.Request.Headers.Authorization.Count == 1, "Media callback authorization is required.", 401);
                InternalInterviewPolicy.Require(context.Request.ContentType?.Split(';')[0] is "application/webhook+json" or "application/json", "Unsupported media callback content type.", 415);
                InternalInterviewPolicy.Require(context.Request.ContentLength is null or <= InternalInterviewMediaWebhook.MaxBodyBytes, "Media callback exceeds its allowed size.", 413);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(10));
                var bytes = await InternalInterviewSpeechService.ReadBoundedAsync(context.Request.Body, InternalInterviewMediaWebhook.MaxBodyBytes, deadline.Token);
                var mediaEvent = InternalInterviewMediaWebhook.Verify(bytes, context.Request.Headers.Authorization.ToString(), options, DateTimeOffset.UtcNow);
                if (mediaEvent is not null) await repository.SaveMediaEventAsync(mediaEvent, deadline.Token);
                return Results.NoContent(); // never disclose whether an opaque room exists
            }
            catch (InternalInterviewException error) { return Results.Json(new { error = error.Message }, statusCode: error.StatusCode); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Results.StatusCode(408); }
        });
    }

    public static void MapInternalInterviews(this WebApplication app)
    {
        app.MapInternalInterviewMediaEvents();
        object Describe(InterviewAccess access) => View(access, InternalInterviewRuntimeState.Read(app.Configuration).Draining);
        var admin = app.MapGroup("/api/recruitment/internal-interviews").AddEndpointFilter<InternalInterviewFilter>();
        var candidate = app.MapGroup("/api/public/internal-interviews").AddEndpointFilter<InternalInterviewFilter>();
        candidate.MapPost("/{id:long}/speech/transcribe/{questionId:long}", async (long id, long questionId, InternalInterviewRepository repository,
            InternalInterviewLinks links, InternalInterviewSpeechService speech, HttpContext context, CancellationToken ct) =>
        {
            var access = await CandidateAccess(id, repository, links, context);
            InternalInterviewPolicy.RequireOpen(access.Context, access.Session, DateTime.UtcNow);
            InternalInterviewPolicy.Require(access.Session.Status == "Live" && access.Session.TranscriptionConsent, "Live session and transcription consent are required.", 403);
            var currentQuestion = await repository.CurrentQuestionAsync(access);
            InternalInterviewPolicy.Require(currentQuestion?.Id == questionId, "Only the current question may receive voice input.", 409);
            InternalInterviewPolicy.Require(context.Request.ContentLength is > 0 and <= InternalInterviewSpeechService.MaxAudioBytes
                && context.Request.ContentType?.Split(';')[0] is "audio/webm" or "audio/ogg" or "audio/wav" or "audio/mp4", "Choose supported audio up to 12 MB.", 413);
            var bytes = await InternalInterviewSpeechService.ReadBoundedAsync(context.Request.Body, InternalInterviewSpeechService.MaxAudioBytes, ct);
            await repository.BeginTranscriptionAsync(access, questionId);
            using var content = new ByteArrayContent(bytes); content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(context.Request.ContentType!.Split(';')[0]);
            var result = await speech.SendAsync(id, "transcribe", content, access.Session.Configuration.Language, ct);
            var fresh = await CandidateAccess(id, repository, links, context);
            InternalInterviewPolicy.RequireOpen(fresh.Context, fresh.Session, DateTime.UtcNow);
            return Results.Ok(new { text = InternalInterviewSpeechService.ParseTranscript(result), draft = true, message = "Review/edit this transcript, then submit your answer." });
        });
        candidate.MapGet("/{id:long}/speech/question/{eventId:long}", async (long id, long eventId, InternalInterviewRepository repository,
            InternalInterviewLinks links, InternalInterviewSpeechService speech, HttpContext context, CancellationToken ct) =>
        {
            var access = await CandidateAccess(id, repository, links, context);
            InternalInterviewPolicy.RequireOpen(access.Context, access.Session, DateTime.UtcNow);
            InternalInterviewPolicy.Require(access.Session.Status == "Live" && access.Session.TranscriptionConsent, "A consented live session is required.", 403);
            var question = await repository.SpokenQuestionAsync(access, eventId);
            using var content = JsonContent.Create(new { text = question.Text });
            var bytes = await speech.SendAsync(id, "synthesize", content, access.Session.Configuration.Language, ct);
            return Results.Bytes(bytes, "audio/wav");
        });
        admin.MapGet("/capabilities", (HttpContext context) =>
        {
            var user = User(context);
            return InternalInterviewPolicy.CanManage(user) || InternalInterviewPolicy.Has(user, "recruitment.interview.panel")
                ? Results.Ok(new { enabled = true, canManage = InternalInterviewPolicy.CanManage(user),
                    acceptingNewSessions = InternalInterviewRuntimeState.Read(app.Configuration).AcceptingNewSessions }) : Results.StatusCode(403);
        });
        admin.MapGet("/{id:long}/context", async (long id, InternalInterviewRepository repository, HttpContext context) =>
            Results.Ok(await repository.GetContextAsync(id, User(context))));
        admin.MapGet("/dashboard", async (InternalInterviewRepository repository, RecruitmentTalentRepository interviews, HttpContext context) =>
        {
            var user = User(context);
            InternalInterviewPolicy.Require(InternalInterviewPolicy.CanManage(user) || InternalInterviewPolicy.Has(user, "recruitment.interview.panel"), "Interview access is required.", 403);
            var scoped = await interviews.GetInterviewsAsync(user);
            var rows = await repository.ScopedDashboardAsync(scoped.Select(i => i.Id), user);
            return Results.Ok(rows.Select(s => { var i = scoped.First(x => x.Id == s.InterviewId); return new { s.InterviewId, i.CandidateName, i.PositionTitle,
                i.RoundCode, s.ScheduledStartUtc, s.ScheduledEndUtc, s.Status, mode = s.Configuration.Mode, s.MediaState, s.MediaError }; }));
        });
        admin.MapGet("/questions", async (long positionId, InternalInterviewRepository repository, HttpContext context) =>
            Results.Ok(await repository.QuestionsAsync(positionId, User(context))));
        admin.MapPost("/questions", async (InterviewQuestion question, InternalInterviewRepository repository, HttpContext context) =>
            Results.Ok(await repository.SaveQuestionAsync(question, User(context))));
        admin.MapPost("/{id:long}/configuration", async (long id, InternalInterviewConfiguration request, InternalInterviewRepository repository, HttpContext context) =>
        {
            await repository.ConfigureAsync(id, request, User(context));
            return Results.Ok(Describe(await repository.AccessAsync(id, User(context))));
        });
        admin.MapGet("/{id:long}", async (long id, InternalInterviewRepository repository, HttpContext context) => Results.Ok(Describe(await repository.AccessAsync(id, User(context)))));
        admin.MapPost("/{id:long}/schedule-reset", async (long id, InterviewScheduleReset request, InternalInterviewRepository repository, HttpContext context) =>
        {
            await repository.ResetUnstartedScheduleAsync(id, request.Revision, User(context));
            return Results.Ok(Describe(await repository.AccessAsync(id, User(context))));
        });
        admin.MapPost("/{id:long}/link", async (long id, InternalInterviewRepository repository, InternalInterviewLinks links, HttpContext context) =>
        {
            // Every issuance explicitly invalidates the previous candidate credential.
            _ = links.PortalOrigin(); // Validate configuration before revoking a working link.
            var session = await repository.RotateLinkAsync(id, User(context));
            return Results.Ok(links.Create(session));
        });
        admin.MapPost("/{id:long}/command", async (long id, InterviewCommand request, InternalInterviewRepository repository,
            InternalInterviewTransport transport, InternalInterviewMediaStore media, HttpContext context, CancellationToken ct) =>
        {
            var session = await repository.CommandAsync(id, request, User(context));
            await repository.ReconcileMediaAsync(id, transport, media, ct);
            return Results.Ok(Describe(await repository.AccessAsync(id, User(context))));
        });
        admin.MapGet("/{id:long}/events", async (long id, long? after, InternalInterviewRepository repository, HttpContext context) =>
            Results.Ok(await repository.EventsAsync(await repository.AccessAsync(id, User(context)), after ?? 0)));
        admin.MapPost("/{id:long}/events", async (long id, InterviewEventInput request, InternalInterviewRepository repository, HttpContext context) =>
            Results.Ok(await repository.AddEventAsync(await repository.AccessAsync(id, User(context)), request)));
        admin.MapPost("/{id:long}/join", async (long id, InternalInterviewRepository repository, InternalInterviewTransport transport, HttpContext context) =>
            Results.Ok(transport.Join(await repository.AccessAsync(id, User(context)))));
        admin.MapPost("/{id:long}/ai-retry", async (long id, InternalInterviewRepository repository, HttpContext context) =>
        { await repository.RetryAiAsync(id, User(context)); return Results.Ok(new { message = "AI retry requested. Saved evidence and hiring result are unchanged." }); });
        admin.MapGet("/{id:long}/recordings", async (long id, InternalInterviewRepository repository, HttpContext context) =>
            Results.Ok((await repository.RecordingsAsync(await repository.AccessAsync(id, User(context)))).Select(r => new { r.Id, r.InterviewId, r.Status, r.ContentType, r.SizeBytes, r.StartedAtUtc, r.EndedAtUtc })));
        admin.MapGet("/{id:long}/recordings/{recordingId}", async (long id, string recordingId, InternalInterviewRepository repository, InternalInterviewMediaStore media, HttpContext context, CancellationToken ct) =>
        {
            var record = (await repository.RecordingsAsync(await repository.AccessAsync(id, User(context)))).SingleOrDefault(r => r.Id == recordingId);
            InternalInterviewPolicy.Require(record is not null && record.Status == "Ready", "The recording is not ready or is unavailable in your permitted scope.", 404);
            var handle = await media.OpenAsync(record!.StorageKey, ct);
            context.Response.RegisterForDisposeAsync(handle);
            return Results.Stream(handle.Stream, record.ContentType, enableRangeProcessing: true);
        });

        candidate.MapGet("/{id:long}", async (long id, InternalInterviewRepository repository, InternalInterviewLinks links, HttpContext context) =>
            Results.Ok(Describe(await CandidateAccess(id, repository, links, context))));
        candidate.MapPost("/{id:long}/consent", async (long id, InterviewConsent request, InternalInterviewRepository repository, InternalInterviewLinks links, HttpContext context) =>
        {
            await repository.ConsentAsync(await CandidateAccess(id, repository, links, context), request);
            return Results.Ok(Describe(await CandidateAccess(id, repository, links, context)));
        });
        candidate.MapGet("/{id:long}/events", async (long id, long? after, InternalInterviewRepository repository, InternalInterviewLinks links, HttpContext context) =>
            Results.Ok(await repository.EventsAsync(await CandidateAccess(id, repository, links, context), after ?? 0)));
        candidate.MapPost("/{id:long}/events", async (long id, InterviewEventInput request, InternalInterviewRepository repository, InternalInterviewLinks links, HttpContext context) =>
            Results.Ok(await repository.AddEventAsync(await CandidateAccess(id, repository, links, context), request)));
        candidate.MapPost("/{id:long}/join", async (long id, InternalInterviewRepository repository, InternalInterviewLinks links, InternalInterviewTransport transport, HttpContext context) =>
            Results.Ok(transport.Join(await CandidateAccess(id, repository, links, context))));
    }

    internal static AuthUser User(HttpContext context) => context.Items["User"] as AuthUser ?? throw new InternalInterviewException(401, "Sign in to access the interview.");
    internal static Task<InterviewAccess> CandidateAccess(long id, InternalInterviewRepository repository, InternalInterviewLinks links, HttpContext context) =>
        repository.AccessAsync(id, null, links.Read(context.Request.Headers["X-Interview-Token"].FirstOrDefault()));
    internal static object View(InterviewAccess access, bool draining = false)
    {
        var s = access.Session; var c = access.Context;
        // Never send the entire session model: it includes revocation state, rubric and future questions.
        return new
        {
            interviewId = c.InterviewId, c.ApplicationId, c.CandidateName, c.PositionTitle, c.RoundCode,
            c.ScheduledStart, c.ScheduledEnd, c.TimeZoneId, s.Status, s.Control, s.Revision, s.StartedAtUtc, s.EndedAtUtc,
            s.ConsentAtUtc, s.RecordingConsent, s.TranscriptionConsent,
            s.MediaState, s.MediaError, s.RetainUntilUtc,
            configuration = new { s.Configuration.Mode, s.Configuration.Language, s.Configuration.Difficulty, s.Configuration.MaxQuestions,
                s.Configuration.AnswerSeconds, s.Configuration.FollowUpLimit, s.Configuration.RecordingEnabled, s.Configuration.TranscriptionEnabled },
            noticeVersion = InternalInterviewPolicy.ConsentNoticeVersion,
            questions = access.IsCandidate ? null : s.Questions,
            canManage = access.User is not null && InternalInterviewPolicy.CanManage(access.User),
            canResetSchedule = access.User is not null && InternalInterviewPolicy.CanManage(access.User)
                && InternalInterviewPolicy.CanResetUnstartedSchedule(c, s, DateTime.UtcNow),
            isCandidate = access.IsCandidate, draining
        };
    }
}

public sealed class InternalInterviewFilter(IConfiguration configuration) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        context.HttpContext.Response.Headers["Referrer-Policy"] = "no-referrer";
        if (!configuration.GetValue("InternalInterviews:Enabled", false))
            return Results.Json(new { error = "Internal interviews are not enabled on this API." }, statusCode: 503);
        try { return await next(context); }
        catch (InternalInterviewException error) { return Results.Json(new { error = error.Message }, statusCode: error.StatusCode); }
        catch (MySqlConnector.MySqlException error) when (error.Number == 1146)
        { return Results.Json(new { error = "Internal interview storage is not installed. Ask the administrator to run the additive interview migration." }, statusCode: 503); }
    }
}
