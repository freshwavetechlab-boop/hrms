using Dapper;
using Payroll.API.Models;

namespace Payroll.API.Services;

public sealed partial class RecruitmentLifecycleNotificationService
{
    // Existing immutable stage events + existing delivery ledger form a small outbox.
    // All transition entry points (manual, approval callback and worker) are covered.
    public async Task QueuePendingHiringStageUpdatesAsync(CancellationToken cancellationToken)
    {
        await using var db = Db(); await db.OpenAsync(cancellationToken);
        var rows = (await db.QueryAsync<StageNotification>(new CommandDefinition(@"SELECT event.Id,event.EventTitle,
journey.ClientId,journey.PositionId,position.PositionTitle,workOrder.WorkOrderNumber
FROM recruitment_position_stage_events event
JOIN recruitment_position_pipeline_instances journey ON journey.Id=event.PositionPipelineInstanceId
JOIN recruitment_open_positions position ON position.Id=journey.PositionId
JOIN recruitment_work_orders workOrder ON workOrder.Id=journey.WorkOrderId
JOIN recruitment_lifecycle_notification_deliveries checkpoint ON checkpoint.EventKey='HIRING_STAGE_MONITOR_START' AND checkpoint.RecipientEmail=''
WHERE event.EventType IN ('StageMoved','StageMovedBack','RejectedByDivision') AND event.CreatedAtUtc>=checkpoint.CreatedAtUtc
AND NOT EXISTS(SELECT 1 FROM recruitment_lifecycle_notification_deliveries done
 WHERE done.EventKey=CONCAT('HIRING_STAGE:',event.Id) AND done.RecipientEmail='' AND done.Status IN ('Processed','Skipped'))
ORDER BY event.Id LIMIT 100", cancellationToken: cancellationToken))).ToList();
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = $"HIRING_STAGE:{row.Id}";
            var rule = await GetRuleAsync(db, row.ClientId, "HIRING_STAGE_MOVED");
            var recipients = rule is null ? [] : (await ResolveInternalRecipientsAsync(db, row.PositionId, rule))
                .Where(ValidEmail).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (rule is not null && recipients.Count > 0)
            {
                var tokens = new Dictionary<string, string> { ["positionTitle"] = row.PositionTitle,
                    ["workOrderNumber"] = row.WorkOrderNumber, ["stageName"] = row.EventTitle };
                await QueueAsync(db, key, recipients, Render(rule.SubjectTemplate, tokens), Render(rule.InternalBodyTemplate, tokens),
                    "RECRUITMENT_HIRING_STAGE_MOVED", "RecruitmentHiringStageEvent", row.Id.ToString(), row.ClientId,
                    new AuthUser { Id = 0, DisplayName = "Recruitment automation" });
                // Queue failure remains retryable; previously queued recipients are deduplicated.
                var queued = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_lifecycle_notification_deliveries WHERE EventKey=@Key AND RecipientEmail IN @Recipients AND Status='Queued'",
                    new { Key = key, Recipients = recipients });
                if (queued != recipients.Count) continue;
            }
            await db.ExecuteAsync("INSERT IGNORE INTO recruitment_lifecycle_notification_deliveries(EventKey,RecipientEmail,Status) VALUES(@Key,'',@Status)",
                new { Key = key, Status = rule is null || recipients.Count == 0 ? "Skipped" : "Processed" });
        }
    }

    private sealed class StageNotification
    {
        public long Id { get; set; }
        public int ClientId { get; set; }
        public long PositionId { get; set; }
        public string PositionTitle { get; set; } = "";
        public string WorkOrderNumber { get; set; } = "";
        public string EventTitle { get; set; } = "";
    }
}
