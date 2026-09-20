using System.Text.Json;
using Dapper;
using Payroll.API.Models;
using Payroll.API.Services;
using static Payroll.API.Services.InternalInterviewPolicy;

namespace Payroll.API.Repositories;

public sealed partial class InternalInterviewRepository
{
    internal async Task<List<long>> PendingLifecycleAsync()
    {
        await using var db = Db();
        return (await db.QueryAsync<long>("""
            SELECT s.InterviewId FROM recruitment_internal_interview_sessions s
            WHERE s.MediaState<>'Purged' AND (s.Status IN ('Scheduled','Waiting','Live')
                OR s.MediaState IN ('Starting','Closing') OR s.RetainUntilUtc<@Now
                OR EXISTS(SELECT 1 FROM recruitment_internal_interview_recordings r WHERE r.InterviewId=s.InterviewId AND r.Status IN ('Pending','Recording','Finalizing')))
            ORDER BY (s.MediaState='Closing' OR (s.Status IN ('Scheduled','Waiting','Live') AND s.ScheduledEndUtc<=@Now)) DESC,
                s.UpdatedAtUtc,s.InterviewId LIMIT 100
            """, new { Now })).ToList();
    }

    // A DB-specific advisory lock spans network work, not a SQL transaction. Restarts resume from persisted state.
    public async Task ReconcileMediaAsync(long id, InternalInterviewTransport transport, InternalInterviewMediaStore media, CancellationToken ct)
    {
        if (!InternalInterviewRuntimeState.Read(configuration).Maintenance) return;
        await using var db = Db(); await db.OpenAsync(ct);
        var lease = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"hrms-interview:{db.Database}:{id}")));
        if (!await db.ExecuteScalarAsync<bool>("SELECT GET_LOCK(@Lease,0)", new { Lease = lease })) return;
        try
        {
            var session = await db.QuerySingleOrDefaultAsync<InternalInterviewSession>("SELECT * FROM recruitment_internal_interview_sessions WHERE InterviewId=@Id", new { Id = id });
            if (session is null || session.MediaState == "Purged") return;
            InternalInterviewContext? context = null;
            try { context = await ContextAsync(db, id); } catch (InternalInterviewException error) when (error.StatusCode == 404) { }
            var runtime = InternalInterviewRuntimeState.Read(configuration);
            var disabled = !runtime.Enabled;
            var externalDelivery = context is not null && !UsesFrevoVideo(context);
            var invalid = disabled || externalDelivery || context is null || Terminal(context.InterviewStatus)
                || session.ScheduledEndUtc == default || session.ScheduledEndUtc <= Now
                || session.ScheduledStartUtc != ScheduleUtc(context.ScheduledStart, context.TimeZoneId)
                || session.ScheduledEndUtc != ScheduleUtc(context.ScheduledEnd, context.TimeZoneId);
            if (invalid && !Terminal(session.Status))
            {
                await using var tx = await db.BeginTransactionAsync(ct);
                session = await LockedAsync(db, tx, id);
                if (!Terminal(session.Status))
                {
                    var admissionExpired = runtime.Draining && !session.StartedAtUtc.HasValue && session.ScheduledEndUtc <= Now;
                    session.Status = disabled || externalDelivery || admissionExpired ? "Cancelled" : session.StartedAtUtc.HasValue ? "Completed" : (context is null || Terminal(context.InterviewStatus) || session.ScheduledEndUtc > Now ? "Cancelled" : "No Show");
                    session.EndedAtUtc = Now; session.RetainUntilUtc = Now.AddDays(RetentionDays);
                    if (session.MediaState != "None") session.MediaState = "Closing";
                    await SaveSessionAsync(db, tx, session);
                    await AppendAsync(db, tx, id, Guid.NewGuid().ToString(), "session-closed-by-server", "server", "server", disabled
                        ? "Internal interview service disabled by deployment settings; maintenance requested room closure. Hiring result unchanged."
                        : externalDelivery ? "Interview with Frevo One switched off for this schedule; internal room closure requested. External meeting details and hiring result unchanged."
                        : admissionExpired ? "Scheduled window ended during an admission pause. Session cancelled, not marked candidate No Show; hiring result unchanged."
                        : "Scheduled window ended or original schedule closed/changed; hiring result unchanged.");
                }
                await tx.CommitAsync(ct);
            }
            if (session.Status == "Live" && session.MediaState == "Starting" && !invalid)
            {
                if (session.Configuration.RecordingEnabled)
                {
                    media.ValidateRoot();
                    Require(session.RecordingConsent, "Recording consent is missing.", 403);
                    await db.ExecuteAsync("""
                        INSERT IGNORE INTO recruitment_internal_interview_recordings (Id,InterviewId,Status,StorageKey,ContentType,StartedAtUtc,CreatedByUserId)
                        VALUES (@RoomId,@InterviewId,'Pending',@Key,'video/mp4',@StartedAtUtc,@CreatedByUserId)
                        """, new { session.RoomId, session.InterviewId, Key = session.RoomId + ".mp4", session.StartedAtUtc, session.CreatedByUserId });
                }
                await transport.CreateRoomAsync(session, context!.PanelUserIds.Count, ct);
                // Completion may race a slow CreateRoom. Never reopen grants after a terminal action.
                await db.ExecuteAsync("""
                    UPDATE recruitment_internal_interview_sessions SET MediaState=IF(Status='Live','Ready','Closing'),MediaError='',UpdatedAtUtc=UTC_TIMESTAMP(6)
                    WHERE InterviewId=@Id AND MediaState='Starting'
                    """, new { Id = id });
                session = await db.QuerySingleAsync<InternalInterviewSession>("SELECT * FROM recruitment_internal_interview_sessions WHERE InterviewId=@Id", new { Id = id });
            }
            if (session.MediaState == "Closing")
            {
                await transport.CloseRoomAsync(session.RoomId, ct);
                await db.ExecuteAsync("UPDATE recruitment_internal_interview_sessions SET MediaState='Closed',MediaError='',UpdatedAtUtc=UTC_TIMESTAMP(6) WHERE InterviewId=@Id AND MediaState='Closing'", new { Id = id });
                session.MediaState = "Closed";
            }
            var record = await db.QuerySingleOrDefaultAsync<InternalInterviewRecording>("SELECT * FROM recruitment_internal_interview_recordings WHERE InterviewId=@Id", new { Id = id });
            if (record is not null && record.Status is "Pending" or "Recording" or "Finalizing")
            {
                var response = await transport.RecordingsAsync(session.RoomId, ct);
                if (response.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array && items.GetArrayLength() > 0)
                {
                    Require(items.GetArrayLength() == 1, "Multiple recording segments require administrator review; no file is silently replaced.", 409);
                    var item = items[0];
                    record.EgressId = item.TryGetProperty("egressId", out var camelId) ? camelId.GetString() ?? "" : item.GetProperty("egress_id").GetString() ?? "";
                    var status = item.GetProperty("status").ToString();
                    record.Status = status switch { "EGRESS_ACTIVE" or "1" => "Recording", "EGRESS_ENDING" or "2" => "Finalizing",
                        "EGRESS_COMPLETE" or "3" => "Ready", "EGRESS_FAILED" or "EGRESS_ABORTED" or "EGRESS_LIMIT_REACHED" or "4" or "5" or "6" => "Failed", _ => "Pending" };
                    if (record.Status == "Ready")
                    {
                        var files = item.TryGetProperty("fileResults", out var camelFiles) ? camelFiles : item.GetProperty("file_results");
                        Require(files.ValueKind == JsonValueKind.Array && files.GetArrayLength() == 1, "The finalized recording file is unavailable.", 502);
                        record.SizeBytes = media.GetSize(record.StorageKey);
                        Require(record.SizeBytes > 0 && record.SizeBytes <= media.MaxBytes, "Recording size is invalid or exceeds its configured limit.", 413);
                        static DateTime NanoUtc(JsonElement file, string snake, string camel) => DateTimeOffset.FromUnixTimeMilliseconds(long.Parse((file.TryGetProperty(snake, out var value) ? value : file.GetProperty(camel)).ToString(), System.Globalization.CultureInfo.InvariantCulture) / 1_000_000).UtcDateTime;
                        record.StartedAtUtc = NanoUtc(files[0], "started_at", "startedAt");
                        record.EndedAtUtc = NanoUtc(files[0], "ended_at", "endedAt");
                    }
                }
                else if (Terminal(session.Status) && session.EndedAtUtc < Now.AddMinutes(-5)) record.Status = "Failed";
                await db.ExecuteAsync("UPDATE recruitment_internal_interview_recordings SET Status=@Status,EgressId=@EgressId,SizeBytes=@SizeBytes,StartedAtUtc=@StartedAtUtc,EndedAtUtc=@EndedAtUtc WHERE Id=@Id", record);
            }
            // Deleting an original interview cannot make its retained media publicly accessible.
            // Purge only this interview's known opaque file after the room is confirmed closed.
            if (session.RetainUntilUtc < Now && session.MediaState is "None" or "Closed")
            {
                if (record is not null) await media.DeleteAsync(record.StorageKey, ct);
                await using var tx = await db.BeginTransactionAsync(ct);
                await db.ExecuteAsync("DELETE FROM recruitment_internal_interview_events WHERE InterviewId=@Id; DELETE FROM recruitment_internal_interview_recordings WHERE InterviewId=@Id", new { Id = id }, tx);
                await db.ExecuteAsync("UPDATE recruitment_internal_interview_sessions SET QuestionsJson='[]',MediaState='Purged',MediaError='',UpdatedAtUtc=UTC_TIMESTAMP(6) WHERE InterviewId=@Id", new { Id = id }, tx);
                await AppendAsync(db, tx, id, Guid.NewGuid().ToString(), "retention-purged", "server", "server", "Configured retention elapsed; recording, transcript, notes and AI review deleted.");
                await tx.CommitAsync(ct);
            }
            // Advance the maintenance cursor even for healthy/idle sessions. Otherwise the oldest
            // 100 scheduled sessions can permanently starve later rooms and retention work.
            await db.ExecuteAsync("UPDATE recruitment_internal_interview_sessions SET UpdatedAtUtc=UTC_TIMESTAMP(6) WHERE InterviewId=@Id", new { Id = id });
        }
        catch (InternalInterviewException error)
        {
            await db.ExecuteAsync("UPDATE recruitment_internal_interview_sessions SET MediaError=@Error,UpdatedAtUtc=UTC_TIMESTAMP(6) WHERE InterviewId=@Id", new { Id = id, Error = error.Message[..Math.Min(error.Message.Length, 600)] });
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or FormatException or InvalidOperationException or IOException)
        {
            await db.ExecuteAsync("UPDATE recruitment_internal_interview_sessions SET MediaError='Media processing could not be verified. Check the private media service and shared recording mount; pending work will retry.',UpdatedAtUtc=UTC_TIMESTAMP(6) WHERE InterviewId=@Id", new { Id = id });
        }
        finally { await db.ExecuteAsync("SELECT RELEASE_LOCK(@Lease)", new { Lease = lease }); }
    }

    public async Task<List<InternalInterviewRecording>> RecordingsAsync(InterviewAccess access)
    {
        Require(!access.IsCandidate, "Recording replay requires an authorized HR/panel login.", 403);
        Require(access.Session.RetainUntilUtc > Now && access.Session.MediaState != "Purged", "Session evidence retention has expired.", 410);
        await using var db = Db();
        return (await db.QueryAsync<InternalInterviewRecording>("SELECT * FROM recruitment_internal_interview_recordings WHERE InterviewId=@Id", new { Id = access.Context.InterviewId })).ToList();
    }
}
