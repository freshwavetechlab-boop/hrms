using System.Text.RegularExpressions;
using Dapper;
using MySqlConnector;

namespace Payroll.API.Repositories;

public partial class PayRunRepository
{
    private sealed class RecaptureMailRow { public long Id { get; set; } public string Hash { get; set; } = ""; }
    private sealed class RecaptureMailLink { public long Id { get; set; } public long EntityAttachmentId { get; set; } }

    private static async Task<string> ReadRecaptureMailWitnessAsync(MySqlConnection db, MySqlTransaction tx, int clientId, string batchId)
    {
        const int bound = 100000;
        var names = new[] { "notification_queue", "notification_logs", "notification_queue_attachments", "entity_attachments" };
        var columns = new Dictionary<string, string[]>();
        foreach (var name in names)
        {
            var list = (await db.QueryAsync<string>("SELECT COLUMN_NAME FROM information_schema.columns WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME=@Name ORDER BY ORDINAL_POSITION", new { Name = name }, tx)).ToArray();
            if (list.Any(c => !Regex.IsMatch(c, "^[A-Za-z0-9_]+$", RegexOptions.CultureInvariant)))
                throw new InvalidOperationException("Mail-history schema cannot be safely attested; no recapture was saved.");
            columns[name] = list;
        }
        if (columns["notification_queue"].Length == 0)
        {
            if (columns["notification_logs"].Length > 0 || columns["notification_queue_attachments"].Length > 0)
                throw new InvalidOperationException("Mail-history queue metadata is missing; no recapture was saved.");
            return CurrencyHash(columns);
        }

        // Digest the complete existing row, not only a status subset. No SMTP
        // configuration or unselected/global queue rows are read or locked.
        async Task<RecaptureMailRow[]> Digest(string table, string alias, string fromAndWhere, object parameters)
        {
            var row = "JSON_OBJECT(" + string.Join(",", columns[table].Select(c => $"'{c}',{alias}.`{c}`")) + ")";
            var result = (await db.QueryAsync<RecaptureMailRow>($"SELECT {alias}.Id,SHA2(CAST({row} AS CHAR),256) Hash {fromAndWhere} ORDER BY {alias}.Id LIMIT {bound + 1}", parameters, tx)).ToArray();
            if (result.Length > bound) throw new InvalidOperationException("This batch's complete mail history exceeds the reviewed recapture bound; no rows were omitted or changed.");
            return result;
        }
        var key = new { ClientId = clientId, BatchId = batchId, Prefix = batchId + ":%" };
        static string Match(string alias) => $"{alias}.ResourceType IN ('ExcelPayslipBatch','ExcelPayslipEmailRequest') AND ({alias}.ResourceId=@BatchId OR {alias}.ResourceId LIKE @Prefix)";
        var queue = await Digest("notification_queue", "q", "FROM notification_queue q WHERE q.ClientId=@ClientId AND " + Match("q"), key);
        RecaptureMailRow[] logs = [], attachments = [], entities = [];
        if (columns["notification_logs"].Length > 0)
            logs = await Digest("notification_logs", "l", "FROM notification_logs l LEFT JOIN notification_queue q ON q.Id=l.QueueId WHERE (q.ClientId=@ClientId OR q.Id IS NULL) AND " + Match("l"), key);
        if (columns["notification_queue_attachments"].Length > 0 && queue.Length > 0)
        {
            var ids = new { Ids = queue.Select(q => q.Id).ToArray() };
            attachments = await Digest("notification_queue_attachments", "a", "FROM notification_queue_attachments a WHERE a.QueueId IN @Ids", ids);
            if (attachments.Length > 0)
            {
                if (columns["entity_attachments"].Length == 0)
                    throw new InvalidOperationException("Existing queued attachment metadata is missing; no recapture was saved.");
                var links = (await db.QueryAsync<RecaptureMailLink>("SELECT Id,EntityAttachmentId FROM notification_queue_attachments WHERE QueueId IN @Ids ORDER BY Id", ids, tx)).ToArray();
                var entityIds = links.Select(a => a.EntityAttachmentId).Distinct().ToArray();
                entities = await Digest("entity_attachments", "e", "FROM entity_attachments e WHERE e.client_id=@ClientId AND e.id IN @Ids", new { ClientId = clientId, Ids = entityIds });
                if (entities.Length != entityIds.Length)
                    throw new InvalidOperationException("A queued document is missing or belongs to a different client; no recapture was saved.");
            }
        }
        return CurrencyHash(new { columns, queue, logs, attachments, entities });
    }
}
