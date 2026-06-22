CREATE DATABASE IF NOT EXISTS payroll;
USE payroll;

CREATE TABLE IF NOT EXISTS Organizations (
    Id INT PRIMARY KEY AUTO_INCREMENT,
    Name VARCHAR(250) NOT NULL,
    LegalName VARCHAR(250),
    BusinessType VARCHAR(150),
    BusinessLocation VARCHAR(100) NOT NULL DEFAULT 'India',
    Industry VARCHAR(150),
    HasRunPayrollThisYear BOOLEAN NOT NULL DEFAULT FALSE,
    SetupCompleted BOOLEAN NOT NULL DEFAULT FALSE,
    LogoDataUrl LONGTEXT,
    PAN VARCHAR(50),
    GSTIN VARCHAR(50),
    FiscalYearStart VARCHAR(50),
    AddressLine1 VARCHAR(255),
    AddressLine2 VARCHAR(255),
    City VARCHAR(100),
    State VARCHAR(100),
    PostalCode VARCHAR(30),
    Country VARCHAR(100),
    BankName VARCHAR(200),
    AccountNumber VARCHAR(100),
    IFSCCode VARCHAR(50),
    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS PayrollSetups (
    Id INT PRIMARY KEY AUTO_INCREMENT,
    SetupJson JSON NOT NULL,
    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS Clients (
    Id INT PRIMARY KEY AUTO_INCREMENT,
    Name VARCHAR(250) NOT NULL,
    Code VARCHAR(50),
    ContactPerson VARCHAR(150),
    Email VARCHAR(150),
    Phone VARCHAR(50),
    Address VARCHAR(500),
    PayScheduleJson JSON NULL,
    IsActive BOOLEAN NOT NULL DEFAULT TRUE,
    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS WorkLocations (
    Id INT PRIMARY KEY AUTO_INCREMENT,
    ClientId INT NOT NULL DEFAULT 0,
    ClientName VARCHAR(250),
    Name VARCHAR(200) NOT NULL,
    Address VARCHAR(500),
    City VARCHAR(100),
    State VARCHAR(100),
    PostalCode VARCHAR(30),
    IsPrimary BOOLEAN NOT NULL DEFAULT FALSE,
    IsActive BOOLEAN NOT NULL DEFAULT TRUE,
    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS DropdownMasters (
    Id INT PRIMARY KEY AUTO_INCREMENT,
    Type VARCHAR(100) NOT NULL,
    Value VARCHAR(200) NOT NULL,
    IsActive BOOLEAN NOT NULL DEFAULT TRUE,
    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    UNIQUE KEY UX_DropdownMasters_Type_Value (Type, Value)
);

CREATE TABLE IF NOT EXISTS Employees (
    Id INT PRIMARY KEY AUTO_INCREMENT,
    ClientId INT NOT NULL,
    EmployeeCode VARCHAR(50) NOT NULL,
    FirstName VARCHAR(100) NOT NULL,
    LastName VARCHAR(100),
    Gender VARCHAR(30),
    DateOfJoining VARCHAR(30),
    WorkEmail VARCHAR(150),
    Department VARCHAR(100),
    Designation VARCHAR(100),
    WorkLocationId INT NOT NULL DEFAULT 0,
    ReportingManagerId INT NOT NULL DEFAULT 0,
    PortalAccess BOOLEAN NOT NULL DEFAULT FALSE,
    SalaryStructureId VARCHAR(50),
    AnnualCtc DECIMAL(18,2) NOT NULL DEFAULT 0,
    SalaryJson JSON NOT NULL,
    PersonalJson JSON NOT NULL,
    PaymentJson JSON NOT NULL,
    IsActive BOOLEAN NOT NULL DEFAULT TRUE,
    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    UNIQUE KEY UX_Employees_Client_Code (ClientId, EmployeeCode)
);

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
    PaymentDate DATE NULL,
    DetailsJson JSON NOT NULL,
    UNIQUE KEY UX_PayRunEmployees_Run_Employee (PayRunId, EmployeeId),
    CONSTRAINT FK_PayRunEmployees_PayRuns FOREIGN KEY (PayRunId) REFERENCES PayRuns(Id) ON DELETE CASCADE
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
    INDEX IX_PayrollAdjustments_Client_Period_Status (ClientId, PayPeriod, Status),
    CONSTRAINT FK_PayrollAdjustments_Employees FOREIGN KEY (EmployeeId) REFERENCES Employees(Id),
    CONSTRAINT FK_PayrollAdjustments_PayRuns FOREIGN KEY (PayRunId) REFERENCES PayRuns(Id) ON DELETE SET NULL
);

CREATE TABLE IF NOT EXISTS AuthUsers (
    Id INT PRIMARY KEY AUTO_INCREMENT,
    Email VARCHAR(190) NOT NULL,
    DisplayName VARCHAR(190) NOT NULL,
    PasswordHash VARCHAR(500) NOT NULL,
    ClientId INT NULL,
    EmployeeId INT NULL,
    IsActive BOOLEAN NOT NULL DEFAULT TRUE,
    MustChangePassword BOOLEAN NOT NULL DEFAULT FALSE,
    LastLoginAt DATETIME NULL,
    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    UNIQUE KEY UX_AuthUsers_Email (Email)
);

CREATE TABLE IF NOT EXISTS AuthRoles (
    Id INT PRIMARY KEY AUTO_INCREMENT,
    Code VARCHAR(80) NOT NULL,
    Name VARCHAR(150) NOT NULL,
    Description VARCHAR(500),
    IsSystem BOOLEAN NOT NULL DEFAULT FALSE,
    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE KEY UX_AuthRoles_Code (Code)
);

CREATE TABLE IF NOT EXISTS AuthPermissions (
    Id INT PRIMARY KEY AUTO_INCREMENT,
    Code VARCHAR(120) NOT NULL,
    Name VARCHAR(150) NOT NULL,
    Module VARCHAR(80) NOT NULL,
    Description VARCHAR(500),
    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE KEY UX_AuthPermissions_Code (Code)
);

CREATE TABLE IF NOT EXISTS AuthUserRoles (
    UserId INT NOT NULL,
    RoleId INT NOT NULL,
    PRIMARY KEY (UserId, RoleId),
    CONSTRAINT FK_AuthUserRoles_User FOREIGN KEY (UserId) REFERENCES AuthUsers(Id) ON DELETE CASCADE,
    CONSTRAINT FK_AuthUserRoles_Role FOREIGN KEY (RoleId) REFERENCES AuthRoles(Id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS AuthRolePermissions (
    RoleId INT NOT NULL,
    PermissionId INT NOT NULL,
    PRIMARY KEY (RoleId, PermissionId),
    CONSTRAINT FK_AuthRolePermissions_Role FOREIGN KEY (RoleId) REFERENCES AuthRoles(Id) ON DELETE CASCADE,
    CONSTRAINT FK_AuthRolePermissions_Permission FOREIGN KEY (PermissionId) REFERENCES AuthPermissions(Id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS AuthSessions (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    UserId INT NOT NULL,
    TokenHash CHAR(64) NOT NULL,
    IpAddress VARCHAR(80),
    UserAgent VARCHAR(500),
    ExpiresAt DATETIME NOT NULL,
    RevokedAt DATETIME NULL,
    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE KEY UX_AuthSessions_TokenHash (TokenHash),
    INDEX IX_AuthSessions_User (UserId),
    CONSTRAINT FK_AuthSessions_User FOREIGN KEY (UserId) REFERENCES AuthUsers(Id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS AuditLogs (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    UserId INT NULL,
    UserEmail VARCHAR(190),
    Action VARCHAR(120) NOT NULL,
    Resource VARCHAR(190),
    Method VARCHAR(20),
    Path VARCHAR(500),
    StatusCode INT NOT NULL DEFAULT 0,
    IpAddress VARCHAR(80),
    UserAgent VARCHAR(500),
    DetailsJson JSON NOT NULL,
    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    INDEX IX_AuditLogs_CreatedAt (CreatedAt),
    INDEX IX_AuditLogs_UserId (UserId),
    INDEX IX_AuditLogs_Action (Action)
);

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
);
