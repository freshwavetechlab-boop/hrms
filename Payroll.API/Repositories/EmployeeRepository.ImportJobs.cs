using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public partial class EmployeeRepository
{
    private static async Task EnsureImportJobSchemaAsync(MySqlConnection db)
    {
        using var script = typeof(EmployeeRepository).Assembly.GetManifestResourceStream("Payroll.API.Database.EmployeeImportJobs.sql")
            ?? throw new InvalidOperationException("The employee import job schema is unavailable.");
        using var reader = new StreamReader(script);
        await db.ExecuteAsync(await reader.ReadToEndAsync());
    }

    // Immediate validation responses never enter the background queue or the bell.
    private static void RememberImportRejection(EmployeeImportJobStatus job)
    {
        foreach (var entry in ImportJobs.Where(entry => entry.Value.CreatedAtUtc < DateTime.UtcNow.Subtract(ImportReviewLifetime)))
            ImportJobs.TryRemove(entry.Key, out _);
        ImportJobs[job.JobId] = job;
    }

    private async Task<EmployeeImportJobStatus> QueueEmployeeImportAsync(int clientId, int actorUserId, EmployeeImportWorkbook workbook, string mode)
    {
        if (actorUserId > 0) workbook.AssignmentActor = $"User #{actorUserId}";
        var headers = GetEmployeeDataSheet(workbook).FirstOrDefault() ?? [];
        var map = HeaderMap(headers);
        var job = new EmployeeImportJobStatus(Guid.NewGuid(), "Queued", CountImportRows(workbook), 0, 0, 0, [])
        {
            ClientId = clientId, RequestedByUserId = actorUserId, Operation = mode,
            Kind = HasHeader(map, RemoveTestPayroll) ? "salary-template-assignment-cleanup"
                : HasHeader(map, AssignSalaryOnly) ? "salary-template-assignment" : "employee-import"
        };
        // This connection is transferred to the worker. Its named lock makes an interrupted
        // job distinguishable from one running on another API instance, without replaying any rows.
        var lease = new MySqlConnection(new MySqlConnectionStringBuilder(configuration.GetConnectionString("Default")) { Pooling = false }.ConnectionString);
        try
        {
            await lease.OpenAsync();
            if (!await lease.ExecuteScalarAsync<bool>("SELECT GET_LOCK(CONCAT('frevo:emp-import:',@Id),0)", new { Id = job.JobId.ToString() }))
                throw new InvalidOperationException("Could not reserve the employee import job.");
            const string insert = """
                INSERT INTO employee_import_jobs(JobId,ClientId,ActorUserId,Kind,Operation,State,TotalRows,ErrorsJson,CreatedAtUtc)
                VALUES(@Id,@ClientId,@ActorUserId,@Kind,@Operation,@State,@TotalRows,JSON_ARRAY(),@CreatedAtUtc)
                """;
            var parameters = new { Id = job.JobId.ToString(), ClientId = clientId, ActorUserId = actorUserId, job.Kind, job.Operation, job.State, job.TotalRows, job.CreatedAtUtc };
            try { await lease.ExecuteAsync(insert, parameters); }
            catch (MySqlException ex) when (ex.Number == 1146)
            {
                await EnsureImportJobSchemaAsync(lease);
                await lease.ExecuteAsync(insert, parameters);
            }
        }
        catch
        {
            await lease.DisposeAsync();
            throw;
        }
        _ = Task.Run(() => ProcessEmployeeImportJobAsync(lease, job, workbook, mode));
        return job;
    }

    private async Task ProcessEmployeeImportJobAsync(MySqlConnection lease, EmployeeImportJobStatus job, EmployeeImportWorkbook workbook, string mode)
    {
        await using var db = lease;
        var id = job.JobId.ToString();
        EmployeeImportResult? outcome = null;
        Task PersistOutcomeAsync(MySqlConnection target, EmployeeImportResult result)
        {
            var state = result.Errors.Count == 0 ? "Completed" : "Failed";
            var processed = result.Errors.Count == 0 ? job.TotalRows : Math.Max(result.ProcessedRows, result.Inserted + result.Updated);
            return target.ExecuteAsync("""
                UPDATE employee_import_jobs SET State=@state,CompletedRows=GREATEST(CompletedRows,@processed),
                InsertedRows=@Inserted,UpdatedRows=@Updated,ErrorsJson=@errors WHERE JobId=@id
                """, new { id, state, processed, result.Inserted, result.Updated, errors = JsonSerializer.Serialize(result.Errors) });
        }
        try
        {
            await db.ExecuteAsync("UPDATE employee_import_jobs SET State='Processing' WHERE JobId=@id", new { id });
            outcome = await ImportWorkbookAsync(job.ClientId!.Value, workbook, mode, (completed, inserted, updated) =>
                db.ExecuteAsync("""
                    UPDATE employee_import_jobs SET CompletedRows=@completed,InsertedRows=@inserted,UpdatedRows=@updated WHERE JobId=@id
                    """, new { id, completed, inserted, updated }));
            await PersistOutcomeAsync(db, outcome);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Employee import job {JobId} did not finish", job.JobId);
            try
            {
                await using var failureDb = Connection();
                if (outcome is not null) await PersistOutcomeAsync(failureDb, outcome);
                else await failureDb.ExecuteAsync("""
                    UPDATE employee_import_jobs SET State='Failed',ErrorsJson=JSON_ARRAY('Processing stopped. Counts show the last checkpoint. Review employee records before retrying.')
                    WHERE JobId=@id AND State IN ('Queued','Processing')
                    """, new { id });
            }
            catch (Exception reportError) { logger?.LogError(reportError, "Could not persist the outcome of employee import job {JobId}", job.JobId); }
        }
    }

    internal async Task<int?> ImportJobClientIdAsync(Guid jobId)
    {
        if (ImportJobs.TryGetValue(jobId, out var rejected)) return rejected.ClientId;
        if (string.IsNullOrEmpty(configuration.GetConnectionString("Default"))) return null;
        await using var db = Connection();
        try { return await db.ExecuteScalarAsync<int?>("SELECT ClientId FROM employee_import_jobs WHERE JobId=@id", new { id = jobId.ToString() }); }
        catch (MySqlException ex) when (ex.Number == 1146) { return null; }
    }

    public async Task<IReadOnlyList<EmployeeImportJobStatus>> OwnEmployeeImportJobsAsync(AuthUser user, Guid? jobId = null)
    {
        if (!CanSeeEmployeeImportJobs(user)) return [];
        if (jobId.HasValue && ImportJobs.TryGetValue(jobId.Value, out var rejected))
            return rejected.RequestedByUserId == user.Id && rejected.ClientId is int client && JobClientAllowed(user, client) ? [rejected] : [];
        const string sql = """
            SELECT j.JobId,j.ClientId,c.Name ClientName,j.Kind,j.Operation,j.State,j.TotalRows,j.CompletedRows,
            j.InsertedRows,j.UpdatedRows,j.CreatedAtUtc,
            CASE WHEN j.State IN ('Queued','Processing') THEN IS_FREE_LOCK(CONCAT('frevo:emp-import:',j.JobId)) ELSE 0 END Interrupted,
            CASE WHEN @id IS NOT NULL THEN CAST(j.ErrorsJson AS CHAR) ELSE NULL END ErrorsJson
            FROM employee_import_jobs j JOIN clients c ON c.Id=j.ClientId
            WHERE j.ActorUserId=@actor AND (@client IS NULL OR j.ClientId=@client)
            AND ((@id IS NULL AND j.DismissedAtUtc IS NULL) OR j.JobId=@id)
            """;
        var selected = SelectedClientAccess.IsSelected(user);
        if (selected && user.AssignedClientIds.Count == 0) return [];
        await using var db = Connection();
        try
        {
            var rows = await db.QueryAsync<ImportJobRow>(sql + (selected ? " AND j.ClientId IN @assigned" : "") + " ORDER BY j.CreatedAtUtc DESC",
                new { actor = user.Id, client = user.ClientId, id = jobId?.ToString(), assigned = user.AssignedClientIds });
            return rows.Select(row => row.AsStatus(jobId.HasValue)).ToList();
        }
        catch (MySqlException ex) when (ex.Number == 1146) { return []; }
    }

    public async Task<bool> DismissEmployeeImportJobAsync(AuthUser user, Guid jobId)
    {
        var job = (await OwnEmployeeImportJobsAsync(user, jobId)).SingleOrDefault();
        if (job?.State is not ("Completed" or "Failed")) return false;
        await using var db = Connection();
        return await db.ExecuteAsync("""
            UPDATE employee_import_jobs SET DismissedAtUtc=COALESCE(DismissedAtUtc,UTC_TIMESTAMP(6))
            WHERE JobId=@id AND ClientId=@client AND ActorUserId=@actor
            AND (State IN ('Failed','Completed') OR IS_FREE_LOCK(CONCAT('frevo:emp-import:', JobId))=1)
            """, new { id = jobId.ToString(), client = job.ClientId, actor = user.Id }) > 0;
    }

    internal static bool CanSeeEmployeeImportJobs(AuthUser user) => user.Id > 0 && user.IsActive && !user.MustChangePassword
        && user.Permissions.Contains("employees.manage", StringComparer.OrdinalIgnoreCase);
    private static bool JobClientAllowed(AuthUser user, int clientId) =>
        (!user.ClientId.HasValue || user.ClientId == clientId) && SelectedClientAccess.Allows(user, clientId);

    private sealed class ImportJobRow
    {
        public Guid JobId { get; set; }
        public int ClientId { get; set; }
        public string ClientName { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Operation { get; set; } = "";
        public string State { get; set; } = "";
        public int TotalRows { get; set; }
        public int CompletedRows { get; set; }
        public int InsertedRows { get; set; }
        public int UpdatedRows { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public bool Interrupted { get; set; }
        public string? ErrorsJson { get; set; }
        public EmployeeImportJobStatus AsStatus(bool detail) => new(JobId, Interrupted ? "Failed" : State, TotalRows, CompletedRows,
            InsertedRows, UpdatedRows, Interrupted && detail ? ["The worker was interrupted. Counts show its last checkpoint. Review employee records before retrying."]
            : ErrorsJson is not null ? JsonSerializer.Deserialize<List<string>>(ErrorsJson) ?? [] : [])
        {
            ClientId = ClientId, ClientName = ClientName, Kind = Kind, Operation = Operation,
            CreatedAtUtc = DateTime.SpecifyKind(CreatedAtUtc, DateTimeKind.Utc)
        };
    }
}
