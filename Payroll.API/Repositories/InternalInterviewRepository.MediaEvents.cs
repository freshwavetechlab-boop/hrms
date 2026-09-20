using Dapper;
using Payroll.API.Models;
using Payroll.API.Services;
using static Payroll.API.Services.InternalInterviewPolicy;

namespace Payroll.API.Repositories;

public sealed partial class InternalInterviewRepository
{
    internal async Task SaveMediaEventAsync(InterviewMediaEvent value, CancellationToken ct)
    {
        await using var db = Db(); await db.OpenAsync(ct); await using var tx = await db.BeginTransactionAsync(ct);
        var session = await db.QuerySingleOrDefaultAsync<InternalInterviewSession>(
            "SELECT * FROM recruitment_internal_interview_sessions WHERE RoomId=@Room FOR UPDATE", new { Room = value.RoomId }, tx);
        // A late retry must never restore purged evidence, follow a rotated room, or create a new session.
        if (session is null || session.MediaState == "Purged" || session.RetainUntilUtc is null || session.RetainUntilUtc <= Now
            || !session.ConsentAtUtc.HasValue || !session.StartedAtUtc.HasValue || value.OccurredAtUtc < session.StartedAtUtc.Value.AddMinutes(-1)) return;
        var key = InternalInterviewMediaWebhook.EventKey(value);
        var text = Json(new { value.EventType, value.Identity, value.ParticipantSessionId, value.Role, value.OccurredAtUtc });
        var previous = await db.QuerySingleOrDefaultAsync<InternalInterviewEvent>(
            "SELECT * FROM recruitment_internal_interview_events WHERE InterviewId=@Id AND EventKey=@Key", new { Id = session.InterviewId, Key = key }, tx);
        if (previous is not null)
        {
            Require(previous.Kind == "media-presence" && previous.Text == text, "Media callback ID was reused for different evidence.", 409);
            return;
        }
        await AppendAsync(db, tx, session.InterviewId, key, "media-presence", "media-server", "livekit-webhook", text);
        await tx.CommitAsync(ct);
    }
}
