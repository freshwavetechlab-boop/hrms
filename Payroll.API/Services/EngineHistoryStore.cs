using Dapper;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Services;

public sealed class EngineHistoryStore(IConfiguration configuration, IHostEnvironment environment)
{
    public static bool CanRead(AuthUser user) => user.IsActive && user.ClientId is null
        && user.Roles.Contains("super_admin", StringComparer.OrdinalIgnoreCase);
    private readonly string writer = Guid.NewGuid().ToString("N");
    public bool Enabled => configuration.GetValue("EngineHistory:Enabled", true);
    public string Deployment { get; } = ResolveDeployment(configuration, environment);
    private long lastSavedTicks;
    public DateTime? LastSavedAtUtc => Volatile.Read(ref lastSavedTicks) is var ticks && ticks > 0 ? new DateTime(ticks, DateTimeKind.Utc) : null;
    public bool SaveUnavailable { get; set; }
    private bool initialized;
    private DateTime nextCleanup;
    public const string SchemaSql = """
CREATE TABLE IF NOT EXISTS engine_metric_history (
 Deployment VARCHAR(64) NOT NULL, WriterId CHAR(32) NOT NULL,
 EngineCode VARCHAR(40) NOT NULL, BucketUtc DATETIME NOT NULL,
 Completed BIGINT NOT NULL, Failed BIGINT NOT NULL,
 DurationMs DOUBLE NOT NULL, MaxDurationMs DOUBLE NOT NULL,
 BusyMs DOUBLE NOT NULL, ObservedMs DOUBLE NOT NULL,
 PRIMARY KEY (Deployment,WriterId,EngineCode,BucketUtc),
 INDEX ix_engine_history_range (Deployment,BucketUtc)
);
""";
    private static string ResolveDeployment(IConfiguration configuration, IHostEnvironment environment)
    {
        var value = configuration["EngineHistory:Deployment"] ?? (environment.IsDevelopment() ? "development" : OperatingSystem.IsWindows() ? "local-windows" : "production");
        if (value.Length is < 1 or > 64 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            return "unconfigured"; // No startup failure; never copy paths or arbitrary values into telemetry.
        return value;
    }
    private MySqlConnection Connection() => new(configuration.GetConnectionString("Default"));
    private static CommandDefinition Command(string sql, object? args, CancellationToken ct) => new(sql, args, commandTimeout: 8, cancellationToken: ct);

    public async Task SaveAsync(IReadOnlyList<EngineHistoryBucket> rows, CancellationToken ct)
    {
        if (!Enabled) return;
        await using var db = Connection();
        await db.OpenAsync(ct);
        if (!initialized) { await db.ExecuteAsync(Command(SchemaSql, null, ct)); initialized = true; }
        if (rows.Count > 0)
        {
            // A single batch, absolute checkpoints: retrying after an uncertain commit is idempotent.
            var parameters = new DynamicParameters();
            parameters.Add("deployment", Deployment); parameters.Add("writer", writer);
            var values = new List<string>();
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                values.Add($"(@deployment,@writer,@code{i},@time{i},@done{i},@fail{i},@duration{i},@max{i},@busy{i},@observed{i})");
                parameters.Add($"code{i}", row.EngineCode); parameters.Add($"time{i}", row.BucketUtc);
                parameters.Add($"done{i}", row.Completed); parameters.Add($"fail{i}", row.Failed);
                parameters.Add($"duration{i}", row.DurationMs); parameters.Add($"max{i}", row.MaxDurationMs);
                parameters.Add($"busy{i}", row.BusyMs); parameters.Add($"observed{i}", row.ObservedMs);
            }
            await db.ExecuteAsync(Command("INSERT INTO engine_metric_history (Deployment,WriterId,EngineCode,BucketUtc,Completed,Failed,DurationMs,MaxDurationMs,BusyMs,ObservedMs) VALUES "
                + string.Join(',', values) + " ON DUPLICATE KEY UPDATE Completed=VALUES(Completed),Failed=VALUES(Failed),DurationMs=VALUES(DurationMs),MaxDurationMs=VALUES(MaxDurationMs),BusyMs=VALUES(BusyMs),ObservedMs=VALUES(ObservedMs)", parameters, ct));
        }
        Interlocked.Exchange(ref lastSavedTicks, DateTime.UtcNow.Ticks);
        SaveUnavailable = false;
        if (DateTime.UtcNow >= nextCleanup)
        {
            // Deletes only this deployment's expired numeric telemetry, in a bounded batch.
            await db.ExecuteAsync(Command("DELETE FROM engine_metric_history WHERE Deployment=@Deployment AND BucketUtc<@Cutoff LIMIT 5000", new { Deployment, Cutoff = DateTime.UtcNow.AddDays(-30) }, ct));
            nextCleanup = DateTime.UtcNow.AddHours(1);
        }
    }
    public async Task<EngineHistoryResult> ReadAsync(DateTimeOffset from, DateTimeOffset until, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (until <= from || until - from > TimeSpan.FromDays(31) || from.UtcDateTime > now || from.UtcDateTime < now.AddDays(-31))
            throw new ArgumentException("Choose a date range within the last 30 days, no longer than 31 days.");
        var end = until.UtcDateTime < now ? until.UtcDateTime : now;
        var step = end - from.UtcDateTime > TimeSpan.FromDays(2) ? 60 : 5;
        var start = EngineHistoryCollector.Floor(from.UtcDateTime, step);
        await using var db = Connection();
        await db.OpenAsync(ct);
        // Never persists shared ATS snapshots. Only the owning process records actual job attempts.
        var rows = (await db.QueryAsync<HistoryRow>(Command("""
SELECT EngineCode,
 TIMESTAMPADD(MINUTE,FLOOR(TIMESTAMPDIFF(MINUTE,'2000-01-01',BucketUtc)/@Step)*@Step,'2000-01-01') BucketUtc,
 SUM(Completed) Completed,SUM(Failed) Failed,SUM(DurationMs) DurationMs,MAX(MaxDurationMs) MaxDurationMs,
 SUM(BusyMs) BusyMs,SUM(ObservedMs) ObservedMs
FROM engine_metric_history WHERE Deployment=@Deployment AND BucketUtc>=@Start AND BucketUtc<@End
GROUP BY EngineCode,
 TIMESTAMPADD(MINUTE,FLOOR(TIMESTAMPDIFF(MINUTE,'2000-01-01',BucketUtc)/@Step)*@Step,'2000-01-01')
""", new { Deployment, Start = EngineHistoryCollector.Floor(from.UtcDateTime), End = end, Step = step }, ct))).ToList();
        return BuildResult(rows, start, end, step);
    }
    internal EngineHistoryResult BuildResult(IEnumerable<HistoryRow> rows, DateTime start, DateTime end, int step)
    {
        var lookup = rows.ToDictionary(row => (row.EngineCode, DateTime.SpecifyKind(row.BucketUtc, DateTimeKind.Utc)));
        var result = new EngineHistoryResult { Deployment = Deployment, FromUtc = start, UntilUtc = end, BucketMinutes = step,
            RecordingEnabled = Enabled, LastSavedAtUtc = LastSavedAtUtc,
            Warning = SaveUnavailable ? "History saving is temporarily unavailable; live engines continue. The in-memory retry buffer is limited to one hour." : "" };
        foreach (var (code, name) in EngineRuntimeMonitor.EngineNames)
        {
            var series = new EngineHistorySeries { Code = code, Name = name };
            for (var time = start; time < end; time = time.AddMinutes(step))
            {
                lookup.TryGetValue((code, time), out var row);
                series.Trend.Add(new EngineHistoryPoint { TimeUtc = time,
                    Requests = row?.Completed, Failures = row?.Failed,
                    AverageDurationMs = row is null ? null : row.Completed == 0 ? 0 : Math.Round(row.DurationMs / row.Completed, 1),
                    MaxDurationMs = row?.MaxDurationMs,
                    LoadPercent = row is null || row.ObservedMs <= 0 ? null : Math.Round(Math.Clamp(100 * row.BusyMs / row.ObservedMs, 0, 100), 2),
                    ObservedInstanceSeconds = (row?.ObservedMs ?? 0) / 1000 });
            }
            result.Engines.Add(series);
        }
        return result;
    }
    internal sealed class HistoryRow
    {
        public string EngineCode { get; set; } = "";
        public DateTime BucketUtc { get; set; }
        public long Completed { get; set; }
        public long Failed { get; set; }
        public double DurationMs { get; set; }
        public double MaxDurationMs { get; set; }
        public double BusyMs { get; set; }
        public double ObservedMs { get; set; }
    }
}
