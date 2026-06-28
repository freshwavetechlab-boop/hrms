using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using System.Text.RegularExpressions;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public class PayRunRepository(IConfiguration configuration)
{
    private MySqlConnection CreateConnection()
    {
        var connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Connection string 'Default' is not configured.");
        return new MySqlConnection(connectionString);
    }

    public async Task InitializeAsync()
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("USE payroll;");
        await connection.ExecuteAsync(@"
CREATE TABLE IF NOT EXISTS PayRuns (
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
    PayrollCost DECIMAL(18,2) NOT NULL DEFAULT 0,
    NetPay DECIMAL(18,2) NOT NULL DEFAULT 0,
    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    UNIQUE KEY UX_PayRuns_Client_Period_Code (ClientId, PayPeriod, RunCode)
);
CREATE TABLE IF NOT EXISTS PayRunEmployees (
    Id INT PRIMARY KEY AUTO_INCREMENT,
    PayRunId INT NOT NULL,
    EmployeeId INT NOT NULL,
    ClientId INT NOT NULL DEFAULT 0,
    ClientName VARCHAR(250),
    EmployeeCode VARCHAR(50) NOT NULL,
    EmployeeName VARCHAR(250) NOT NULL,
    Department VARCHAR(100),
    PresentDays INT NOT NULL,
    PayableDays INT NOT NULL,
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
CREATE TABLE IF NOT EXISTS PayrollAdjustments (
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
);" );
        await EnsureForeignKeyAsync(connection, "PayRunEmployees", "FK_PayRunEmployees_PayRuns", "FOREIGN KEY (PayRunId) REFERENCES PayRuns(Id) ON DELETE CASCADE");
        await EnsureForeignKeyAsync(connection, "PayrollAdjustments", "FK_PayrollAdjustments_Employees", "FOREIGN KEY (EmployeeId) REFERENCES Employees(Id)");
        await EnsureForeignKeyAsync(connection, "PayrollAdjustments", "FK_PayrollAdjustments_PayRuns", "FOREIGN KEY (PayRunId) REFERENCES PayRuns(Id) ON DELETE SET NULL");
        await EnsureColumnAsync(connection, "PayRunEmployees", "PaymentDate", "DATE NULL");
        await EnsureColumnAsync(connection, "PayRunEmployees", "ClientId", "INT NOT NULL DEFAULT 0");
        await EnsureColumnAsync(connection, "PayRunEmployees", "ClientName", "VARCHAR(250) NULL");
        await EnsureColumnAsync(connection, "PayRunEmployees", "ManualTds", "DECIMAL(18,2) NOT NULL DEFAULT 0");
        await EnsureColumnAsync(connection, "PayRuns", "ClientId", "INT NOT NULL DEFAULT 0");
        await EnsureColumnAsync(connection, "PayRuns", "ClientName", "VARCHAR(250) NULL");
        await EnsureColumnAsync(connection, "PayRuns", "RunCode", "VARCHAR(40) NOT NULL DEFAULT 'REGULAR'");
        await EnsureColumnAsync(connection, "PayRuns", "RunType", "VARCHAR(30) NOT NULL DEFAULT 'Regular'");
        await EnsureColumnAsync(connection, "PayRuns", "RunName", "VARCHAR(120) NOT NULL DEFAULT ''");
        await EnsureColumnAsync(connection, "PayRuns", "Reason", "VARCHAR(500) NOT NULL DEFAULT ''");
        await EnsurePayRunIndexAsync(connection);
        await PayrollDataTableStore.EnsureAsync(connection);
        await connection.ExecuteAsync(@"UPDATE PayRunEmployees p JOIN Employees e ON e.Id = p.EmployeeId LEFT JOIN Clients c ON c.Id = e.ClientId SET p.ClientId = e.ClientId, p.ClientName = c.Name WHERE p.ClientId = 0 OR p.ClientName IS NULL;");
    }

    public async Task<IEnumerable<PayRun>> GetAllAsync()
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("USE payroll;");
        return await connection.QueryAsync<PayRun>(@"
SELECT r.*, COUNT(e.Id) AS EmployeeCount
FROM PayRuns r
LEFT JOIN PayRunEmployees e ON e.PayRunId = r.Id AND e.IsSkipped = FALSE
GROUP BY r.Id
ORDER BY r.PayPeriod DESC, r.Id DESC;");
    }

    public async Task<PayRun?> GetAsync(int id)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("USE payroll;");
        using var results = await connection.QueryMultipleAsync(@"
SELECT r.*, COUNT(e.Id) AS EmployeeCount
FROM PayRuns r LEFT JOIN PayRunEmployees e ON e.PayRunId = r.Id AND e.IsSkipped = FALSE
WHERE r.Id = @Id GROUP BY r.Id;
SELECT * FROM PayRunEmployees WHERE PayRunId = @Id ORDER BY EmployeeName;", new { Id = id });
        var payRun = await results.ReadFirstOrDefaultAsync<PayRun>();
        if (payRun is not null)
        {
            payRun.Employees = (await results.ReadAsync<PayRunEmployee>()).ToList();
            await ApplyLeaveBreakdownAsync(connection, payRun);
            await ApplyPreviousRunComparisonAsync(connection, payRun);
        }
        return payRun;
    }

    public async Task<PayRun?> CreateAsync(CreatePayRunRequest request)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("USE payroll;");
        await using var transaction = await connection.BeginTransactionAsync();
        var client = await connection.QueryFirstOrDefaultAsync<Client>("SELECT * FROM Clients WHERE Id = @Id AND IsActive = TRUE", new { Id = request.ClientId }, transaction);
        if (client is null) return null;
        var runType = string.Equals(request.RunType, "Off Cycle", StringComparison.OrdinalIgnoreCase) ? "Off Cycle" : "Regular";
        var runCode = runType == "Regular" ? "REGULAR" : $"OFF-{DateTime.UtcNow:yyyyMMddHHmmss}";
        var runName = string.IsNullOrWhiteSpace(request.RunName) ? (runType == "Regular" ? "Regular payroll" : "Off-cycle payroll") : request.RunName.Trim();
        var existing = runType == "Regular" ? await connection.ExecuteScalarAsync<int?>("SELECT Id FROM PayRuns WHERE PayPeriod = @PayPeriod AND ClientId = @ClientId AND RunCode = 'REGULAR'", new { request.PayPeriod, request.ClientId }, transaction) : null;
        if (existing is not null)
            return null;

        var payRunId = (int)await connection.ExecuteScalarAsync<long>(@"
INSERT INTO PayRuns (ClientId, ClientName, PayPeriod, RunCode, RunType, RunName, Reason, PayDate, TotalWorkingDays) VALUES (@ClientId, @ClientName, @PayPeriod, @RunCode, @RunType, @RunName, @Reason, @PayDate, @TotalWorkingDays);
SELECT LAST_INSERT_ID();", new { request.ClientId, ClientName = client.Name, request.PayPeriod, RunCode = runCode, RunType = runType, RunName = runName, Reason = request.Reason.Trim(), PayDate = request.PayDate.ToDateTime(TimeOnly.MinValue), request.TotalWorkingDays }, transaction);
        var setupJson = await PayrollDataTableStore.GetSetupJsonAsync(connection, transaction);
        var employees = (await connection.QueryAsync<PayRunSourceEmployee>("SELECT e.*, c.Name AS ClientName FROM Employees e LEFT JOIN Clients c ON c.Id = e.ClientId WHERE e.IsActive = TRUE AND e.ClientId = @ClientId ORDER BY e.FirstName, e.LastName", new { request.ClientId }, transaction)).ToList();
        await LoadEmployeeTablesAsync(connection, transaction, employees);
        var attendance = (await connection.QueryAsync<PayRunAttendance>(@"SELECT employee_id AS EmployeeId, present_days AS PresentDays, payable_days AS PayableDays
FROM employee_monthly_attendance WHERE client_id=@ClientId AND attendance_month=@Month", new { request.ClientId, Month = request.PayPeriod }, transaction)).ToDictionary(row => row.EmployeeId);

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

        foreach (var employee in employees)
        {
            var includeEmployee = includedEmployeeIds.Contains(employee.Id);
            var attendanceRow = attendance.GetValueOrDefault(employee.Id);
            var presentDays = runType == "Off Cycle" ? 0 : attendanceRow is null ? request.TotalWorkingDays : (int)Math.Round(Math.Clamp(attendanceRow.PresentDays, 0, request.TotalWorkingDays), MidpointRounding.AwayFromZero);
            var payableDays = runType == "Off Cycle" ? 0 : attendanceRow is null ? request.TotalWorkingDays : (int)Math.Round(Math.Clamp(attendanceRow.PayableDays, 0, request.TotalWorkingDays), MidpointRounding.AwayFromZero);
            var employeeAdjustments = adjustmentByEmployee.GetValueOrDefault(employee.Id) ?? [];
            var row = BuildEmployee(payRunId, employee, setupJson, request.TotalWorkingDays, presentDays, payableDays, employeeAdjustments, 0, 0, 0, !includeEmployee);
            await SaveEmployeeAsync(connection, transaction, row);
        }
        if (adjustments.Count > 0)
            await connection.ExecuteAsync("UPDATE PayrollAdjustments SET Status = 'Applied', PayRunId = @PayRunId WHERE Id IN @Ids", new { PayRunId = payRunId, Ids = adjustments.Select(item => item.Id).ToArray() }, transaction);

        await RefreshTotalsAsync(connection, transaction, payRunId);
        await transaction.CommitAsync();
        return await GetAsync(payRunId);
    }

    public async Task<PayRunEmployee?> UpdateEmployeeAsync(int payRunId, int employeeId, UpdatePayRunEmployeeRequest request)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("USE payroll;");
        await using var transaction = await connection.BeginTransactionAsync();
        var payRun = await connection.QueryFirstOrDefaultAsync<PayRun>("SELECT * FROM PayRuns WHERE Id = @Id", new { Id = payRunId }, transaction);
        if (payRun is null || payRun.Status != "Draft")
            return null;
        var employee = await connection.QueryFirstOrDefaultAsync<PayRunSourceEmployee>("SELECT e.*, c.Name AS ClientName FROM Employees e LEFT JOIN Clients c ON c.Id = e.ClientId WHERE e.Id = @Id", new { Id = employeeId }, transaction);
        if (employee is null)
            return null;
        await LoadEmployeeTablesAsync(connection, transaction, [employee]);
        var setupJson = await PayrollDataTableStore.GetSetupJsonAsync(connection, transaction);
        var presentDays = Math.Clamp(request.PresentDays, 0, payRun.TotalWorkingDays);
        var row = BuildEmployee(payRunId, employee, setupJson, payRun.TotalWorkingDays, presentDays, presentDays, [], Math.Max(0, request.OneTimeEarnings), Math.Max(0, request.OneTimeDeductions), Math.Max(0, request.ManualTds), request.IsSkipped);
        await SaveEmployeeAsync(connection, transaction, row);
        await RefreshTotalsAsync(connection, transaction, payRunId);
        await transaction.CommitAsync();
        return row;
    }

    public async Task<IEnumerable<PayrollAdjustment>> GetAdjustmentsAsync(int? clientId, string? payPeriod, string? status)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("USE payroll;");
        return await connection.QueryAsync<PayrollAdjustment>(@"
SELECT a.*, CONCAT(e.FirstName, ' ', e.LastName) AS EmployeeName, e.EmployeeCode
FROM PayrollAdjustments a
JOIN Employees e ON e.Id = a.EmployeeId
WHERE (@ClientId IS NULL OR a.ClientId = @ClientId)
  AND (@PayPeriod IS NULL OR a.PayPeriod = @PayPeriod)
  AND (@Status IS NULL OR a.Status = @Status)
ORDER BY a.PayPeriod DESC, a.CreatedAt DESC;", new { ClientId = clientId, PayPeriod = string.IsNullOrWhiteSpace(payPeriod) ? null : payPeriod, Status = string.IsNullOrWhiteSpace(status) ? null : status });
    }

    public async Task<PayrollAdjustment?> SaveAdjustmentAsync(PayrollAdjustment adjustment)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("USE payroll;");
        var employee = await connection.QueryFirstOrDefaultAsync<Employee>("SELECT * FROM Employees WHERE Id = @Id AND ClientId = @ClientId AND IsActive = TRUE", new { Id = adjustment.EmployeeId, adjustment.ClientId });
        if (employee is null || adjustment.Amount <= 0 || string.IsNullOrWhiteSpace(adjustment.PayPeriod)) return null;
        adjustment.EmployeeName = $"{employee.FirstName} {employee.LastName}".Trim();
        adjustment.EmployeeCode = employee.EmployeeCode;
        adjustment.AdjustmentType = NormalizeAdjustmentType(adjustment.AdjustmentType);
        adjustment.PayRunType = string.Equals(adjustment.PayRunType, "Off Cycle", StringComparison.OrdinalIgnoreCase) ? "Off Cycle" : "Regular";
        adjustment.Status = string.IsNullOrWhiteSpace(adjustment.Status) ? "Approved" : adjustment.Status;
        if (adjustment.Id == 0)
        {
            var id = (int)await connection.ExecuteScalarAsync<long>(@"
INSERT INTO PayrollAdjustments (ClientId, EmployeeId, EmployeeName, EmployeeCode, ComponentId, ComponentCode, ComponentName, AdjustmentType, Amount, PayPeriod, PayRunType, ReasonCode, Notes, Taxable, Status)
VALUES (@ClientId, @EmployeeId, @EmployeeName, @EmployeeCode, @ComponentId, @ComponentCode, @ComponentName, @AdjustmentType, @Amount, @PayPeriod, @PayRunType, @ReasonCode, @Notes, @Taxable, @Status);
SELECT LAST_INSERT_ID();", adjustment);
            adjustment.Id = id;
        }
        else
        {
            var rows = await connection.ExecuteAsync(@"
UPDATE PayrollAdjustments SET ClientId=@ClientId, EmployeeId=@EmployeeId, EmployeeName=@EmployeeName, EmployeeCode=@EmployeeCode, ComponentId=@ComponentId, ComponentCode=@ComponentCode, ComponentName=@ComponentName, AdjustmentType=@AdjustmentType, Amount=@Amount, PayPeriod=@PayPeriod, PayRunType=@PayRunType, ReasonCode=@ReasonCode, Notes=@Notes, Taxable=@Taxable, Status=@Status
WHERE Id=@Id AND Status != 'Applied';", adjustment);
            if (rows == 0) return null;
        }
        return adjustment;
    }

    public async Task<bool> CancelAdjustmentAsync(int id)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("USE payroll;");
        return await connection.ExecuteAsync("UPDATE PayrollAdjustments SET Status = 'Cancelled' WHERE Id = @Id AND Status != 'Applied'", new { Id = id }) == 1;
    }

    public async Task<PayRun?> SubmitForApprovalAsync(int id)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("USE payroll;");
        var rows = await connection.ExecuteAsync("UPDATE PayRuns SET Status = 'Pending Approval' WHERE Id = @Id AND Status = 'Draft'", new { Id = id });
        return rows == 1 ? await GetAsync(id) : null;
    }

    public async Task<PayRun?> ApproveAsync(int id)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("USE payroll;");
        var rows = await connection.ExecuteAsync("UPDATE PayRuns SET Status = 'Approved' WHERE Id = @Id AND Status IN ('Draft', 'Pending Approval')", new { Id = id });
        return rows == 1 ? await GetAsync(id) : null;
    }

    public async Task<bool> DeleteDraftAsync(int id)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("USE payroll;");
        return await connection.ExecuteAsync("DELETE FROM PayRuns WHERE Id = @Id AND Status = 'Draft'", new { Id = id }) == 1;
    }

    public async Task<PayRun?> RecallAsync(int id)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("USE payroll;");
        var rows = await connection.ExecuteAsync(@"UPDATE PayRuns SET Status = 'Draft' WHERE Id = @Id AND Status IN ('Approved', 'Pending Approval') AND NOT EXISTS (SELECT 1 FROM PayRunEmployees WHERE PayRunId = @Id AND PaymentStatus = 'Paid')", new { Id = id });
        return rows == 1 ? await GetAsync(id) : null;
    }

    public async Task<PayRun?> RecordPaymentsAsync(int id, RecordPaymentRequest request)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("USE payroll;");
        await using var transaction = await connection.BeginTransactionAsync();
        var payRun = await connection.QueryFirstOrDefaultAsync<PayRun>("SELECT * FROM PayRuns WHERE Id = @Id", new { Id = id }, transaction);
        if (payRun is null || payRun.Status is not ("Approved" or "Partially Paid")) return null;
        var employeeIds = request.EmployeeIds.Distinct().ToArray();
        if (employeeIds.Length == 0)
            employeeIds = (await connection.QueryAsync<int>("SELECT EmployeeId FROM PayRunEmployees WHERE PayRunId = @Id AND IsSkipped = FALSE AND PaymentStatus != 'Paid'", new { Id = id }, transaction)).ToArray();
        if (employeeIds.Length == 0) return null;
        await connection.ExecuteAsync("UPDATE PayRunEmployees SET PaymentStatus = 'Paid', PaymentDate = @PaymentDate WHERE PayRunId = @Id AND IsSkipped = FALSE AND EmployeeId IN @EmployeeIds", new { Id = id, PaymentDate = request.PaymentDate.ToDateTime(TimeOnly.MinValue), EmployeeIds = employeeIds }, transaction);
        var pending = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM PayRunEmployees WHERE PayRunId = @Id AND IsSkipped = FALSE AND PaymentStatus != 'Paid'", new { Id = id }, transaction);
        await connection.ExecuteAsync("UPDATE PayRuns SET Status = @Status WHERE Id = @Id", new { Id = id, Status = pending == 0 ? "Paid" : "Partially Paid" }, transaction);
        await transaction.CommitAsync();
        return await GetAsync(id);
    }

    private static PayRunEmployee BuildEmployee(int payRunId, PayRunSourceEmployee employee, string setupJson, int totalWorkingDays, int presentDays, int payableDays, IEnumerable<PayrollAdjustment> adjustments, decimal manualOneTimeEarnings, decimal manualOneTimeDeductions, decimal manualTds, bool isSkipped)
    {
        var setup = ReadPayrollSetup(setupJson);
        var salary = CalculateConfiguredSalary(employee, setup, totalWorkingDays, presentDays, payableDays);
        var salaryDeductionCodes = salary.Where(row => IsDeductionCategory(row.Component.Category)).Select(row => row.Component.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
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

        foreach (var seededDeduction in ReadSeededPayrollDeductions(employee))
        {
            if (salaryDeductionCodes.Contains(seededDeduction.Code)) continue;
            var amount = isSkipped ? 0 : seededDeduction.Amount;
            if (amount <= 0) continue;
            deductions += amount;
            lines.Add(new { Id = seededDeduction.Code, Name = seededDeduction.Name, Category = "Deduction", monthlyAmount = seededDeduction.Amount, amount, ProRata = false });
        }

        var adjustmentRows = adjustments.ToList();
        var oneTimeEarnings = isSkipped ? 0 : manualOneTimeEarnings + adjustmentRows.Where(item => !IsDeduction(item)).Sum(item => item.Amount);
        var oneTimeDeductions = isSkipped ? 0 : manualOneTimeDeductions + adjustmentRows.Where(IsDeduction).Sum(item => item.Amount);
        var tds = isSkipped ? 0 : manualTds;
        if (tds > 0)
        {
            deductions += tds;
            lines.Add(new { Id = "TDS", Name = "Manual TDS", Category = "Deduction", monthlyAmount = tds, amount = tds, ProRata = false });
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

    private static List<CalculatedPayrollComponent> CalculateConfiguredSalary(PayRunSourceEmployee employee, PayrollSetupData setup, int payrollDays, int presentDays, int payableDays)
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

    private static List<CalculatedPayrollComponent> CalculateStructureLines(List<PayrollComponent> components, SalaryStructureSetup structure, decimal monthlyTarget, int payrollDays, int presentDays, int payableDays)
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

    private static decimal EvaluateComponentFormula(PayrollComponent component, string source, decimal monthlyTarget, int payrollDays, int presentDays, int payableDays, Dictionary<string, decimal> values)
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

    private static decimal EvaluateFormula(string source, decimal monthlyTarget, int payrollDays, int presentDays, int payableDays, Dictionary<string, decimal> values)
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
            return new PayrollSetupData(components, structures);
        }
        catch
        {
            return new PayrollSetupData([], []);
        }
    }

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

    private sealed record PayrollSetupData(List<PayrollComponent> Components, List<SalaryStructureSetup> Structures);
    private sealed record SalaryStructureSetup(string Id, string ClientId, string AnnualCtc, List<SalaryStructureLine> Lines);
    private sealed record SalaryStructureLine(string ComponentId, string Value);
    private sealed record PayrollComponent(string Id, string Code, string Name, string Category, string CalculationType, string Value, string Formula, string BaseComponent, bool ProRata, bool Active, int Priority, string PayType);
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
INSERT INTO PayRunEmployees (PayRunId, EmployeeId, ClientId, ClientName, EmployeeCode, EmployeeName, Department, PresentDays, PayableDays, MonthlyGross, GrossPay, StatutoryDeductions, OneTimeEarnings, OneTimeDeductions, ManualTds, NetPay, IsSkipped, DetailsJson)
VALUES (@PayRunId, @EmployeeId, @ClientId, @ClientName, @EmployeeCode, @EmployeeName, @Department, @PresentDays, @PayableDays, @MonthlyGross, @GrossPay, @StatutoryDeductions, @OneTimeEarnings, @OneTimeDeductions, @ManualTds, @NetPay, @IsSkipped, @DetailsJson)
ON DUPLICATE KEY UPDATE Id=LAST_INSERT_ID(Id), ClientId=@ClientId, ClientName=@ClientName, EmployeeCode=@EmployeeCode, EmployeeName=@EmployeeName, Department=@Department, PresentDays=@PresentDays, PayableDays=@PayableDays, MonthlyGross=@MonthlyGross, GrossPay=@GrossPay, StatutoryDeductions=@StatutoryDeductions, OneTimeEarnings=@OneTimeEarnings, OneTimeDeductions=@OneTimeDeductions, ManualTds=@ManualTds, NetPay=@NetPay, IsSkipped=@IsSkipped, DetailsJson=@DetailsJson;
SELECT LAST_INSERT_ID();", row, transaction);
        await PayrollDataTableStore.SyncPayRunEmployeeLinesAsync(connection, transaction, row);
    }

    private static Task RefreshTotalsAsync(MySqlConnection connection, MySqlTransaction transaction, int payRunId) =>
        connection.ExecuteAsync(@"
UPDATE PayRuns r
JOIN (SELECT PayRunId, COALESCE(SUM(GrossPay + OneTimeEarnings), 0) AS PayrollCost, COALESCE(SUM(NetPay), 0) AS NetPay FROM PayRunEmployees WHERE PayRunId = @PayRunId GROUP BY PayRunId) e ON e.PayRunId = r.Id
SET r.PayrollCost = e.PayrollCost, r.NetPay = e.NetPay
WHERE r.Id = @PayRunId;", new { PayRunId = payRunId }, transaction);

    private static async Task ApplyPreviousRunComparisonAsync(MySqlConnection connection, PayRun payRun)
    {
        var previousRunId = await connection.ExecuteScalarAsync<int?>(@"
SELECT Id FROM PayRuns
WHERE ClientId = @ClientId AND PayPeriod < @PayPeriod
ORDER BY PayPeriod DESC
LIMIT 1;", new { payRun.ClientId, payRun.PayPeriod });
        if (previousRunId is null)
        {
            foreach (var employee in payRun.Employees)
                employee.VarianceReason = employee.IsSkipped ? "Excluded from current run" : "First payroll comparison period";
            return;
        }

        var previousRows = await connection.QueryAsync<PayRunEmployee>("SELECT * FROM PayRunEmployees WHERE PayRunId = @PreviousRunId", new { PreviousRunId = previousRunId.Value });
        var previousByEmployee = previousRows.ToDictionary(employee => employee.EmployeeId);
        foreach (var employee in payRun.Employees)
        {
            if (!previousByEmployee.TryGetValue(employee.EmployeeId, out var previous))
            {
                employee.VarianceReason = employee.IsSkipped ? "Excluded from current run" : "New in current run";
                continue;
            }

            employee.PreviousNetPay = previous.NetPay;
            employee.NetPayVariance = employee.NetPay - previous.NetPay;
            employee.VariancePercent = previous.NetPay == 0 ? null : decimal.Round((employee.NetPay - previous.NetPay) / previous.NetPay * 100, 2);
            employee.VarianceReason = employee.IsSkipped ? "Excluded from current run" :
                employee.NetPay == previous.NetPay ? "No change" :
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
       COALESCE(lt.type, CASE WHEN a.payable_value > 0 THEN 'Paid' ELSE 'Unpaid' END) AS Type,
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

    private static async Task<List<PayrollAdjustment>> GetApplicableAdjustmentsAsync(MySqlConnection connection, MySqlTransaction transaction, int clientId, string payPeriod, string runType, int[] adjustmentIds)
    {
        var sql = adjustmentIds.Length > 0
            ? @"SELECT * FROM PayrollAdjustments WHERE ClientId=@ClientId AND PayPeriod=@PayPeriod AND Status='Approved' AND Id IN @AdjustmentIds"
            : @"SELECT * FROM PayrollAdjustments WHERE ClientId=@ClientId AND PayPeriod=@PayPeriod AND Status='Approved' AND PayRunType=@RunType";
        var rows = await connection.QueryAsync<PayrollAdjustment>(sql, new { ClientId = clientId, PayPeriod = payPeriod, RunType = runType, AdjustmentIds = adjustmentIds }, transaction);
        return rows.ToList();
    }

    private static bool IsDeduction(PayrollAdjustment adjustment) =>
        adjustment.AdjustmentType.Equals("Deduction", StringComparison.OrdinalIgnoreCase) || adjustment.AdjustmentType.Equals("Recovery", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeAdjustmentType(string value) =>
        value.Equals("Deduction", StringComparison.OrdinalIgnoreCase) || value.Equals("Recovery", StringComparison.OrdinalIgnoreCase) ? "Deduction" :
        value.Equals("Reimbursement", StringComparison.OrdinalIgnoreCase) ? "Reimbursement" : "Earning";

    private sealed record PayRunAttendance(int EmployeeId, decimal PresentDays, decimal PayableDays);
    private sealed record LeaveBreakdownRow(int EmployeeId, string Code, string Name, string Type, decimal Days, decimal PayableDays);
    private sealed record EmployeeSalaryTableRow(int EmployeeId, string ComponentId, decimal Amount);
    private sealed record EmployeePersonalPayrollRow(int EmployeeId, decimal EsicEmployee, decimal PtLwfWorkmenComp, decimal Tds, decimal Recovery);
    private sealed class PayRunSourceEmployee : Employee
    {
        public string ClientName { get; set; } = string.Empty;
        public Dictionary<string, decimal> SalaryComponents { get; set; } = [];
        public bool HasPersonalDetails { get; set; }
        public decimal EsicEmployee { get; set; }
        public decimal PtLwfWorkmenComp { get; set; }
        public decimal Tds { get; set; }
        public decimal Recovery { get; set; }
    }

    private static async Task LoadEmployeeTablesAsync(MySqlConnection connection, MySqlTransaction transaction, List<PayRunSourceEmployee> employees)
    {
        if (employees.Count == 0) return;
        var employeeIds = employees.Select(employee => employee.Id).ToArray();
        var salaryRows = (await connection.QueryAsync<EmployeeSalaryTableRow>("SELECT EmployeeId,ComponentId,Amount FROM EmployeeSalaryComponents WHERE EmployeeId IN @EmployeeIds", new { EmployeeIds = employeeIds }, transaction)).GroupBy(row => row.EmployeeId).ToDictionary(group => group.Key, group => group.ToDictionary(row => row.ComponentId, row => row.Amount, StringComparer.OrdinalIgnoreCase));
        var personalRows = (await connection.QueryAsync<EmployeePersonalPayrollRow>("SELECT EmployeeId,EsicEmployee,PtLwfWorkmenComp,Tds,Recovery FROM EmployeePersonalDetails WHERE EmployeeId IN @EmployeeIds", new { EmployeeIds = employeeIds }, transaction)).ToDictionary(row => row.EmployeeId);
        foreach (var employee in employees)
        {
            employee.SalaryComponents = salaryRows.GetValueOrDefault(employee.Id) ?? [];
            if (!personalRows.TryGetValue(employee.Id, out var personal)) continue;
            employee.HasPersonalDetails = true;
            employee.EsicEmployee = personal.EsicEmployee;
            employee.PtLwfWorkmenComp = personal.PtLwfWorkmenComp;
            employee.Tds = personal.Tds;
            employee.Recovery = personal.Recovery;
        }
    }

    private static async Task EnsureColumnAsync(MySqlConnection connection, string tableName, string columnName, string definition)
    {
        var exists = await connection.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'payroll' AND TABLE_NAME = @TableName AND COLUMN_NAME = @ColumnName", new { TableName = tableName, ColumnName = columnName });
        if (exists == 0) await connection.ExecuteAsync($"ALTER TABLE `{tableName}` ADD COLUMN `{columnName}` {definition}");
    }

    private static async Task EnsureForeignKeyAsync(MySqlConnection connection, string tableName, string constraintName, string definition)
    {
        var exists = await connection.ExecuteScalarAsync<int>(@"
SELECT COUNT(*)
FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS
WHERE CONSTRAINT_SCHEMA = 'payroll'
  AND CONSTRAINT_NAME = @ConstraintName;", new { ConstraintName = constraintName });

        if (exists == 0)
            await connection.ExecuteAsync($"ALTER TABLE `{tableName}` ADD CONSTRAINT `{constraintName}` {definition}");
    }

    private static async Task EnsurePayRunIndexAsync(MySqlConnection connection)
    {
        var oldIndex = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA = 'payroll' AND TABLE_NAME = 'PayRuns' AND INDEX_NAME = 'UX_PayRuns_PayPeriod'");
        if (oldIndex > 0) await connection.ExecuteAsync("ALTER TABLE PayRuns DROP INDEX UX_PayRuns_PayPeriod");
        var newIndex = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA = 'payroll' AND TABLE_NAME = 'PayRuns' AND INDEX_NAME = 'UX_PayRuns_Client_Period'");
        if (newIndex == 0) await connection.ExecuteAsync("ALTER TABLE PayRuns ADD UNIQUE KEY UX_PayRuns_Client_Period (ClientId, PayPeriod)");
        var clientPeriodIndex = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA = 'payroll' AND TABLE_NAME = 'PayRuns' AND INDEX_NAME = 'UX_PayRuns_Client_Period'");
        if (clientPeriodIndex > 0) await connection.ExecuteAsync("ALTER TABLE PayRuns DROP INDEX UX_PayRuns_Client_Period");
        var runCodeIndex = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA = 'payroll' AND TABLE_NAME = 'PayRuns' AND INDEX_NAME = 'UX_PayRuns_Client_Period_Code'");
        if (runCodeIndex == 0) await connection.ExecuteAsync("ALTER TABLE PayRuns ADD UNIQUE KEY UX_PayRuns_Client_Period_Code (ClientId, PayPeriod, RunCode)");
    }
}
