using System.Text.Json;
using System.Text.Json.Serialization;
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
    PayDate DATE NOT NULL,
    TotalWorkingDays INT NOT NULL,
    Status VARCHAR(30) NOT NULL DEFAULT 'Draft',
    PayrollCost DECIMAL(18,2) NOT NULL DEFAULT 0,
    NetPay DECIMAL(18,2) NOT NULL DEFAULT 0,
    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    UNIQUE KEY UX_PayRuns_Client_Period (ClientId, PayPeriod)
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
    UNIQUE KEY UX_PayRunEmployees_Run_Employee (PayRunId, EmployeeId),
    CONSTRAINT FK_PayRunEmployees_PayRuns FOREIGN KEY (PayRunId) REFERENCES PayRuns(Id) ON DELETE CASCADE
);" );
        await EnsureColumnAsync(connection, "PayRunEmployees", "PaymentDate", "DATE NULL");
        await EnsureColumnAsync(connection, "PayRunEmployees", "ClientId", "INT NOT NULL DEFAULT 0");
        await EnsureColumnAsync(connection, "PayRunEmployees", "ClientName", "VARCHAR(250) NULL");
        await EnsureColumnAsync(connection, "PayRunEmployees", "ManualTds", "DECIMAL(18,2) NOT NULL DEFAULT 0");
        await EnsureColumnAsync(connection, "PayRuns", "ClientId", "INT NOT NULL DEFAULT 0");
        await EnsureColumnAsync(connection, "PayRuns", "ClientName", "VARCHAR(250) NULL");
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
ORDER BY r.PayPeriod DESC;");
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
        var existing = await connection.ExecuteScalarAsync<int?>("SELECT Id FROM PayRuns WHERE PayPeriod = @PayPeriod AND ClientId = @ClientId", new { request.PayPeriod, request.ClientId }, transaction);
        if (existing is not null)
            return null;

        var payRunId = (int)await connection.ExecuteScalarAsync<long>(@"
INSERT INTO PayRuns (ClientId, ClientName, PayPeriod, PayDate, TotalWorkingDays) VALUES (@ClientId, @ClientName, @PayPeriod, @PayDate, @TotalWorkingDays);
SELECT LAST_INSERT_ID();", new { request.ClientId, ClientName = client.Name, request.PayPeriod, PayDate = request.PayDate.ToDateTime(TimeOnly.MinValue), request.TotalWorkingDays }, transaction);
        var setupJson = await connection.ExecuteScalarAsync<string?>("SELECT SetupJson FROM PayrollSetups ORDER BY Id LIMIT 1", transaction: transaction) ?? "{}";
        var employees = await connection.QueryAsync<PayRunSourceEmployee>("SELECT e.*, c.Name AS ClientName FROM Employees e LEFT JOIN Clients c ON c.Id = e.ClientId WHERE e.IsActive = TRUE AND e.ClientId = @ClientId ORDER BY e.FirstName, e.LastName", new { request.ClientId }, transaction);
        var attendance = (await connection.QueryAsync<PayRunAttendance>(@"SELECT employee_id AS EmployeeId, present_days AS PresentDays, payable_days AS PayableDays
FROM employee_monthly_attendance WHERE client_id=@ClientId AND attendance_month=@Month", new { request.ClientId, Month = request.PayPeriod }, transaction)).ToDictionary(row => row.EmployeeId);

        var excludedEmployeeIds = request.ExcludedEmployeeIds.ToHashSet();
        foreach (var employee in employees)
        {
            var attendanceRow = attendance.GetValueOrDefault(employee.Id);
            var presentDays = attendanceRow is null ? request.TotalWorkingDays : (int)Math.Round(Math.Clamp(attendanceRow.PresentDays, 0, request.TotalWorkingDays), MidpointRounding.AwayFromZero);
            var payableDays = attendanceRow is null ? request.TotalWorkingDays : (int)Math.Round(Math.Clamp(attendanceRow.PayableDays, 0, request.TotalWorkingDays), MidpointRounding.AwayFromZero);
            var row = BuildEmployee(payRunId, employee, setupJson, request.TotalWorkingDays, presentDays, payableDays, 0, 0, 0, excludedEmployeeIds.Contains(employee.Id));
            await SaveEmployeeAsync(connection, transaction, row);
        }

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
        var salary = JsonSerializer.Deserialize<Dictionary<string, decimal>>(employee.SalaryJson, new JsonSerializerOptions { NumberHandling = JsonNumberHandling.AllowReadingFromString }) ?? [];
        var factor = totalWorkingDays == 0 ? 0 : (decimal)payableDays / totalWorkingDays;
        var lines = new List<object>();
        decimal monthlyGross = 0, grossPay = 0, deductions = 0;

        foreach (var (id, amount) in salary)
        {
            var component = components.GetValueOrDefault(id, new PayrollComponent(id, "Component", "Earning", true));
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
        using var document = JsonDocument.Parse(setupJson);
        if (!document.RootElement.TryGetProperty("salaryComponents", out var components))
            return [];
        return components.EnumerateArray().ToDictionary(
            component => component.GetProperty("id").ToString(),
            component => new PayrollComponent(
                component.GetProperty("id").ToString(),
                component.TryGetProperty("name", out var name) ? name.GetString() ?? "Component" : "Component",
                component.TryGetProperty("category", out var category) ? category.GetString() ?? "Earning" : "Earning",
                !component.TryGetProperty("proRata", out var proRata) || proRata.GetBoolean()));
    }

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

    private sealed record PayrollComponent(string Id, string Name, string Category, bool ProRata);
    private sealed record PayRunAttendance(int EmployeeId, decimal PresentDays, decimal PayableDays);
    private sealed class PayRunSourceEmployee : Employee { public string ClientName { get; set; } = string.Empty; }

    private static async Task EnsureColumnAsync(MySqlConnection connection, string tableName, string columnName, string definition)
    {
        var exists = await connection.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'payroll' AND TABLE_NAME = @TableName AND COLUMN_NAME = @ColumnName", new { TableName = tableName, ColumnName = columnName });
        if (exists == 0) await connection.ExecuteAsync($"ALTER TABLE `{tableName}` ADD COLUMN `{columnName}` {definition}");
    }

    private static async Task EnsurePayRunIndexAsync(MySqlConnection connection)
    {
        var oldIndex = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA = 'payroll' AND TABLE_NAME = 'PayRuns' AND INDEX_NAME = 'UX_PayRuns_PayPeriod'");
        if (oldIndex > 0) await connection.ExecuteAsync("ALTER TABLE PayRuns DROP INDEX UX_PayRuns_PayPeriod");
        var newIndex = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA = 'payroll' AND TABLE_NAME = 'PayRuns' AND INDEX_NAME = 'UX_PayRuns_Client_Period'");
        if (newIndex == 0) await connection.ExecuteAsync("ALTER TABLE PayRuns ADD UNIQUE KEY UX_PayRuns_Client_Period (ClientId, PayPeriod)");
    }
}
