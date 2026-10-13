using Dapper;
using MySqlConnector;
using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed class PayrollAdvisorRepository(IConfiguration configuration, PayrollAdvisorService advisor, AuthRepository auth, ILogger<PayrollAdvisorRepository> logger)
{
    private MySqlConnection Db() => new(configuration.GetConnectionString("Default"));
    private const string Select = "SELECT id Id,user_id UserId,client_id ClientId,snapshot_hash SnapshotHash,snapshot_json SnapshotJson,status Status,result_json ResultJson,error Error,created_at CreatedAt,finished_at FinishedAt FROM payroll_config_reviews";
    private const string Schema = """
CREATE TABLE IF NOT EXISTS payroll_config_reviews (
 id varchar(36) NOT NULL PRIMARY KEY,
 user_id int NOT NULL,
 client_id int NOT NULL,
 snapshot_hash varchar(64) NOT NULL,
 snapshot_json longtext NOT NULL,
 status varchar(24) NOT NULL,
 result_json longtext NULL,
 error varchar(1000) NULL,
 created_at datetime(6) NOT NULL,
 started_at datetime(6) NULL,
 finished_at datetime(6) NULL,
 KEY ix_payroll_review_queue (status,created_at),
 KEY ix_payroll_review_owner (user_id,client_id,created_at)
);
CREATE TABLE IF NOT EXISTS payroll_config_review_events (
 id bigint NOT NULL AUTO_INCREMENT PRIMARY KEY,
 review_id varchar(36) NOT NULL,
 user_id int NOT NULL,
 finding_id varchar(140) NOT NULL,
 decision varchar(40) NOT NULL,
 created_at datetime(6) NOT NULL,
 KEY ix_payroll_review_event (review_id,created_at)
);
""";

    public async Task<PayrollReviewJob> QueueAsync(PayrollReviewSnapshot snapshot, AuthUser user, CancellationToken ct)
    {
        var report = PayrollAdvisorService.Check(snapshot);
        await using var db = Db(); await db.OpenAsync(ct);
        if (snapshot.ClientId > 0 && !await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM clients WHERE Id=@Id)", new { Id = snapshot.ClientId })) throw new InvalidOperationException("Select an existing client.");
        if (await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name IN ('payroll_config_reviews','payroll_config_review_events')") != 2)
            await db.ExecuteAsync(Schema);
        var key = $"payroll-advisor-user-{user.Id}";
        if (await db.ExecuteScalarAsync<int>("SELECT GET_LOCK(@key,2)", new { key }) != 1) throw new InvalidOperationException("A review request is already being queued. Try again shortly.");
        try
        {
            var hash = PayrollAdvisorService.Hash(snapshot);
            var pending = await db.QueryFirstOrDefaultAsync<PayrollReviewJob>(Select + " WHERE user_id=@UserId AND client_id=@ClientId AND snapshot_hash=@hash AND status IN ('Queued','Processing') ORDER BY created_at DESC LIMIT 1", new { UserId = user.Id, snapshot.ClientId, hash });
            if (pending is not null) return pending;
            if (await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM payroll_config_reviews WHERE user_id=@Id AND created_at>UTC_TIMESTAMP()-INTERVAL 1 HOUR", new { user.Id }) >= 20)
                throw new InvalidOperationException("Review limit reached. You can request up to 20 reviews per hour.");
            if (await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM payroll_config_reviews WHERE user_id=@Id AND status IN ('Queued','Processing')", new { user.Id }) >= 3)
                throw new InvalidOperationException("Three reviews are already pending. Wait for one to finish.");
            var row = new PayrollReviewJob { Id = Guid.NewGuid().ToString(), UserId = user.Id, ClientId = snapshot.ClientId, SnapshotHash = hash, SnapshotJson = PayrollAdvisorService.Serialize(snapshot), ResultJson = JsonSerializer.Serialize(report, PayrollAdvisorService.Json), CreatedAt = DateTime.UtcNow };
            await db.ExecuteAsync("INSERT INTO payroll_config_reviews(id,user_id,client_id,snapshot_hash,snapshot_json,status,result_json,created_at) VALUES(@Id,@UserId,@ClientId,@SnapshotHash,@SnapshotJson,'Queued',@ResultJson,UTC_TIMESTAMP(6))", row);
            return row;
        }
        finally { await db.ExecuteScalarAsync<int>("SELECT RELEASE_LOCK(@key)", new { key }); }
    }

    public async Task<PayrollReviewJob?> GetAsync(string id, int userId, CancellationToken ct)
    {
        await using var db = Db(); await db.OpenAsync(ct);
        if (!await Exists(db)) return null;
        return await db.QuerySingleOrDefaultAsync<PayrollReviewJob>(Select + " WHERE id=@id AND user_id=@userId", new { id, userId });
    }

    public async Task<List<PayrollReviewJob>> HistoryAsync(int clientId, int userId, CancellationToken ct)
    {
        await using var db = Db(); await db.OpenAsync(ct);
        if (!await Exists(db)) return [];
        return (await db.QueryAsync<PayrollReviewJob>(Select + " WHERE client_id=@clientId AND user_id=@userId ORDER BY created_at DESC LIMIT 10", new { clientId, userId })).ToList();
    }

    public async Task DecideAsync(PayrollReviewJob row, PayrollReviewDecision decision, int userId)
    {
        if (row.Status is not ("NeedsReview" or "Failed") || decision.Decision is not ("ApplyToDraft" or "KeepExisting") || row.SnapshotHash != PayrollAdvisorService.Hash(decision.Snapshot) || row.CreatedAt < DateTime.UtcNow.AddDays(-1))
            throw new InvalidOperationException("This review no longer matches the draft, or is not ready. Run a new review.");
        var report = JsonSerializer.Deserialize<PayrollReviewReport>(row.ResultJson ?? "{}", PayrollAdvisorService.Json);
        var finding = report?.Findings.FirstOrDefault(f => f.Id == decision.FindingId) ?? throw new InvalidOperationException("Finding not found.");
        if (decision.Decision == "ApplyToDraft" && !finding.CanDisableProration) throw new InvalidOperationException("This suggestion needs a manual configuration review; it cannot be applied automatically.");
        await using var db = Db(); await db.OpenAsync();
        await db.ExecuteAsync("INSERT INTO payroll_config_review_events(review_id,user_id,finding_id,decision,created_at) VALUES(@Id,@userId,@FindingId,@Decision,UTC_TIMESTAMP(6))", new { row.Id, userId, decision.FindingId, decision.Decision });
    }

    private static Task<bool> Exists(MySqlConnection db) => db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name='payroll_config_reviews')");

    public async Task<bool> ProcessNextAsync(CancellationToken ct)
    {
        await using var db = Db(); await db.OpenAsync(ct);
        if (!await Exists(db) || await db.ExecuteScalarAsync<int>("SELECT GET_LOCK('payroll-advisor-worker',0)") != 1) return false;
        try
        {
            await db.ExecuteAsync("UPDATE payroll_config_reviews SET status='Failed',error='Review interrupted or timed out. Request a new review; no configuration was changed.',finished_at=UTC_TIMESTAMP(6) WHERE status='Processing' AND started_at<UTC_TIMESTAMP()-INTERVAL 8 MINUTE");
            var job = await db.QueryFirstOrDefaultAsync<PayrollReviewJob>(Select + " WHERE status='Queued' ORDER BY created_at LIMIT 1");
            if (job is null) return false;
            await db.ExecuteAsync("UPDATE payroll_config_reviews SET status='Processing',started_at=UTC_TIMESTAMP(6) WHERE id=@Id AND status='Queued'", job);
            try
            {
                var owner = await auth.GetUserByIdAsync(job.UserId);
                if (owner is null || !PayrollAdvisorService.CanAccess(owner, job.ClientId)) throw new InvalidOperationException("Review access was revoked. No configuration was sent to AI.");
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct); budget.CancelAfter(TimeSpan.FromMinutes(3));
                var snapshot = JsonSerializer.Deserialize<PayrollReviewSnapshot>(job.SnapshotJson, PayrollAdvisorService.Json)!;
                var report = await advisor.ReviewAsync(snapshot, budget.Token);
                await db.ExecuteAsync("UPDATE payroll_config_reviews SET status='NeedsReview',result_json=@result,finished_at=UTC_TIMESTAMP(6) WHERE id=@Id AND status='Processing'", new { job.Id, result = JsonSerializer.Serialize(report, PayrollAdvisorService.Json) });
            }
            catch (Exception error)
            {
                logger.LogWarning(error, "Payroll advisor review {ReviewId} failed; no configuration was changed.", job.Id);
                var message = error is OperationCanceledException ? "AI review timed out or was interrupted. Request a new review." : error is InvalidOperationException ? error.Message : "AI review failed. Check AI Integrations and retry. No configuration was changed.";
                await db.ExecuteAsync("UPDATE payroll_config_reviews SET status='Failed',error=@message,finished_at=UTC_TIMESTAMP(6) WHERE id=@Id AND status='Processing'", new { job.Id, message });
            }
            return true;
        }
        finally { await db.ExecuteScalarAsync<int>("SELECT RELEASE_LOCK('payroll-advisor-worker')"); }
    }
}
