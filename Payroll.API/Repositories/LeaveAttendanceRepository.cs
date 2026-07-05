using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace Payroll.API.Repositories;

public class LeaveAttendanceRepository(IConfiguration configuration)
{
    private static readonly ConcurrentDictionary<Guid, ClientImportJobStatus> LeaveTypeImportJobs = new();

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
        await connection.ExecuteAsync(@"
CREATE TABLE IF NOT EXISTS modulesettings (
    Id INT PRIMARY KEY AUTO_INCREMENT,
    ModuleCode VARCHAR(80) NOT NULL,
    IsEnabled BOOLEAN NOT NULL DEFAULT FALSE,
    SettingsJson JSON NULL,
    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    UNIQUE KEY UX_ModuleSettings_ModuleCode (ModuleCode)
);
CREATE TABLE IF NOT EXISTS modulesetupprogress (
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
    work_location_id INT NOT NULL DEFAULT 0,
    work_week VARCHAR(80) NOT NULL DEFAULT '',
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
    INDEX IX_monthly_attendance_client_month (client_id, attendance_month)
);
CREATE TABLE IF NOT EXISTS employee_daily_attendance (
    id INT PRIMARY KEY AUTO_INCREMENT,
    client_id INT NOT NULL,
    employee_id INT NOT NULL,
    attendance_date DATE NOT NULL,
    status VARCHAR(30) NOT NULL DEFAULT 'Present',
    payable_value DECIMAL(4,2) NOT NULL DEFAULT 1,
    check_in_time TIME NULL,
    check_out_time TIME NULL,
    total_hours DECIMAL(5,2) NOT NULL DEFAULT 0,
    remarks VARCHAR(600),
    created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    UNIQUE KEY UX_daily_attendance_employee_date (client_id, employee_id, attendance_date),
    INDEX IX_daily_attendance_client_date (client_id, attendance_date)
);
CREATE TABLE IF NOT EXISTS attendance_geo_fence_rules (
    id INT PRIMARY KEY AUTO_INCREMENT,
    client_id INT NOT NULL,
    name VARCHAR(180) NOT NULL,
    scope_type VARCHAR(40) NOT NULL DEFAULT 'Work Location',
    work_location_id INT NULL,
    latitude DECIMAL(10,7) NOT NULL,
    longitude DECIMAL(10,7) NOT NULL,
    radius_meters INT NOT NULL DEFAULT 100,
    gps_tolerance_meters INT NOT NULL DEFAULT 30,
    strictness VARCHAR(60) NOT NULL DEFAULT 'Block outside fence',
    allow_check_in BOOLEAN NOT NULL DEFAULT TRUE,
    allow_check_out BOOLEAN NOT NULL DEFAULT TRUE,
    effective_from DATE NOT NULL,
    effective_to DATE NULL,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    priority INT NOT NULL DEFAULT 20,
    created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    INDEX IX_geo_fence_client_scope (client_id, scope_type, is_active),
    INDEX IX_geo_fence_location (work_location_id)
);
CREATE TABLE IF NOT EXISTS attendance_geo_fence_rule_employees (
    id INT PRIMARY KEY AUTO_INCREMENT,
    geo_fence_rule_id INT NOT NULL,
    employee_id INT NOT NULL,
    created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE KEY UX_geo_fence_rule_employee (geo_fence_rule_id, employee_id),
    INDEX IX_geo_fence_employee (employee_id),
    CONSTRAINT FK_geo_fence_rule_employee_rule FOREIGN KEY (geo_fence_rule_id) REFERENCES attendance_geo_fence_rules(id) ON DELETE CASCADE,
    CONSTRAINT FK_geo_fence_rule_employee_employee FOREIGN KEY (employee_id) REFERENCES employees(Id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS attendance_groups (
    id INT PRIMARY KEY AUTO_INCREMENT,
    client_id INT NOT NULL,
    name VARCHAR(180) NOT NULL,
    work_location_id INT NOT NULL,
    department VARCHAR(150) NOT NULL DEFAULT '',
    designation VARCHAR(150) NOT NULL DEFAULT '',
    work_week VARCHAR(80) NOT NULL DEFAULT '',
    attendance_cycle_start_day INT NOT NULL DEFAULT 1,
    attendance_cycle_end_day INT NOT NULL DEFAULT 25,
    payroll_report_generation_day INT NOT NULL DEFAULT 28,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    UNIQUE KEY UX_attendance_groups_client_name (client_id, name),
    INDEX IX_attendance_groups_client_location (client_id, work_location_id)
);
CREATE TABLE IF NOT EXISTS attendance_group_employees (
    id INT PRIMARY KEY AUTO_INCREMENT,
    attendance_group_id INT NOT NULL,
    employee_id INT NOT NULL,
    created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE KEY UX_attendance_group_employee (attendance_group_id, employee_id),
    INDEX IX_attendance_group_employee_employee (employee_id),
    CONSTRAINT FK_attendance_group_employee_group FOREIGN KEY (attendance_group_id) REFERENCES attendance_groups(id) ON DELETE CASCADE,
    CONSTRAINT FK_attendance_group_employee_employee FOREIGN KEY (employee_id) REFERENCES employees(Id) ON DELETE CASCADE
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
    UNIQUE KEY UX_leave_type_policies_leave_type (leave_type_id)
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
    UNIQUE KEY UX_leave_type_applicability_leave_type (leave_type_id)
);
CREATE TABLE IF NOT EXISTS holidays (
    id INT PRIMARY KEY AUTO_INCREMENT,
    name VARCHAR(180) NOT NULL,
    holiday_type VARCHAR(40) NOT NULL DEFAULT 'Holiday',
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
    INDEX IX_holiday_locations_location (work_location_id)
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
    INDEX IX_employee_leave_balances_employee (employee_id)
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
    INDEX IX_leave_balance_import_errors_log (import_log_id)
);");
        await EnsureForeignKeyAsync(connection, "employee_monthly_attendance", "FK_monthly_attendance_employee", "FOREIGN KEY (employee_id) REFERENCES employees(Id) ON DELETE CASCADE");
        await EnsureForeignKeyAsync(connection, "employee_daily_attendance", "FK_daily_attendance_employee", "FOREIGN KEY (employee_id) REFERENCES employees(Id) ON DELETE CASCADE");
        await EnsureForeignKeyAsync(connection, "attendance_groups", "FK_attendance_groups_client", "FOREIGN KEY (client_id) REFERENCES clients(Id) ON DELETE CASCADE");
        await EnsureForeignKeyAsync(connection, "attendance_groups", "FK_attendance_groups_location", "FOREIGN KEY (work_location_id) REFERENCES worklocations(Id)");
        await EnsureForeignKeyAsync(connection, "attendance_group_employees", "FK_attendance_group_employee_group", "FOREIGN KEY (attendance_group_id) REFERENCES attendance_groups(id) ON DELETE CASCADE");
        await EnsureForeignKeyAsync(connection, "attendance_group_employees", "FK_attendance_group_employee_employee", "FOREIGN KEY (employee_id) REFERENCES employees(Id) ON DELETE CASCADE");
        await EnsureForeignKeyAsync(connection, "leave_type_policies", "FK_leave_type_policies_type", "FOREIGN KEY (leave_type_id) REFERENCES leave_types(id) ON DELETE CASCADE");
        await EnsureForeignKeyAsync(connection, "leave_type_applicability", "FK_leave_type_applicability_type", "FOREIGN KEY (leave_type_id) REFERENCES leave_types(id) ON DELETE CASCADE");
        await EnsureForeignKeyAsync(connection, "holiday_locations", "FK_holiday_locations_holiday", "FOREIGN KEY (holiday_id) REFERENCES holidays(id) ON DELETE CASCADE");
        await EnsureForeignKeyAsync(connection, "employee_leave_balances", "FK_employee_leave_balances_employee", "FOREIGN KEY (employee_id) REFERENCES employees(Id) ON DELETE CASCADE");
        await EnsureForeignKeyAsync(connection, "employee_leave_balances", "FK_employee_leave_balances_leave_type", "FOREIGN KEY (leave_type_id) REFERENCES leave_types(id) ON DELETE CASCADE");
        await EnsureForeignKeyAsync(connection, "leave_balance_import_errors", "FK_leave_balance_import_errors_log", "FOREIGN KEY (import_log_id) REFERENCES leave_balance_import_logs(id) ON DELETE CASCADE");
        await EnsureColumnAsync(connection, "holidays", "holiday_type", "VARCHAR(40) NOT NULL DEFAULT 'Holiday' AFTER name");
        await EnsureColumnAsync(connection, "employee_daily_attendance", "check_in_time", "TIME NULL AFTER payable_value");
        await EnsureColumnAsync(connection, "employee_daily_attendance", "check_out_time", "TIME NULL AFTER check_in_time");
        await EnsureColumnAsync(connection, "employee_daily_attendance", "total_hours", "DECIMAL(5,2) NOT NULL DEFAULT 0 AFTER check_out_time");
        await EnsureColumnAsync(connection, "attendance_groups", "work_week", "VARCHAR(80) NOT NULL DEFAULT '' AFTER designation");
        await EnsureColumnAsync(connection, "attendance_groups", "attendance_cycle_start_day", "INT NOT NULL DEFAULT 1 AFTER work_week");
        await EnsureColumnAsync(connection, "attendance_groups", "attendance_cycle_end_day", "INT NOT NULL DEFAULT 25 AFTER attendance_cycle_start_day");
        await EnsureColumnAsync(connection, "attendance_groups", "payroll_report_generation_day", "INT NOT NULL DEFAULT 28 AFTER attendance_cycle_end_day");
        await EnsureColumnAsync(connection, "leave_attendance_preferences", "work_location_id", "INT NOT NULL DEFAULT 0 AFTER client_id");
        await EnsureColumnAsync(connection, "leave_attendance_preferences", "work_week", "VARCHAR(80) NOT NULL DEFAULT '' AFTER work_location_id");
        await EnsureColumnAsync(connection, "dropdownmasters", "ConfigJson", "JSON NULL AFTER Value");
        await connection.ExecuteAsync("UPDATE leave_attendance_preferences SET work_location_id=0 WHERE work_location_id IS NULL");
        await EnsureClientScopeAsync(connection);
    }

    public async Task<LeaveAttendanceSetup> GetAsync(int clientId)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        var isEnabled = await connection.ExecuteScalarAsync<bool?>("SELECT IsEnabled FROM modulesettings WHERE ModuleCode = 'leave_attendance' AND client_id=@ClientId", new { ClientId = clientId }) ?? false;
        var steps = (await connection.QueryAsync<LeaveAttendanceSetupStep>(@"SELECT StepCode AS Code, Title, Description, Status, IsMandatory, CanDisable, UpdatedAt 
FROM modulesetupprogress WHERE ModuleCode = 'leave_attendance' AND client_id=@ClientId ORDER BY FIELD(StepCode, 'preferences', 'leave_types', 'holiday', 'attendance', 'import_balance');", new { ClientId = clientId })).ToList();
        return new LeaveAttendanceSetup { ClientId = clientId, IsEnabled = isEnabled, Steps = steps };
    }

    public async Task<LeaveAttendanceSetup> SetEnabledAsync(int clientId, bool isEnabled)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync(@"INSERT INTO modulesettings (client_id, ModuleCode, IsEnabled, SettingsJson)
VALUES (@ClientId, 'leave_attendance', @IsEnabled, JSON_OBJECT())
ON DUPLICATE KEY UPDATE IsEnabled=@IsEnabled", new { IsEnabled = isEnabled, ClientId = clientId });
        if (!isEnabled)
            await connection.ExecuteAsync("UPDATE modulesetupprogress SET Status = CASE WHEN IsMandatory THEN Status ELSE 'Disabled' END WHERE ModuleCode = 'leave_attendance' AND client_id=@ClientId", new { ClientId = clientId });
        return await GetAsync(clientId);
    }

    public async Task<LeaveAttendanceSetup?> UpdateStepAsync(int clientId, string stepCode, string status)
    {
        if (!IsValidStatus(status)) return null;
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        var step = await connection.QueryFirstOrDefaultAsync<LeaveAttendanceSetupStep>(@"SELECT StepCode AS Code, Title, Description, Status, IsMandatory, CanDisable
FROM modulesetupprogress WHERE ModuleCode = 'leave_attendance' AND client_id=@ClientId AND StepCode = @StepCode", new { ClientId = clientId, StepCode = stepCode });
        if (step is null || (step.IsMandatory && status == "Disabled")) return null;
        await connection.ExecuteAsync(@"UPDATE modulesetupprogress SET Status = @Status WHERE ModuleCode = 'leave_attendance' AND client_id=@ClientId AND StepCode = @StepCode", new { ClientId = clientId, StepCode = stepCode, Status = status });
        return await GetAsync(clientId);
    }

    public async Task<LeaveAttendancePreferences> GetPreferencesAsync(int clientId, int? workLocationId = null)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        return await GetPreferencesAsync(connection, clientId, workLocationId);
    }

    private static async Task<LeaveAttendancePreferences> GetPreferencesAsync(MySqlConnection connection, int clientId, int? workLocationId = null)
    {
        await EnsureColumnAsync(connection, "leave_attendance_preferences", "work_location_id", "INT NOT NULL DEFAULT 0 AFTER client_id");
        await EnsureColumnAsync(connection, "leave_attendance_preferences", "work_week", "VARCHAR(80) NOT NULL DEFAULT '' AFTER work_location_id");
        var locationId = workLocationId.GetValueOrDefault();
        return await connection.QueryFirstOrDefaultAsync<LeaveAttendancePreferences>(@"SELECT p.id AS Id, p.client_id AS ClientId,
NULLIF(p.work_location_id, 0) AS WorkLocationId,
CASE WHEN p.work_location_id = 0 THEN 'All locations' ELSE COALESCE(w.Name, 'All locations') END AS WorkLocationName,
p.work_week AS WorkWeek,
attendance_cycle_start_day AS AttendanceCycleStartDay,
attendance_cycle_end_day AS AttendanceCycleEndDay,
payroll_report_generation_day AS PayrollReportGenerationDay,
include_leave_encashment_in_pay_run AS IncludeLeaveEncashmentInPayRun,
leave_encashment_salary_component_id AS LeaveEncashmentSalaryComponentId,
p.created_at AS CreatedAt,
p.updated_at AS UpdatedAt
FROM leave_attendance_preferences p
LEFT JOIN worklocations w ON w.Id = p.work_location_id
WHERE p.client_id=@ClientId AND ((@WorkLocationId > 0 AND p.work_location_id=@WorkLocationId) OR p.work_location_id=0)
ORDER BY CASE WHEN p.work_location_id=@WorkLocationId THEN 0 ELSE 1 END
LIMIT 1;", new { ClientId = clientId, WorkLocationId = locationId }) ?? new LeaveAttendancePreferences { ClientId = clientId, WorkLocationId = locationId > 0 ? locationId : null };
    }

    public async Task<(LeaveAttendancePreferences? Preferences, string? Error)> SavePreferencesAsync(SaveLeaveAttendancePreferencesRequest request)
    {
        var validationError = await ValidatePreferencesAsync(request);
        if (validationError is not null) return (null, validationError);
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        var payload = new
        {
            request.ClientId,
            WorkLocationId = request.WorkLocationId is > 0 ? request.WorkLocationId : 0,
            WorkWeek = request.WorkWeek.Trim(),
            request.AttendanceCycleStartDay,
            request.AttendanceCycleEndDay,
            request.PayrollReportGenerationDay,
            request.IncludeLeaveEncashmentInPayRun,
            request.LeaveEncashmentSalaryComponentId
        };
        await connection.ExecuteAsync(@"INSERT INTO leave_attendance_preferences (client_id, work_location_id, work_week, attendance_cycle_start_day, attendance_cycle_end_day, payroll_report_generation_day, include_leave_encashment_in_pay_run, leave_encashment_salary_component_id)
VALUES (@ClientId, @WorkLocationId, @WorkWeek, @AttendanceCycleStartDay, @AttendanceCycleEndDay, @PayrollReportGenerationDay, @IncludeLeaveEncashmentInPayRun, @LeaveEncashmentSalaryComponentId)
ON DUPLICATE KEY UPDATE
work_week = @WorkWeek,
attendance_cycle_start_day = @AttendanceCycleStartDay,
attendance_cycle_end_day = @AttendanceCycleEndDay,
payroll_report_generation_day = @PayrollReportGenerationDay,
include_leave_encashment_in_pay_run = @IncludeLeaveEncashmentInPayRun,
leave_encashment_salary_component_id = @LeaveEncashmentSalaryComponentId
;", payload);
        return (await GetPreferencesAsync(connection, request.ClientId, request.WorkLocationId), null);
    }

    public async Task<AttendanceSettings> GetAttendanceSettingsAsync(int clientId)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        return await connection.QueryFirstOrDefaultAsync<AttendanceSettings>(@"SELECT id AS Id, client_id AS ClientId,
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
FROM attendance_settings WHERE client_id=@ClientId LIMIT 1;", new { ClientId = clientId }) ?? new AttendanceSettings { ClientId = clientId };
    }

    public async Task<(AttendanceSettings? Settings, string? Error)> SaveAttendanceSettingsAsync(SaveAttendanceSettingsRequest request)
    {
        var error = ValidateAttendanceSettings(request);
        if (error is not null) return (null, error);
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync(@"INSERT INTO attendance_settings (client_id, check_in_time, check_out_time, working_hours_calculation, minimum_hours_for_half_day, minimum_hours_for_full_day, maximum_hours_allowed_for_full_day, allow_regularization_requests, regularization_window, past_days_allowed, restrict_regularization_requests_per_month, max_regularization_requests_per_month)
VALUES (@ClientId, @CheckInTime, @CheckOutTime, @WorkingHoursCalculation, @MinimumHoursForHalfDay, @MinimumHoursForFullDay, @MaximumHoursAllowedForFullDay, @AllowRegularizationRequests, @RegularizationWindow, @PastDaysAllowed, @RestrictRegularizationRequestsPerMonth, @MaxRegularizationRequestsPerMonth)
ON DUPLICATE KEY UPDATE
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
;", request);
        return (await GetAttendanceSettingsAsync(request.ClientId), null);
    }

    public async Task<IEnumerable<GeoFenceRule>> GetGeoFenceRulesAsync(int clientId, string? scopeType = null)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        var rows = (await connection.QueryAsync<GeoFenceRule>(GeoFenceRuleSelectSql + @"
WHERE r.client_id=@ClientId AND (@ScopeType IS NULL OR r.scope_type=@ScopeType)
GROUP BY r.id
ORDER BY r.priority, r.name;", new { ClientId = clientId, ScopeType = string.IsNullOrWhiteSpace(scopeType) ? null : scopeType })).ToList();
        await LoadGeoFenceEmployeesAsync(connection, rows);
        return rows;
    }

    public async Task<GeoFenceRule?> GetApplicableGeoFenceRuleAsync(int clientId, int employeeId, DateTime? onDate = null)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        var date = (onDate ?? DateTime.Today).Date;
        var rows = (await connection.QueryAsync<GeoFenceRule>(GeoFenceRuleSelectSql + @"
LEFT JOIN attendance_geo_fence_rule_employees ge ON ge.geo_fence_rule_id = r.id
LEFT JOIN employees e ON e.Id=@EmployeeId AND e.ClientId=r.client_id
WHERE r.client_id=@ClientId AND r.is_active=TRUE AND r.effective_from <= @Date AND (r.effective_to IS NULL OR r.effective_to >= @Date)
AND (
    (r.scope_type='Employee' AND ge.employee_id=@EmployeeId)
    OR (r.scope_type='Work Location' AND r.work_location_id=e.WorkLocationId)
    OR r.scope_type='Client Default'
)
GROUP BY r.id
ORDER BY CASE r.scope_type WHEN 'Employee' THEN 1 WHEN 'Work Location' THEN 2 ELSE 3 END, r.priority
LIMIT 1;", new { ClientId = clientId, EmployeeId = employeeId, Date = date })).ToList();
        await LoadGeoFenceEmployeesAsync(connection, rows);
        return rows.FirstOrDefault();
    }

    public async Task<(GeoFenceRule? Rule, string? Error)> SaveGeoFenceRuleAsync(SaveGeoFenceRuleRequest request)
    {
        var error = ValidateGeoFenceRule(request);
        if (error is not null) return (null, error);
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        request.Priority = request.ScopeType == "Employee" ? 10 : request.ScopeType == "Work Location" ? 20 : 30;
        await using var transaction = await connection.BeginTransactionAsync();
        var id = request.Id;
        if (id == 0)
        {
            id = (int)await connection.ExecuteScalarAsync<long>(@"INSERT INTO attendance_geo_fence_rules (client_id, name, scope_type, work_location_id, latitude, longitude, radius_meters, gps_tolerance_meters, strictness, allow_check_in, allow_check_out, effective_from, effective_to, is_active, priority)
VALUES (@ClientId, @Name, @ScopeType, @WorkLocationId, @Latitude, @Longitude, @RadiusMeters, @GpsToleranceMeters, @Strictness, @AllowCheckIn, @AllowCheckOut, @EffectiveFrom, @EffectiveTo, @IsActive, @Priority); SELECT LAST_INSERT_ID();", CleanGeoFenceRequest(request), transaction);
        }
        else
        {
            var updated = await connection.ExecuteAsync(@"UPDATE attendance_geo_fence_rules SET name=@Name, scope_type=@ScopeType, work_location_id=@WorkLocationId, latitude=@Latitude, longitude=@Longitude, radius_meters=@RadiusMeters, gps_tolerance_meters=@GpsToleranceMeters, strictness=@Strictness, allow_check_in=@AllowCheckIn, allow_check_out=@AllowCheckOut, effective_from=@EffectiveFrom, effective_to=@EffectiveTo, is_active=@IsActive, priority=@Priority WHERE id=@Id AND client_id=@ClientId", CleanGeoFenceRequest(request), transaction);
            if (updated == 0) return (null, "Geo-fence rule was not found for the selected client.");
            await connection.ExecuteAsync("DELETE FROM attendance_geo_fence_rule_employees WHERE geo_fence_rule_id=@Id", new { Id = id }, transaction);
        }
        if (request.ScopeType == "Employee" && request.EmployeeIds.Count > 0)
            await connection.ExecuteAsync("INSERT INTO attendance_geo_fence_rule_employees (geo_fence_rule_id, employee_id) VALUES (@RuleId, @EmployeeId)", request.EmployeeIds.Distinct().Select(employeeId => new { RuleId = id, EmployeeId = employeeId }), transaction);
        await transaction.CommitAsync();
        return (await GetGeoFenceRuleAsync(id, request.ClientId), null);
    }

    public async Task<bool> DeleteGeoFenceRuleAsync(int id, int clientId)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        return await connection.ExecuteAsync("DELETE FROM attendance_geo_fence_rules WHERE id=@Id AND client_id=@ClientId", new { Id = id, ClientId = clientId }) > 0;
    }

    public async Task<IEnumerable<AttendanceGroup>> GetAttendanceGroupsAsync(int clientId = 0)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        var rows = (await connection.QueryAsync<AttendanceGroup>(AttendanceGroupSelectSql + @"
WHERE (@ClientId <= 0 OR g.client_id=@ClientId)
GROUP BY g.id
ORDER BY c.Name, w.Name, g.name;", new { ClientId = clientId })).ToList();
        await LoadAttendanceGroupEmployeesAsync(connection, rows);
        return rows;
    }

    public async Task<(AttendanceGroup? Group, string? Error)> SaveAttendanceGroupAsync(SaveAttendanceGroupRequest request)
    {
        var validationError = await ValidateAttendanceGroupAsync(request);
        if (validationError is not null) return (null, validationError);

        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var id = request.Id;
        var payload = CleanAttendanceGroupRequest(request);
        if (id == 0)
        {
            id = (int)await connection.ExecuteScalarAsync<long>(@"INSERT INTO attendance_groups (client_id, name, work_location_id, department, designation, work_week, attendance_cycle_start_day, attendance_cycle_end_day, payroll_report_generation_day, is_active)
VALUES (@ClientId, @Name, @WorkLocationId, @Department, @Designation, @WorkWeek, @AttendanceCycleStartDay, @AttendanceCycleEndDay, @PayrollReportGenerationDay, @IsActive); SELECT LAST_INSERT_ID();", payload, transaction);
        }
        else
        {
            var updated = await connection.ExecuteAsync(@"UPDATE attendance_groups SET name=@Name, work_location_id=@WorkLocationId, department=@Department, designation=@Designation, work_week=@WorkWeek, attendance_cycle_start_day=@AttendanceCycleStartDay, attendance_cycle_end_day=@AttendanceCycleEndDay, payroll_report_generation_day=@PayrollReportGenerationDay, is_active=@IsActive WHERE id=@Id AND client_id=@ClientId", payload, transaction);
            if (updated == 0) return (null, "Attendance group was not found for the selected client.");
        }
        var employeeIds = (request.EmployeeIds ?? new List<int>()).Distinct().ToArray();
        await connection.ExecuteAsync("DELETE FROM attendance_group_employees WHERE attendance_group_id=@Id", new { Id = id }, transaction);
        await connection.ExecuteAsync("INSERT INTO attendance_group_employees (attendance_group_id, employee_id) VALUES (@GroupId, @EmployeeId)", employeeIds.Select(employeeId => new { GroupId = id, EmployeeId = employeeId }), transaction);
        await transaction.CommitAsync();
        return (await GetAttendanceGroupAsync(id, request.ClientId), null);
    }

    public async Task<bool> DeleteAttendanceGroupAsync(int id, int clientId)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        return await connection.ExecuteAsync("DELETE FROM attendance_groups WHERE id=@Id AND client_id=@ClientId", new { Id = id, ClientId = clientId }) > 0;
    }

    public async Task<AttendanceReviewContext> GetAttendanceReviewContextAsync(int clientId, string month, int? workLocationId = null)
    {
        var monthStart = IsValidMonth(month) ? DateTime.Parse($"{month}-01") : new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        var monthEnd = monthStart.AddMonths(1).AddDays(-1);
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        var settings = await connection.QueryFirstOrDefaultAsync<AttendanceSettings>(@"SELECT id AS Id, client_id AS ClientId,
check_in_time AS CheckInTime, check_out_time AS CheckOutTime, working_hours_calculation AS WorkingHoursCalculation,
minimum_hours_for_half_day AS MinimumHoursForHalfDay, minimum_hours_for_full_day AS MinimumHoursForFullDay, maximum_hours_allowed_for_full_day AS MaximumHoursAllowedForFullDay,
allow_regularization_requests AS AllowRegularizationRequests, regularization_window AS RegularizationWindow, past_days_allowed AS PastDaysAllowed,
restrict_regularization_requests_per_month AS RestrictRegularizationRequestsPerMonth, max_regularization_requests_per_month AS MaxRegularizationRequestsPerMonth,
created_at AS CreatedAt, updated_at AS UpdatedAt
FROM attendance_settings WHERE client_id=@ClientId LIMIT 1;", new { ClientId = clientId }) ?? new AttendanceSettings { ClientId = clientId };
        var preferences = await GetPreferencesAsync(connection, clientId, workLocationId);
        var schedule = new ClientAttendanceSchedule
        {
            WorkWeek = preferences.WorkWeek ?? string.Empty,
            SalaryDays = "Actual days",
            FixedDays = "30",
            PayDay = "Last working day",
            FirstPayPeriod = string.Empty
        };
        var balances = (await connection.QueryAsync<EmployeeLeaveBalanceSummary>(@"SELECT b.employee_id AS EmployeeId, lt.id AS LeaveTypeId, lt.code AS LeaveTypeCode, lt.name AS LeaveTypeName,
b.balance_count AS Balance, b.balance_date AS BalanceDate, p.allow_negative_leave_balance AS AllowNegativeLeaveBalance
FROM employee_leave_balances b
JOIN leave_types lt ON lt.id=b.leave_type_id AND lt.client_id=@ClientId
JOIN leave_type_policies p ON p.leave_type_id=lt.id
JOIN (
    SELECT employee_id, leave_type_id, MAX(balance_date) AS balance_date
    FROM employee_leave_balances
    WHERE client_id=@ClientId AND balance_date<=@MonthEnd
    GROUP BY employee_id, leave_type_id
) latest ON latest.employee_id=b.employee_id AND latest.leave_type_id=b.leave_type_id AND latest.balance_date=b.balance_date
WHERE b.client_id=@ClientId;", new { ClientId = clientId, MonthEnd = monthEnd })).ToList();
        var holidayStart = monthStart.AddMonths(-1);
        var holidayRows = new List<Holiday>();
        foreach (var year in new[] { holidayStart.Year, monthStart.Year }.Distinct())
            holidayRows.AddRange(await GetHolidaysAsync(clientId, year, workLocationId is > 0 ? workLocationId : null));
        var holidays = holidayRows
            .Where(holiday => holiday.StartDate.Date <= monthEnd && holiday.EndDate.Date >= holidayStart)
            .GroupBy(holiday => holiday.Id)
            .Select(group => group.First())
            .ToList();
        return new AttendanceReviewContext { Settings = settings, Schedule = schedule, Preferences = preferences, Holidays = holidays, LeaveBalances = balances };
    }

    public async Task<IEnumerable<EmployeeMonthlyAttendance>> GetMonthlyAttendanceAsync(int clientId, string month, int? workLocationId = null)
    {
        if (!IsValidMonth(month)) return [];
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await EnsureColumnAsync(connection, "attendance_groups", "work_week", "VARCHAR(80) NOT NULL DEFAULT '' AFTER designation");
        await EnsureColumnAsync(connection, "attendance_groups", "attendance_cycle_start_day", "INT NOT NULL DEFAULT 1 AFTER work_week");
        await EnsureColumnAsync(connection, "attendance_groups", "attendance_cycle_end_day", "INT NOT NULL DEFAULT 25 AFTER attendance_cycle_start_day");
        await EnsureColumnAsync(connection, "attendance_groups", "payroll_report_generation_day", "INT NOT NULL DEFAULT 28 AFTER attendance_cycle_end_day");
        var locationId = workLocationId.GetValueOrDefault();
        return await connection.QueryAsync<EmployeeMonthlyAttendance>(@"SELECT e.Id AS EmployeeId, e.EmployeeCode, CONCAT(e.FirstName, ' ', e.LastName) AS EmployeeName, e.Department, e.WorkLocationId,
g.id AS AttendanceGroupId,
COALESCE(g.name, '') AS AttendanceGroupName,
COALESCE(g.work_week, '') AS WorkWeek,
g.attendance_cycle_start_day AS AttendanceCycleStartDay,
g.attendance_cycle_end_day AS AttendanceCycleEndDay,
g.payroll_report_generation_day AS PayrollReportGenerationDay,
@Month AS Month,
COALESCE(a.working_days, 0) AS WorkingDays,
COALESCE(a.present_days, 0) AS PresentDays,
COALESCE(a.payable_days, 0) AS PayableDays,
COALESCE(a.lop_days, 0) AS LopDays,
COALESCE(a.source_type, 'Monthly') AS SourceType,
COALESCE(a.remarks, '') AS Remarks
FROM employees e
LEFT JOIN employee_monthly_attendance a ON a.employee_id=e.Id AND a.client_id=e.ClientId AND a.attendance_month=@Month
LEFT JOIN (
    SELECT age.employee_id, MIN(g.id) AS group_id
    FROM attendance_group_employees age
    JOIN attendance_groups g ON g.id=age.attendance_group_id AND g.client_id=@ClientId AND g.is_active=TRUE
    GROUP BY age.employee_id
) employee_group ON employee_group.employee_id=e.Id
LEFT JOIN attendance_groups g ON g.id=employee_group.group_id
WHERE e.ClientId=@ClientId AND e.IsActive=TRUE AND (@WorkLocationId <= 0 OR e.WorkLocationId=@WorkLocationId)
ORDER BY e.FirstName, e.LastName, e.EmployeeCode;", new { ClientId = clientId, Month = month, WorkLocationId = locationId });
    }

    public async Task<(IEnumerable<EmployeeMonthlyAttendance>? Rows, string? Error)> SaveMonthlyAttendanceAsync(SaveMonthlyAttendanceRequest request)
    {
        var error = ValidateMonthlyAttendance(request);
        if (error is not null) return (null, error);
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        var validEmployeeIds = (await connection.QueryAsync<int>("SELECT Id FROM employees WHERE ClientId=@ClientId AND IsActive=TRUE", new { request.ClientId })).ToHashSet();
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
        return await connection.QueryAsync<EmployeeDailyAttendance>(@"SELECT id AS Id, client_id AS ClientId, employee_id AS EmployeeId, attendance_date AS AttendanceDate, status AS Status, payable_value AS PayableValue,
check_in_time AS CheckInTime, check_out_time AS CheckOutTime, total_hours AS TotalHours, COALESCE(remarks, '') AS Remarks
FROM employee_daily_attendance
WHERE client_id=@ClientId AND employee_id=@EmployeeId AND DATE_FORMAT(attendance_date, '%Y-%m')=@Month
ORDER BY attendance_date;", new { ClientId = clientId, EmployeeId = employeeId, Month = month });
    }

    public async Task<IEnumerable<EmployeeDailyAttendance>> GetDailyAttendanceMonthAsync(int clientId, string month, int? workLocationId = null)
    {
        if (!IsValidMonth(month)) return [];
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        var locationId = workLocationId.GetValueOrDefault();
        return await connection.QueryAsync<EmployeeDailyAttendance>(@"SELECT d.id AS Id, d.client_id AS ClientId, d.employee_id AS EmployeeId, d.attendance_date AS AttendanceDate, d.status AS Status, d.payable_value AS PayableValue,
d.check_in_time AS CheckInTime, d.check_out_time AS CheckOutTime, d.total_hours AS TotalHours, COALESCE(d.remarks, '') AS Remarks
FROM employee_daily_attendance d
JOIN employees e ON e.Id=d.employee_id AND e.ClientId=d.client_id
WHERE d.client_id=@ClientId AND DATE_FORMAT(d.attendance_date, '%Y-%m')=@Month AND (@WorkLocationId <= 0 OR e.WorkLocationId=@WorkLocationId)
ORDER BY d.employee_id, d.attendance_date;", new { ClientId = clientId, Month = month, WorkLocationId = locationId });
    }

    public async Task<(IEnumerable<EmployeeDailyAttendance>? Rows, string? Error)> SaveDailyAttendanceAsync(SaveDailyAttendanceRequest request)
    {
        var error = ValidateDailyAttendance(request);
        if (error is not null) return (null, error);
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        var exists = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employees WHERE Id=@EmployeeId AND ClientId=@ClientId AND IsActive=TRUE", new { request.EmployeeId, request.ClientId });
        if (exists == 0) return (null, "Employee was not found for the selected client.");
        var settings = await connection.QueryFirstOrDefaultAsync<AttendanceSettings>(@"SELECT id AS Id, client_id AS ClientId,
check_in_time AS CheckInTime, check_out_time AS CheckOutTime, minimum_hours_for_half_day AS MinimumHoursForHalfDay,
minimum_hours_for_full_day AS MinimumHoursForFullDay, maximum_hours_allowed_for_full_day AS MaximumHoursAllowedForFullDay
FROM attendance_settings WHERE client_id=@ClientId LIMIT 1;", new { request.ClientId }) ?? new AttendanceSettings { ClientId = request.ClientId };
        var activeLeaveTypes = (await connection.QueryAsync<AttendanceLeaveRule>(@"SELECT lt.id AS Id, lt.code AS Code, lt.name AS Name, lt.type AS Type, p.allow_negative_leave_balance AS AllowNegativeLeaveBalance
FROM leave_types lt JOIN leave_type_policies p ON p.leave_type_id=lt.id
WHERE lt.client_id=@ClientId AND lt.is_active=TRUE;", new { request.ClientId }))
            .ToDictionary(row => row.Code, row => row, StringComparer.OrdinalIgnoreCase);
        var invalidStatus = request.Rows.FirstOrDefault(row => NormalizeAttendanceStatus(row.Status, activeLeaveTypes) is null);
        if (invalidStatus is not null) return (null, $"Attendance status '{invalidStatus.Status}' is not valid.");
        var rows = request.Rows.Where(row => row.AttendanceDate.ToString("yyyy-MM") == request.Month).Select(row =>
        {
            var status = NormalizeAttendanceStatus(row.Status, activeLeaveTypes)!;
            var checkIn = status == "Present" ? row.CheckInTime : null;
            var checkOut = status == "Present" ? row.CheckOutTime : null;
            var totalHours = status == "Present" ? CalculateHours(checkIn, checkOut, row.TotalHours) : 0m;
            var payableValue = ResolvePayableValue(status, row.PayableValue, totalHours, checkIn.HasValue && checkOut.HasValue, settings, activeLeaveTypes);
            return new { request.ClientId, request.EmployeeId, AttendanceDate = row.AttendanceDate.Date, Status = status, PayableValue = payableValue, CheckInTime = checkIn, CheckOutTime = checkOut, TotalHours = totalHours, Remarks = row.Remarks ?? string.Empty };
        }).ToList();
        var balanceError = await ValidateLeaveBalancesAsync(connection, request.ClientId, request.EmployeeId, request.Month, rows.Select(row => new AttendanceSaveRow(row.Status, row.PayableValue)), activeLeaveTypes);
        if (balanceError is not null) return (null, balanceError);
        await connection.ExecuteAsync(@"INSERT INTO employee_daily_attendance (client_id, employee_id, attendance_date, status, payable_value, check_in_time, check_out_time, total_hours, remarks)
VALUES (@ClientId, @EmployeeId, @AttendanceDate, @Status, @PayableValue, @CheckInTime, @CheckOutTime, @TotalHours, @Remarks)
ON DUPLICATE KEY UPDATE status=VALUES(status), payable_value=VALUES(payable_value), check_in_time=VALUES(check_in_time), check_out_time=VALUES(check_out_time), total_hours=VALUES(total_hours), remarks=VALUES(remarks);", rows);
        var cycleDates = rows.Select(row => row.AttendanceDate.Date).Distinct().OrderBy(date => date).ToArray();
        await RollupDailyAttendanceAsync(connection, request.ClientId, request.EmployeeId, request.Month, cycleDates.First(), cycleDates.Last());
        return (await GetDailyAttendanceAsync(request.ClientId, request.EmployeeId, request.Month), null);
    }

    public async Task<(IEnumerable<EmployeeMonthlyAttendance>? Rows, string? Error)> SaveDailyAttendanceBatchAsync(SaveDailyAttendanceBatchRequest request)
    {
        var error = ValidateDailyAttendanceBatch(request);
        if (error is not null) return (null, error);
        await using var connection = CreateConnection();
        await connection.OpenAsync();

        var settings = await connection.QueryFirstOrDefaultAsync<AttendanceSettings>(@"SELECT id AS Id, client_id AS ClientId,
check_in_time AS CheckInTime, check_out_time AS CheckOutTime, minimum_hours_for_half_day AS MinimumHoursForHalfDay,
minimum_hours_for_full_day AS MinimumHoursForFullDay, maximum_hours_allowed_for_full_day AS MaximumHoursAllowedForFullDay
FROM attendance_settings WHERE client_id=@ClientId LIMIT 1;", new { request.ClientId }) ?? new AttendanceSettings { ClientId = request.ClientId };
        var activeLeaveTypes = (await connection.QueryAsync<AttendanceLeaveRule>(@"SELECT lt.id AS Id, lt.code AS Code, lt.name AS Name, lt.type AS Type, p.allow_negative_leave_balance AS AllowNegativeLeaveBalance
FROM leave_types lt JOIN leave_type_policies p ON p.leave_type_id=lt.id
WHERE lt.client_id=@ClientId AND lt.is_active=TRUE;", new { request.ClientId }))
            .ToDictionary(row => row.Code, row => row, StringComparer.OrdinalIgnoreCase);
        var validEmployeeIds = (await connection.QueryAsync<int>("SELECT Id FROM employees WHERE ClientId=@ClientId AND IsActive=TRUE", new { request.ClientId })).ToHashSet();
        var groupedRows = request.Rows
            .Where(row => validEmployeeIds.Contains(row.EmployeeId))
            .GroupBy(row => row.EmployeeId)
            .ToList();
        if (groupedRows.Count == 0) return (null, "No valid employee attendance rows were submitted.");

        var expectedDays = groupedRows.Max(group => group.Select(row => row.AttendanceDate.Date).Distinct().Count());
        var cycleDates = request.Rows.Select(row => row.AttendanceDate.Date).Distinct().OrderBy(date => date).ToArray();
        var rows = new List<object>();
        foreach (var group in groupedRows)
        {
            if (group.Select(row => row.AttendanceDate.Date).Distinct().Count() != expectedDays)
                return (null, "Save the complete attendance cycle before payroll review.");
            var invalidStatus = group.FirstOrDefault(row => NormalizeAttendanceStatus(row.Status, activeLeaveTypes) is null);
            if (invalidStatus is not null) return (null, $"Attendance status '{invalidStatus.Status}' is not valid.");
            var employeeRows = group.Select(row =>
            {
                var status = NormalizeAttendanceStatus(row.Status, activeLeaveTypes)!;
                var checkIn = status == "Present" ? row.CheckInTime : null;
                var checkOut = status == "Present" ? row.CheckOutTime : null;
                var totalHours = status == "Present" ? CalculateHours(checkIn, checkOut, row.TotalHours) : 0m;
                var payableValue = ResolvePayableValue(status, row.PayableValue, totalHours, checkIn.HasValue && checkOut.HasValue, settings, activeLeaveTypes);
                return new { request.ClientId, EmployeeId = group.Key, AttendanceDate = row.AttendanceDate.Date, Status = status, PayableValue = payableValue, CheckInTime = checkIn, CheckOutTime = checkOut, TotalHours = totalHours, Remarks = row.Remarks ?? string.Empty };
            }).ToList();
            var balanceError = await ValidateLeaveBalancesAsync(connection, request.ClientId, group.Key, request.Month, employeeRows.Select(row => new AttendanceSaveRow(row.Status, row.PayableValue)), activeLeaveTypes);
            if (balanceError is not null) return (null, balanceError);
            rows.AddRange(employeeRows);
        }

        await using var transaction = await connection.BeginTransactionAsync();
        await connection.ExecuteAsync(@"INSERT INTO employee_daily_attendance (client_id, employee_id, attendance_date, status, payable_value, check_in_time, check_out_time, total_hours, remarks)
VALUES (@ClientId, @EmployeeId, @AttendanceDate, @Status, @PayableValue, @CheckInTime, @CheckOutTime, @TotalHours, @Remarks)
ON DUPLICATE KEY UPDATE status=VALUES(status), payable_value=VALUES(payable_value), check_in_time=VALUES(check_in_time), check_out_time=VALUES(check_out_time), total_hours=VALUES(total_hours), remarks=VALUES(remarks);", rows, transaction);
        await RollupDailyAttendanceBatchAsync(connection, transaction, request.ClientId, groupedRows.Select(row => row.Key).ToArray(), request.Month, cycleDates.First(), cycleDates.Last());
        await transaction.CommitAsync();
        return (await GetMonthlyAttendanceAsync(request.ClientId, request.Month), null);
    }

    public async Task<IEnumerable<LeaveType>> GetLeaveTypesAsync(int clientId)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        return await connection.QueryAsync<LeaveType>(LeaveTypeSelectSql + " WHERE lt.client_id=@ClientId ORDER BY lt.name;", new { ClientId = clientId });
    }

    public async Task<(LeaveType? LeaveType, string? Error)> SaveLeaveTypeAsync(SaveLeaveTypeRequest request)
    {
        var error = ValidateLeaveType(request);
        if (error is not null) return (null, error);
        await using var connection = CreateConnection();
        await connection.OpenAsync();
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
        await connection.ExecuteAsync("UPDATE leave_types SET is_active=@IsActive WHERE id=@Id AND client_id=@ClientId", new { Id = id, ClientId = clientId, IsActive = isActive });
        return await GetLeaveTypeAsync(id, clientId);
    }

    public async Task<bool> DeleteLeaveTypeAsync(int id, int clientId)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        return await connection.ExecuteAsync("DELETE FROM leave_types WHERE id=@Id AND client_id=@ClientId", new { Id = id, ClientId = clientId }) > 0;
    }

    public async Task<byte[]> BuildLeaveTypeImportTemplateAsync(int clientId)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        var rows = (await connection.QueryAsync<LeaveType>(LeaveTypeSelectSql + " WHERE lt.client_id=@ClientId ORDER BY lt.name;", new { ClientId = clientId })).ToList();
        var templateRows = new List<string[]> { LeaveTypeImportHeaders };
        templateRows.AddRange(rows.Select(row => new[]
        {
            row.Name,
            row.Code,
            row.Type,
            row.Description,
            row.Entitlement.ToString(CultureInfo.InvariantCulture),
            row.EntitlementPeriod,
            BoolText(row.ProRateForNewJoinees),
            BoolText(row.ResetEnabled),
            row.ResetFrequency,
            BoolText(row.CarryForwardUnusedLeaves),
            row.MaxCarryForwardLimit?.ToString(CultureInfo.InvariantCulture) ?? "",
            BoolText(row.EncashUnusedLeaves),
            row.MaxEncashmentLimit?.ToString(CultureInfo.InvariantCulture) ?? "",
            BoolText(row.AllowNegativeLeaveBalance),
            row.NegativeBalanceHandling,
            BoolText(row.AllowPastDates),
            row.PastDateLimitType,
            row.PastDateLimitDays?.ToString(CultureInfo.InvariantCulture) ?? "",
            BoolText(row.AllowFutureDates),
            row.FutureDateLimitType,
            row.FutureDateLimitDays?.ToString(CultureInfo.InvariantCulture) ?? "",
            row.ApplicabilityMode,
            row.WorkLocation,
            row.Department,
            row.Designation,
            row.Gender,
            row.EffectiveFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            row.ExpiresOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
            BoolText(row.PostponeCreditsForNewEmployees),
            row.PostponeCreditValue?.ToString(CultureInfo.InvariantCulture) ?? "",
            row.PostponeCreditUnit,
            BoolText(row.IsActive)
        }));
        if (templateRows.Count == 1)
            templateRows.Add(new[] { "Casual Leave", "CL", "Paid", "Casual leave", "12", "Yearly", "TRUE", "TRUE", "Yearly", "TRUE", "6", "FALSE", "", "FALSE", "Mark as LOP", "FALSE", "No limit", "", "TRUE", "No limit", "", "All employees", "", "", "", "", DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "", "FALSE", "", "Days", "TRUE" });

        var locations = await connection.QueryAsync<string>("SELECT Name FROM worklocations WHERE ClientId=@ClientId AND IsActive=TRUE ORDER BY Name", new { ClientId = clientId });
        var departments = await connection.QueryAsync<string>("SELECT Value FROM dropdownmasters WHERE Type='Department' AND IsActive=TRUE ORDER BY Value");
        var designations = await connection.QueryAsync<string>("SELECT Value FROM dropdownmasters WHERE Type='Designation' AND IsActive=TRUE ORDER BY Value");
        var reference = new List<string[]>
        {
            new[] { "Options", "Values", "" },
            new[] { "Type", "Paid, Unpaid", "" },
            new[] { "Period", "Monthly, Yearly", "" },
            new[] { "Reset Frequency", "Monthly, Yearly", "" },
            new[] { "Negative Balance Handling", "Mark as LOP, Without limit, Up to year-end limit", "" },
            new[] { "Date Limit Type", "No limit, Set number of days", "" },
            new[] { "Applicability", "All employees, Criteria based employees", "" },
            new[] { "Postpone Credit Unit", "Days, Months", "" },
            new[] { "Boolean", "TRUE/FALSE", "" },
            new[] { "", "", "" },
            new[] { "Work Locations", "", "" }
        };
        reference.AddRange(locations.Select(item => new[] { item, "", "" }));
        reference.Add(new[] { "", "", "" });
        reference.Add(new[] { "Departments", "", "" });
        reference.AddRange(departments.Select(item => new[] { item, "", "" }));
        reference.Add(new[] { "", "", "" });
        reference.Add(new[] { "Designations", "", "" });
        reference.AddRange(designations.Select(item => new[] { item, "", "" }));
        return BuildImportXlsx(("Leave Types", templateRows), ("Reference", reference));
    }

    public async Task<ClientImportJobStatus> StartLeaveTypeImportJobAsync(int clientId, IFormFile file)
    {
        var rows = await ParseImportFileAsync(file);
        var totalRows = Math.Max(0, rows.Skip(1).Count(row => row.Any(value => !string.IsNullOrWhiteSpace(value))));
        var job = new ClientImportJobStatus(Guid.NewGuid(), "Queued", totalRows, 0, 0, 0, []);
        LeaveTypeImportJobs[job.JobId] = job;
        _ = Task.Run(async () =>
        {
            SetLeaveTypeImportJob(job.JobId, current => current with { State = "Processing" });
            try
            {
                var result = await ImportLeaveTypeRowsAsync(clientId, rows, (completed, inserted, updated) => SetLeaveTypeImportJob(job.JobId, current => current with { CompletedRows = completed, Inserted = inserted, Updated = updated }));
                SetLeaveTypeImportJob(job.JobId, current => current with { State = result.Errors.Count > 0 ? "Failed" : "Completed", TotalRows = result.TotalRows, CompletedRows = result.TotalRows, Inserted = result.Inserted, Updated = result.Updated, Errors = result.Errors });
            }
            catch (Exception ex)
            {
                SetLeaveTypeImportJob(job.JobId, current => current with { State = "Failed", Errors = [$"Import failed: {ex.Message}"] });
            }
        });
        return job;
    }

    public ClientImportJobStatus? GetLeaveTypeImportJob(Guid jobId) => LeaveTypeImportJobs.TryGetValue(jobId, out var job) ? job : null;

    private async Task<ClientImportResult> ImportLeaveTypeRowsAsync(int clientId, List<List<string>> rows, Action<int, int, int>? progress = null)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        var totalRows = Math.Max(0, rows.Skip(1).Count(row => row.Any(value => !string.IsNullOrWhiteSpace(value))));
        if (rows.Count < 2 || totalRows == 0)
            return new ClientImportResult(0, 0, 0, ["Import file has no data rows."]);
        var clientExists = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM clients WHERE Id=@ClientId AND IsActive=TRUE", new { ClientId = clientId });
        if (clientExists == 0)
            return new ClientImportResult(totalRows, 0, 0, ["Selected client was not found."]);

        var header = rows[0].Select(Norm).ToList();
        var existingRows = (await connection.QueryAsync<LeaveType>(LeaveTypeSelectSql + " WHERE lt.client_id=@ClientId", new { ClientId = clientId })).ToList();
        var existingByCode = existingRows.ToDictionary(row => row.Code.ToUpperInvariant(), row => row, StringComparer.OrdinalIgnoreCase);
        var allCodes = (await connection.QueryAsync<(int Id, int ClientId, string Code)>("SELECT id AS Id, client_id AS ClientId, code AS Code FROM leave_types")).ToList();
        var drafts = new List<SaveLeaveTypeRequest>();
        var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        var completed = 0;

        for (var i = 1; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.All(string.IsNullOrWhiteSpace)) continue;

            string V(params string[] names)
            {
                foreach (var name in names)
                {
                    var ix = header.IndexOf(Norm(name));
                    if (ix >= 0 && ix < row.Count) return row[ix].Trim();
                }
                return "";
            }

            var rowNumber = i + 1;
            var rowErrors = new List<string>();
            var code = V("Code").ToUpperInvariant();
            var name = V("Leave Type Name", "Name");
            var type = NormalizeOption(V("Type", "Paid/Unpaid"), LeaveTypeTypes);
            var entitlementPeriod = NormalizeOption(V("Entitlement Period", "Period"), LeavePeriods);
            var resetFrequency = NormalizeOption(V("Reset Frequency"), LeavePeriods);
            var negativeHandling = NormalizeOption(V("Negative Balance Handling"), NegativeBalanceHandlingOptions);
            var pastLimitType = NormalizeOption(V("Past Date Limit Type", "Past Date Limit"), DateLimitTypes);
            var futureLimitType = NormalizeOption(V("Future Date Limit Type", "Future Date Limit"), DateLimitTypes);
            var applicability = NormalizeOption(V("Applicability", "Applicability Mode"), ApplicabilityModes);
            var creditUnit = NormalizeOption(V("Postpone Credit Unit", "Delay Unit"), PostponeCreditUnits);
            var entitlement = ParseDecimal(V("Entitlement", "Number of leaves"), out var entitlementOk);
            var effectiveFrom = ParseDate(V("Effective From"), out var effectiveOk);
            var expiresOn = ParseOptionalDate(V("Expires On", "Expiry Date"), out var expiresOk);
            var existing = !string.IsNullOrWhiteSpace(code) && existingByCode.TryGetValue(code, out var found) ? found : null;
            var globalDuplicate = !string.IsNullOrWhiteSpace(code) ? allCodes.FirstOrDefault(item => item.Code.Equals(code, StringComparison.OrdinalIgnoreCase) && item.ClientId != clientId) : default;

            if (string.IsNullOrWhiteSpace(name)) rowErrors.Add($"Row {rowNumber}: Leave Type Name is required.");
            if (string.IsNullOrWhiteSpace(code)) rowErrors.Add($"Row {rowNumber}: Code is required.");
            else if (!System.Text.RegularExpressions.Regex.IsMatch(code, @"^[A-Z0-9_]+$")) rowErrors.Add($"Row {rowNumber}: Code can use only letters, numbers and underscore.");
            else if (!seenCodes.Add(code)) rowErrors.Add($"Row {rowNumber}: Code {code} is repeated in the file.");
            else if (globalDuplicate.Code is not null) rowErrors.Add($"Row {rowNumber}: Code {code} already exists for another client.");
            if (!LeaveTypeTypes.Contains(type)) rowErrors.Add($"Row {rowNumber}: Type must be Paid/Unpaid.");
            if (!entitlementOk || entitlement < 0) rowErrors.Add($"Row {rowNumber}: Entitlement must be a non-negative number.");
            if (!LeavePeriods.Contains(entitlementPeriod)) rowErrors.Add($"Row {rowNumber}: Entitlement Period must be Monthly/Yearly.");
            if (!LeavePeriods.Contains(resetFrequency)) rowErrors.Add($"Row {rowNumber}: Reset Frequency must be Monthly/Yearly.");
            if (!NegativeBalanceHandlingOptions.Contains(negativeHandling)) rowErrors.Add($"Row {rowNumber}: Negative Balance Handling is invalid.");
            if (!DateLimitTypes.Contains(pastLimitType)) rowErrors.Add($"Row {rowNumber}: Past Date Limit Type is invalid.");
            if (!DateLimitTypes.Contains(futureLimitType)) rowErrors.Add($"Row {rowNumber}: Future Date Limit Type is invalid.");
            if (!ApplicabilityModes.Contains(applicability)) rowErrors.Add($"Row {rowNumber}: Applicability is invalid.");
            if (!PostponeCreditUnits.Contains(creditUnit)) rowErrors.Add($"Row {rowNumber}: Postpone Credit Unit is invalid.");
            foreach (var (label, text) in new[] { ("Pro Rate New Joinees", V("Pro Rate New Joinees", "Pro-rata for new joinees")), ("Reset Enabled", V("Reset Enabled", "Enable Reset")), ("Carry Forward", V("Carry Forward", "Carry Forward Unused Leaves")), ("Encash", V("Encash", "Encash Unused Leaves")), ("Allow Negative Balance", V("Allow Negative Balance", "Allow Negative Leave Balance")), ("Allow Past Dates", V("Allow Past Dates")), ("Allow Future Dates", V("Allow Future Dates")), ("Postpone Credits", V("Postpone Credits", "Postpone Leave Credits")), ("Active", V("Active")) })
                if (!IsImportFlag(text)) rowErrors.Add($"Row {rowNumber}: {label} must be TRUE/FALSE.");
            if (!effectiveOk) rowErrors.Add($"Row {rowNumber}: Effective From is required as a valid date.");
            if (!expiresOk) rowErrors.Add($"Row {rowNumber}: Expires On must be a valid date when filled.");
            if (expiresOn.HasValue && effectiveOk && expiresOn.Value.Date < effectiveFrom.Date) rowErrors.Add($"Row {rowNumber}: Expires On cannot be before Effective From.");
            var pastDays = ParseOptionalInt(V("Past Date Limit Days"), rowNumber, "Past Date Limit Days", rowErrors);
            var futureDays = ParseOptionalInt(V("Future Date Limit Days"), rowNumber, "Future Date Limit Days", rowErrors);
            var carryLimit = ParseOptionalDecimal(V("Max Carry Forward", "Max Carry Forward Limit"), rowNumber, "Max Carry Forward", rowErrors);
            var encashLimit = ParseOptionalDecimal(V("Max Encashment", "Max Encashment Limit"), rowNumber, "Max Encashment", rowErrors);
            var postponeValue = ParseOptionalInt(V("Postpone Credit Value", "Delay Value"), rowNumber, "Postpone Credit Value", rowErrors);
            ValidateLength(name, "Leave Type Name", 180, rowNumber, rowErrors);
            ValidateLength(code, "Code", 40, rowNumber, rowErrors);

            if (rowErrors.Count == 0)
            {
                drafts.Add(new SaveLeaveTypeRequest
                {
                    Id = existing?.Id ?? 0,
                    ClientId = clientId,
                    Name = name.Trim(),
                    Code = code,
                    Type = type,
                    Description = V("Description"),
                    Entitlement = entitlement,
                    EntitlementPeriod = entitlementPeriod,
                    ProRateForNewJoinees = ParseImportFlag(V("Pro Rate New Joinees", "Pro-rata for new joinees"), existing?.ProRateForNewJoinees ?? false),
                    ResetEnabled = ParseImportFlag(V("Reset Enabled", "Enable Reset"), existing?.ResetEnabled ?? false),
                    ResetFrequency = resetFrequency,
                    CarryForwardUnusedLeaves = ParseImportFlag(V("Carry Forward", "Carry Forward Unused Leaves"), existing?.CarryForwardUnusedLeaves ?? false),
                    MaxCarryForwardLimit = carryLimit,
                    EncashUnusedLeaves = ParseImportFlag(V("Encash", "Encash Unused Leaves"), existing?.EncashUnusedLeaves ?? false),
                    MaxEncashmentLimit = encashLimit,
                    AllowNegativeLeaveBalance = ParseImportFlag(V("Allow Negative Balance", "Allow Negative Leave Balance"), existing?.AllowNegativeLeaveBalance ?? false),
                    NegativeBalanceHandling = negativeHandling,
                    AllowPastDates = ParseImportFlag(V("Allow Past Dates"), existing?.AllowPastDates ?? false),
                    PastDateLimitType = pastLimitType,
                    PastDateLimitDays = pastDays,
                    AllowFutureDates = ParseImportFlag(V("Allow Future Dates"), existing?.AllowFutureDates ?? true),
                    FutureDateLimitType = futureLimitType,
                    FutureDateLimitDays = futureDays,
                    ApplicabilityMode = applicability,
                    WorkLocation = V("Work Location"),
                    Department = V("Department"),
                    Designation = V("Designation"),
                    Gender = V("Gender"),
                    EffectiveFrom = effectiveFrom,
                    ExpiresOn = expiresOn,
                    PostponeCreditsForNewEmployees = ParseImportFlag(V("Postpone Credits", "Postpone Leave Credits"), existing?.PostponeCreditsForNewEmployees ?? false),
                    PostponeCreditValue = postponeValue,
                    PostponeCreditUnit = creditUnit,
                    IsActive = ParseImportFlag(V("Active"), existing?.IsActive ?? true)
                });
            }
            else errors.AddRange(rowErrors);

            completed++;
            progress?.Invoke(completed, 0, 0);
        }

        if (errors.Count > 0)
            return new ClientImportResult(totalRows, 0, 0, errors);

        await using var transaction = await connection.BeginTransactionAsync();
        var inserted = 0;
        var updated = 0;
        try
        {
            foreach (var draft in drafts)
            {
                var id = draft.Id;
                if (id == 0)
                {
                    id = (int)await connection.ExecuteScalarAsync<long>(@"INSERT INTO leave_types (client_id, name, code, type, description, is_active)
VALUES (@ClientId, @Name, @Code, @Type, @Description, @IsActive); SELECT LAST_INSERT_ID();", draft, transaction);
                    inserted++;
                }
                else
                {
                    await connection.ExecuteAsync(@"UPDATE leave_types SET name=@Name, code=@Code, type=@Type, description=@Description, is_active=@IsActive WHERE id=@Id AND client_id=@ClientId", draft, transaction);
                    updated++;
                }
                await UpsertPolicyAsync(connection, transaction, id, draft);
                await UpsertApplicabilityAsync(connection, transaction, id, draft);
            }
            await transaction.CommitAsync();
            return new ClientImportResult(totalRows, inserted, updated, []);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            return new ClientImportResult(0, 0, 0, [$"Import failed: {ex.Message}"]);
        }
    }

    private async Task<LeaveType?> GetLeaveTypeAsync(int id, int clientId)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        return await connection.QueryFirstOrDefaultAsync<LeaveType>(LeaveTypeSelectSql + " WHERE lt.id=@Id AND lt.client_id=@ClientId", new { Id = id, ClientId = clientId });
    }

    public async Task<IEnumerable<Holiday>> GetHolidaysAsync(int clientId, int? year, int? workLocationId)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        var rows = (await connection.QueryAsync<Holiday>(@"SELECT h.id AS Id, h.client_id AS ClientId, h.name AS Name, h.holiday_type AS HolidayType, h.start_date AS StartDate, h.end_date AS EndDate, h.description AS Description, h.all_locations AS AllLocations, h.created_at AS CreatedAt, h.updated_at AS UpdatedAt,
CASE WHEN h.all_locations THEN 'All locations' ELSE COALESCE(GROUP_CONCAT(w.Name ORDER BY w.Name SEPARATOR ', '), 'No locations') END AS worklocations
FROM holidays h
LEFT JOIN holiday_locations hl ON hl.holiday_id = h.id
LEFT JOIN worklocations w ON w.Id = hl.work_location_id
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
        request.WorkLocationIds = request.AllLocations ? [] : request.WorkLocationIds.Distinct().ToList();
        if (!request.AllLocations)
        {
            var validLocationCount = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM worklocations WHERE ClientId=@ClientId AND IsActive=TRUE AND Id IN @Ids", new { request.ClientId, Ids = request.WorkLocationIds });
            if (validLocationCount != request.WorkLocationIds.Count) return (null, "Selected work location does not belong to this client.");
        }
        var duplicate = await HasDuplicateHolidayAsync(connection, request);
        if (duplicate) return (null, "Duplicate holiday exists for the same location and date range.");
        await using var transaction = await connection.BeginTransactionAsync();
        var id = request.Id;
        if (id == 0)
        {
            id = (int)await connection.ExecuteScalarAsync<long>(@"INSERT INTO holidays (client_id, name, holiday_type, start_date, end_date, description, all_locations)
VALUES (@ClientId, @Name, @HolidayType, @StartDate, @EndDate, @Description, @AllLocations); SELECT LAST_INSERT_ID();", new { request.ClientId, Name = request.Name.Trim(), HolidayType = NormalizeHolidayType(request.HolidayType), request.StartDate, request.EndDate, request.Description, request.AllLocations }, transaction);
        }
        else
        {
            var updated = await connection.ExecuteAsync(@"UPDATE holidays SET name=@Name, holiday_type=@HolidayType, start_date=@StartDate, end_date=@EndDate, description=@Description, all_locations=@AllLocations WHERE id=@Id AND client_id=@ClientId", new { request.ClientId, Id = id, Name = request.Name.Trim(), HolidayType = NormalizeHolidayType(request.HolidayType), request.StartDate, request.EndDate, request.Description, request.AllLocations }, transaction);
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
        return await connection.ExecuteAsync("DELETE FROM holidays WHERE id=@Id AND client_id=@ClientId", new { Id = id, ClientId = clientId }) > 0;
    }

    private async Task<Holiday?> GetHolidayAsync(int id, int clientId) =>
        (await GetHolidaysAsync(clientId, null, null)).FirstOrDefault(holiday => holiday.Id == id);

    private async Task<GeoFenceRule?> GetGeoFenceRuleAsync(int id, int clientId)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        var rows = (await connection.QueryAsync<GeoFenceRule>(GeoFenceRuleSelectSql + @"
WHERE r.id=@Id AND r.client_id=@ClientId
GROUP BY r.id;", new { Id = id, ClientId = clientId })).ToList();
        await LoadGeoFenceEmployeesAsync(connection, rows);
        return rows.FirstOrDefault();
    }

    private static async Task LoadGeoFenceEmployeesAsync(MySqlConnection connection, List<GeoFenceRule> rows)
    {
        if (rows.Count == 0) return;
        var employees = await connection.QueryAsync<(int RuleId, int EmployeeId)>(@"SELECT geo_fence_rule_id AS RuleId, employee_id AS EmployeeId
FROM attendance_geo_fence_rule_employees WHERE geo_fence_rule_id IN @Ids", new { Ids = rows.Select(row => row.Id).ToArray() });
        foreach (var row in rows)
            row.EmployeeIds = employees.Where(employee => employee.RuleId == row.Id).Select(employee => employee.EmployeeId).ToList();
    }

    private async Task<AttendanceGroup?> GetAttendanceGroupAsync(int id, int clientId)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        var rows = (await connection.QueryAsync<AttendanceGroup>(AttendanceGroupSelectSql + @"
WHERE g.id=@Id AND g.client_id=@ClientId
GROUP BY g.id;", new { Id = id, ClientId = clientId })).ToList();
        await LoadAttendanceGroupEmployeesAsync(connection, rows);
        return rows.FirstOrDefault();
    }

    private static async Task LoadAttendanceGroupEmployeesAsync(MySqlConnection connection, List<AttendanceGroup> rows)
    {
        if (rows.Count == 0) return;
        var employees = await connection.QueryAsync<(int GroupId, int EmployeeId)>(@"SELECT attendance_group_id AS GroupId, employee_id AS EmployeeId
FROM attendance_group_employees WHERE attendance_group_id IN @Ids", new { Ids = rows.Select(row => row.Id).ToArray() });
        foreach (var row in rows)
            row.EmployeeIds = employees.Where(employee => employee.GroupId == row.Id).Select(employee => employee.EmployeeId).ToList();
    }

    private static object CleanGeoFenceRequest(SaveGeoFenceRuleRequest request) => new
    {
        request.Id,
        request.ClientId,
        Name = request.Name.Trim(),
        request.ScopeType,
        WorkLocationId = request.ScopeType == "Work Location" ? request.WorkLocationId : null,
        request.Latitude,
        request.Longitude,
        request.RadiusMeters,
        request.GpsToleranceMeters,
        request.Strictness,
        request.AllowCheckIn,
        request.AllowCheckOut,
        EffectiveFrom = request.EffectiveFrom.Date,
        EffectiveTo = request.EffectiveTo?.Date,
        request.IsActive,
        request.Priority
    };

    private static object CleanAttendanceGroupRequest(SaveAttendanceGroupRequest request) => new
    {
        request.Id,
        request.ClientId,
        Name = request.Name.Trim(),
        request.WorkLocationId,
        Department = (request.Department ?? string.Empty).Trim(),
        Designation = (request.Designation ?? string.Empty).Trim(),
        WorkWeek = (request.WorkWeek ?? string.Empty).Trim(),
        request.AttendanceCycleStartDay,
        request.AttendanceCycleEndDay,
        request.PayrollReportGenerationDay,
        request.IsActive
    };

    private async Task<string?> ValidatePreferencesAsync(SaveLeaveAttendancePreferencesRequest request)
    {
        if (request.ClientId <= 0) return "Select a client.";
        if (!await IsValidWorkWeekAsync((request.WorkWeek ?? string.Empty).Trim())) return "Select a valid work week.";
        if (request.WorkLocationId is > 0)
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync();
            var locationOk = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM worklocations WHERE Id=@WorkLocationId AND ClientId=@ClientId AND IsActive=TRUE", new { request.ClientId, request.WorkLocationId });
            if (locationOk == 0) return "Select a valid work location for the client.";
        }
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
        if (NormalizeHolidayType(request.HolidayType) is not ("Holiday" or "Restricted Holiday")) return "Select a valid holiday type.";
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

    private static string? ValidateGeoFenceRule(SaveGeoFenceRuleRequest request)
    {
        if (request.ClientId <= 0) return "Select a client.";
        if (string.IsNullOrWhiteSpace(request.Name)) return "Rule name is required.";
        if (request.ScopeType is not ("Client Default" or "Work Location" or "Employee")) return "Select a valid geo-fence scope.";
        if (request.ScopeType == "Work Location" && request.WorkLocationId is null or <= 0) return "Select a work location for this rule.";
        if (request.ScopeType == "Employee" && request.EmployeeIds.Count == 0) return "Select at least one employee for an employee override.";
        if (request.Latitude is < -90 or > 90 || request.Longitude is < -180 or > 180) return "Enter valid latitude and longitude.";
        if (request.RadiusMeters is < 25 or > 5000) return "Radius must be between 25 and 5000 meters.";
        if (request.GpsToleranceMeters is < 0 or > 500) return "GPS tolerance must be between 0 and 500 meters.";
        if (request.Strictness is not ("Block outside fence" or "Allow with reason" or "Allow with approval")) return "Select a valid strictness mode.";
        if (!request.AllowCheckIn && !request.AllowCheckOut) return "Allow at least one attendance action.";
        if (request.EffectiveTo.HasValue && request.EffectiveTo.Value.Date < request.EffectiveFrom.Date) return "Effective to date cannot be before effective from date.";
        return null;
    }

    private async Task<string?> ValidateAttendanceGroupAsync(SaveAttendanceGroupRequest request)
    {
        if (request.ClientId <= 0) return "Select a client.";
        if (string.IsNullOrWhiteSpace(request.Name)) return "Group name is required.";
        if (request.WorkLocationId <= 0) return "Select a work location.";
        if (!await IsValidWorkWeekAsync((request.WorkWeek ?? string.Empty).Trim())) return "Select a valid work week.";
        if (!IsValidDay(request.AttendanceCycleStartDay) || !IsValidDay(request.AttendanceCycleEndDay) || !IsValidDay(request.PayrollReportGenerationDay))
            return "Attendance cycle and report generation days must be between 1 and 31.";
        if (AttendanceCycleDays(request.AttendanceCycleStartDay, request.AttendanceCycleEndDay) > 31)
            return "Attendance cycle cannot exceed 31 days in any payroll month.";
        var buffer = request.PayrollReportGenerationDay >= request.AttendanceCycleEndDay
            ? request.PayrollReportGenerationDay - request.AttendanceCycleEndDay
            : request.PayrollReportGenerationDay + 31 - request.AttendanceCycleEndDay;
        if (buffer is < 3 or > 7)
            return "Payroll report generation day must have a 3 to 7 day buffer after attendance cycle end day.";
        var employeeIds = (request.EmployeeIds ?? new List<int>()).Distinct().ToArray();
        if (employeeIds.Length == 0) return "Select at least one employee.";

        await using var connection = CreateConnection();
        await connection.OpenAsync();
        var duplicateName = await connection.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM attendance_groups
WHERE client_id=@ClientId AND name=@Name AND id<>@Id", new { request.ClientId, Name = request.Name.Trim(), request.Id });
        if (duplicateName > 0) return "A group with this name already exists for this client.";
        var locationOk = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM worklocations WHERE Id=@WorkLocationId AND ClientId=@ClientId AND IsActive=TRUE", new { request.ClientId, request.WorkLocationId });
        if (locationOk == 0) return "Selected work location does not belong to this client.";
        var matchingEmployeeIds = (await connection.QueryAsync<int>(@"SELECT Id FROM employees
WHERE IsActive=TRUE AND ClientId=@ClientId AND WorkLocationId=@WorkLocationId
AND (@Department='' OR Department=@Department)
AND (@Designation='' OR Designation=@Designation)
AND Id IN @EmployeeIds", new { request.ClientId, request.WorkLocationId, Department = (request.Department ?? string.Empty).Trim(), Designation = (request.Designation ?? string.Empty).Trim(), EmployeeIds = employeeIds })).Distinct().ToHashSet();
        return matchingEmployeeIds.Count == employeeIds.Length
            ? null
            : "Selected employees must belong to the selected client, work location, department and designation.";
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

    private static int AttendanceCycleDays(int startDay, int endDay) =>
        startDay == 1 ? endDay : 31 - startDay + 1 + endDay;

    private static string? ValidateDailyAttendance(SaveDailyAttendanceRequest request)
    {
        if (request.ClientId <= 0 || request.EmployeeId <= 0) return "Select a client and employee.";
        if (!IsValidMonth(request.Month)) return "Select a valid attendance month.";
        if (request.Rows.Count == 0) return "Add at least one date-wise attendance row.";
        if (request.Rows.Select(row => row.AttendanceDate.Date).Distinct().Count() > 31) return "Attendance cycle cannot exceed 31 days in any payroll month.";
        if (request.Rows.Any(row => row.PayableValue < 0 || row.PayableValue > 1)) return "Payable value must be between 0 and 1.";
        if (request.Rows.Any(row => row.TotalHours < 0 || row.TotalHours > 24)) return "Total hours must be between 0 and 24.";
        return null;
    }

    private static string? ValidateDailyAttendanceBatch(SaveDailyAttendanceBatchRequest request)
    {
        if (request.ClientId <= 0) return "Select a client.";
        if (!IsValidMonth(request.Month)) return "Select a valid attendance month.";
        if (request.Rows.Count == 0) return "Add at least one date-wise attendance row.";
        if (request.Rows.Any(row => row.EmployeeId <= 0)) return "Every attendance row must have an employee.";
        if (request.Rows.GroupBy(row => row.EmployeeId).Any(group => group.Select(row => row.AttendanceDate.Date).Distinct().Count() > 31)) return "Attendance cycle cannot exceed 31 days in any payroll month.";
        if (request.Rows.Any(row => row.PayableValue < 0 || row.PayableValue > 1)) return "Payable value must be between 0 and 1.";
        if (request.Rows.Any(row => row.TotalHours < 0 || row.TotalHours > 24)) return "Total hours must be between 0 and 24.";
        return null;
    }

    private static async Task RollupDailyAttendanceAsync(MySqlConnection connection, int clientId, int employeeId, string month, DateTime cycleStart, DateTime cycleEnd)
    {
        var summary = await connection.QuerySingleAsync<(decimal WorkingDays, decimal PresentDays, decimal PayableDays)>(@"SELECT COALESCE(COUNT(*), 0) AS WorkingDays,
COALESCE(SUM(CASE WHEN status='Present' THEN payable_value ELSE 0 END), 0) AS PresentDays,
COALESCE(SUM(CASE WHEN status IN ('WO','H') THEN 1 ELSE payable_value END), 0) AS PayableDays
FROM employee_daily_attendance
WHERE client_id=@ClientId AND employee_id=@EmployeeId AND attendance_date BETWEEN @CycleStart AND @CycleEnd;", new { ClientId = clientId, EmployeeId = employeeId, CycleStart = cycleStart, CycleEnd = cycleEnd });
        var lop = Math.Max(0, summary.WorkingDays - summary.PayableDays);
        await connection.ExecuteAsync(@"INSERT INTO employee_monthly_attendance (client_id, employee_id, attendance_month, working_days, present_days, payable_days, lop_days, source_type, remarks)
VALUES (@ClientId, @EmployeeId, @Month, @WorkingDays, @PresentDays, @PayableDays, @LopDays, 'Date-wise', 'Rolled up from date-wise attendance')
ON DUPLICATE KEY UPDATE working_days=VALUES(working_days), present_days=VALUES(present_days), payable_days=VALUES(payable_days), lop_days=VALUES(lop_days), source_type='Date-wise', remarks=VALUES(remarks);",
            new { ClientId = clientId, EmployeeId = employeeId, Month = month, summary.WorkingDays, summary.PresentDays, summary.PayableDays, LopDays = lop });
    }

    private static async Task RollupDailyAttendanceBatchAsync(MySqlConnection connection, MySqlTransaction transaction, int clientId, int[] employeeIds, string month, DateTime cycleStart, DateTime cycleEnd)
    {
        if (employeeIds.Length == 0) return;
        await connection.ExecuteAsync(@"INSERT INTO employee_monthly_attendance (client_id, employee_id, attendance_month, working_days, present_days, payable_days, lop_days, source_type, remarks)
SELECT @ClientId, employee_id, @Month,
COUNT(*) AS working_days,
COALESCE(SUM(CASE WHEN status='Present' THEN payable_value ELSE 0 END), 0) AS present_days,
COALESCE(SUM(CASE WHEN status IN ('WO','H') THEN 1 ELSE payable_value END), 0) AS payable_days,
GREATEST(0, COUNT(*) - COALESCE(SUM(CASE WHEN status IN ('WO','H') THEN 1 ELSE payable_value END), 0)) AS lop_days,
'Date-wise',
'Rolled up from date-wise attendance'
FROM employee_daily_attendance
WHERE client_id=@ClientId AND employee_id IN @EmployeeIds AND attendance_date BETWEEN @CycleStart AND @CycleEnd
GROUP BY employee_id
ON DUPLICATE KEY UPDATE working_days=VALUES(working_days), present_days=VALUES(present_days), payable_days=VALUES(payable_days), lop_days=VALUES(lop_days), source_type='Date-wise', remarks=VALUES(remarks);",
            new { ClientId = clientId, EmployeeIds = employeeIds, Month = month, CycleStart = cycleStart, CycleEnd = cycleEnd }, transaction);
    }

    private static string? NormalizeAttendanceStatus(string? status, IReadOnlyDictionary<string, AttendanceLeaveRule> leaveTypes)
    {
        var text = (status ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text) || string.Equals(text, "P", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "Present", StringComparison.OrdinalIgnoreCase)) return "Present";
        if (string.Equals(text, "A", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "Absent", StringComparison.OrdinalIgnoreCase)) return "A";
        if (string.Equals(text, "WO", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "Weekly Off", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "Week Off", StringComparison.OrdinalIgnoreCase)) return "WO";
        if (string.Equals(text, "H", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "Holiday", StringComparison.OrdinalIgnoreCase)) return "H";
        return leaveTypes.Keys.FirstOrDefault(code => string.Equals(code, text, StringComparison.OrdinalIgnoreCase));
    }

    private static decimal ResolvePayableValue(string status, decimal incoming, decimal totalHours, bool hasTimes, AttendanceSettings settings, IReadOnlyDictionary<string, AttendanceLeaveRule> leaveTypes)
    {
        if (status == "WO" || status == "H") return 1;
        if (status == "A") return 0;
        if (status == "Present")
        {
            if (hasTimes)
            {
                if (totalHours >= settings.MinimumHoursForFullDay) return 1;
                if (totalHours >= settings.MinimumHoursForHalfDay) return 0.5m;
                return 0;
            }
            return Math.Clamp(incoming > 0 ? incoming : 1, 0, 1);
        }
        return leaveTypes.TryGetValue(status, out var leaveType) && string.Equals(leaveType.Type, "Paid", StringComparison.OrdinalIgnoreCase)
            ? Math.Clamp(incoming > 0 ? incoming : 1, 0, 1)
            : 0;
    }

    private static decimal CalculateHours(TimeSpan? checkIn, TimeSpan? checkOut, decimal fallback)
    {
        if (!checkIn.HasValue || !checkOut.HasValue) return Math.Clamp(fallback, 0, 24);
        var minutes = (decimal)(checkOut.Value - checkIn.Value).TotalMinutes;
        if (minutes < 0) minutes += 24 * 60;
        return Math.Clamp(Math.Round(minutes / 60, 2), 0, 24);
    }

    private static async Task<string?> ValidateLeaveBalancesAsync(MySqlConnection connection, int clientId, int employeeId, string month, IEnumerable<AttendanceSaveRow> rows, IReadOnlyDictionary<string, AttendanceLeaveRule> leaveTypes)
    {
        var requested = rows
            .Where(row => leaveTypes.TryGetValue(row.Status, out var leaveType) && string.Equals(leaveType.Type, "Paid", StringComparison.OrdinalIgnoreCase) && !leaveType.AllowNegativeLeaveBalance)
            .GroupBy(row => row.Status, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(row => row.PayableValue > 0 ? row.PayableValue : 1), StringComparer.OrdinalIgnoreCase);
        if (requested.Count == 0) return null;
        var monthEnd = DateTime.Parse($"{month}-01").AddMonths(1).AddDays(-1);
        var balances = (await connection.QueryAsync<LeaveBalanceRow>(@"SELECT lt.code AS Code, COALESCE(b.balance_count, 0) AS Balance
FROM leave_types lt
LEFT JOIN (
    SELECT employee_id, leave_type_id, MAX(balance_date) AS balance_date
    FROM employee_leave_balances
    WHERE client_id=@ClientId AND employee_id=@EmployeeId AND balance_date<=@MonthEnd
    GROUP BY employee_id, leave_type_id
) latest ON latest.leave_type_id=lt.id
LEFT JOIN employee_leave_balances b ON b.client_id=@ClientId AND b.employee_id=@EmployeeId AND b.leave_type_id=lt.id AND b.balance_date=latest.balance_date
WHERE lt.client_id=@ClientId AND lt.code IN @Codes;", new { ClientId = clientId, EmployeeId = employeeId, MonthEnd = monthEnd, Codes = requested.Keys.ToArray() }))
            .ToDictionary(row => row.Code, row => row.Balance, StringComparer.OrdinalIgnoreCase);
        foreach (var item in requested)
        {
            var balance = balances.GetValueOrDefault(item.Key);
            if (item.Value > balance + 0.001m)
            {
                var name = leaveTypes[item.Key].Name;
                return $"{name} balance is {balance:0.##}; selected {item.Value:0.##}.";
            }
        }
        return null;
    }

    private static ClientAttendanceSchedule ReadSchedule(string? json)
    {
        ClientAttendanceSchedule? schedule = null;
        if (!string.IsNullOrWhiteSpace(json) && json.Trim() != "{}")
        {
            try { schedule = JsonSerializer.Deserialize<ClientAttendanceSchedule>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
            catch { schedule = null; }
        }
        schedule ??= new ClientAttendanceSchedule();
        return new ClientAttendanceSchedule
        {
            WorkWeek = schedule.WorkWeek ?? string.Empty,
            SalaryDays = string.IsNullOrWhiteSpace(schedule.SalaryDays) ? "Actual days" : schedule.SalaryDays,
            FixedDays = string.IsNullOrWhiteSpace(schedule.FixedDays) ? "30" : schedule.FixedDays,
            PayDay = string.IsNullOrWhiteSpace(schedule.PayDay) ? "Last working day" : schedule.PayDay,
            FirstPayPeriod = schedule.FirstPayPeriod ?? string.Empty
        };
    }

    private sealed record AttendanceSaveRow(string Status, decimal PayableValue);
    private sealed class AttendanceLeaveRule { public int Id { get; set; } public string Code { get; set; } = string.Empty; public string Name { get; set; } = string.Empty; public string Type { get; set; } = "Paid"; public bool AllowNegativeLeaveBalance { get; set; } }
    private sealed class LeaveBalanceRow { public string Code { get; set; } = string.Empty; public decimal Balance { get; set; } }

    private static readonly string[] LeaveTypeImportHeaders = ["Leave Type Name", "Code", "Type", "Description", "Entitlement", "Entitlement Period", "Pro Rate New Joinees", "Reset Enabled", "Reset Frequency", "Carry Forward", "Max Carry Forward", "Encash", "Max Encashment", "Allow Negative Balance", "Negative Balance Handling", "Allow Past Dates", "Past Date Limit Type", "Past Date Limit Days", "Allow Future Dates", "Future Date Limit Type", "Future Date Limit Days", "Applicability", "Work Location", "Department", "Designation", "Gender", "Effective From", "Expires On", "Postpone Credits", "Postpone Credit Value", "Postpone Credit Unit", "Active"];
    private static readonly string[] LeaveTypeTypes = ["Paid", "Unpaid"];
    private static readonly string[] LeavePeriods = ["Monthly", "Yearly"];
    private static readonly string[] NegativeBalanceHandlingOptions = ["Mark as LOP", "Without limit", "Up to year-end limit"];
    private static readonly string[] DateLimitTypes = ["No limit", "Set number of days"];
    private static readonly string[] ApplicabilityModes = ["All employees", "Criteria based employees"];
    private static readonly string[] PostponeCreditUnits = ["Days", "Months"];

    private static void SetLeaveTypeImportJob(Guid jobId, Func<ClientImportJobStatus, ClientImportJobStatus> update) =>
        LeaveTypeImportJobs.AddOrUpdate(jobId, _ => update(new ClientImportJobStatus(jobId, "Processing", 0, 0, 0, 0, [])), (_, current) => update(current));

    private static string NormalizeOption(string value, string[] options) =>
        options.FirstOrDefault(option => option.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase)) ?? value.Trim();

    private static void ValidateLength(string value, string label, int max, int row, List<string> errors)
    {
        if (value.Length > max) errors.Add($"Row {row}: {label} must be {max} characters or less.");
    }

    private static bool IsImportFlag(string value) =>
        string.IsNullOrWhiteSpace(value) ||
        new[] { "true", "yes", "active", "1" }.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase) ||
        new[] { "false", "no", "inactive", "0" }.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);

    private static bool ParseImportFlag(string value, bool defaultValue) =>
        string.IsNullOrWhiteSpace(value) ? defaultValue :
        new[] { "true", "yes", "active", "1" }.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase) ? true :
        new[] { "false", "no", "inactive", "0" }.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase) ? false : defaultValue;

    private static decimal ParseDecimal(string value, out bool ok)
    {
        ok = decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var result);
        return ok ? result : 0;
    }

    private static DateTime ParseDate(string value, out bool ok)
    {
        ok = DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date);
        return ok ? date.Date : DateTime.Today;
    }

    private static DateTime? ParseOptionalDate(string value, out bool ok)
    {
        if (string.IsNullOrWhiteSpace(value)) { ok = true; return null; }
        ok = DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date);
        return ok ? date.Date : null;
    }

    private static int? ParseOptionalInt(string value, int row, string label, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) && result >= 0) return result;
        errors.Add($"Row {row}: {label} must be a non-negative number.");
        return null;
    }

    private static decimal? ParseOptionalDecimal(string value, int row, string label, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var result) && result >= 0) return result;
        errors.Add($"Row {row}: {label} must be a non-negative number.");
        return null;
    }

    private static string BoolText(bool value) => value ? "TRUE" : "FALSE";

    private static string Norm(string value) => value.Replace(" ", "").Replace("_", "").Replace("-", "").ToLowerInvariant();

    private static async Task<List<List<string>>> ParseImportFileAsync(IFormFile file)
    {
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        var bytes = ms.ToArray();
        return file.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ? ParseXlsx(bytes) : ParseCsv(Encoding.UTF8.GetString(bytes));
    }

    private static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted && ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
            else if (ch == '"') quoted = !quoted;
            else if (!quoted && ch == ',') { row.Add(cell.ToString()); cell.Clear(); }
            else if (!quoted && (ch == '\n' || ch == '\r'))
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(cell.ToString());
                cell.Clear();
                rows.Add(row);
                row = [];
            }
            else cell.Append(ch);
        }
        row.Add(cell.ToString());
        if (row.Any(value => value.Length > 0)) rows.Add(row);
        return rows;
    }

    private static List<List<string>> ParseXlsx(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var shared = ReadSharedStrings(zip);
        var sheet = zip.GetEntry("xl/worksheets/sheet1.xml") ?? throw new InvalidDataException("Import sheet not found.");
        using var stream = sheet.Open();
        var doc = XDocument.Load(stream);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var rows = new List<List<string>>();
        foreach (var row in doc.Descendants(ns + "row"))
        {
            var values = new List<string>();
            foreach (var cell in row.Elements(ns + "c"))
            {
                var index = CellIndex((string?)cell.Attribute("r") ?? "A1");
                while (values.Count < index) values.Add("");
                var type = (string?)cell.Attribute("t") ?? "";
                var raw = type == "inlineStr" ? cell.Descendants(ns + "t").FirstOrDefault()?.Value ?? "" : cell.Element(ns + "v")?.Value ?? "";
                values.Add(type == "s" && int.TryParse(raw, out var sharedIndex) && sharedIndex >= 0 && sharedIndex < shared.Count ? shared[sharedIndex] : raw);
            }
            rows.Add(values);
        }
        return rows;
    }

    private static List<string> ReadSharedStrings(ZipArchive zip)
    {
        var entry = zip.GetEntry("xl/sharedStrings.xml");
        if (entry is null) return [];
        using var stream = entry.Open();
        var doc = XDocument.Load(stream);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        return doc.Descendants(ns + "si").Select(item => string.Concat(item.Descendants(ns + "t").Select(text => text.Value))).ToList();
    }

    private static int CellIndex(string reference)
    {
        var n = 0;
        foreach (var ch in reference.TakeWhile(char.IsLetter)) n = n * 26 + char.ToUpperInvariant(ch) - 'A' + 1;
        return Math.Max(0, n - 1);
    }

    private static byte[] BuildImportXlsx(params (string Name, IEnumerable<string[]> Rows)[] sheets)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            Add(zip, "[Content_Types].xml", $"""<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>{string.Join("", sheets.Select((_, index) => $"""<Override PartName="/xl/worksheets/sheet{index + 1}.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>"""))}</Types>""");
            Add(zip, "_rels/.rels", """<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""");
            Add(zip, "xl/_rels/workbook.xml.rels", $"""<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">{string.Join("", sheets.Select((_, index) => $"""<Relationship Id="rId{index + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet{index + 1}.xml"/>"""))}<Relationship Id="rId{sheets.Length + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>""");
            Add(zip, "xl/styles.xml", """<?xml version="1.0" encoding="UTF-8"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><fonts count="1"><font><sz val="11"/><name val="Calibri"/></font></fonts><fills count="1"><fill><patternFill patternType="none"/></fill></fills><borders count="1"><border/></borders><cellStyleXfs count="1"><xf/></cellStyleXfs><cellXfs count="1"><xf/></cellXfs></styleSheet>""");
            Add(zip, "xl/workbook.xml", WorkbookXml(sheets.Select((sheet, index) => (sheet.Name, index + 1))));
            foreach (var (sheet, index) in sheets.Select((sheet, index) => (sheet, index + 1)))
                Add(zip, $"xl/worksheets/sheet{index}.xml", SheetXml(sheet.Rows));
        }
        return ms.ToArray();
    }

    private static void Add(ZipArchive zip, string path, string text)
    {
        var entry = zip.CreateEntry(path);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(text);
    }

    private static string WorkbookXml(IEnumerable<(string Name, int Index)> sheets) =>
        new XDocument(new XElement(XName.Get("workbook", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"),
            new XAttribute(XNamespace.Xmlns + "r", "http://schemas.openxmlformats.org/officeDocument/2006/relationships"),
            new XElement(XName.Get("sheets", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"),
                sheets.Select(sheet => new XElement(XName.Get("sheet", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"),
                    new XAttribute("name", sheet.Name),
                    new XAttribute("sheetId", sheet.Index),
                    new XAttribute(XName.Get("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships"), $"rId{sheet.Index}")))))).ToString(SaveOptions.DisableFormatting);

    private static string SheetXml(IEnumerable<string[]> rows) =>
        new XDocument(new XElement(XName.Get("worksheet", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"),
            new XElement(XName.Get("sheetData", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"),
                rows.Select((row, rowIndex) => new XElement(XName.Get("row", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"),
                    new XAttribute("r", rowIndex + 1),
                    row.Select((cell, colIndex) => new XElement(XName.Get("c", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"),
                        new XAttribute("r", $"{Col(colIndex + 1)}{rowIndex + 1}"),
                        new XAttribute("t", "inlineStr"),
                        new XElement(XName.Get("is", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"),
                            new XElement(XName.Get("t", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"), cell ?? ""))))))))).ToString(SaveOptions.DisableFormatting);

    private static string Col(int n)
    {
        var value = "";
        while (n > 0)
        {
            n--;
            value = (char)('A' + n % 26) + value;
            n /= 26;
        }
        return value;
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

    private static string NormalizeHolidayType(string? holidayType) =>
        string.Equals(holidayType?.Trim(), "Restricted Holiday", StringComparison.OrdinalIgnoreCase) || string.Equals(holidayType?.Trim(), "RH", StringComparison.OrdinalIgnoreCase)
            ? "Restricted Holiday"
            : "Holiday";

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

    private const string GeoFenceRuleSelectSql = @"SELECT r.id AS Id, r.client_id AS ClientId, r.name AS Name, r.scope_type AS ScopeType, r.work_location_id AS WorkLocationId,
COALESCE(w.Name, '') AS WorkLocationName,
COALESCE(GROUP_CONCAT(DISTINCT CONCAT(e.FirstName, ' ', e.LastName, ' (', e.EmployeeCode, ')') ORDER BY e.FirstName, e.LastName SEPARATOR ', '), '') AS EmployeeNames,
r.latitude AS Latitude, r.longitude AS Longitude, r.radius_meters AS RadiusMeters, r.gps_tolerance_meters AS GpsToleranceMeters,
r.strictness AS Strictness, r.allow_check_in AS AllowCheckIn, r.allow_check_out AS AllowCheckOut, r.effective_from AS EffectiveFrom, r.effective_to AS EffectiveTo,
r.is_active AS IsActive, r.priority AS Priority, r.created_at AS CreatedAt, r.updated_at AS UpdatedAt
FROM attendance_geo_fence_rules r
LEFT JOIN worklocations w ON w.Id = r.work_location_id
LEFT JOIN attendance_geo_fence_rule_employees gre ON gre.geo_fence_rule_id = r.id
LEFT JOIN employees e ON e.Id = gre.employee_id";

    private const string AttendanceGroupSelectSql = @"SELECT g.id AS Id, g.client_id AS ClientId, COALESCE(c.Name, '') AS ClientName,
g.name AS Name, g.work_location_id AS WorkLocationId, COALESCE(w.Name, '') AS WorkLocationName,
g.department AS Department, g.designation AS Designation, g.work_week AS WorkWeek,
g.attendance_cycle_start_day AS AttendanceCycleStartDay,
g.attendance_cycle_end_day AS AttendanceCycleEndDay,
g.payroll_report_generation_day AS PayrollReportGenerationDay,
g.is_active AS IsActive, g.created_at AS CreatedAt, g.updated_at AS UpdatedAt,
COUNT(DISTINCT age.employee_id) AS EmployeeCount,
COALESCE(GROUP_CONCAT(DISTINCT CONCAT(e.FirstName, ' ', e.LastName, ' (', e.EmployeeCode, ')') ORDER BY e.FirstName, e.LastName SEPARATOR ', '), '') AS EmployeeNames
FROM attendance_groups g
LEFT JOIN clients c ON c.Id = g.client_id
LEFT JOIN worklocations w ON w.Id = g.work_location_id
LEFT JOIN attendance_group_employees age ON age.attendance_group_id = g.id
LEFT JOIN employees e ON e.Id = age.employee_id";

    private async Task<bool> IsFormulaBasedSalaryComponentAsync(int componentId)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        return await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM salarycomponents WHERE Id=@componentId AND CalculationType='Formula'", new { componentId }) > 0;
    }

    private static bool IsValidStatus(string status) =>
        status is "Not Started" or "In Progress" or "Completed" or "Disabled";
    private static bool IsValidDay(int day) => day is >= 1 and <= 31;
    private static bool IsValidMonth(string month) => month.Length == 7 && DateTime.TryParse($"{month}-01", out _);
    private async Task<bool> IsValidWorkWeekAsync(string workWeek)
    {
        if (WorkWeekOptions.Contains(workWeek)) return true;
        if (string.IsNullOrWhiteSpace(workWeek)) return false;
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await EnsureColumnAsync(connection, "dropdownmasters", "ConfigJson", "JSON NULL AFTER Value");
        var activeMasterCount = await connection.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM dropdownmasters
WHERE Type='Work Week' AND Value=@WorkWeek AND IsActive=TRUE LIMIT 1", new { WorkWeek = workWeek });
        return activeMasterCount > 0;
    }

    private static bool IsValidWorkWeekConfig(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return false;
        try
        {
            using var document = JsonDocument.Parse(configJson);
            if (!document.RootElement.TryGetProperty("workingDays", out var workingDays) || workingDays.ValueKind != JsonValueKind.Array) return false;
            var days = workingDays.EnumerateArray().Select(item => item.GetInt32()).ToArray();
            return days.Length > 0 && days.All(day => day is >= 0 and <= 6);
        }
        catch
        {
            return false;
        }
    }

    private static readonly HashSet<string> WorkWeekOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "Monday - Friday",
        "Monday - Saturday",
        "All days",
        "Sunday + 2nd Saturday off",
        "Sunday + 2nd/4th Saturday off",
        "Only 2nd Saturday off"
    };

    private static async Task EnsureClientScopeAsync(MySqlConnection connection)
    {
        foreach (var table in new[] { "modulesettings", "modulesetupprogress", "leave_attendance_preferences", "attendance_settings", "employee_monthly_attendance", "employee_daily_attendance", "leave_types", "holidays", "employee_leave_balances", "leave_balance_import_logs" })
     
            await AddClientColumnIfMissingAsync(connection, table);
        var clientId = await connection.ExecuteScalarAsync<int?>("SELECT Id FROM clients ORDER BY Id LIMIT 1");
        if (clientId is null) return;

        foreach (var table in new[] { "modulesettings", "modulesetupprogress", "leave_attendance_preferences", "attendance_settings", "employee_monthly_attendance", "employee_daily_attendance", "leave_types", "holidays", "employee_leave_balances", "leave_balance_import_logs" })
            await connection.ExecuteAsync($"UPDATE {table} SET client_id=@ClientId WHERE client_id IS NULL", new { ClientId = clientId });
        await connection.ExecuteAsync("UPDATE leave_attendance_preferences SET work_location_id=0 WHERE work_location_id IS NULL");
        await KeepLatestPreferenceScopeAsync(connection);
        await DropIndexIfExistsAsync(connection, "leave_attendance_preferences", "UX_preferences_client");
        await DropIndexIfExistsAsync(connection, "leave_types", "UX_leave_types_code");
        await CreateIndexIfMissingAsync(connection, "modulesettings", "UX_ModuleSettings_Client_Module", "CREATE UNIQUE INDEX UX_ModuleSettings_Client_Module ON modulesettings (client_id, ModuleCode)");
        await CreateIndexIfMissingAsync(connection, "modulesetupprogress", "UX_ModuleSetupProgress_Client_Module_Step", "CREATE UNIQUE INDEX UX_ModuleSetupProgress_Client_Module_Step ON modulesetupprogress (client_id, ModuleCode, StepCode)");
        await CreateIndexIfMissingAsync(connection, "leave_attendance_preferences", "UX_preferences_client_location", "CREATE UNIQUE INDEX UX_preferences_client_location ON leave_attendance_preferences (client_id, work_location_id)");
        await CreateIndexIfMissingAsync(connection, "attendance_settings", "UX_attendance_client", "CREATE UNIQUE INDEX UX_attendance_client ON attendance_settings (client_id)");
        await CreateIndexIfMissingAsync(connection, "leave_types", "UX_leave_types_client_code", "CREATE UNIQUE INDEX UX_leave_types_client_code ON leave_types (client_id, code)");
        await CreateIndexIfMissingAsync(connection, "attendance_geo_fence_rules", "IX_geo_fence_client_scope", "CREATE INDEX IX_geo_fence_client_scope ON attendance_geo_fence_rules (client_id, scope_type, is_active)");
        await CreateIndexIfMissingAsync(connection, "attendance_groups", "UX_attendance_groups_client_name", "CREATE UNIQUE INDEX UX_attendance_groups_client_name ON attendance_groups (client_id, name)");
        await CreateIndexIfMissingAsync(connection, "attendance_groups", "IX_attendance_groups_client_location", "CREATE INDEX IX_attendance_groups_client_location ON attendance_groups (client_id, work_location_id)");
        await CreateIndexIfMissingAsync(connection, "attendance_group_employees", "UX_attendance_group_employee", "CREATE UNIQUE INDEX UX_attendance_group_employee ON attendance_group_employees (attendance_group_id, employee_id)");
    }

    private static async Task AddClientColumnIfMissingAsync(MySqlConnection connection, string table)
    {
        var exists = await connection.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM information_schema.columns
WHERE table_schema = DATABASE() AND table_name = @TableName AND column_name = 'client_id'", new { TableName = table });
        if (exists == 0)
            await connection.ExecuteAsync($"ALTER TABLE `{table}` ADD COLUMN client_id INT NULL");
    }

    private static async Task EnsureColumnAsync(MySqlConnection connection, string tableName, string columnName, string definition)
    {
        var exists = await connection.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM information_schema.columns
WHERE table_schema = DATABASE() AND table_name = @TableName AND column_name = @ColumnName", new { TableName = tableName, ColumnName = columnName });
        if (exists == 0)
            await connection.ExecuteAsync($"ALTER TABLE `{tableName}` ADD COLUMN `{columnName}` {definition}");
    }

    private static Task KeepLatestPreferenceScopeAsync(MySqlConnection connection) =>
        connection.ExecuteAsync(@"DELETE older FROM leave_attendance_preferences older
JOIN leave_attendance_preferences newer
  ON newer.client_id = older.client_id
 AND newer.work_location_id = older.work_location_id
 AND newer.id > older.id");

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
