using System.Security.Cryptography;
using System.Text.Json;
using MySqlConnector;

var password = args.FirstOrDefault(arg => arg.StartsWith("--password=", StringComparison.OrdinalIgnoreCase))?.Split('=', 2)[1] ?? "Test@12345";
var root = FindRepoRoot(AppContext.BaseDirectory);
var settingsPath = Path.Combine(root, "Payroll.API", "appsettings.Development.json");
if (!File.Exists(settingsPath)) settingsPath = Path.Combine(root, "Payroll.API", "appsettings.json");
if (!File.Exists(settingsPath)) throw new FileNotFoundException("Could not find API appsettings.", settingsPath);

using var settings = JsonDocument.Parse(File.ReadAllText(settingsPath));
var connectionString = settings.RootElement.GetProperty("ConnectionStrings").GetProperty("Default").GetString();
if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("ConnectionStrings:Default is missing.");
if (!connectionString.Contains("Allow User Variables", StringComparison.OrdinalIgnoreCase))
{
    connectionString = connectionString.TrimEnd(';') + ";Allow User Variables=True;";
}

var hash = HashPassword(password).Replace("'", "''");
var sql = SeedSql(hash);

await using var connection = new MySqlConnection(connectionString);
await connection.OpenAsync();
foreach (var statement in sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
{
    await using var command = connection.CreateCommand();
    command.CommandText = statement;
    command.CommandTimeout = 180;
    try
    {
        await command.ExecuteNonQueryAsync();
    }
    catch (Exception ex)
    {
        throw new InvalidOperationException($"Recruitment seed failed at SQL statement: {statement}", ex);
    }
}

var clientId = await ScalarAsync(connection, "SELECT Id FROM clients WHERE Code='TAT' ORDER BY Id LIMIT 1;");
var employees = await ScalarAsync(connection, $"SELECT COUNT(*) FROM employees WHERE ClientId={clientId} AND EmployeeCode LIKE 'TAT%';");
var users = await ScalarAsync(connection, "SELECT COUNT(*) FROM authusers WHERE Email LIKE 'tat.%@frevo.local';");
var masters = await ScalarAsync(connection, $"SELECT COUNT(*) FROM recruitment_master_values WHERE ClientId={clientId};");

Console.WriteLine("Recruitment test data ready.");
Console.WriteLine($"Client: TAT / TA Test Client Pvt Ltd (Id: {clientId})");
Console.WriteLine($"Employees: {employees} | Users: {users} | Recruitment masters: {masters}");
Console.WriteLine($"Password for test users: {password}");

static string FindRepoRoot(string start)
{
    var directory = new DirectoryInfo(start);
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "Payroll.sln"))) return directory.FullName;
        directory = directory.Parent;
    }
    return Directory.GetCurrentDirectory();
}

static async Task<object?> ScalarAsync(MySqlConnection connection, string sql)
{
    await using var command = connection.CreateCommand();
    command.CommandText = sql;
    return await command.ExecuteScalarAsync();
}

static string HashPassword(string password)
{
    var salt = RandomNumberGenerator.GetBytes(16);
    var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 120_000, HashAlgorithmName.SHA256, 32);
    return $"PBKDF2-SHA256$120000${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
}

static string SeedSql(string hash) => """
START TRANSACTION;

INSERT INTO clients (Name, Code, ContactPerson, Email, Phone, Address, IsActive)
SELECT 'TA Test Client Pvt Ltd', 'TAT', 'Radhika Sharma', 'ta.client.test@frevo.local', '9999900001', 'Recruitment test client - safe seed data', TRUE
WHERE NOT EXISTS (SELECT 1 FROM clients WHERE Code='TAT');
SET @client_id := (SELECT Id FROM clients WHERE Code='TAT' ORDER BY Id LIMIT 1);
UPDATE clients SET Name='TA Test Client Pvt Ltd', ContactPerson='Radhika Sharma', Email='ta.client.test@frevo.local', Phone='9999900001', IsActive=TRUE WHERE Id=@client_id;

INSERT INTO worklocations (ClientId, ClientName, Name, Address, City, State, PostalCode, IsPrimary, IsActive)
SELECT @client_id, 'TA Test Client Pvt Ltd', 'TAT Corporate Office', 'Tower A, Recruitment Test Park', 'Gurugram', 'Haryana', '122001', TRUE, TRUE
WHERE NOT EXISTS (SELECT 1 FROM worklocations WHERE ClientId=@client_id AND Name='TAT Corporate Office');
INSERT INTO worklocations (ClientId, ClientName, Name, Address, City, State, PostalCode, IsPrimary, IsActive)
SELECT @client_id, 'TA Test Client Pvt Ltd', 'TAT Delivery Center', 'Plot 9, Talent Hub', 'Noida', 'Uttar Pradesh', '201301', FALSE, TRUE
WHERE NOT EXISTS (SELECT 1 FROM worklocations WHERE ClientId=@client_id AND Name='TAT Delivery Center');
SET @loc_main := (SELECT Id FROM worklocations WHERE ClientId=@client_id AND Name='TAT Corporate Office' ORDER BY Id LIMIT 1);
SET @loc_delivery := (SELECT Id FROM worklocations WHERE ClientId=@client_id AND Name='TAT Delivery Center' ORDER BY Id LIMIT 1);

INSERT INTO employees (ClientId, EmployeeCode, FirstName, LastName, Gender, DateOfJoining, WorkEmail, Department, Designation, Grade, WorkLocationId, ReportingManagerId, ReportingManagerUserId, PortalAccess, SalaryStructureId, AnnualCtc, SalaryJson, PersonalJson, PaymentJson, IsActive)
VALUES
(@client_id, 'TAT100', 'Anita', 'Requester', 'Female', '2026-04-01', 'tat.requester@frevo.local', 'Engineering', 'Team Lead', 'L3', @loc_delivery, 0, NULL, TRUE, '', 900000, '{}', '{}', '{}', TRUE)
ON DUPLICATE KEY UPDATE FirstName=VALUES(FirstName), LastName=VALUES(LastName), WorkEmail=VALUES(WorkEmail), Department=VALUES(Department), Designation=VALUES(Designation), Grade=VALUES(Grade), WorkLocationId=VALUES(WorkLocationId), PortalAccess=TRUE, IsActive=TRUE;

INSERT INTO employees (ClientId, EmployeeCode, FirstName, LastName, Gender, DateOfJoining, WorkEmail, Department, Designation, Grade, WorkLocationId, ReportingManagerId, ReportingManagerUserId, PortalAccess, SalaryStructureId, AnnualCtc, SalaryJson, PersonalJson, PaymentJson, IsActive)
VALUES
(@client_id, 'TAT101', 'Mohan', 'Approver', 'Male', '2026-03-01', 'tat.approver@frevo.local', 'Engineering', 'Delivery Manager', 'M2', @loc_delivery, 0, NULL, TRUE, '', 1500000, '{}', '{}', '{}', TRUE)
ON DUPLICATE KEY UPDATE FirstName=VALUES(FirstName), LastName=VALUES(LastName), WorkEmail=VALUES(WorkEmail), Department=VALUES(Department), Designation=VALUES(Designation), Grade=VALUES(Grade), WorkLocationId=VALUES(WorkLocationId), PortalAccess=TRUE, IsActive=TRUE;

INSERT INTO employees (ClientId, EmployeeCode, FirstName, LastName, Gender, DateOfJoining, WorkEmail, Department, Designation, Grade, WorkLocationId, ReportingManagerId, ReportingManagerUserId, PortalAccess, SalaryStructureId, AnnualCtc, SalaryJson, PersonalJson, PaymentJson, IsActive)
VALUES
(@client_id, 'TAT102', 'Rekha', 'Recruiter', 'Female', '2026-02-01', 'tat.recruiter@frevo.local', 'Human Resources', 'Recruiter', 'HR2', @loc_main, 0, NULL, TRUE, '', 800000, '{}', '{}', '{}', TRUE)
ON DUPLICATE KEY UPDATE FirstName=VALUES(FirstName), LastName=VALUES(LastName), WorkEmail=VALUES(WorkEmail), Department=VALUES(Department), Designation=VALUES(Designation), Grade=VALUES(Grade), WorkLocationId=VALUES(WorkLocationId), PortalAccess=TRUE, IsActive=TRUE;

SET @emp_requester := (SELECT Id FROM employees WHERE ClientId=@client_id AND EmployeeCode='TAT100');
SET @emp_approver := (SELECT Id FROM employees WHERE ClientId=@client_id AND EmployeeCode='TAT101');
SET @emp_recruiter := (SELECT Id FROM employees WHERE ClientId=@client_id AND EmployeeCode='TAT102');

INSERT INTO authusers (Email, DisplayName, PasswordHash, ClientId, EmployeeId, IsActive, MustChangePassword)
VALUES ('tat.requester@frevo.local', 'TAT Anita Requester', '__HASH__', @client_id, @emp_requester, TRUE, FALSE)
ON DUPLICATE KEY UPDATE DisplayName=VALUES(DisplayName), PasswordHash=VALUES(PasswordHash), ClientId=VALUES(ClientId), EmployeeId=VALUES(EmployeeId), IsActive=TRUE, MustChangePassword=FALSE;
INSERT INTO authusers (Email, DisplayName, PasswordHash, ClientId, EmployeeId, IsActive, MustChangePassword)
VALUES ('tat.approver@frevo.local', 'TAT Mohan Approver', '__HASH__', @client_id, @emp_approver, TRUE, FALSE)
ON DUPLICATE KEY UPDATE DisplayName=VALUES(DisplayName), PasswordHash=VALUES(PasswordHash), ClientId=VALUES(ClientId), EmployeeId=VALUES(EmployeeId), IsActive=TRUE, MustChangePassword=FALSE;
INSERT INTO authusers (Email, DisplayName, PasswordHash, ClientId, EmployeeId, IsActive, MustChangePassword)
VALUES ('tat.recruiter@frevo.local', 'TAT Rekha Recruiter', '__HASH__', @client_id, @emp_recruiter, TRUE, FALSE)
ON DUPLICATE KEY UPDATE DisplayName=VALUES(DisplayName), PasswordHash=VALUES(PasswordHash), ClientId=VALUES(ClientId), EmployeeId=VALUES(EmployeeId), IsActive=TRUE, MustChangePassword=FALSE;
INSERT INTO authusers (Email, DisplayName, PasswordHash, ClientId, EmployeeId, IsActive, MustChangePassword)
VALUES ('tat.admin@frevo.local', 'TAT Recruitment Admin', '__HASH__', @client_id, NULL, TRUE, FALSE)
ON DUPLICATE KEY UPDATE DisplayName=VALUES(DisplayName), PasswordHash=VALUES(PasswordHash), ClientId=VALUES(ClientId), IsActive=TRUE, MustChangePassword=FALSE;

SET @user_requester := (SELECT Id FROM authusers WHERE Email='tat.requester@frevo.local');
SET @user_approver := (SELECT Id FROM authusers WHERE Email='tat.approver@frevo.local');
SET @user_recruiter := (SELECT Id FROM authusers WHERE Email='tat.recruiter@frevo.local');
SET @user_admin := (SELECT Id FROM authusers WHERE Email='tat.admin@frevo.local');

UPDATE employees SET ReportingManagerId=@emp_approver, ReportingManagerUserId=@user_approver WHERE Id=@emp_requester;
UPDATE employees SET ReportingManagerUserId=@user_admin WHERE Id IN (@emp_approver, @emp_recruiter);

INSERT IGNORE INTO authuserroles (UserId, RoleId) SELECT @user_requester, Id FROM authroles WHERE Code='employee';
INSERT IGNORE INTO authuserroles (UserId, RoleId) SELECT @user_approver, Id FROM authroles WHERE Code IN ('employee','hr_manager');
INSERT IGNORE INTO authuserroles (UserId, RoleId) SELECT @user_recruiter, Id FROM authroles WHERE Code IN ('employee','hr_manager');
INSERT IGNORE INTO authuserroles (UserId, RoleId) SELECT @user_admin, Id FROM authroles WHERE Code='admin';

INSERT INTO recruitment_settings (ClientId, RecruitmentEnabled, AllowEmployeeRfrCreation, AllowReplacementHiring, AllowMultipleHiringManagers, AllowMultipleRecruiters, AutoGeneratePositionCode, AutoGenerateRfrNumber, EnableVendorHiring, EnableConsultantHiring, EnableInternalHiring, EnableReferralHiring, EnableCampusHiring, EnableWalkInHiring, EnableOfferApproval, EnablePreOfferProcess, EnableBackgroundVerification, EnableDocumentVerification, EnableCandidatePortal, EnableVendorPortal, EnableJobPortalIntegration, IsActive)
VALUES (@client_id, TRUE, TRUE, TRUE, TRUE, TRUE, TRUE, TRUE, TRUE, TRUE, TRUE, TRUE, FALSE, FALSE, TRUE, FALSE, TRUE, TRUE, FALSE, FALSE, FALSE, TRUE)
ON DUPLICATE KEY UPDATE RecruitmentEnabled=TRUE, AllowEmployeeRfrCreation=TRUE, EnableVendorHiring=TRUE, EnableConsultantHiring=TRUE, EnableInternalHiring=TRUE, EnableReferralHiring=TRUE, IsActive=TRUE;

INSERT INTO recruitment_master_values (ClientId, MasterType, Code, Name, Description, SortOrder, IsSystem, IsActive) VALUES
(@client_id,'Position Category','DEV','Software Development','Engineering delivery roles',10,FALSE,TRUE),
(@client_id,'Position Category','QA','Quality Assurance','Testing and automation roles',20,FALSE,TRUE),
(@client_id,'Hiring Type','NEW','New Hire','Additional approved headcount',10,FALSE,TRUE),
(@client_id,'Hiring Type','REPL','Replacement','Replacement hiring',20,FALSE,TRUE),
(@client_id,'Employment Type','PERM','Permanent','Full-time employee',10,FALSE,TRUE),
(@client_id,'Employment Type','CONTRACT','Contract','Fixed-term or contract staffing',20,FALSE,TRUE),
(@client_id,'Recruitment Source','REFERRAL','Employee Referral','Internal employee referral',10,FALSE,TRUE),
(@client_id,'Recruitment Source','VENDOR','Vendor','External vendor source',20,FALSE,TRUE),
(@client_id,'Assignment Priority','HIGH','High','High priority assignment',10,FALSE,TRUE),
(@client_id,'Publishing Channel','INTERNAL','Internal Job Board','ESS internal opening',10,FALSE,TRUE),
(@client_id,'Interview Round','L1','L1 Technical','First technical round',10,FALSE,TRUE),
(@client_id,'Interview Round','HR','HR Discussion','HR and compensation discussion',20,FALSE,TRUE),
(@client_id,'Candidate Status','NEW','New','New candidate',10,FALSE,TRUE),
(@client_id,'Candidate Status','SHORTLISTED','Shortlisted','Shortlisted candidate',20,FALSE,TRUE),
(@client_id,'Offer Status','DRAFT','Draft','Draft offer',10,FALSE,TRUE),
(@client_id,'Offer Status','RELEASED','Released','Offer released',20,FALSE,TRUE)
ON DUPLICATE KEY UPDATE Name=VALUES(Name), Description=VALUES(Description), SortOrder=VALUES(SortOrder), IsActive=TRUE;

INSERT INTO recruitment_partners (PartnerType, ClientId, Code, Name, Company, ContactPerson, Email, Phone, CommissionType, CommissionValue, Status, PerformanceRating, IsActive)
VALUES
('Vendor', @client_id, 'TAT-VEND-01', 'Talent Bridge Vendor', 'Talent Bridge Services', 'Isha Verma', 'vendor.test@frevo.local', '9999900101', 'Percentage', 8.00, 'Active', 4.20, TRUE),
('Consultant', @client_id, 'TAT-CONS-01', 'Senior Hiring Consultant', 'People Search Advisory', 'Karan Mehta', 'consultant.test@frevo.local', '9999900102', 'Fixed', 25000.00, 'Active', 4.50, TRUE)
ON DUPLICATE KEY UPDATE Name=VALUES(Name), Company=VALUES(Company), ContactPerson=VALUES(ContactPerson), Email=VALUES(Email), Phone=VALUES(Phone), Status='Active', IsActive=TRUE;

INSERT INTO recruitment_assignment_rules (ClientId, RuleName, BusinessUnit, Department, PositionCategory, SkillCategory, Project, Location, ExperienceRange, JobLevel, RecruitmentSource, Priority, RecruiterUserId, MaximumOpenPositions, WorkloadBased, ManualOverrideAllowed, SortOrder, IsActive)
SELECT @client_id, 'TAT Engineering Recruiter Rule', 'Digital Delivery', 'Engineering', 'DEV', 'Java/.NET', 'HRMS', 'Noida', '3-6 years', 'L3', '', 'HIGH', @user_recruiter, 10, TRUE, TRUE, 10, TRUE
WHERE NOT EXISTS (SELECT 1 FROM recruitment_assignment_rules WHERE ClientId=@client_id AND RuleName='TAT Engineering Recruiter Rule');

INSERT INTO recruitment_sla_rules (ClientId, ProcessName, DurationDays, ReminderEnabled, ReminderBeforeDays, EscalationEnabled, EscalationAfterDays, NotificationRuleId, IsActive)
SELECT @client_id, 'RFR Approval', 2, TRUE, 1, TRUE, 3, 0, TRUE
WHERE NOT EXISTS (SELECT 1 FROM recruitment_sla_rules WHERE ClientId=@client_id AND ProcessName='RFR Approval');

INSERT INTO recruitment_document_checklist (ClientId, HiringType, DocumentName, Mandatory, Stage, IsActive)
VALUES
(@client_id, 'Permanent', 'Updated resume', TRUE, 'Screening', TRUE),
(@client_id, 'Permanent', 'Experience certificates', TRUE, 'Pre-Onboarding', TRUE),
(@client_id, 'Contract', 'Contractor agreement', TRUE, 'Pre-Onboarding', TRUE)
ON DUPLICATE KEY UPDATE Mandatory=VALUES(Mandatory), Stage=VALUES(Stage), IsActive=TRUE;

INSERT INTO recruitment_templates (ClientId, TemplateType, TemplateCode, TemplateName, SubjectTemplate, BodyTemplate, IsHtml, IsActive)
VALUES
(@client_id, 'Job Description', 'TAT-JD-DEV', 'TAT Developer JD', 'Developer opening - {{positionTitle}}', '<p>Role: {{positionTitle}}</p><p>Skills: {{requiredSkills}}</p>', TRUE, TRUE),
(@client_id, 'Communication', 'TAT-RFR-SUBMIT', 'RFR Submitted Notification', 'RFR {{rfrNumber}} submitted', '<p>RFR {{rfrNumber}} has been submitted for approval.</p>', TRUE, TRUE)
ON DUPLICATE KEY UPDATE TemplateName=VALUES(TemplateName), SubjectTemplate=VALUES(SubjectTemplate), BodyTemplate=VALUES(BodyTemplate), IsActive=TRUE;

INSERT INTO workflowmasters (ClientId, Code, Name, ResourceType, IsActive)
SELECT @client_id, 'TAT_RFR_APPROVAL', 'TAT RFR Approval Workflow', 'RecruitmentRequisition', TRUE
WHERE NOT EXISTS (SELECT 1 FROM workflowmasters WHERE ClientId=@client_id AND Code='TAT_RFR_APPROVAL');
SET @workflow_id := (SELECT MIN(Id) FROM workflowmasters WHERE ClientId=@client_id AND Code='TAT_RFR_APPROVAL');
DELETE FROM workflowstages WHERE WorkflowId IN (SELECT Id FROM workflowmasters WHERE ClientId=@client_id AND Code='TAT_RFR_APPROVAL' AND Id<>@workflow_id);
DELETE FROM workflowmasters WHERE ClientId=@client_id AND Code='TAT_RFR_APPROVAL' AND Id<>@workflow_id;
UPDATE workflowmasters SET Name='TAT RFR Approval Workflow', ResourceType='RecruitmentRequisition', IsActive=TRUE WHERE Id=@workflow_id;

INSERT INTO workflowstages (WorkflowId, StageOrder, Name, ApproverType, ApproverUserId)
VALUES (@workflow_id, 1, 'Manager approval', 'Specific User', @user_approver)
ON DUPLICATE KEY UPDATE Name=VALUES(Name), ApproverType=VALUES(ApproverType), ApproverUserId=VALUES(ApproverUserId);

INSERT INTO recruitment_approval_mappings (ClientId, ProcessCode, WorkflowId, IsActive)
VALUES (@client_id, 'RFR_APPROVAL', @workflow_id, TRUE)
ON DUPLICATE KEY UPDATE WorkflowId=VALUES(WorkflowId), IsActive=TRUE;

INSERT INTO departmentheadassignments (ClientId, Department, UserId)
VALUES (@client_id, 'Engineering', @user_approver)
ON DUPLICATE KEY UPDATE UserId=VALUES(UserId);

COMMIT;
""".Replace("__HASH__", hash, StringComparison.Ordinal);
