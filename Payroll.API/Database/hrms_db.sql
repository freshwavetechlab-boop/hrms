-- payroll.attendance_settings definition

CREATE TABLE `attendance_settings` (
  `id` int NOT NULL AUTO_INCREMENT,
  `check_in_time` time NOT NULL DEFAULT '09:00:00',
  `check_out_time` time NOT NULL DEFAULT '18:00:00',
  `working_hours_calculation` varchar(80) NOT NULL DEFAULT 'First check-in and last check-out',
  `minimum_hours_for_half_day` decimal(5,2) NOT NULL DEFAULT '4.00',
  `minimum_hours_for_full_day` decimal(5,2) NOT NULL DEFAULT '8.00',
  `maximum_hours_allowed_for_full_day` decimal(5,2) NOT NULL DEFAULT '12.00',
  `allow_regularization_requests` tinyint(1) NOT NULL DEFAULT '1',
  `regularization_window` varchar(40) NOT NULL DEFAULT 'Anytime',
  `past_days_allowed` int NOT NULL DEFAULT '7',
  `restrict_regularization_requests_per_month` tinyint(1) NOT NULL DEFAULT '0',
  `max_regularization_requests_per_month` int NOT NULL DEFAULT '3',
  `created_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `updated_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  `client_id` int DEFAULT NULL,
  PRIMARY KEY (`id`),
  UNIQUE KEY `UX_attendance_client` (`client_id`)
) ENGINE=InnoDB AUTO_INCREMENT=4 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;


-- payroll.auditlogs definition

CREATE TABLE `auditlogs` (
  `Id` bigint NOT NULL AUTO_INCREMENT,
  `UserId` int DEFAULT NULL,
  `UserEmail` varchar(190) DEFAULT NULL,
  `Action` varchar(120) NOT NULL,
  `Resource` varchar(190) DEFAULT NULL,
  `Method` varchar(20) DEFAULT NULL,
  `Path` varchar(500) DEFAULT NULL,
  `StatusCode` int NOT NULL DEFAULT '0',
  `IpAddress` varchar(80) DEFAULT NULL,
  `UserAgent` varchar(500) DEFAULT NULL,
  `DetailsJson` json NOT NULL,
  `CreatedAt` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`Id`),
  KEY `IX_AuditLogs_CreatedAt` (`CreatedAt`),
  KEY `IX_AuditLogs_UserId` (`UserId`),
  KEY `IX_AuditLogs_Action` (`Action`)
) ENGINE=InnoDB AUTO_INCREMENT=173 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;


-- payroll.authpermissions definition

CREATE TABLE `authpermissions` (
  `Id` int NOT NULL AUTO_INCREMENT,
  `Code` varchar(120) NOT NULL,
  `Name` varchar(150) NOT NULL,
  `Module` varchar(80) NOT NULL,
  `Description` varchar(500) DEFAULT NULL,
  `CreatedAt` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`Id`),
  UNIQUE KEY `UX_AuthPermissions_Code` (`Code`)
) ENGINE=InnoDB AUTO_INCREMENT=533 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;


-- payroll.authroles definition

CREATE TABLE `authroles` (
  `Id` int NOT NULL AUTO_INCREMENT,
  `Code` varchar(80) NOT NULL,
  `Name` varchar(150) NOT NULL,
  `Description` varchar(500) DEFAULT NULL,
  `IsSystem` tinyint(1) NOT NULL DEFAULT '0',
  `CreatedAt` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`Id`),
  UNIQUE KEY `UX_AuthRoles_Code` (`Code`)
) ENGINE=InnoDB AUTO_INCREMENT=282 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;


-- payroll.authusers definition

CREATE TABLE `authusers` (
  `Id` int NOT NULL AUTO_INCREMENT,
  `Email` varchar(190) NOT NULL,
  `DisplayName` varchar(190) NOT NULL,
  `PasswordHash` varchar(500) NOT NULL,
  `ClientId` int DEFAULT NULL,
  `IsActive` tinyint(1) NOT NULL DEFAULT '1',
  `MustChangePassword` tinyint(1) NOT NULL DEFAULT '0',
  `LastLoginAt` datetime DEFAULT NULL,
  `CreatedAt` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `UpdatedAt` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  `EmployeeId` int DEFAULT NULL,
  PRIMARY KEY (`Id`),
  UNIQUE KEY `UX_AuthUsers_Email` (`Email`)
) ENGINE=InnoDB AUTO_INCREMENT=3 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;


-- payroll.clients definition

CREATE TABLE `clients` (
  `Id` int NOT NULL AUTO_INCREMENT,
  `Name` varchar(250) NOT NULL,
  `Code` varchar(50) DEFAULT NULL,
  `ContactPerson` varchar(150) DEFAULT NULL,
  `Email` varchar(150) DEFAULT NULL,
  `Phone` varchar(50) DEFAULT NULL,
  `Address` varchar(500) DEFAULT NULL,
  `IsActive` tinyint(1) NOT NULL DEFAULT '1',
  `CreatedAt` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `UpdatedAt` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  `PayScheduleJson` json DEFAULT NULL,
  PRIMARY KEY (`Id`)
) ENGINE=InnoDB AUTO_INCREMENT=7 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;


-- payroll.departmentheadassignments definition

CREATE TABLE `departmentheadassignments` (
  `Id` int NOT NULL AUTO_INCREMENT,
  `ClientId` int NOT NULL,
  `Department` varchar(100) NOT NULL,
  `UserId` int NOT NULL,
  PRIMARY KEY (`Id`),
  UNIQUE KEY `UX_DepartmentHeadAssignment` (`ClientId`,`Department`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;


-- payroll.dropdownmasters definition

CREATE TABLE `dropdownmasters` (
  `Id` int NOT NULL AUTO_INCREMENT,
  `Type` varchar(100) NOT NULL,
  `Value` varchar(200) NOT NULL,
  `IsActive` tinyint(1) NOT NULL DEFAULT '1',
  `CreatedAt` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `UpdatedAt` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`Id`),
  UNIQUE KEY `UX_DropdownMasters_Type_Value` (`Type`,`Value`)
) ENGINE=InnoDB AUTO_INCREMENT=33 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;


-- payroll.employees definition

CREATE TABLE `employees` (
  `Id` int NOT NULL AUTO_INCREMENT,
  `ClientId` int NOT NULL,
  `EmployeeCode` varchar(50) NOT NULL,
  `FirstName` varchar(100) NOT NULL,
  `LastName` varchar(100) DEFAULT NULL,
  `Gender` varchar(30) DEFAULT NULL,
  `DateOfJoining` varchar(30) DEFAULT NULL,
  `WorkEmail` varchar(150) DEFAULT NULL,
  `Department` varchar(100) DEFAULT NULL,
  `Designation` varchar(100) DEFAULT NULL,
  `WorkLocationId` int NOT NULL DEFAULT '0',
  `ReportingManagerId` int NOT NULL DEFAULT '0',
  `PortalAccess` tinyint(1) NOT NULL DEFAULT '0',
  `SalaryStructureId` varchar(50) DEFAULT NULL,
  `AnnualCtc` decimal(18,2) NOT NULL DEFAULT '0.00',
  `SalaryJson` json NOT NULL,
  `PersonalJson` json NOT NULL,
  `PaymentJson` json NOT NULL,
  `IsActive` tinyint(1) NOT NULL DEFAULT '1',
  `CreatedAt` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `UpdatedAt` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`Id`),
  UNIQUE KEY `UX_Employees_Client_Code` (`ClientId`,`EmployeeCode`)
) ENGINE=InnoDB AUTO_INCREMENT=595 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;


-- payroll.essleaverequests definition

CREATE TABLE `essleaverequests` (
  `Id` bigint NOT NULL AUTO_INCREMENT,
  `EmployeeId` int NOT NULL,
  `ClientId` int NOT NULL,
  `LeaveTypeId` int NOT NULL,
  `FromDate` date NOT NULL,
  `ToDate` date NOT NULL,
  `Days` decimal(8,2) NOT NULL,
  `Reason` varchar(1000) DEFAULT NULL,
  `Status` varchar(40) NOT NULL,
  `CreatedAt` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`Id`)
) ENGINE=InnoDB AUTO_INCREMENT=2 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;


-- payroll.holidays definition

CREATE TABLE `holidays` (
  `id` int NOT NULL AUTO_INCREMENT,
  `name` varchar(180) NOT NULL,
  `start_date` date NOT NULL,
  `end_date` date NOT NULL,
  `description` varchar(800) DEFAULT NULL,
  `all_locations` tinyint(1) NOT NULL DEFAULT '1',
  `created_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `updated_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  `client_id` int DEFAULT NULL,
  PRIMARY KEY (`id`),
  KEY `IX_holidays_dates` (`start_date`,`end_date`)
) ENGINE=InnoDB AUTO_INCREMENT=2 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;


-- payroll.leave_attendance_preferences definition

CREATE TABLE `leave_attendance_preferences` (
  `id` int NOT NULL AUTO_INCREMENT,
  `attendance_cycle_start_day` int NOT NULL DEFAULT '1',
  `attendance_cycle_end_day` int NOT NULL DEFAULT '25',
  `payroll_report_generation_day` int NOT NULL DEFAULT '28',
  `include_leave_encashment_in_pay_run` tinyint(1) NOT NULL DEFAULT '0',
  `leave_encashment_salary_component_id` int DEFAULT NULL,
  `created_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `updated_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  `client_id` int DEFAULT NULL,
  PRIMARY KEY (`id`),
  UNIQUE KEY `UX_preferences_client` (`client_id`)
) ENGINE=InnoDB AUTO_INCREMENT=3 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;


-- payroll.leave_balance_import_logs definition

CREATE TABLE `leave_balance_import_logs` (
  `id` int NOT NULL AUTO_INCREMENT,
  `file_name` varchar(260) NOT NULL,
  `encoding` varchar(80) NOT NULL,
  `total_records` int NOT NULL DEFAULT '0',
  `imported_records` int NOT NULL DEFAULT '0',
  `skipped_records` int NOT NULL DEFAULT '0',
  `mapping_json` json DEFAULT NULL,
  `created_by` varchar(180) DEFAULT NULL,
  `created_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `client_id` int DEFAULT NULL,
  PRIMARY KEY (`id`)
) ENGINE=InnoDB AUTO_INCREMENT=2 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;


-- payroll.leave_types definition

CREATE TABLE `leave_types` (
  `id` int NOT NULL AUTO_INCREMENT,
  `name` varchar(180) NOT NULL,
  `code` varchar(40) NOT NULL,
  `type` varchar(20) NOT NULL DEFAULT 'Paid',
  `description` varchar(800) DEFAULT NULL,
  `is_active` tinyint(1) NOT NULL DEFAULT '1',
  `created_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `updated_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  `client_id` int DEFAULT NULL,
  PRIMARY KEY (`id`),
  UNIQUE KEY `UX_leave_types_client_code` (`client_id`,`code`)
) ENGINE=InnoDB AUTO_INCREMENT=11 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;


-- payroll.modulesettings definition

CREATE TABLE `modulesettings` (
  `Id` int NOT NULL AUTO_INCREMENT,
  `ModuleCode` varchar(80) NOT NULL,
  `IsEnabled` tinyint(1) NOT NULL DEFAULT '0',
  `SettingsJson` json DEFAULT NULL,
  `CreatedAt` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `UpdatedAt` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  `client_id` int DEFAULT NULL,
  PRIMARY KEY (`Id`),
  UNIQUE KEY `UX_ModuleSettings_ModuleCode` (`ModuleCode`),
  UNIQUE KEY `UX_ModuleSettings_Client_Module` (`client_id`,`ModuleCode`)
) ENGINE=InnoDB AUTO_INCREMENT=141 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;


-- payroll.modulesetupprogress definition

CREATE TABLE `modulesetupprogress` (
  `Id` int NOT NULL AUTO_INCREMENT,
  `ModuleCode` varchar(80) NOT NULL,
  `StepCode` varchar(80) NOT NULL,
  `Title` varchar(180) NOT NULL,
  `Description` varchar(600) DEFAULT NULL,
  `Status` varchar(40) NOT NULL DEFAULT 'Not Started',
  `IsMandatory` tinyint(1) NOT NULL DEFAULT '0',
  `CanDisable` tinyint(1) NOT NULL DEFAULT '1',
  `CreatedAt` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `UpdatedAt` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  `client_id` int DEFAULT NULL,
  PRIMARY KEY (`Id`),
  UNIQUE KEY `UX_ModuleSetupProgress_Module_Step` (`ModuleCode`,`StepCode`),
  UNIQUE KEY `UX_ModuleSetupProgress_Client_Module_Step` (`client_id`,`ModuleCode`,`StepCode`),
  KEY `IX_ModuleSetupProgress_ModuleCode` (`ModuleCode`)
) ENGINE=InnoDB AUTO_INCREMENT=701 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;


-- payroll.organizations definition

CREATE TABLE `organizations` (
  `Id` int NOT NULL AUTO_INCREMENT,
  `Name` varchar(250) NOT NULL,
  `LegalName` varchar(250) DEFAULT NULL,
  `BusinessType` varchar(150) DEFAULT NULL,
  `BusinessLocation` varchar(100) NOT NULL DEFAULT 'India',
  `Industry` varchar(150) DEFAULT NULL,
  `HasRunPayrollThisYear` tinyint(1) NOT NULL DEFAULT '0',
  `SetupCompleted` tinyint(1) NOT NULL DEFAULT '0',
  `PAN` varchar(50) DEFAULT NULL,
  `GSTIN` varchar(50) DEFAULT NULL,
  `FiscalYearStart` varchar(50) DEFAULT NULL,
  `AddressLine1` varchar(255) DEFAULT NULL,
  `AddressLine2` varchar(255) DEFAULT NULL,
  `City` varchar(100) DEFAULT NULL,
  `State` varchar(100) DEFAULT NULL,
  `PostalCode` varchar(30) DEFAULT NULL,
  `Country` varchar(100) DEFAULT NULL,
  `BankName` varchar(200) DEFAULT NULL,
  `AccountNumber` varchar(100) DEFAULT NULL,
  `IFSCCode` varchar(50) DEFAULT NULL,
  `CreatedAt` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `UpdatedAt` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  `LogoDataUrl` longtext,
  PRIMARY KEY (`Id`)
) ENGINE=InnoDB AUTO_INCREMENT=2 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;


-- payroll.payrollsetups definition

CREATE TABLE `payrollsetups` (
  `Id` int NOT NULL AUTO_INCREMENT,
  `SetupJson` json NOT NULL,
  `UpdatedAt` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`Id`)
) ENGINE=InnoDB AUTO_INCREMENT=2 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;


-- payroll.payruns definition

CREATE TABLE `payruns` (
  `Id` int NOT NULL AUTO_INCREMENT,
  `PayPeriod` varchar(7) NOT NULL,
  `PayDate` date NOT NULL,
  `TotalWorkingDays` int NOT NULL,
  `Status` varchar(30) NOT NULL DEFAULT 'Draft',
  `PayrollCost` decimal(18,2) NOT NULL DEFAULT '0.00',
  `NetPay` decimal(18,2) NOT NULL DEFAULT '0.00',
  `CreatedAt` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `UpdatedAt` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  `ClientId` int NOT NULL DEFAULT '0',
  `ClientName` varchar(250) DEFAULT NULL,
  `RunCode` varchar(40) NOT NULL DEFAULT 'REGULAR',
  `RunType` varchar(30) NOT NULL DEFAULT 'Regular',
  `RunName` varchar(120) NOT NULL DEFAULT '',
  `Reason` varchar(500) NOT NULL DEFAULT '',
  PRIMARY KEY (`Id`),
  UNIQUE KEY `UX_PayRuns_Client_Period_Code` (`ClientId`,`PayPeriod`,`RunCode`)
) ENGINE=InnoDB AUTO_INCREMENT=9 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;


-- payroll.workflowhistory definition

CREATE TABLE `workflowhistory` (
  `Id` bigint NOT NULL AUTO_INCREMENT,
  `InstanceId` bigint NOT NULL,
  `TaskId` bigint DEFAULT NULL,
  `Action` varchar(30) NOT NULL,
  `ActorUserId` int NOT NULL,
  `Comment` varchar(1000) DEFAULT NULL,
  `CreatedAt` datetime DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`Id`)
) ENGINE=InnoDB AUTO_INCREMENT=4 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;


-- payroll.workflowinstances definition

CREATE TABLE `workflowinstances` (
  `Id` bigint NOT NULL AUTO_INCREMENT,
  `WorkflowId` int NOT NULL,
  `ResourceType` varchar(100) NOT NULL,
  `ResourceId` varchar(120) NOT NULL,
  `RequestorUserId` int NOT NULL,
  `PayloadJson` json NOT NULL,
  `Status` varchar(30) NOT NULL DEFAULT 'Pending',
  `CreatedAt` datetime DEFAULT CURRENT_TIMESTAMP,
  `CompletedAt` datetime DEFAULT NULL,
  PRIMARY KEY (`Id`)
) ENGINE=InnoDB AUTO_INCREMENT=3 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;


-- payroll.workflowmasters definition

CREATE TABLE `workflowmasters` (
  `Id` int NOT NULL AUTO_INCREMENT,
  `ClientId` int DEFAULT NULL,
  `Code` varchar(80) NOT NULL,
  `Name` varchar(180) NOT NULL,
  `ResourceType` varchar(100) NOT NULL,
  `IsActive` tinyint(1) NOT NULL DEFAULT '1',
  `CreatedAt` datetime DEFAULT CURRENT_TIMESTAMP,
  `UpdatedAt` datetime DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`Id`)
) ENGINE=InnoDB AUTO_INCREMENT=6 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;


-- payroll.workflowstages definition

CREATE TABLE `workflowstages` (
  `Id` int NOT NULL AUTO_INCREMENT,
  `WorkflowId` int NOT NULL,
  `StageOrder` int NOT NULL,
  `Name` varchar(180) NOT NULL,
  `ApproverType` varchar(40) NOT NULL,
  `ApproverUserId` int DEFAULT NULL,
  PRIMARY KEY (`Id`)
) ENGINE=InnoDB AUTO_INCREMENT=8 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;


-- payroll.workflowtasks definition

CREATE TABLE `workflowtasks` (
  `Id` bigint NOT NULL AUTO_INCREMENT,
  `InstanceId` bigint NOT NULL,
  `StageId` int NOT NULL,
  `ApproverUserId` int NOT NULL,
  `Status` varchar(30) NOT NULL DEFAULT 'Pending',
  `CreatedAt` datetime DEFAULT CURRENT_TIMESTAMP,
  `ActionedAt` datetime DEFAULT NULL,
  `Comment` varchar(1000) DEFAULT NULL,
  PRIMARY KEY (`Id`)
) ENGINE=InnoDB AUTO_INCREMENT=3 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;


-- payroll.worklocations definition

CREATE TABLE `worklocations` (
  `Id` int NOT NULL AUTO_INCREMENT,
  `Name` varchar(200) NOT NULL,
  `Address` varchar(500) DEFAULT NULL,
  `City` varchar(100) DEFAULT NULL,
  `State` varchar(100) DEFAULT NULL,
  `PostalCode` varchar(30) DEFAULT NULL,
  `IsPrimary` tinyint(1) NOT NULL DEFAULT '0',
  `IsActive` tinyint(1) NOT NULL DEFAULT '1',
  `CreatedAt` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `UpdatedAt` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`Id`)
) ENGINE=InnoDB AUTO_INCREMENT=44 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;


-- payroll.authrolepermissions definition

CREATE TABLE `authrolepermissions` (
  `RoleId` int NOT NULL,
  `PermissionId` int NOT NULL,
  PRIMARY KEY (`RoleId`,`PermissionId`),
  KEY `FK_AuthRolePermissions_Permission` (`PermissionId`),
  CONSTRAINT `FK_AuthRolePermissions_Permission` FOREIGN KEY (`PermissionId`) REFERENCES `authpermissions` (`Id`) ON DELETE CASCADE,
  CONSTRAINT `FK_AuthRolePermissions_Role` FOREIGN KEY (`RoleId`) REFERENCES `authroles` (`Id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;


-- payroll.authsessions definition

CREATE TABLE `authsessions` (
  `Id` bigint NOT NULL AUTO_INCREMENT,
  `UserId` int NOT NULL,
  `TokenHash` char(64) NOT NULL,
  `IpAddress` varchar(80) DEFAULT NULL,
  `UserAgent` varchar(500) DEFAULT NULL,
  `ExpiresAt` datetime NOT NULL,
  `RevokedAt` datetime DEFAULT NULL,
  `CreatedAt` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`Id`),
  UNIQUE KEY `UX_AuthSessions_TokenHash` (`TokenHash`),
  KEY `IX_AuthSessions_User` (`UserId`),
  CONSTRAINT `FK_AuthSessions_User` FOREIGN KEY (`UserId`) REFERENCES `authusers` (`Id`) ON DELETE CASCADE
) ENGINE=InnoDB AUTO_INCREMENT=53 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;


-- payroll.authuserroles definition

CREATE TABLE `authuserroles` (
  `UserId` int NOT NULL,
  `RoleId` int NOT NULL,
  PRIMARY KEY (`UserId`,`RoleId`),
  KEY `FK_AuthUserRoles_Role` (`RoleId`),
  CONSTRAINT `FK_AuthUserRoles_Role` FOREIGN KEY (`RoleId`) REFERENCES `authroles` (`Id`) ON DELETE CASCADE,
  CONSTRAINT `FK_AuthUserRoles_User` FOREIGN KEY (`UserId`) REFERENCES `authusers` (`Id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;


-- payroll.employee_daily_attendance definition

CREATE TABLE `employee_daily_attendance` (
  `id` int NOT NULL AUTO_INCREMENT,
  `client_id` int NOT NULL,
  `employee_id` int NOT NULL,
  `attendance_date` date NOT NULL,
  `status` varchar(30) NOT NULL DEFAULT 'Present',
  `payable_value` decimal(4,2) NOT NULL DEFAULT '1.00',
  `check_in_time` time DEFAULT NULL,
  `check_out_time` time DEFAULT NULL,
  `total_hours` decimal(5,2) NOT NULL DEFAULT '0.00',
  `remarks` varchar(600) DEFAULT NULL,
  `created_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `updated_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`id`),
  UNIQUE KEY `UX_daily_attendance_employee_date` (`client_id`,`employee_id`,`attendance_date`),
  KEY `IX_daily_attendance_client_date` (`client_id`,`attendance_date`),
  KEY `FK_daily_attendance_employee` (`employee_id`),
  CONSTRAINT `FK_daily_attendance_employee` FOREIGN KEY (`employee_id`) REFERENCES `employees` (`Id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;


-- payroll.employee_leave_balances definition

CREATE TABLE `employee_leave_balances` (
  `id` int NOT NULL AUTO_INCREMENT,
  `employee_id` int NOT NULL,
  `leave_type_id` int NOT NULL,
  `balance_date` date NOT NULL,
  `balance_count` decimal(10,2) NOT NULL DEFAULT '0.00',
  `created_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `updated_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  `client_id` int DEFAULT NULL,
  `BalanceCount` decimal(10,2) GENERATED ALWAYS AS (`balance_count`) VIRTUAL,
  `BalanceDate` date GENERATED ALWAYS AS (`balance_date`) VIRTUAL,
  PRIMARY KEY (`id`),
  UNIQUE KEY `UX_employee_leave_balances_employee_type_date` (`employee_id`,`leave_type_id`,`balance_date`),
  KEY `IX_employee_leave_balances_employee` (`employee_id`),
  KEY `FK_employee_leave_balances_leave_type` (`leave_type_id`),
  CONSTRAINT `FK_employee_leave_balances_employee` FOREIGN KEY (`employee_id`) REFERENCES `employees` (`Id`) ON DELETE CASCADE,
  CONSTRAINT `FK_employee_leave_balances_leave_type` FOREIGN KEY (`leave_type_id`) REFERENCES `leave_types` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB AUTO_INCREMENT=373 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;


-- payroll.employee_monthly_attendance definition

CREATE TABLE `employee_monthly_attendance` (
  `id` int NOT NULL AUTO_INCREMENT,
  `client_id` int NOT NULL,
  `employee_id` int NOT NULL,
  `attendance_month` varchar(7) NOT NULL,
  `working_days` decimal(5,2) NOT NULL DEFAULT '0.00',
  `present_days` decimal(5,2) NOT NULL DEFAULT '0.00',
  `payable_days` decimal(5,2) NOT NULL DEFAULT '0.00',
  `lop_days` decimal(5,2) NOT NULL DEFAULT '0.00',
  `source_type` varchar(30) NOT NULL DEFAULT 'Monthly',
  `remarks` varchar(600) DEFAULT NULL,
  `created_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `updated_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`id`),
  UNIQUE KEY `UX_monthly_attendance_employee_month` (`client_id`,`employee_id`,`attendance_month`),
  KEY `IX_monthly_attendance_client_month` (`client_id`,`attendance_month`),
  KEY `FK_monthly_attendance_employee` (`employee_id`),
  CONSTRAINT `FK_monthly_attendance_employee` FOREIGN KEY (`employee_id`) REFERENCES `employees` (`Id`) ON DELETE CASCADE
) ENGINE=InnoDB AUTO_INCREMENT=11 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;


-- payroll.holiday_locations definition

CREATE TABLE `holiday_locations` (
  `id` int NOT NULL AUTO_INCREMENT,
  `holiday_id` int NOT NULL,
  `work_location_id` int NOT NULL,
  `created_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`id`),
  UNIQUE KEY `UX_holiday_locations_holiday_location` (`holiday_id`,`work_location_id`),
  KEY `IX_holiday_locations_location` (`work_location_id`),
  CONSTRAINT `FK_holiday_locations_holiday` FOREIGN KEY (`holiday_id`) REFERENCES `holidays` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;


-- payroll.leave_balance_import_errors definition

CREATE TABLE `leave_balance_import_errors` (
  `id` int NOT NULL AUTO_INCREMENT,
  `import_log_id` int NOT NULL,
  `row_no` int NOT NULL,
  `employee_number` varchar(80) DEFAULT NULL,
  `leave_type` varchar(180) DEFAULT NULL,
  `date_text` varchar(80) DEFAULT NULL,
  `count_text` varchar(80) DEFAULT NULL,
  `error_message` varchar(1000) NOT NULL,
  `created_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`id`),
  KEY `IX_leave_balance_import_errors_log` (`import_log_id`),
  CONSTRAINT `FK_leave_balance_import_errors_log` FOREIGN KEY (`import_log_id`) REFERENCES `leave_balance_import_logs` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;


-- payroll.leave_type_applicability definition

CREATE TABLE `leave_type_applicability` (
  `id` int NOT NULL AUTO_INCREMENT,
  `leave_type_id` int NOT NULL,
  `applicability_mode` varchar(40) NOT NULL DEFAULT 'All employees',
  `work_location` varchar(150) DEFAULT NULL,
  `department` varchar(150) DEFAULT NULL,
  `designation` varchar(150) DEFAULT NULL,
  `gender` varchar(40) DEFAULT NULL,
  `created_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `updated_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`id`),
  UNIQUE KEY `UX_leave_type_applicability_leave_type` (`leave_type_id`),
  CONSTRAINT `FK_leave_type_applicability_type` FOREIGN KEY (`leave_type_id`) REFERENCES `leave_types` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB AUTO_INCREMENT=9 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;


-- payroll.leave_type_policies definition

CREATE TABLE `leave_type_policies` (
  `id` int NOT NULL AUTO_INCREMENT,
  `leave_type_id` int NOT NULL,
  `entitlement` decimal(10,2) NOT NULL DEFAULT '0.00',
  `entitlement_period` varchar(20) NOT NULL DEFAULT 'Yearly',
  `pro_rate_for_new_joinees` tinyint(1) NOT NULL DEFAULT '0',
  `reset_enabled` tinyint(1) NOT NULL DEFAULT '0',
  `reset_frequency` varchar(20) NOT NULL DEFAULT 'Yearly',
  `carry_forward_unused_leaves` tinyint(1) NOT NULL DEFAULT '0',
  `max_carry_forward_limit` decimal(10,2) DEFAULT NULL,
  `encash_unused_leaves` tinyint(1) NOT NULL DEFAULT '0',
  `max_encashment_limit` decimal(10,2) DEFAULT NULL,
  `allow_negative_leave_balance` tinyint(1) NOT NULL DEFAULT '0',
  `negative_balance_handling` varchar(50) NOT NULL DEFAULT 'Mark as LOP',
  `allow_past_dates` tinyint(1) NOT NULL DEFAULT '0',
  `past_date_limit_type` varchar(30) NOT NULL DEFAULT 'No limit',
  `past_date_limit_days` int DEFAULT NULL,
  `allow_future_dates` tinyint(1) NOT NULL DEFAULT '0',
  `future_date_limit_type` varchar(30) NOT NULL DEFAULT 'No limit',
  `future_date_limit_days` int DEFAULT NULL,
  `effective_from` date NOT NULL,
  `expires_on` date DEFAULT NULL,
  `postpone_credits_for_new_employees` tinyint(1) NOT NULL DEFAULT '0',
  `postpone_credit_value` int DEFAULT NULL,
  `postpone_credit_unit` varchar(20) NOT NULL DEFAULT 'Days',
  `created_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `updated_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`id`),
  UNIQUE KEY `UX_leave_type_policies_leave_type` (`leave_type_id`),
  CONSTRAINT `FK_leave_type_policies_type` FOREIGN KEY (`leave_type_id`) REFERENCES `leave_types` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB AUTO_INCREMENT=9 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;


-- payroll.payrolladjustments definition

CREATE TABLE `payrolladjustments` (
  `Id` int NOT NULL AUTO_INCREMENT,
  `ClientId` int NOT NULL,
  `EmployeeId` int NOT NULL,
  `EmployeeName` varchar(250) NOT NULL DEFAULT '',
  `EmployeeCode` varchar(50) NOT NULL DEFAULT '',
  `ComponentId` int NOT NULL DEFAULT '0',
  `ComponentCode` varchar(50) NOT NULL DEFAULT '',
  `ComponentName` varchar(150) NOT NULL,
  `AdjustmentType` varchar(30) NOT NULL,
  `Amount` decimal(18,2) NOT NULL,
  `PayPeriod` varchar(7) NOT NULL,
  `PayRunType` varchar(30) NOT NULL DEFAULT 'Regular',
  `ReasonCode` varchar(80) NOT NULL DEFAULT '',
  `Notes` varchar(500) NOT NULL DEFAULT '',
  `Taxable` tinyint(1) NOT NULL DEFAULT '1',
  `Status` varchar(30) NOT NULL DEFAULT 'Approved',
  `PayRunId` int DEFAULT NULL,
  `CreatedAt` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `UpdatedAt` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`Id`),
  KEY `IX_PayrollAdjustments_Client_Period_Status` (`ClientId`,`PayPeriod`,`Status`),
  KEY `FK_PayrollAdjustments_Employees` (`EmployeeId`),
  KEY `FK_PayrollAdjustments_PayRuns` (`PayRunId`),
  CONSTRAINT `FK_PayrollAdjustments_Employees` FOREIGN KEY (`EmployeeId`) REFERENCES `employees` (`Id`),
  CONSTRAINT `FK_PayrollAdjustments_PayRuns` FOREIGN KEY (`PayRunId`) REFERENCES `payruns` (`Id`) ON DELETE SET NULL
) ENGINE=InnoDB AUTO_INCREMENT=3 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;


-- payroll.payrunemployees definition

CREATE TABLE `payrunemployees` (
  `Id` int NOT NULL AUTO_INCREMENT,
  `PayRunId` int NOT NULL,
  `EmployeeId` int NOT NULL,
  `EmployeeCode` varchar(50) NOT NULL,
  `EmployeeName` varchar(250) NOT NULL,
  `Department` varchar(100) DEFAULT NULL,
  `PresentDays` int NOT NULL,
  `PayableDays` int NOT NULL,
  `MonthlyGross` decimal(18,2) NOT NULL DEFAULT '0.00',
  `GrossPay` decimal(18,2) NOT NULL DEFAULT '0.00',
  `StatutoryDeductions` decimal(18,2) NOT NULL DEFAULT '0.00',
  `OneTimeEarnings` decimal(18,2) NOT NULL DEFAULT '0.00',
  `OneTimeDeductions` decimal(18,2) NOT NULL DEFAULT '0.00',
  `NetPay` decimal(18,2) NOT NULL DEFAULT '0.00',
  `IsSkipped` tinyint(1) NOT NULL DEFAULT '0',
  `PaymentStatus` varchar(30) NOT NULL DEFAULT 'Pending',
  `DetailsJson` json NOT NULL,
  `PaymentDate` date DEFAULT NULL,
  `ClientId` int NOT NULL DEFAULT '0',
  `ClientName` varchar(250) DEFAULT NULL,
  `ManualTds` decimal(18,2) NOT NULL DEFAULT '0.00',
  PRIMARY KEY (`Id`),
  UNIQUE KEY `UX_PayRunEmployees_Run_Employee` (`PayRunId`,`EmployeeId`),
  CONSTRAINT `FK_PayRunEmployees_PayRuns` FOREIGN KEY (`PayRunId`) REFERENCES `payruns` (`Id`) ON DELETE CASCADE
) ENGINE=InnoDB AUTO_INCREMENT=132 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
