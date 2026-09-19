using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Services;

public sealed class FrevoPilotAnalyticsService(
    IConfiguration configuration, IWebHostEnvironment environment,
    IHostApplicationLifetime lifetime, RecruitmentAiScoringService ai,
    EngineRuntimeMonitor monitor, AuthRepository audit,
    ILogger<FrevoPilotAnalyticsService> logger, PortableIntegrationCredentialProtector? planProtector = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<Guid, FrevoPilotRun> runs = new();
    private readonly SemaphoreSlim gate = new(2, 2);
    private readonly object startLock = new();
    private FrevoPilotPlanMemory? verifiedPlans;
    private const string AllowedTables = "clients,employees,worklocations,departments,designations,payruns,payrunemployees,employee_monthly_attendance,essleaverequests,leavetypes,salarycomponents,salarytemplates,recruitment_open_positions,recruitment_candidates,recruitment_candidate_applications,recruitment_application_scores,recruitment_interviews,recruitment_offers,recruitment_work_orders,recruitment_requisitions,recruitment_pipeline_stages,recruitment_position_pipeline_instances,recruitment_position_stage_instances,workflowtasks,workflowinstances,workflowmasters,workflowactivities,ess_expense_claims,ess_travel_requests,client_billing_configurations,client_billing_cost_rule_headers,client_billing_cost_rule_lines";
    private static readonly Regex ProtectedField = new("password|secret|token|apikey|cipher|credential|aadhaar|aadhar|pannumber|bankaccount|accountno|ifsccode|mobile|phone|email|address|parsedtext|selfie|filepath|firstname|lastname|fullname|dateofbirth|json|employeecode|candidatecode|applicationcode|offernumber|overallfeedback", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    public bool Enabled => configuration.GetValue("FrevoPilot:Enabled", true);
    public static bool CanUse(AuthUser user) => user.IsActive && user.ClientId is null && user.Roles.Contains("super_admin", StringComparer.OrdinalIgnoreCase);

    public (FrevoPilotRun? Run, string Error) Start(FrevoPilotQuestion request, AuthUser actor)
    {
        if (!Enabled || !CanUse(actor)) return (null, "FrevoPilot is available to global super administrators only.");
        var question = (request.Question ?? "").Trim();
        if (question.Length is < 4 or > 2000) return (null, "Enter a business question between 4 and 2,000 characters.");
        if (request.ClientId is <= 0) return (null, "Select a valid client or all clients.");
        if (request.ModelId is <= 0) return (null, "Select a valid saved model or the active provider.");
        if (Regex.IsMatch(question, @"\b(?:drop|truncate|alter|insert|update|delete|grant|revoke|password|secret|token|api key)\b", RegexOptions.IgnoreCase))
            return (null, "FrevoPilot provides read-only aggregate insights; changes and secret data are not supported.");
        if (!File.Exists(Path.Combine(environment.ContentRootPath, "FrevoPilot", "runtime.mjs")))
            return (null, "FrevoPilot runtime is missing from this API deployment.");
        FrevoPilotRun run;
        lock (startLock)
        {
            foreach (var old in runs.Where(pair => pair.Value.Status != "Processing" && pair.Value.CreatedAtUtc < DateTime.UtcNow.AddHours(-1))) runs.TryRemove(old.Key, out _);
            if (runs.Values.Any(value => value.ActorId == actor.Id && value.Status == "Processing"))
                return (null, "Your previous question is still processing. Wait for its result.");
            if (!gate.Wait(0)) return (null, "FrevoPilot is busy with two questions. Please retry shortly.");
            run = new FrevoPilotRun { Id = Guid.NewGuid(), ActorId = actor.Id, Question = question, ClientId = request.ClientId, ModelId = request.ModelId, UseFastMetrics = request.UseFastMetrics };
            runs[run.Id] = run;
        }
        _ = ExecuteAsync(run, actor);
        return (run, "");
    }

    public FrevoPilotRun? Get(Guid id, AuthUser actor) => Enabled && CanUse(actor) && runs.TryGetValue(id, out var run) && run.ActorId == actor.Id ? run : null;

    private async Task ExecuteAsync(FrevoPilotRun run, AuthUser actor)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
        timeout.CancelAfter(TimeSpan.FromMinutes(Math.Clamp(configuration.GetValue("FrevoPilot:RunTimeoutMinutes", 4), 4, 15)));
        var cancellation = timeout.Token;
        Process? process = null;
        long? observation = null;
        try
        {
            try { observation = monitor.Start("frevopilot", new EngineActivityContext("Dashboard query", "Dashboard run", run.Id.ToString("N"), ClientId: run.ClientId)); } catch { }
            var providerBudget = await ai.AnalyticsRunBudgetSecondsAsync(run.ModelId, cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(providerBudget, Math.Clamp(configuration.GetValue("FrevoPilot:RunTimeoutMinutes", 4), 4, 15) * 60)));
            await using var db = new MySqlConnection(configuration.GetConnectionString("Default"));
            await db.OpenAsync(cancellation);
            string? clientName = null;
            if (run.ClientId.HasValue)
            {
                clientName = await db.QueryFirstOrDefaultAsync<string>(new CommandDefinition("SELECT Name FROM clients WHERE Id=@Id", new { Id = run.ClientId }, cancellationToken: cancellation));
                if (clientName is null) throw new InvalidOperationException("The selected client was not found.");
            }
            var columns = await db.QueryAsync<SchemaColumn>(new CommandDefinition(@"SELECT TABLE_NAME TableName,COLUMN_NAME ColumnName,DATA_TYPE DataType
FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME IN @Tables ORDER BY TABLE_NAME,ORDINAL_POSITION",
                new { Tables = AllowedTables.Split(',') }, cancellationToken: cancellation));
            var catalog = columns.GroupBy(column => column.TableName).Select(group => new
            {
                name = group.Key, rowEstimate = 0,
                columns = group.Select(column => new { name = column.ColumnName, type = column.DataType, sensitive = ProtectedField.IsMatch(column.ColumnName) })
            }).ToList();
            var memory = CreatePlanSession(actor, run.ClientId, db, JsonSerializer.Serialize(catalog, Json));
            var info = new ProcessStartInfo(configuration["FrevoPilot:NodeExecutable"] ?? "node")
            {
                WorkingDirectory = Path.Combine(environment.ContentRootPath, "FrevoPilot"),
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            };
            info.ArgumentList.Add(Path.Combine(info.WorkingDirectory, "runtime.mjs"));
            // RAG only reads packaged public business rules, never config/credential files.
            info.Environment["FREVOPILOT_KNOWLEDGE_ROOT"] = info.WorkingDirectory;
            process = Process.Start(info) ?? throw new InvalidOperationException("FrevoPilot runtime could not start.");
            var stderr = process.StandardError.ReadToEndAsync(cancellation);
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
            {
                question = run.Question, catalog, sourceDatabase = db.Database, run.ClientId, clientName,
                rules = new[] {
                    "Active employees means employees.IsActive=1. Do not invent a reporting period.",
                    "Use stored payruns totals; current ATS scores require IsCurrent=1.",
                    "Count applications separately from global talent pool: ApplicationType='Application'.",
                    "Pre-aggregate independent child tables to avoid multiplying counts or monetary totals.",
                    "A job opening is not a candidate. Report ApprovedPositions and RemainingPositions distinctly."
                }
            }, Json));
            while (await process.StandardOutput.ReadLineAsync(cancellation) is { } line)
            {
                if (line.Length > 2_000_000) throw new InvalidOperationException("FrevoPilot result exceeds the safe size limit.");
                using var message = JsonDocument.Parse(line);
                var root = message.RootElement;
                var kind = root.GetProperty("kind").GetString();
                if (kind == "progress") { run.Progress = root.GetProperty("progress").GetInt32(); run.Message = root.GetProperty("message").GetString() ?? ""; }
                else if (kind == "ai" || kind == "query")
                {
                    object? result = null;
                    string? error = null;
                    try
                    {
                        if (kind == "ai") result = await ai.GenerateAnalyticsJsonAsync(root.GetProperty("prompt").GetString() ?? "",
                            root.GetProperty("systemInstruction").GetString() ?? "", root.GetProperty("schema").GetRawText(), cancellation, run.ModelId, memory,
                            run.UseFastMetrics && configuration.GetValue("FrevoPilot:FastMetricsEnabled", true));
                        else
                        {
                            result = await QueryAsync(db, root.GetProperty("sql").GetString() ?? "", root.GetProperty("parameters"), cancellation);
                            // The worker echoes only a run-bound receipt after its SQL,
                            // semantic and scope guards. Never persist worker-supplied SQL.
                            if (root.TryGetProperty("planReceipt", out var receipt) && receipt.ValueKind == JsonValueKind.String)
                                memory?.Promote(receipt.GetString() ?? "");
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (MySqlException exception) { error = $"Query does not match the current schema (MySQL {exception.Number}). Check table and column names."; }
                    catch (InvalidOperationException exception) { error = exception.Message; }
                    catch (Exception exception) { logger.LogWarning("FrevoPilot RPC failed: {Type}", exception.GetType().Name); error = "AI or analytics data is temporarily unavailable."; }
                    await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { id = root.GetProperty("id").GetInt32(), result, error }, Json));
                }
                else if (kind == "result") { run.Result = root.GetProperty("result").Clone(); run.Progress = 100; run.Status = "Completed"; run.Message = "Dashboard ready"; }
                else if (kind == "error") throw new InvalidOperationException(root.GetProperty("message").GetString());
            }
            await process.WaitForExitAsync(cancellation);
            await stderr; // Drain safely without logging prompts, SQL or provider responses.
            if (process.ExitCode != 0 || run.Result is null) throw new InvalidOperationException("FrevoPilot could not finish. Check that the analytics Node runtime and dependencies are installed.");
        }
        catch (OperationCanceledException) { run.Status = "Failed"; run.Message = "FrevoPilot timed out. No business records were changed. Retry the question."; }
        catch (Exception exception)
        {
            run.Status = "Failed";
            run.Message = exception is InvalidOperationException ? exception.Message : "FrevoPilot is temporarily unavailable. Check the API analytics runtime.";
            logger.LogWarning("FrevoPilot run {RunId} failed: {Type}", run.Id, exception.GetType().Name);
        }
        finally
        {
            if (process is not null) { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { } process.Dispose(); }
            run.CompletedAtUtc = DateTime.UtcNow;
            try { if (observation.HasValue) monitor.Complete("frevopilot", observation.Value, run.Status != "Completed", run.Status == "Completed" ? null : "Dashboard query failed; see FrevoPilot status."); } catch { }
            gate.Release();
            try { await audit.WriteAuditAsync(actor, "frevopilot.analytics", run.Id.ToString(), "POST", "/api/frevopilot/analytics/runs", run.Status == "Completed" ? 200 : 422, "", "", JsonSerializer.Serialize(new { run.Id, run.ClientId, run.Status, run.CreatedAtUtc, run.CompletedAtUtc }, Json)); } catch { }
        }
    }

    private FrevoPilotPlanSession? CreatePlanSession(AuthUser actor, int? clientId, MySqlConnection db, string catalog)
    {
        // A disabled/unavailable persistent memory still has a per-run receipt
        // store for exact compiled metrics, but cannot reuse another run's plans.
        if (!configuration.GetValue("FrevoPilot:VerifiedPlanMemoryEnabled", true))
            return new FrevoPilotPlanMemory().CreateSession(Guid.NewGuid().ToString("N"));
        try
        {
            var runtime = Path.Combine(environment.ContentRootPath, "FrevoPilot");
            var files = new[] { Path.Combine(runtime, "runtime.mjs") }
                .Concat(Directory.EnumerateFiles(Path.Combine(runtime, "analytics"), "*.mjs"))
                .Concat(Directory.EnumerateFiles(Path.Combine(runtime, "docs"), "*.md"))
                .OrderBy(path => path, StringComparer.Ordinal).Select(path => new { name = Path.GetRelativePath(runtime, path), hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) });
            // Fresh schema and exact authorization/provider/policy identity invalidate
            // learning after scope, schema or executable changes. No row data or keys.
            var fingerprint = JsonSerializer.Serialize(new { version = "verified-plan-v1", actor.Id, actorClientId = actor.ClientId, clientId,
                roles = actor.Roles.OrderBy(role => role, StringComparer.Ordinal).ToArray(), db.DataSource, db.Database,
                catalog, assembly = typeof(FrevoPilotAnalyticsService).Module.ModuleVersionId, policy = files.ToArray() }, Json);
            lock (startLock)
            {
                verifiedPlans ??= planProtector is null ? new FrevoPilotPlanMemory() : new FrevoPilotPlanMemory(
                    Path.Combine(environment.ContentRootPath, "App_Data", "FrevoPilot", "validated-plans.enc"),
                    planProtector.ProtectVerifiedPlan, planProtector.UnprotectVerifiedPlan);
                return verifiedPlans.CreateSession(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint))));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger.LogWarning("FrevoPilot verified-plan memory unavailable; normal guarded planning remains available.");
            return new FrevoPilotPlanMemory().CreateSession(Guid.NewGuid().ToString("N"));
        }
    }

    private static async Task<List<Dictionary<string, object?>>> QueryAsync(MySqlConnection db, string sql, JsonElement parameters, CancellationToken cancellation)
    {
        if (!sql.TrimStart().StartsWith("SELECT ", StringComparison.OrdinalIgnoreCase) || sql.Length > 20000
            || Regex.IsMatch(sql, @";|--|/\*|\b(?:insert|update|delete|alter|drop|grant|revoke|outfile|dumpfile|load_file|sleep|benchmark)\b", RegexOptions.IgnoreCase))
            throw new InvalidOperationException("Only one bounded read-only SELECT is allowed.");
        await db.ExecuteAsync(new CommandDefinition("SET SESSION MAX_EXECUTION_TIME=15000", cancellationToken: cancellation));
        await using var transaction = await db.BeginTransactionAsync(IsolationLevel.ReadCommitted, isReadOnly: true, cancellation);
        await using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = 20;
        var values = parameters.EnumerateArray().ToList();
        var index = 0;
        // Planner parameters are strings and SQL forbids arbitrary variables. Placeholder
        // replacement is outside quoted SQL literals only.
        command.CommandText = Regex.Replace(sql, @"'(?:(?:'')|[^'])*'|`[^`]*`|\?", match =>
        {
            if (match.Value != "?") return match.Value;
            if (index >= values.Count) throw new InvalidOperationException("The query is missing a filter value.");
            var name = "@p" + index;
            command.Parameters.AddWithValue(name, values[index++].ToString());
            return name;
        });
        if (index != values.Count) throw new InvalidOperationException("The query contains unexpected filter values.");
        var rows = new List<Dictionary<string, object?>>();
        await using (var reader = await command.ExecuteReaderAsync(cancellation))
        {
            while (await reader.ReadAsync(cancellation))
            {
                if (rows.Count >= 500) throw new InvalidOperationException("The result is too large. Narrow the question or select a client.");
                var row = new Dictionary<string, object?>();
                for (var column = 0; column < reader.FieldCount; column++) row[reader.GetName(column)] = reader.IsDBNull(column) ? null : reader.GetValue(column);
                rows.Add(row);
            }
        }
        await transaction.RollbackAsync(cancellation);
        return rows;
    }

    private sealed class SchemaColumn { public string TableName { get; set; } = ""; public string ColumnName { get; set; } = ""; public string DataType { get; set; } = ""; }
}

public sealed record FrevoPilotQuestion(string Question, int? ClientId, long? ModelId = null, bool UseFastMetrics = true);
public sealed class FrevoPilotRun
{
    public Guid Id { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public int ActorId { get; set; }
    public string Question { get; set; } = "";
    public int? ClientId { get; set; }
    public long? ModelId { get; set; }
    public bool UseFastMetrics { get; set; } = true;
    public string Status { get; set; } = "Processing";
    public int Progress { get; set; }
    public string Message { get; set; } = "Preparing analytics";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; set; }
    public JsonElement? Result { get; set; }
}
