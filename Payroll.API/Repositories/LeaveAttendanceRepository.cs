using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using System.Text.Json;

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
);
CREATE TABLE IF NOT EXISTS leave_attendance_preferences (
    id INT PRIMARY KEY AUTO_INCREMENT,
    attendance_cycle_start_day INT NOT NULL DEFAULT 1,
    attendance_cycle_end_day INT NOT NULL DEFAULT 25,
    payroll_report_generation_day INT NOT NULL DEFAULT 28,
    include_leave_encashment_in_pay_run BOOLEAN NOT NULL DEFAULT FALSE,
    leave_encashment_salary_component_id INT NULL,
    created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
);
CREATE TABLE IF NOT EXISTS attendance_settings (
    id INT PRIMARY KEY AUTO_INCREMENT,
    check_in_time TIME NOT NULL DEFAULT '09:00:00',
    check_out_time TIME NOT NULL DEFAULT '18:00:00',
    working_hours_calculation VARCHAR(80) NOT NULL DEFAULT 'First check-in and last check-out',
    minimum_hours_for_half_day DECIMAL(5,2) NOT NULL DEFAULT 4,
    minimum_hours_for_full_day DECIMAL(5,2) NOT NULL DEFAULT 8,
    maximum_hours_allowed_for_full_day DECIMAL(5,2) NOT NULL DEFAULT 12,
    allow_regularization_requests BOOLEAN NOT NULL DEFAULT TRUE,
    regularization_window VARCHAR(40) NOT NULL DEFAULT 'Anytime',
    past_days_allowed INT NOT NULL DEFAULT 7,
    restrict_regularization_requests_per_month BOOLEAN NOT NULL DEFAULT FALSE,
    max_regularization_requests_per_month INT NOT NULL DEFAULT 3,
    created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
);
CREATE TABLE IF NOT EXISTS employee_monthly_attendance (
    id INT PRIMARY KEY AUTO_INCREMENT,
    client_id INT NOT NULL,
    employee_id INT NOT NULL,
    attendance_month VARCHAR(7) NOT NULL,
    working_days DECIMAL(5,2) NOT NULL DEFAULT 0,
    present_days DECIMAL(5,2) NOT NULL DEFAULT 0,
    payable_days DECIMAL(5,2) NOT NULL DEFAULT 0,
    lop_days DECIMAL(5,2) NOT NULL DEFAULT 0,
    source_type VARCHAR(30) NOT NULL DEFAULT 'Monthly',
    remarks VARCHAR(600),
    created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    UNIQUE KEY UX_monthly_attendance_employee_month (client_id, employee_id, attendance_month),
    INDEX IX_monthly_attendance_client_month (client_id, attendance_month),
    CONSTRAINT FK_monthly_attendance_employee FOREIGN KEY (employee_id) REFERENCES Employees(Id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS employee_daily_attendance (
    id INT PRIMARY KEY AUTO_INCREMENT,
    client_id INT NOT NULL,
    employee_id INT NOT NULL,
    attendance_date DATE NOT NULL,
    status VARCHAR(30) NOT NULL DEFAULT 'Present',
    payable_value DECIMAL(4,2) NOT NULL DEFAULT 1,
    remarks VARCHAR(600),
    created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    UNIQUE KEY UX_daily_attendance_employee_date (client_id, employee_id, attendance_date),
    INDEX IX_daily_attendance_client_date (client_id, attendance_date),
    CONSTRAINT FK_daily_attendance_employee FOREIGN KEY (employee_id) REFERENCES Employees(Id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS leave_types (
    id INT PRIMARY KEY AUTO_INCREMENT,
    name VARCHAR(180) NOT NULL,
    code VARCHAR(40) NOT NULL,
    type VARCHAR(20) NOT NULL DEFAULT 'Paid',
    description VARCHAR(800),
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    UNIQUE KEY UX_leave_types_code (code)
);
CREATE TABLE IF NOT EXISTS leave_type_policies (
    id INT PRIMARY KEY AUTO_INCREMENT,
    leave_type_id INT NOT NULL,
    entitlement DECIMAL(10,2) NOT NULL DEFAULT 0,
    entitlement_period VARCHAR(20) NOT NULL DEFAULT 'Yearly',
    pro_rate_for_new_joinees BOOLEAN NOT NULL DEFAULT FALSE,
    reset_enabled BOOLEAN NOT NULL DEFAULT FALSE,
    reset_frequency VARCHAR(20) NOT NULL DEFAULT 'Yearly',
    carry_forward_unused_leaves BOOLEAN NOT NULL DEFAULT FALSE,
    max_carry_forward_limit DECIMAL(10,2) NULL,
    encash_unused_leaves BOOLEAN NOT NULL DEFAULT FALSE,
    max_encashment_limit DECIMAL(10,2) NULL,
    allow_negative_leave_balance BOOLEAN NOT NULL DEFAULT FALSE,
    negative_balance_handling VARCHAR(50) NOT NULL DEFAULT 'Mark as LOP',
    allow_past_dates BOOLEAN NOT NULL DEFAULT FALSE,
    past_date_limit_type VARCHAR(30) NOT NULL DEFAULT 'No limit',
    past_date_limit_days INT NULL,
    allow_future_dates BOOLEAN NOT NULL DEFAULT FALSE,
    future_date_limit_type VARCHAR(30) NOT NULL DEFAULT 'No limit',
    future_date_limit_days INT NULL,
    effective_from DATE NOT NULL,
    expires_on DATE NULL,
    postpone_credits_for_new_employees BOOLEAN NOT NULL DEFAULT FALSE,
    postpone_credit_value INT NULL,
    postpone_credit_unit VARCHAR(20) NOT NULL DEFAULT 'Days',
    created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    UNIQUE KEY UX_leave_type_policies_leave_type (leave_type_id),
    CONSTRAINT FK_leave_type_policies_type FOREIGN KEY (leave_type_id) REFERENCES leave_types(id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS leave_type_applicability (
    id INT PRIMARY KEY AUTO_INCREMENT,
    leave_type_id INT NOT NULL,
    applicability_mode VARCHAR(40) NOT NULL DEFAULT 'All employees',
    work_location VARCHAR(150),
    department VARCHAR(150),
    designation VARCHAR(150),
    gender VARCHAR(40),
    created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    UNIQUE KEY UX_leave_type_applicability_leave_type (leave_type_id),
    CONSTRAINT FK_leave_type_applicability_type FOREIGN KEY (leave_type_id) REFERENCES leave_types(id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS holidays (
    id INT PRIMARY KEY AUTO_INCREMENT,
    name VARCHAR(180) NOT NULL,
    start_date DATE NOT NULL,
    end_date DATE NOT NULL,
    description VARCHAR(800),
    all_locations BOOLEAN NOT NULL DEFAULT TRUE,
    created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    INDEX IX_holidays_dates (start_date, end_date)
);
CREATE TABLE IF NOT EXISTS holiday_locations (
    id INT PRIMARY KEY AUTO_INCREMENT,
    holiday_id INT NOT NULL,
    work_location_id INT NOT NULL,
    created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE KEY UX_holiday_locations_holiday_location (holiday_id, work_location_id),
    INDEX IX_holiday_locations_location (work_location_id),
    CONSTRAINT FK_holiday_locations_holiday FOREIGN KEY (holiday_id) REFERENCES holidays(id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS employee_leave_balances (
    id INT PRIMARY KEY AUTO_INCREMENT,
    employee_id INT NOT NULL,
    leave_type_id INT NOT NULL,
    balance_date DATE NOT NULL,
    balance_count DECIMAL(10,2) NOT NULL DEFAULT 0,
    created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    UNIQUE KEY UX_employee_leave_balances_employee_type_date (employee_id, leave_type_id, balance_date),
    INDEX IX_employee_leave_balances_employee (employee_id),
    CONSTRAINT FK_employee_leave_balances_employee FOREIGN KEY (employee_id) REFERENCES Employees(Id) ON DELETE CASCADE,
    CONSTRAINT FK_employee_leave_balances_leave_type FOREIGN KEY (leave_type_id) REFERENCES leave_types(id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS leave_balance_import_logs (
    id INT PRIMARY KEY AUTO_INCREMENT,
    file_name VARCHAR(260) NOT NULL,
    encoding VARCHAR(80) NOT NULL,
    total_records INT NOT NULL DEFAULT 0,
    imported_records INT NOT NULL DEFAULT 0,
    skipped_records INT NOT NULL DEFAULT 0,
    mapping_json JSON NULL,
    created_by VARCHAR(180),
    created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
);
CREATE TABLE IF NOT EXISTS leave_balance_import_errors (
    id INT PRIMARY KEY AUTO_INCREMENT,
    import_log_id INT NOT NULL,
    row_no INT NOT NULL,
    employee_number VARCHAR(80),
    leave_type VARCHAR(180),
    date_text VARCHAR(80),
    count_text VARCHAR(80),
    error_message VARCHAR(1000) NOT NULL,
    created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    INDEX IX_leave_balance_import_errors_log (import_log_id),
    CONSTRAINT FK_leave_balance_import_errors_log FOREIGN KEY (import_log_id) REFERENCES leave_balance_import_logs(id) ON DELETE CASCADE
);");
        await EnsureClientScopeAsync(connection);
        await SeedAsync(connection);
    }

    public async Task<LeaveAttendanceSetup> GetAsync(int clientId)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("USE payroll;");
        await SeedAsync(connection, clientId);
        var isEnabled = await connection.ExecuteScalarAsync<bool>("SELECT IsEnabled FROM ModuleSettings WHERE ModuleCode = 'leave_attendance' AND client_id=@ClientId", new { ClientId = clientId });
        var steps = (await connection.QueryAsync<LeaveAttendanceSetupStep>(@"SELECT StepCode AS Code, Title, Description, Status, IsMandatory, CanDisable, UpdatedAt
FROM ModuleSetupProgress WHERE ModuleCode = 'leave_attendance' AND client_id=@ClientId ORDER BY FIELD(StepCode, 'preferences', 'leave_types', 'holiday', 'attendance', 'import_balance');", new { ClientId = clientId })).ToList();
        return new LeaveAttendanceSetup { ClientId = clientId, IsEnabled = isEnabled, Steps = steps };
    }

    public async Task<LeaveAttendanceSetup> SetEnabledAsync(int clientId, bool isEnabled)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("USE payroll;");
        await SeedAsync(connection, clientId);
        await connection.ExecuteAsync("UPDATE ModuleSettings SET IsEnabled = @IsEnabled WHERE ModuleCode = 'leave_attendance' AND client_id=@ClientId", new { IsEnabled = isEnabled, ClientId = clientId });
        if (!isEnabled)
            await connection.ExecuteAsync("UPDATE ModuleSetupProgress SET Status = CASE WHEN IsMandatory THEN Status ELSE 'Disabled' END WHERE ModuleCode = 'leave_attendance' AND client_id=@ClientId", new { ClientId = clientId });
        return await GetAsync(clientId);
    }

    public async Task<LeaveAttendanceSetup?> UpdateStepAsync(int clientId, string stepCode, string status)
    {
        if (!IsValidStatus(status)) return null;
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("USE payroll;");
        await SeedAsync(connection, clientId);
        var step = await connection.QueryFirstOrDefaultAsync<LeaveAttendanceSetupStep>(@"SELECT StepCode AS Code, Title, Description, Status, IsMandatory, CanDisable
FROM ModuleSetupProgress WHERE ModuleCode = 'leave_attendance' AND client_id=@ClientId AND StepCode = @StepCode", new { ClientId = clientId, StepCode = stepCode });
        if (step is null || (step.IsMandatory && status == "Disabled")) return null;
        await connection.ExecuteAsync(@"UPDATE ModuleSetupProgress SET Status = @Status WHERE ModuleCode = 'leave_attendance' AND client_id=@ClientId AND StepCode = @StepCode", new { ClientId = clientId, StepCode = stepCode, Status = status });
        return await GetAsync(clientId);
    }

    public async Task<LeaveAttendancePreferences> GetPreferencesAsync(int clientId)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("USE payroll;");
        await SeedPreferencesAsync(connection, clientId);
        return await connection.QuerySingleAsync<LeaveAttendancePreferences>(@"SELECT id AS Id, client_id AS ClientId,
attendance_cycle_start_day AS AttendanceCycleStartDay,
attendance_cycle_end_day AS AttendanceCycleEndDay,
payroll_report_generation_day AS PayrollReportGenerationDay,
include_leave_encashment_in_pay_run AS IncludeLeaveEncashmentInPayRun,
leave_encashment_salary_component_id AS LeaveEncashmentSalaryComponentId,
created_at AS CreatedAt,
updated_at AS UpdatedAt
FROM leave_attendance_preferences WHERE client_id=@ClientId LIMIT 1;", new { ClientId = clientId });
    }

    public async Task<(LeaveAttendancePreferences? Preferences, string? Error)> SavePreferencesAsync(SaveLeaveAttendancePreferencesRequest request)
    {
        var validationError = await ValidatePreferencesAsync(request);
        if (validationError is not null) return (null, validationError);
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("USE payroll;");
        await SeedPreferencesAsync(connection, request.ClientId);
        await connection.ExecuteAsync(@"UPDATE leave_attendance_preferences SET
attendance_cycle_start_day = @AttendanceCycleStartDay,
attendance_cycle_end_day = @AttendanceCycleEndDay,
payroll_report_generation_day = @PayrollReportGenerationDay,
include_leave_encashment_in_pay_run = @IncludeLeaveEncashmentInPayRun,
leave_encashment_salary_component_id = @LeaveEncashmentSalaryComponentId
WHERE client_id=@ClientId;", request);
        return (await GetPreferencesAsync(request.ClientId), null);
    }

    public async Task<AttendanceSettings> GetAttendanceSettingsAsync(int clientId)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("USE payroll;");
        await SeedAttendanceSettingsAsync(connection, clientId);
        return await connection.QuerySingleAsync<AttendanceSettings>(@"SELECT id AS Id, client_id AS ClientId,
check_in_time AS CheckInTime,
check_out_time AS CheckOutTime,
working_hours_calculation AS WorkingHoursCalculation,
minimum_hours_for_half_day AS MinimumHoursForHalfDay,
minimum_hours_for_full_day AS MinimumHoursForFullDay,
maximum_hours_allowed_for_full_day AS MaximumHoursAllowedForFullDay,
allow_regularization_requests AS AllowRegularizationRequests,
regularization_window AS RegularizationWindow,
past_days_allowed AS PastDaysAllowed,
restrict_regularization_requests_per_month AS RestrictRegularizationRequestsPerMonth,
max_regularization_requests_per_month AS MaxRegularizationRequestsPerMonth,
created_at AS CreatedAt,
updated_at AS UpdatedAt
FROM attendance_settings WHERE client_id=@ClientId LIMIT 1;", new { ClientId = clientId });
    }

    public async Task<(AttendanceSettings? Settings, string? Error)> SaveAttendanceSettingsAsync(SaveAttendanceSettingsRequest request)
    {
        var error = ValidateAttendanceSettings(request);
        if (error is not null) return (null, error);
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("USE payroll;");
        await SeedAttendanceSettingsAsync(connection, request.ClientId);
        await connection.ExecuteAsync(@"UPDATE attendance_settings SET
check_in_time=@CheckInTime,
check_out_time=@CheckOutTime,
working_hours_calculation=@WorkingHoursCalculation,
minimum_hours_for_half_day=@MinimumHoursForHalfDay,
minimum_hours_for_full_day=@MinimumHoursForFullDay,
maximum_hours_allowed_for_full_day=@MaximumHoursAllowedForFullDay,
allow_regularization_requests=@AllowRegularizationRequests,
regularization_window=@RegularizationWindow,
past_days_allowed=@PastDaysAllowed,
restrict_regularization_requests_per_month=@RestrictRegularizationRequestsPerMonth,
max_regularization_requests_per_month=@MaxRegularizationRequestsPerMonth
WHERE client_id=@ClientId;", request);
        return (await GetAttendanceSettingsAsync(request.ClientId), null);
    }

    public async Task<IEnumerable<EmployeeMonthlyAttendance>> GetMonthlyAttendanceAsync(int clientId, string month)
    {
        if (!IsValidMonth(month)) return [];
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("USE payroll;");
        return await connection.QueryAsync<EmployeeMonthlyAttendance>(@"SELECT e.Id AS EmployeeId, e.EmployeeCode, CONCAT(e.FirstName, ' ', e.LastName) AS EmployeeName, e.Department,
@Month AS Month,
COALESCE(a.working_days, 0) AS WorkingDays,
COALESCE(a.present_days, 0) AS PresentDays,
COALESCE(a.payable_days, 0) AS PayableDays,
COALESCE(a.lop_days, 0) AS LopDays,
COALESCE(a.source_type, 'Monthly') AS SourceType,
COALESCE(a.remarks, '') AS Remarks
FROM Employees e
LEFT JOIN employee_monthly_attendance a ON a.employee_id=e.Id AND a.client_id=e.ClientId AND a.attendance_month=@Month
WHERE e.ClientId=@ClientId AND e.IsActive=TRUE
ORDER BY e.FirstName, e.LastName, e.EmployeeCode;", new { ClientId = clientId, Month = month });
    }

    public async Task<(IEnumerable<EmployeeMonthlyAttendance>? Rows, string? Error)> SaveMonthlyAttendanceAsync(SaveMonthlyAttendanceRequest request)
    {
        var error = ValidateMonthlyAttendance(request);
        if (error is not null) return (null, error);
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("USE payroll;");
        var validEmployeeIds = (await connection.QueryAsync<int>("SELECT Id FROM Employees WHERE ClientId=@ClientId AND IsActive=TRUE", new { request.ClientId })).ToHashSet();
        var rows = request.Rows.Where(row => validEmployeeIds.Contains(row.EmployeeId)).Select(row =>
        {
            var working = Math.Max(0, row.WorkingDays);
            var present = Math.Clamp(row.PresentDays, 0, working == 0 ? row.PresentDays : working);
            var payable = Math.Clamp(row.PayableDays, 0, working == 0 ? row.PayableDays : working);
            return new { request.ClientId, request.Month, row.EmployeeId, WorkingDays = working, PresentDays = present, PayableDays = payable, LopDays = Math.Max(0, row.LopDays), Remarks = row.Remarks ?? string.Empty };
        }).ToList();
        await connection.ExecuteAsync(@"INSERT INTO employee_monthly_attendance (client_id, employee_id, attendance_month, working_days, present_days, payable_days, lop_days, source_type, remarks)
VALUES (@ClientId, @EmployeeId, @Month, @WorkingDays, @PresentDays, @PayableDays, @LopDays, 'Monthly', @Remarks)
ON DUPLICATE KEY UPDATE working_days=VALUES(working_days), present_days=VALUES(present_days), payable_days=VALUES(payable_days), lop_days=VALUES(lop_days), source_type='Monthly', remarks=VALUES(remarks);", rows);
        return (await GetMonthlyAttendanceAsync(request.ClientId, request.Month), null);
    }

    public async Task<IEnumerable<EmployeeDailyAttendance>> GetDailyAttendanceAsync(int clientId, int employeeId, string month)
    {
        if (!IsValidMonth(month)) return [];
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("USE payroll;");
        return await connection.QueryAsync<EmployeeDailyAttendance>(@"SELECT id AS Id, client_id AS ClientId, employee_id AS EmployeeId, attendance_date AS AttendanceDate, status AS Status, payable_value AS PayableValue, COALESCE(remarks, '') AS Remarks
FROM employee_daily_attendance
WHERE client_id=@ClientId AND employee_id=@EmployeeId AND DATE_FORMAT(attendance_date, '%Y-%m')=@Month
ORDER BY attendance_date;", new { ClientId = clientId, EmployeeId = employeeId, Month = month });
    }

    public async Task<(IEnumerable<EmployeeDailyAttendance>? Rows, string? Error)> SaveDailyAttendanceAsync(SaveDailyAttendanceRequest request)
    {
        var error = ValidateDailyAttendance(request);
        if (error is not null) return (null, error);
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("USE payroll;");
        var exists = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Employees WHERE Id=@EmployeeId AND ClientId=@ClientId AND IsActive=TRUE", new { request.EmployeeId, request.ClientId });
        if (exists == 0) return (null, "Employee was not found for the selected client.");
        var activeLeaveTypes = (await connection.QueryAsync<(string Code, string Type)>(@"SELECT code AS Code, type AS Type
FROM leave_types
WHERE client_id=@ClientId AND is_active=TRUE;", new { request.ClientId }))
            .ToDictionary(row => row.Code, row => row.Type, StringComparer.OrdinalIgnoreCase);
        var invalidStatus = request.Rows.FirstOrDefault(row => !string.Equals(row.Status, "Present", StringComparison.OrdinalIgnoreCase) && !activeLeaveTypes.ContainsKey(row.Status));
        if (invalidStatus is not null) return (null, $"Attendance status '{invalidStatus.Status}' is not an active leave type.");
        var rows = request.Rows.Where(row => row.AttendanceDate.ToString("yyyy-MM") == request.Month).Select(row =>
        {
            var status = string.Equals(row.Status, "Present", StringComparison.OrdinalIgnoreCase) ? "Present" : activeLeaveTypes.Keys.First(code => string.Equals(code, row.Status, StringComparison.OrdinalIgnoreCase));
            var payableValue = status == "Present" || string.Equals(activeLeaveTypes[status], "Paid", StringComparison.OrdinalIgnoreCase) ? 1m : 0m;
            return new { request.ClientId, request.EmployeeId, AttendanceDate = row.AttendanceDate.Date, Status = status, PayableValue = payableValue, Remarks = row.Remarks ?? string.Empty };
        }).ToList();
        await connection.ExecuteAsync(@"INSERT INTO employee_daily_attendance (client_id, employee_id, attendance_date, status, payable_value, remarks)
VALUES (@ClientId, @EmployeeId, @AttendanceDate, @Status, @PayableValue, @Remarks)
ON DUPLICATE KEY UPDATE status=VALUES(status), payable_value=VALUES(payable_value), remarks=VALUES(remarks);", rows);
        await RollupDailyAttendanceAsync(connection, request.ClientId, request.EmployeeId, request.Month);
        return (await GetDailyAttendanceAsync(request.ClientId, request.EmployeeId, request.Month), null);
    }

    public async Task<IEnumerable<LeaveType>> GetLeaveTypesAsync(int clientId)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("USE payroll;");
        return await connection.QueryAsync<LeaveType>(LeaveTypeSelectSql + " WHERE lt.client_id=@ClientId ORDER BY lt.name;", new { ClientId = clientId });
    }

    public async Task<(LeaveType? LeaveType, string? Error)> SaveLeaveTypeAsync(SaveLeaveTypeRequest request)
    {
        var error = ValidateLeaveType(request);
        if (error is not null) return (null, error);
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("USE payroll;");
        await using var transaction = await connection.BeginTransactionAsync();
        var id = request.Id;
        var code = request.Code.Trim().ToUpperInvariant();
        var duplicateCode = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM leave_types WHERE client_id=@ClientId AND code=@Code AND id<>@Id", new { request.ClientId, Code = code, Id = id }, transaction);
        if (duplicateCode > 0) return (null, "Leave type code already exists. Use a unique code.");
        if (id == 0)
        {
            id = (int)await connection.ExecuteScalarAsync<long>(@"INSERT INTO leave_types (client_id, name, code, type, description, is_active)
VALUES (@ClientId, @Name, @Code, @Type, @Description, TRUE); SELECT LAST_INSERT_ID();", new { request.ClientId, Name = request.Name.Trim(), Code = code, request.Type, request.Description }, transaction);
        }
        else
        {
            await connection.ExecuteAsync(@"UPDATE leave_types SET name=@Name, code=@Code, type=@Type, description=@Description, is_active=@IsActive WHERE id=@Id", new { Id = id, Name = request.Name.Trim(), Code = code, request.Type, request.Description, request.IsActive }, transaction);
        }
        await UpsertPolicyAsync(connection, transaction, id, request);
        await UpsertApplicabilityAsync(connection, transaction, id, request);
        await transaction.CommitAsync();
        return (await GetLeaveTypeAsync(id, request.ClientId), null);
    }

    public async Task<LeaveType?> SetLeaveTypeActiveAsync(int id, int clientId, bool isActive)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("USE payroll;");
        await connection.ExecuteAsync("UPDATE leave_types SET is_active=@IsActive WHERE id=@Id AND client_id=@ClientId", new { Id = id, ClientId = clientId, IsActive = isActive });
        return await GetLeaveTypeAsync(id, clientId);
    }

    public async Task<bool> DeleteLeaveTypeAsync(int id, int clientId)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("USE payroll;");
        return await connection.ExecuteAsync("DELETE FROM leave_types WHERE id=@Id AND client_id=@ClientId", new { Id = id, ClientId = clientId }) > 0;
    }

    private async Task<LeaveType?> GetLeaveTypeAsync(int id, int clientId)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("USE payroll;");
        return await connection.QueryFirstOrDefaultAsync<LeaveType>(LeaveTypeSelectSql + " WHERE lt.id=@Id AND lt.client_id=@ClientId", new { Id = id, ClientId = clientId });
    }

    public async Task<IEnumerable<Holiday>> GetHolidaysAsync(int clientId, int? year, int? workLocationId)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("USE payroll;");
        var rows = (await connection.QueryAsync<Holiday>(@"SELECT h.id AS Id, h.client_id AS ClientId, h.name AS Name, h.start_date AS StartDate, h.end_date AS EndDate, h.description AS Description, h.all_locations AS AllLocations, h.created_at AS CreatedAt, h.updated_at AS UpdatedAt,
CASE WHEN h.all_locations THEN 'All locations' ELSE COALESCE(GROUP_CONCAT(w.Name ORDER BY w.Name SEPARATOR ', '), 'No locations') END AS WorkLocations
FROM holidays h
LEFT JOIN holiday_locations hl ON hl.holiday_id = h.id
LEFT JOIN WorkLocations w ON w.Id = hl.work_location_id
WHERE h.client_id=@ClientId AND (@Year IS NULL OR YEAR(h.start_date) = @Year OR YEAR(h.end_date) = @Year)
AND (@WorkLocationId IS NULL OR h.all_locations = TRUE OR hl.work_location_id = @WorkLocationId)
GROUP BY h.id
ORDER BY h.start_date, h.name;", new { ClientId = clientId, Year = year, WorkLocationId = workLocationId })).ToList();
        if (rows.Count == 0) return rows;
        var locations = await connection.QueryAsync<(int HolidayId, int WorkLocationId)>("SELECT holiday_id AS HolidayId, work_location_id AS WorkLocationId FROM holiday_locations WHERE holiday_id IN @Ids", new { Ids = rows.Select(row => row.Id).ToArray() });
        foreach (var row in rows)
            row.WorkLocationIds = locations.Where(location => location.HolidayId == row.Id).Select(location => location.WorkLocationId).ToList();
        return rows;
    }

    public async Task<(Holiday? Holiday, string? Error)> SaveHolidayAsync(SaveHolidayRequest request)
    {
        var error = ValidateHoliday(request);
        if (error is not null) return (null, error);
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("USE payroll;");
        var duplicate = await HasDuplicateHolidayAsync(connection, request);
        if (duplicate) return (null, "Duplicate holiday exists for the same location and date range.");
        await using var transaction = await connection.BeginTransactionAsync();
        var id = request.Id;
        if (id == 0)
        {
            id = (int)await connection.ExecuteScalarAsync<long>(@"INSERT INTO holidays (client_id, name, start_date, end_date, description, all_locations)
VALUES (@ClientId, @Name, @StartDate, @EndDate, @Description, @AllLocations); SELECT LAST_INSERT_ID();", new { request.ClientId, Name = request.Name.Trim(), request.StartDate, request.EndDate, request.Description, request.AllLocations }, transaction);
        }
        else
        {
            var updated = await connection.ExecuteAsync(@"UPDATE holidays SET name=@Name, start_date=@StartDate, end_date=@EndDate, description=@Description, all_locations=@AllLocations WHERE id=@Id AND client_id=@ClientId", new { request.ClientId, Id = id, Name = request.Name.Trim(), request.StartDate, request.EndDate, request.Description, request.AllLocations }, transaction);
            if (updated == 0) return (null, "Holiday was not found for the selected client.");
            await connection.ExecuteAsync("DELETE FROM holiday_locations WHERE holiday_id=@Id", new { Id = id }, transaction);
        }
        if (!request.AllLocations && request.WorkLocationIds.Count > 0)
            await connection.ExecuteAsync("INSERT INTO holiday_locations (holiday_id, work_location_id) VALUES (@HolidayId, @WorkLocationId)", request.WorkLocationIds.Distinct().Select(locationId => new { HolidayId = id, WorkLocationId = locationId }), transaction);
        await transaction.CommitAsync();
        return (await GetHolidayAsync(id, request.ClientId), null);
    }

    public async Task<bool> DeleteHolidayAsync(int id, int clientId)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("USE payroll;");
        return await connection.ExecuteAsync("DELETE FROM holidays WHERE id=@Id AND client_id=@ClientId", new { Id = id, ClientId = clientId }) > 0;
    }

    private async Task<Holiday?> GetHolidayAsync(int id, int clientId) =>
        (await GetHolidaysAsync(clientId, null, null)).FirstOrDefault(holiday => holiday.Id == id);

    private async Task<string?> ValidatePreferencesAsync(SaveLeaveAttendancePreferencesRequest request)
    {
        if (!IsValidDay(request.AttendanceCycleStartDay) || !IsValidDay(request.AttendanceCycleEndDay) || !IsValidDay(request.PayrollReportGenerationDay))
            return "Attendance cycle and report generation days must be between 1 and 31.";
        var buffer = request.PayrollReportGenerationDay >= request.AttendanceCycleEndDay
            ? request.PayrollReportGenerationDay - request.AttendanceCycleEndDay
            : request.PayrollReportGenerationDay + 31 - request.AttendanceCycleEndDay;
        if (buffer is < 3 or > 7)
            return "Payroll report generation day must have a 3 to 7 day buffer after attendance cycle end day.";
        if (!request.IncludeLeaveEncashmentInPayRun)
            return null;
        if (request.LeaveEncashmentSalaryComponentId is null or <= 0)
            return "Select a formula-based salary component for leave encashment.";
        return await IsFormulaBasedSalaryComponentAsync(request.LeaveEncashmentSalaryComponentId.Value)
            ? null
            : "Leave encashment can only be enabled with a formula-based salary component.";
    }

    private static string? ValidateLeaveType(SaveLeaveTypeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Code)) return "Leave type name and code are required.";
        if (request.Type is not ("Paid" or "Unpaid")) return "Leave type must be Paid or Unpaid.";
        if (request.Entitlement < 0) return "Entitlement cannot be negative.";
        if (request.ExpiresOn.HasValue && request.ExpiresOn.Value.Date < request.EffectiveFrom.Date) return "Expiry date cannot be before effective date.";
        return null;
    }

    private static string? ValidateHoliday(SaveHolidayRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) return "Holiday name is required.";
        if (request.EndDate.Date < request.StartDate.Date) return "End date cannot be before start date.";
        if (!request.AllLocations && request.WorkLocationIds.Count == 0) return "Select at least one work location or choose all locations.";
        return null;
    }

    private static string? ValidateAttendanceSettings(SaveAttendanceSettingsRequest request)
    {
        if (request.CheckOutTime <= request.CheckInTime) return "Check-out time must be after check-in time.";
        if (request.WorkingHoursCalculation is not ("First check-in and last check-out" or "Every valid check-in and check-out")) return "Select a valid working hours calculation method.";
        if (request.MinimumHoursForHalfDay <= 0 || request.MinimumHoursForFullDay <= 0 || request.MaximumHoursAllowedForFullDay <= 0) return "Workday duration hours must be greater than zero.";
        if (request.MinimumHoursForHalfDay > request.MinimumHoursForFullDay) return "Half-day minimum hours cannot exceed full-day minimum hours.";
        if (request.MinimumHoursForFullDay > request.MaximumHoursAllowedForFullDay) return "Full-day minimum hours cannot exceed maximum full-day hours.";
        if (request.RegularizationWindow is not ("Anytime" or "Limited by past days")) return "Select a valid regularization window.";
        if (request.RegularizationWindow == "Limited by past days" && request.PastDaysAllowed < 0) return "Past days allowed cannot be negative.";
        if (request.RestrictRegularizationRequestsPerMonth && request.MaxRegularizationRequestsPerMonth <= 0) return "Max regularization requests per month must be greater than zero.";
        return null;
    }

    private static string? ValidateMonthlyAttendance(SaveMonthlyAttendanceRequest request)
    {
        if (request.ClientId <= 0) return "Select a client.";
        if (!IsValidMonth(request.Month)) return "Select a valid attendance month.";
        if (request.Rows.Count == 0) return "No attendance rows were submitted.";
        if (request.Rows.Any(row => row.WorkingDays < 0 || row.PresentDays < 0 || row.PayableDays < 0 || row.LopDays < 0)) return "Attendance values cannot be negative.";
        if (request.Rows.Any(row => row.WorkingDays > 31 || row.PresentDays > 31 || row.PayableDays > 31 || row.LopDays > 31)) return "Attendance values cannot exceed 31 days.";
        return null;
    }

    private static string? ValidateDailyAttendance(SaveDailyAttendanceRequest request)
    {
        if (request.ClientId <= 0 || request.EmployeeId <= 0) return "Select a client and employee.";
        if (!IsValidMonth(request.Month)) return "Select a valid attendance month.";
        if (request.Rows.Count == 0) return "Add at least one date-wise attendance row.";
        if (request.Rows.Any(row => row.AttendanceDate.ToString("yyyy-MM") != request.Month)) return "All attendance dates must fall in the selected month.";
        if (request.Rows.Any(row => row.PayableValue < 0 || row.PayableValue > 1)) return "Payable value must be between 0 and 1.";
        return null;
    }

    private static async Task RollupDailyAttendanceAsync(MySqlConnection connection, int clientId, int employeeId, string month)
    {
        var summary = await connection.QuerySingleAsync<(decimal WorkingDays, decimal PresentDays, decimal PayableDays)>(@"SELECT COUNT(*) AS WorkingDays,
COALESCE(SUM(payable_value), 0) AS PresentDays,
COALESCE(SUM(payable_value), 0) AS PayableDays
FROM employee_daily_attendance
WHERE client_id=@ClientId AND employee_id=@EmployeeId AND DATE_FORMAT(attendance_date, '%Y-%m')=@Month;", new { ClientId = clientId, EmployeeId = employeeId, Month = month });
        var lop = Math.Max(0, summary.WorkingDays - summary.PayableDays);
        await connection.ExecuteAsync(@"INSERT INTO employee_monthly_attendance (client_id, employee_id, attendance_month, working_days, present_days, payable_days, lop_days, source_type, remarks)
VALUES (@ClientId, @EmployeeId, @Month, @WorkingDays, @PresentDays, @PayableDays, @LopDays, 'Date-wise', 'Rolled up from date-wise attendance')
ON DUPLICATE KEY UPDATE working_days=VALUES(working_days), present_days=VALUES(present_days), payable_days=VALUES(payable_days), lop_days=VALUES(lop_days), source_type='Date-wise', remarks=VALUES(remarks);",
            new { ClientId = clientId, EmployeeId = employeeId, Month = month, summary.WorkingDays, summary.PresentDays, summary.PayableDays, LopDays = lop });
    }

    private static async Task<bool> HasDuplicateHolidayAsync(MySqlConnection connection, SaveHolidayRequest request)
    {
        var ids = request.WorkLocationIds.Distinct().ToArray();
        return await connection.ExecuteScalarAsync<int>(@"SELECT COUNT(DISTINCT h.id)
FROM holidays h
LEFT JOIN holiday_locations hl ON hl.holiday_id = h.id
WHERE h.id <> @Id AND h.client_id=@ClientId
AND h.start_date <= @EndDate AND h.end_date >= @StartDate
AND (
    @AllLocations = TRUE
    OR h.all_locations = TRUE
    OR hl.work_location_id IN @WorkLocationIds
);", new { request.ClientId, request.Id, request.StartDate, request.EndDate, request.AllLocations, WorkLocationIds = ids.Length == 0 ? [0] : ids }) > 0;
    }

    private static Task UpsertPolicyAsync(MySqlConnection connection, MySqlTransaction transaction, int leaveTypeId, SaveLeaveTypeRequest request) =>
        connection.ExecuteAsync(@"INSERT INTO leave_type_policies (leave_type_id, entitlement, entitlement_period, pro_rate_for_new_joinees, reset_enabled, reset_frequency, carry_forward_unused_leaves, max_carry_forward_limit, encash_unused_leaves, max_encashment_limit, allow_negative_leave_balance, negative_balance_handling, allow_past_dates, past_date_limit_type, past_date_limit_days, allow_future_dates, future_date_limit_type, future_date_limit_days, effective_from, expires_on, postpone_credits_for_new_employees, postpone_credit_value, postpone_credit_unit)
VALUES (@LeaveTypeId, @Entitlement, @EntitlementPeriod, @ProRateForNewJoinees, @ResetEnabled, @ResetFrequency, @CarryForwardUnusedLeaves, @MaxCarryForwardLimit, @EncashUnusedLeaves, @MaxEncashmentLimit, @AllowNegativeLeaveBalance, @NegativeBalanceHandling, @AllowPastDates, @PastDateLimitType, @PastDateLimitDays, @AllowFutureDates, @FutureDateLimitType, @FutureDateLimitDays, @EffectiveFrom, @ExpiresOn, @PostponeCreditsForNewEmployees, @PostponeCreditValue, @PostponeCreditUnit)
ON DUPLICATE KEY UPDATE entitlement=VALUES(entitlement), entitlement_period=VALUES(entitlement_period), pro_rate_for_new_joinees=VALUES(pro_rate_for_new_joinees), reset_enabled=VALUES(reset_enabled), reset_frequency=VALUES(reset_frequency), carry_forward_unused_leaves=VALUES(carry_forward_unused_leaves), max_carry_forward_limit=VALUES(max_carry_forward_limit), encash_unused_leaves=VALUES(encash_unused_leaves), max_encashment_limit=VALUES(max_encashment_limit), allow_negative_leave_balance=VALUES(allow_negative_leave_balance), negative_balance_handling=VALUES(negative_balance_handling), allow_past_dates=VALUES(allow_past_dates), past_date_limit_type=VALUES(past_date_limit_type), past_date_limit_days=VALUES(past_date_limit_days), allow_future_dates=VALUES(allow_future_dates), future_date_limit_type=VALUES(future_date_limit_type), future_date_limit_days=VALUES(future_date_limit_days), effective_from=VALUES(effective_from), expires_on=VALUES(expires_on), postpone_credits_for_new_employees=VALUES(postpone_credits_for_new_employees), postpone_credit_value=VALUES(postpone_credit_value), postpone_credit_unit=VALUES(postpone_credit_unit);", new { LeaveTypeId = leaveTypeId, request.Entitlement, request.EntitlementPeriod, request.ProRateForNewJoinees, request.ResetEnabled, request.ResetFrequency, request.CarryForwardUnusedLeaves, request.MaxCarryForwardLimit, request.EncashUnusedLeaves, request.MaxEncashmentLimit, request.AllowNegativeLeaveBalance, request.NegativeBalanceHandling, request.AllowPastDates, request.PastDateLimitType, request.PastDateLimitDays, request.AllowFutureDates, request.FutureDateLimitType, request.FutureDateLimitDays, request.EffectiveFrom, request.ExpiresOn, request.PostponeCreditsForNewEmployees, request.PostponeCreditValue, request.PostponeCreditUnit }, transaction);

    private static Task UpsertApplicabilityAsync(MySqlConnection connection, MySqlTransaction transaction, int leaveTypeId, SaveLeaveTypeRequest request) =>
        connection.ExecuteAsync(@"INSERT INTO leave_type_applicability (leave_type_id, applicability_mode, work_location, department, designation, gender)
VALUES (@LeaveTypeId, @ApplicabilityMode, @WorkLocation, @Department, @Designation, @Gender)
ON DUPLICATE KEY UPDATE applicability_mode=VALUES(applicability_mode), work_location=VALUES(work_location), department=VALUES(department), designation=VALUES(designation), gender=VALUES(gender);", new { LeaveTypeId = leaveTypeId, request.ApplicabilityMode, request.WorkLocation, request.Department, request.Designation, request.Gender }, transaction);

    private const string LeaveTypeSelectSql = @"SELECT lt.id AS Id, lt.client_id AS ClientId, lt.name AS Name, lt.code AS Code, lt.type AS Type, lt.description AS Description, lt.is_active AS IsActive, lt.created_at AS CreatedAt, lt.updated_at AS UpdatedAt,
p.entitlement AS Entitlement, p.entitlement_period AS EntitlementPeriod, p.pro_rate_for_new_joinees AS ProRateForNewJoinees, p.reset_enabled AS ResetEnabled, p.reset_frequency AS ResetFrequency, p.carry_forward_unused_leaves AS CarryForwardUnusedLeaves, p.max_carry_forward_limit AS MaxCarryForwardLimit, p.encash_unused_leaves AS EncashUnusedLeaves, p.max_encashment_limit AS MaxEncashmentLimit, p.allow_negative_leave_balance AS AllowNegativeLeaveBalance, p.negative_balance_handling AS NegativeBalanceHandling, p.allow_past_dates AS AllowPastDates, p.past_date_limit_type AS PastDateLimitType, p.past_date_limit_days AS PastDateLimitDays, p.allow_future_dates AS AllowFutureDates, p.future_date_limit_type AS FutureDateLimitType, p.future_date_limit_days AS FutureDateLimitDays, p.effective_from AS EffectiveFrom, p.expires_on AS ExpiresOn, p.postpone_credits_for_new_employees AS PostponeCreditsForNewEmployees, p.postpone_credit_value AS PostponeCreditValue, p.postpone_credit_unit AS PostponeCreditUnit,
a.applicability_mode AS ApplicabilityMode, a.work_location AS WorkLocation, a.department AS Department, a.designation AS Designation, a.gender AS Gender
FROM leave_types lt JOIN leave_type_policies p ON p.leave_type_id = lt.id JOIN leave_type_applicability a ON a.leave_type_id = lt.id";

    private async Task<bool> IsFormulaBasedSalaryComponentAsync(int componentId)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("USE payroll;");
        var setupJson = await connection.ExecuteScalarAsync<string?>("SELECT SetupJson FROM PayrollSetups ORDER BY Id LIMIT 1") ?? "{}";
        using var document = JsonDocument.Parse(setupJson);
        if (!document.RootElement.TryGetProperty("salaryComponents", out var components)) return false;
        return components.EnumerateArray().Any(component =>
            component.TryGetProperty("id", out var id) && id.GetInt32() == componentId &&
            component.TryGetProperty("calculationType", out var calculationType) &&
            calculationType.GetString()?.Equals("Formula", StringComparison.OrdinalIgnoreCase) == true);
    }

    private static bool IsValidStatus(string status) =>
        status is "Not Started" or "In Progress" or "Completed" or "Disabled";
    private static bool IsValidDay(int day) => day is >= 1 and <= 31;
    private static bool IsValidMonth(string month) => month.Length == 7 && DateTime.TryParse($"{month}-01", out _);

    private static async Task SeedAsync(MySqlConnection connection, int clientId = 0)
    {
        if (clientId <= 0) return;
        await connection.ExecuteAsync(@"INSERT IGNORE INTO ModuleSettings (client_id, ModuleCode, IsEnabled, SettingsJson)
VALUES (@ClientId, 'leave_attendance', FALSE, JSON_OBJECT());", new { ClientId = clientId });
        foreach (var step in DefaultSteps)
        {
            await connection.ExecuteAsync(@"INSERT IGNORE INTO ModuleSetupProgress (client_id, ModuleCode, StepCode, Title, Description, Status, IsMandatory, CanDisable)
VALUES (@ClientId, 'leave_attendance', @Code, @Title, @Description, 'Not Started', @IsMandatory, @CanDisable);", new { ClientId = clientId, step.Code, step.Title, step.Description, step.IsMandatory, step.CanDisable });
        }
        await SeedPreferencesAsync(connection, clientId);
        await SeedAttendanceSettingsAsync(connection, clientId);
    }

    private static Task SeedPreferencesAsync(MySqlConnection connection, int clientId) =>
        connection.ExecuteAsync(@"INSERT INTO leave_attendance_preferences (client_id, attendance_cycle_start_day, attendance_cycle_end_day, payroll_report_generation_day)
SELECT @ClientId, 1, 25, 28 WHERE NOT EXISTS (SELECT 1 FROM leave_attendance_preferences WHERE client_id=@ClientId);", new { ClientId = clientId });

    private static Task SeedAttendanceSettingsAsync(MySqlConnection connection, int clientId) =>
        connection.ExecuteAsync(@"INSERT INTO attendance_settings (client_id, check_in_time, check_out_time, working_hours_calculation, minimum_hours_for_half_day, minimum_hours_for_full_day, maximum_hours_allowed_for_full_day)
SELECT @ClientId, '09:00:00', '18:00:00', 'First check-in and last check-out', 4, 8, 12 WHERE NOT EXISTS (SELECT 1 FROM attendance_settings WHERE client_id=@ClientId);", new { ClientId = clientId });

    private static async Task EnsureClientScopeAsync(MySqlConnection connection)
    {
        foreach (var table in new[] { "ModuleSettings", "ModuleSetupProgress", "leave_attendance_preferences", "attendance_settings", "employee_monthly_attendance", "employee_daily_attendance", "leave_types", "holidays", "employee_leave_balances", "leave_balance_import_logs" })
            await AddClientColumnIfMissingAsync(connection, table);
        var clientId = await connection.ExecuteScalarAsync<int?>("SELECT Id FROM Clients ORDER BY Id LIMIT 1");
        if (clientId is null) return;
        foreach (var table in new[] { "ModuleSettings", "ModuleSetupProgress", "leave_attendance_preferences", "attendance_settings", "employee_monthly_attendance", "employee_daily_attendance", "leave_types", "holidays", "employee_leave_balances", "leave_balance_import_logs" })
            await connection.ExecuteAsync($"UPDATE {table} SET client_id=@ClientId WHERE client_id IS NULL", new { ClientId = clientId });
        await DropIndexIfExistsAsync(connection, "leave_types", "UX_leave_types_code");
        await CreateIndexIfMissingAsync(connection, "ModuleSettings", "UX_ModuleSettings_Client_Module", "CREATE UNIQUE INDEX UX_ModuleSettings_Client_Module ON ModuleSettings (client_id, ModuleCode)");
        await CreateIndexIfMissingAsync(connection, "ModuleSetupProgress", "UX_ModuleSetupProgress_Client_Module_Step", "CREATE UNIQUE INDEX UX_ModuleSetupProgress_Client_Module_Step ON ModuleSetupProgress (client_id, ModuleCode, StepCode)");
        await CreateIndexIfMissingAsync(connection, "leave_attendance_preferences", "UX_preferences_client", "CREATE UNIQUE INDEX UX_preferences_client ON leave_attendance_preferences (client_id)");
        await CreateIndexIfMissingAsync(connection, "attendance_settings", "UX_attendance_client", "CREATE UNIQUE INDEX UX_attendance_client ON attendance_settings (client_id)");
        await CreateIndexIfMissingAsync(connection, "leave_types", "UX_leave_types_client_code", "CREATE UNIQUE INDEX UX_leave_types_client_code ON leave_types (client_id, code)");
    }

    private static async Task AddClientColumnIfMissingAsync(MySqlConnection connection, string table)
    {
        var exists = await connection.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM information_schema.columns
WHERE table_schema = DATABASE() AND table_name = @TableName AND column_name = 'client_id'", new { TableName = table });
        if (exists == 0)
            await connection.ExecuteAsync($"ALTER TABLE `{table}` ADD COLUMN client_id INT NULL");
    }

    private static async Task CreateIndexIfMissingAsync(MySqlConnection connection, string table, string indexName, string createSql)
    {
        var exists = await connection.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM information_schema.statistics
WHERE table_schema = DATABASE() AND table_name = @TableName AND index_name = @IndexName", new { TableName = table, IndexName = indexName });
        if (exists == 0)
            await connection.ExecuteAsync(createSql);
    }

    private static async Task DropIndexIfExistsAsync(MySqlConnection connection, string table, string indexName)
    {
        var exists = await connection.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM information_schema.statistics
WHERE table_schema = DATABASE() AND table_name = @TableName AND index_name = @IndexName", new { TableName = table, IndexName = indexName });
        if (exists > 0)
            await connection.ExecuteAsync($"DROP INDEX `{indexName}` ON `{table}`");
    }
}
