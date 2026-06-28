using MySqlConnector;

public static class DatabaseBootstrapper
{
    public static async Task RunIfEnabledAsync(IConfiguration configuration, string contentRootPath, ILogger logger)
    {
        var env = LoadEnv(contentRootPath);
        if (!Truthy(Read("HRMS_BOOTSTRAP_DATA", configuration, env, "false"))) return;

        var relativePath = Read("HRMS_BOOTSTRAP_SQL", configuration, env, "Database/2026-06-28_local_bootstrap.sql");
        var sqlPath = Path.IsPathRooted(relativePath) ? relativePath : Path.Combine(contentRootPath, relativePath);
        if (!File.Exists(sqlPath)) throw new FileNotFoundException($"Bootstrap SQL not found: {sqlPath}");
        var runOnce = Truthy(Read("HRMS_BOOTSTRAP_ONCE", configuration, env, "true"));
        var markerPath = Path.Combine(contentRootPath, ".bootstrap", $"{Path.GetFileName(sqlPath)}.done");
        if (runOnce && File.Exists(markerPath))
        {
            logger.LogInformation("HRMS bootstrap skipped. Marker exists: {MarkerPath}", markerPath);
            return;
        }

        var connectionString = configuration.GetConnectionString("Default") ?? throw new InvalidOperationException("Connection string 'Default' is not configured.");
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();

        var count = 0;
        var sql = await File.ReadAllTextAsync(sqlPath);
        foreach (var statement in SplitSql(sql))
        {
            if (string.IsNullOrWhiteSpace(statement)) continue;
            await using var command = new MySqlCommand(statement, connection) { CommandTimeout = 0 };
            await command.ExecuteNonQueryAsync();
            count++;
        }
        if (runOnce)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
            await File.WriteAllTextAsync(markerPath, DateTimeOffset.Now.ToString("O"));
        }
        logger.LogWarning("HRMS bootstrap imported {StatementCount} SQL statements from {SqlPath}. Existing data may have been overwritten.", count, sqlPath);
    }

    private static Dictionary<string, string> LoadEnv(string contentRootPath)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in new[] { Path.Combine(Directory.GetParent(contentRootPath)?.FullName ?? contentRootPath, ".env"), Path.Combine(contentRootPath, ".env") })
        {
            if (!File.Exists(path)) continue;
            foreach (var line in File.ReadAllLines(path))
            {
                var text = line.Trim();
                if (text.Length == 0 || text.StartsWith('#')) continue;
                var split = text.IndexOf('=');
                if (split <= 0) continue;
                values[text[..split].Trim()] = text[(split + 1)..].Trim().Trim('"', '\'');
            }
        }
        return values;
    }

    private static string Read(string key, IConfiguration configuration, Dictionary<string, string> env, string fallback) =>
        Environment.GetEnvironmentVariable(key)
        ?? (env.TryGetValue(key, out var value) ? value : null)
        ?? configuration[key]
        ?? fallback;

    private static bool Truthy(string value) =>
        value.Equals("true", StringComparison.OrdinalIgnoreCase) || value is "1" or "yes" or "YES" or "on" or "ON";

    private static IEnumerable<string> SplitSql(string sql)
    {
        var start = 0;
        var quote = '\0';
        var lineComment = false;
        var blockComment = false;
        for (var i = 0; i < sql.Length; i++)
        {
            var c = sql[i];
            var next = i + 1 < sql.Length ? sql[i + 1] : '\0';
            if (lineComment) { if (c is '\n' or '\r') lineComment = false; continue; }
            if (blockComment) { if (c == '*' && next == '/') { blockComment = false; i++; } continue; }
            if (quote != '\0') { if (c == '\\') { i++; continue; } if (c == quote) quote = '\0'; continue; }
            if (c is '\'' or '"' or '`') { quote = c; continue; }
            if (c == '-' && next == '-') { lineComment = true; i++; continue; }
            if (c == '#') { lineComment = true; continue; }
            if (c == '/' && next == '*') { blockComment = true; i++; continue; }
            if (c != ';') continue;
            yield return sql[start..i].Trim();
            start = i + 1;
        }
        if (start < sql.Length) yield return sql[start..].Trim();
    }
}
