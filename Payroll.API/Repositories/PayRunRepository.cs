using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
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
        var setupJson = await connection.ExecuteScalarAsync<string?>("SELECT SetupJson FROM PayrollSetups ORDER BY Id LIMIT 1", transaction: transaction) ?? "{}";
        var employees = await connection.QueryAsync<PayRunSourceEmployee>("SELECT e.*, c.Name AS ClientName FROM Employees e LEFT JOIN Clients c ON c.Id = e.ClientId WHERE e.IsActive = TRUE AND e.ClientId = @ClientId ORDER BY e.FirstName, e.LastName", new { request.ClientId }, transaction);
        var attendance = (await connection.QueryAsync<PayRunAttendance>(@"SELECT employee_id AS EmployeeId, present_days AS PresentDays, payable_days AS PayableDays
FROM employee_monthly_attendance WHERE client_id=@ClientId AND attendance_month=@Month", new { request.ClientId, Month = request.PayPeriod }, transaction)).ToDictionary(row => row.EmployeeId);

        var selectedAdjustmentIds = request.AdjustmentIds.Distinct().ToArray();
        var adjustments = await GetApplicableAdjustmentsAsync(connection, transaction, request.ClientId, request.PayPeriod, runType, selectedAdjustmentIds);
        var adjustmentByEmployee = adjustments.GroupBy(item => item.EmployeeId).ToDictionary(group => group.Key, group => new AdjustmentTotal(group.Sum(item => IsDeduction(item) ? 0 : item.Amount), group.Sum(item => IsDeduction(item) ? item.Amount : 0)));
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
            var adjustment = adjustmentByEmployee.GetValueOrDefault(employee.Id);
            var row = BuildEmployee(payRunId, employee, setupJson, request.TotalWorkingDays, presentDays, payableDays, adjustment.Earnings, adjustment.Deductions, 0, !includeEmployee);
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
        var setupJson = await connection.ExecuteScalarAsync<string?>("SELECT SetupJson FROM PayrollSetups ORDER BY Id LIMIT 1", transaction: transaction) ?? "{}";
        var presentDays = Math.Clamp(request.PresentDays, 0, payRun.TotalWorkingDays);
        var row = BuildEmployee(payRunId, employee, setupJson, payRun.TotalWorkingDays, presentDays, presentDays, Math.Max(0, request.OneTimeEarnings), Math.Max(0, request.OneTimeDeductions), Math.Max(0, request.ManualTds), request.IsSkipped);
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

    private static PayRunEmployee BuildEmployee(int payRunId, PayRunSourceEmployee employee, string setupJson, int totalWorkingDays, int presentDays, int payableDays, decimal oneTimeEarnings, decimal oneTimeDeductions, decimal manualTds, bool isSkipped)
    {
        if (employee.ClientId == 6) return BuildReclEmployee(payRunId, employee, totalWorkingDays, payableDays, oneTimeEarnings, oneTimeDeductions, manualTds, isSkipped);
        var components = ReadComponents(setupJson);
        var salary = CalculateSalaryFromSetup(setupJson, employee);
        var employeeSalary = JsonSerializer.Deserialize<Dictionary<string, decimal>>(employee.SalaryJson, new JsonSerializerOptions { NumberHandling = JsonNumberHandling.AllowReadingFromString }) ?? [];
        foreach (var item in employeeSalary.Where(item => item.Value != 0))
            salary[item.Key] = item.Value;
        if (salary.Count == 0 && employee.AnnualCtc > 0)
        {
            var fallback = components.Values.FirstOrDefault(component => component.Code.Equals("BASIC", StringComparison.OrdinalIgnoreCase) && component.Active)
                ?? components.Values.FirstOrDefault(component => component.Category.Equals("Earning", StringComparison.OrdinalIgnoreCase) && component.Active);
            salary[fallback?.Id ?? "GROSS"] = decimal.Round(employee.AnnualCtc / 12m, 2);
        }
        var factor = totalWorkingDays == 0 ? 0 : (decimal)payableDays / totalWorkingDays;
        var lines = new List<object>();
        decimal monthlyGross = 0, grossPay = 0, deductions = 0;

        foreach (var (id, amount) in salary)
        {
            var component = components.GetValueOrDefault(id, new PayrollComponent(id, id, "Component", "Earning", true, true, "Flat Amount", "", "", "", 100));
            var calculatedAmount = component.ProRata ? decimal.Round(amount * factor, 2) : amount;
            lines.Add(new { component.Id, component.Name, component.Category, monthlyAmount = amount, amount = calculatedAmount, component.ProRata });
            if (component.Category.Equals("Deduction", StringComparison.OrdinalIgnoreCase))
                deductions += calculatedAmount;
            else
            {
                monthlyGross += amount;
                grossPay += calculatedAmount;
            }
        }

        if (isSkipped)
            grossPay = deductions = oneTimeEarnings = oneTimeDeductions = 0;
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
            NetPay = Math.Max(0, grossPay + oneTimeEarnings - deductions - oneTimeDeductions),
            IsSkipped = isSkipped,
            DetailsJson = JsonSerializer.Serialize(lines)
        };
    }

    private static PayRunEmployee BuildReclEmployee(int payRunId, PayRunSourceEmployee employee, int payrollDays, int payableDays, decimal taDa, decimal recovery, decimal manualTds, bool isSkipped)
    {
        var salary = JsonSerializer.Deserialize<Dictionary<string, decimal>>(employee.SalaryJson, new JsonSerializerOptions { NumberHandling = JsonNumberHandling.AllowReadingFromString }) ?? [];
        var gross = salary.TryGetValue("GROSS", out var sourceGross) ? sourceGross : new[] { "401", "402", "403", "404", "405", "406", "407" }.Sum(key => salary.GetValueOrDefault(key));
        if (gross <= 0 && employee.AnnualCtc > 0) gross = decimal.Round(employee.AnnualCtc / 12m, 2);
        var basic = decimal.Round(gross * .50m, 2);
        var hra = decimal.Round(basic * .40m, 2);
        var telephonic = 2000m;
        var bonus = decimal.Floor(basic * .0833m);
        var medical = 1250m;
        var other = gross - (basic + hra + bonus + telephonic + medical);
        var laptop = isSkipped ? 0m : 2000m;
        var factor = payrollDays == 0 || isSkipped ? 0 : (decimal)payableDays / payrollDays;
        var basicEarned = decimal.Round(basic * factor, 2);
        var hraEarned = decimal.Round(hra * factor, 2);
        var telephonicEarned = decimal.Round(telephonic * factor, 2);
        var bonusEarned = decimal.Round(bonus * factor, 2);
        var medicalEarned = decimal.Round(medical * factor, 2);
        var otherEarned = decimal.Round(other * factor, 2);
        var grossEarned = basicEarned + hraEarned + telephonicEarned + bonusEarned + medicalEarned + otherEarned + laptop + (isSkipped ? 0 : taDa);
        var pf = Math.Min(decimal.Round(basicEarned * .12m, 2), 1800m);
        var pt = grossEarned < 6000 ? 0m : grossEarned < 9000 ? 80m : grossEarned < 12000 ? 150m : 200m;
        var tds = isSkipped ? 0m : manualTds;
        var employerPf = pf;
        var pfAdmin = decimal.Round(employerPf * .0004167m, 2);
        var edli = decimal.Round(employerPf * .0004167m, 2);
        var employerCost = employerPf + pfAdmin + edli;
        var deductions = pf + pt + tds;
        var net = Math.Max(0, grossEarned - deductions - (isSkipped ? 0 : recovery));
        var lines = new[] { ("BASIC", "Basic", "Earning", basic), ("HRA", "HRA", "Earning", hra), ("TA_DA", "TA / DA", "Earning", taDa), ("BASIC_EARNED", "Basic Earned", "Earning", basicEarned), ("HRA_EARNED", "HRA Earned", "Earning", hraEarned), ("TELEPHONIC_EARNED", "Telephonic Earned", "Earning", telephonicEarned), ("BONUS_EARNED", "Bonus Earned", "Earning", bonusEarned), ("MEDICAL_EARNED", "Medical Earned", "Earning", medicalEarned), ("OTHER_ALLOWANCE_EARNED", "Other Allowance Earned", "Earning", otherEarned), ("LAPTOP_ALLOWANCE", "Laptop Allowance", "Earning", laptop), ("EMPLOYEE_PF", "Employee PF", "Deduction", pf), ("ESIC", "ESIC", "Deduction", 0m), ("PT", "Professional Tax", "Deduction", pt), ("TDS", "TDS", "Deduction", tds), ("RECOVERY", "Recovery", "Deduction", recovery), ("GROSS_EARNED", "Gross Earned", "Summary", grossEarned), ("NET_PAY", "Net Pay", "Summary", net), ("EMPLOYER_COST", "Employer Cost", "Employer Contribution", employerCost) }.Select(line => new { Id = line.Item1, Name = line.Item2, Category = line.Item3, monthlyAmount = line.Item4, amount = line.Item4, ProRata = line.Item1.Contains("EARNED") }).ToList();
        return new PayRunEmployee { PayRunId = payRunId, EmployeeId = employee.Id, ClientId = employee.ClientId, ClientName = employee.ClientName, EmployeeCode = employee.EmployeeCode, EmployeeName = $"{employee.FirstName} {employee.LastName}".Trim(), Department = employee.Department, PresentDays = payableDays, PayableDays = isSkipped ? 0 : payableDays, MonthlyGross = gross + laptop, GrossPay = grossEarned, StatutoryDeductions = deductions, OneTimeEarnings = isSkipped ? 0 : taDa, OneTimeDeductions = isSkipped ? 0 : recovery, ManualTds = tds, NetPay = net, IsSkipped = isSkipped, DetailsJson = JsonSerializer.Serialize(lines) };
    }

    private static Dictionary<string, PayrollComponent> ReadComponents(string setupJson)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(setupJson) ? "{}" : setupJson);
        if (!document.RootElement.TryGetProperty("salaryComponents", out var components) || components.ValueKind != JsonValueKind.Array)
            return [];
        return components.EnumerateArray()
            .Where(component => component.TryGetProperty("id", out _))
            .Select(component => new PayrollComponent(
                component.GetProperty("id").ToString(),
                component.TryGetProperty("code", out var code) ? code.GetString() ?? component.GetProperty("id").ToString() : component.GetProperty("id").ToString(),
                component.TryGetProperty("name", out var name) ? name.GetString() ?? "Component" : "Component",
                component.TryGetProperty("category", out var category) ? category.GetString() ?? "Earning" : "Earning",
                !component.TryGetProperty("proRata", out var proRata) || proRata.GetBoolean(),
                !component.TryGetProperty("active", out var active) || active.GetBoolean(),
                component.TryGetProperty("calculationType", out var calculationType) ? calculationType.GetString() ?? "Flat Amount" : "Flat Amount",
                component.TryGetProperty("value", out var value) ? value.GetString() ?? "" : "",
                component.TryGetProperty("formula", out var formula) ? formula.GetString() ?? "" : "",
                component.TryGetProperty("baseComponent", out var baseComponent) ? baseComponent.GetString() ?? "" : "",
                int.TryParse(component.TryGetProperty("priority", out var priority) ? priority.GetString() : "", out var order) ? order : 100))
            .ToDictionary(component => component.Id);
    }

    private static Dictionary<string, decimal> CalculateSalaryFromSetup(string setupJson, PayRunSourceEmployee employee)
    {
        if (employee.AnnualCtc <= 0) return [];
        var components = ReadComponents(setupJson);
        if (components.Count == 0) return [];
        var lines = ReadSalaryLines(setupJson, employee, components.Values).ToList();
        var monthlyCtc = employee.AnnualCtc / 12m;
        var values = new Dictionary<string, decimal>();
        var byCode = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
        {
            ["CTC"] = monthlyCtc,
            ["GROSS"] = monthlyCtc,
            ["MONTHLY_CTC"] = monthlyCtc,
            ["ANNUAL_CTC"] = employee.AnnualCtc,
            ["PAYROLL_DAYS"] = 30,
            ["PAYABLE_DAYS"] = 30,
            ["PRESENT_DAYS"] = 30,
            ["LOP_DAYS"] = 0
        };

        foreach (var line in lines)
        {
            if (!components.TryGetValue(line.ComponentId, out var component) || !component.Active) continue;
            var source = FirstText(line.Value, component.Formula, component.Value);
            var amount = CalculateComponentAmount(component, source, monthlyCtc, values, components, byCode);
            values[component.Id] = amount;
            byCode[component.Code] = amount;
        }
        return values;
    }

    private static IEnumerable<SalaryLine> ReadSalaryLines(string setupJson, PayRunSourceEmployee employee, IEnumerable<PayrollComponent> components)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(setupJson) ? "{}" : setupJson);
        if (document.RootElement.TryGetProperty("salaryStructures", out var structures) && structures.ValueKind == JsonValueKind.Array)
        {
            foreach (var structure in structures.EnumerateArray())
            {
                var structureId = structure.TryGetProperty("id", out var id) ? id.ToString() : "";
                var client = structure.TryGetProperty("clientId", out var clientId) ? clientId.GetString() ?? "" : "";
                if ((!string.IsNullOrWhiteSpace(employee.SalaryStructureId) && structureId == employee.SalaryStructureId)
                    || (!string.IsNullOrWhiteSpace(client) && client.Split(':')[0] == employee.ClientId.ToString()))
                {
                    if (structure.TryGetProperty("lines", out var lines) && lines.ValueKind == JsonValueKind.Array)
                        return lines.EnumerateArray().Select(line => new SalaryLine(
                            line.TryGetProperty("componentId", out var componentId) ? componentId.ToString() : "",
                            line.TryGetProperty("value", out var value) ? value.GetString() ?? "" : ""))
                            .Where(line => !string.IsNullOrWhiteSpace(line.ComponentId));
                }
            }
        }
        return components
            .Where(component => component.Active && component.Category is "Earning" or "Deduction" or "Reimbursement")
            .OrderBy(component => component.Priority)
            .ThenBy(component => component.Name)
            .Select(component => new SalaryLine(component.Id, FirstText(component.Formula, component.Value)));
    }

    private static decimal CalculateComponentAmount(PayrollComponent component, string source, decimal monthlyCtc, Dictionary<string, decimal> values, Dictionary<string, PayrollComponent> components, Dictionary<string, decimal> byCode)
    {
        if (component.CalculationType.Equals("Percentage of CTC", StringComparison.OrdinalIgnoreCase))
        {
            var percent = ReadNumber(FirstText(source, component.Value));
            return percent > 0 && !source.Contains("CTC", StringComparison.OrdinalIgnoreCase)
                ? decimal.Round(monthlyCtc * percent / 100m, 2)
                : decimal.Round(EvaluateFormula(source, byCode), 2);
        }
        if (component.CalculationType.Equals("Percentage of Component", StringComparison.OrdinalIgnoreCase))
        {
            if (HasFormula(source)) return decimal.Round(EvaluateFormula(source, byCode), 2);
            var baseAmount = byCode.GetValueOrDefault(component.BaseComponent);
            return decimal.Round(baseAmount * ReadNumber(FirstText(source, component.Value)) / 100m, 2);
        }
        if (component.CalculationType.Equals("Formula", StringComparison.OrdinalIgnoreCase))
            return decimal.Round(EvaluateFormula(source, byCode), 2);
        if (component.CalculationType.Equals("Balancing Amount", StringComparison.OrdinalIgnoreCase))
        {
            var used = values.Where(item => components.TryGetValue(item.Key, out var row) && row.Category.Equals("Earning", StringComparison.OrdinalIgnoreCase)).Sum(item => item.Value);
            return Math.Max(0, decimal.Round(monthlyCtc - used, 2));
        }
        return decimal.Round(ReadNumber(source), 2);
    }

    private static decimal EvaluateFormula(string source, Dictionary<string, decimal> byCode)
    {
        if (string.IsNullOrWhiteSpace(source)) return 0;
        var formula = source.ToUpperInvariant();
        formula = Regex.Replace(formula, @"(\d+(?:\.\d+)?)\s*%\s*OF\s*([A-Z0-9_]+)", "$2*$1/100");
        formula = Regex.Replace(formula, @"([A-Z0-9_]+)\s*\*\s*(\d+(?:\.\d+)?)\s*%", "$1*$2/100");
        formula = Regex.Replace(formula, @"(\d+(?:\.\d+)?)\s*%\s*\*\s*([A-Z0-9_]+)", "$1/100*$2");
        formula = Regex.Replace(formula, @"(\d+(?:\.\d+)?)%", "$1/100");
        foreach (var item in byCode.OrderByDescending(item => item.Key.Length))
            formula = Regex.Replace(formula, $@"(?<![A-Z0-9_]){Regex.Escape(item.Key)}(?![A-Z0-9_])", item.Value.ToString(CultureInfo.InvariantCulture), RegexOptions.IgnoreCase);
        var function = new Regex(@"\b(MIN|MAX)\(([^(),]+),([^(),]+)\)", RegexOptions.IgnoreCase);
        while (function.IsMatch(formula))
        {
            formula = function.Replace(formula, match =>
            {
                var left = EvaluateArithmetic(match.Groups[2].Value);
                var right = EvaluateArithmetic(match.Groups[3].Value);
                return (match.Groups[1].Value.Equals("MIN", StringComparison.OrdinalIgnoreCase) ? Math.Min(left, right) : Math.Max(left, right)).ToString(CultureInfo.InvariantCulture);
            });
        }
        return EvaluateArithmetic(formula);
    }

    private static decimal EvaluateArithmetic(string formula)
    {
        formula = Regex.Replace(formula, @"\s+", "");
        if (string.IsNullOrWhiteSpace(formula) || !Regex.IsMatch(formula, @"^[0-9+\-*/().]+$")) return 0;
        try { return Convert.ToDecimal(new DataTable().Compute(formula, ""), CultureInfo.InvariantCulture); }
        catch { return 0; }
    }

    private static string FirstText(params string[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
    private static bool HasFormula(string value) => Regex.IsMatch(value ?? "", @"[-+*/()%A-Z_]", RegexOptions.IgnoreCase);
    private static decimal ReadNumber(string value) => decimal.TryParse((value ?? "").Replace(",", "").Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var number) ? number : 0;

    private static async Task SaveEmployeeAsync(MySqlConnection connection, MySqlTransaction transaction, PayRunEmployee row)
    {
        await connection.ExecuteAsync(@"
INSERT INTO PayRunEmployees (PayRunId, EmployeeId, ClientId, ClientName, EmployeeCode, EmployeeName, Department, PresentDays, PayableDays, MonthlyGross, GrossPay, StatutoryDeductions, OneTimeEarnings, OneTimeDeductions, ManualTds, NetPay, IsSkipped, DetailsJson)
VALUES (@PayRunId, @EmployeeId, @ClientId, @ClientName, @EmployeeCode, @EmployeeName, @Department, @PresentDays, @PayableDays, @MonthlyGross, @GrossPay, @StatutoryDeductions, @OneTimeEarnings, @OneTimeDeductions, @ManualTds, @NetPay, @IsSkipped, @DetailsJson)
ON DUPLICATE KEY UPDATE ClientId=@ClientId, ClientName=@ClientName, EmployeeCode=@EmployeeCode, EmployeeName=@EmployeeName, Department=@Department, PresentDays=@PresentDays, PayableDays=@PayableDays, MonthlyGross=@MonthlyGross, GrossPay=@GrossPay, StatutoryDeductions=@StatutoryDeductions, OneTimeEarnings=@OneTimeEarnings, OneTimeDeductions=@OneTimeDeductions, ManualTds=@ManualTds, NetPay=@NetPay, IsSkipped=@IsSkipped, DetailsJson=@DetailsJson;", row, transaction);
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

    private readonly record struct AdjustmentTotal(decimal Earnings, decimal Deductions);
    private sealed record PayrollComponent(string Id, string Code, string Name, string Category, bool ProRata, bool Active, string CalculationType, string Value, string Formula, string BaseComponent, int Priority);
    private sealed record SalaryLine(string ComponentId, string Value);
    private sealed record PayRunAttendance(int EmployeeId, decimal PresentDays, decimal PayableDays);
    private sealed record LeaveBreakdownRow(int EmployeeId, string Code, string Name, string Type, decimal Days, decimal PayableDays);
    private sealed class PayRunSourceEmployee : Employee { public string ClientName { get; set; } = string.Empty; }

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
