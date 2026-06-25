using Dapper;
using MySqlConnector;

namespace Payroll.API.Repositories;

public class DatabaseMigrationRepository(IConfiguration configuration, ILogger<DatabaseMigrationRepository> logger)
{
    private MySqlConnection Connection() => new(configuration.GetConnectionString("Default"));

    public async Task RunAsync(string name, Func<Task> action)
    {
        await using var db = Connection();
        await db.OpenAsync();
        await db.ExecuteAsync("CREATE DATABASE IF NOT EXISTS payroll;");
        await db.ExecuteAsync("USE payroll;");
        var locked = await db.ExecuteScalarAsync<int>("SELECT GET_LOCK('payroll_schema_migration', 30);");
        if (locked != 1) throw new TimeoutException("Could not acquire schema migration lock.");
        try
        {
            await db.ExecuteAsync("""
CREATE TABLE IF NOT EXISTS SchemaMigrations (
    Name VARCHAR(180) PRIMARY KEY,
    AppliedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
);
""");
            if (await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM SchemaMigrations WHERE Name=@name", new { name }) > 0)
                return;
            logger.LogInformation("Applying database migration {MigrationName}", name);
            await action();
            await db.ExecuteAsync("INSERT INTO SchemaMigrations (Name) VALUES (@name)", new { name });
        }
        finally
        {
            await db.ExecuteAsync("SELECT RELEASE_LOCK('payroll_schema_migration');");
        }
    }
}
