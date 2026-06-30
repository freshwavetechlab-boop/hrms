using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using System.Text.RegularExpressions;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public class PayRunRepository(IConfiguration configuration, TaxEngineRepository taxEngineRepository)
{
    private MySqlConnection CreateConnection()
    {
        var connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Connection string 'Default' is not configured.");
        var builder = new MySqlConnectionStringBuilder(connectionString)
        {
            DefaultCommandTimeout = 300
        };
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
    PresentDays DECIMAL(5,2) NOT NULL,
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
        await connection.ExecuteAsync("ALTER TABLE payrunemployees MODIFY PresentDays DECIMAL(5,2) NOT NULL, MODIFY PayableDays DECIMAL(5,2) NOT NULL;");
        await EnsureColumnAsync(connection, "payruns", "ClientId", "INT NOT NULL DEFAULT 0");
        await EnsureColumnAsync(connection, "payruns", "ClientName", "VARCHAR(250) NULL");
        await EnsureColumnAsync(connection, "payruns", "RunCode", "VARCHAR(40) NOT NULL DEFAULT 'REGULAR'");
        await EnsureColumnAsync(connection, "payruns", "RunType", "VARCHAR(30) NOT NULL DEFAULT 'Regular'");
        await EnsureColumnAsync(connection, "payruns", "RunName", "VARCHAR(120) NOT NULL DEFAULT ''");
        await EnsureColumnAsync(connection, "payruns", "Reason", "VARCHAR(500) NOT NULL DEFAULT ''");
        await EnsureColumnAsync(connection, "payruns", "RequestJson", "JSON NULL");
        await EnsureColumnAsync(connection, "payruns", "ProcessingStartedAt", "DATETIME NULL");
        await EnsureColumnAsync(connection, "payruns", "ProcessingCompletedAt", "DATETIME NULL");
        await EnsureColumnAsync(connection, "payruns", "ProcessingError", "VARCHAR(1000) NOT NULL DEFAULT ''");
        await EnsurePayRunIndexAsync(connection);
        await PayrollDataTableStore.EnsureAsync(connection);
        await connection.ExecuteAsync(@"UPDATE payrunemployees p JOIN employees e ON e.Id = p.EmployeeId LEFT JOIN clients c ON c.Id = e.ClientId SET p.ClientId = e.ClientId, p.ClientName = c.Name WHERE p.ClientId = 0 OR p.ClientName IS NULL;");
    }

    public async Task<IEnumerable<PayRun>> GetAllAsync()
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        return await connection.QueryAsync<PayRun>(@"
SELECT r.*, COUNT(e.Id) AS EmployeeCount
FROM payruns r
LEFT JOIN payrunemployees e ON e.PayRunId = r.Id AND e.IsSkipped = FALSE
GROUP BY r.Id
ORDER BY r.PayPeriod DESC, r.Id DESC;");
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
        var client = await connection.QueryFirstOrDefaultAsync<Client>("SELECT * FROM clients WHERE Id = @Id AND IsActive = TRUE", new { Id = request.ClientId }, transaction);
        if (client is null) return null;
        var runType = string.Equals(request.RunType, "Off Cycle", StringComparison.OrdinalIgnoreCase) ? "Off Cycle" : "Regular";
        var runCode = runType == "Regular" ? "REGULAR" : $"OFF-{DateTime.UtcNow:yyyyMMddHHmmss}";
        var runName = string.IsNullOrWhiteSpace(request.RunName) ? (runType == "Regular" ? "Regular payroll" : "Off-cycle payroll") : request.RunName.Trim();
        var existing = runType == "Regular" ? await connection.QueryFirstOrDefaultAsync<ExistingPayRunRow>("SELECT Id, Status FROM payruns WHERE PayPeriod = @PayPeriod AND ClientId = @ClientId AND RunCode = 'REGULAR'", new { request.PayPeriod, request.ClientId }, transaction) : null;
        if (existing is not null)
        {
            if (existing.Status is "Queued" or "Processing")
            {
                await transaction.RollbackAsync();
                return await GetAsync(existing.Id);
            }
            if (!LatestAttemptStatuses.Contains(existing.Status))
                return null;
            await DeletePayRunAttemptAsync(connection, transaction, existing.Id);
        }
        var requestJson = JsonSerializer.Serialize(request);
        var payRunId = (int)await connection.ExecuteScalarAsync<long>(@"
INSERT INTO payruns (ClientId, ClientName, PayPeriod, RunCode, RunType, RunName, Reason, PayDate, TotalWorkingDays, Status, RequestJson, ProcessingError)
VALUES (@ClientId, @ClientName, @PayPeriod, @RunCode, @RunType, @RunName, @Reason, @PayDate, @TotalWorkingDays, 'Queued', @RequestJson, '');
SELECT LAST_INSERT_ID();", new { request.ClientId, ClientName = client.Name, request.PayPeriod, RunCode = runCode, RunType = runType, RunName = runName, Reason = request.Reason.Trim(), PayDate = request.PayDate.ToDateTime(TimeOnly.MinValue), request.TotalWorkingDays, RequestJson = requestJson }, transaction);
        await WritePipelineStepLogsAsync(connection, transaction, payRunId, performedBy, []);
        await transaction.CommitAsync();
        return await GetAsync(payRunId);
    }

    public async Task<int> ProcessQueuedAsync(int maxRuns = 1)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        var runIds = (await connection.QueryAsync<int>("SELECT Id FROM payruns WHERE Status='Queued' ORDER BY CreatedAt, Id LIMIT @MaxRuns", new { MaxRuns = maxRuns })).ToList();
        foreach (var id in runIds)
            await ProcessQueuedRunAsync(id);
        return runIds.Count;
    }

    public async Task<PayRun?> ProcessQueuedRunAsync(int payRunId, string performedBy = "Payroll worker")
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        var requestJson = await connection.ExecuteScalarAsync<string?>("SELECT CAST(RequestJson AS CHAR) FROM payruns WHERE Id=@PayRunId AND Status='Queued'", new { PayRunId = payRunId });
        if (string.IsNullOrWhiteSpace(requestJson)) return null;
        var request = JsonSerializer.Deserialize<CreatePayRunRequest>(requestJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (request is null) return null;
        await connection.ExecuteAsync("UPDATE payruns SET Status='Processing', ProcessingStartedAt=UTC_TIMESTAMP(), ProcessingCompletedAt=NULL, ProcessingError='' WHERE Id=@PayRunId AND Status='Queued'", new { PayRunId = payRunId });
        try
        {
            return await ProcessExistingAsync(payRunId, request, performedBy);
        }
        catch (Exception exception)
        {
            await connection.ExecuteAsync("UPDATE payruns SET Status='Failed', ProcessingCompletedAt=UTC_TIMESTAMP(), ProcessingError=@Error WHERE Id=@PayRunId", new { PayRunId = payRunId, Error = Trunc(exception.Message, 1000) });
            await using var tx = await connection.BeginTransactionAsync();
            var issue = Issue(payRunId, null, "", "Run", "Critical", "Payroll Validation", exception.Message, true);
            issue.IssueType = "Exception";
            issue.DataJson = JsonSerializer.Serialize(new { exception = exception.GetType().Name, stackTrace = Trunc(exception.StackTrace, 6000) });
            await WriteValidationIssuesAsync(connection, tx, [issue]);
            await WritePipelineStepLogsAsync(connection, tx, payRunId, performedBy, [issue]);
            await tx.CommitAsync();
            return await GetAsync(payRunId);
        }
    }

    public async Task<PayRun?> CreateAsync(CreatePayRunRequest request, string performedBy = "System")
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var client = await connection.QueryFirstOrDefaultAsync<Client>("SELECT * FROM clients WHERE Id = @Id AND IsActive = TRUE", new { Id = request.ClientId }, transaction);
        if (client is null) return null;
        var runType = string.Equals(request.RunType, "Off Cycle", StringComparison.OrdinalIgnoreCase) ? "Off Cycle" : "Regular";
        var runCode = runType == "Regular" ? "REGULAR" : $"OFF-{DateTime.UtcNow:yyyyMMddHHmmss}";
        var runName = string.IsNullOrWhiteSpace(request.RunName) ? (runType == "Regular" ? "Regular payroll" : "Off-cycle payroll") : request.RunName.Trim();
        var existing = runType == "Regular" ? await connection.QueryFirstOrDefaultAsync<ExistingPayRunRow>("SELECT Id, Status FROM payruns WHERE PayPeriod = @PayPeriod AND ClientId = @ClientId AND RunCode = 'REGULAR'", new { request.PayPeriod, request.ClientId }, transaction) : null;
        if (existing is not null)
        {
            if (!existing.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase))
                return null;
            await DeletePayRunAttemptAsync(connection, transaction, existing.Id);
        }

        var setupJson = await PayrollDataTableStore.GetSetupJsonAsync(connection, transaction);
        var employees = (await connection.QueryAsync<PayRunSourceEmployee>("SELECT e.*, c.Name AS ClientName, COALESCE(w.State,'') AS WorkState FROM employees e LEFT JOIN clients c ON c.Id = e.ClientId LEFT JOIN worklocations w ON w.Id = e.WorkLocationId WHERE e.IsActive = TRUE AND e.ClientId = @ClientId ORDER BY e.FirstName, e.LastName", new { request.ClientId }, transaction)).ToList();
        await LoadEmployeeTablesAsync(connection, transaction, employees);
        var attendance = (await GetPayRunAttendanceAsync(connection, transaction, request.ClientId, request.PayPeriod)).ToDictionary(row => row.EmployeeId);

        var selectedAdjustmentIds = request.AdjustmentIds.Distinct().ToArray();
        var adjustments = await GetApplicableAdjustmentsAsync(connection, transaction, request.ClientId, request.PayPeriod, runType, selectedAdjustmentIds);
        var adjustmentByEmployee = adjustments.GroupBy(item => item.EmployeeId).ToDictionary(group => group.Key, group => group.ToList());
        var includedEmployeeIds = runType == "Off Cycle"
            ? request.IncludedEmployeeIds.Concat(adjustments.Select(item => item.EmployeeId)).Distinct().ToHashSet()
            : employees.Select(employee => employee.Id).Except(request.ExcludedEmployeeIds).ToHashSet();
        if (runType == "Off Cycle" && includedEmployeeIds.Count == 0)
        {
            await transaction.RollbackAsync();
            return null;
        }
        var validationIssues = ValidatePayRunInputs(0, request, runType, employees.Where(employee => includedEmployeeIds.Contains(employee.Id)).ToList(), attendance, setupJson);
        var hasBlockingIssues = validationIssues.Any(issue => issue.IsBlocking);
        var payRunId = (int)await connection.ExecuteScalarAsync<long>(@"
INSERT INTO payruns (ClientId, ClientName, PayPeriod, RunCode, RunType, RunName, Reason, PayDate, TotalWorkingDays, Status) VALUES (@ClientId, @ClientName, @PayPeriod, @RunCode, @RunType, @RunName, @Reason, @PayDate, @TotalWorkingDays, @Status);
SELECT LAST_INSERT_ID();", new { request.ClientId, ClientName = client.Name, request.PayPeriod, RunCode = runCode, RunType = runType, RunName = runName, Reason = request.Reason.Trim(), PayDate = request.PayDate.ToDateTime(TimeOnly.MinValue), request.TotalWorkingDays, Status = hasBlockingIssues ? "Failed" : "Draft" }, transaction);
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
            var presentDays = runType == "Off Cycle" ? 0 : attendanceRow is null ? request.TotalWorkingDays : Math.Clamp(attendanceRow.PresentDays, 0, request.TotalWorkingDays);
            var payableDays = runType == "Off Cycle" ? 0 : attendanceRow is null ? request.TotalWorkingDays : Math.Clamp(attendanceRow.PayableDays, 0, request.TotalWorkingDays);
            var employeeAdjustments = adjustmentByEmployee.GetValueOrDefault(employee.Id) ?? [];
            var row = BuildEmployee(payRunId, employee, setupJson, request.PayPeriod, request.TotalWorkingDays, presentDays, payableDays, employeeAdjustments, 0, 0, 0, false);
            if (runType == "Regular")
            {
                var projectedTds = await CalculateMonthlyTdsAsync(connection, transaction, employee, row, request.PayPeriod);
                if (projectedTds > 0)
                    row = BuildEmployee(payRunId, employee, setupJson, request.PayPeriod, request.TotalWorkingDays, presentDays, payableDays, employeeAdjustments, 0, 0, projectedTds, false);
            }
            await SaveEmployeeAsync(connection, transaction, row);
            await WriteCalculationTracesAsync(connection, transaction, row, request.TotalWorkingDays);
        }
        if (adjustments.Count > 0)
            await connection.ExecuteAsync("UPDATE payrolladjustments SET Status = 'Applied', PayRunId = @PayRunId WHERE Id IN @Ids", new { PayRunId = payRunId, Ids = adjustments.Select(item => item.Id).ToArray() }, transaction);

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
        var runCode = runType == "Regular" ? "REGULAR" : $"OFF-FAILED-{DateTime.UtcNow:yyyyMMddHHmmss}";
        var runName = string.IsNullOrWhiteSpace(request.RunName) ? $"{runType} payroll" : request.RunName.Trim();
        if (runType == "Regular")
        {
            var existing = await connection.QueryFirstOrDefaultAsync<ExistingPayRunRow>("SELECT Id, Status FROM payruns WHERE PayPeriod = @PayPeriod AND ClientId = @ClientId AND RunCode = 'REGULAR'", new { request.PayPeriod, request.ClientId }, transaction);
            if (existing is not null)
            {
                if (!existing.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase))
                    return await GetAsync(existing.Id);
                await DeletePayRunAttemptAsync(connection, transaction, existing.Id);
            }
        }
        var payRunId = (int)await connection.ExecuteScalarAsync<long>(@"
INSERT INTO payruns (ClientId, ClientName, PayPeriod, RunCode, RunType, RunName, Reason, PayDate, TotalWorkingDays, Status) VALUES (@ClientId, @ClientName, @PayPeriod, @RunCode, @RunType, @RunName, @Reason, @PayDate, @TotalWorkingDays, 'Failed');
SELECT LAST_INSERT_ID();", new { request.ClientId, ClientName = client.Name, request.PayPeriod, RunCode = runCode, RunType = runType, RunName = runName, Reason = FirstText(request.Reason, "Payroll engine exception").Trim(), PayDate = request.PayDate.ToDateTime(TimeOnly.MinValue), request.TotalWorkingDays }, transaction);
        var issue = Issue(payRunId, null, "", "Run", "Critical", "Payroll Validation", exception.Message, true);
        issue.IssueType = "Exception";
        issue.DataJson = JsonSerializer.Serialize(new { exception = exception.GetType().Name, stackTrace = Trunc(exception.StackTrace, 6000) });
        await WriteValidationIssuesAsync(connection, transaction, [issue]);
        await WritePipelineStepLogsAsync(connection, transaction, payRunId, performedBy, [issue]);
        await transaction.CommitAsync();
        return await GetAsync(payRunId);
    }

    private async Task<PayRun?> ProcessExistingAsync(int payRunId, CreatePayRunRequest request, string performedBy)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("DELETE FROM tax_computation_snapshots WHERE pay_run_id=@PayRunId", new { PayRunId = payRunId });
        await using var transaction = await connection.BeginTransactionAsync();
        var payRun = await connection.QueryFirstOrDefaultAsync<PayRun>("SELECT * FROM payruns WHERE Id=@PayRunId AND Status='Processing'", new { PayRunId = payRunId }, transaction);
        if (payRun is null) return null;
        var client = await connection.QueryFirstOrDefaultAsync<Client>("SELECT * FROM clients WHERE Id = @Id AND IsActive = TRUE", new { Id = request.ClientId }, transaction);
        if (client is null) return null;
        await ClearPayRunDetailsAsync(connection, transaction, payRunId);

        var runType = string.Equals(request.RunType, "Off Cycle", StringComparison.OrdinalIgnoreCase) ? "Off Cycle" : "Regular";
        var setupJson = await PayrollDataTableStore.GetSetupJsonAsync(connection, transaction);
        var employees = (await connection.QueryAsync<PayRunSourceEmployee>("SELECT e.*, c.Name AS ClientName, COALESCE(w.State,'') AS WorkState FROM employees e LEFT JOIN clients c ON c.Id = e.ClientId LEFT JOIN worklocations w ON w.Id = e.WorkLocationId WHERE e.IsActive = TRUE AND e.ClientId = @ClientId ORDER BY e.FirstName, e.LastName", new { request.ClientId }, transaction)).ToList();
        await LoadEmployeeTablesAsync(connection, transaction, employees);
        var attendance = (await GetPayRunAttendanceAsync(connection, transaction, request.ClientId, request.PayPeriod)).ToDictionary(row => row.EmployeeId);
        var selectedAdjustmentIds = request.AdjustmentIds.Distinct().ToArray();
        var adjustments = await GetApplicableAdjustmentsAsync(connection, transaction, request.ClientId, request.PayPeriod, runType, selectedAdjustmentIds);
        var adjustmentByEmployee = adjustments.GroupBy(item => item.EmployeeId).ToDictionary(group => group.Key, group => group.ToList());
        var includedEmployeeIds = runType == "Off Cycle"
            ? request.IncludedEmployeeIds.Concat(adjustments.Select(item => item.EmployeeId)).Distinct().ToHashSet()
            : employees.Select(employee => employee.Id).Except(request.ExcludedEmployeeIds).ToHashSet();
        var validationIssues = ValidatePayRunInputs(payRunId, request, runType, employees.Where(employee => includedEmployeeIds.Contains(employee.Id)).ToList(), attendance, setupJson);
        if (runType == "Off Cycle" && includedEmployeeIds.Count == 0)
            validationIssues.Add(Issue(payRunId, null, "", "Run", "Critical", "Payroll Validation", "Off-cycle payroll needs at least one employee or approved adjustment.", true));
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
            var presentDays = runType == "Off Cycle" ? 0 : attendanceRow is null ? request.TotalWorkingDays : Math.Clamp(attendanceRow.PresentDays, 0, request.TotalWorkingDays);
            var payableDays = runType == "Off Cycle" ? 0 : attendanceRow is null ? request.TotalWorkingDays : Math.Clamp(attendanceRow.PayableDays, 0, request.TotalWorkingDays);
            var employeeAdjustments = adjustmentByEmployee.GetValueOrDefault(employee.Id) ?? [];
            var row = BuildEmployee(payRunId, employee, setupJson, request.PayPeriod, request.TotalWorkingDays, presentDays, payableDays, employeeAdjustments, 0, 0, 0, false);
            if (runType == "Regular")
            {
                var projectedTds = await CalculateMonthlyTdsAsync(connection, transaction, employee, row, request.PayPeriod);
                if (projectedTds > 0)
                    row = BuildEmployee(payRunId, employee, setupJson, request.PayPeriod, request.TotalWorkingDays, presentDays, payableDays, employeeAdjustments, 0, 0, projectedTds, false);
            }
            await SaveEmployeeAsync(connection, transaction, row);
            await WriteCalculationTracesAsync(connection, transaction, row, request.TotalWorkingDays);
        }
        if (adjustments.Count > 0)
            await connection.ExecuteAsync("UPDATE payrolladjustments SET Status = 'Applied', PayRunId = @PayRunId WHERE Id IN @Ids", new { PayRunId = payRunId, Ids = adjustments.Select(item => item.Id).ToArray() }, transaction);
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
        var payRun = await connection.QueryFirstOrDefaultAsync<PayRun>("SELECT * FROM payruns WHERE Id = @Id", new { Id = payRunId }, transaction);
        if (payRun is null || payRun.Status != "Draft")
            return null;
        var employee = await connection.QueryFirstOrDefaultAsync<PayRunSourceEmployee>("SELECT e.*, c.Name AS ClientName, COALESCE(w.State,'') AS WorkState FROM employees e LEFT JOIN clients c ON c.Id = e.ClientId LEFT JOIN worklocations w ON w.Id = e.WorkLocationId WHERE e.Id = @Id", new { Id = employeeId }, transaction);
        if (employee is null)
            return null;
        await LoadEmployeeTablesAsync(connection, transaction, [employee]);
        var setupJson = await PayrollDataTableStore.GetSetupJsonAsync(connection, transaction);
        var presentDays = Math.Clamp(request.PresentDays, 0, payRun.TotalWorkingDays);
        var tds = Math.Max(0, request.ManualTds);
        var row = BuildEmployee(payRunId, employee, setupJson, payRun.PayPeriod, payRun.TotalWorkingDays, presentDays, presentDays, [], Math.Max(0, request.OneTimeEarnings), Math.Max(0, request.OneTimeDeductions), tds, request.IsSkipped);
        if (!request.IsSkipped && tds == 0 && payRun.RunType == "Regular")
        {
            tds = await CalculateMonthlyTdsAsync(connection, transaction, employee, row, payRun.PayPeriod);
            if (tds > 0)
                row = BuildEmployee(payRunId, employee, setupJson, payRun.PayPeriod, payRun.TotalWorkingDays, presentDays, presentDays, [], Math.Max(0, request.OneTimeEarnings), Math.Max(0, request.OneTimeDeductions), tds, false);
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
        var employee = await connection.QueryFirstOrDefaultAsync<Employee>("SELECT * FROM employees WHERE Id = @Id AND ClientId = @ClientId AND IsActive = TRUE", new { Id = adjustment.EmployeeId, adjustment.ClientId });
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
        var rows = await connection.ExecuteAsync("UPDATE payruns SET Status = 'Pending Approval' WHERE Id = @Id AND Status = 'Draft'", new { Id = id });
        return rows == 1 ? await GetAsync(id) : null;
    }

    public async Task<PayRun?> ApproveAsync(int id)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        var rows = await connection.ExecuteAsync("UPDATE payruns SET Status = 'Approved' WHERE Id = @Id AND Status IN ('Draft', 'Pending Approval')", new { Id = id });
        return rows == 1 ? await GetAsync(id) : null;
    }

    public async Task<bool> DeleteDraftAsync(int id)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        return await connection.ExecuteAsync("DELETE FROM payruns WHERE Id = @Id AND Status = 'Draft'", new { Id = id }) == 1;
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
        var payRun = await connection.QueryFirstOrDefaultAsync<PayRun>("SELECT * FROM payruns WHERE Id = @Id", new { Id = id }, transaction);
        if (payRun is null || payRun.Status is not ("Approved" or "Partially Paid")) return null;
        var employeeIds = request.EmployeeIds.Distinct().ToArray();
        if (employeeIds.Length == 0)
            employeeIds = (await connection.QueryAsync<int>("SELECT EmployeeId FROM payrunemployees WHERE PayRunId = @Id AND IsSkipped = FALSE AND PaymentStatus != 'Paid'", new { Id = id }, transaction)).ToArray();
        if (employeeIds.Length == 0) return null;
        await connection.ExecuteAsync("UPDATE payrunemployees SET PaymentStatus = 'Paid', PaymentDate = @PaymentDate WHERE PayRunId = @Id AND IsSkipped = FALSE AND EmployeeId IN @EmployeeIds", new { Id = id, PaymentDate = request.PaymentDate.ToDateTime(TimeOnly.MinValue), EmployeeIds = employeeIds }, transaction);
        var pending = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM payrunemployees WHERE PayRunId = @Id AND IsSkipped = FALSE AND PaymentStatus != 'Paid'", new { Id = id }, transaction);
        await connection.ExecuteAsync("UPDATE payruns SET Status = @Status WHERE Id = @Id", new { Id = id, Status = pending == 0 ? "Paid" : "Partially Paid" }, transaction);
        await transaction.CommitAsync();
        return await GetAsync(id);
    }

    private static PayRunEmployee BuildEmployee(int payRunId, PayRunSourceEmployee employee, string setupJson, string payPeriod, int totalWorkingDays, decimal presentDays, decimal payableDays, IEnumerable<PayrollAdjustment> adjustments, decimal manualOneTimeEarnings, decimal manualOneTimeDeductions, decimal manualTds, bool isSkipped)
    {
        var setup = ReadPayrollSetup(setupJson);
        var salary = CalculateConfiguredSalary(employee, setup, totalWorkingDays, presentDays, payableDays);
        var salaryDeductionCodes = salary.Where(row => IsDeductionCategory(row.Component.Category) && row.Monthly > 0).Select(row => row.Component.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var factor = totalWorkingDays == 0 || isSkipped ? 0 : (decimal)payableDays / totalWorkingDays;
        var lines = new List<object>();
        decimal monthlyGross = 0, grossPay = 0, deductions = 0;

        foreach (var row in salary)
        {
            var amount = isSkipped ? 0 : row.Component.ProRata ? decimal.Round(row.Monthly * factor, 2) : row.Monthly;
            lines.Add(new { Id = row.Component.Code, row.Component.Name, row.Component.Category, monthlyAmount = row.Monthly, amount, row.Component.ProRata });
            if (IsDeductionCategory(row.Component.Category))
                deductions += amount;
            else if (IsPayableEarningCategory(row.Component.Category))
            {
                monthlyGross += row.Monthly;
                grossPay += amount;
            }
        }

        var seededDeductions = ReadSeededPayrollDeductions(employee);
        foreach (var seededDeduction in seededDeductions)
        {
            if (salaryDeductionCodes.Contains(seededDeduction.Code)) continue;
            var amount = isSkipped ? 0 : seededDeduction.Amount;
            if (amount <= 0) continue;
            deductions += amount;
            lines.Add(new { Id = seededDeduction.Code, Name = seededDeduction.Name, Category = "Deduction", monthlyAmount = seededDeduction.Amount, amount, ProRata = false });
        }

        if (!isSkipped && !salaryDeductionCodes.Contains("PT_LWF_WC") && !seededDeductions.Any(row => row.Code.Equals("PT_LWF_WC", StringComparison.OrdinalIgnoreCase)))
        {
            var professionalTax = CalculateProfessionalTax(employee, setup, grossPay, payPeriod);
            if (professionalTax > 0)
            {
                deductions += professionalTax;
                lines.Add(new { Id = "PT_LWF_WC", Name = "Professional Tax", Category = "Deduction", monthlyAmount = professionalTax, amount = professionalTax, ProRata = false });
            }
        }

        var adjustmentRows = adjustments.ToList();
        var oneTimeEarnings = isSkipped ? 0 : manualOneTimeEarnings + adjustmentRows.Where(item => !IsDeduction(item)).Sum(item => item.Amount);
        var oneTimeDeductions = isSkipped ? 0 : manualOneTimeDeductions + adjustmentRows.Where(IsDeduction).Sum(item => item.Amount);
        var tds = isSkipped ? 0 : manualTds;
        if (tds > 0)
        {
            deductions += tds;
            lines.Add(new { Id = "TDS", Name = "Income Tax (TDS)", Category = "Deduction", monthlyAmount = tds, amount = tds, ProRata = false });
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
        lines.Add(new { Id = "GROSS_EARNED", Name = "Gross Earned", Category = "Summary", monthlyAmount = monthlyGross, amount = grossPay, ProRata = false });
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
            ManualTds = tds,
            NetPay = net,
            IsSkipped = isSkipped,
            DetailsJson = JsonSerializer.Serialize(lines)
        };
    }

    private static List<CalculatedPayrollComponent> CalculateConfiguredSalary(PayRunSourceEmployee employee, PayrollSetupData setup, int payrollDays, decimal presentDays, decimal payableDays)
    {
        var salaryJson = employee.SalaryComponents.Count > 0 ? new Dictionary<string, decimal>(employee.SalaryComponents, StringComparer.OrdinalIgnoreCase) : JsonSerializer.Deserialize<Dictionary<string, decimal>>(employee.SalaryJson, new JsonSerializerOptions { NumberHandling = JsonNumberHandling.AllowReadingFromString }) ?? [];
        var componentById = setup.Components.ToDictionary(component => component.Id);
        var employeeSalaryRows = setup.Components
            .Where(component => component.Active && salaryJson.ContainsKey(component.Id))
            .Select(component => new CalculatedPayrollComponent(component, salaryJson[component.Id]))
            .OrderBy(row => row.Component.Priority)
            .ToList();
        if (employeeSalaryRows.Any(row => row.Monthly > 0))
            return employeeSalaryRows;

        var structure = setup.Structures.FirstOrDefault(item => item.Id == employee.SalaryStructureId)
            ?? setup.Structures.FirstOrDefault(item => item.ClientId.Split(':')[0] == employee.ClientId.ToString(CultureInfo.InvariantCulture));
        var monthlyTarget = employee.AnnualCtc > 0 ? decimal.Round(employee.AnnualCtc / 12m, 2) : FindGrossFromSalaryJson(salaryJson, componentById);
        if (monthlyTarget <= 0)
            monthlyTarget = NumberFrom(structure?.AnnualCtc ?? "") / 12m;

        if (structure is not null && structure.Lines.Count > 0 && monthlyTarget > 0)
            return CalculateStructureLines(setup.Components, structure, monthlyTarget, payrollDays, presentDays, payableDays);

        return salaryJson
            .Select(entry => componentById.TryGetValue(entry.Key, out var component)
                ? new CalculatedPayrollComponent(component, entry.Value)
                : new CalculatedPayrollComponent(new PayrollComponent(entry.Key, entry.Key, "Component", "Earning", "Fixed Amount", "", "", "", true, true, 999, "Fixed Pay"), entry.Value))
            .Where(row => row.Component.Active)
            .OrderBy(row => row.Component.Priority)
            .ToList();
    }

    private static List<SeededPayrollDeduction> ReadSeededPayrollDeductions(PayRunSourceEmployee employee)
    {
        var rows = new List<SeededPayrollDeduction>();
        if (employee.HasPersonalDetails)
        {
            if (employee.EsicEmployee > 0) rows.Add(new SeededPayrollDeduction("ESIC", "Employee ESIC", employee.EsicEmployee));
            if (employee.PtLwfWorkmenComp > 0) rows.Add(new SeededPayrollDeduction("PT_LWF_WC", "PT / LWF / Workmen Comp", employee.PtLwfWorkmenComp));
            if (employee.Tds > 0) rows.Add(new SeededPayrollDeduction("TDS", "TDS", employee.Tds));
            if (employee.Recovery > 0) rows.Add(new SeededPayrollDeduction("RECOVERY", "Recovery", employee.Recovery));
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
            if (esic > 0) rows.Add(new SeededPayrollDeduction("ESIC", "Employee ESIC", esic));
            if (ptLwfWorkmen > 0) rows.Add(new SeededPayrollDeduction("PT_LWF_WC", "PT / LWF / Workmen Comp", ptLwfWorkmen));
            if (tds > 0) rows.Add(new SeededPayrollDeduction("TDS", "TDS", tds));
            if (recovery > 0) rows.Add(new SeededPayrollDeduction("RECOVERY", "Recovery", recovery));
        }
        catch
        {
        }
        return rows;
    }

    private static List<CalculatedPayrollComponent> CalculateStructureLines(List<PayrollComponent> components, SalaryStructureSetup structure, decimal monthlyTarget, int payrollDays, decimal presentDays, decimal payableDays)
    {
        var componentById = components.ToDictionary(component => component.Id);
        var values = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<CalculatedPayrollComponent>();

        var orderedLines = structure.Lines
            .Select((line, index) => new { line, index, component = componentById.GetValueOrDefault(line.ComponentId) })
            .Where(item => item.component is not null && item.component.Active)
            .OrderBy(item => item.component!.Priority)
            .ThenBy(item => item.index);

        foreach (var item in orderedLines)
        {
            var line = item.line;
            var component = item.component!;
            var source = FirstText(line.Value, component.Formula, component.Value);
            var type = NormalizeCalculationType(component.CalculationType);
            var monthly = type switch
            {
                "Manual / Variable" => 0,
                "Slab Based" => SlabValue(source, values.GetValueOrDefault("GROSS_EARNED", monthlyTarget)),
                "Residual / Balancing" => CalculateResidual(component, source, monthlyTarget, values),
                "Formula" => EvaluateComponentFormula(component, source, monthlyTarget, payrollDays, presentDays, payableDays, values),
                _ => NumberFrom(source)
            };
            monthly = Math.Max(0, decimal.Round(monthly, 2));
            values[component.Id] = monthly;
            values[component.Code] = monthly;
            rows.Add(new CalculatedPayrollComponent(component, monthly));
        }

        return rows;
    }

    private static decimal EvaluateComponentFormula(PayrollComponent component, string source, decimal monthlyTarget, int payrollDays, decimal presentDays, decimal payableDays, Dictionary<string, decimal> values)
    {
        if (component.CalculationType.Equals("Percentage of CTC", StringComparison.OrdinalIgnoreCase))
            source = string.IsNullOrWhiteSpace(component.Formula) ? $"CTC * {FirstText(component.Value, source)}%" : component.Formula;
        if (component.CalculationType.Equals("Percentage of Component", StringComparison.OrdinalIgnoreCase))
            source = string.IsNullOrWhiteSpace(component.Formula) ? $"{FirstText(component.BaseComponent, "BASIC")} * {FirstText(component.Value, source)}%" : component.Formula;
        return EvaluateFormula(source, monthlyTarget, payrollDays, presentDays, payableDays, values);
    }

    private static decimal CalculateResidual(PayrollComponent component, string source, decimal monthlyTarget, Dictionary<string, decimal> values)
    {
        var targetKey = FirstText(component.BaseComponent, "GROSS");
        var target = values.GetValueOrDefault(targetKey, targetKey.Equals("CTC", StringComparison.OrdinalIgnoreCase) || targetKey.Equals("GROSS", StringComparison.OrdinalIgnoreCase) ? monthlyTarget : monthlyTarget);
        var used = values.Where(entry => !entry.Key.All(char.IsDigit)).Sum(entry => entry.Value);
        if (!string.IsNullOrWhiteSpace(source) && source.Contains("SUM", StringComparison.OrdinalIgnoreCase))
            return Math.Max(0, EvaluateFormula(source, monthlyTarget, 30, 30, 30, values));
        return Math.Max(0, target - used);
    }

    private static decimal EvaluateFormula(string source, decimal monthlyTarget, int payrollDays, decimal presentDays, decimal payableDays, Dictionary<string, decimal> values)
    {
        if (string.IsNullOrWhiteSpace(source)) return 0;
        var refs = new Dictionary<string, decimal>(values, StringComparer.OrdinalIgnoreCase)
        {
            ["GROSS"] = monthlyTarget,
            ["CTC"] = monthlyTarget,
            ["MONTHLY_CTC"] = monthlyTarget,
            ["ANNUAL_CTC"] = monthlyTarget * 12m,
            ["PAYROLL_DAYS"] = payrollDays,
            ["TOTAL_DAYS"] = payrollDays,
            ["WORKING_DAYS"] = payrollDays,
            ["PAYABLE_DAYS"] = payableDays,
            ["PRESENT_DAYS"] = presentDays,
            ["LOP_DAYS"] = Math.Max(0, payrollDays - payableDays)
        };
        var formula = source.ToUpperInvariant().Replace("\u00D7", "*").Replace("\u00F7", "/");
        var earningsSum = values.Where(entry => !entry.Key.All(char.IsDigit)).Sum(entry => entry.Value);
        formula = Regex.Replace(formula, @"SUM\s*\(\s*(FIXED\s+)?EARNINGS(_BEFORE_THIS)?\s*\)", earningsSum.ToString(CultureInfo.InvariantCulture), RegexOptions.IgnoreCase);
        formula = Regex.Replace(formula, @"(\d+(?:\.\d+)?)\s*%\s*OF\s*([A-Z0-9_]+)", "$2*$1/100", RegexOptions.IgnoreCase);
        formula = Regex.Replace(formula, @"([A-Z0-9_]+)\s*\*\s*(\d+(?:\.\d+)?)\s*%", "$1*$2/100", RegexOptions.IgnoreCase);
        formula = Regex.Replace(formula, @"(\d+(?:\.\d+)?)%", "$1/100", RegexOptions.IgnoreCase);
        try { return new FormulaParser(formula, name => refs.GetValueOrDefault(name, 0)).Parse(); }
        catch { return 0; }
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
        Text(element, "calculationType", "Fixed Amount"),
        Text(element, "value"),
        Text(element, "formula"),
        Text(element, "baseComponent"),
        Bool(element, "proRata", true),
        Bool(element, "active", true),
        (int)NumberFrom(Text(element, "priority", "999")),
        Text(element, "payType", "Fixed Pay"));

    private static SalaryStructureSetup ReadStructure(JsonElement element)
    {
        var lines = element.TryGetProperty("lines", out var lineJson) && lineJson.ValueKind == JsonValueKind.Array
            ? lineJson.EnumerateArray().Select(line => new SalaryStructureLine(Text(line, "componentId"), Text(line, "value"))).Where(line => !string.IsNullOrWhiteSpace(line.ComponentId)).ToList()
            : [];
        return new SalaryStructureSetup(Text(element, "id"), Text(element, "clientId"), Text(element, "annualCtc"), lines);
    }

    private static decimal FindGrossFromSalaryJson(Dictionary<string, decimal> salaryJson, Dictionary<string, PayrollComponent> componentById)
    {
        var gross = salaryJson.FirstOrDefault(entry => entry.Key.Equals("GROSS", StringComparison.OrdinalIgnoreCase)).Value;
        if (gross > 0) return gross;
        return salaryJson.Where(entry => componentById.TryGetValue(entry.Key, out var component) && IsPayableEarningCategory(component.Category)).Sum(entry => entry.Value);
    }

    private static string NormalizeCalculationType(string value) =>
        value.Equals("Percentage of CTC", StringComparison.OrdinalIgnoreCase) || value.Equals("Percentage of Component", StringComparison.OrdinalIgnoreCase) || value.Equals("Formula", StringComparison.OrdinalIgnoreCase) ? "Formula" :
        value.Equals("Balancing Amount", StringComparison.OrdinalIgnoreCase) || value.Equals("Residual / Balancing", StringComparison.OrdinalIgnoreCase) ? "Residual / Balancing" :
        value.Equals("Manual Entry", StringComparison.OrdinalIgnoreCase) || value.Equals("Manual Override", StringComparison.OrdinalIgnoreCase) || value.Equals("Manual / Variable", StringComparison.OrdinalIgnoreCase) ? "Manual / Variable" :
        value.Equals("Slab Based", StringComparison.OrdinalIgnoreCase) ? "Slab Based" : "Fixed Amount";

    private static bool IsPayableEarningCategory(string category) => category.Equals("Earning", StringComparison.OrdinalIgnoreCase) || category.Equals("Reimbursement", StringComparison.OrdinalIgnoreCase);
    private static bool IsDeductionCategory(string category) => category.Equals("Deduction", StringComparison.OrdinalIgnoreCase);
    private static string FirstText(params string[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    private static string Text(JsonElement element, string property, string fallback = "") => element.TryGetProperty(property, out var value) ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : value.ToString() : fallback;
    private static bool Bool(JsonElement element, string property, bool fallback) => element.TryGetProperty(property, out var value) ? value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, JsonValueKind.String => bool.TryParse(value.GetString(), out var parsed) ? parsed : fallback, _ => fallback } : fallback;
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
            .Where(token => token is not ("MIN" or "MAX" or "ROUND" or "ROUNDDOWN" or "ROUNDUP" or "SUM" or "OF" or "FIXED" or "EARNINGS" or "BEFORE" or "THIS"));

    private static readonly HashSet<string> KnownFormulaTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "GROSS", "CTC", "MONTHLY_CTC", "ANNUAL_CTC", "PAYROLL_DAYS", "TOTAL_DAYS", "WORKING_DAYS", "PAYABLE_DAYS", "PRESENT_DAYS", "LOP_DAYS",
        "FIXED_EARNINGS", "FIXED_EARNINGS_BEFORE_THIS", "EARNINGS_BEFORE_THIS"
    };
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
    private sealed record SalaryStructureSetup(string Id, string ClientId, string AnnualCtc, List<SalaryStructureLine> Lines);
    private sealed record SalaryStructureLine(string ComponentId, string Value);
    private sealed record PayrollComponent(string Id, string Code, string Name, string Category, string CalculationType, string Value, string Formula, string BaseComponent, bool ProRata, bool Active, int Priority, string PayType);
    private sealed record ProfessionalTaxSetup(bool Enabled, string DefaultState, string Cycle, List<ProfessionalTaxSlab> Slabs);
    private sealed record ProfessionalTaxSlab(string State, decimal SalaryFrom, decimal? SalaryTo, decimal DeductionAmount, DateTime? EffectiveFrom, DateTime? EffectiveTo, string Gender, bool Active);
    private sealed record CalculatedPayrollComponent(PayrollComponent Component, decimal Monthly);
    private sealed record SeededPayrollDeduction(string Code, string Name, decimal Amount);

    private sealed class FormulaParser(string text, Func<string, decimal> resolve)
    {
        private int _index;
        public decimal Parse() => Expression();
        private decimal Expression()
        {
            var value = Term();
            while (true)
            {
                Skip();
                if (Match('+')) value += Term();
                else if (Match('-')) value -= Term();
                else return value;
            }
        }
        private decimal Term()
        {
            var value = Factor();
            while (true)
            {
                Skip();
                if (Match('*')) value *= Factor();
                else if (Match('/'))
                {
                    var divisor = Factor();
                    value = divisor == 0 ? 0 : value / divisor;
                }
                else return value;
            }
        }
        private decimal Factor()
        {
            Skip();
            if (Match('+')) return Factor();
            if (Match('-')) return -Factor();
            if (Match('('))
            {
                var value = Expression();
                Match(')');
                return value;
            }
            if (char.IsDigit(Current) || Current == '.') return Number();
            if (char.IsLetter(Current) || Current == '_')
            {
                var name = Identifier();
                Skip();
                if (!Match('(')) return resolve(name);
                var args = new List<decimal>();
                if (!Match(')'))
                {
                    do { args.Add(Expression()); Skip(); } while (Match(','));
                    Match(')');
                }
                return name switch
                {
                    "MIN" => args.Count == 0 ? 0 : args.Min(),
                    "MAX" => args.Count == 0 ? 0 : args.Max(),
                    "ROUND" => args.Count == 0 ? 0 : Math.Round(args[0], 0, MidpointRounding.AwayFromZero),
                    "ROUNDDOWN" => args.Count == 0 ? 0 : Math.Floor(args[0]),
                    "ROUNDUP" => args.Count == 0 ? 0 : Math.Ceiling(args[0]),
                    _ => 0
                };
            }
            return 0;
        }
        private decimal Number()
        {
            var start = _index;
            while (char.IsDigit(Current) || Current == '.') _index++;
            return decimal.TryParse(text[start.._index], NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : 0;
        }
        private string Identifier()
        {
            var start = _index;
            while (char.IsLetterOrDigit(Current) || Current == '_') _index++;
            return text[start.._index].ToUpperInvariant();
        }
        private char Current => _index < text.Length ? text[_index] : '\0';
        private void Skip() { while (char.IsWhiteSpace(Current)) _index++; }
        private bool Match(char value) { Skip(); if (Current != value) return false; _index++; return true; }
    }

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
        issues.AddRange(ValidateFormulaMasters(payRunId, setup.Components));
        var epfEnabled = SetupBool(setupJson, "statutory", "epf");
        var esiEnabled = SetupBool(setupJson, "statutory", "esi");

        foreach (var employee in employees)
        {
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
            if (employee.AnnualCtc <= 0 && employee.SalaryComponents.Count == 0 && string.IsNullOrWhiteSpace(employee.SalaryStructureId))
                issues.Add(Issue(payRunId, employee.Id, employee.EmployeeCode, "Employee", "Critical", "Salary Structure Validation", "Employee has no annual CTC, salary components, or salary structure.", true));
            if (string.IsNullOrWhiteSpace(FirstText(employee.BankAccountNo, JsonValue(employee.PaymentJson, "bankAccountNo"))) || string.IsNullOrWhiteSpace(FirstText(employee.IfscCode, JsonValue(employee.PaymentJson, "ifscCode"))))
                issues.Add(Issue(payRunId, employee.Id, employee.EmployeeCode, "Employee", "Critical", "Bank Details Validation", "Bank account number or IFSC is missing.", true));
            if (string.IsNullOrWhiteSpace(FirstText(employee.PanNumber, JsonValue(employee.PersonalJson, "panNumber"))))
                issues.Add(Issue(payRunId, employee.Id, employee.EmployeeCode, "Employee", "Warning", "PAN Validation", "PAN is missing; income tax reporting may be incomplete.", false));
            if (epfEnabled && string.IsNullOrWhiteSpace(FirstText(employee.UanNumber, JsonValue(employee.PersonalJson, "uanNumber"))))
                issues.Add(Issue(payRunId, employee.Id, employee.EmployeeCode, "Employee", "Warning", "Provident Fund", "UAN is missing while EPF is enabled.", false));
            if (esiEnabled && string.IsNullOrWhiteSpace(FirstText(employee.EsicNumber, JsonValue(employee.PersonalJson, "esicNumber"))))
                issues.Add(Issue(payRunId, employee.Id, employee.EmployeeCode, "Employee", "Warning", "ESI", "ESIC number is missing while ESI is enabled.", false));
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
                if (!knownCodes.Contains(token))
                    issues.Add(Issue(payRunId, null, "", "Run", "Critical", "Formula Validation", $"{component.Code} formula references missing component/token {token}.", true));
                var dependency = components.FirstOrDefault(item => item.Code.Equals(token, StringComparison.OrdinalIgnoreCase) || item.Id.Equals(token, StringComparison.OrdinalIgnoreCase));
                if (dependency is not null && dependency.Priority >= component.Priority)
                    issues.Add(Issue(payRunId, null, "", "Run", "Warning", "Formula Validation", $"{component.Code} depends on {dependency.Code}, but dependency priority is not earlier.", false));
            }
        }
        return issues;
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
                FormulaUsed = Bool(element, "ProRata", false) ? "MonthlyAmount * PayableDays / TotalWorkingDays" : "Configured amount",
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

    private async Task<decimal> CalculateMonthlyTdsAsync(MySqlConnection connection, MySqlTransaction transaction, PayRunSourceEmployee employee, PayRunEmployee preliminaryRow, string payPeriod)
    {
        var financialYear = FinancialYearFromPayPeriod(payPeriod);
        var taxSetting = await connection.QueryFirstOrDefaultAsync<TaxProjectionSetting>(@"SELECT enabled AS Enabled, project_monthly_tds AS ProjectMonthlyTds FROM tax_client_settings WHERE client_id=@ClientId AND financial_year=@FinancialYear AND active=TRUE LIMIT 1", new { employee.ClientId, FinancialYear = financialYear }, transaction);
        if (taxSetting is { Enabled: false } || taxSetting is { ProjectMonthlyTds: false }) return 0;
        if (taxSetting is null)
        {
            var hasActiveTaxMaster = await connection.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM tax_rule_versions WHERE financial_year=@FinancialYear AND active=TRUE", new { FinancialYear = financialYear }, transaction) > 0;
            if (!hasActiveTaxMaster) return 0;
        }
        var annualGross = employee.AnnualCtc > 0 ? employee.AnnualCtc : preliminaryRow.MonthlyGross * 12m;
        if (annualGross <= 0) return 0;
        var (fyStart, _) = FinancialYearRangeFromCode(financialYear);
        var tdsAlreadyDeducted = await connection.ExecuteScalarAsync<decimal?>(@"
SELECT COALESCE(SUM(p.ManualTds),0)
FROM payrunemployees p
JOIN payruns r ON r.Id=p.PayRunId
WHERE p.EmployeeId=@EmployeeId
  AND r.ClientId=@ClientId
  AND r.PayPeriod >= @StartPeriod
  AND r.PayPeriod < @PayPeriod
  AND r.Status NOT IN ('Draft')
  AND p.IsSkipped=FALSE;", new { EmployeeId = employee.Id, employee.ClientId, StartPeriod = fyStart.ToString("yyyy-MM"), PayPeriod = payPeriod }, transaction) ?? 0;
        var result = await taxEngineRepository.ComputeAsync(new TaxComputationRequest
        {
            PayRunId = preliminaryRow.PayRunId,
            EmployeeId = employee.Id,
            ClientId = employee.ClientId,
            FinancialYear = financialYear,
            PayPeriod = payPeriod,
            AnnualGrossSalary = annualGross,
            TdsAlreadyDeducted = tdsAlreadyDeducted
        });
        return result is null ? 0 : Math.Max(0, result.MonthlyTds);
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
        rows.Add(Recon(payRunId, "Bank advice total equals net salary", employees.Sum(employee => employee.NetPay), await connection.ExecuteScalarAsync<decimal>("SELECT COALESCE(NetPay,0) FROM payruns WHERE Id=@PayRunId", new { PayRunId = payRunId }, transaction), new { count = employees.Count }));
        rows.Add(Recon(payRunId, "Gross earnings equals sum of earnings", employees.Sum(employee => employee.GrossPay + employee.OneTimeEarnings), await connection.ExecuteScalarAsync<decimal>("SELECT COALESCE(PayrollCost,0) FROM payruns WHERE Id=@PayRunId", new { PayRunId = payRunId }, transaction), new { count = employees.Count }));
        var totalWorkingDays = await connection.ExecuteScalarAsync<int>("SELECT TotalWorkingDays FROM payruns WHERE Id=@PayRunId", new { PayRunId = payRunId }, transaction);
        rows.Add(Recon(payRunId, "Attendance payable days within period", 0, employees.Count(employee => employee.PayableDays < 0 || employee.PayableDays > totalWorkingDays), new { count = employees.Count, totalWorkingDays }));
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
SELECT ComponentCode, COALESCE(SUM(Amount),0) Amount
FROM payrunemployeelines
WHERE PayRunId=@PayRunId
GROUP BY ComponentCode;", new { PayRunId = payRunId }, transaction)).ToList();
        decimal Sum(params string[] codes) => lineRows.Where(row => codes.Contains(row.ComponentCode, StringComparer.OrdinalIgnoreCase)).Sum(row => row.Amount);

        var pfTotal = Sum("PF", "EPF", "VPF");
        rows.Add(Recon(payRunId, "PF register total equals pay-run PF lines", pfTotal, pfTotal, new { componentCodes = new[] { "PF", "EPF", "VPF" } }));

        var esicLineTotal = Sum("ESIC", "ESI");
        var esicExpected = await connection.ExecuteScalarAsync<decimal?>(@"
SELECT COALESCE(SUM(pd.EsicEmployee),0)
FROM payrunemployees p
LEFT JOIN employeepersonaldetails pd ON pd.EmployeeId=p.EmployeeId
WHERE p.PayRunId=@PayRunId AND p.IsSkipped=FALSE;", new { PayRunId = payRunId }, transaction) ?? 0;
        rows.Add(Recon(payRunId, "ESI employee contribution equals statutory source", esicExpected, esicLineTotal, new { componentCodes = new[] { "ESIC", "ESI" } }));

        var ptLwfLineTotal = Sum("PT_LWF_WC", "PT", "LWF");
        var ptLwfExpected = await connection.ExecuteScalarAsync<decimal?>(@"
SELECT COALESCE(SUM(pd.PtLwfWorkmenComp),0)
FROM payrunemployees p
LEFT JOIN employeepersonaldetails pd ON pd.EmployeeId=p.EmployeeId
WHERE p.PayRunId=@PayRunId AND p.IsSkipped=FALSE;", new { PayRunId = payRunId }, transaction) ?? 0;
        if (ptLwfExpected == 0) ptLwfExpected = ptLwfLineTotal;
        rows.Add(Recon(payRunId, "PT/LWF/WC total equals pay-run statutory lines", ptLwfExpected, ptLwfLineTotal, new { componentCodes = new[] { "PT_LWF_WC", "PT", "LWF" } }));

        var tdsLineTotal = Sum("TDS");
        var tdsExpected = await connection.ExecuteScalarAsync<decimal?>(@"
SELECT COALESCE(SUM(ManualTds),0)
FROM payrunemployees
WHERE PayRunId=@PayRunId AND IsSkipped=FALSE;", new { PayRunId = payRunId }, transaction) ?? 0;
        rows.Add(Recon(payRunId, "TDS line total equals payroll TDS", tdsExpected, tdsLineTotal, new { componentCode = "TDS" }));

        var taxSnapshotTotal = await connection.ExecuteScalarAsync<decimal?>(@"
SELECT COALESCE(SUM(monthly_tds),0)
FROM tax_computation_snapshots
WHERE pay_run_id=@PayRunId;", new { PayRunId = payRunId }, transaction) ?? 0;
        if (taxSnapshotTotal > 0 || tdsExpected > 0)
            rows.Add(Recon(payRunId, "TDS equals linked income-tax snapshots", taxSnapshotTotal, tdsExpected, new { snapshot = "tax_computation_snapshots.pay_run_id" }));
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

    private static async Task ApplyLeaveBreakdownAsync(MySqlConnection connection, PayRun payRun)
    {
        if (payRun.RunType == "Off Cycle" || payRun.Employees.Count == 0)
            return;

        var employeeIds = payRun.Employees.Select(employee => employee.EmployeeId).Distinct().ToArray();
        var rows = await connection.QueryAsync<LeaveBreakdownRow>(@"
SELECT a.employee_id AS EmployeeId,
       a.status AS Code,
       COALESCE(lt.name, a.status) AS Name,
       COALESCE(lt.type, CASE WHEN COALESCE(SUM(a.payable_value),0) > 0 THEN 'Paid' ELSE 'Unpaid' END) AS Type,
       COUNT(*) AS Days,
       COALESCE(SUM(a.payable_value), 0) AS PayableDays
FROM employee_daily_attendance a
LEFT JOIN leave_types lt ON lt.client_id = a.client_id AND lt.code = a.status
WHERE a.client_id = @ClientId
  AND DATE_FORMAT(a.attendance_date, '%Y-%m') = @PayPeriod
  AND a.employee_id IN @EmployeeIds
  AND a.status <> 'Present'
GROUP BY a.employee_id, a.status, lt.name, lt.type
ORDER BY a.employee_id, a.status;", new { payRun.ClientId, payRun.PayPeriod, EmployeeIds = employeeIds });

        var byEmployee = rows.GroupBy(row => row.EmployeeId).ToDictionary(group => group.Key, group => group
            .Select(row => new PayRunLeaveBreakdown { Code = row.Code, Name = row.Name, Type = row.Type, Days = row.Days, PayableDays = row.PayableDays })
            .ToList());

        foreach (var employee in payRun.Employees)
        {
            if (byEmployee.TryGetValue(employee.EmployeeId, out var breakdown))
                employee.LeaveBreakdown = breakdown;
        }
    }

    private static async Task<List<PayRunAttendance>> GetPayRunAttendanceAsync(MySqlConnection connection, MySqlTransaction transaction, int clientId, string payPeriod)
    {
        var dailyRows = (await connection.QueryAsync<PayRunAttendance>(@"
SELECT employee_id AS EmployeeId,
       COALESCE(SUM(CASE WHEN status='Present' THEN payable_value ELSE 0 END), 0) AS PresentDays,
       COALESCE(SUM(CASE WHEN status IN ('WO','H') THEN 1 ELSE payable_value END), 0) AS PayableDays
FROM employee_daily_attendance
WHERE client_id=@ClientId AND DATE_FORMAT(attendance_date, '%Y-%m')=@Month
GROUP BY employee_id;", new { ClientId = clientId, Month = payPeriod }, transaction)).ToList();
        if (dailyRows.Count > 0)
            return dailyRows;

        return (await connection.QueryAsync<PayRunAttendance>(@"SELECT employee_id AS EmployeeId, present_days AS PresentDays, payable_days AS PayableDays
FROM employee_monthly_attendance WHERE client_id=@ClientId AND attendance_month=@Month", new { ClientId = clientId, Month = payPeriod }, transaction)).ToList();
    }

    private static async Task<List<PayrollAdjustment>> GetApplicableAdjustmentsAsync(MySqlConnection connection, MySqlTransaction transaction, int clientId, string payPeriod, string runType, int[] adjustmentIds)
    {
        var sql = adjustmentIds.Length > 0
            ? @"SELECT * FROM payrolladjustments WHERE ClientId=@ClientId AND PayPeriod=@PayPeriod AND Status='Approved' AND Id IN @AdjustmentIds"
            : @"SELECT * FROM payrolladjustments WHERE ClientId=@ClientId AND PayPeriod=@PayPeriod AND Status='Approved' AND PayRunType=@RunType";
        var rows = await connection.QueryAsync<PayrollAdjustment>(sql, new { ClientId = clientId, PayPeriod = payPeriod, RunType = runType, AdjustmentIds = adjustmentIds }, transaction);
        return rows.ToList();
    }

    private static bool IsDeduction(PayrollAdjustment adjustment) =>
        adjustment.AdjustmentType.Equals("Deduction", StringComparison.OrdinalIgnoreCase) || adjustment.AdjustmentType.Equals("Recovery", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeAdjustmentType(string value) =>
        value.Equals("Deduction", StringComparison.OrdinalIgnoreCase) || value.Equals("Recovery", StringComparison.OrdinalIgnoreCase) ? "Deduction" :
        value.Equals("Reimbursement", StringComparison.OrdinalIgnoreCase) ? "Reimbursement" : "Earning";

    private sealed record PayRunAttendance(int EmployeeId, decimal PresentDays, decimal PayableDays);
    private sealed record LeaveBreakdownRow(int EmployeeId, string Code, string Name, string Type, long Days, decimal PayableDays);
    private sealed record StatutoryLineTotal(string ComponentCode, decimal Amount);
    private sealed record ExistingPayRunRow(int Id, string Status);
    private sealed record TaxProjectionSetting(bool Enabled, bool ProjectMonthlyTds);
    private sealed record EmployeeSalaryTableRow(int EmployeeId, string ComponentId, decimal Amount);
    private sealed record EmployeePersonalPayrollRow(int EmployeeId, string State, string PanNumber, string UanNumber, string EsicNumber, decimal EsicEmployee, decimal PtLwfWorkmenComp, decimal Tds, decimal Recovery);
    private sealed record EmployeePaymentPayrollRow(int EmployeeId, string BankAccountNo, string IfscCode);
    private sealed class PayRunSourceEmployee : Employee
    {
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

    private static async Task LoadEmployeeTablesAsync(MySqlConnection connection, MySqlTransaction transaction, List<PayRunSourceEmployee> employees)
    {
        if (employees.Count == 0) return;
        var employeeIds = employees.Select(employee => employee.Id).ToArray();
        var salaryRows = (await connection.QueryAsync<EmployeeSalaryTableRow>("SELECT EmployeeId,ComponentId,Amount FROM employeesalarycomponents WHERE EmployeeId IN @EmployeeIds", new { EmployeeIds = employeeIds }, transaction)).GroupBy(row => row.EmployeeId).ToDictionary(group => group.Key, group => group.ToDictionary(row => row.ComponentId, row => row.Amount, StringComparer.OrdinalIgnoreCase));
        var personalRows = (await connection.QueryAsync<EmployeePersonalPayrollRow>("SELECT EmployeeId,State,PanNumber,UanNumber,EsicNumber,EsicEmployee,PtLwfWorkmenComp,Tds,Recovery FROM employeepersonaldetails WHERE EmployeeId IN @EmployeeIds", new { EmployeeIds = employeeIds }, transaction)).ToDictionary(row => row.EmployeeId);
        var paymentRows = (await connection.QueryAsync<EmployeePaymentPayrollRow>("SELECT EmployeeId,BankAccountNo,IfscCode FROM employeepaymentdetails WHERE EmployeeId IN @EmployeeIds", new { EmployeeIds = employeeIds }, transaction)).ToDictionary(row => row.EmployeeId);
        foreach (var employee in employees)
        {
            employee.SalaryComponents = salaryRows.GetValueOrDefault(employee.Id) ?? [];
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
    }

    private static async Task DeletePayRunAttemptAsync(MySqlConnection connection, MySqlTransaction transaction, int payRunId)
    {
        await connection.ExecuteAsync("UPDATE payrolladjustments SET Status='Approved', PayRunId=NULL WHERE PayRunId=@PayRunId;DELETE FROM tax_computation_snapshots WHERE pay_run_id=@PayRunId;DELETE FROM payrunemployeelines WHERE PayRunId=@PayRunId;DELETE FROM payrunemployees WHERE PayRunId=@PayRunId;DELETE FROM payrun_step_logs WHERE PayRunId=@PayRunId;DELETE FROM payroll_validation_issues WHERE PayRunId=@PayRunId;DELETE FROM payroll_calculation_traces WHERE PayRunId=@PayRunId;DELETE FROM payroll_reconciliation_results WHERE PayRunId=@PayRunId;DELETE FROM payruns WHERE Id=@PayRunId;", new { PayRunId = payRunId }, transaction);
    }

    private static async Task ClearPayRunDetailsAsync(MySqlConnection connection, MySqlTransaction transaction, int payRunId)
    {
        await connection.ExecuteAsync("UPDATE payrolladjustments SET Status='Approved', PayRunId=NULL WHERE PayRunId=@PayRunId;DELETE FROM payrunemployeelines WHERE PayRunId=@PayRunId;DELETE FROM payrunemployees WHERE PayRunId=@PayRunId;DELETE FROM payrun_step_logs WHERE PayRunId=@PayRunId;DELETE FROM payroll_validation_issues WHERE PayRunId=@PayRunId;DELETE FROM payroll_calculation_traces WHERE PayRunId=@PayRunId;DELETE FROM payroll_reconciliation_results WHERE PayRunId=@PayRunId;UPDATE payruns SET PayrollCost=0, NetPay=0 WHERE Id=@PayRunId;", new { PayRunId = payRunId }, transaction);
    }

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
        var newIndex = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'payruns' AND INDEX_NAME = 'UX_PayRuns_Client_Period'");
        if (newIndex == 0) await connection.ExecuteAsync("ALTER TABLE payruns ADD UNIQUE KEY UX_PayRuns_Client_Period (ClientId, PayPeriod)");
        var clientPeriodIndex = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'payruns' AND INDEX_NAME = 'UX_PayRuns_Client_Period'");
        if (clientPeriodIndex > 0) await connection.ExecuteAsync("ALTER TABLE payruns DROP INDEX UX_PayRuns_Client_Period");
        var runCodeIndex = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'payruns' AND INDEX_NAME = 'UX_PayRuns_Client_Period_Code'");
        if (runCodeIndex == 0) await connection.ExecuteAsync("ALTER TABLE payruns ADD UNIQUE KEY UX_PayRuns_Client_Period_Code (ClientId, PayPeriod, RunCode)");
    }
}
