using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using System.Text.RegularExpressions;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public partial class PayRunRepository(IConfiguration configuration, TaxEngineRepository taxEngineRepository, EngineRuntimeMonitor? engineMonitor = null)
{
    private MySqlConnection CreateConnection()
    {
        var connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Connection string 'Default' is not configured.");
        var builder = new MySqlConnectionStringBuilder(connectionString)
        {
            DefaultCommandTimeout = 300
        };
        if (string.IsNullOrWhiteSpace(builder.Database))
            builder.Database = "payroll";
        return new MySqlConnection(builder.ConnectionString);
    }

    public async Task InitializeAsync()
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync(@"
CREATE TABLE IF NOT EXISTS payruns (
    Id INT PRIMARY KEY AUTO_INCREMENT,
    ClientId INT NOT NULL DEFAULT 0,
    ClientName VARCHAR(250),
    PayPeriod VARCHAR(7) NOT NULL,
    RunCode VARCHAR(40) NOT NULL DEFAULT 'REGULAR',
    AttendancePolicyBatchId VARCHAR(64) NOT NULL DEFAULT '',
    RunType VARCHAR(30) NOT NULL DEFAULT 'Regular',
    RunName VARCHAR(120) NOT NULL DEFAULT '',
    Reason VARCHAR(500) NOT NULL DEFAULT '',
    PayDate DATE NOT NULL,
    TotalWorkingDays INT NOT NULL,
    Status VARCHAR(30) NOT NULL DEFAULT 'Draft',
    RequestJson JSON NULL,
    ProcessingStartedAt DATETIME NULL,
    ProcessingCompletedAt DATETIME NULL,
    ProcessingError VARCHAR(1000) NOT NULL DEFAULT '',
    PayrollCost DECIMAL(18,2) NOT NULL DEFAULT 0,
    NetPay DECIMAL(18,2) NOT NULL DEFAULT 0,
    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    UNIQUE KEY UX_PayRuns_Client_Period_Code (ClientId, PayPeriod, RunCode)
);
CREATE TABLE IF NOT EXISTS payrunemployees (
    Id INT PRIMARY KEY AUTO_INCREMENT,
    PayRunId INT NOT NULL,
    EmployeeId INT NOT NULL,
    ClientId INT NOT NULL DEFAULT 0,
    ClientName VARCHAR(250),
    EmployeeCode VARCHAR(50) NOT NULL,
    EmployeeName VARCHAR(250) NOT NULL,
    Department VARCHAR(100),
    PresentDays DECIMAL(5,2) NULL,
    PayableDays DECIMAL(5,2) NOT NULL,
    MonthlyGross DECIMAL(18,2) NOT NULL DEFAULT 0,
    GrossPay DECIMAL(18,2) NOT NULL DEFAULT 0,
    StatutoryDeductions DECIMAL(18,2) NOT NULL DEFAULT 0,
    OneTimeEarnings DECIMAL(18,2) NOT NULL DEFAULT 0,
    OneTimeDeductions DECIMAL(18,2) NOT NULL DEFAULT 0,
    NetPay DECIMAL(18,2) NOT NULL DEFAULT 0,
    IsSkipped BOOLEAN NOT NULL DEFAULT FALSE,
    PaymentStatus VARCHAR(30) NOT NULL DEFAULT 'Pending',
    DetailsJson JSON NOT NULL,
    UNIQUE KEY UX_PayRunEmployees_Run_Employee (PayRunId, EmployeeId)
);
CREATE TABLE IF NOT EXISTS payrolladjustments (
    Id INT PRIMARY KEY AUTO_INCREMENT,
    ClientId INT NOT NULL,
    EmployeeId INT NOT NULL,
    EmployeeName VARCHAR(250) NOT NULL DEFAULT '',
    EmployeeCode VARCHAR(50) NOT NULL DEFAULT '',
    ComponentId INT NOT NULL DEFAULT 0,
    ComponentCode VARCHAR(50) NOT NULL DEFAULT '',
    ComponentName VARCHAR(150) NOT NULL,
    AdjustmentType VARCHAR(30) NOT NULL,
    Amount DECIMAL(18,2) NOT NULL,
    PayPeriod VARCHAR(7) NOT NULL,
    PayRunType VARCHAR(30) NOT NULL DEFAULT 'Regular',
    ReasonCode VARCHAR(80) NOT NULL DEFAULT '',
    Notes VARCHAR(500) NOT NULL DEFAULT '',
    Taxable BOOLEAN NOT NULL DEFAULT TRUE,
    Status VARCHAR(30) NOT NULL DEFAULT 'Approved',
    PayRunId INT NULL,
    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    INDEX IX_PayrollAdjustments_Client_Period_Status (ClientId, PayPeriod, Status)
);
CREATE TABLE IF NOT EXISTS payrun_step_logs (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    PayRunId INT NOT NULL,
    EmployeeId INT NULL,
    StepNumber INT NOT NULL,
    StepName VARCHAR(160) NOT NULL,
    StartTime DATETIME NOT NULL,
    EndTime DATETIME NULL,
    DurationMs INT NOT NULL DEFAULT 0,
    InputJson JSON NULL,
    RuleJson JSON NULL,
    FormulaJson JSON NULL,
    OldValueJson JSON NULL,
    NewValueJson JSON NULL,
    OutputJson JSON NULL,
    Status VARCHAR(30) NOT NULL DEFAULT 'Success',
    Warning VARCHAR(1000) NOT NULL DEFAULT '',
    ErrorMessage VARCHAR(1000) NOT NULL DEFAULT '',
    PerformedBy VARCHAR(190) NOT NULL DEFAULT '',
    MachineName VARCHAR(190) NOT NULL DEFAULT '',
    Version VARCHAR(40) NOT NULL DEFAULT '1.0',
    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    INDEX IX_PayRunStepLogs_Run_Step (PayRunId, StepNumber),
    INDEX IX_PayRunStepLogs_Run_Employee (PayRunId, EmployeeId)
);
CREATE TABLE IF NOT EXISTS payroll_validation_issues (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    PayRunId INT NOT NULL,
    EmployeeId INT NULL,
    EmployeeCode VARCHAR(50) NOT NULL DEFAULT '',
    Scope VARCHAR(40) NOT NULL DEFAULT 'Employee',
    IssueType VARCHAR(40) NOT NULL DEFAULT 'Validation',
    Severity VARCHAR(20) NOT NULL DEFAULT 'Warning',
    StepName VARCHAR(160) NOT NULL DEFAULT '',
    Message VARCHAR(1000) NOT NULL,
    DataJson JSON NULL,
    IsBlocking BOOLEAN NOT NULL DEFAULT FALSE,
    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    INDEX IX_PayrollValidation_Run_Blocking (PayRunId, IsBlocking),
    INDEX IX_PayrollValidation_Run_Employee (PayRunId, EmployeeId)
);
CREATE TABLE IF NOT EXISTS payroll_calculation_traces (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    PayRunId INT NOT NULL,
    EmployeeId INT NOT NULL,
    EmployeeCode VARCHAR(50) NOT NULL DEFAULT '',
    ComponentCode VARCHAR(80) NOT NULL,
    ComponentName VARCHAR(180) NOT NULL,
    ParentComponentCode VARCHAR(80) NOT NULL DEFAULT '',
    TraceOrder INT NOT NULL,
    RuleUsed VARCHAR(250) NOT NULL DEFAULT '',
    FormulaUsed VARCHAR(500) NOT NULL DEFAULT '',
    BaseAmount DECIMAL(18,2) NOT NULL DEFAULT 0,
    Factor DECIMAL(18,6) NOT NULL DEFAULT 0,
    CalculatedAmount DECIMAL(18,2) NOT NULL DEFAULT 0,
    InputJson JSON NULL,
    OutputJson JSON NULL,
    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    INDEX IX_PayrollTrace_Run_Employee (PayRunId, EmployeeId, TraceOrder),
    INDEX IX_PayrollTrace_Run_Component (PayRunId, ComponentCode)
);
CREATE TABLE IF NOT EXISTS payroll_reconciliation_results (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    PayRunId INT NOT NULL,
    CheckName VARCHAR(160) NOT NULL,
    ExpectedAmount DECIMAL(18,2) NOT NULL DEFAULT 0,
    ActualAmount DECIMAL(18,2) NOT NULL DEFAULT 0,
    DifferenceAmount DECIMAL(18,2) NOT NULL DEFAULT 0,
    Status VARCHAR(30) NOT NULL DEFAULT 'Passed',
    DetailsJson JSON NULL,
    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    INDEX IX_PayrollRecon_Run_Status (PayRunId, Status)
);" );
        await EnsureForeignKeyAsync(connection, "payrunemployees", "FK_PayRunEmployees_PayRuns", "FOREIGN KEY (PayRunId) REFERENCES payruns(Id) ON DELETE CASCADE");
        await EnsureForeignKeyAsync(connection, "payrolladjustments", "FK_PayrollAdjustments_Employees", "FOREIGN KEY (EmployeeId) REFERENCES employees(Id)");
        await EnsureForeignKeyAsync(connection, "payrolladjustments", "FK_PayrollAdjustments_PayRuns", "FOREIGN KEY (PayRunId) REFERENCES payruns(Id) ON DELETE SET NULL");
        await EnsureColumnAsync(connection, "payrunemployees", "PaymentDate", "DATE NULL");
        await EnsureColumnAsync(connection, "payrunemployees", "ClientId", "INT NOT NULL DEFAULT 0");
        await EnsureColumnAsync(connection, "payrunemployees", "ClientName", "VARCHAR(250) NULL");
        await EnsureColumnAsync(connection, "payrunemployees", "ManualTds", "DECIMAL(18,2) NOT NULL DEFAULT 0");
        // Existing actual-presence nullability belongs to the explicit reviewed
        // migration. Startup must neither widen it implicitly nor coerce a later
        // reviewed NULL value back to NOT NULL/zero.
        await connection.ExecuteAsync("ALTER TABLE payrunemployees MODIFY PayableDays DECIMAL(5,2) NOT NULL;");
        await EnsureColumnAsync(connection, "payruns", "ClientId", "INT NOT NULL DEFAULT 0");
        await EnsureColumnAsync(connection, "payruns", "ClientName", "VARCHAR(250) NULL");
        await EnsureColumnAsync(connection, "payruns", "RunCode", "VARCHAR(40) NOT NULL DEFAULT 'REGULAR'");
        await EnsureColumnAsync(connection, "payruns", "AttendancePolicyBatchId", "VARCHAR(64) NOT NULL DEFAULT '' AFTER RunCode");
        await EnsureColumnAsync(connection, "payruns", "RunType", "VARCHAR(30) NOT NULL DEFAULT 'Regular'");
        await EnsureColumnAsync(connection, "payruns", "RunName", "VARCHAR(120) NOT NULL DEFAULT ''");
        await EnsureColumnAsync(connection, "payruns", "Reason", "VARCHAR(500) NOT NULL DEFAULT ''");
        await EnsureColumnAsync(connection, "payruns", "RequestJson", "JSON NULL");
        await EnsureColumnAsync(connection, "payruns", "ProcessingStartedAt", "DATETIME NULL");
        await EnsureColumnAsync(connection, "payruns", "ProcessingCompletedAt", "DATETIME NULL");
        await EnsureColumnAsync(connection, "payruns", "ProcessingError", "VARCHAR(1000) NOT NULL DEFAULT ''");
        await EnsurePayRunIndexAsync(connection);
        await CleanupObsoletePayRunValidationIssuesAsync(connection);
        await PayrollDataTableStore.EnsureAsync(connection);
        await connection.ExecuteAsync(@"UPDATE payrunemployees p JOIN employees e ON e.Id = p.EmployeeId LEFT JOIN clients c ON c.Id = e.ClientId SET p.ClientId = e.ClientId, p.ClientName = c.Name WHERE p.ClientId = 0 OR p.ClientName IS NULL;");
    }

    public async Task<IEnumerable<PayRun>> GetAllAsync(int? clientId = null)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        return await connection.QueryAsync<PayRun>(@"
SELECT r.*, COUNT(e.Id) AS EmployeeCount
FROM payruns r
LEFT JOIN payrunemployees e ON e.PayRunId = r.Id AND e.IsSkipped = FALSE
WHERE (@ClientId IS NULL OR r.ClientId=@ClientId)
GROUP BY r.Id
ORDER BY r.PayPeriod DESC, r.Id DESC;", new { ClientId = clientId });
    }

    public async Task<PayRun?> GetAsync(int id)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        using var results = await connection.QueryMultipleAsync(@"
SELECT r.*, COUNT(e.Id) AS EmployeeCount
FROM payruns r LEFT JOIN payrunemployees e ON e.PayRunId = r.Id AND e.IsSkipped = FALSE
WHERE r.Id = @Id GROUP BY r.Id;
SELECT * FROM payrunemployees WHERE PayRunId = @Id ORDER BY EmployeeName;", new { Id = id });
        var payRun = await results.ReadFirstOrDefaultAsync<PayRun>();
        if (payRun is not null)
        {
            payRun.Employees = (await results.ReadAsync<PayRunEmployee>()).ToList();
            await ApplyLeaveBreakdownAsync(connection, payRun);
            await ApplyPreviousRunComparisonAsync(connection, payRun);
        }
        return payRun;
    }

    public async Task<PayRunDiagnostics?> GetDiagnosticsAsync(int id)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        var exists = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM payruns WHERE Id=@Id", new { Id = id });
        if (exists == 0) return null;
        var diagnostics = new PayRunDiagnostics { PayRunId = id };
        diagnostics.StepLogs = (await connection.QueryAsync<PayRunStepLog>("SELECT * FROM payrun_step_logs WHERE PayRunId=@Id ORDER BY StepNumber, EmployeeId, Id", new { Id = id })).ToList();
        var issues = (await connection.QueryAsync<PayrollValidationIssue>("SELECT * FROM payroll_validation_issues WHERE PayRunId=@Id ORDER BY IsBlocking DESC, Severity, EmployeeCode, Id", new { Id = id })).ToList();
        diagnostics.ValidationIssues = issues.Where(issue => !issue.IssueType.Equals("Exception", StringComparison.OrdinalIgnoreCase)).ToList();
        diagnostics.Exceptions = issues.Where(issue => issue.IssueType.Equals("Exception", StringComparison.OrdinalIgnoreCase)).ToList();
        diagnostics.CalculationTraces = (await connection.QueryAsync<PayrollCalculationTrace>("SELECT * FROM payroll_calculation_traces WHERE PayRunId=@Id ORDER BY EmployeeCode, TraceOrder, Id", new { Id = id })).ToList();
        diagnostics.ReconciliationResults = (await connection.QueryAsync<PayrollReconciliationResult>("SELECT * FROM payroll_reconciliation_results WHERE PayRunId=@Id ORDER BY Status DESC, CheckName", new { Id = id })).ToList();
        return diagnostics;
    }

    public async Task<PayRun?> QueueAsync(CreatePayRunRequest request, string performedBy = "System")
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var client = await connection.QueryFirstOrDefaultAsync<Client>("SELECT * FROM clients WHERE Id = @Id AND IsActive = TRUE FOR UPDATE", new { Id = request.ClientId }, transaction);
        if (client is null) return null;
        var runType = string.Equals(request.RunType, "Off Cycle", StringComparison.OrdinalIgnoreCase) ? "Off Cycle" : "Regular";
        request.AttendancePolicyBatchId = await ResolveAttendancePolicyBatchIdAsync(connection, transaction, request);
        var runName = string.IsNullOrWhiteSpace(request.RunName) ? (runType == "Regular" ? "Regular payroll" : "Off-cycle payroll") : request.RunName.Trim();
        if (runType == "Regular")
        {
            var running = await FindRunningRegularRunAsync(connection, transaction, request);
            if (running is not null)
            {
                await transaction.RollbackAsync();
                throw new PayRunAttemptConflictException(running.Id, running.Status);
            }
            var employees = (await connection.QueryAsync<PayRunSourceEmployee>("SELECT e.Id FROM employees e WHERE " + EmployeeEmploymentRules.EligibleInMonth("e", "@PayPeriod") + " AND e.ClientId=@ClientId ORDER BY e.FirstName,e.LastName FOR UPDATE", new { request.ClientId, request.PayPeriod }, transaction)).ToList();
            var scopedEmployeeIds = await NormalizeRegularEmployeeScopeAsync(connection, transaction, request, employees);
            if (scopedEmployeeIds.Count == 0)
            {
                await transaction.RollbackAsync();
                throw new InvalidOperationException("All selected employees are already covered by another payroll run for this client and month. Select employees who have not been processed yet.");
            }
        }
        var runCode = runType == "Regular" ? RegularRunCode(request) : $"OFF-{DateTime.UtcNow:yyyyMMddHHmmss}";
        var existing = runType == "Regular" ? await FindExistingRegularRunAsync(connection, transaction, request, runCode) : null;
        if (existing is not null)
        {
            if (!LatestAttemptStatuses.Contains(existing.Status))
                return null;
            throw new PayRunAttemptConflictException(existing.Id, existing.Status);
        }
        var requestJson = JsonSerializer.Serialize(request);
        var payRunId = (int)await connection.ExecuteScalarAsync<long>(@"
INSERT INTO payruns (ClientId, ClientName, PayPeriod, RunCode, AttendancePolicyBatchId, RunType, RunName, Reason, PayDate, TotalWorkingDays, Status, RequestJson, ProcessingError)
VALUES (@ClientId, @ClientName, @PayPeriod, @RunCode, @AttendancePolicyBatchId, @RunType, @RunName, @Reason, @PayDate, @TotalWorkingDays, 'Queued', @RequestJson, '');
SELECT LAST_INSERT_ID();", new { request.ClientId, ClientName = client.Name, request.PayPeriod, RunCode = runCode, AttendancePolicyBatchId = request.AttendancePolicyBatchId, RunType = runType, RunName = runName, Reason = request.Reason.Trim(), PayDate = request.PayDate.ToDateTime(TimeOnly.MinValue), request.TotalWorkingDays, RequestJson = requestJson }, transaction);
        await WritePipelineStepLogsAsync(connection, transaction, payRunId, performedBy, []);
        await transaction.CommitAsync();
        return await GetAsync(payRunId);
    }

    public async Task<int> ProcessQueuedAsync(int maxRuns = 1)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        // A connection-scoped lock survives long calculations, but is released by MySQL after a worker crash.
        await connection.ExecuteAsync(@"UPDATE payruns SET Status='Failed', ProcessingCompletedAt=UTC_TIMESTAMP(),
ProcessingError='Payroll worker stopped before completion. Review diagnostics and run payroll again.'
WHERE Status='Processing' AND COALESCE(ProcessingStartedAt,CreatedAt)<DATE_SUB(UTC_TIMESTAMP(), INTERVAL @Minutes MINUTE)
AND IS_FREE_LOCK(CONCAT('payroll:',MD5(DATABASE()),':',Id))=1", new { Minutes = Math.Max(5, configuration.GetValue<int?>("Payroll:AbandonedRunMinutes") ?? 120) });
        var runIds = (await connection.QueryAsync<int>("SELECT Id FROM payruns WHERE Status='Queued' ORDER BY CreatedAt, Id LIMIT @MaxRuns", new { MaxRuns = maxRuns })).ToList();
        foreach (var id in runIds)
            await ProcessQueuedRunAsync(id);
        return runIds.Count;
    }

    public Task<PayRun?> ProcessQueuedRunAsync(int payRunId, string performedBy = "Payroll worker") =>
        ProcessQueuedRunCoreAsync(payRunId, performedBy, null);

    private async Task<PayRun?> ProcessQueuedRunCoreAsync(int payRunId, string performedBy, SelectedPayRunProcessingRequest? selected)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        var lockName = await connection.ExecuteScalarAsync<string>("SELECT CONCAT('payroll:',MD5(DATABASE()),':',@Id)", new { Id = payRunId });
        if (await connection.ExecuteScalarAsync<int?>("SELECT GET_LOCK(@Name,0)", new { Name = lockName }) != 1)
        {
            if (selected is not null) throw new SelectedPayRunConflictException("This exact run is already locked for processing. Refresh before retrying.");
            return null;
        }
        try
        {
        CreatePayRunRequest? request;
        if (selected is not null)
        {
            await using var claim = await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            var state = await ReadSelectedRunStateAsync(connection, claim, payRunId, selected.ClientId, selected.PayPeriod, true)
                ?? throw new SelectedPayRunConflictException("The selected run was not found in this client and period.");
            SelectedPayRunPolicy.RequireReviewed(state, SelectedRunRuntime.Read(configuration), selected);
            request = state.Request!;
            if (await connection.ExecuteAsync("UPDATE payruns SET Status='Processing', ProcessingStartedAt=UTC_TIMESTAMP(), ProcessingCompletedAt=NULL, ProcessingError='' WHERE Id=@PayRunId AND Status=@ExpectedStatus", new { PayRunId = payRunId, ExpectedStatus = state.Run.Status }, claim) != 1)
                throw new SelectedPayRunConflictException("Run state changed before processing. Refresh the run; no second calculation was started.");
            await claim.CommitAsync();
        }
        else
        {
            var requestJson = await connection.ExecuteScalarAsync<string?>("SELECT CAST(RequestJson AS CHAR) FROM payruns WHERE Id=@PayRunId AND Status='Queued'", new { PayRunId = payRunId });
            if (string.IsNullOrWhiteSpace(requestJson)) return null;
            request = JsonSerializer.Deserialize<CreatePayRunRequest>(requestJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (request is null) return null;
        }
        using var observation = engineMonitor?.Observe("payroll",new EngineActivityContext("Process queued payroll", "Pay run", payRunId.ToString(CultureInfo.InvariantCulture), ClientId: request.ClientId));
        if (selected is null && await connection.ExecuteAsync("UPDATE payruns SET Status='Processing', ProcessingStartedAt=UTC_TIMESTAMP(), ProcessingCompletedAt=NULL, ProcessingError='' WHERE Id=@PayRunId AND Status='Queued'", new { PayRunId = payRunId }) != 1) return null;
        try
        {
            var processed = await ProcessExistingAsync(payRunId, request, performedBy, selected is not null);
            if(observation is not null) observation.Succeeded = processed is not null && processed.Status != "Failed";
            return processed;
        }
        catch (Exception exception)
        {
            var resolved = await ResolveProcessingExceptionAsync(connection, payRunId, performedBy, exception);
            if (observation is not null) observation.Succeeded = resolved.Status != "Failed";
            return resolved;
        }
        }
        finally { await connection.ExecuteAsync("SELECT RELEASE_LOCK(@Name)", new { Name = lockName }); }
    }

    public async Task<PayRun?> CreateAsync(CreatePayRunRequest request, string performedBy = "System")
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var client = await connection.QueryFirstOrDefaultAsync<Client>("SELECT * FROM clients WHERE Id = @Id AND IsActive = TRUE FOR UPDATE", new { Id = request.ClientId }, transaction);
        if (client is null) return null;
        var runType = string.Equals(request.RunType, "Off Cycle", StringComparison.OrdinalIgnoreCase) ? "Off Cycle" : "Regular";
        request.AttendancePolicyBatchId = await ResolveAttendancePolicyBatchIdAsync(connection, transaction, request);
        var runName = string.IsNullOrWhiteSpace(request.RunName) ? (runType == "Regular" ? "Regular payroll" : "Off-cycle payroll") : request.RunName.Trim();

        var setupJson = await PayrollDataTableStore.GetSetupJsonAsync(connection, transaction);
        var employees = (await connection.QueryAsync<PayRunSourceEmployee>("SELECT e.*, c.Name AS ClientName, COALESCE(w.State,'') AS WorkState FROM employees e LEFT JOIN clients c ON c.Id = e.ClientId LEFT JOIN worklocations w ON w.Id = e.WorkLocationId WHERE " + EmployeeEmploymentRules.EligibleInMonth("e", "@PayPeriod") + " AND e.ClientId=@ClientId ORDER BY e.FirstName,e.LastName FOR UPDATE", new { request.ClientId, request.PayPeriod }, transaction)).ToList();
        await LoadEmployeeTablesAsync(connection, transaction, employees, request.PayPeriod);
        var attendance = (await GetPayRunAttendanceAsync(connection, transaction, request.ClientId, request.PayPeriod)).ToDictionary(row => row.EmployeeId);
        foreach(var employee in employees)
            if(attendance.TryGetValue(employee.Id,out var input))employee.PayableOnlyAttendance=input.PayableOnlyEvidence;

        var selectedAdjustmentIds = request.AdjustmentIds.Distinct().ToArray();
        var adjustments = await GetApplicableAdjustmentsAsync(connection, transaction, request.ClientId, request.PayPeriod, runType, selectedAdjustmentIds);
        var requestedIncludedIds = request.IncludedEmployeeIds.Distinct().ToHashSet();
        var includedEmployeeIds = runType == "Off Cycle"
            ? request.IncludedEmployeeIds.Concat(adjustments.Select(item => item.EmployeeId)).Distinct().ToHashSet()
            : requestedIncludedIds.Count > 0 ? employees.Where(employee => requestedIncludedIds.Contains(employee.Id)).Select(employee => employee.Id).ToHashSet() : employees.Select(employee => employee.Id).Except(request.ExcludedEmployeeIds).ToHashSet();
        if (runType == "Regular")
        {
            includedEmployeeIds = await NormalizeRegularEmployeeScopeAsync(connection, transaction, request, employees);
            requestedIncludedIds = request.IncludedEmployeeIds.Distinct().ToHashSet();
        }
        if (runType == "Regular" && requestedIncludedIds.Count > 0)
            adjustments = adjustments.Where(item => includedEmployeeIds.Contains(item.EmployeeId)).ToList();
        var adjustmentByEmployee = adjustments.GroupBy(item => item.EmployeeId).ToDictionary(group => group.Key, group => group.ToList());
        if (runType == "Regular")
            await LoadPfPolicyPeriodsAsync(connection, transaction, employees.Where(employee => includedEmployeeIds.Contains(employee.Id)).ToList(), request.PayPeriod, setupJson);
        if (runType == "Off Cycle" && includedEmployeeIds.Count == 0)
        {
            await transaction.RollbackAsync();
            return null;
        }
        var runCode = runType == "Regular" ? RegularRunCode(request) : $"OFF-{DateTime.UtcNow:yyyyMMddHHmmss}";
        var existing = runType == "Regular" ? await FindExistingRegularRunAsync(connection, transaction, request, runCode) : null;
        if (existing is not null)
        {
            if (!existing.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase))
                return null;
            throw new PayRunAttemptConflictException(existing.Id, existing.Status);
        }
        var validationIssues = ValidatePayRunInputs(0, request, runType, employees.Where(employee => includedEmployeeIds.Contains(employee.Id)).ToList(), attendance, setupJson);
        if (runType == "Regular")
        {
            var employmentError = await EmployeeAttendanceEligibility.ValidateMonthlyWithOptionalPresenceAsync(connection, transaction, request.ClientId, request.PayPeriod,
                employees.Where(employee => includedEmployeeIds.Contains(employee.Id)).Select(employee => {
                    var entry = attendance.GetValueOrDefault(employee.Id);
                    var days = AttendanceWorkingDays(entry, request.TotalWorkingDays);
                    return (employee.Id, entry is null ? (decimal?)days : ClampKnownPresence(entry.PresentDays, days), entry is null ? (decimal)days : Math.Clamp(entry.PayableDays, 0, days));
                }));
            if (employmentError is not null) validationIssues.Add(Issue(0, null, "", "Employment dates", "Critical", "Payroll Validation", employmentError, true));
        }
        var hasBlockingIssues = validationIssues.Any(issue => issue.IsBlocking);
        var processingError = hasBlockingIssues ? Trunc(string.Join(" ", validationIssues.Where(issue => issue.IsBlocking).Take(3).Select(issue => issue.Message)), 1000) : "";
        var payRunId = (int)await connection.ExecuteScalarAsync<long>(@"
INSERT INTO payruns (ClientId, ClientName, PayPeriod, RunCode, AttendancePolicyBatchId, RunType, RunName, Reason, PayDate, TotalWorkingDays, Status, ProcessingError) VALUES (@ClientId, @ClientName, @PayPeriod, @RunCode, @AttendancePolicyBatchId, @RunType, @RunName, @Reason, @PayDate, @TotalWorkingDays, @Status, @ProcessingError);
SELECT LAST_INSERT_ID();", new { request.ClientId, ClientName = client.Name, request.PayPeriod, RunCode = runCode, AttendancePolicyBatchId = request.AttendancePolicyBatchId, RunType = runType, RunName = runName, Reason = request.Reason.Trim(), PayDate = request.PayDate.ToDateTime(TimeOnly.MinValue), request.TotalWorkingDays, Status = hasBlockingIssues ? "Failed" : "Draft", ProcessingError = processingError }, transaction);
        validationIssues.ForEach(issue => issue.PayRunId = payRunId);
        await WriteValidationIssuesAsync(connection, transaction, validationIssues);
        await WritePipelineStepLogsAsync(connection, transaction, payRunId, performedBy, validationIssues);
        if (hasBlockingIssues)
        {
            await transaction.CommitAsync();
            return await GetAsync(payRunId);
        }

        foreach (var employee in employees.Where(employee => includedEmployeeIds.Contains(employee.Id)))
        {
            var attendanceRow = attendance.GetValueOrDefault(employee.Id);
            var employeeWorkingDays = runType == "Off Cycle" ? request.TotalWorkingDays : AttendanceWorkingDays(attendanceRow, request.TotalWorkingDays);
            decimal? presentDays = runType == "Off Cycle" ? 0 : attendanceRow is null ? employeeWorkingDays : ClampKnownPresence(attendanceRow.PresentDays, employeeWorkingDays);
            var payableDays = runType == "Off Cycle" ? 0 : attendanceRow is null ? employeeWorkingDays : Math.Clamp(attendanceRow.PayableDays, 0, employeeWorkingDays);
            employee.AttendancePolicySnapshot = attendanceRow?.PolicySnapshot;
            employee.PayableOnlyAttendance = attendanceRow?.PayableOnlyEvidence;
            var employeeAdjustments = adjustmentByEmployee.GetValueOrDefault(employee.Id) ?? [];
            var row = BuildEmployee(payRunId, employee, setupJson, request.PayPeriod, employeeWorkingDays, presentDays, payableDays, employeeAdjustments, 0, 0, -1, false);
            if (runType == "Regular")
            {
                var projectedTds = await CalculateMonthlyTdsAsync(connection, transaction, employee, row, request.PayPeriod);
                if (projectedTds.Calculated)
                    row = BuildEmployee(payRunId, employee, setupJson, request.PayPeriod, employeeWorkingDays, presentDays, payableDays, employeeAdjustments, 0, 0, projectedTds.Amount, false, projectedTds.ComponentCode);
            }
            await SaveEmployeeAsync(connection, transaction, row);
            await WriteCalculationTracesAsync(connection, transaction, row, employeeWorkingDays);
        }
        if (adjustments.Count > 0)
        {
            await connection.ExecuteAsync("UPDATE payrolladjustments SET Status = 'Applied', PayRunId = @PayRunId WHERE Id IN @Ids", new { PayRunId = payRunId, Ids = adjustments.Select(item => item.Id).ToArray() }, transaction);
            await SyncExpenseClaimPayrollStatusAsync(connection, transaction, payRunId, "Applied");
        }

        await RefreshTotalsAsync(connection, transaction, payRunId);
        await WriteReconciliationResultsAsync(connection, transaction, payRunId);
        await transaction.CommitAsync();
        return await GetAsync(payRunId);
    }

    public async Task<PayRun?> CreateFailedAttemptAsync(CreatePayRunRequest request, string performedBy, Exception exception)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var client = await connection.QueryFirstOrDefaultAsync<Client>("SELECT * FROM clients WHERE Id = @Id", new { Id = request.ClientId }, transaction);
        if (client is null) return null;
        var runType = string.Equals(request.RunType, "Off Cycle", StringComparison.OrdinalIgnoreCase) ? "Off Cycle" : "Regular";
        request.AttendancePolicyBatchId = await ResolveAttendancePolicyBatchIdAsync(connection, transaction, request);
        var runCode = runType == "Regular" ? RegularRunCode(request) : $"OFF-FAILED-{DateTime.UtcNow:yyyyMMddHHmmss}";
        var runName = string.IsNullOrWhiteSpace(request.RunName) ? $"{runType} payroll" : request.RunName.Trim();
        if (runType == "Regular")
        {
            var existing = await FindExistingRegularRunAsync(connection, transaction, request, runCode);
            if (existing is not null)
            {
                if (!existing.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase))
                    return await GetAsync(existing.Id);
                throw new PayRunAttemptConflictException(existing.Id, existing.Status);
            }
        }
        var payRunId = (int)await connection.ExecuteScalarAsync<long>(@"
INSERT INTO payruns (ClientId, ClientName, PayPeriod, RunCode, AttendancePolicyBatchId, RunType, RunName, Reason, PayDate, TotalWorkingDays, Status, ProcessingError) VALUES (@ClientId, @ClientName, @PayPeriod, @RunCode, @AttendancePolicyBatchId, @RunType, @RunName, @Reason, @PayDate, @TotalWorkingDays, 'Failed', @ProcessingError);
SELECT LAST_INSERT_ID();", new { request.ClientId, ClientName = client.Name, request.PayPeriod, RunCode = runCode, AttendancePolicyBatchId = request.AttendancePolicyBatchId, RunType = runType, RunName = runName, Reason = FirstText(request.Reason, "Payroll engine exception").Trim(), PayDate = request.PayDate.ToDateTime(TimeOnly.MinValue), request.TotalWorkingDays, ProcessingError = Trunc(exception.Message, 1000) }, transaction);
        var issue = Issue(payRunId, null, "", "Run", "Critical", "Payroll Validation", exception.Message, true);
        issue.IssueType = "Exception";
        issue.DataJson = JsonSerializer.Serialize(new { exception = exception.GetType().Name, stackTrace = Trunc(exception.StackTrace, 6000) });
        await WriteValidationIssuesAsync(connection, transaction, [issue]);
        await WritePipelineStepLogsAsync(connection, transaction, payRunId, performedBy, [issue]);
        await transaction.CommitAsync();
        return await GetAsync(payRunId);
    }

    private async Task<PayRun?> ProcessExistingAsync(int payRunId, CreatePayRunRequest request, string performedBy, bool preserveQueuedEvidence = false)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await using var transaction = preserveQueuedEvidence && request.AdjustmentIds.Count>0
            ? await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable) : await connection.BeginTransactionAsync();
        var payRun = await connection.QueryFirstOrDefaultAsync<PayRun>("SELECT * FROM payruns WHERE Id=@PayRunId AND Status='Processing' FOR UPDATE", new { PayRunId = payRunId }, transaction);
        if (payRun is null) return null;
        // Explicitly queued draft/failed attempts can calculate a new snapshot. Approved and paid runs cannot enter this path.
        if (!preserveQueuedEvidence)
            await connection.ExecuteAsync("DELETE FROM tax_computation_snapshots WHERE pay_run_id=@PayRunId", new { PayRunId = payRunId }, transaction);
        var client = await connection.QueryFirstOrDefaultAsync<Client>("SELECT * FROM clients WHERE Id = @Id AND IsActive = TRUE FOR UPDATE", new { Id = request.ClientId }, transaction);
        if (client is null)
        {
            if (preserveQueuedEvidence) throw new SelectedPayRunConflictException("The client became inactive before calculation. No payroll lines were saved.");
            return null;
        }
        if (!preserveQueuedEvidence) await ClearPayRunDetailsAsync(connection, transaction, payRunId);
        var reviewedEmployeeIds = request.IncludedEmployeeIds.ToArray();

        var runType = string.Equals(request.RunType, "Off Cycle", StringComparison.OrdinalIgnoreCase) ? "Off Cycle" : "Regular";
        var setupJson = await PayrollDataTableStore.GetSetupJsonAsync(connection, transaction);
        var employees = (await connection.QueryAsync<PayRunSourceEmployee>("SELECT e.*, c.Name AS ClientName, COALESCE(w.State,'') AS WorkState FROM employees e LEFT JOIN clients c ON c.Id = e.ClientId LEFT JOIN worklocations w ON w.Id = e.WorkLocationId WHERE " + EmployeeEmploymentRules.EligibleInMonth("e", "@PayPeriod") + " AND e.ClientId=@ClientId ORDER BY e.FirstName,e.LastName FOR UPDATE", new { request.ClientId, request.PayPeriod }, transaction)).ToList();
        await LoadEmployeeTablesAsync(connection, transaction, employees, request.PayPeriod);
        var attendance = (await GetPayRunAttendanceAsync(connection, transaction, request.ClientId, request.PayPeriod)).ToDictionary(row => row.EmployeeId);
        foreach(var employee in employees)
            if(attendance.TryGetValue(employee.Id,out var input))employee.PayableOnlyAttendance=input.PayableOnlyEvidence;
        var selectedAdjustmentIds = request.AdjustmentIds.Distinct().ToArray();
        SelectedAdjustmentState? selectedAdjustmentState=null;
        List<PayrollAdjustment> adjustments;
        if(preserveQueuedEvidence)
        {
            // The claim transaction has committed. Re-lock and re-prove source and
            // business snapshots here, inside the calculation/output transaction.
            selectedAdjustmentState=await ReadSelectedAdjustmentsAsync(connection,transaction,request,true,true);
            RequireSelectedAdjustmentState(selectedAdjustmentState);
            adjustments=selectedAdjustmentState.Rows;
        }
        else adjustments=await GetApplicableAdjustmentsAsync(connection, transaction, request.ClientId, request.PayPeriod, runType, selectedAdjustmentIds);
        var requestedIncludedIds = request.IncludedEmployeeIds.Distinct().ToHashSet();
        var includedEmployeeIds = runType == "Off Cycle"
            ? request.IncludedEmployeeIds.Concat(adjustments.Select(item => item.EmployeeId)).Distinct().ToHashSet()
            : requestedIncludedIds.Count > 0 ? employees.Where(employee => requestedIncludedIds.Contains(employee.Id)).Select(employee => employee.Id).ToHashSet() : employees.Select(employee => employee.Id).Except(request.ExcludedEmployeeIds).ToHashSet();
        if (runType == "Regular")
        {
            includedEmployeeIds = await NormalizeRegularEmployeeScopeAsync(connection, transaction, request, employees, payRunId);
            requestedIncludedIds = request.IncludedEmployeeIds.Distinct().ToHashSet();
            if (preserveQueuedEvidence) RequireUnchangedSelectedScope(request, includedEmployeeIds, reviewedEmployeeIds);
        }
        if (runType == "Regular" && requestedIncludedIds.Count > 0)
            adjustments = adjustments.Where(item => includedEmployeeIds.Contains(item.EmployeeId)).ToList();
        var adjustmentByEmployee = adjustments.GroupBy(item => item.EmployeeId).ToDictionary(group => group.Key, group => group.ToList());
        if (runType == "Regular")
            await LoadPfPolicyPeriodsAsync(connection, transaction, employees.Where(employee => includedEmployeeIds.Contains(employee.Id)).ToList(), request.PayPeriod, setupJson);
        var validationIssues = ValidatePayRunInputs(payRunId, request, runType, employees.Where(employee => includedEmployeeIds.Contains(employee.Id)).ToList(), attendance, setupJson);
        if (runType == "Off Cycle" && includedEmployeeIds.Count == 0)
            validationIssues.Add(Issue(payRunId, null, "", "Run", "Critical", "Payroll Validation", "Off-cycle payroll needs at least one employee or approved adjustment.", true));
        if (runType == "Regular" && includedEmployeeIds.Count == 0)
            validationIssues.Add(Issue(payRunId, null, "", "Run", "Critical", "Payroll Validation", "All selected employees are already covered by another payroll run for this client and month.", true));
        if (runType == "Regular")
        {
            var employmentError = await EmployeeAttendanceEligibility.ValidateMonthlyWithOptionalPresenceAsync(connection, transaction, request.ClientId, request.PayPeriod,
                employees.Where(employee => includedEmployeeIds.Contains(employee.Id)).Select(employee => {
                    var entry = attendance.GetValueOrDefault(employee.Id);
                    var days = AttendanceWorkingDays(entry, request.TotalWorkingDays);
                    return (employee.Id, entry is null ? (decimal?)days : ClampKnownPresence(entry.PresentDays, days), entry is null ? (decimal)days : Math.Clamp(entry.PayableDays, 0, days));
                }));
            if (employmentError is not null) validationIssues.Add(Issue(payRunId, null, "", "Employment dates", "Critical", "Payroll Validation", employmentError, true));
        }
        var hasBlockingIssues = validationIssues.Any(issue => issue.IsBlocking);
        await WriteValidationIssuesAsync(connection, transaction, validationIssues);
        await WritePipelineStepLogsAsync(connection, transaction, payRunId, performedBy, validationIssues);
        if (hasBlockingIssues)
        {
            await connection.ExecuteAsync("UPDATE payruns SET Status='Failed', ProcessingCompletedAt=UTC_TIMESTAMP(), ProcessingError=@Error WHERE Id=@PayRunId", new { PayRunId = payRunId, Error = Trunc(string.Join(" ", validationIssues.Where(issue => issue.IsBlocking).Take(3).Select(issue => issue.Message)), 1000) }, transaction);
            await transaction.CommitAsync();
            return await GetAsync(payRunId);
        }

        foreach (var employee in employees.Where(employee => includedEmployeeIds.Contains(employee.Id)))
        {
            var attendanceRow = attendance.GetValueOrDefault(employee.Id);
            var employeeWorkingDays = runType == "Off Cycle" ? request.TotalWorkingDays : AttendanceWorkingDays(attendanceRow, request.TotalWorkingDays);
            decimal? presentDays = runType == "Off Cycle" ? 0 : attendanceRow is null ? employeeWorkingDays : ClampKnownPresence(attendanceRow.PresentDays, employeeWorkingDays);
            var payableDays = runType == "Off Cycle" ? 0 : attendanceRow is null ? employeeWorkingDays : Math.Clamp(attendanceRow.PayableDays, 0, employeeWorkingDays);
            employee.AttendancePolicySnapshot = attendanceRow?.PolicySnapshot;
            employee.PayableOnlyAttendance = attendanceRow?.PayableOnlyEvidence;
            var employeeAdjustments = adjustmentByEmployee.GetValueOrDefault(employee.Id) ?? [];
            var row = BuildEmployee(payRunId, employee, setupJson, request.PayPeriod, employeeWorkingDays, presentDays, payableDays, employeeAdjustments, 0, 0, -1, false);
            if (runType == "Regular")
            {
                var projectedTds = await CalculateMonthlyTdsAsync(connection, transaction, employee, row, request.PayPeriod);
                if (projectedTds.Calculated)
                    row = BuildEmployee(payRunId, employee, setupJson, request.PayPeriod, employeeWorkingDays, presentDays, payableDays, employeeAdjustments, 0, 0, projectedTds.Amount, false, projectedTds.ComponentCode);
            }
            await SaveEmployeeAsync(connection, transaction, row);
            await WriteCalculationTracesAsync(connection, transaction, row, employeeWorkingDays);
        }
        if (adjustments.Count > 0)
        {
            if(preserveQueuedEvidence) await ApplySelectedAdjustmentsAsync(connection,transaction,payRunId,request,selectedAdjustmentState!);
            else
            {
                await connection.ExecuteAsync("UPDATE payrolladjustments SET Status = 'Applied', PayRunId = @PayRunId WHERE Id IN @Ids", new { PayRunId = payRunId, Ids = adjustments.Select(item => item.Id).ToArray() }, transaction);
                await SyncExpenseClaimPayrollStatusAsync(connection, transaction, payRunId, "Applied");
            }
        }
        await RefreshTotalsAsync(connection, transaction, payRunId);
        await WriteReconciliationResultsAsync(connection, transaction, payRunId);
        await connection.ExecuteAsync("UPDATE payruns SET Status='Draft', ProcessingCompletedAt=UTC_TIMESTAMP(), ProcessingError='' WHERE Id=@PayRunId", new { PayRunId = payRunId }, transaction);
        await transaction.CommitAsync();
        return await GetAsync(payRunId);
    }

    public async Task<PayRunEmployee?> UpdateEmployeeAsync(int payRunId, int employeeId, UpdatePayRunEmployeeRequest request)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var payRun = await connection.QueryFirstOrDefaultAsync<PayRun>("SELECT * FROM payruns WHERE Id = @Id FOR UPDATE", new { Id = payRunId }, transaction);
        if (payRun is null || payRun.Status != "Draft")
            return null;
        var savedDetails = await connection.ExecuteScalarAsync<string?>("SELECT CAST(DetailsJson AS CHAR) FROM payrunemployees WHERE PayRunId=@PayRunId AND EmployeeId=@EmployeeId AND ClientId=@ClientId", new { PayRunId = payRunId, EmployeeId = employeeId, payRun.ClientId }, transaction);
        if (savedDetails is null) return null;
        if(HasUnknownPresenceSnapshot(savedDetails))
            throw new InvalidOperationException("This draft retains unknown actual presence from reviewed payable-only source attendance. Record actual attendance and use a reviewed payroll calculation; the payroll editor cannot replace unknown presence with zero or payable days.");
        if (HasPfPolicySnapshot(savedDetails) || HasAttendancePolicySnapshot(savedDetails))
            throw new InvalidOperationException("This payroll employee has a frozen effective policy calculation. Direct attendance/amount edits would discard its dated snapshot; use a new reviewed payroll calculation instead.");
        var employee = await connection.QueryFirstOrDefaultAsync<PayRunSourceEmployee>("SELECT e.*, c.Name AS ClientName, COALESCE(w.State,'') AS WorkState FROM employees e LEFT JOIN clients c ON c.Id = e.ClientId LEFT JOIN worklocations w ON w.Id = e.WorkLocationId WHERE e.Id = @Id AND e.ClientId=@ClientId", new { Id = employeeId, payRun.ClientId }, transaction);
        if (employee is null)
            return null;
        await LoadEmployeeTablesAsync(connection, transaction, [employee], payRun.PayPeriod);
        var setupJson = await PayrollDataTableStore.GetSetupJsonAsync(connection, transaction);
        var adjustments = (await connection.QueryAsync<PayrollAdjustment>("SELECT * FROM payrolladjustments WHERE PayRunId=@PayRunId AND EmployeeId=@EmployeeId AND ClientId=@ClientId AND Status='Applied'", new { PayRunId = payRunId, EmployeeId = employeeId, payRun.ClientId }, transaction)).ToList();
        if (request.IsSkipped && adjustments.Count > 0)
            throw new InvalidOperationException("This employee has applied adjustments. Recreate the draft with the correct employee selection before excluding them.");
        var presentDays = Math.Clamp(request.PresentDays, 0, payRun.TotalWorkingDays);
        var employmentError = await EmployeeAttendanceEligibility.ValidateMonthlyAsync(connection, transaction, payRun.ClientId, payRun.PayPeriod, [(employeeId, presentDays, presentDays)]);
        if (employmentError is not null) throw new InvalidOperationException(employmentError);
        var tds = Math.Max(0, request.ManualTds);
        await connection.ExecuteAsync("DELETE FROM tax_computation_snapshots WHERE pay_run_id=@PayRunId AND employee_id=@EmployeeId", new { PayRunId = payRunId, EmployeeId = employeeId }, transaction);
        // The editor sends aggregate one-time amounts; keep linked adjustments and apply only the difference as a manual amount.
        var manualEarnings = Math.Max(0, request.OneTimeEarnings) - adjustments.Where(item => !IsDeduction(item)).Sum(item => item.Amount);
        var manualDeductions = Math.Max(0, request.OneTimeDeductions) - adjustments.Where(IsDeduction).Sum(item => item.Amount);
        if (manualEarnings < 0 || manualDeductions < 0)
            throw new InvalidOperationException("One-time totals cannot be lower than linked approved adjustments. Correct the adjustment through a new payroll calculation.");
        var row = BuildEmployee(payRunId, employee, setupJson, payRun.PayPeriod, payRun.TotalWorkingDays, presentDays, presentDays, adjustments, manualEarnings, manualDeductions, tds > 0 ? tds : -1, request.IsSkipped);
        if (!request.IsSkipped && tds == 0 && payRun.RunType == "Regular")
        {
            var projectedTds = await CalculateMonthlyTdsAsync(connection, transaction, employee, row, payRun.PayPeriod);
            tds = projectedTds.Amount;
            if (projectedTds.Calculated)
                row = BuildEmployee(payRunId, employee, setupJson, payRun.PayPeriod, payRun.TotalWorkingDays, presentDays, presentDays, adjustments, manualEarnings, manualDeductions, tds, false, projectedTds.ComponentCode);
        }
        await SaveEmployeeAsync(connection, transaction, row);
        await connection.ExecuteAsync("DELETE FROM payroll_calculation_traces WHERE PayRunId=@PayRunId AND EmployeeId=@EmployeeId", new { PayRunId = payRunId, EmployeeId = employeeId }, transaction);
        await WriteCalculationTracesAsync(connection, transaction, row, payRun.TotalWorkingDays);
        await RefreshTotalsAsync(connection, transaction, payRunId);
        await WriteReconciliationResultsAsync(connection, transaction, payRunId);
        await transaction.CommitAsync();
        return row;
    }

    public async Task<IEnumerable<PayrollAdjustment>> GetAdjustmentsAsync(int? clientId, string? payPeriod, string? status)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        return await connection.QueryAsync<PayrollAdjustment>(@"
SELECT a.*, CONCAT(e.FirstName, ' ', e.LastName) AS EmployeeName, e.EmployeeCode
FROM payrolladjustments a
JOIN employees e ON e.Id = a.EmployeeId
WHERE (@ClientId IS NULL OR a.ClientId = @ClientId)
  AND (@PayPeriod IS NULL OR a.PayPeriod = @PayPeriod)
  AND (@Status IS NULL OR a.Status = @Status)
ORDER BY a.PayPeriod DESC, a.CreatedAt DESC;", new { ClientId = clientId, PayPeriod = string.IsNullOrWhiteSpace(payPeriod) ? null : payPeriod, Status = string.IsNullOrWhiteSpace(status) ? null : status });
    }

    public async Task<PayrollAdjustment?> SaveAdjustmentAsync(PayrollAdjustment adjustment)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        var employee = await connection.QueryFirstOrDefaultAsync<Employee>("SELECT e.* FROM employees e WHERE e.Id=@Id AND e.ClientId=@ClientId AND " + EmployeeEmploymentRules.EligibleInMonth("e", "@PayPeriod"), new { Id = adjustment.EmployeeId, adjustment.ClientId, adjustment.PayPeriod });
        if (employee is null || adjustment.Amount <= 0 || string.IsNullOrWhiteSpace(adjustment.PayPeriod)) return null;
        adjustment.EmployeeName = $"{employee.FirstName} {employee.LastName}".Trim();
        adjustment.EmployeeCode = employee.EmployeeCode;
        adjustment.AdjustmentType = NormalizeAdjustmentType(adjustment.AdjustmentType);
        adjustment.PayRunType = string.Equals(adjustment.PayRunType, "Off Cycle", StringComparison.OrdinalIgnoreCase) ? "Off Cycle" : "Regular";
        adjustment.Status = string.IsNullOrWhiteSpace(adjustment.Status) ? "Approved" : adjustment.Status;
        if (adjustment.Id == 0)
        {
            var id = (int)await connection.ExecuteScalarAsync<long>(@"
INSERT INTO payrolladjustments (ClientId, EmployeeId, EmployeeName, EmployeeCode, ComponentId, ComponentCode, ComponentName, AdjustmentType, Amount, PayPeriod, PayRunType, ReasonCode, Notes, Taxable, Status)
VALUES (@ClientId, @EmployeeId, @EmployeeName, @EmployeeCode, @ComponentId, @ComponentCode, @ComponentName, @AdjustmentType, @Amount, @PayPeriod, @PayRunType, @ReasonCode, @Notes, @Taxable, @Status);
SELECT LAST_INSERT_ID();", adjustment);
            adjustment.Id = id;
        }
        else
        {
            var rows = await connection.ExecuteAsync(@"
UPDATE payrolladjustments SET ClientId=@ClientId, EmployeeId=@EmployeeId, EmployeeName=@EmployeeName, EmployeeCode=@EmployeeCode, ComponentId=@ComponentId, ComponentCode=@ComponentCode, ComponentName=@ComponentName, AdjustmentType=@AdjustmentType, Amount=@Amount, PayPeriod=@PayPeriod, PayRunType=@PayRunType, ReasonCode=@ReasonCode, Notes=@Notes, Taxable=@Taxable, Status=@Status
WHERE Id=@Id AND Status != 'Applied';", adjustment);
            if (rows == 0) return null;
        }
        return adjustment;
    }

    public async Task<bool> CancelAdjustmentAsync(int id)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        return await connection.ExecuteAsync("UPDATE payrolladjustments SET Status = 'Cancelled' WHERE Id = @Id AND Status != 'Applied'", new { Id = id }) == 1;
    }

    public async Task<PayRun?> SubmitForApprovalAsync(int id)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var status = await connection.ExecuteScalarAsync<string?>("SELECT Status FROM payruns WHERE Id=@Id FOR UPDATE", new { Id = id }, transaction);
        if (status != "Draft") return null;
        await EnsureReconciledAsync(connection, transaction, id);
        var rows = await connection.ExecuteAsync("UPDATE payruns SET Status = 'Pending Approval' WHERE Id = @Id AND Status = 'Draft'", new { Id = id }, transaction);
        await transaction.CommitAsync();
        return rows == 1 ? await GetAsync(id) : null;
    }

    public async Task<PayRun?> ApproveAsync(int id)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var status = await connection.ExecuteScalarAsync<string?>("SELECT Status FROM payruns WHERE Id=@Id FOR UPDATE", new { Id = id }, transaction);
        if (status != "Pending Approval") return null;
        await EnsureReconciledAsync(connection, transaction, id);
        var rows = await connection.ExecuteAsync("UPDATE payruns SET Status = 'Approved' WHERE Id = @Id AND Status='Pending Approval'", new { Id = id }, transaction);
        await transaction.CommitAsync();
        return rows == 1 ? await GetAsync(id) : null;
    }

    public async Task<bool> DeleteAsync(int id)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var status = await connection.ExecuteScalarAsync<string?>("SELECT Status FROM payruns WHERE Id=@Id FOR UPDATE", new { Id = id }, transaction);
        if (status is null || status is "Queued" or "Processing" or "Paid" or "Partially Paid") return false;
        var canDelete = await connection.ExecuteScalarAsync<int>(@"
SELECT COUNT(*)
FROM payruns
WHERE Id=@Id
AND Status NOT IN ('Queued', 'Processing', 'Paid', 'Partially Paid')
AND NOT EXISTS (
    SELECT 1 FROM payrunemployees
    WHERE PayRunId=@Id AND PaymentStatus='Paid'
);", new { Id = id }, transaction);
        if (canDelete == 0) return false;
        await DeletePayRunAttemptAsync(connection, transaction, id);
        await transaction.CommitAsync();
        return true;
    }

    public async Task<PayRun?> RecallAsync(int id)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        var rows = await connection.ExecuteAsync(@"UPDATE payruns SET Status = 'Draft' WHERE Id = @Id AND Status IN ('Approved', 'Pending Approval') AND NOT EXISTS (SELECT 1 FROM payrunemployees WHERE PayRunId = @Id AND PaymentStatus = 'Paid')", new { Id = id });
        return rows == 1 ? await GetAsync(id) : null;
    }

    public async Task<PayRun?> RecordPaymentsAsync(int id, RecordPaymentRequest request)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var payRun = await connection.QueryFirstOrDefaultAsync<PayRun>("SELECT * FROM payruns WHERE Id = @Id FOR UPDATE", new { Id = id }, transaction);
        if (payRun is null || payRun.Status is not ("Approved" or "Partially Paid")) return null;
        var employeeIds = request.EmployeeIds.Distinct().ToArray();
        if (employeeIds.Length == 0)
            employeeIds = (await connection.QueryAsync<int>("SELECT EmployeeId FROM payrunemployees WHERE PayRunId = @Id AND IsSkipped = FALSE AND PaymentStatus != 'Paid'", new { Id = id }, transaction)).ToArray();
        if (employeeIds.Length == 0) return null;
        await connection.ExecuteAsync("UPDATE payrunemployees SET PaymentStatus = 'Paid', PaymentDate = @PaymentDate WHERE PayRunId = @Id AND IsSkipped = FALSE AND EmployeeId IN @EmployeeIds", new { Id = id, PaymentDate = request.PaymentDate.ToDateTime(TimeOnly.MinValue), EmployeeIds = employeeIds }, transaction);
        await SyncExpenseClaimPayrollStatusAsync(connection, transaction, id, "Paid", employeeIds);
        var pending = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM payrunemployees WHERE PayRunId = @Id AND IsSkipped = FALSE AND PaymentStatus != 'Paid'", new { Id = id }, transaction);
        await connection.ExecuteAsync("UPDATE payruns SET Status = @Status WHERE Id = @Id", new { Id = id, Status = pending == 0 ? "Paid" : "Partially Paid" }, transaction);
        await transaction.CommitAsync();
        return await GetAsync(id);
    }

    public async Task<int?> GetAdjustmentClientIdAsync(int id)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        return await connection.ExecuteScalarAsync<int?>("SELECT ClientId FROM payrolladjustments WHERE Id=@Id", new { Id = id });
    }

    private static PayRunEmployee BuildEmployee(int payRunId, PayRunSourceEmployee employee, string setupJson, string payPeriod, int totalWorkingDays, decimal? presentDays, decimal payableDays, IEnumerable<PayrollAdjustment> adjustments, decimal manualOneTimeEarnings, decimal manualOneTimeDeductions, decimal manualTds, bool isSkipped, string tdsComponentCode = "TDS")
    {
        if (employee.LocationParts is { } locationPeriod && locationPeriod.PayPeriod != payPeriod)
            throw new InvalidOperationException("Location attendance allocations belong to another payroll period.");
        if(!presentDays.HasValue&&employee.PayableOnlyAttendance?.PayPeriod!=payPeriod)
            throw new InvalidOperationException("Unknown actual presence requires reviewed payable-only attendance for this exact payroll period.");
        var setup = ReadPayrollSetup(setupJson);
        var salary = CalculateConfiguredSalary(employee, setup, totalWorkingDays, presentDays, payableDays);
        var replaceTds = manualTds >= 0;
        var salaryDeductionCodes = salary.Where(row => IsDeductionCategory(row.Component.Category) && (row.EarnedOverride ?? row.Monthly) > 0).Select(row => row.Component.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var salaryDeductionStatutoryTypes = salary.Where(row => IsDeductionCategory(row.Component.Category) && (row.EarnedOverride ?? row.Monthly) > 0 && !string.IsNullOrWhiteSpace(row.Component.StatutoryType)).Select(row => row.Component.StatutoryType).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var factor = totalWorkingDays == 0 || isSkipped ? 0 : (decimal)payableDays / totalWorkingDays;
        var lines = new List<object>();
        if (!presentDays.HasValue)
            lines.Add(new { Id="ATTENDANCE_INPUT",Name="Reviewed payable-only source; actual presence not supplied",Category="Summary",
                monthlyAmount=0,amount=0,ProRata=false,actualPresentDays=(decimal?)null,payableDays,payrollDays=totalWorkingDays,
                sourceType=SourcePayableAttendance.SourceType,sourceEvidence=employee.PayableOnlyAttendance });
        decimal monthlyGross = 0, grossPay = 0, deductions = 0, totalTds = 0, taxableMonthly = 0, taxableEarned = 0;

        foreach (var row in salary)
        {
            var isTds = row.Component.StatutoryType.Equals("TDS", StringComparison.OrdinalIgnoreCase) || row.Component.Code.Equals(tdsComponentCode, StringComparison.OrdinalIgnoreCase) || row.Component.Code == "TDS";
            if (replaceTds && IsDeductionCategory(row.Component.Category) && isTds) continue;
            var amount = isSkipped ? 0 : row.EarnedOverride ?? (row.DailyRate is decimal rate ? decimal.Round(rate * payableDays, 2) : row.Component.ProRata ? decimal.Round(row.Monthly * factor, 2) : row.Monthly);
            if (row.LocationParts is not null)
                lines.Add(new { Id = row.Component.Code, row.Component.Name, row.Component.Category, row.Component.ComponentRole, row.Component.StatutoryType, monthlyAmount = row.Monthly, amount, ProRata = false, locationParts = row.LocationParts });
            else if (row.DailyRate is decimal dailyRate)
                lines.Add(new { Id = row.Component.Code, row.Component.Name, row.Component.Category, row.Component.ComponentRole, row.Component.StatutoryType, monthlyAmount = row.Monthly, amount, ProRata = false, rate = dailyRate, units = isSkipped ? 0 : payableDays, unit = "Day" });
            else if (row.PfPolicy is null)
                lines.Add(new { Id = row.Component.Code, row.Component.Name, row.Component.Category, row.Component.ComponentRole, row.Component.StatutoryType, monthlyAmount = row.Monthly, amount, row.Component.ProRata });
            else
                lines.Add(new { Id = row.Component.Code, row.Component.Name, row.Component.Category, row.Component.ComponentRole, row.Component.StatutoryType, monthlyAmount = row.Monthly, amount, row.Component.ProRata, pfPolicy = row.PfPolicy });
            if (IsDeductionCategory(row.Component.Category))
            {
                deductions += amount;
                if (isTds) totalTds += amount;
            }
            else if (IsPayableEarningCategory(row.Component.Category))
            {
                monthlyGross += row.Monthly;
                grossPay += amount;
                if (row.Component.Taxable) { taxableMonthly += row.Monthly; taxableEarned += amount; }
            }
        }

        var seededDeductions = ReadSeededPayrollDeductions(employee);
        foreach (var seededDeduction in seededDeductions)
        {
            if (replaceTds && seededDeduction.StatutoryType == "TDS") continue;
            if (salaryDeductionCodes.Contains(seededDeduction.Code)) continue;
            if (!string.IsNullOrWhiteSpace(seededDeduction.StatutoryType) && salaryDeductionStatutoryTypes.Contains(seededDeduction.StatutoryType)) continue;
            var amount = isSkipped ? 0 : seededDeduction.Amount;
            if (amount <= 0) continue;
            deductions += amount;
            if (seededDeduction.StatutoryType == "TDS") totalTds += amount;
            lines.Add(new { Id = seededDeduction.Code, Name = seededDeduction.Name, Category = "Deduction", ComponentRole = "Statutory Deduction", StatutoryType = seededDeduction.StatutoryType, monthlyAmount = seededDeduction.Amount, amount, ProRata = false });
        }

        var hasProfessionalTaxLine = salaryDeductionCodes.Contains("PT") || salaryDeductionCodes.Contains("PT_LWF_WC") || seededDeductions.Any(row => row.Code.Equals("PT", StringComparison.OrdinalIgnoreCase) || row.Code.Equals("PT_LWF_WC", StringComparison.OrdinalIgnoreCase))
            || employee.LocationParts is not null && salary.Any(row=>IsDeductionCategory(row.Component.Category)
                && (row.Component.StatutoryType.Equals("Professional Tax",StringComparison.OrdinalIgnoreCase)||row.Component.Code is "PT" or "PT_LWF_WC"));
        if(employee.LocationParts is not null&&setup.ProfessionalTax.Enabled&&!hasProfessionalTaxLine
            &&employee.LocationParts.Parts.Select(part=>part.WorkState.Trim().ToUpperInvariant()).Distinct().Count()>1)
            throw new InvalidOperationException("Location allocations span different work states. Review an explicit monthly or source-part Professional Tax component instead of choosing one state implicitly.");
        if (!isSkipped && !hasProfessionalTaxLine)
        {
            var professionalTax = CalculateProfessionalTax(employee, setup, grossPay, payPeriod);
            if (professionalTax > 0)
            {
                deductions += professionalTax;
                lines.Add(new { Id = "PT", Name = "Professional Tax", Category = "Deduction", ComponentRole = "Statutory Deduction", StatutoryType = "Professional Tax", monthlyAmount = professionalTax, amount = professionalTax, ProRata = false });
            }
        }

        var adjustmentRows = adjustments.ToList();
        var oneTimeEarnings = isSkipped ? 0 : manualOneTimeEarnings + adjustmentRows.Where(item => !IsDeduction(item)).Sum(item => item.Amount);
        var oneTimeDeductions = isSkipped ? 0 : manualOneTimeDeductions + adjustmentRows.Where(IsDeduction).Sum(item => item.Amount);
        var tds = isSkipped ? 0 : Math.Max(0, manualTds);
        if (tds > 0)
        {
            deductions += tds;
            totalTds += tds;
            var code = FirstText(tdsComponentCode, "TDS").ToUpperInvariant();
            lines.Add(new { Id = code, Name = "Income Tax (TDS)", Category = "Deduction", ComponentRole = "Statutory Deduction", StatutoryType = "TDS", monthlyAmount = tds, amount = tds, ProRata = false });
        }
        foreach (var group in adjustmentRows.GroupBy(item => new { item.ComponentId, item.ComponentCode, item.ComponentName, Category = NormalizeAdjustmentType(item.AdjustmentType) }))
        {
            var amount = isSkipped ? 0 : group.Sum(item => item.Amount);
            if (amount <= 0) continue;
            lines.Add(new
            {
                Id = string.IsNullOrWhiteSpace(group.Key.ComponentCode) ? $"ADJ_{group.Key.ComponentId}" : group.Key.ComponentCode,
                Name = string.IsNullOrWhiteSpace(group.Key.ComponentName) ? "Payroll adjustment" : group.Key.ComponentName,
                group.Key.Category,
                monthlyAmount = amount,
                amount,
                ProRata = false
            });
        }

        var net = Math.Max(0, grossPay + oneTimeEarnings - deductions - oneTimeDeductions);
        lines.Add(new { Id = "TAXABLE_PAY", Name = "Taxable salary basis", Category = "Summary", monthlyAmount = taxableMonthly, amount = isSkipped ? 0 : taxableEarned + manualOneTimeEarnings + adjustmentRows.Where(item => !IsDeduction(item) && item.Taxable).Sum(item => item.Amount), ProRata = false });
        if (employee.AttendancePolicySnapshot is null)
            lines.Add(new { Id = "GROSS_EARNED", Name = "Gross Earned", Category = "Summary", monthlyAmount = monthlyGross, amount = grossPay, ProRata = false });
        else
            lines.Add(new { Id = "GROSS_EARNED", Name = "Gross Earned", Category = "Summary", monthlyAmount = monthlyGross, amount = grossPay, ProRata = false, attendancePolicy = employee.AttendancePolicySnapshot });
        lines.Add(new { Id = "NET_PAY", Name = "Net Pay", Category = "Summary", monthlyAmount = net, amount = net, ProRata = false });

        return new PayRunEmployee
        {
            PayRunId = payRunId,
            EmployeeId = employee.Id,
            ClientId = employee.ClientId,
            ClientName = employee.ClientName,
            EmployeeCode = employee.EmployeeCode,
            EmployeeName = $"{employee.FirstName} {employee.LastName}".Trim(),
            Department = employee.Department,
            PresentDays = presentDays,
            PayableDays = isSkipped ? 0 : payableDays,
            MonthlyGross = monthlyGross,
            GrossPay = grossPay,
            StatutoryDeductions = deductions,
            OneTimeEarnings = oneTimeEarnings,
            OneTimeDeductions = oneTimeDeductions,
            ManualTds = totalTds,
            NetPay = net,
            IsSkipped = isSkipped,
            DetailsJson = JsonSerializer.Serialize(lines)
        };
    }

    private static List<CalculatedPayrollComponent> CalculateConfiguredSalary(PayRunSourceEmployee employee, PayrollSetupData setup, int payrollDays, decimal? presentDays, decimal payableDays, List<PayrollSimulationLine>? trace = null)
    {
        if (!presentDays.HasValue) ValidatePayableOnlyCalculation(employee, setup, payrollDays, payableDays);
        ValidateSourcePayableTemplate(employee, setup);
        if (employee.LocationParts is not null)
            return CalculateLocationPartSalary(employee, setup, payrollDays, presentDays!.Value, payableDays, trace);
        var salaryJson = employee.SalaryComponents.Count > 0 ? new Dictionary<string, decimal>(employee.SalaryComponents, StringComparer.OrdinalIgnoreCase) : JsonSerializer.Deserialize<Dictionary<string, decimal>>(employee.SalaryJson, new JsonSerializerOptions { NumberHandling = JsonNumberHandling.AllowReadingFromString }) ?? [];
        var componentById = setup.Components.ToDictionary(component => component.Id);
        var activeStructures = setup.Structures.Where(item => item.Active).ToList();
        bool MatchesClient(SalaryStructureSetup item) => item.ClientId.Split([';', ',', '|']).Any(value => value.Split(':')[0].Trim() == employee.ClientId.ToString(CultureInfo.InvariantCulture));
        var structure = activeStructures.FirstOrDefault(item => item.Id == employee.SalaryStructureId && MatchesClient(item));
        if (!string.IsNullOrWhiteSpace(employee.SalaryStructureId) && structure is null)
            throw new InvalidOperationException($"{employee.EmployeeCode}: assigned salary template is inactive, missing, or belongs to another client.");
        structure ??= activeStructures.FirstOrDefault(MatchesClient);
        var hasDailyRates = structure?.Lines.Any(line => componentById.TryGetValue(line.ComponentId, out var c) && c.CalculationType == "Daily Rate") == true;
        if (hasDailyRates && string.IsNullOrWhiteSpace(employee.SalaryStructureId))
            throw new InvalidOperationException($"{employee.EmployeeCode}: explicitly assign a daily-rate salary template before running daily wages. Client fallback must not change the employee's pay basis.");
        var monthlyTarget = employee.SimulationAnnualCtc is decimal whatIfCtc ? decimal.Round(whatIfCtc / 12m, 2) : employee.AnnualCtc > 0 ? decimal.Round(employee.AnnualCtc / 12m, 2) : hasDailyRates ? 0 : FindGrossFromSalaryJson(salaryJson, componentById);
        if (monthlyTarget <= 0 && !employee.SimulationAnnualCtc.HasValue)
            monthlyTarget = NumberFrom(structure?.AnnualCtc ?? "") / 12m;

        if (structure is not null && structure.Lines.Count > 0 && (monthlyTarget > 0 || hasDailyRates || employee.SimulationAnnualCtc.HasValue))
        {
            ValidateRuntimePfMappings(setup.Components, structure, employee.PfPolicyPeriod);
            var overrides = new Dictionary<string, decimal>();
            // Legacy employee salary maps contain calculated values as well as overrides. Compare sequential full-month values, not earned-period values.
            CalculateStructureLines(setup.Components, structure, monthlyTarget, 30, 30, 30, storedAmounts: salaryJson, inferredOverrides: overrides);
            // Unknown presence has been explicitly proved irrelevant to every
            // effective expression above. This unused arithmetic operand is not
            // stored, exposed, or permitted to resolve a PRESENT_DAYS formula.
            return CalculateStructureLines(setup.Components, structure, monthlyTarget, payrollDays, presentDays.GetValueOrDefault(), payableDays, employee.PfPolicyPeriod, overrides, trace: trace);
        }
        if (employee.PfPolicyPeriod is not null)
            throw new InvalidOperationException("The published PF policy requires its salary template and a positive monthly salary base.");
        if (salaryJson.Keys.Any(key => componentById.TryGetValue(key, out var c) && c.CalculationType == "Daily Rate"))
            throw new InvalidOperationException("Daily-rate pay requires an assigned salary template; a daily rate cannot be treated as a monthly salary.");

        return salaryJson
            .Select(entry => componentById.TryGetValue(entry.Key, out var component)
                ? new CalculatedPayrollComponent(component, entry.Value)
                : new CalculatedPayrollComponent(new PayrollComponent(entry.Key, entry.Key, "Component", "Earning", "Regular Earning", "None", "Fixed Amount", "", "", "", true, true, 999, "Fixed Pay"), entry.Value))
            .Where(row => row.Component.Active)
            .OrderBy(row => row.Component.Priority)
            .ToList();
    }

    private static List<SeededPayrollDeduction> ReadSeededPayrollDeductions(PayRunSourceEmployee employee)
    {
        var rows = new List<SeededPayrollDeduction>();
        if (employee.HasPersonalDetails)
        {
            if (employee.EsicEmployee > 0) rows.Add(new SeededPayrollDeduction("ESIC", "Employee ESIC", employee.EsicEmployee, "ESI Employee"));
            if (employee.PtLwfWorkmenComp > 0) rows.Add(new SeededPayrollDeduction("PT", "Professional Tax", employee.PtLwfWorkmenComp, "Professional Tax"));
            if (employee.Tds > 0) rows.Add(new SeededPayrollDeduction("TDS", "TDS", employee.Tds, "TDS"));
            if (employee.Recovery > 0) rows.Add(new SeededPayrollDeduction("RECOVERY", "Recovery", employee.Recovery, "None"));
            return rows;
        }
        var personalJson = employee.PersonalJson;
        if (string.IsNullOrWhiteSpace(personalJson)) return rows;
        try
        {
            using var document = JsonDocument.Parse(personalJson);
            var root = document.RootElement;
            var esic = NumberFrom(Text(root, "esicEmployee"));
            var ptLwfWorkmen = NumberFrom(Text(root, "ptLwfWorkmenComp"));
            var tds = NumberFrom(Text(root, "tds"));
            var recovery = NumberFrom(Text(root, "recovery"));
            if (esic > 0) rows.Add(new SeededPayrollDeduction("ESIC", "Employee ESIC", esic, "ESI Employee"));
            if (ptLwfWorkmen > 0) rows.Add(new SeededPayrollDeduction("PT", "Professional Tax", ptLwfWorkmen, "Professional Tax"));
            if (tds > 0) rows.Add(new SeededPayrollDeduction("TDS", "TDS", tds, "TDS"));
            if (recovery > 0) rows.Add(new SeededPayrollDeduction("RECOVERY", "Recovery", recovery, "None"));
        }
        catch
        {
        }
        return rows;
    }

    private static void ValidateRuntimePfMappings(List<PayrollComponent> components, SalaryStructureSetup structure, PfPolicyPeriod? period)
    {
        if (period is null) return;
        foreach (var version in period.Versions)
        {
            var baseComponent = components.SingleOrDefault(c => c.Active && c.Code.Equals(version.BaseComponentCode, StringComparison.OrdinalIgnoreCase) && c.Category == "Earning");
            var baseLine = baseComponent is null ? null : structure.Lines.FirstOrDefault(l => l.ComponentId == baseComponent.Id);
            if (baseComponent is null || baseLine is null) throw new InvalidOperationException("A published PF policy monthly base was removed or deactivated. Review the policy before payroll.");
            var baseProRata = NullableBool(baseLine.ProRataOverride) ?? baseComponent.ProRata;
            var baseFormula = FirstText(baseLine.Formula, baseLine.Value, baseComponent.Formula, baseComponent.Value);
            if ((!baseProRata && baseComponent.CalculationType != "Daily Rate") || baseFormula.Contains("PAYABLE_DAYS", StringComparison.OrdinalIgnoreCase) || baseFormula.Contains("PRESENT_DAYS", StringComparison.OrdinalIgnoreCase) || baseFormula.Contains("_EARNED", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("A published PF monthly base now uses incompatible proration. Review the policy before payroll.");
            foreach (var rule in version.Contributions)
            {
                var component = components.SingleOrDefault(c => c.Id == rule.ComponentId && c.Active);
                if (component is null || !structure.Lines.Any(l => l.ComponentId == rule.ComponentId) || component.Code != rule.ComponentCode || component.StatutoryType != rule.StatutoryType || component.Priority <= baseComponent.Priority
                    || rule.StatutoryType == "PF Employee" && component.Category != "Deduction" || rule.StatutoryType == "PF Employer" && component.Category != "Benefit")
                    throw new InvalidOperationException("A published PF component mapping was removed, reclassified or reordered. Review the policy before payroll.");
            }
        }
    }

    public static List<PayrollReviewAmount> PreviewAdvisorTemplate(PayrollReviewSnapshot snapshot, int payrollDays, decimal payableDays)
    {
        if (payrollDays is < 1 or > 31 || payableDays < 0 || payableDays > payrollDays) throw new InvalidOperationException("Invalid simulation days.");
        var components = snapshot.Components.Select(c => new PayrollComponent(c.Id, c.Code, c.Code, c.Category, c.ComponentRole, c.StatutoryType,
            c.CalculationType, c.Value, c.Formula, c.BaseComponent, c.ProRata, c.Active, c.Priority, c.PayType, c.Taxable)).ToList();
        var structure = new SalaryStructureSetup("draft-review", snapshot.ClientId.ToString(CultureInfo.InvariantCulture), snapshot.AnnualCtc.ToString(CultureInfo.InvariantCulture),
            snapshot.Lines.Select(l => new SalaryStructureLine(l.ComponentId, l.Value, l.CalculationType, l.Formula, l.BaseComponent, l.ProRataOverride, "")).ToList(), true);
        // Same arithmetic as payroll, without employees, persisted results, tax projection or dated PF policy application.
        return CalculateStructureLines(components, structure, decimal.Round(snapshot.AnnualCtc / 12m, 2), payrollDays, payableDays, payableDays)
            .Select(r => new PayrollReviewAmount(r.Component.Code, r.Component.Category, r.Monthly,
                r.DailyRate is decimal rate ? decimal.Round(rate * payableDays, 2) : r.Component.ProRata ? decimal.Round(r.Monthly * payableDays / payrollDays, 2) : r.Monthly)).ToList();
    }

    private static List<CalculatedPayrollComponent> CalculateStructureLines(List<PayrollComponent> components, SalaryStructureSetup structure, decimal monthlyTarget, int payrollDays, decimal presentDays, decimal payableDays, PfPolicyPeriod? pfPeriod = null, Dictionary<string, decimal>? overrides = null, Dictionary<string, decimal>? storedAmounts = null, Dictionary<string, decimal>? inferredOverrides = null, List<PayrollSimulationLine>? trace = null)
    {
        var componentById = components.ToDictionary(component => component.Id);
        var values = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<CalculatedPayrollComponent>();

        if (structure.Lines.Any(line => !componentById.TryGetValue(line.ComponentId, out var component) || !component.Active))
            throw new InvalidOperationException($"Salary template {structure.Id} contains an inactive or missing component.");
        if (structure.Lines.Select(line => line.ComponentId).Distinct().Count() != structure.Lines.Count)
            throw new InvalidOperationException($"Salary template {structure.Id} contains duplicate components.");
        var orderedLines = structure.Lines
            .Select((line, index) => new { line, index, component = componentById.GetValueOrDefault(line.ComponentId) })
            .Where(item => item.component is not null && item.component.Active)
            .OrderBy(item => item.component!.Priority)
            .ThenBy(item => item.index).ToList();

        var sorted = new List<SalaryStructureLine>();
        var visiting = new HashSet<string>();
        var visited = new HashSet<string>();
        void Visit(SalaryStructureLine line)
        {
            if (visited.Contains(line.ComponentId)) return;
            var component = componentById[line.ComponentId];
            if (!visiting.Add(line.ComponentId)) throw new InvalidOperationException($"Circular salary formula dependency: {component.Code}.");
            var type = FirstText(line.CalculationType, component.CalculationType);
            var normalizedType = NormalizeCalculationType(type);
            if (normalizedType is "Formula" or "Residual / Balancing")
            {
                var formula = ComponentFormula(line, component);
                var references = normalizedType == "Residual / Balancing" && !formula.Contains("SUM", StringComparison.OrdinalIgnoreCase)
                    ? new[] { FirstText(line.BaseComponent, component.BaseComponent, "GROSS").ToUpperInvariant() } : FormulaReferences(formula);
                foreach (var token in references)
                {
                    if (KnownFormulaTokens.Contains(token) || token == "GROSS_EARNED") continue;
                    var dependency = orderedLines.FirstOrDefault(other => other.component!.Code.Equals(token, StringComparison.OrdinalIgnoreCase) || other.component.Id == token)
                        ?? orderedLines.FirstOrDefault(other => other.component!.Code.Equals(BaseComponentToken(token), StringComparison.OrdinalIgnoreCase) || other.component.Id == BaseComponentToken(token));
                    if (dependency is null) throw new InvalidOperationException($"{component.Code}: reference {token} is not in this salary template.");
                    Visit(dependency.line);
                }
            }
            visiting.Remove(line.ComponentId);
            visited.Add(line.ComponentId);
            sorted.Add(line);
        }
        foreach (var item in orderedLines) Visit(item.line);

        foreach (var line in sorted)
        {
            var component = componentById[line.ComponentId];
            var earnedRows = rows.Where(row => IsPayableEarningCategory(row.Component.Category)).ToList();
            values["EARNINGS_BEFORE_THIS"] = earnedRows.Sum(row => row.Monthly);
            values["FIXED_EARNINGS_BEFORE_THIS"] = earnedRows.Where(row => row.Component.PayType == "Fixed Pay").Sum(row => row.Monthly);
            values["FIXED_EARNINGS"] = values["FIXED_EARNINGS_BEFORE_THIS"];
            values["GROSS_EARNED"] = earnedRows.Sum(row => row.DailyRate is decimal rate ? decimal.Round(rate * payableDays, 2) : row.Component.ProRata && payrollDays > 0 ? decimal.Round(row.Monthly * payableDays / payrollDays, 2) : row.Monthly);
            var calculationType = FirstText(line.CalculationType, component.CalculationType);
            var formula = ComponentFormula(line, component);
            var proRata = NullableBool(line.ProRataOverride) ?? component.ProRata;
            var effectiveComponent = component with
            {
                CalculationType = calculationType,
                Formula = formula,
                Value = FirstText(line.Value, component.Value),
                BaseComponent = FirstText(line.BaseComponent, component.BaseComponent),
                ProRata = proRata
            };
            var type = NormalizeCalculationType(calculationType);
            var traceInputs = trace is null ? null : PayrollValueCatalogue.Resolve(monthlyTarget, payrollDays, presentDays, payableDays, values);
            if ((component.CalculationType == "Daily Rate") != (type == "Daily Rate"))
                throw new InvalidOperationException($"{component.Code}: daily-rate calculation cannot be converted by a template override. Use a separate component.");
            decimal? dailyRate = null;
            if (type == "Daily Rate")
            {
                if (component.Category != "Earning" || payrollDays <= 0 || payableDays < 0 || payableDays > payrollDays)
                    throw new InvalidOperationException($"{component.Code}: daily wages require valid attendance payable days and an earning component.");
                var rateText = overrides?.TryGetValue(component.Id, out var suppliedRate) == true ? suppliedRate.ToString(CultureInfo.InvariantCulture)
                    : storedAmounts is not null && (storedAmounts.TryGetValue(component.Id, out var savedRate) || storedAmounts.TryGetValue(component.Code, out savedRate)) ? savedRate.ToString(CultureInfo.InvariantCulture) : formula;
                if (!decimal.TryParse(rateText, NumberStyles.Number, CultureInfo.InvariantCulture, out var rate))
                    throw new InvalidOperationException($"{component.Code}: enter a numeric daily rate.");
                if (storedAmounts is not null && (storedAmounts.TryGetValue(component.Id, out var storedRate) || storedAmounts.TryGetValue(component.Code, out storedRate)))
                { rate = storedRate; inferredOverrides![component.Id] = storedRate; }
                if (overrides?.TryGetValue(component.Id, out var personalRate) == true) rate = personalRate;
                if (rate <= 0) throw new InvalidOperationException($"{component.Code}: daily rate must be greater than zero.");
                dailyRate = rate;
                effectiveComponent = effectiveComponent with { ProRata = false };
            }
            var monthly = type switch
            {
                "Daily Rate" => dailyRate!.Value * payrollDays,
                "Manual / Variable" => 0,
                "Slab Based" => SlabValue(formula, values.GetValueOrDefault("GROSS_EARNED", monthlyTarget)),
                "Residual / Balancing" => CalculateResidual(effectiveComponent, formula, monthlyTarget, payrollDays, presentDays, payableDays, values),
                "Formula" => EvaluateComponentFormula(effectiveComponent, formula, monthlyTarget, payrollDays, presentDays, payableDays, values),
                _ => ReadRequiredAmount(component, formula, overrides, storedAmounts)
            };
            if (dailyRate is null && storedAmounts is not null && (storedAmounts.TryGetValue(component.Id, out var stored) || storedAmounts.TryGetValue(component.Code, out stored))
                && !new[] { "GROSS_EARNED", "NET_PAY", "EMPLOYER_COST" }.Contains(component.Code)
                && ((Math.Abs(stored - monthly) > 0.009m && stored != Math.Round(monthly, 0, MidpointRounding.AwayFromZero))
                    || type == "Fixed Amount" && !decimal.TryParse(formula, NumberStyles.Number, CultureInfo.InvariantCulture, out _)))
            {
                inferredOverrides![component.Id] = stored;
                monthly = stored;
            }
            if (dailyRate is null && overrides?.TryGetValue(component.Id, out var overridden) == true) monthly = overridden;
            var pfSnapshot = PfPolicyCalculator.Calculate(pfPeriod, structure.Id, component.Id, component.Code, component.StatutoryType,
                values, payrollDays, payableDays);
            if (pfSnapshot is not null)
            {
                monthly = pfSnapshot.Contribution;
                effectiveComponent = effectiveComponent with { ProRata = false };
            }
            var raw = monthly;
            monthly = Math.Max(0, decimal.Round(monthly, 2));
            values[component.Id] = monthly;
            values[component.Code] = monthly;
            values[$"{component.Id}_MONTHLY"] = monthly;
            values[$"{component.Code}_MONTHLY"] = monthly;
            var earned = dailyRate is decimal rateValue ? decimal.Round(rateValue * payableDays, 2) : effectiveComponent.ProRata && payrollDays > 0 ? decimal.Round(monthly * payableDays / payrollDays, 2) : monthly;
            values[$"{component.Id}_EARNED"] = earned;
            values[$"{component.Code}_EARNED"] = earned;
            rows.Add(new CalculatedPayrollComponent(effectiveComponent, monthly, pfSnapshot, dailyRate));
            if (trace is not null) trace.Add(CreateSimulationTrace(effectiveComponent, formula, raw, monthly, earned, dailyRate, payableDays, pfSnapshot is not null, overrides?.ContainsKey(component.Id) == true, traceInputs!) with
            {
                // Observational trace only: retain the actual input to the earned currency-rounding stage.
                UnroundedEarned = dailyRate is decimal tracedRate ? tracedRate * payableDays
                    : effectiveComponent.ProRata && payrollDays > 0 ? monthly * payableDays / payrollDays : raw
            });
        }

        return rows;
    }

    private static string ComponentFormula(SalaryStructureLine line, PayrollComponent component)
    {
        var source = FirstText(line.Formula, line.Value, component.Formula, component.Value);
        var type = FirstText(line.CalculationType, component.CalculationType);
        if (Regex.IsMatch(source.Trim(), @"^\d+(\.\d+)?\s*%?$"))
        {
            var rate = source.Trim().TrimEnd('%').Trim();
            if (type.Equals("Percentage of CTC", StringComparison.OrdinalIgnoreCase)) return $"CTC * {rate}%";
            if (type.Equals("Percentage of Component", StringComparison.OrdinalIgnoreCase)) return $"{FirstText(line.BaseComponent, component.BaseComponent, "BASIC")} * {rate}%";
        }
        return source;
    }

    private static decimal EvaluateComponentFormula(PayrollComponent component, string source, decimal monthlyTarget, int payrollDays, decimal presentDays, decimal payableDays, Dictionary<string, decimal> values) =>
        EvaluateFormula(source, monthlyTarget, payrollDays, presentDays, payableDays, values);

    private static decimal CalculateResidual(PayrollComponent component, string source, decimal monthlyTarget, int payrollDays, decimal presentDays, decimal payableDays, Dictionary<string, decimal> values)
    {
        if (!string.IsNullOrWhiteSpace(source) && source.Contains("SUM", StringComparison.OrdinalIgnoreCase))
            return Math.Max(0, EvaluateFormula(source, monthlyTarget, payrollDays, presentDays, payableDays, values));
        var targetKey = FirstText(component.BaseComponent, "GROSS");
        var resolved = PayrollValueCatalogue.Resolve(monthlyTarget, payrollDays, presentDays, payableDays, values);
        if (!resolved.TryGetValue(targetKey, out var target)) throw new InvalidOperationException($"{component.Code}: balance target {targetKey} is missing. Include its component in the template.");
        var used = values.GetValueOrDefault("EARNINGS_BEFORE_THIS");
        return Math.Max(0, target - used);
    }

    private static decimal ReadRequiredAmount(PayrollComponent component, string source, Dictionary<string, decimal>? overrides, Dictionary<string, decimal>? stored)
    {
        if (overrides?.TryGetValue(component.Id, out var personal) == true) return personal;
        if (decimal.TryParse(source, NumberStyles.Number, CultureInfo.InvariantCulture, out var result)) return result;
        // During the legacy 30-day inference pass, a supplied employee amount becomes an explicit override below.
        if (stored is not null && (stored.ContainsKey(component.Id) || stored.ContainsKey(component.Code))) return 0;
        throw new InvalidOperationException($"{component.Code}: amount is missing or not numeric. Supply a component, template or employee amount; use an explicit zero only if intended.");
    }

    private static decimal EvaluateFormula(string source, decimal monthlyTarget, int payrollDays, decimal presentDays, decimal payableDays, Dictionary<string, decimal> values)
    {
        if (string.IsNullOrWhiteSpace(source)) throw new InvalidOperationException("Payroll formula is empty.");
        var refs = PayrollValueCatalogue.Resolve(monthlyTarget, payrollDays, presentDays, payableDays, values);
        var formula = source.ToUpperInvariant().Replace("\u00D7", "*").Replace("\u00F7", "/");
        formula = Regex.Replace(formula, @"SUM\s*\(\s*(FIXED\s+)?EARNINGS(_BEFORE_THIS)?\s*\)", match => values.GetValueOrDefault(match.Groups[1].Success ? "FIXED_EARNINGS_BEFORE_THIS" : "EARNINGS_BEFORE_THIS").ToString(CultureInfo.InvariantCulture), RegexOptions.IgnoreCase);
        formula = Regex.Replace(formula, @"(\d+(?:\.\d+)?)\s*%\s*OF\s*([A-Z0-9_]+)", "$2*$1/100", RegexOptions.IgnoreCase);
        try { return PayrollFormulaEvaluator.Evaluate(formula, name => refs.TryGetValue(name, out var amount) ? amount : throw new InvalidOperationException($"Unknown or unresolved value: {name}.")); }
        catch (Exception exception) when (exception is FormatException or OverflowException or DivideByZeroException or InvalidOperationException)
        { throw new InvalidOperationException($"Invalid payroll formula '{source}': {exception.Message}", exception); }
    }

    private static decimal SlabValue(string source, decimal baseAmount)
    {
        foreach (var slab in source.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = slab.Split(':', 2);
            if (parts.Length != 2) continue;
            var amount = NumberFrom(parts[1]);
            if (parts[0].Contains('+') && baseAmount >= NumberFrom(parts[0])) return amount;
            var range = parts[0].Split('-', 2);
            if (range.Length == 2 && baseAmount >= NumberFrom(range[0]) && baseAmount <= NumberFrom(range[1])) return amount;
        }
        return 0;
    }

    private static decimal CalculateProfessionalTax(PayRunSourceEmployee employee, PayrollSetupData setup, decimal baseAmount, string payPeriod)
    {
        if (!setup.ProfessionalTax.Enabled || baseAmount <= 0) return 0;
        var state = FirstText(employee.PersonalState, employee.WorkState, setup.ProfessionalTax.DefaultState);
        if (string.IsNullOrWhiteSpace(state)) return 0;
        var gender = FirstText(employee.Gender, "All");
        var payDate = DateTime.TryParse($"{payPeriod}-01", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed.Date : DateTime.Today;
        var slab = setup.ProfessionalTax.Slabs
            .Where(row => row.Active)
            .Where(row => row.State.Equals(state, StringComparison.OrdinalIgnoreCase))
            .Where(row => row.Gender.Equals("All", StringComparison.OrdinalIgnoreCase) || row.Gender.Equals(gender, StringComparison.OrdinalIgnoreCase))
            .Where(row => row.EffectiveFrom is null || row.EffectiveFrom.Value <= payDate)
            .Where(row => row.EffectiveTo is null || row.EffectiveTo.Value >= payDate)
            .Where(row => baseAmount >= row.SalaryFrom && (row.SalaryTo is null || baseAmount <= row.SalaryTo.Value))
            .OrderByDescending(row => row.SalaryFrom)
            .ThenBy(row => row.Gender.Equals("All", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
        return slab is null ? 0 : Math.Max(0, decimal.Round(slab.DeductionAmount, 2));
    }

    private static PayrollSetupData ReadPayrollSetup(string setupJson)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(setupJson) ? "{}" : setupJson);
            var root = document.RootElement;
            var components = root.TryGetProperty("salaryComponents", out var componentJson) && componentJson.ValueKind == JsonValueKind.Array
                ? componentJson.EnumerateArray().Select(ReadComponent).Where(component => !string.IsNullOrWhiteSpace(component.Id)).ToList()
                : [];
            var structures = root.TryGetProperty("salaryStructures", out var structureJson) && structureJson.ValueKind == JsonValueKind.Array
                ? structureJson.EnumerateArray().Select(ReadStructure).Where(item => !string.IsNullOrWhiteSpace(item.Id)).ToList()
                : [];
            return new PayrollSetupData(components, structures, ReadProfessionalTaxSetup(root));
        }
        catch
        {
            return new PayrollSetupData([], [], new ProfessionalTaxSetup(false, "", "Monthly", []));
        }
    }

    private static ProfessionalTaxSetup ReadProfessionalTaxSetup(JsonElement root)
    {
        if (!root.TryGetProperty("statutory", out var statutory) || statutory.ValueKind != JsonValueKind.Object)
            return new ProfessionalTaxSetup(false, "", "Monthly", []);
        var slabs = statutory.TryGetProperty("ptStateSlabs", out var slabJson) && slabJson.ValueKind == JsonValueKind.Array
            ? slabJson.EnumerateArray().Select(ReadProfessionalTaxSlab).Where(row => !string.IsNullOrWhiteSpace(row.State) && row.DeductionAmount > 0).ToList()
            : [];
        return new ProfessionalTaxSetup(Bool(statutory, "pt", false) || slabs.Count > 0, Text(statutory, "ptState"), Text(statutory, "ptCycle", "Monthly"), slabs);
    }

    private static ProfessionalTaxSlab ReadProfessionalTaxSlab(JsonElement element) => new(
        Text(element, "state"),
        NumberFrom(Text(element, "salaryFrom", "0")),
        string.IsNullOrWhiteSpace(Text(element, "salaryTo")) ? null : NumberFrom(Text(element, "salaryTo")),
        NumberFrom(Text(element, "deductionAmount")),
        DateFrom(Text(element, "effectiveFrom")),
        DateFrom(Text(element, "effectiveTo")),
        FirstText(Text(element, "gender"), "All"),
        Bool(element, "active", true));

    private static PayrollComponent ReadComponent(JsonElement element) => new(
        Text(element, "id"),
        Text(element, "code", Text(element, "id")).ToUpperInvariant(),
        Text(element, "name", "Component"),
        Text(element, "category", "Earning"),
        Text(element, "componentRole", "Regular Earning"),
        Text(element, "statutoryType", "None"),
        Text(element, "calculationType", "Fixed Amount"),
        Text(element, "value"),
        Text(element, "formula"),
        Text(element, "baseComponent"),
        Bool(element, "proRata", true),
        Bool(element, "active", true),
        (int)NumberFrom(Text(element, "priority", "999")),
        Text(element, "payType", "Fixed Pay"), Bool(element, "taxable", true));

    private static SalaryStructureSetup ReadStructure(JsonElement element)
    {
        var lines = element.TryGetProperty("lines", out var lineJson) && lineJson.ValueKind == JsonValueKind.Array
            ? lineJson.EnumerateArray().Select(line => new SalaryStructureLine(
                Text(line, "componentId"),
                Text(line, "value"),
                Text(line, "calculationType"),
                Text(line, "formula"),
                Text(line, "baseComponent"),
                Text(line, "proRataOverride"),
                Text(line, "roundingMode"))).Where(line => !string.IsNullOrWhiteSpace(line.ComponentId)).ToList()
            : [];
        return new SalaryStructureSetup(Text(element, "id"), Text(element, "clientId"), Text(element, "annualCtc"), lines, Bool(element, "active", true));
    }

    private static decimal FindGrossFromSalaryJson(Dictionary<string, decimal> salaryJson, Dictionary<string, PayrollComponent> componentById)
    {
        var gross = salaryJson.FirstOrDefault(entry => entry.Key.Equals("GROSS", StringComparison.OrdinalIgnoreCase)).Value;
        if (gross > 0) return gross;
        return salaryJson.Where(entry => componentById.TryGetValue(entry.Key, out var component) && IsPayableEarningCategory(component.Category)).Sum(entry => entry.Value);
    }

    private static string NormalizeCalculationType(string value) =>
        value == "Daily Rate" ? "Daily Rate" :
        value.Equals("Percentage of CTC", StringComparison.OrdinalIgnoreCase) || value.Equals("Percentage of Component", StringComparison.OrdinalIgnoreCase) || value.Equals("Formula", StringComparison.OrdinalIgnoreCase) ? "Formula" :
        value.Equals("Balancing Amount", StringComparison.OrdinalIgnoreCase) || value.Equals("Residual / Balancing", StringComparison.OrdinalIgnoreCase) ? "Residual / Balancing" :
        value.Equals("Manual Entry", StringComparison.OrdinalIgnoreCase) || value.Equals("Manual Override", StringComparison.OrdinalIgnoreCase) || value.Equals("Manual / Variable", StringComparison.OrdinalIgnoreCase) ? "Manual / Variable" :
        value.Equals("Slab Based", StringComparison.OrdinalIgnoreCase) ? "Slab Based" : "Fixed Amount";

    private static bool IsPayableEarningCategory(string category) => category.Equals("Earning", StringComparison.OrdinalIgnoreCase) || category.Equals("Reimbursement", StringComparison.OrdinalIgnoreCase);
    private static bool IsDeductionCategory(string category) => category.Equals("Deduction", StringComparison.OrdinalIgnoreCase);
    private static string FirstText(params string[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    private static string Text(JsonElement element, string property, string fallback = "") => element.TryGetProperty(property, out var value) ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : value.ToString() : fallback;
    private static bool Bool(JsonElement element, string property, bool fallback) => element.TryGetProperty(property, out var value) ? value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, JsonValueKind.String => bool.TryParse(value.GetString(), out var parsed) ? parsed : fallback, _ => fallback } : fallback;
    private static bool? NullableBool(string value) => string.IsNullOrWhiteSpace(value) ? null : bool.TryParse(value, out var parsed) ? parsed : value.Equals("Yes", StringComparison.OrdinalIgnoreCase) ? true : value.Equals("No", StringComparison.OrdinalIgnoreCase) ? false : null;
    private static decimal NumberFrom(string value) => decimal.TryParse(Regex.Replace(value ?? "", @"[^\d.-]", ""), NumberStyles.Any, CultureInfo.InvariantCulture, out var result) ? result : 0;
    private static DateTime? DateFrom(string value) => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date.Date : null;
    private static string FinancialYearFromPayPeriod(string payPeriod)
    {
        var date = DateTime.TryParse($"{payPeriod}-01", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : DateTime.Today;
        var startYear = date.Month >= 4 ? date.Year : date.Year - 1;
        return $"{startYear}-{(startYear + 1) % 100:00}";
    }

    private static (DateTime Start, DateTime End) FinancialYearRangeFromCode(string financialYear)
    {
        var startYear = int.TryParse((financialYear ?? "").Split('-').FirstOrDefault(), out var parsed) ? parsed : DateTime.Today.Year;
        return (new DateTime(startYear, 4, 1), new DateTime(startYear + 1, 3, 31));
    }

    private static string JsonValue(string json, string property)
    {
        if (string.IsNullOrWhiteSpace(json)) return "";
        try
        {
            using var document = JsonDocument.Parse(json);
            return Text(document.RootElement, property);
        }
        catch
        {
            return "";
        }
    }

    private static bool SetupBool(string setupJson, string section, string property)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(setupJson) ? "{}" : setupJson);
            return document.RootElement.TryGetProperty(section, out var sectionElement) && Bool(sectionElement, property, false);
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<string> FormulaReferences(string formula) =>
        Regex.Matches(formula.ToUpperInvariant(), @"\b[A-Z_][A-Z0-9_]*\b")
            .Select(match => match.Value)
            .Where(token => token is not ("MIN" or "MAX" or "ROUND" or "ROUNDDOWN" or "ROUNDUP" or "SUM" or "IF" or "IFERROR" or "AND" or "OR" or "ABS" or "TRUE" or "FALSE" or "OF" or "FIXED" or "EARNINGS" or "BEFORE" or "THIS"));

    private static readonly HashSet<string> KnownFormulaTokens = PayrollValueCatalogue.Names;
    private static readonly HashSet<string> LatestAttemptStatuses = new(StringComparer.OrdinalIgnoreCase) { "Draft", "Failed", "Queued", "Processing" };

    private static readonly List<PayrollPipelineStep> PayrollPipelineSteps =
    [
        new(1, "Employee Master Validation"), new(2, "Employment Status Validation"), new(3, "Salary Structure Validation"), new(4, "Payroll Period Validation"),
        new(5, "Attendance Freeze"), new(6, "Leave Freeze"), new(7, "Shift Processing"), new(8, "Holiday Processing"), new(9, "Weekly Off Processing"),
        new(10, "Leave Without Pay"), new(11, "Payable Days Calculation"), new(12, "Fixed Earnings Calculation"), new(13, "Variable Earnings"),
        new(14, "Incentives"), new(15, "Overtime"), new(16, "Night Shift Allowance"), new(17, "Attendance Bonus"), new(18, "Performance Bonus"),
        new(19, "Sales Commission"), new(20, "Reimbursements"), new(21, "Claims Processing"), new(22, "Loan Recovery"), new(23, "Salary Advance Recovery"),
        new(24, "Notice Recovery"), new(25, "Other Recoveries"), new(26, "Arrear Processing"), new(27, "Increment Arrears"), new(28, "Promotion Arrears"),
        new(29, "Retrospective Salary Revision"), new(30, "Full & Final Settlement Adjustments"), new(31, "Gratuity"), new(32, "Leave Encashment"),
        new(33, "Employer Contributions"), new(34, "Employee Deductions"), new(35, "Provident Fund"), new(36, "ESI"), new(37, "Professional Tax"),
        new(38, "Labour Welfare Fund"), new(39, "Other Statutory Deductions"), new(40, "Income Tax Projection"), new(41, "Tax Exemptions"),
        new(42, "Previous Employer Income"), new(43, "Other Income"), new(44, "Tax Regime Processing"), new(45, "Investment Declaration"),
        new(46, "Investment Proof Validation"), new(47, "Monthly TDS Calculation"), new(48, "Final Gross Salary"), new(49, "Total Deductions"),
        new(50, "Net Salary"), new(51, "Payroll Validation"), new(52, "Payroll Approval"), new(53, "Payroll Freeze"), new(54, "Bank Advice Generation"),
        new(55, "GL Posting"), new(56, "Report Population"), new(57, "Statutory Return Population"), new(58, "Employee Self Service Data Population"),
        new(59, "Reconciliation")
    ];

    private sealed record PayrollSetupData(List<PayrollComponent> Components, List<SalaryStructureSetup> Structures, ProfessionalTaxSetup ProfessionalTax);
    private sealed record PayrollPipelineStep(int Number, string Name);
    private sealed record SalaryStructureSetup(string Id, string ClientId, string AnnualCtc, List<SalaryStructureLine> Lines, bool Active);
    private sealed record SalaryStructureLine(string ComponentId, string Value, string CalculationType, string Formula, string BaseComponent, string ProRataOverride, string RoundingMode);
    private sealed record PayrollComponent(string Id, string Code, string Name, string Category, string ComponentRole, string StatutoryType, string CalculationType, string Value, string Formula, string BaseComponent, bool ProRata, bool Active, int Priority, string PayType, bool Taxable = true);
    private sealed record ProfessionalTaxSetup(bool Enabled, string DefaultState, string Cycle, List<ProfessionalTaxSlab> Slabs);
    private sealed record ProfessionalTaxSlab(string State, decimal SalaryFrom, decimal? SalaryTo, decimal DeductionAmount, DateTime? EffectiveFrom, DateTime? EffectiveTo, string Gender, bool Active);
    private sealed record CalculatedPayrollComponent(PayrollComponent Component, decimal Monthly, PfComponentSnapshot? PfPolicy = null, decimal? DailyRate = null,
        decimal? EarnedOverride = null, IReadOnlyList<PayrollLocationComponentPart>? LocationParts = null);
    private sealed record SeededPayrollDeduction(string Code, string Name, decimal Amount, string StatutoryType);

    private static async Task SaveEmployeeAsync(MySqlConnection connection, MySqlTransaction transaction, PayRunEmployee row)
    {
        row.Id = (int)await connection.ExecuteScalarAsync<long>(@"
INSERT INTO payrunemployees (PayRunId, EmployeeId, ClientId, ClientName, EmployeeCode, EmployeeName, Department, PresentDays, PayableDays, MonthlyGross, GrossPay, StatutoryDeductions, OneTimeEarnings, OneTimeDeductions, ManualTds, NetPay, IsSkipped, DetailsJson)
VALUES (@PayRunId, @EmployeeId, @ClientId, @ClientName, @EmployeeCode, @EmployeeName, @Department, @PresentDays, @PayableDays, @MonthlyGross, @GrossPay, @StatutoryDeductions, @OneTimeEarnings, @OneTimeDeductions, @ManualTds, @NetPay, @IsSkipped, @DetailsJson)
ON DUPLICATE KEY UPDATE Id=LAST_INSERT_ID(Id), ClientId=@ClientId, ClientName=@ClientName, EmployeeCode=@EmployeeCode, EmployeeName=@EmployeeName, Department=@Department, PresentDays=@PresentDays, PayableDays=@PayableDays, MonthlyGross=@MonthlyGross, GrossPay=@GrossPay, StatutoryDeductions=@StatutoryDeductions, OneTimeEarnings=@OneTimeEarnings, OneTimeDeductions=@OneTimeDeductions, ManualTds=@ManualTds, NetPay=@NetPay, IsSkipped=@IsSkipped, DetailsJson=@DetailsJson;
SELECT LAST_INSERT_ID();", row, transaction);
        await PayrollDataTableStore.SyncPayRunEmployeeLinesAsync(connection, transaction, row);
    }

    private static List<PayrollValidationIssue> ValidatePayRunInputs(int payRunId, CreatePayRunRequest request, string runType, List<PayRunSourceEmployee> employees, Dictionary<int, PayRunAttendance> attendance, string setupJson)
    {
        var issues = new List<PayrollValidationIssue>();
        var setup = ReadPayrollSetup(setupJson);
        if (employees.Count == 0)
            issues.Add(Issue(payRunId, null, "", "Run", "Critical", "Employee Master Validation", "No active employees are available for this client.", true));
        if (setup.Components.Count == 0)
            issues.Add(Issue(payRunId, null, "", "Run", "Critical", "Salary Structure Validation", "No salary components are configured.", true));
        if (runType == "Regular" && request.AttendanceGroupId > 0 && request.IncludedEmployeeIds.Count == 0)
            issues.Add(Issue(payRunId, null, "", "Run", "Critical", "Attendance Policy", "Attendance policy has no mapped employees. Select a policy with employees before running payroll.", true));
        // Effective template formulas are validated during calculation; unused global masters must not block this client.
        foreach (var employee in employees)
        {
            if (runType == "Regular" && attendance.TryGetValue(employee.Id, out var policyAttendance) && !string.IsNullOrWhiteSpace(policyAttendance.PolicyError))
                issues.Add(Issue(payRunId, employee.Id, employee.EmployeeCode, "Employee", "Critical", "Attendance Policy", policyAttendance.PolicyError, true));
            if(runType=="Regular"&&attendance.TryGetValue(employee.Id,out var supplied)&&supplied.PresentDays is null)
            {
                employee.PayableOnlyAttendance=supplied.PayableOnlyEvidence;
                employee.AttendancePolicySnapshot=supplied.PolicySnapshot;
                try { ValidatePayableOnlyCalculation(employee,setup,AttendanceWorkingDays(supplied,request.TotalWorkingDays),supplied.PayableDays); }
                catch(InvalidOperationException error) { issues.Add(Issue(payRunId,employee.Id,employee.EmployeeCode,"Employee","Critical","Actual presence unavailable",error.Message,true)); }
            }
            if (string.IsNullOrWhiteSpace(employee.EmployeeCode))
                issues.Add(Issue(payRunId, employee.Id, employee.EmployeeCode, "Employee", "Critical", "Employee Master Validation", "Employee code is missing.", true));
            if (string.IsNullOrWhiteSpace(employee.FirstName))
                issues.Add(Issue(payRunId, employee.Id, employee.EmployeeCode, "Employee", "Critical", "Employee Master Validation", "Employee name is missing.", true));
            if (string.IsNullOrWhiteSpace(employee.Department))
                issues.Add(Issue(payRunId, employee.Id, employee.EmployeeCode, "Employee", "Warning", "Employee Master Validation", "Department is missing; reporting and GL allocation may be incomplete.", false));
            if (employee.WorkLocationId <= 0)
                issues.Add(Issue(payRunId, employee.Id, employee.EmployeeCode, "Employee", "Warning", "Employee Master Validation", "Work location is missing; state-wise statutory logic may be incomplete.", false));
            if (runType == "Regular" && !attendance.ContainsKey(employee.Id))
                issues.Add(Issue(payRunId, employee.Id, employee.EmployeeCode, "Employee", "Critical", "Attendance Freeze", "Monthly attendance is missing for the pay period.", true));
            if (runType == "Regular" && attendance.TryGetValue(employee.Id, out var attendanceRow) && attendanceRow.WorkingDays > 31)
                issues.Add(Issue(payRunId, employee.Id, employee.EmployeeCode, "Employee", "Critical", "Attendance Freeze", "Attendance cycle cannot exceed 31 days in any payroll month.", true));
            if (employee.LocationParts is null && employee.AnnualCtc <= 0 && employee.SalaryComponents.Count == 0 && string.IsNullOrWhiteSpace(employee.SalaryStructureId))
                issues.Add(Issue(payRunId, employee.Id, employee.EmployeeCode, "Employee", "Critical", "Salary Structure Validation", "Employee has no annual CTC, salary components, or salary structure.", true));
            if (setup.ProfessionalTax.Enabled && string.IsNullOrWhiteSpace(FirstText(employee.PersonalState, employee.WorkState, setup.ProfessionalTax.DefaultState)))
                issues.Add(Issue(payRunId, employee.Id, employee.EmployeeCode, "Employee", "Warning", "Professional Tax", "State is missing while Professional Tax is enabled.", false));
        }
        return issues;
    }

    private static IEnumerable<PayrollValidationIssue> ValidateFormulaMasters(int payRunId, List<PayrollComponent> components)
    {
        var issues = new List<PayrollValidationIssue>();
        var knownCodes = components.Select(component => component.Code).Concat(components.Select(component => component.Id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var component in components.Where(component => component.Active && NormalizeCalculationType(component.CalculationType) == "Formula"))
        {
            var formula = FirstText(component.Formula, component.Value);
            if (string.IsNullOrWhiteSpace(formula))
            {
                issues.Add(Issue(payRunId, null, "", "Run", "Critical", "Formula Validation", $"{component.Code} has formula calculation type but no formula.", true));
                continue;
            }
            foreach (var token in FormulaReferences(formula).Where(token => !KnownFormulaTokens.Contains(token)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!knownCodes.Contains(token) && !IsKnownDerivedComponentToken(token, knownCodes))
                    issues.Add(Issue(payRunId, null, "", "Run", "Critical", "Formula Validation", $"{component.Code} formula references missing component/token {token}.", true));
                var dependencyToken = BaseComponentToken(token);
                var dependency = components.FirstOrDefault(item => item.Code.Equals(dependencyToken, StringComparison.OrdinalIgnoreCase) || item.Id.Equals(dependencyToken, StringComparison.OrdinalIgnoreCase));
                if (dependency is not null && dependency.Priority >= component.Priority)
                    issues.Add(Issue(payRunId, null, "", "Run", "Warning", "Formula Validation", $"{component.Code} depends on {dependency.Code}, but dependency priority is not earlier.", false));
            }
        }
        return issues;
    }

    private static bool IsKnownDerivedComponentToken(string token, HashSet<string> knownCodes) =>
        knownCodes.Contains(BaseComponentToken(token));

    private static string BaseComponentToken(string token)
    {
        foreach (var suffix in new[] { "_EARNED", "_MONTHLY" })
            if (token.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return token[..^suffix.Length];
        return token;
    }

    private static PayrollValidationIssue Issue(int payRunId, int? employeeId, string employeeCode, string scope, string severity, string stepName, string message, bool blocking) => new()
    {
        PayRunId = payRunId,
        EmployeeId = employeeId,
        EmployeeCode = employeeCode ?? "",
        Scope = scope,
        Severity = severity,
        StepName = stepName,
        Message = message,
        IsBlocking = blocking,
        DataJson = "{}"
    };

    private static Task WriteValidationIssuesAsync(MySqlConnection connection, MySqlTransaction transaction, List<PayrollValidationIssue> issues) =>
        issues.Count == 0
            ? Task.CompletedTask
            : connection.ExecuteAsync(@"INSERT INTO payroll_validation_issues (PayRunId,EmployeeId,EmployeeCode,Scope,IssueType,Severity,StepName,Message,DataJson,IsBlocking)
VALUES (@PayRunId,@EmployeeId,@EmployeeCode,@Scope,@IssueType,@Severity,@StepName,@Message,@DataJson,@IsBlocking);", issues.Select(issue => new { issue.PayRunId, issue.EmployeeId, EmployeeCode = Trunc(issue.EmployeeCode, 50), Scope = Trunc(issue.Scope, 40), IssueType = Trunc(issue.IssueType, 40), Severity = Trunc(issue.Severity, 20), StepName = Trunc(issue.StepName, 160), Message = Trunc(issue.Message, 1000), issue.DataJson, issue.IsBlocking }), transaction);

    private static Task WritePipelineStepLogsAsync(MySqlConnection connection, MySqlTransaction transaction, int payRunId, string performedBy, List<PayrollValidationIssue> issues)
    {
        var now = DateTime.UtcNow;
        var rows = PayrollPipelineSteps.Select(step =>
        {
            var stepIssues = issues.Where(issue => issue.StepName.Equals(step.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            var blocking = stepIssues.Any(issue => issue.IsBlocking);
            var warnings = stepIssues.Where(issue => !issue.IsBlocking).Select(issue => issue.Message).Take(5).ToArray();
            return new PayRunStepLog
            {
                PayRunId = payRunId,
                StepNumber = step.Number,
                StepName = step.Name,
                StartTime = now.AddMilliseconds(step.Number),
                EndTime = now.AddMilliseconds(step.Number + 1),
                DurationMs = 1,
                InputJson = "{}",
                RuleJson = JsonSerializer.Serialize(new { stage = step.Name, version = "1.0" }),
                FormulaJson = "{}",
                OldValueJson = "{}",
                NewValueJson = "{}",
                OutputJson = JsonSerializer.Serialize(new { warningCount = warnings.Length }),
                Status = blocking ? "Failed" : "Success",
                Warning = Trunc(string.Join(" | ", warnings), 1000),
                ErrorMessage = blocking ? Trunc(string.Join(" | ", stepIssues.Where(issue => issue.IsBlocking).Select(issue => issue.Message).Take(3)), 1000) : "",
                PerformedBy = performedBy,
                MachineName = Environment.MachineName,
                Version = "1.0"
            };
        }).ToList();
        return connection.ExecuteAsync(@"INSERT INTO payrun_step_logs (PayRunId,EmployeeId,StepNumber,StepName,StartTime,EndTime,DurationMs,InputJson,RuleJson,FormulaJson,OldValueJson,NewValueJson,OutputJson,Status,Warning,ErrorMessage,PerformedBy,MachineName,Version)
VALUES (@PayRunId,@EmployeeId,@StepNumber,@StepName,@StartTime,@EndTime,@DurationMs,@InputJson,@RuleJson,@FormulaJson,@OldValueJson,@NewValueJson,@OutputJson,@Status,@Warning,@ErrorMessage,@PerformedBy,@MachineName,@Version);", rows, transaction);
    }

    private static async Task WriteCalculationTracesAsync(MySqlConnection connection, MySqlTransaction transaction, PayRunEmployee row, int totalWorkingDays)
    {
        var traces = new List<PayrollCalculationTrace>();
        var factor = totalWorkingDays <= 0 || row.IsSkipped ? 0 : (decimal)row.PayableDays / totalWorkingDays;
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(row.DetailsJson) ? "[]" : row.DetailsJson);
        var order = 1;
        foreach (var element in document.RootElement.EnumerateArray())
        {
            var code = Text(element, "Id");
            if (string.IsNullOrWhiteSpace(code)) continue;
            var category = Text(element, "Category");
            traces.Add(new PayrollCalculationTrace
            {
                PayRunId = row.PayRunId,
                EmployeeId = row.EmployeeId,
                EmployeeCode = row.EmployeeCode,
                ComponentCode = code,
                ComponentName = Text(element, "Name", code),
                ParentComponentCode = category == "Summary" ? "" : "GROSS",
                TraceOrder = order++,
                RuleUsed = category,
                FormulaUsed = element.TryGetProperty("rate", out _) ? "Daily rate * attendance payable days" : element.TryGetProperty("pfPolicy", out _) ? "Sum of effective PF segments; round once after aggregation" : Bool(element, "ProRata", false) ? "MonthlyAmount * PayableDays / TotalWorkingDays" : "Configured amount",
                BaseAmount = NumberFrom(Text(element, "monthlyAmount")),
                Factor = Bool(element, "ProRata", false) ? factor : 1,
                CalculatedAmount = NumberFrom(Text(element, "amount")),
                InputJson = JsonSerializer.Serialize(new { row.PayableDays, TotalWorkingDays = totalWorkingDays, row.PresentDays }),
                OutputJson = element.GetRawText()
            });
        }
        if (traces.Count > 0)
            await connection.ExecuteAsync(@"INSERT INTO payroll_calculation_traces (PayRunId,EmployeeId,EmployeeCode,ComponentCode,ComponentName,ParentComponentCode,TraceOrder,RuleUsed,FormulaUsed,BaseAmount,Factor,CalculatedAmount,InputJson,OutputJson)
VALUES (@PayRunId,@EmployeeId,@EmployeeCode,@ComponentCode,@ComponentName,@ParentComponentCode,@TraceOrder,@RuleUsed,@FormulaUsed,@BaseAmount,@Factor,@CalculatedAmount,@InputJson,@OutputJson);", traces, transaction);
    }

    private async Task<TdsProjectionResult> CalculateMonthlyTdsAsync(MySqlConnection connection, MySqlTransaction transaction, PayRunSourceEmployee employee, PayRunEmployee preliminaryRow, string payPeriod, bool persistSnapshot = true, Action<object>? recordInputs = null)
    {
        var financialYear = FinancialYearFromPayPeriod(payPeriod);
        var taxSetting = await connection.QueryFirstOrDefaultAsync<TaxProjectionSetting>(@"SELECT enabled AS Enabled, project_monthly_tds AS ProjectMonthlyTds, tax_deduction_component_code AS TaxDeductionComponentCode FROM tax_client_settings WHERE client_id=@ClientId AND financial_year=@FinancialYear AND active=TRUE LIMIT 1", new { employee.ClientId, FinancialYear = financialYear }, transaction);
        var componentCode = FirstText(taxSetting?.TaxDeductionComponentCode ?? "", "TDS").ToUpperInvariant();
        recordInputs?.Invoke(new { financialYear,taxSetting,componentCode });
        if (taxSetting is { Enabled: false } || taxSetting is { ProjectMonthlyTds: false }) return new TdsProjectionResult(0, componentCode);
        if (taxSetting is null)
        {
            var hasActiveTaxMaster = await connection.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM tax_rule_versions WHERE financial_year=@FinancialYear AND active=TRUE", new { FinancialYear = financialYear }, transaction) > 0;
            recordInputs?.Invoke(new { hasActiveTaxMaster });
            if (!hasActiveTaxMaster) return new TdsProjectionResult(0, componentCode);
        }
        var (fyStart, _) = FinancialYearRangeFromCode(financialYear);
        var history = (await connection.QueryAsync<PayRunEmployee>(@"SELECT p.* FROM payrunemployees p JOIN payruns r ON r.Id=p.PayRunId
WHERE p.EmployeeId=@EmployeeId AND r.ClientId=@ClientId AND r.PayPeriod>=@StartPeriod AND r.PayPeriod<@PayPeriod
AND r.Status IN ('Approved','Partially Paid','Paid') AND p.IsSkipped=FALSE", new { EmployeeId = employee.Id, employee.ClientId, StartPeriod = fyStart.ToString("yyyy-MM"), PayPeriod = payPeriod }, transaction)).ToList();
        var months = RemainingFinancialYearMonths(payPeriod, financialYear);
        var annualGross = history.Sum(row => TaxableSnapshotValue(row, false)) + TaxableSnapshotValue(preliminaryRow, false) + TaxableSnapshotValue(preliminaryRow, true) * (months - 1);
        var tdsAlreadyDeducted = await connection.ExecuteScalarAsync<decimal?>(@"
SELECT COALESCE(SUM(l.Amount),0)
FROM payrunemployees p
JOIN payruns r ON r.Id=p.PayRunId
JOIN payrunemployeelines l ON l.PayRunEmployeeId=p.Id AND (l.StatutoryType='TDS' OR l.ComponentCode=@ComponentCode OR l.ComponentCode='TDS')
WHERE p.EmployeeId=@EmployeeId
  AND r.ClientId=@ClientId
  AND r.PayPeriod >= @StartPeriod
  AND r.PayPeriod < @PayPeriod
  AND r.Status IN ('Approved','Partially Paid','Paid')
  AND p.IsSkipped=FALSE;", new { EmployeeId = employee.Id, employee.ClientId, ComponentCode = componentCode, StartPeriod = fyStart.ToString("yyyy-MM"), PayPeriod = payPeriod }, transaction) ?? 0;
        var result = await taxEngineRepository.ComputeAsync(new TaxComputationRequest
        {
            PayRunId = preliminaryRow.PayRunId,
            EmployeeId = employee.Id,
            ClientId = employee.ClientId,
            FinancialYear = financialYear,
            PayPeriod = payPeriod,
            AnnualGrossSalary = annualGross,
            TdsAlreadyDeducted = tdsAlreadyDeducted
        }, connection, transaction, persistSnapshot);
        if (result is null) throw new InvalidOperationException($"No effective published tax rule exists for {financialYear}, period {payPeriod}.");
        if(recordInputs is not null)
        {
            var taxSnapshot=StableProjectedTaxInputs(result.SnapshotJson);
            recordInputs(new {history,tdsAlreadyDeducted,annualGross,months,taxSnapshot});
        }
        var remainingMonths = RemainingFinancialYearMonths(payPeriod, financialYear);
        var amount = remainingMonths <= 0 ? result.MonthlyTds : Math.Round(result.RemainingTax / remainingMonths, 0, MidpointRounding.AwayFromZero);
        return new TdsProjectionResult(Math.Max(0, amount), componentCode, true);
    }

    private static decimal TaxableSnapshotValue(PayRunEmployee row, bool monthly)
    {
        using var document = JsonDocument.Parse(row.DetailsJson);
        foreach (var line in document.RootElement.EnumerateArray())
            if (Text(line, "Id") == "TAXABLE_PAY") return NumberFrom(Text(line, monthly ? "monthlyAmount" : "amount"));
        // Historical rows predate the taxable snapshot; do not rewrite those paid runs.
        return monthly ? row.MonthlyGross : row.GrossPay + row.OneTimeEarnings;
    }

    private static int RemainingFinancialYearMonths(string payPeriod, string financialYear)
    {
        if (!DateTime.TryParse($"{payPeriod}-01", CultureInfo.InvariantCulture, DateTimeStyles.None, out var periodDate)) return 12;
        var (_, fyEnd) = FinancialYearRangeFromCode(financialYear);
        return Math.Max(1, ((fyEnd.Year - periodDate.Year) * 12) + fyEnd.Month - periodDate.Month + 1);
    }

    private static async Task WriteReconciliationResultsAsync(MySqlConnection connection, MySqlTransaction transaction, int payRunId)
    {
        await connection.ExecuteAsync("DELETE FROM payroll_reconciliation_results WHERE PayRunId=@PayRunId", new { PayRunId = payRunId }, transaction);
        var employees = (await connection.QueryAsync<PayRunEmployee>("SELECT * FROM payrunemployees WHERE PayRunId=@PayRunId AND IsSkipped=FALSE", new { PayRunId = payRunId }, transaction)).ToList();
        var rows = new List<PayrollReconciliationResult>();
        foreach (var employee in employees)
        {
            var expectedNet = Math.Max(0, employee.GrossPay + employee.OneTimeEarnings - employee.StatutoryDeductions - employee.OneTimeDeductions);
            rows.Add(Recon(payRunId, $"Net salary - {employee.EmployeeCode}", expectedNet, employee.NetPay, new { employee.EmployeeId, employee.EmployeeCode }));
        }
        rows.Add(Recon(payRunId, "Run net total equals employee net totals", employees.Sum(employee => employee.NetPay), await connection.ExecuteScalarAsync<decimal>("SELECT COALESCE(NetPay,0) FROM payruns WHERE Id=@PayRunId", new { PayRunId = payRunId }, transaction), new { count = employees.Count }));
        rows.Add(Recon(payRunId, "Gross earnings equals sum of earnings", employees.Sum(employee => employee.GrossPay + employee.OneTimeEarnings), await connection.ExecuteScalarAsync<decimal>("SELECT COALESCE(PayrollCost,0) FROM payruns WHERE Id=@PayRunId", new { PayRunId = payRunId }, transaction), new { count = employees.Count }));
        var totalWorkingDays = await connection.ExecuteScalarAsync<int>("SELECT TotalWorkingDays FROM payruns WHERE Id=@PayRunId", new { PayRunId = payRunId }, transaction);
        var employeePeriods = (await connection.QueryAsync<(int EmployeeId, int Days)>(@"SELECT EmployeeId,
MAX(CAST(JSON_UNQUOTE(JSON_EXTRACT(InputJson,'$.TotalWorkingDays')) AS UNSIGNED)) Days
FROM payroll_calculation_traces WHERE PayRunId=@PayRunId GROUP BY EmployeeId", new { PayRunId = payRunId }, transaction)).ToDictionary(row => row.EmployeeId, row => row.Days);
        rows.Add(Recon(payRunId, "Attendance payable days within period", 0, employees.Count(employee => employee.PayableDays < 0 || employee.PayableDays > employeePeriods.GetValueOrDefault(employee.EmployeeId, totalWorkingDays)), new { count = employees.Count, totalWorkingDays }));
        var debit = employees.Sum(employee => employee.GrossPay + employee.OneTimeEarnings);
        var credit = employees.Sum(employee => employee.NetPay + employee.StatutoryDeductions + employee.OneTimeDeductions);
        rows.Add(Recon(payRunId, "GL debit equals GL credit", debit, credit, new { debit, credit }));
        await AddStatutoryReconciliationsAsync(connection, transaction, payRunId, rows);
        await connection.ExecuteAsync(@"INSERT INTO payroll_reconciliation_results (PayRunId,CheckName,ExpectedAmount,ActualAmount,DifferenceAmount,Status,DetailsJson)
VALUES (@PayRunId,@CheckName,@ExpectedAmount,@ActualAmount,@DifferenceAmount,@Status,@DetailsJson);", rows, transaction);
    }

    private static async Task AddStatutoryReconciliationsAsync(MySqlConnection connection, MySqlTransaction transaction, int payRunId, List<PayrollReconciliationResult> rows)
    {
        var lineRows = (await connection.QueryAsync<StatutoryLineTotal>(@"
SELECT l.ComponentCode, COALESCE(l.StatutoryType,'None') StatutoryType, COALESCE(SUM(l.Amount),0) Amount
FROM payrunemployeelines l JOIN payrunemployees p ON p.Id=l.PayRunEmployeeId
WHERE l.PayRunId=@PayRunId AND p.IsSkipped=FALSE
GROUP BY l.ComponentCode, l.StatutoryType;", new { PayRunId = payRunId }, transaction)).ToList();
        decimal SumBy(string[] codes, string[] types) => lineRows.Where(row => codes.Contains(row.ComponentCode, StringComparer.OrdinalIgnoreCase) || types.Contains(row.StatutoryType, StringComparer.OrdinalIgnoreCase)).Sum(row => row.Amount);

        var snapshots = await connection.QueryAsync<string>("SELECT CAST(DetailsJson AS CHAR) FROM payrunemployees WHERE PayRunId=@PayRunId AND IsSkipped=FALSE", new { PayRunId = payRunId }, transaction);
        var expected = new List<StatutoryLineTotal>();
        foreach (var json in snapshots)
        {
            using var document = JsonDocument.Parse(json);
            expected.AddRange(document.RootElement.EnumerateArray().Select(line => new StatutoryLineTotal(Text(line, "Id"), Text(line, "StatutoryType", "None"), NumberFrom(Text(line, "amount")))));
        }
        void Check(string name, string[] codes, string[] types)
        {
            var total = expected.Where(row => codes.Contains(row.ComponentCode, StringComparer.OrdinalIgnoreCase) || types.Contains(row.StatutoryType, StringComparer.OrdinalIgnoreCase)).Sum(row => row.Amount);
            rows.Add(Recon(payRunId, name + " snapshot equals stored lines", total, SumBy(codes, types), new { source = "Frozen calculation DetailsJson versus payrunemployeelines; not statutory eligibility certification" }));
        }
        Check("PF employee", ["PF", "EPF"], ["PF Employee"]);
        Check("PF employer", ["EPF_ER", "PF_ER", "PF_EMPLOYER"], ["PF Employer"]);
        Check("EPS", ["EPS"], ["EPS"]);
        Check("VPF", ["VPF"], ["VPF"]);
        Check("ESI employee", ["ESIC", "ESI"], ["ESI Employee"]);
        Check("ESI employer", ["ESIC_ER", "ESI_ER"], ["ESI Employer"]);
        Check("PT/LWF/WC", ["PT_LWF_WC", "PT", "LWF", "WC"], ["Professional Tax", "LWF Employee", "LWF Employer", "Workmen Compensation"]);
        Check("TDS", ["TDS"], ["TDS"]);
        var taxRows = await connection.QueryAsync<(int EmployeeId, decimal Expected, decimal Actual)>(@"
SELECT t.employee_id, t.monthly_tds, COALESCE(SUM(CASE WHEN l.StatutoryType='TDS' OR l.ComponentCode='TDS' THEN l.Amount ELSE 0 END),0)
FROM tax_computation_snapshots t
JOIN (SELECT employee_id,MAX(id) id FROM tax_computation_snapshots WHERE pay_run_id=@PayRunId GROUP BY employee_id) latest ON latest.id=t.id
JOIN payrunemployees p ON p.PayRunId=t.pay_run_id AND p.EmployeeId=t.employee_id AND p.IsSkipped=FALSE
LEFT JOIN payrunemployeelines l ON l.PayRunEmployeeId=p.Id
GROUP BY t.id,t.employee_id,t.monthly_tds", new { PayRunId = payRunId }, transaction);
        foreach (var row in taxRows) rows.Add(Recon(payRunId, $"TDS projection - employee {row.EmployeeId}", row.Expected, row.Actual, new { row.EmployeeId }));
    }

    private static async Task EnsureReconciledAsync(MySqlConnection connection, MySqlTransaction transaction, int payRunId)
    {
        await WriteReconciliationResultsAsync(connection, transaction, payRunId);
        var failed = (await connection.QueryAsync<string>("SELECT CheckName FROM payroll_reconciliation_results WHERE PayRunId=@PayRunId AND Status='Failed'", new { PayRunId = payRunId }, transaction)).ToList();
        if (failed.Count > 0) throw new InvalidOperationException("Payroll reconciliation failed: " + string.Join("; ", failed.Take(5)) + ". Review diagnostics before approval.");
    }

    private static PayrollReconciliationResult Recon(int payRunId, string name, decimal expected, decimal actual, object details)
    {
        var difference = decimal.Round(actual - expected, 2);
        return new PayrollReconciliationResult
        {
            PayRunId = payRunId,
            CheckName = name,
            ExpectedAmount = decimal.Round(expected, 2),
            ActualAmount = decimal.Round(actual, 2),
            DifferenceAmount = difference,
            Status = Math.Abs(difference) <= 0.01m ? "Passed" : "Failed",
            DetailsJson = JsonSerializer.Serialize(details)
        };
    }

    private static Task RefreshTotalsAsync(MySqlConnection connection, MySqlTransaction transaction, int payRunId) =>
        connection.ExecuteAsync(@"
UPDATE payruns r
JOIN (SELECT PayRunId, COALESCE(SUM(GrossPay + OneTimeEarnings), 0) AS PayrollCost, COALESCE(SUM(NetPay), 0) AS NetPay FROM payrunemployees WHERE PayRunId = @PayRunId GROUP BY PayRunId) e ON e.PayRunId = r.Id
SET r.PayrollCost = e.PayrollCost, r.NetPay = e.NetPay
WHERE r.Id = @PayRunId;", new { PayRunId = payRunId }, transaction);

    private static async Task ApplyPreviousRunComparisonAsync(MySqlConnection connection, PayRun payRun)
    {
        var previousRunIds = (await connection.QueryAsync<int>(@"
SELECT Id FROM payruns
WHERE ClientId = @ClientId AND PayPeriod < @PayPeriod
  AND Status IN ('Draft', 'Pending Approval', 'Approved', 'Partially Paid', 'Paid')
ORDER BY PayPeriod DESC
LIMIT 2;", new { payRun.ClientId, payRun.PayPeriod })).ToList();
        if (previousRunIds.Count == 0)
        {
            foreach (var employee in payRun.Employees)
                employee.VarianceReason = employee.IsSkipped ? "Excluded from current run" : "First payroll comparison period";
            return;
        }

        var previousRows = (await connection.QueryAsync<PayRunEmployee>("SELECT * FROM payrunemployees WHERE PayRunId IN @PreviousRunIds", new { PreviousRunIds = previousRunIds })).ToList();
        var latestPreviousByEmployee = previousRows.Where(row => row.PayRunId == previousRunIds[0]).ToDictionary(employee => employee.EmployeeId);
        var secondPreviousByEmployee = previousRunIds.Count > 1
            ? previousRows.Where(row => row.PayRunId == previousRunIds[1]).ToDictionary(employee => employee.EmployeeId)
            : [];
        foreach (var employee in payRun.Employees)
        {
            if (!latestPreviousByEmployee.TryGetValue(employee.EmployeeId, out var previous))
            {
                employee.VarianceReason = employee.IsSkipped ? "Excluded from current run" : "New in current run";
                continue;
            }

            secondPreviousByEmployee.TryGetValue(employee.EmployeeId, out var secondPrevious);
            var previousValues = new[] { previous.NetPay, secondPrevious?.NetPay }.Where(value => value is not null).Select(value => value!.Value).ToList();
            var average = previousValues.Count == 0 ? (decimal?)null : decimal.Round(previousValues.Average(), 2);
            employee.PreviousNetPay = previous.NetPay;
            employee.PreviousSecondNetPay = secondPrevious?.NetPay;
            employee.PreviousTwoMonthAverageNetPay = average;
            employee.NetPayVariance = employee.NetPay - previous.NetPay;
            employee.TwoMonthAverageVariance = average is null ? null : employee.NetPay - average.Value;
            employee.VariancePercent = previous.NetPay == 0 ? null : decimal.Round((employee.NetPay - previous.NetPay) / previous.NetPay * 100, 2);
            employee.TwoMonthAverageVariancePercent = average is null or 0 ? null : decimal.Round((employee.NetPay - average.Value) / average.Value * 100, 2);
            employee.VarianceReason = employee.IsSkipped ? "Excluded from current run" :
                employee.NetPay == previous.NetPay ? "No change" :
                average is not null && Math.Abs(employee.NetPay - average.Value) > average.Value * 0.1m ? "Material variance against 2-month average" :
                Math.Abs(employee.NetPay - previous.NetPay) > previous.NetPay * 0.1m ? "Material net pay variance" :
                "Minor payroll movement";
        }
    }

    internal static async Task ApplyLeaveBreakdownAsync(MySqlConnection connection, PayRun payRun)
    {
        if (payRun.RunType == "Off Cycle" || payRun.Employees.Count == 0)
            return;

        var employeeIds = payRun.Employees.Select(employee => employee.EmployeeId).Distinct().ToArray();
        var cycleRows = await connection.QueryAsync<EmployeeCycleRow>(@"
SELECT e.Id AS EmployeeId,
       COALESCE(g.attendance_cycle_start_day, 1) AS StartDay,
       COALESCE(g.attendance_cycle_end_day, DAY(LAST_DAY(STR_TO_DATE(CONCAT(@PayPeriod, '-01'), '%Y-%m-%d')))) AS EndDay
FROM employees e
LEFT JOIN (
    SELECT age.employee_id, MIN(g.id) AS group_id
    FROM attendance_group_employees age
    JOIN attendance_groups g ON g.id=age.attendance_group_id AND g.client_id=@ClientId AND g.is_active=TRUE
    GROUP BY age.employee_id
) employee_group ON employee_group.employee_id=e.Id
LEFT JOIN attendance_groups g ON g.id=employee_group.group_id
WHERE e.ClientId=@ClientId AND e.Id IN @EmployeeIds;", new { payRun.ClientId, payRun.PayPeriod, EmployeeIds = employeeIds });
        var ranges = cycleRows.ToDictionary(row => row.EmployeeId, row => CycleRangeFor(payRun.PayPeriod, (int)row.StartDay, (int)row.EndDay));
        if (ranges.Count == 0) return;
        var minStart = ranges.Values.Min(row => row.Start);
        var maxEnd = ranges.Values.Max(row => row.End);
        var rows = await connection.QueryAsync<LeaveDailyBreakdownRow>(@"
SELECT a.employee_id AS EmployeeId,
       a.attendance_date AS AttendanceDate,
       a.status AS Code,
       COALESCE(lt.name, a.status) AS Name,
       COALESCE(lt.type, '') AS Type,
       a.payable_value AS PayableValue
FROM employee_daily_attendance a
LEFT JOIN leave_types lt ON lt.client_id = a.client_id AND lt.code = a.status
WHERE a.client_id = @ClientId
  AND a.attendance_date BETWEEN @StartDate AND @EndDate
  AND a.employee_id IN @EmployeeIds
  AND a.status <> 'Present';", new { payRun.ClientId, EmployeeIds = employeeIds, StartDate = minStart, EndDate = maxEnd });

        var scopedRows = rows.Where(row => ranges.TryGetValue(row.EmployeeId, out var range) && row.AttendanceDate.Date >= range.Start && row.AttendanceDate.Date <= range.End);
        var byEmployee = scopedRows.GroupBy(row => row.EmployeeId).ToDictionary(group => group.Key, group => group
            .GroupBy(row => new { row.Code, row.Name, row.Type })
            .Select(row => new PayRunLeaveBreakdown { Code = row.Key.Code, Name = row.Key.Name, Type = string.IsNullOrWhiteSpace(row.Key.Type) ? row.Sum(item => item.PayableValue) > 0 ? "Paid" : "Unpaid" : row.Key.Type, Days = row.Count(), PayableDays = row.Sum(item => item.PayableValue) })
            .ToList());

        foreach (var employee in payRun.Employees)
        {
            if (byEmployee.TryGetValue(employee.EmployeeId, out var breakdown))
                employee.LeaveBreakdown = breakdown;
        }
    }

    private static AttendanceCycleRange CycleRangeFor(string payPeriod, int startDay, int endDay)
    {
        var endMonth = DateTime.ParseExact($"{payPeriod}-01", "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var startMonth = startDay > 1 ? endMonth.AddMonths(-1) : endMonth;
        return new AttendanceCycleRange(
            new DateTime(startMonth.Year, startMonth.Month, ClampDay(startMonth, startDay)),
            new DateTime(endMonth.Year, endMonth.Month, ClampDay(endMonth, endDay)));
    }

    private static int ClampDay(DateTime month, int day) =>
        Math.Min(Math.Max(1, day), DateTime.DaysInMonth(month.Year, month.Month));

    private static async Task<List<PayRunAttendance>> GetPayRunAttendanceAsync(MySqlConnection connection, MySqlTransaction transaction, int clientId, string payPeriod, int? employeeId = null)
    {
        var monthlyRows = (await connection.QueryAsync<PayRunAttendance>(@"SELECT employee_id AS EmployeeId, working_days AS WorkingDays, present_days AS PresentDays, payable_days AS PayableDays, source_type AS SourceType,COALESCE(remarks,'') AS Remarks, 'Monthly snapshot' AS InputSource
FROM employee_monthly_attendance WHERE client_id=@ClientId AND attendance_month=@Month AND (@EmployeeId IS NULL OR employee_id=@EmployeeId)", new { ClientId = clientId, Month = payPeriod, EmployeeId = employeeId }, transaction)).ToList();
        foreach(var monthly in monthlyRows)ReadPayableOnlyAttendance(monthly,clientId,payPeriod);
        foreach (var monthly in monthlyRows) await ValidateSourcePayableOverrideAsync(connection, transaction, clientId, payPeriod, monthly);
        // Preserve the regular engine's client-level monthly/daily source choice when previewing one employee.
        var hasMonthly = monthlyRows.Count > 0 || employeeId.HasValue && await connection.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM employee_monthly_attendance WHERE client_id=@ClientId AND attendance_month=@Month)", new { ClientId = clientId, Month = payPeriod }, transaction);
        var periodStart = PayPeriodStart(payPeriod);
        var periodEnd = periodStart.AddMonths(1).AddDays(-1);
        var woVersions = (await AttendanceIntegrationRepository.ReadAsync(connection, transaction, clientId)).WeeklyOffRuleVersions;
        var applicable = woVersions.Any(v => v.EffectiveFrom.Date <= periodEnd.Date);
        if (hasMonthly && !applicable) return monthlyRows;
        List<PayRunAttendance> legacyDaily = hasMonthly ? [] : (await connection.QueryAsync<PayRunAttendance>(@"
SELECT employee_id AS EmployeeId,
       COUNT(*) AS WorkingDays,
       COALESCE(SUM(CASE WHEN status='Present' THEN payable_value ELSE 0 END), 0) AS PresentDays,
       COALESCE(SUM(CASE WHEN status IN ('WO','H') THEN 1 ELSE payable_value END), 0) AS PayableDays,
       'Legacy daily attendance' AS InputSource,
       COALESCE(SUM(CASE WHEN status='WO' THEN 1 ELSE 0 END),0) AS WeeklyOffPayableUnits,
       COALESCE(SUM(CASE WHEN status='H' THEN 1 ELSE 0 END),0) AS HolidayPayableUnits
FROM employee_daily_attendance
WHERE client_id=@ClientId AND DATE_FORMAT(attendance_date, '%Y-%m')=@Month AND (@EmployeeId IS NULL OR employee_id=@EmployeeId)
GROUP BY employee_id;", new { ClientId = clientId, Month = payPeriod, EmployeeId = employeeId }, transaction)).ToList();
        if (!applicable) return legacyDaily;

        var cycles = (await connection.QueryAsync<EmployeeCycleRow>(@"
SELECT e.Id AS EmployeeId, COALESCE(g.attendance_cycle_start_day,1) AS StartDay,
COALESCE(g.attendance_cycle_end_day,DAY(LAST_DAY(STR_TO_DATE(CONCAT(@PayPeriod,'-01'),'%Y-%m-%d')))) AS EndDay
FROM employees e
LEFT JOIN (SELECT membership.employee_id,MIN(g.id) AS group_id FROM attendance_group_employees membership
JOIN attendance_groups g ON g.id=membership.attendance_group_id AND g.client_id=@ClientId AND g.is_active=TRUE GROUP BY membership.employee_id) current_group ON current_group.employee_id=e.Id
LEFT JOIN attendance_groups g ON g.id=current_group.group_id
WHERE e.ClientId=@ClientId AND " + EmployeeEmploymentRules.EligibleInMonth("e", "@PayPeriod") + " AND (@EmployeeId IS NULL OR e.Id=@EmployeeId)", new { ClientId = clientId, PayPeriod = payPeriod, EmployeeId = employeeId }, transaction)).ToList();
        if (cycles.Count == 0) return monthlyRows.Count > 0 ? monthlyRows : legacyDaily;
        var ranges = cycles.ToDictionary(c => c.EmployeeId, c => CycleRangeFor(payPeriod, (int)c.StartDay, (int)c.EndDay));
        var resolved = await WeeklyOffAttendanceService.ResolveAsync(connection, transaction, clientId, cycles.Select(c => c.EmployeeId).ToArray(), ranges.Values.Min(r => r.Start), ranges.Values.Max(r => r.End));
        var monthlyById = monthlyRows.ToDictionary(r => r.EmployeeId);
        var dailyById = legacyDaily.ToDictionary(r => r.EmployeeId);
        var result = new List<PayRunAttendance>();
        foreach (var cycle in cycles)
        {
            var range = ranges[cycle.EmployeeId];
            var saved = monthlyById.GetValueOrDefault(cycle.EmployeeId);
            // Explicit source authority replaces salary payable only for its bound month.
            // Do not add weekly-off/leave credits again or rewrite the dated presence ledger.
            if (saved?.SourceType == SourcePayableOverride.SourceType) { result.Add(saved); continue; }
            if(saved is {PresentDays:null} && woVersions.Any(v=>v.EffectiveFrom.Date<=range.End.Date))
            {
                saved.PolicyError="A published weekly-off policy requires recorded dated attendance. Reviewed payable-only source totals do not establish actual presence or weekly-off eligibility.";
                result.Add(saved);continue;
            }
            if (!woVersions.Any(v => v.EffectiveFrom.Date <= range.End.Date))
            {
                var legacy = hasMonthly ? saved : dailyById.GetValueOrDefault(cycle.EmployeeId);
                if (legacy is not null) result.Add(legacy);
                continue;
            }
            var days = resolved.Days.Where(d => d.EmployeeId == cycle.EmployeeId && d.Date >= range.Start && d.Date <= range.End).ToArray();
            var row = saved ?? new PayRunAttendance { EmployeeId = cycle.EmployeeId, WorkingDays = days.Length, PresentDays = days.Where(d => d.Status == "Present").Sum(d => d.PayableValue), PayableDays = days.Sum(d => d.PayableValue) };
            row.InputSource = saved is null ? "Dated daily attendance" : "Monthly snapshot validated against dated attendance";
            row.WeeklyOffPayableUnits = days.Where(d => d.Status == "WO").Sum(d => d.PayableValue);
            row.HolidayPayableUnits = days.Where(d => d.Status == "H").Sum(d => d.PayableValue);
            row.PolicyError = AttendancePolicyError(range.Start, range.End, days, saved?.PresentDays, saved?.PayableDays);
            row.PolicySnapshot = new(1, DateOnly.FromDateTime(range.Start), DateOnly.FromDateTime(range.End), saved is null ? "Date-wise" : "Monthly validated against dated attendance",
                woVersions.Where(v => v.EffectiveFrom.Date <= range.End.Date).ToArray(),
                days.Select(d => new PayrollAttendanceDaySnapshot(DateOnly.FromDateTime(d.Date), d.Status, d.PayableValue, d.RuleVersionId, d.RuleVersionNumber, d.Reason, d.WorkWeek, d.WorkWeekConfig,
                    d.PreviousWorkingDate is DateTime before ? DateOnly.FromDateTime(before) : null, d.NextWorkingDate is DateTime after ? DateOnly.FromDateTime(after) : null)).ToArray());
            result.Add(row);
        }
        return result;
    }

    internal static string AttendancePolicyError(DateTime start, DateTime end, IReadOnlyList<WeeklyOffResolvedDay> days, decimal? savedPresentDays, decimal? savedPayableDays)
    {
        if (days.Any(d => d.Pending)) return "Weekly-off attendance is unresolved; complete the adjacent attendance before payroll.";
        var expected = (end.Date - start.Date).Days + 1;
        if (days.Count != expected || days.Select(d => d.Date.Date).Distinct().Count() != expected)
            return "The published weekly-off policy needs recorded attendance for every day in the payroll cycle; monthly totals cannot identify weekly-off eligibility.";
        if (savedPayableDays.HasValue && days.Sum(d => d.PayableValue) != savedPayableDays.Value || savedPresentDays.HasValue && days.Where(d => d.Status == "Present").Sum(d => d.PayableValue) != savedPresentDays.Value)
            return "Stored monthly attendance does not match the effective weekly-off rules. Review and save the dated attendance before payroll; publishing a version does not rewrite attendance.";
        return "";
    }

    private static int AttendanceWorkingDays(PayRunAttendance? row, int fallback) =>
        Math.Max(1, (int)Math.Ceiling(row is not null && row.WorkingDays > 0 ? row.WorkingDays : fallback));

    private static async Task<List<PayrollAdjustment>> GetApplicableAdjustmentsAsync(MySqlConnection connection, MySqlTransaction transaction, int clientId, string payPeriod, string runType, int[] adjustmentIds, int? employeeId = null)
    {
        var sql = adjustmentIds.Length > 0
            ? @"SELECT * FROM payrolladjustments WHERE ClientId=@ClientId AND PayPeriod=@PayPeriod AND Status='Approved' AND Id IN @AdjustmentIds"
            : @"SELECT * FROM payrolladjustments WHERE ClientId=@ClientId AND PayPeriod=@PayPeriod AND Status='Approved' AND PayRunType=@RunType";
        sql += " AND (@EmployeeId IS NULL OR EmployeeId=@EmployeeId)";
        var rows = await connection.QueryAsync<PayrollAdjustment>(sql, new { ClientId = clientId, PayPeriod = payPeriod, RunType = runType, AdjustmentIds = adjustmentIds, EmployeeId = employeeId }, transaction);
        return rows.ToList();
    }

    private static bool IsDeduction(PayrollAdjustment adjustment) =>
        adjustment.AdjustmentType.Equals("Deduction", StringComparison.OrdinalIgnoreCase) || adjustment.AdjustmentType.Equals("Recovery", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeAdjustmentType(string value) =>
        value.Equals("Deduction", StringComparison.OrdinalIgnoreCase) || value.Equals("Recovery", StringComparison.OrdinalIgnoreCase) ? "Deduction" :
        value.Equals("Reimbursement", StringComparison.OrdinalIgnoreCase) ? "Reimbursement" : "Earning";

    private static string RegularRunCode(CreatePayRunRequest request)
    {
        var employeeScope = request.IncludedEmployeeIds.Count > 0
            ? $"-E-{StableRunHash(string.Join(",", request.IncludedEmployeeIds.Distinct().Order()))}"
            : "";
        if (!string.IsNullOrWhiteSpace(request.AttendancePolicyBatchId))
            return $"REG-B-{StableRunHash(request.AttendancePolicyBatchId.Trim())}{employeeScope}";
        return $"{LegacyRegularRunCode(request)}{employeeScope}";
    }

    private static string LegacyRegularRunCode(CreatePayRunRequest request)
    {
        var ids = (request.AttendanceGroupIds.Count > 0 ? request.AttendanceGroupIds : request.AttendanceGroupId > 0 ? [request.AttendanceGroupId] : [])
            .Where(id => id > 0)
            .Distinct()
            .Order()
            .ToArray();
        if (ids.Length == 0) return "REGULAR";
        if (ids.Length == 1) return $"REG-G{ids[0]}";
        return $"REG-M-{StableRunHash(string.Join(",", ids))}";
    }

    private static async Task<ExistingPayRunRow?> FindExistingRegularRunAsync(MySqlConnection connection, MySqlTransaction transaction, CreatePayRunRequest request, string runCode)
    {
        var runCodes = new[] { runCode }
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return await connection.QueryFirstOrDefaultAsync<ExistingPayRunRow>(@"
SELECT Id, Status
FROM payruns
WHERE PayPeriod=@PayPeriod
AND ClientId=@ClientId
AND RunCode IN @RunCodes
ORDER BY
    CASE
        WHEN RunCode=@RunCode THEN 0
        ELSE 2
    END,
    Id DESC
LIMIT 1;", new { request.PayPeriod, request.ClientId, RunCode = runCode, RunCodes = runCodes, AttendancePolicyBatchId = request.AttendancePolicyBatchId }, transaction);
    }

    private static async Task<ExistingPayRunRow?> FindRunningRegularRunAsync(MySqlConnection connection, MySqlTransaction transaction, CreatePayRunRequest request)
    {
        return await connection.QueryFirstOrDefaultAsync<ExistingPayRunRow>(@"
SELECT Id, Status
FROM payruns
WHERE PayPeriod=@PayPeriod
AND ClientId=@ClientId
AND RunType='Regular'
AND Status IN ('AwaitingProcessing','Queued','Processing')
ORDER BY Id DESC
LIMIT 1;", new { request.PayPeriod, request.ClientId }, transaction);
    }

    private static async Task<HashSet<int>> NormalizeRegularEmployeeScopeAsync(MySqlConnection connection, MySqlTransaction transaction, CreatePayRunRequest request, List<PayRunSourceEmployee> employees, int currentPayRunId = 0)
    {
        var requestedIncludedIds = request.IncludedEmployeeIds.Distinct().ToHashSet();
        var activeEmployeeIds = employees.Select(employee => employee.Id).ToHashSet();
        var includedEmployeeIds = requestedIncludedIds.Count > 0
            ? activeEmployeeIds.Where(requestedIncludedIds.Contains).ToHashSet()
            : activeEmployeeIds.Except(request.ExcludedEmployeeIds).ToHashSet();

        if (includedEmployeeIds.Count == 0)
        {
            request.IncludedEmployeeIds = [];
            request.ExcludedEmployeeIds = [];
            return [];
        }

        var alreadyProcessedIds = (await connection.QueryAsync<int>(@"
SELECT DISTINCT pe.EmployeeId
FROM payrunemployees pe
JOIN payruns r ON r.Id=pe.PayRunId
WHERE r.ClientId=@ClientId
AND r.PayPeriod=@PayPeriod
AND r.RunType='Regular'
AND r.Id<>@CurrentPayRunId
AND r.Status IN ('Draft','Pending Approval','Approved','Partially Paid','Paid')
AND pe.IsSkipped=FALSE
AND pe.EmployeeId IN @EmployeeIds;", new { request.ClientId, request.PayPeriod, CurrentPayRunId = currentPayRunId, EmployeeIds = includedEmployeeIds.ToArray() }, transaction)).ToHashSet();

        includedEmployeeIds.ExceptWith(alreadyProcessedIds);
        request.IncludedEmployeeIds = includedEmployeeIds.Order().ToList();
        request.ExcludedEmployeeIds = [];
        return includedEmployeeIds;
    }

    private static async Task<string> ResolveAttendancePolicyBatchIdAsync(MySqlConnection connection, MySqlTransaction transaction, CreatePayRunRequest request)
    {
        var batchId = request.AttendancePolicyBatchId?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(batchId)) return batchId;
        var groupIds = (request.AttendanceGroupIds.Count > 0 ? request.AttendanceGroupIds : request.AttendanceGroupId > 0 ? [request.AttendanceGroupId] : [])
            .Where(id => id > 0)
            .Distinct()
            .ToArray();
        if (groupIds.Length == 0) return string.Empty;
        return await connection.ExecuteScalarAsync<string?>(@"SELECT COALESCE(policy_batch_id, '')
FROM attendance_groups
WHERE client_id=@ClientId AND id IN @GroupIds AND COALESCE(policy_batch_id, '')<>''
ORDER BY id
LIMIT 1;", new { request.ClientId, GroupIds = groupIds }, transaction) ?? string.Empty;
    }

    private static string StableRunHash(string value)
    {
        uint hash = 2166136261;
        foreach (var ch in value)
        {
            hash ^= ch;
            hash *= 16777619;
        }
        return hash.ToString("X8");
    }

    internal sealed class PayRunAttendance
    {
        public string InputSource { get; set; } = "Unclassified native attendance";
        public decimal? WeeklyOffPayableUnits { get; set; }
        public decimal? HolidayPayableUnits { get; set; }
        public string PolicyError { get; set; } = "";
        public PayrollAttendanceSnapshot? PolicySnapshot { get; set; }

        public int EmployeeId { get; set; }
        public decimal WorkingDays { get; set; }
        public decimal? PresentDays { get; set; }
        public string SourceType { get; set; } = "";
        public string Remarks { get; set; } = "";
        public SourcePayableAttendanceEvidence? PayableOnlyEvidence { get; set; }
        public decimal PayableDays { get; set; }
    }
    private sealed record EmployeeCycleRow(int EmployeeId, long StartDay, long EndDay);
    private sealed record AttendanceCycleRange(DateTime Start, DateTime End);
    private sealed record LeaveDailyBreakdownRow(int EmployeeId, DateTime AttendanceDate, string Code, string Name, string Type, decimal PayableValue);
    private sealed record StatutoryLineTotal(string ComponentCode, string StatutoryType, decimal Amount);
    private sealed record ExistingPayRunRow(int Id, string Status);
    private sealed record TaxProjectionSetting(bool Enabled, bool ProjectMonthlyTds, string TaxDeductionComponentCode);
    private sealed record TdsProjectionResult(decimal Amount, string ComponentCode, bool Calculated = false);
    private sealed record EmployeeSalaryTableRow(int EmployeeId, string ComponentId, decimal Amount);
    private sealed record EmployeeBasicPayInfotypeRow(int EmployeeId, string SalaryStructureId, decimal AnnualCtc, string SalaryJson);
    private sealed record EmployeePersonalPayrollRow(int EmployeeId, string State, string PanNumber, string UanNumber, string EsicNumber, decimal EsicEmployee, decimal PtLwfWorkmenComp, decimal Tds, decimal Recovery);
    private sealed record EmployeePaymentPayrollRow(int EmployeeId, string BankAccountNo, string IfscCode);
    private sealed class PayRunSourceEmployee : Employee
    {
        [JsonIgnore] public SourcePayableAttendanceEvidence? PayableOnlyAttendance { get; set; }
        [JsonIgnore] public decimal? SimulationAnnualCtc { get; set; }
        [JsonIgnore] public PfPolicyPeriod? PfPolicyPeriod { get; set; }
        [JsonIgnore] public PayrollAttendanceSnapshot? AttendancePolicySnapshot { get; set; }
        [JsonIgnore] public PayrollLocationPartPeriod? LocationParts { get; set; }

        public string ClientName { get; set; } = string.Empty;
        public string WorkState { get; set; } = string.Empty;
        public string PersonalState { get; set; } = string.Empty;
        public bool HasPersonalDetails { get; set; }
        public decimal EsicEmployee { get; set; }
        public decimal PtLwfWorkmenComp { get; set; }
        public decimal Tds { get; set; }
        public decimal Recovery { get; set; }
        public string PanNumber { get; set; } = string.Empty;
        public string UanNumber { get; set; } = string.Empty;
        public string EsicNumber { get; set; } = string.Empty;
        public string BankAccountNo { get; set; } = string.Empty;
        public string IfscCode { get; set; } = string.Empty;
    }

    internal static bool HasPfPolicySnapshot(string? detailsJson)
    {
        if (string.IsNullOrWhiteSpace(detailsJson)) return false;
        using var document = JsonDocument.Parse(detailsJson);
        return document.RootElement.ValueKind == JsonValueKind.Array && document.RootElement.EnumerateArray().Any(line => line.TryGetProperty("pfPolicy", out var snapshot) && snapshot.ValueKind == JsonValueKind.Object);
    }

    internal static bool HasAttendancePolicySnapshot(string? detailsJson)
    {
        if (string.IsNullOrWhiteSpace(detailsJson)) return false;
        using var document = JsonDocument.Parse(detailsJson);
        return document.RootElement.ValueKind == JsonValueKind.Array && document.RootElement.EnumerateArray().Any(line => line.TryGetProperty("attendancePolicy", out var snapshot) && snapshot.ValueKind == JsonValueKind.Object);
    }

    internal static bool IsProtectedPolicyRun(string status, IEnumerable<string> details) =>
        !LatestAttemptStatuses.Contains(status) && details.Any(d => HasPfPolicySnapshot(d) || HasAttendancePolicySnapshot(d));

    private static async Task LoadPfPolicyPeriodsAsync(MySqlConnection connection, MySqlTransaction transaction, List<PayRunSourceEmployee> employees, string payPeriod, string setupJson)
    {
        if (employees.Count == 0) return;
        var clientId = employees[0].ClientId;
        var allVersions = await PfPolicyRepository.ReadVersionsAsync(connection, transaction, clientId);
        if (!allVersions.Any(v => v.Status == "Published")) return;
        var setup = ReadPayrollSetup(setupJson);
        var employeeIds = employees.Select(e => e.Id).ToArray();
        var cycles = (await connection.QueryAsync<EmployeeCycleRow>(@"
SELECT e.Id AS EmployeeId, COALESCE(g.attendance_cycle_start_day,1) AS StartDay,
COALESCE(g.attendance_cycle_end_day,DAY(LAST_DAY(STR_TO_DATE(CONCAT(@PayPeriod,'-01'),'%Y-%m-%d')))) AS EndDay
FROM employees e
LEFT JOIN (SELECT membership.employee_id,MIN(g.id) AS group_id FROM attendance_group_employees membership
JOIN attendance_groups g ON g.id=membership.attendance_group_id AND g.client_id=@ClientId AND g.is_active=TRUE GROUP BY membership.employee_id) current_group ON current_group.employee_id=e.Id
LEFT JOIN attendance_groups g ON g.id=current_group.group_id
WHERE e.ClientId=@ClientId AND e.Id IN @EmployeeIds", new { ClientId = clientId, PayPeriod = payPeriod, EmployeeIds = employeeIds }, transaction)).ToDictionary(c => c.EmployeeId);
        foreach (var employee in employees)
        {
            if(employee.LocationParts is { } allocation)
            {
                var allocationStructures=allocation.Parts.Select(part=>part.SalaryStructureId).ToHashSet(StringComparer.Ordinal);
                if(allVersions.Any(version=>version.Status=="Published"&&allocationStructures.Contains(version.SalaryStructureId)
                    &&version.EffectiveFrom<=DateOnly.FromDateTime(PayPeriodStart(payPeriod).AddMonths(1).AddDays(-1))))
                    throw new InvalidOperationException("A location salary template has a published PF policy. Review its combined monthly allocation policy before payroll; source-part formulas cannot override that policy.");
                continue; // An unrelated legacy primary template's PF policy is not this period's reviewed allocation template.
            }
            var structure = setup.Structures.FirstOrDefault(s => s.Active && s.Id == employee.SalaryStructureId)
                ?? setup.Structures.FirstOrDefault(s => s.Active && s.ClientId.Split(':')[0] == clientId.ToString(CultureInfo.InvariantCulture));
            if (structure is null) continue;
            var cycle = cycles.GetValueOrDefault(employee.Id);
            var range = CycleRangeFor(payPeriod, cycle is null ? 1 : (int)cycle.StartDay, cycle is null ? DateTime.DaysInMonth(PayPeriodStart(payPeriod).Year, PayPeriodStart(payPeriod).Month) : (int)cycle.EndDay);
            var start = DateOnly.FromDateTime(range.Start); var end = DateOnly.FromDateTime(range.End);
            var versions = PfPolicyCalculator.ApplicableVersions(allVersions, structure.Id, start, end);
            if (versions.Count == 0) continue;
            employee.PfPolicyPeriod = new(start, end, versions, []);
        }
        var splitEmployees = employees.Where(e => e.PfPolicyPeriod?.Versions.Count > 1).ToList();
        if (splitEmployees.Count == 0) return;
        if(splitEmployees.Any(employee=>employee.PayableOnlyAttendance is not null))
            throw new InvalidOperationException("Date-split PF requires dated attendance. Payable-only source totals with unknown actual presence cannot establish the split.");
        var first = splitEmployees.Min(e => e.PfPolicyPeriod!.Start).ToDateTime(TimeOnly.MinValue);
        var last = splitEmployees.Max(e => e.PfPolicyPeriod!.End).ToDateTime(TimeOnly.MinValue);
        var resolved = await WeeklyOffAttendanceService.ResolveAsync(connection, transaction, clientId, splitEmployees.Select(e => e.Id).ToArray(), first, last);
        foreach (var employee in splitEmployees)
        {
            var period = employee.PfPolicyPeriod!;
            var days = resolved.Days.Where(d => d.EmployeeId == employee.Id && DateOnly.FromDateTime(d.Date) >= period.Start && DateOnly.FromDateTime(d.Date) <= period.End).ToArray();
            if (days.Any(d => d.Pending)) throw new InvalidOperationException($"Date-split PF for {employee.EmployeeCode} has unresolved weekly-off attendance. Resolve the neighboring attendance before payroll.");
            employee.PfPolicyPeriod = period with { Days = days.Select(d => new PfPolicyAttendanceDay(DateOnly.FromDateTime(d.Date), d.PayableValue, d.Status, d.RuleVersionId)).ToArray() };
        }
    }

    private static async Task LoadEmployeeTablesAsync(MySqlConnection connection, MySqlTransaction transaction, List<PayRunSourceEmployee> employees, string payPeriod)
    {
        if (employees.Count == 0) return;
        var employeeIds = employees.Select(employee => employee.Id).ToArray();
        var salaryRows = (await connection.QueryAsync<EmployeeSalaryTableRow>("SELECT EmployeeId,ComponentId,Amount FROM employeesalarycomponents WHERE EmployeeId IN @EmployeeIds", new { EmployeeIds = employeeIds }, transaction)).GroupBy(row => row.EmployeeId).ToDictionary(group => group.Key, group => group.ToDictionary(row => row.ComponentId, row => row.Amount, StringComparer.OrdinalIgnoreCase));
        var periodStart = PayPeriodStart(payPeriod);
        var periodEnd = periodStart.AddMonths(1).AddDays(-1);
        var basicPayRows = (await connection.QueryAsync<EmployeeBasicPayInfotypeRow>(@"
SELECT EmployeeId, SalaryStructureId, AnnualCtc, CAST(SalaryJson AS CHAR) SalaryJson
FROM employee_it0008_basic_pay
WHERE EmployeeId IN @EmployeeIds
  AND EffectiveFrom <= @PeriodEnd
  AND (EffectiveTo IS NULL OR EffectiveTo >= @PeriodStart)
ORDER BY EmployeeId, EffectiveFrom DESC, Id DESC", new { EmployeeIds = employeeIds, PeriodStart = periodStart, PeriodEnd = periodEnd }, transaction))
            .GroupBy(row => row.EmployeeId)
            .ToDictionary(group => group.Key, group => group.First());
        var personalRows = (await connection.QueryAsync<EmployeePersonalPayrollRow>(@"
SELECT
    EmployeeId,
    State,
    PanNumber,
    UanNumber,
    EsicNumber,
    CASE WHEN CAST(EsicEmployee AS CHAR) REGEXP '^-?[0-9]+(\\.[0-9]+)?$' THEN CAST(EsicEmployee AS DECIMAL(18,4)) ELSE 0 END EsicEmployee,
    PtLwfWorkmenComp,
    Tds,
    Recovery
FROM employeepersonaldetails
WHERE EmployeeId IN @EmployeeIds", new { EmployeeIds = employeeIds }, transaction)).ToDictionary(row => row.EmployeeId);
        var paymentRows = (await connection.QueryAsync<EmployeePaymentPayrollRow>(@"
SELECT
    base.EmployeeId,
    COALESCE(NULLIF(it9.BankAccountNo,''), base.BankAccountNo, '') BankAccountNo,
    COALESCE(NULLIF(it9.IfscCode,''), base.IfscCode, '') IfscCode
FROM (
    SELECT e.Id EmployeeId, COALESCE(p.BankAccountNo,'') BankAccountNo, COALESCE(p.IfscCode,'') IfscCode
    FROM employees e
    LEFT JOIN employeepaymentdetails p ON p.EmployeeId=e.Id
    WHERE e.Id IN @EmployeeIds
) base
LEFT JOIN (
    SELECT t.EmployeeId,t.BankAccountNo,t.IfscCode
    FROM employee_it0009_bank_details t
    JOIN (
        SELECT EmployeeId, MAX(Id) Id
        FROM employee_it0009_bank_details
        WHERE Status='Active' AND EmployeeId IN @EmployeeIds
        GROUP BY EmployeeId
    ) latest ON latest.Id=t.Id
) it9 ON it9.EmployeeId=base.EmployeeId", new { EmployeeIds = employeeIds }, transaction)).ToDictionary(row => row.EmployeeId);
        foreach (var employee in employees)
        {
            employee.SalaryComponents = salaryRows.GetValueOrDefault(employee.Id) ?? [];
            if (basicPayRows.TryGetValue(employee.Id, out var basicPay))
            {
                employee.SalaryStructureId = basicPay.SalaryStructureId;
                employee.AnnualCtc = basicPay.AnnualCtc;
                employee.SalaryJson = string.IsNullOrWhiteSpace(basicPay.SalaryJson) ? "{}" : basicPay.SalaryJson;
                employee.SalaryComponents = ParseSalaryComponentMap(employee.SalaryJson);
                if (employee.SalaryComponents.Count == 0)
                    employee.SalaryComponents = salaryRows.GetValueOrDefault(employee.Id) ?? [];
            }
            if (personalRows.TryGetValue(employee.Id, out var personal))
            {
                employee.HasPersonalDetails = true;
                employee.PersonalState = personal.State;
                employee.PanNumber = personal.PanNumber;
                employee.UanNumber = personal.UanNumber;
                employee.EsicNumber = personal.EsicNumber;
                employee.EsicEmployee = personal.EsicEmployee;
                employee.PtLwfWorkmenComp = personal.PtLwfWorkmenComp;
                employee.Tds = personal.Tds;
                employee.Recovery = personal.Recovery;
            }
            if (!paymentRows.TryGetValue(employee.Id, out var payment)) continue;
            employee.BankAccountNo = payment.BankAccountNo;
            employee.IfscCode = payment.IfscCode;
        }
        await LoadLocationPartPeriodsAsync(connection, transaction, employees, payPeriod);
    }

    private static DateTime PayPeriodStart(string payPeriod) =>
        DateTime.TryParse($"{payPeriod}-01", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
            ? start.Date
            : new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);

    private static Dictionary<string, decimal> ParseSalaryComponentMap(string salaryJson)
    {
        if (string.IsNullOrWhiteSpace(salaryJson)) return [];
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, decimal>>(salaryJson, new JsonSerializerOptions { NumberHandling = JsonNumberHandling.AllowReadingFromString }) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static async Task DeletePayRunAttemptAsync(MySqlConnection connection, MySqlTransaction transaction, int payRunId)
    {
        var status = await connection.ExecuteScalarAsync<string?>("SELECT Status FROM payruns WHERE Id=@PayRunId FOR UPDATE", new { PayRunId = payRunId }, transaction);
        var storedDetails = await connection.QueryAsync<string>("SELECT CAST(DetailsJson AS CHAR) FROM payrunemployees WHERE PayRunId=@PayRunId", new { PayRunId = payRunId }, transaction);
        if (IsProtectedPolicyRun(status ?? "", storedDetails)) throw new InvalidOperationException("An approved or completed pay run with frozen policy calculations cannot be deleted or replaced. Recall an unpaid approval to Draft before correcting it.");
        await SyncExpenseClaimPayrollStatusAsync(connection, transaction, payRunId, "Pending Payroll");
        await connection.ExecuteAsync("UPDATE payrolladjustments SET Status='Approved', PayRunId=NULL WHERE PayRunId=@PayRunId;DELETE FROM tax_computation_snapshots WHERE pay_run_id=@PayRunId;DELETE FROM payrunemployeelines WHERE PayRunId=@PayRunId;DELETE FROM payrunemployees WHERE PayRunId=@PayRunId;DELETE FROM payrun_step_logs WHERE PayRunId=@PayRunId;DELETE FROM payroll_validation_issues WHERE PayRunId=@PayRunId;DELETE FROM payroll_calculation_traces WHERE PayRunId=@PayRunId;DELETE FROM payroll_reconciliation_results WHERE PayRunId=@PayRunId;DELETE FROM payruns WHERE Id=@PayRunId;", new { PayRunId = payRunId }, transaction);
    }

    private static async Task ClearPayRunDetailsAsync(MySqlConnection connection, MySqlTransaction transaction, int payRunId)
    {
        await SyncExpenseClaimPayrollStatusAsync(connection, transaction, payRunId, "Pending Payroll");
        await connection.ExecuteAsync("UPDATE payrolladjustments SET Status='Approved', PayRunId=NULL WHERE PayRunId=@PayRunId;DELETE FROM payrunemployeelines WHERE PayRunId=@PayRunId;DELETE FROM payrunemployees WHERE PayRunId=@PayRunId;DELETE FROM payrun_step_logs WHERE PayRunId=@PayRunId;DELETE FROM payroll_validation_issues WHERE PayRunId=@PayRunId;DELETE FROM payroll_calculation_traces WHERE PayRunId=@PayRunId;DELETE FROM payroll_reconciliation_results WHERE PayRunId=@PayRunId;UPDATE payruns SET PayrollCost=0, NetPay=0 WHERE Id=@PayRunId;", new { PayRunId = payRunId }, transaction);
    }

    private static async Task SyncExpenseClaimPayrollStatusAsync(MySqlConnection connection, MySqlTransaction transaction, int payRunId, string payrollStatus, IEnumerable<int>? employeeIds = null)
    {
        if (!await TableExistsAsync(connection, transaction, "ess_expense_claims") || !await TableExistsAsync(connection, transaction, "payroll_reimbursement_queue"))
            return;

        var employees = employeeIds?.Distinct().ToArray() ?? [];
        var employeeFilter = employees.Length > 0 ? "AND c.EmployeeId IN @EmployeeIds" : "";
        var queueEmployeeFilter = employees.Length > 0 ? "AND q.EmployeeId IN @EmployeeIds" : "";
        var claimStatus = payrollStatus == "Pending Payroll" ? "Pending Payroll" : payrollStatus;
        int? claimRunId = payrollStatus == "Pending Payroll" ? null : payRunId;
        await connection.ExecuteAsync(@$"
UPDATE ess_expense_claims c
JOIN payrolladjustments a ON a.ClientId=c.ClientId
    AND a.EmployeeId=c.EmployeeId
    AND a.ReasonCode='EXPENSE_CLAIM'
    AND a.PayRunId=@PayRunId
    AND a.Notes LIKE CONCAT('Expense claim ', c.ClaimNumber, ' line %')
SET c.PayrollStatus=@PayrollStatus,
    c.PayrollRunId=@ClaimRunId,
    c.UpdatedAt=CURRENT_TIMESTAMP
WHERE 1=1 {employeeFilter};", new { PayRunId = payRunId, PayrollStatus = claimStatus, ClaimRunId = claimRunId, EmployeeIds = employees }, transaction);

        await connection.ExecuteAsync(@$"
UPDATE payroll_reimbursement_queue q
JOIN payrolladjustments a ON a.ClientId=q.ClientId
    AND a.EmployeeId=q.EmployeeId
    AND a.ReasonCode='EXPENSE_CLAIM'
    AND a.PayRunId=@PayRunId
    AND a.Notes=CONCAT('Expense claim ', (SELECT c.ClaimNumber FROM ess_expense_claims c WHERE c.Id=q.ExpenseClaimId), ' line ', q.ExpenseLineId)
SET q.Status=@PayrollStatus,
    q.PayrollRunId=@ClaimRunId,
    q.UpdatedAt=CURRENT_TIMESTAMP
WHERE 1=1 {queueEmployeeFilter};", new { PayRunId = payRunId, PayrollStatus = claimStatus, ClaimRunId = claimRunId, EmployeeIds = employees }, transaction);
    }

    private static async Task<bool> TableExistsAsync(MySqlConnection connection, MySqlTransaction transaction, string tableName) =>
        await connection.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME=@TableName", new { TableName = tableName }, transaction) > 0;

    private static string Trunc(string? value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value ?? "" : value[..max];

    private static async Task EnsureColumnAsync(MySqlConnection connection, string tableName, string columnName, string definition)
    {
        var exists = await connection.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @TableName AND COLUMN_NAME = @ColumnName", new { TableName = tableName, ColumnName = columnName });
        if (exists == 0) await connection.ExecuteAsync($"ALTER TABLE `{tableName}` ADD COLUMN `{columnName}` {definition}");
    }

    private static async Task EnsureForeignKeyAsync(MySqlConnection connection, string tableName, string constraintName, string definition)
    {
        var exists = await connection.ExecuteScalarAsync<int>(@"
SELECT COUNT(*)
FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS
WHERE CONSTRAINT_SCHEMA = DATABASE()
  AND CONSTRAINT_NAME = @ConstraintName;", new { ConstraintName = constraintName });

        if (exists == 0)
            await connection.ExecuteAsync($"ALTER TABLE `{tableName}` ADD CONSTRAINT `{constraintName}` {definition}");
    }

    private static async Task EnsurePayRunIndexAsync(MySqlConnection connection)
    {
        var oldIndex = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'payruns' AND INDEX_NAME = 'UX_PayRuns_PayPeriod'");
        if (oldIndex > 0) await connection.ExecuteAsync("ALTER TABLE payruns DROP INDEX UX_PayRuns_PayPeriod");
        var clientPeriodIndex = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'payruns' AND INDEX_NAME = 'UX_PayRuns_Client_Period'");
        if (clientPeriodIndex > 0) await connection.ExecuteAsync("ALTER TABLE payruns DROP INDEX UX_PayRuns_Client_Period");
        var runCodeIndex = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'payruns' AND INDEX_NAME = 'UX_PayRuns_Client_Period_Code'");
        if (runCodeIndex == 0) await connection.ExecuteAsync("ALTER TABLE payruns ADD UNIQUE KEY UX_PayRuns_Client_Period_Code (ClientId, PayPeriod, RunCode)");
    }

    private static async Task CleanupObsoletePayRunValidationIssuesAsync(MySqlConnection connection)
    {
        const string bankValidationMessage = "Bank account number or IFSC is missing.";
        const string panValidationMessage = "PAN is missing; income tax reporting may be incomplete.";
        const string uanValidationMessage = "UAN is missing while EPF is enabled.";
        await connection.ExecuteAsync(@"DELETE FROM payroll_validation_issues
WHERE StepName='Bank Details Validation'
   OR StepName='PAN Validation'
   OR StepName='Provident Fund'
   OR Message IN @Messages;", new { Messages = new[] { bankValidationMessage, panValidationMessage, uanValidationMessage } });
        await connection.ExecuteAsync(@"UPDATE payruns
SET ProcessingError = TRIM(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(ProcessingError, @BankTriple, ''), @BankDouble, ''), @Bank, ''), @Pan, ''), @Uan, ''))
WHERE ProcessingError LIKE @LikeBank
   OR ProcessingError LIKE @LikePan
   OR ProcessingError LIKE @LikeUan;", new
        {
            Bank = bankValidationMessage,
            BankDouble = $"{bankValidationMessage} {bankValidationMessage}",
            BankTriple = $"{bankValidationMessage} {bankValidationMessage} {bankValidationMessage}",
            Pan = panValidationMessage,
            Uan = uanValidationMessage,
            LikeBank = $"%{bankValidationMessage}%",
            LikePan = $"%{panValidationMessage}%",
            LikeUan = $"%{uanValidationMessage}%"
        });
    }
}
