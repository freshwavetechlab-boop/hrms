using Dapper;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public class LeaveAttendanceRepository(IConfiguration configuration)
{
    private static readonly LeaveAttendanceSetupStep[] DefaultSteps =
    [
        new() { Code = "preferences", Title = "General Settings / Preferences", Description = "Mandatory rules for leave year, attendance cycle, weekly offs and payroll impact.", IsMandatory = true, CanDisable = false },
        new() { Code = "leave_types", Title = "Leave Types", Description = "Define paid, unpaid, sick, casual and custom leave policies." },
        new() { Code = "holiday", Title = "Holiday Management", Description = "Maintain client/location-wise holiday calendars and restricted holidays." },
        new() { Code = "attendance", Title = "Attendance Management", Description = "Configure attendance capture, regularization, overtime and late rules." },
        new() { Code = "import_balance", Title = "Import Employee Leave Balance", Description = "Upload opening balances before employees start applying leaves." }
    ];

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
CREATE TABLE IF NOT EXISTS ModuleSettings (
    Id INT PRIMARY KEY AUTO_INCREMENT,
    ModuleCode VARCHAR(80) NOT NULL,
    IsEnabled BOOLEAN NOT NULL DEFAULT FALSE,
    SettingsJson JSON NULL,
    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    UNIQUE KEY UX_ModuleSettings_ModuleCode (ModuleCode)
);
CREATE TABLE IF NOT EXISTS ModuleSetupProgress (
    Id INT PRIMARY KEY AUTO_INCREMENT,
    ModuleCode VARCHAR(80) NOT NULL,
    StepCode VARCHAR(80) NOT NULL,
    Title VARCHAR(180) NOT NULL,
    Description VARCHAR(600),
    Status VARCHAR(40) NOT NULL DEFAULT 'Not Started',
    IsMandatory BOOLEAN NOT NULL DEFAULT FALSE,
    CanDisable BOOLEAN NOT NULL DEFAULT TRUE,
    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    UNIQUE KEY UX_ModuleSetupProgress_Module_Step (ModuleCode, StepCode),
    INDEX IX_ModuleSetupProgress_ModuleCode (ModuleCode)
);");
        await SeedAsync(connection);
    }

    public async Task<LeaveAttendanceSetup> GetAsync()
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("USE payroll;");
        await SeedAsync(connection);
        var isEnabled = await connection.ExecuteScalarAsync<bool>("SELECT IsEnabled FROM ModuleSettings WHERE ModuleCode = 'leave_attendance'");
        var steps = (await connection.QueryAsync<LeaveAttendanceSetupStep>(@"SELECT StepCode AS Code, Title, Description, Status, IsMandatory, CanDisable, UpdatedAt
FROM ModuleSetupProgress WHERE ModuleCode = 'leave_attendance' ORDER BY FIELD(StepCode, 'preferences', 'leave_types', 'holiday', 'attendance', 'import_balance');")).ToList();
        return new LeaveAttendanceSetup { IsEnabled = isEnabled, Steps = steps };
    }

    public async Task<LeaveAttendanceSetup> SetEnabledAsync(bool isEnabled)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("USE payroll;");
        await SeedAsync(connection);
        await connection.ExecuteAsync("UPDATE ModuleSettings SET IsEnabled = @IsEnabled WHERE ModuleCode = 'leave_attendance'", new { IsEnabled = isEnabled });
        if (!isEnabled)
            await connection.ExecuteAsync("UPDATE ModuleSetupProgress SET Status = CASE WHEN IsMandatory THEN Status ELSE 'Disabled' END WHERE ModuleCode = 'leave_attendance'");
        return await GetAsync();
    }

    public async Task<LeaveAttendanceSetup?> UpdateStepAsync(string stepCode, string status)
    {
        if (!IsValidStatus(status)) return null;
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("USE payroll;");
        await SeedAsync(connection);
        var step = await connection.QueryFirstOrDefaultAsync<LeaveAttendanceSetupStep>(@"SELECT StepCode AS Code, Title, Description, Status, IsMandatory, CanDisable
FROM ModuleSetupProgress WHERE ModuleCode = 'leave_attendance' AND StepCode = @StepCode", new { StepCode = stepCode });
        if (step is null || (step.IsMandatory && status == "Disabled")) return null;
        await connection.ExecuteAsync(@"UPDATE ModuleSetupProgress SET Status = @Status WHERE ModuleCode = 'leave_attendance' AND StepCode = @StepCode", new { StepCode = stepCode, Status = status });
        return await GetAsync();
    }

    private static bool IsValidStatus(string status) =>
        status is "Not Started" or "In Progress" or "Completed" or "Disabled";

    private static async Task SeedAsync(MySqlConnection connection)
    {
        await connection.ExecuteAsync(@"INSERT IGNORE INTO ModuleSettings (ModuleCode, IsEnabled, SettingsJson)
VALUES ('leave_attendance', FALSE, JSON_OBJECT());");
        foreach (var step in DefaultSteps)
        {
            await connection.ExecuteAsync(@"INSERT IGNORE INTO ModuleSetupProgress (ModuleCode, StepCode, Title, Description, Status, IsMandatory, CanDisable)
VALUES ('leave_attendance', @Code, @Title, @Description, 'Not Started', @IsMandatory, @CanDisable);", step);
        }
    }
}
