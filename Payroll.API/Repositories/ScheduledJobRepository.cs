using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public class ScheduledJobRepository(IConfiguration configuration, NotificationRepository notifications, ReportingRepository reporting, WorkflowRepository workflows, ILogger<ScheduledJobRepository> logger)
{
    public const string NotificationHandler = "NOTIFICATION_QUEUE_DISPATCH";
    public const string LeaveAccrualHandler = "LEAVE_MONTHLY_ACCRUAL";
    public const string LeaveResetHandler = "LEAVE_YEAR_END_RESET";
    public const string ConfiguredNotificationHandler = "CONFIGURED_NOTIFICATION_EVENT";
    public const string ConfiguredActionHandler = "CONFIGURED_JOB_ACTION";

    private MySqlConnection Db() => new(configuration.GetConnectionString("Default"));

    public static IReadOnlyList<ScheduledJobHandlerOption> HandlerOptions { get; } =
    [
        new() { HandlerKey = NotificationHandler, Name = "Send queued emails", Description = "Processes pending notification_queue rows using configured SMTP." },
        new() { HandlerKey = LeaveAccrualHandler, Name = "Monthly leave credit", Description = "Credits active employees based on monthly leave type entitlement." },
        new() { HandlerKey = LeaveResetHandler, Name = "Year-end leave reset", Description = "Carries forward or lapses balances based on leave type reset rules." },
        new() { HandlerKey = ConfiguredActionHandler, Name = "Configured job action", Description = "Runs a reusable job action created from the Job Actions tab." },
        new() { HandlerKey = ConfiguredNotificationHandler, Name = "Configurable notification event", Description = "Legacy: publishes a configured event so matching notification rules can send mail." }
    ];

    public async Task InitializeAsync()
    {
        await using var db = Db();
        await db.OpenAsync();
        await db.ExecuteAsync(@"
CREATE TABLE IF NOT EXISTS scheduled_jobs (
    Id INT PRIMARY KEY AUTO_INCREMENT,
    JobCode VARCHAR(120) NOT NULL,
    JobName VARCHAR(220) NOT NULL,
    Description VARCHAR(1000) NOT NULL DEFAULT '',
    HandlerKey VARCHAR(120) NOT NULL,
    IsEnabled BOOLEAN NOT NULL DEFAULT TRUE,
    ScheduleType VARCHAR(30) NOT NULL DEFAULT 'Interval',
    IntervalMinutes INT NOT NULL DEFAULT 60,
    DailyRunTime VARCHAR(5) NOT NULL DEFAULT '01:00',
    MonthlyRunDay INT NOT NULL DEFAULT 1,
    ConfigJson JSON NOT NULL,
    LastRunAt DATETIME NULL,
    NextRunAt DATETIME NULL,
    LastStatus VARCHAR(40) NOT NULL DEFAULT 'Never Run',
    LastMessage VARCHAR(1000) NOT NULL DEFAULT '',
    IsRunning BOOLEAN NOT NULL DEFAULT FALSE,
    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    UNIQUE KEY UX_scheduled_jobs_code (JobCode),
    INDEX IX_scheduled_jobs_due (IsEnabled, NextRunAt, IsRunning)
);
CREATE TABLE IF NOT EXISTS scheduled_job_runs (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    JobId INT NOT NULL,
    JobCode VARCHAR(120) NOT NULL,
    StartedAt DATETIME NOT NULL,
    CompletedAt DATETIME NULL,
    Status VARCHAR(40) NOT NULL DEFAULT 'Running',
    SuccessCount INT NOT NULL DEFAULT 0,
    FailureCount INT NOT NULL DEFAULT 0,
    Message VARCHAR(1000) NOT NULL DEFAULT '',
    ErrorDetails MEDIUMTEXT NULL,
    TriggeredBy VARCHAR(120) NOT NULL DEFAULT 'Scheduler',
    DurationMs BIGINT NOT NULL DEFAULT 0,
    INDEX IX_scheduled_job_runs_job (JobId, StartedAt)
);
CREATE TABLE IF NOT EXISTS scheduled_job_actions (
    Id INT PRIMARY KEY AUTO_INCREMENT,
    ActionCode VARCHAR(120) NOT NULL,
    ActionName VARCHAR(220) NOT NULL,
    ActionType VARCHAR(60) NOT NULL DEFAULT 'Notification Event',
    Description VARCHAR(1000) NOT NULL DEFAULT '',
    ConfigJson JSON NOT NULL,
    IsActive BOOLEAN NOT NULL DEFAULT TRUE,
    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    UNIQUE KEY UX_scheduled_job_actions_code (ActionCode),
    INDEX IX_scheduled_job_actions_active (IsActive, ActionType)
);
CREATE TABLE IF NOT EXISTS employee_leave_ledger (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    ClientId INT NOT NULL,
    EmployeeId INT NOT NULL,
    LeaveTypeId INT NOT NULL,
    LeaveCode VARCHAR(40) NOT NULL,
    TransactionDate DATE NOT NULL,
    PeriodKey VARCHAR(20) NOT NULL,
    TransactionType VARCHAR(60) NOT NULL,
    Quantity DECIMAL(10,2) NOT NULL,
    BalanceAfter DECIMAL(10,2) NOT NULL DEFAULT 0,
    ReferenceType VARCHAR(80) NOT NULL DEFAULT '',
    ReferenceId VARCHAR(120) NOT NULL DEFAULT '',
    DedupKey VARCHAR(190) NULL,
    Remarks VARCHAR(1000) NOT NULL DEFAULT '',
    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE KEY UX_employee_leave_ledger_dedup (DedupKey),
    INDEX IX_employee_leave_ledger_employee (EmployeeId, LeaveTypeId, TransactionDate)
);");
        await EnsureClientColumnsAsync(db);
        await EnsureDefaultsAsync(db);
    }

    public Task<IReadOnlyList<ScheduledJobHandlerOption>> GetHandlerOptionsAsync() => Task.FromResult(HandlerOptions);

    public async Task<IEnumerable<ScheduledJobAction>> GetActionsAsync()
    {
        await using var db = Db();
        await db.OpenAsync();
        return await db.QueryAsync<ScheduledJobAction>("SELECT * FROM scheduled_job_actions ORDER BY ActionName");
    }

    public async Task<ScheduledJobAction?> SaveActionAsync(ScheduledJobActionSaveRequest request)
    {
        NormalizeAction(request);
        ValidateAction(request);
        await using var db = Db();
        await db.OpenAsync();
        if (request.Id == 0)
        {
            var id = await db.ExecuteScalarAsync<int>(@"INSERT INTO scheduled_job_actions (ActionCode,ActionName,ActionType,Description,ConfigJson,IsActive)
VALUES (@ActionCode,@ActionName,@ActionType,@Description,@ConfigJson,@IsActive);
SELECT LAST_INSERT_ID();", request);
            request.Id = id;
        }
        else
        {
            await db.ExecuteAsync(@"UPDATE scheduled_job_actions SET ActionCode=@ActionCode,ActionName=@ActionName,ActionType=@ActionType,Description=@Description,ConfigJson=@ConfigJson,IsActive=@IsActive WHERE Id=@Id", request);
        }
        return await db.QueryFirstOrDefaultAsync<ScheduledJobAction>("SELECT * FROM scheduled_job_actions WHERE Id=@Id", new { request.Id });
    }

    public async Task<IEnumerable<ScheduledJob>> GetAsync()
    {
        await using var db = Db();
        await db.OpenAsync();
        return await db.QueryAsync<ScheduledJob>("SELECT * FROM scheduled_jobs ORDER BY JobName");
    }

    public async Task<IEnumerable<ScheduledJobRun>> GetRunsAsync(int? jobId, int limit = 100)
    {
        await using var db = Db();
        await db.OpenAsync();
        return await db.QueryAsync<ScheduledJobRun>(@"SELECT * FROM scheduled_job_runs
WHERE (@JobId IS NULL OR JobId=@JobId)
ORDER BY StartedAt DESC LIMIT @Limit", new { JobId = jobId, Limit = Math.Clamp(limit, 10, 500) });
    }

    public async Task<ScheduledJob?> SaveAsync(ScheduledJobSaveRequest request)
    {
        Normalize(request);
        await using var db = Db();
        await db.OpenAsync();
        if (request.Id == 0)
        {
            request.NextRunAt = CalculateNextRun(request, DateTime.Now);
            var id = await db.ExecuteScalarAsync<int>(@"INSERT INTO scheduled_jobs (JobCode,JobName,Description,HandlerKey,IsEnabled,ScheduleType,IntervalMinutes,DailyRunTime,MonthlyRunDay,ConfigJson,NextRunAt)
VALUES (@JobCode,@JobName,@Description,@HandlerKey,@IsEnabled,@ScheduleType,@IntervalMinutes,@DailyRunTime,@MonthlyRunDay,@ConfigJson,@NextRunAt);
SELECT LAST_INSERT_ID();", request);
            request.Id = id;
        }
        else
        {
            request.NextRunAt = request.IsEnabled ? CalculateNextRun(request, DateTime.Now) : null;
            await db.ExecuteAsync(@"UPDATE scheduled_jobs SET JobCode=@JobCode,JobName=@JobName,Description=@Description,HandlerKey=@HandlerKey,IsEnabled=@IsEnabled,
ScheduleType=@ScheduleType,IntervalMinutes=@IntervalMinutes,DailyRunTime=@DailyRunTime,MonthlyRunDay=@MonthlyRunDay,ConfigJson=@ConfigJson,NextRunAt=@NextRunAt
WHERE Id=@Id", request);
        }
        return await db.QueryFirstOrDefaultAsync<ScheduledJob>("SELECT * FROM scheduled_jobs WHERE Id=@Id", new { request.Id });
    }

    public async Task<ScheduledJob?> SetEnabledAsync(int id, bool isEnabled)
    {
        await using var db = Db();
        await db.OpenAsync();
        var job = await db.QueryFirstOrDefaultAsync<ScheduledJob>("SELECT * FROM scheduled_jobs WHERE Id=@Id", new { Id = id });
        if (job is null) return null;
        job.IsEnabled = isEnabled;
        job.NextRunAt = isEnabled ? CalculateNextRun(job, DateTime.Now) : null;
        await db.ExecuteAsync("UPDATE scheduled_jobs SET IsEnabled=@IsEnabled,NextRunAt=@NextRunAt WHERE Id=@Id", job);
        return await db.QueryFirstOrDefaultAsync<ScheduledJob>("SELECT * FROM scheduled_jobs WHERE Id=@Id", new { Id = id });
    }

    public async Task RunDueJobsAsync(CancellationToken cancellationToken)
    {
        await using var db = Db();
        await db.OpenAsync(cancellationToken);
        var due = (await db.QueryAsync<ScheduledJob>(@"SELECT * FROM scheduled_jobs
WHERE IsEnabled=TRUE AND IsRunning=FALSE AND NextRunAt IS NOT NULL AND NextRunAt<=NOW()
ORDER BY NextRunAt LIMIT 5")).ToList();
        foreach (var job in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RunJobAsync(job.Id, "Scheduler", cancellationToken);
        }
    }

    public async Task<ScheduledJobRun?> RunJobAsync(int id, string triggeredBy, CancellationToken cancellationToken)
    {
        await using var db = Db();
        await db.OpenAsync(cancellationToken);
        var job = await db.QueryFirstOrDefaultAsync<ScheduledJob>("SELECT * FROM scheduled_jobs WHERE Id=@Id", new { Id = id });
        if (job is null) return null;
        if (job.IsRunning) throw new InvalidOperationException("Job is already running.");
        await db.ExecuteAsync("UPDATE scheduled_jobs SET IsRunning=TRUE,LastStatus='Running',LastMessage='' WHERE Id=@Id", new { job.Id });
        var runId = await db.ExecuteScalarAsync<long>(@"INSERT INTO scheduled_job_runs (JobId,JobCode,StartedAt,Status,TriggeredBy)
VALUES (@JobId,@JobCode,NOW(),'Running',@TriggeredBy); SELECT LAST_INSERT_ID();", new { JobId = job.Id, job.JobCode, TriggeredBy = triggeredBy });
        var watch = Stopwatch.StartNew();
        try
        {
            var result = await ExecuteHandlerAsync(job, cancellationToken);
            watch.Stop();
            var completed = DateTime.Now;
            await db.ExecuteAsync(@"UPDATE scheduled_job_runs SET CompletedAt=@CompletedAt,Status='Completed',SuccessCount=@SuccessCount,FailureCount=@FailureCount,Message=@Message,DurationMs=@DurationMs WHERE Id=@RunId;
UPDATE scheduled_jobs SET IsRunning=FALSE,LastRunAt=@CompletedAt,NextRunAt=@NextRunAt,LastStatus='Completed',LastMessage=@Message WHERE Id=@JobId;", new
            {
                RunId = runId,
                JobId = job.Id,
                CompletedAt = completed,
                result.SuccessCount,
                result.FailureCount,
                result.Message,
                DurationMs = watch.ElapsedMilliseconds,
                NextRunAt = CalculateNextRun(job, completed)
            });
        }
        catch (Exception exception)
        {
            watch.Stop();
            logger.LogError(exception, "Scheduled job {JobCode} failed.", job.JobCode);
            var completed = DateTime.Now;
            await db.ExecuteAsync(@"UPDATE scheduled_job_runs SET CompletedAt=@CompletedAt,Status='Failed',FailureCount=1,Message=@Message,ErrorDetails=@ErrorDetails,DurationMs=@DurationMs WHERE Id=@RunId;
UPDATE scheduled_jobs SET IsRunning=FALSE,LastRunAt=@CompletedAt,NextRunAt=@NextRunAt,LastStatus='Failed',LastMessage=@Message WHERE Id=@JobId;", new
            {
                RunId = runId,
                JobId = job.Id,
                CompletedAt = completed,
                Message = exception.Message,
                ErrorDetails = exception.ToString(),
                DurationMs = watch.ElapsedMilliseconds,
                NextRunAt = CalculateNextRun(job, completed)
            });
        }
        return await db.QueryFirstOrDefaultAsync<ScheduledJobRun>("SELECT * FROM scheduled_job_runs WHERE Id=@RunId", new { RunId = runId });
    }

    private async Task<ScheduledJobRunResult> ExecuteHandlerAsync(ScheduledJob job, CancellationToken cancellationToken) =>
        job.HandlerKey.ToUpperInvariant() switch
        {
            NotificationHandler => await DispatchNotificationsAsync(cancellationToken),
            LeaveAccrualHandler => await CreditMonthlyLeaveAsync(job, cancellationToken),
            LeaveResetHandler => await ResetYearEndLeaveAsync(job, cancellationToken),
            ConfiguredNotificationHandler => await PublishConfiguredNotificationAsync(job),
            ConfiguredActionHandler => await ExecuteConfiguredActionAsync(job, cancellationToken),
            _ => throw new InvalidOperationException($"No executor is available for handler '{job.HandlerKey}'.")
        };

    private async Task<ScheduledJobRunResult> DispatchNotificationsAsync(CancellationToken cancellationToken)
    {
        var processed = await notifications.ProcessPendingAsync(cancellationToken);
        return new ScheduledJobRunResult { SuccessCount = processed, Message = processed == 0 ? "No queued emails found." : $"Processed {processed} queued emails." };
    }

    private async Task<ScheduledJobRunResult> PublishConfiguredNotificationAsync(ScheduledJob job)
    {
        var config = System.Text.Json.JsonSerializer.Deserialize<ConfiguredNotificationJob>(job.ConfigJson, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new ConfiguredNotificationJob();
        if (string.IsNullOrWhiteSpace(config.EventCode)) throw new InvalidOperationException("Event code is required for configurable notification jobs.");
        if (string.IsNullOrWhiteSpace(config.ResourceType)) throw new InvalidOperationException("Record type is required for configurable notification jobs.");
        await notifications.PublishEventAsync(new NotificationEvent
        {
            EventCode = config.EventCode.Trim(),
            ResourceType = config.ResourceType.Trim(),
            ResourceId = string.IsNullOrWhiteSpace(config.ResourceId) ? job.JobCode : config.ResourceId.Trim(),
            ClientId = config.ClientId <= 0 ? null : config.ClientId,
            ActorName = "Scheduled Job",
            ActorEmail = "scheduler@system.local",
            PayloadJson = string.IsNullOrWhiteSpace(config.PayloadJson) ? "{}" : config.PayloadJson
        });
        return new ScheduledJobRunResult { SuccessCount = 1, Message = $"Notification event {config.EventCode} published." };
    }

    private async Task<ScheduledJobRunResult> ExecuteConfiguredActionAsync(ScheduledJob job, CancellationToken cancellationToken)
    {
        var jobConfig = System.Text.Json.JsonSerializer.Deserialize<ConfiguredActionJob>(job.ConfigJson, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new ConfiguredActionJob();
        if (jobConfig.ActionId <= 0) throw new InvalidOperationException("Select a job action.");
        await using var db = Db();
        await db.OpenAsync(cancellationToken);
        var action = await db.QueryFirstOrDefaultAsync<ScheduledJobAction>("SELECT * FROM scheduled_job_actions WHERE Id=@ActionId AND IsActive=TRUE", jobConfig);
        if (action is null) throw new InvalidOperationException("Configured job action is inactive or unavailable.");
        return action.ActionType switch
        {
            "Notification Event" => await ExecuteNotificationActionAsync(action),
            "Internal API Call" => await ExecuteInternalApiActionAsync(action, cancellationToken),
            "Stored Procedure" => await ExecuteStoredProcedureActionAsync(db, action, cancellationToken),
            "Report Email" => await ExecuteReportEmailActionAsync(action),
            "Workflow Trigger" => await ExecuteWorkflowTriggerActionAsync(action),
            _ => throw new InvalidOperationException($"Action type '{action.ActionType}' is not supported.")
        };
    }

    private async Task<ScheduledJobRunResult> ExecuteNotificationActionAsync(ScheduledJobAction action)
    {
        var config = System.Text.Json.JsonSerializer.Deserialize<ConfiguredNotificationJob>(action.ConfigJson, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new ConfiguredNotificationJob();
        if (string.IsNullOrWhiteSpace(config.EventCode)) throw new InvalidOperationException("Event code is required.");
        await notifications.PublishEventAsync(new NotificationEvent
        {
            EventCode = config.EventCode.Trim(),
            ResourceType = string.IsNullOrWhiteSpace(config.ResourceType) ? "ScheduledJob" : config.ResourceType.Trim(),
            ResourceId = string.IsNullOrWhiteSpace(config.ResourceId) ? action.ActionCode : config.ResourceId.Trim(),
            ClientId = config.ClientId <= 0 ? null : config.ClientId,
            ActorName = "Scheduled Job",
            ActorEmail = "scheduler@system.local",
            PayloadJson = string.IsNullOrWhiteSpace(config.PayloadJson) ? "{}" : config.PayloadJson
        });
        return new ScheduledJobRunResult { SuccessCount = 1, Message = $"Notification event {config.EventCode} published from action {action.ActionCode}." };
    }

    private static async Task<ScheduledJobRunResult> ExecuteStoredProcedureActionAsync(MySqlConnection db, ScheduledJobAction action, CancellationToken cancellationToken)
    {
        var config = System.Text.Json.JsonSerializer.Deserialize<StoredProcedureJobAction>(action.ConfigJson, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new StoredProcedureJobAction();
        if (string.IsNullOrWhiteSpace(config.ProcedureName)) throw new InvalidOperationException("Procedure name is required.");
        var procedureName = config.ProcedureName.Trim();
        if (!procedureName.StartsWith("job_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only approved job procedures with prefix 'job_' can be scheduled.");
        var exists = await db.ExecuteScalarAsync<int>(new CommandDefinition(@"SELECT COUNT(*) FROM information_schema.ROUTINES
WHERE ROUTINE_SCHEMA=DATABASE() AND ROUTINE_TYPE='PROCEDURE' AND ROUTINE_NAME=@ProcedureName", new { ProcedureName = procedureName }, cancellationToken: cancellationToken));
        if (exists == 0) throw new InvalidOperationException($"Stored procedure '{procedureName}' was not found.");
        var parameters = config.Parameters ?? new Dictionary<string, string>();
        var names = parameters.Keys.Select(NormalizeProcedureParameter).ToList();
        var sql = names.Count == 0 ? $"CALL `{procedureName}`();" : $"CALL `{procedureName}`({string.Join(",", names.Select(name => $"@{name}"))});";
        var dynamic = new DynamicParameters();
        foreach (var (key, value) in parameters) dynamic.Add(NormalizeProcedureParameter(key), value);
        var affected = await db.ExecuteAsync(new CommandDefinition(sql, dynamic, cancellationToken: cancellationToken));
        return new ScheduledJobRunResult { SuccessCount = 1, Message = $"Procedure {procedureName} executed. Rows affected: {affected}." };
    }

    private static async Task<ScheduledJobRunResult> ExecuteInternalApiActionAsync(ScheduledJobAction action, CancellationToken cancellationToken)
    {
        var config = JsonSerializer.Deserialize<InternalApiJobAction>(action.ConfigJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new InternalApiJobAction();
        if (string.IsNullOrWhiteSpace(config.Url)) throw new InvalidOperationException("API URL is required.");
        var method = new HttpMethod(string.IsNullOrWhiteSpace(config.Method) ? "POST" : config.Method.Trim().ToUpperInvariant());
        var url = config.Url.Trim();
        if (url.StartsWith('/')) url = $"http://localhost:5062{url}";
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Clamp(config.TimeoutSeconds, 5, 600)) };
        using var request = new HttpRequestMessage(method, url);
        foreach (var header in config.Headers ?? [])
            if (!string.IsNullOrWhiteSpace(header.Key) && !string.IsNullOrWhiteSpace(header.Value))
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        if (method != HttpMethod.Get && method != HttpMethod.Head)
            request.Content = new StringContent(string.IsNullOrWhiteSpace(config.BodyJson) ? "{}" : config.BodyJson, Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"API returned {(int)response.StatusCode}: {body}");
        return new ScheduledJobRunResult { SuccessCount = 1, Message = $"API call completed with {(int)response.StatusCode}." };
    }

    private async Task<ScheduledJobRunResult> ExecuteReportEmailActionAsync(ScheduledJobAction action)
    {
        var config = JsonSerializer.Deserialize<ReportEmailJobAction>(action.ConfigJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new ReportEmailJobAction();
        if (string.IsNullOrWhiteSpace(config.ReportCode)) throw new InvalidOperationException("Report code is required.");
        if (string.IsNullOrWhiteSpace(config.EventCode)) throw new InvalidOperationException("Notification event code is required.");
        var report = await reporting.RunAsync(config.ReportCode, config.Filter ?? new ReportFilter());
        var payload = JsonSerializer.Serialize(new
        {
            reportCode = config.ReportCode,
            reportTitle = report.Title,
            rowCount = report.Rows.Count,
            columns = report.Columns,
            rows = report.Rows.Take(Math.Clamp(config.PreviewRows, 1, 50)).ToList()
        });
        await notifications.PublishEventAsync(new NotificationEvent
        {
            EventCode = config.EventCode.Trim(),
            ResourceType = "Report",
            ResourceId = config.ReportCode,
            ClientId = config.Filter?.ClientId > 0 ? config.Filter.ClientId : null,
            ActorName = "Scheduled Job",
            ActorEmail = "scheduler@system.local",
            PayloadJson = payload
        });
        return new ScheduledJobRunResult { SuccessCount = report.Rows.Count, Message = $"Report {report.Title} generated with {report.Rows.Count} rows and notification event published." };
    }

    private async Task<ScheduledJobRunResult> ExecuteWorkflowTriggerActionAsync(ScheduledJobAction action)
    {
        var config = JsonSerializer.Deserialize<WorkflowTriggerJobAction>(action.ConfigJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new WorkflowTriggerJobAction();
        if (config.WorkflowId <= 0) throw new InvalidOperationException("Workflow ID is required.");
        if (string.IsNullOrWhiteSpace(config.ResourceType)) throw new InvalidOperationException("Resource type is required.");
        var resourceIds = (config.ResourceIds ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (resourceIds.Count == 0) throw new InvalidOperationException("At least one resource ID is required.");
        var requestor = config.RequestorUserId <= 0 ? 1 : config.RequestorUserId;
        var started = 0;
        foreach (var resourceId in resourceIds)
        {
            var existing = await workflows.GetResourceStateAsync(config.ResourceType, resourceId);
            if (existing?.CurrentState == "Pending" && config.SkipIfPending) continue;
            var instance = await workflows.StartAsync(new StartWorkflowRequest
            {
                WorkflowId = config.WorkflowId,
                ResourceType = config.ResourceType,
                ResourceId = resourceId,
                PayloadJson = string.IsNullOrWhiteSpace(config.PayloadJson) ? "{}" : config.PayloadJson
            }, requestor);
            if (instance is not null) started++;
        }
        return new ScheduledJobRunResult { SuccessCount = started, Message = $"Started {started} workflow instance(s)." };
    }

    private async Task<ScheduledJobRunResult> CreditMonthlyLeaveAsync(ScheduledJob job, CancellationToken cancellationToken)
    {
        await using var db = Db();
        await db.OpenAsync(cancellationToken);
        var today = DateTime.Today;
        var periodKey = today.ToString("yyyy-MM");
        var creditDate = new DateTime(today.Year, today.Month, 1);
        var rows = (await db.QueryAsync<LeaveAccrualRow>(@"SELECT lt.id LeaveTypeId,lt.client_id ClientId,lt.code LeaveCode,p.entitlement Entitlement,p.effective_from EffectiveFrom,p.expires_on ExpiresOn,
a.applicability_mode ApplicabilityMode,COALESCE(a.work_location,'') WorkLocation,COALESCE(a.department,'') Department,COALESCE(a.designation,'') Designation,COALESCE(a.gender,'') Gender
FROM leave_types lt
JOIN leave_type_policies p ON p.leave_type_id=lt.id
JOIN leave_type_applicability a ON a.leave_type_id=lt.id
WHERE lt.is_active=TRUE AND lt.type='Paid' AND p.entitlement_period='Monthly' AND p.entitlement>0
AND p.effective_from<=@CreditDate AND (p.expires_on IS NULL OR p.expires_on>=@CreditDate)", new { CreditDate = creditDate })).ToList();
        var credited = 0;
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var employees = await GetApplicableEmployeesAsync(db, row, cancellationToken);
            foreach (var employee in employees)
            {
                var dedupKey = $"ACCRUAL:{employee.Id}:{row.LeaveTypeId}:{periodKey}";
                var current = await CurrentLeaveBalanceAsync(db, employee.Id, row.LeaveTypeId, cancellationToken);
                var next = current + row.Entitlement;
                var inserted = await db.ExecuteAsync(@"INSERT IGNORE INTO employee_leave_ledger (ClientId,EmployeeId,LeaveTypeId,LeaveCode,TransactionDate,PeriodKey,TransactionType,Quantity,BalanceAfter,ReferenceType,ReferenceId,DedupKey,Remarks)
VALUES (@ClientId,@EmployeeId,@LeaveTypeId,@LeaveCode,@TransactionDate,@PeriodKey,'Monthly Accrual',@Quantity,@BalanceAfter,'ScheduledJob',@JobCode,@DedupKey,@Remarks);", new { row.ClientId, EmployeeId = employee.Id, row.LeaveTypeId, row.LeaveCode, TransactionDate = creditDate, PeriodKey = periodKey, Quantity = row.Entitlement, BalanceAfter = next, job.JobCode, DedupKey = dedupKey, Remarks = "Monthly auto credit" });
                if (inserted == 0) continue;
                await UpsertLeaveBalanceAsync(db, row.ClientId, employee.Id, row.LeaveTypeId, creditDate, next, cancellationToken);
                credited++;
            }
        }
        return new ScheduledJobRunResult { SuccessCount = credited, Message = credited == 0 ? "No monthly leave credits were due." : $"Credited {credited} employee leave balances." };
    }

    private async Task<ScheduledJobRunResult> ResetYearEndLeaveAsync(ScheduledJob job, CancellationToken cancellationToken)
    {
        await using var db = Db();
        await db.OpenAsync(cancellationToken);
        var today = DateTime.Today;
        var rows = (await db.QueryAsync<LeaveAccrualRow>(@"SELECT lt.id LeaveTypeId,lt.client_id ClientId,lt.code LeaveCode,p.carry_forward_unused_leaves CarryForwardUnusedLeaves,p.max_carry_forward_limit MaxCarryForwardLimit,
a.applicability_mode ApplicabilityMode,COALESCE(a.work_location,'') WorkLocation,COALESCE(a.department,'') Department,COALESCE(a.designation,'') Designation,COALESCE(a.gender,'') Gender
FROM leave_types lt
JOIN leave_type_policies p ON p.leave_type_id=lt.id
JOIN leave_type_applicability a ON a.leave_type_id=lt.id
WHERE lt.is_active=TRUE AND lt.type='Paid' AND p.reset_enabled=TRUE AND p.reset_frequency='Yearly'", new { })).ToList();
        var reset = 0;
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var employees = await GetApplicableEmployeesAsync(db, row, cancellationToken);
            foreach (var employee in employees)
            {
                var current = await CurrentLeaveBalanceAsync(db, employee.Id, row.LeaveTypeId, cancellationToken);
                if (current <= 0) continue;
                var carry = row.CarryForwardUnusedLeaves ? Math.Min(current, row.MaxCarryForwardLimit ?? current) : 0;
                var adjustment = carry - current;
                var dedupKey = $"RESET:{employee.Id}:{row.LeaveTypeId}:{today:yyyy}";
                var inserted = await db.ExecuteAsync(@"INSERT IGNORE INTO employee_leave_ledger (ClientId,EmployeeId,LeaveTypeId,LeaveCode,TransactionDate,PeriodKey,TransactionType,Quantity,BalanceAfter,ReferenceType,ReferenceId,DedupKey,Remarks)
VALUES (@ClientId,@EmployeeId,@LeaveTypeId,@LeaveCode,@TransactionDate,@PeriodKey,'Year End Reset',@Quantity,@BalanceAfter,'ScheduledJob',@JobCode,@DedupKey,@Remarks);", new { row.ClientId, EmployeeId = employee.Id, row.LeaveTypeId, row.LeaveCode, TransactionDate = today, PeriodKey = today.ToString("yyyy"), Quantity = adjustment, BalanceAfter = carry, job.JobCode, DedupKey = dedupKey, Remarks = row.CarryForwardUnusedLeaves ? "Year-end carry forward applied" : "Year-end balance lapsed" });
                if (inserted == 0) continue;
                await UpsertLeaveBalanceAsync(db, row.ClientId, employee.Id, row.LeaveTypeId, today, carry, cancellationToken);
                reset++;
            }
        }
        return new ScheduledJobRunResult { SuccessCount = reset, Message = reset == 0 ? "No leave balances required reset." : $"Reset {reset} employee leave balances." };
    }

    private static async Task<IEnumerable<EmployeeScopeRow>> GetApplicableEmployeesAsync(MySqlConnection db, LeaveAccrualRow rule, CancellationToken cancellationToken)
    {
        var rows = await db.QueryAsync<EmployeeScopeRow>(@"SELECT e.Id,e.ClientId,e.EmployeeCode,e.Department,e.Designation,e.Gender,COALESCE(w.Name,'') WorkLocation
FROM employees e
LEFT JOIN worklocations w ON w.Id=e.WorkLocationId
WHERE e.ClientId=@ClientId AND e.IsActive=TRUE", new { rule.ClientId });
        return rows.Where(employee =>
            Matches(rule.WorkLocation, employee.WorkLocation) &&
            Matches(rule.Department, employee.Department) &&
            Matches(rule.Designation, employee.Designation) &&
            Matches(rule.Gender, employee.Gender)).ToList();
    }

    private static async Task<decimal> CurrentLeaveBalanceAsync(MySqlConnection db, int employeeId, int leaveTypeId, CancellationToken cancellationToken) =>
        await db.ExecuteScalarAsync<decimal?>(new CommandDefinition(@"SELECT balance_count FROM employee_leave_balances
WHERE employee_id=@EmployeeId AND leave_type_id=@LeaveTypeId
ORDER BY balance_date DESC,id DESC LIMIT 1", new { EmployeeId = employeeId, LeaveTypeId = leaveTypeId }, cancellationToken: cancellationToken)) ?? 0;

    private static Task UpsertLeaveBalanceAsync(MySqlConnection db, int clientId, int employeeId, int leaveTypeId, DateTime balanceDate, decimal balance, CancellationToken cancellationToken) =>
        db.ExecuteAsync(new CommandDefinition(@"INSERT INTO employee_leave_balances (client_id,employee_id,leave_type_id,balance_date,balance_count)
VALUES (@ClientId,@EmployeeId,@LeaveTypeId,@BalanceDate,@Balance)
ON DUPLICATE KEY UPDATE balance_count=VALUES(balance_count);", new { ClientId = clientId, EmployeeId = employeeId, LeaveTypeId = leaveTypeId, BalanceDate = balanceDate.Date, Balance = balance }, cancellationToken: cancellationToken));

    private static bool Matches(string? configured, string actual)
    {
        if (string.IsNullOrWhiteSpace(configured) || configured.Equals("All", StringComparison.OrdinalIgnoreCase)) return true;
        var options = configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return options.Length == 0 || options.Any(item => item.Equals(actual, StringComparison.OrdinalIgnoreCase));
    }

    private static DateTime? CalculateNextRun(ScheduledJob job, DateTime from)
    {
        if (!job.IsEnabled) return null;
        if (job.ScheduleType.Equals("Interval", StringComparison.OrdinalIgnoreCase))
            return from.AddMinutes(Math.Max(1, job.IntervalMinutes));
        if (!TimeSpan.TryParse(job.DailyRunTime, out var time)) time = new TimeSpan(1, 0, 0);
        var candidate = from.Date.Add(time);
        if (job.ScheduleType.Equals("Monthly", StringComparison.OrdinalIgnoreCase))
        {
            var day = Math.Clamp(job.MonthlyRunDay, 1, DateTime.DaysInMonth(from.Year, from.Month));
            candidate = new DateTime(from.Year, from.Month, day).Add(time);
            if (candidate <= from)
            {
                var nextMonth = from.AddMonths(1);
                day = Math.Clamp(job.MonthlyRunDay, 1, DateTime.DaysInMonth(nextMonth.Year, nextMonth.Month));
                candidate = new DateTime(nextMonth.Year, nextMonth.Month, day).Add(time);
            }
            return candidate;
        }
        return candidate <= from ? candidate.AddDays(1) : candidate;
    }

    private static void Normalize(ScheduledJob job)
    {
        job.JobCode = job.JobCode.Trim().ToUpperInvariant();
        job.JobName = job.JobName.Trim();
        job.Description = job.Description?.Trim() ?? "";
        job.HandlerKey = job.HandlerKey.Trim().ToUpperInvariant();
        job.ScheduleType = string.IsNullOrWhiteSpace(job.ScheduleType) ? "Interval" : job.ScheduleType;
        job.IntervalMinutes = Math.Max(1, job.IntervalMinutes);
        job.MonthlyRunDay = Math.Clamp(job.MonthlyRunDay, 1, 31);
        job.DailyRunTime = string.IsNullOrWhiteSpace(job.DailyRunTime) ? "01:00" : job.DailyRunTime;
        job.ConfigJson = string.IsNullOrWhiteSpace(job.ConfigJson) ? "{}" : job.ConfigJson;
    }

    private static void NormalizeAction(ScheduledJobAction action)
    {
        action.ActionCode = action.ActionCode.Trim().ToUpperInvariant();
        action.ActionName = action.ActionName.Trim();
        action.ActionType = string.IsNullOrWhiteSpace(action.ActionType) ? "Notification Event" : action.ActionType.Trim();
        action.Description = action.Description?.Trim() ?? "";
        action.ConfigJson = string.IsNullOrWhiteSpace(action.ConfigJson) ? "{}" : action.ConfigJson;
    }

    private static void ValidateAction(ScheduledJobAction action)
    {
        if (string.IsNullOrWhiteSpace(action.ActionCode) || string.IsNullOrWhiteSpace(action.ActionName)) throw new InvalidOperationException("Action code and action name are required.");
        if (action.ActionType is not ("Notification Event" or "Internal API Call" or "Stored Procedure" or "Report Email" or "Workflow Trigger")) throw new InvalidOperationException("Select a supported action type.");
        if (action.ActionType == "Notification Event")
        {
            var config = System.Text.Json.JsonSerializer.Deserialize<ConfiguredNotificationJob>(action.ConfigJson, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new ConfiguredNotificationJob();
            if (string.IsNullOrWhiteSpace(config.EventCode)) throw new InvalidOperationException("Event code is required.");
        }
        if (action.ActionType == "Stored Procedure")
        {
            var config = System.Text.Json.JsonSerializer.Deserialize<StoredProcedureJobAction>(action.ConfigJson, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new StoredProcedureJobAction();
            if (string.IsNullOrWhiteSpace(config.ProcedureName)) throw new InvalidOperationException("Procedure name is required.");
        }
        if (action.ActionType == "Internal API Call")
        {
            var config = JsonSerializer.Deserialize<InternalApiJobAction>(action.ConfigJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new InternalApiJobAction();
            if (string.IsNullOrWhiteSpace(config.Url)) throw new InvalidOperationException("API URL is required.");
        }
        if (action.ActionType == "Report Email")
        {
            var config = JsonSerializer.Deserialize<ReportEmailJobAction>(action.ConfigJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new ReportEmailJobAction();
            if (string.IsNullOrWhiteSpace(config.ReportCode) || string.IsNullOrWhiteSpace(config.EventCode)) throw new InvalidOperationException("Report code and notification event code are required.");
        }
        if (action.ActionType == "Workflow Trigger")
        {
            var config = JsonSerializer.Deserialize<WorkflowTriggerJobAction>(action.ConfigJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new WorkflowTriggerJobAction();
            if (config.WorkflowId <= 0 || string.IsNullOrWhiteSpace(config.ResourceType) || string.IsNullOrWhiteSpace(config.ResourceIds)) throw new InvalidOperationException("Workflow, resource type, and resource IDs are required.");
        }
    }

    private static string NormalizeProcedureParameter(string value)
    {
        var text = new string(value.Where(character => char.IsLetterOrDigit(character) || character == '_').ToArray());
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("Stored procedure parameter names can contain only letters, numbers, and underscore.");
        return text;
    }

    private static async Task EnsureDefaultsAsync(MySqlConnection db)
    {
        await SeedJobAsync(db, new ScheduledJob { JobCode = "NOTIFICATION_QUEUE_DISPATCH", JobName = "Send queued emails", Description = "Sends pending email notifications.", HandlerKey = NotificationHandler, ScheduleType = "Interval", IntervalMinutes = 1, DailyRunTime = "00:00", ConfigJson = "{}" });
        await SeedJobAsync(db, new ScheduledJob { JobCode = "LEAVE_MONTHLY_ACCRUAL", JobName = "Monthly leave credit", Description = "Credits monthly leave entitlement to employees.", HandlerKey = LeaveAccrualHandler, ScheduleType = "Monthly", MonthlyRunDay = 1, DailyRunTime = "01:00", ConfigJson = "{}" });
        await SeedJobAsync(db, new ScheduledJob { JobCode = "LEAVE_YEAR_END_RESET", JobName = "Year-end leave reset", Description = "Carries forward or lapses leave balances at year end.", HandlerKey = LeaveResetHandler, ScheduleType = "Monthly", MonthlyRunDay = 31, DailyRunTime = "01:30", ConfigJson = "{}" });
    }

    private static Task SeedJobAsync(MySqlConnection db, ScheduledJob job)
    {
        job.NextRunAt = CalculateNextRun(job, DateTime.Now);
        return db.ExecuteAsync(@"INSERT INTO scheduled_jobs (JobCode,JobName,Description,HandlerKey,IsEnabled,ScheduleType,IntervalMinutes,DailyRunTime,MonthlyRunDay,ConfigJson,NextRunAt)
VALUES (@JobCode,@JobName,@Description,@HandlerKey,TRUE,@ScheduleType,@IntervalMinutes,@DailyRunTime,@MonthlyRunDay,@ConfigJson,@NextRunAt)
ON DUPLICATE KEY UPDATE JobName=VALUES(JobName),Description=VALUES(Description),HandlerKey=VALUES(HandlerKey);", job);
    }

    private static async Task EnsureClientColumnsAsync(MySqlConnection db)
    {
        var hasClientId = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*)
FROM information_schema.COLUMNS
WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='leave_types' AND COLUMN_NAME='client_id';");
        if (hasClientId == 0)
            await db.ExecuteAsync(@"ALTER TABLE leave_types ADD COLUMN client_id INT NOT NULL DEFAULT 1 AFTER id;");
    }

    private sealed class LeaveAccrualRow
    {
        public int ClientId { get; set; }
        public int LeaveTypeId { get; set; }
        public string LeaveCode { get; set; } = string.Empty;
        public decimal Entitlement { get; set; }
        public bool CarryForwardUnusedLeaves { get; set; }
        public decimal? MaxCarryForwardLimit { get; set; }
        public string ApplicabilityMode { get; set; } = string.Empty;
        public string WorkLocation { get; set; } = string.Empty;
        public string Department { get; set; } = string.Empty;
        public string Designation { get; set; } = string.Empty;
        public string Gender { get; set; } = string.Empty;
    }

    private sealed class EmployeeScopeRow
    {
        public int Id { get; set; }
        public int ClientId { get; set; }
        public string EmployeeCode { get; set; } = string.Empty;
        public string WorkLocation { get; set; } = string.Empty;
        public string Department { get; set; } = string.Empty;
        public string Designation { get; set; } = string.Empty;
        public string Gender { get; set; } = string.Empty;
    }

    private sealed class ConfiguredNotificationJob
    {
        public string EventCode { get; set; } = string.Empty;
        public string ResourceType { get; set; } = "ScheduledJob";
        public string ResourceId { get; set; } = string.Empty;
        public int? ClientId { get; set; }
        public string PayloadJson { get; set; } = "{}";
    }

    private sealed class ConfiguredActionJob
    {
        public int ActionId { get; set; }
    }

    private sealed class StoredProcedureJobAction
    {
        public string ProcedureName { get; set; } = string.Empty;
        public Dictionary<string, string> Parameters { get; set; } = [];
    }

    private sealed class InternalApiJobAction
    {
        public string Method { get; set; } = "POST";
        public string Url { get; set; } = string.Empty;
        public string BodyJson { get; set; } = "{}";
        public Dictionary<string, string> Headers { get; set; } = [];
        public int TimeoutSeconds { get; set; } = 60;
    }

    private sealed class ReportEmailJobAction
    {
        public string ReportCode { get; set; } = string.Empty;
        public string EventCode { get; set; } = string.Empty;
        public ReportFilter? Filter { get; set; } = new();
        public int PreviewRows { get; set; } = 10;
    }

    private sealed class WorkflowTriggerJobAction
    {
        public int WorkflowId { get; set; }
        public string ResourceType { get; set; } = string.Empty;
        public string ResourceIds { get; set; } = string.Empty;
        public int RequestorUserId { get; set; } = 1;
        public bool SkipIfPending { get; set; } = true;
        public string PayloadJson { get; set; } = "{}";
    }
}
