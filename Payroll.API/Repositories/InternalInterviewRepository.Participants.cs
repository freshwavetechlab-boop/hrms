using System.Text.Json;
using Dapper;
using Payroll.API.Models;
using Payroll.API.Services;
using static Payroll.API.Services.InternalInterviewPolicy;

namespace Payroll.API.Repositories;

public sealed partial class InternalInterviewRepository
{
    // A short JWT only governs joining, not an already connected participant. Recheck current RBAC.
    internal async Task ReconcileParticipantsAsync(long id, InternalInterviewTransport transport, Func<int, Task<AuthUser?>> loadUser, CancellationToken ct)
    {
        await using var db = Db(); await db.OpenAsync(ct);
        var session = await db.QuerySingleOrDefaultAsync<InternalInterviewSession>("SELECT * FROM recruitment_internal_interview_sessions WHERE InterviewId=@Id AND Status='Live' AND MediaState='Ready'", new { Id = id });
        if (session is null) return;
        var lease = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"hrms-interview-membership:{db.Database}:{id}")));
        if (!await db.ExecuteScalarAsync<bool>("SELECT GET_LOCK(@Lease,0)", new { Lease = lease })) return;
        try
        {
            var context = await ContextAsync(db, id); RequireOpen(context, session, Now);
            var response = await transport.ParticipantsAsync(session.RoomId, ct);
            Require(response.ValueKind == JsonValueKind.Object && response.TryGetProperty("participants", out var rows)
                && rows.ValueKind == JsonValueKind.Array && rows.GetArrayLength() <= 30, "Participant access verification returned an invalid room response.", 502);
            foreach (var participant in response.GetProperty("participants").EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();
                var identity = participant.GetProperty("identity").GetString() ?? "";
                Require(identity.Length is > 0 and <= 256, "Participant access verification returned an invalid identity.", 502);
                // Server-reported Egress participants are not human panel identities. No client receives this grant.
                var recorder = participant.TryGetProperty("kind", out var kind) && kind.ToString() is "EGRESS" or "2";
                if (recorder && session.RecordingConsent && session.Configuration.RecordingEnabled) continue;
                if (identity == "candidate" && session.ConsentAtUtc.HasValue) continue;
                var allowed = false;
                if (identity.StartsWith("panel-", StringComparison.Ordinal) && int.TryParse(identity.AsSpan(6), out var userId) && identity == "panel-" + userId)
                {
                    var user = await loadUser(userId);
                    if (user is not null && CanAccess(user, context))
                        allowed = await RecruitmentAccessScope.CanAccessLocationAsync(db, user, context.ClientId, context.JobLocation);
                }
                if (allowed) continue;
                await transport.RemoveParticipantAsync(session.RoomId, identity, ct);
                await using var tx = await db.BeginTransactionAsync(ct);
                var fresh = await LockedAsync(db, tx, id);
                if (fresh.MediaState != "Purged")
                    await AppendAsync(db, tx, id, Guid.NewGuid().ToString(), "participant-access-removed", "server", "server", "Participant removed after current role/client/location check: " + identity);
                await tx.CommitAsync(ct);
            }
            await db.ExecuteAsync("UPDATE recruitment_internal_interview_sessions SET MediaError='' WHERE InterviewId=@Id AND MediaError LIKE 'Participant access verification%'", new { Id = id });
        }
        catch (Exception error) when (error is InternalInterviewException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            await db.ExecuteAsync("UPDATE recruitment_internal_interview_sessions SET MediaError='Participant access verification is pending. Check media connectivity; the server will retry.' WHERE InterviewId=@Id AND Status='Live'", new { Id = id });
        }
        finally { await db.ExecuteAsync("SELECT RELEASE_LOCK(@Lease)", new { Lease = lease }); }
    }
}
