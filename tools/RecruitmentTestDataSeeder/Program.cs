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
await using var connection = new MySqlConnection(connectionString);
await connection.OpenAsync();
foreach (var script in new[] { SeedSql(hash), OrchestrationSeedSql() })
{
    foreach (var statement in script.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
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
}

var clientId = await ScalarAsync(connection, "SELECT Id FROM clients WHERE Code='TAT' ORDER BY Id LIMIT 1;");
var employees = await ScalarAsync(connection, $"SELECT COUNT(*) FROM employees WHERE ClientId={clientId} AND EmployeeCode LIKE 'TAT%';");
var users = await ScalarAsync(connection, "SELECT COUNT(*) FROM authusers WHERE Email LIKE 'tat.%@frevo.local';");
var masters = await ScalarAsync(connection, $"SELECT COUNT(*) FROM recruitment_master_values WHERE ClientId={clientId};");
var forms = await ScalarAsync(connection, $"SELECT COUNT(*) FROM form_definitions WHERE ClientId={clientId} AND ModuleCode='RECRUITMENT';");
var pipelines = await ScalarAsync(connection, $"SELECT COUNT(*) FROM recruitment_pipeline_definitions WHERE ClientId={clientId};");
var postings = await ScalarAsync(connection, $"SELECT COUNT(*) FROM recruitment_job_postings WHERE ClientId={clientId};");

Console.WriteLine("Recruitment test data ready.");
Console.WriteLine($"Client: TAT / TA Test Client Pvt Ltd (Id: {clientId})");
Console.WriteLine($"Employees: {employees} | Users: {users} | Recruitment masters: {masters}");
Console.WriteLine($"Dynamic forms: {forms} | Pipelines: {pipelines} | Public postings: {postings}");
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

INSERT INTO recruitment_settings (ClientId, RecruitmentEnabled, AllowEmployeeRfrCreation, AllowReplacementHiring, AllowMultipleHiringManagers, AllowMultipleRecruiters, AutoGeneratePositionCode, AutoGenerateRfrNumber, EnableVendorHiring, EnableConsultantHiring, EnableInternalHiring, EnableReferralHiring, EnableCampusHiring, EnableWalkInHiring, EnableOfferApproval, EnablePreOfferProcess, EnableBackgroundVerification, EnableDocumentVerification, EnableCandidatePortal, PublicPortalBaseUrl, EnableVendorPortal, EnableJobPortalIntegration, IsActive)
VALUES (@client_id, TRUE, TRUE, TRUE, TRUE, TRUE, TRUE, TRUE, TRUE, TRUE, TRUE, TRUE, FALSE, FALSE, TRUE, FALSE, TRUE, TRUE, TRUE, 'http://localhost:5173', FALSE, FALSE, TRUE)
ON DUPLICATE KEY UPDATE RecruitmentEnabled=TRUE, AllowEmployeeRfrCreation=TRUE, EnableVendorHiring=TRUE, EnableConsultantHiring=TRUE, EnableInternalHiring=TRUE, EnableReferralHiring=TRUE, EnableCandidatePortal=TRUE, PublicPortalBaseUrl='http://localhost:5173', IsActive=TRUE;

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
(@client_id, 'Communication', 'TAT-RFR-SUBMIT', 'RFR Submitted Notification', 'RFR {{rfrNumber}} submitted', '<p>RFR {{rfrNumber}} has been submitted for approval.</p>', TRUE, TRUE),
(@client_id, 'Offer Letter', 'TAT-OFFER-LETTER', 'TAT Standard Offer Letter', 'Offer letter - {{positionTitle}}', '<p>Date: {{offerDate}}</p><p>Dear {{candidateName}},</p><p>We are pleased to offer you the position of {{positionTitle}} with {{clientName}}.</p><p>Your annual CTC will be {{currency}} {{formattedCtc}} and your proposed joining date is {{joiningDate}}.</p><p>Offer reference: {{offerNumber}}</p><p>Regards,<br/>Human Resources</p>', TRUE, TRUE)
ON DUPLICATE KEY UPDATE TemplateName=VALUES(TemplateName), SubjectTemplate=VALUES(SubjectTemplate), BodyTemplate=VALUES(BodyTemplate), IsActive=TRUE;
SET @offer_template_id := (SELECT Id FROM recruitment_templates WHERE ClientId=@client_id AND TemplateCode='TAT-OFFER-LETTER' ORDER BY Id LIMIT 1);

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

static string OrchestrationSeedSql() => """
START TRANSACTION;

SET @client_id := (SELECT Id FROM clients WHERE Code='TAT' ORDER BY Id LIMIT 1);
SET @user_requester := (SELECT Id FROM authusers WHERE Email='tat.requester@frevo.local');
SET @user_approver := (SELECT Id FROM authusers WHERE Email='tat.approver@frevo.local');
SET @user_recruiter := (SELECT Id FROM authusers WHERE Email='tat.recruiter@frevo.local');
SET @user_admin := (SELECT Id FROM authusers WHERE Email='tat.admin@frevo.local');
SET @loc_delivery := (SELECT Id FROM worklocations WHERE ClientId=@client_id AND Name='TAT Delivery Center' ORDER BY Id LIMIT 1);

INSERT INTO attachment_attributes
(client_id,attribute_code,attribute_name,description,data_classification,requires_document_number,requires_issue_date,requires_expiry_date,is_active,created_by_user_id)
VALUES
(@client_id,'RESUME','Resume / CV','Resume collected through the public recruitment form and promoted to Candidate 360.','Restricted',FALSE,FALSE,FALSE,TRUE,@user_admin),
(@client_id,'GOVERNMENT_ID','Government identity proof','Government identity proof requested during pre-onboarding.','Restricted',TRUE,FALSE,FALSE,TRUE,@user_admin),
(@client_id,'EDUCATION_CERTIFICATE','Education certificate','Qualification evidence requested during pre-onboarding.','Restricted',FALSE,FALSE,FALSE,TRUE,@user_admin)
ON DUPLICATE KEY UPDATE attribute_name=VALUES(attribute_name),description=VALUES(description),data_classification=VALUES(data_classification),is_active=TRUE;
SET @resume_attribute := (SELECT id FROM attachment_attributes WHERE client_id=@client_id AND attribute_code='RESUME');
SET @identity_attribute := (SELECT id FROM attachment_attributes WHERE client_id=@client_id AND attribute_code='GOVERNMENT_ID');
SET @education_attribute := (SELECT id FROM attachment_attributes WHERE client_id=@client_id AND attribute_code='EDUCATION_CERTIFICATE');

INSERT INTO attachment_field_configurations
(client_id,attachment_attribute_id,module_code,form_code,section_code,field_key,field_label,help_text,is_required,allow_multiple,minimum_file_count,maximum_file_count,allowed_extensions_json,allowed_mime_types_json,maximum_file_size_bytes,maximum_total_size_bytes,owner_can_view,owner_can_upload,owner_can_replace,owner_can_delete,requires_verification,versioning_enabled,requirement_scope,display_order,is_active,created_by_user_id)
VALUES
(@client_id,@resume_attribute,'RECRUITMENT','PUBLIC_CANDIDATE_APPLICATION','APPLICATION','RESUME','Resume / CV','Upload PDF or DOCX. The file is privately stored in the global document system.',TRUE,FALSE,1,1,JSON_ARRAY('pdf','docx'),JSON_ARRAY('application/pdf','application/vnd.openxmlformats-officedocument.wordprocessingml.document'),10485760,10485760,TRUE,TRUE,TRUE,FALSE,FALSE,TRUE,'AllEntities',10,TRUE,@user_admin),
(@client_id,@identity_attribute,'RECRUITMENT','PUBLIC_CANDIDATE_APPLICATION','PRE_ONBOARDING','GOVERNMENT_ID','Government identity proof','Upload one PDF or image. The document becomes part of Candidate 360 after submission.',TRUE,FALSE,1,1,JSON_ARRAY('pdf','jpg','jpeg','png'),JSON_ARRAY('application/pdf','image/jpeg','image/png'),10485760,10485760,TRUE,TRUE,TRUE,FALSE,TRUE,TRUE,'AllEntities',20,TRUE,@user_admin),
(@client_id,@education_attribute,'RECRUITMENT','PUBLIC_CANDIDATE_APPLICATION','PRE_ONBOARDING','EDUCATION_CERTIFICATES','Education certificates','Upload up to five PDF or image files.',TRUE,TRUE,1,5,JSON_ARRAY('pdf','jpg','jpeg','png'),JSON_ARRAY('application/pdf','image/jpeg','image/png'),10485760,26214400,TRUE,TRUE,TRUE,FALSE,TRUE,TRUE,'AllEntities',30,TRUE,@user_admin)
ON DUPLICATE KEY UPDATE attachment_attribute_id=VALUES(attachment_attribute_id),field_label=VALUES(field_label),help_text=VALUES(help_text),is_required=VALUES(is_required),allow_multiple=VALUES(allow_multiple),minimum_file_count=VALUES(minimum_file_count),maximum_file_count=VALUES(maximum_file_count),allowed_extensions_json=VALUES(allowed_extensions_json),allowed_mime_types_json=VALUES(allowed_mime_types_json),maximum_file_size_bytes=VALUES(maximum_file_size_bytes),maximum_total_size_bytes=VALUES(maximum_total_size_bytes),owner_can_view=TRUE,owner_can_upload=TRUE,owner_can_replace=TRUE,requires_verification=VALUES(requires_verification),versioning_enabled=TRUE,is_active=TRUE;
SET @resume_config := (SELECT id FROM attachment_field_configurations WHERE client_id=@client_id AND module_code='RECRUITMENT' AND form_code='PUBLIC_CANDIDATE_APPLICATION' AND field_key='RESUME');
SET @identity_config := (SELECT id FROM attachment_field_configurations WHERE client_id=@client_id AND module_code='RECRUITMENT' AND form_code='PUBLIC_CANDIDATE_APPLICATION' AND field_key='GOVERNMENT_ID');
SET @education_config := (SELECT id FROM attachment_field_configurations WHERE client_id=@client_id AND module_code='RECRUITMENT' AND form_code='PUBLIC_CANDIDATE_APPLICATION' AND field_key='EDUCATION_CERTIFICATES');

INSERT INTO recruitment_requisitions
(RfrNumber,RequestDate,RequestedByEmployeeId,RequestedByUserId,ClientId,BranchId,BusinessUnit,Department,CostCenter,PositionTitle,PositionCategory,EmploymentType,HiringType,NumberOfOpenings,IsReplacement,TargetJoiningDate,JobLocation,WorkMode,Project,BudgetAvailable,BudgetAmount,HiringPriority,BusinessJustification,ReasonForHiring,ExperienceRange,Qualification,RequiredSkills,PreferredSkills,SalaryMin,SalaryMax,Currency,Status,SubmittedAt)
SELECT 'TAT-RFR-ORCH-001',CURRENT_DATE,(SELECT Id FROM employees WHERE ClientId=@client_id AND EmployeeCode='TAT100'),@user_requester,@client_id,@loc_delivery,'Digital Delivery','Engineering','ENG-HRMS','Senior .NET Engineer','DEV','Permanent','New Hire',2,FALSE,DATE_ADD(CURRENT_DATE,INTERVAL 45 DAY),'Noida','Hybrid','HRMS',TRUE,2400000,'High','Approved product roadmap requires two additional engineers.','New approved headcount','4-7 years','B.Tech / MCA','.NET, C#, ASP.NET Core, MySQL, React','Azure, Docker',900000,1400000,'INR','Approved',UTC_TIMESTAMP()
WHERE NOT EXISTS (SELECT 1 FROM recruitment_requisitions WHERE RfrNumber='TAT-RFR-ORCH-001');
SET @rfr_id := (SELECT Id FROM recruitment_requisitions WHERE RfrNumber='TAT-RFR-ORCH-001');

INSERT INTO recruitment_open_positions
(RequisitionId,PositionCode,ClientId,BranchId,BusinessUnit,Department,CostCenter,PositionTitle,PositionCategory,EmploymentType,HiringType,NumberOfPositions,ApprovedPositions,FilledPositions,CancelledPositions,OnHoldPositions,RemainingPositions,TargetJoiningDate,JobLocation,Project,BudgetAvailable,BudgetAmount,SalaryMin,SalaryMax,Currency,HiringPriority,RequiredSkills,PreferredSkills,ExperienceRange,Status,RecruiterUserId,PublishedAt)
SELECT @rfr_id,'TAT-POS-ORCH-001',@client_id,@loc_delivery,'Digital Delivery','Engineering','ENG-HRMS','Senior .NET Engineer','DEV','Permanent','New Hire',2,2,0,0,0,2,DATE_ADD(CURRENT_DATE,INTERVAL 45 DAY),'Noida','HRMS',TRUE,2400000,900000,1400000,'INR','High','.NET, C#, ASP.NET Core, MySQL, React','Azure, Docker','4-7 years','Open',@user_recruiter,UTC_TIMESTAMP()
WHERE NOT EXISTS (SELECT 1 FROM recruitment_open_positions WHERE PositionCode='TAT-POS-ORCH-001');
SET @position_id := (SELECT Id FROM recruitment_open_positions WHERE PositionCode='TAT-POS-ORCH-001');
UPDATE recruitment_requisitions SET OpenPositionId=@position_id,Status='Approved' WHERE Id=@rfr_id;

INSERT INTO recruitment_job_description_versions
(RequisitionId,ClientId,VersionNumber,Title,Summary,RolePurpose,Status,CreatedByUserId,ApprovedByUserId,ApprovedAtUtc)
SELECT @rfr_id,@client_id,1,'Senior .NET Engineer','Build secure and scalable HRMS services and reusable user experiences.','Own backend and frontend delivery for employee-facing HRMS workflows.','Approved',@user_requester,@user_approver,UTC_TIMESTAMP()
WHERE NOT EXISTS (SELECT 1 FROM recruitment_job_description_versions WHERE RequisitionId=@rfr_id AND VersionNumber=1);
SET @jd_id := (SELECT Id FROM recruitment_job_description_versions WHERE RequisitionId=@rfr_id AND VersionNumber=1);
UPDATE recruitment_job_description_versions SET Status='Approved',ApprovedByUserId=@user_approver,ApprovedAtUtc=COALESCE(ApprovedAtUtc,UTC_TIMESTAMP()) WHERE Id=@jd_id;
UPDATE recruitment_open_positions SET ApprovedJobDescriptionVersionId=@jd_id WHERE Id=@position_id;

INSERT INTO recruitment_jd_responsibilities (JobDescriptionVersionId,ResponsibilityText,DisplayOrder)
SELECT @jd_id,'Design maintainable ASP.NET Core APIs and normalized MySQL data models.',10 WHERE NOT EXISTS (SELECT 1 FROM recruitment_jd_responsibilities WHERE JobDescriptionVersionId=@jd_id AND ResponsibilityText='Design maintainable ASP.NET Core APIs and normalized MySQL data models.');
INSERT INTO recruitment_jd_responsibilities (JobDescriptionVersionId,ResponsibilityText,DisplayOrder)
SELECT @jd_id,'Build reusable React and Ant Design workflows with strong validation and accessibility.',20 WHERE NOT EXISTS (SELECT 1 FROM recruitment_jd_responsibilities WHERE JobDescriptionVersionId=@jd_id AND ResponsibilityText='Build reusable React and Ant Design workflows with strong validation and accessibility.');
INSERT INTO recruitment_jd_responsibilities (JobDescriptionVersionId,ResponsibilityText,DisplayOrder)
SELECT @jd_id,'Review code, automate tests and support secure production releases.',30 WHERE NOT EXISTS (SELECT 1 FROM recruitment_jd_responsibilities WHERE JobDescriptionVersionId=@jd_id AND ResponsibilityText='Review code, automate tests and support secure production releases.');
INSERT INTO recruitment_jd_skill_requirements (JobDescriptionVersionId,SkillName,IsRequired,MinimumYears,MinimumProficiency,WeightPercent,DisplayOrder)
SELECT @jd_id,'.NET / C#',TRUE,4,'Advanced',35,10 WHERE NOT EXISTS (SELECT 1 FROM recruitment_jd_skill_requirements WHERE JobDescriptionVersionId=@jd_id AND SkillName='.NET / C#');
INSERT INTO recruitment_jd_skill_requirements (JobDescriptionVersionId,SkillName,IsRequired,MinimumYears,MinimumProficiency,WeightPercent,DisplayOrder)
SELECT @jd_id,'ASP.NET Core',TRUE,3,'Advanced',25,20 WHERE NOT EXISTS (SELECT 1 FROM recruitment_jd_skill_requirements WHERE JobDescriptionVersionId=@jd_id AND SkillName='ASP.NET Core');
INSERT INTO recruitment_jd_skill_requirements (JobDescriptionVersionId,SkillName,IsRequired,MinimumYears,MinimumProficiency,WeightPercent,DisplayOrder)
SELECT @jd_id,'MySQL',TRUE,3,'Intermediate',20,30 WHERE NOT EXISTS (SELECT 1 FROM recruitment_jd_skill_requirements WHERE JobDescriptionVersionId=@jd_id AND SkillName='MySQL');
INSERT INTO recruitment_jd_skill_requirements (JobDescriptionVersionId,SkillName,IsRequired,MinimumYears,MinimumProficiency,WeightPercent,DisplayOrder)
SELECT @jd_id,'React',FALSE,2,'Intermediate',20,40 WHERE NOT EXISTS (SELECT 1 FROM recruitment_jd_skill_requirements WHERE JobDescriptionVersionId=@jd_id AND SkillName='React');
INSERT INTO recruitment_jd_qualification_requirements (JobDescriptionVersionId,QualificationName,Specialization,IsMandatory,DisplayOrder)
SELECT @jd_id,'B.Tech / MCA','Computer Science or equivalent',TRUE,10 WHERE NOT EXISTS (SELECT 1 FROM recruitment_jd_qualification_requirements WHERE JobDescriptionVersionId=@jd_id AND QualificationName='B.Tech / MCA');
INSERT INTO recruitment_jd_benefits (JobDescriptionVersionId,BenefitName,Description,DisplayOrder)
SELECT @jd_id,'Hybrid working','Role supports configured office and remote working days.',10 WHERE NOT EXISTS (SELECT 1 FROM recruitment_jd_benefits WHERE JobDescriptionVersionId=@jd_id AND BenefitName='Hybrid working');

INSERT INTO form_definitions
(ClientId,ModuleCode,FormCode,FormName,PurposeCode,EntityType,Status,CreatedByUserId)
VALUES (@client_id,'RECRUITMENT','TAT_PUBLIC_APPLICATION','TAT Public Job Application','JOB_APPLICATION','CANDIDATE','Active',@user_admin)
ON DUPLICATE KEY UPDATE FormName=VALUES(FormName),PurposeCode=VALUES(PurposeCode),EntityType=VALUES(EntityType),Status='Active';
SET @application_form := (SELECT Id FROM form_definitions WHERE ClientId=@client_id AND ModuleCode='RECRUITMENT' AND FormCode='TAT_PUBLIC_APPLICATION');
INSERT INTO form_versions (FormDefinitionId,VersionNumber,Status,CreatedByUserId,PublishedByUserId,PublishedAtUtc)
VALUES (@application_form,1,'Published',@user_admin,@user_admin,UTC_TIMESTAMP())
ON DUPLICATE KEY UPDATE Status='Published',PublishedByUserId=VALUES(PublishedByUserId),PublishedAtUtc=COALESCE(PublishedAtUtc,VALUES(PublishedAtUtc));
SET @application_form_version := (SELECT Id FROM form_versions WHERE FormDefinitionId=@application_form AND VersionNumber=1);
UPDATE form_definitions SET CurrentPublishedVersionId=@application_form_version WHERE Id=@application_form;
INSERT INTO form_sections (FormVersionId,SectionCode,SectionLabel,Description,DisplayOrder)
VALUES (@application_form_version,'PERSONAL','Personal details','Tell us how HR can contact you.',10)
ON DUPLICATE KEY UPDATE SectionLabel=VALUES(SectionLabel),Description=VALUES(Description),DisplayOrder=VALUES(DisplayOrder);
INSERT INTO form_sections (FormVersionId,SectionCode,SectionLabel,Description,DisplayOrder)
VALUES (@application_form_version,'PROFILE','Professional profile','Add your current profile and resume.',20)
ON DUPLICATE KEY UPDATE SectionLabel=VALUES(SectionLabel),Description=VALUES(Description),DisplayOrder=VALUES(DisplayOrder);
SET @application_personal_section := (SELECT Id FROM form_sections WHERE FormVersionId=@application_form_version AND SectionCode='PERSONAL');
SET @application_profile_section := (SELECT Id FROM form_sections WHERE FormVersionId=@application_form_version AND SectionCode='PROFILE');

INSERT INTO form_fields (FormVersionId,SectionId,FieldTypeId,StableFieldCode,Label,Placeholder,HelpText,IsRequired,DisplayOrder,WidthColumns,MinimumLength,MaximumLength,IsActive)
VALUES
(@application_form_version,@application_personal_section,(SELECT Id FROM form_field_types WHERE TypeCode='TEXT'),'FIRST_NAME','First name','Enter first name','',TRUE,10,12,2,120,TRUE),
(@application_form_version,@application_personal_section,(SELECT Id FROM form_field_types WHERE TypeCode='TEXT'),'LAST_NAME','Last name','Enter last name','',FALSE,20,12,0,120,TRUE),
(@application_form_version,@application_personal_section,(SELECT Id FROM form_field_types WHERE TypeCode='EMAIL'),'EMAIL','Email address','name@example.com','Use the same email used to start the application.',TRUE,30,12,5,190,TRUE),
(@application_form_version,@application_personal_section,(SELECT Id FROM form_field_types WHERE TypeCode='PHONE'),'PHONE','Mobile number','Enter mobile number','',TRUE,40,12,7,15,TRUE),
(@application_form_version,@application_profile_section,(SELECT Id FROM form_field_types WHERE TypeCode='TEXT'),'CURRENT_LOCATION','Current location','City, state','',TRUE,10,12,2,180,TRUE),
(@application_form_version,@application_profile_section,(SELECT Id FROM form_field_types WHERE TypeCode='NUMBER'),'EXPECTED_CTC','Expected annual CTC','','Amount in INR.',FALSE,20,12,NULL,NULL,TRUE),
(@application_form_version,@application_profile_section,(SELECT Id FROM form_field_types WHERE TypeCode='UPLOAD'),'RESUME','Resume / CV','','PDF or DOCX, maximum 10 MB.',TRUE,30,24,NULL,NULL,TRUE),
(@application_form_version,@application_profile_section,(SELECT Id FROM form_field_types WHERE TypeCode='CHECKBOX'),'CONSENT','I consent to recruitment data processing','','Required to submit this application.',TRUE,40,24,NULL,NULL,TRUE)
ON DUPLICATE KEY UPDATE SectionId=VALUES(SectionId),FieldTypeId=VALUES(FieldTypeId),Label=VALUES(Label),Placeholder=VALUES(Placeholder),HelpText=VALUES(HelpText),IsRequired=VALUES(IsRequired),DisplayOrder=VALUES(DisplayOrder),WidthColumns=VALUES(WidthColumns),MinimumLength=VALUES(MinimumLength),MaximumLength=VALUES(MaximumLength),IsActive=TRUE;
UPDATE form_fields SET AttachmentFieldConfigurationId=@resume_config WHERE FormVersionId=@application_form_version AND StableFieldCode='RESUME';
INSERT IGNORE INTO form_field_semantic_mappings (FieldId,SemanticAttributeId)
SELECT f.Id,s.Id FROM form_fields f JOIN form_semantic_attributes s ON s.SemanticCode=f.StableFieldCode
WHERE f.FormVersionId=@application_form_version AND f.StableFieldCode IN ('FIRST_NAME','LAST_NAME','EMAIL','PHONE','CURRENT_LOCATION','EXPECTED_CTC','RESUME','CONSENT');

INSERT INTO form_definitions
(ClientId,ModuleCode,FormCode,FormName,PurposeCode,EntityType,Status,CreatedByUserId)
VALUES (@client_id,'RECRUITMENT','TAT_PRE_ONBOARDING','TAT Pre-Onboarding Documents','PRE_ONBOARDING','CANDIDATE','Active',@user_admin)
ON DUPLICATE KEY UPDATE FormName=VALUES(FormName),PurposeCode=VALUES(PurposeCode),EntityType=VALUES(EntityType),Status='Active';
SET @preboarding_form := (SELECT Id FROM form_definitions WHERE ClientId=@client_id AND ModuleCode='RECRUITMENT' AND FormCode='TAT_PRE_ONBOARDING');
INSERT INTO form_versions (FormDefinitionId,VersionNumber,Status,CreatedByUserId,PublishedByUserId,PublishedAtUtc)
VALUES (@preboarding_form,1,'Published',@user_admin,@user_admin,UTC_TIMESTAMP())
ON DUPLICATE KEY UPDATE Status='Published',PublishedByUserId=VALUES(PublishedByUserId),PublishedAtUtc=COALESCE(PublishedAtUtc,VALUES(PublishedAtUtc));
SET @preboarding_form_version := (SELECT Id FROM form_versions WHERE FormDefinitionId=@preboarding_form AND VersionNumber=1);
UPDATE form_definitions SET CurrentPublishedVersionId=@preboarding_form_version WHERE Id=@preboarding_form;
INSERT INTO form_sections (FormVersionId,SectionCode,SectionLabel,Description,DisplayOrder)
VALUES (@preboarding_form_version,'DOCUMENTS','Pre-onboarding documents','Upload the requested secure documents. HR can verify them from Candidate 360.',10)
ON DUPLICATE KEY UPDATE SectionLabel=VALUES(SectionLabel),Description=VALUES(Description),DisplayOrder=VALUES(DisplayOrder);
SET @preboarding_section := (SELECT Id FROM form_sections WHERE FormVersionId=@preboarding_form_version AND SectionCode='DOCUMENTS');
INSERT INTO form_fields (FormVersionId,SectionId,FieldTypeId,StableFieldCode,Label,Placeholder,HelpText,IsRequired,DisplayOrder,WidthColumns,AttachmentFieldConfigurationId,IsActive)
VALUES
(@preboarding_form_version,@preboarding_section,(SELECT Id FROM form_field_types WHERE TypeCode='UPLOAD'),'GOVERNMENT_ID','Government identity proof','','Enter the document number when uploading.',TRUE,10,24,@identity_config,TRUE),
(@preboarding_form_version,@preboarding_section,(SELECT Id FROM form_field_types WHERE TypeCode='UPLOAD'),'EDUCATION_CERTIFICATES','Education certificates','','Upload one or more qualification documents.',TRUE,20,24,@education_config,TRUE),
(@preboarding_form_version,@preboarding_section,(SELECT Id FROM form_field_types WHERE TypeCode='TEXTAREA'),'CANDIDATE_NOTE','Candidate note','Optional note for HR','Do not enter sensitive bank or password information.',FALSE,30,24,NULL,TRUE)
ON DUPLICATE KEY UPDATE SectionId=VALUES(SectionId),FieldTypeId=VALUES(FieldTypeId),Label=VALUES(Label),HelpText=VALUES(HelpText),IsRequired=VALUES(IsRequired),DisplayOrder=VALUES(DisplayOrder),WidthColumns=VALUES(WidthColumns),AttachmentFieldConfigurationId=VALUES(AttachmentFieldConfigurationId),IsActive=TRUE;

INSERT INTO recruitment_interview_competency_definitions (ClientId,CompetencyCode,CompetencyName,Description,IsActive)
VALUES
(@client_id,'TECH_DEPTH','Technical depth','Practical depth in the role technology stack.',TRUE),
(@client_id,'PROBLEM_SOLVING','Problem solving','Structured analysis and solution quality.',TRUE),
(@client_id,'COMMUNICATION','Communication','Clear and collaborative communication.',TRUE),
(@client_id,'CULTURE_FIT','Values alignment','Alignment with company values and ways of working.',TRUE)
ON DUPLICATE KEY UPDATE CompetencyName=VALUES(CompetencyName),Description=VALUES(Description),IsActive=TRUE;

INSERT INTO recruitment_pipeline_definitions (ClientId,PipelineCode,PipelineName,Description,IsActive,CreatedByUserId)
VALUES (@client_id,'TAT_STANDARD_TECH','TAT Standard Technology Hiring','Reusable ATS, interview, pre-onboarding, offer and joining pipeline.',TRUE,@user_admin)
ON DUPLICATE KEY UPDATE PipelineName=VALUES(PipelineName),Description=VALUES(Description),IsActive=TRUE;
SET @pipeline_definition := (SELECT Id FROM recruitment_pipeline_definitions WHERE ClientId=@client_id AND PipelineCode='TAT_STANDARD_TECH');
INSERT INTO recruitment_pipeline_versions (PipelineDefinitionId,VersionNumber,Status,CreatedByUserId,PublishedByUserId,PublishedAtUtc)
VALUES (@pipeline_definition,1,'Published',@user_admin,@user_admin,UTC_TIMESTAMP())
ON DUPLICATE KEY UPDATE Status='Published',PublishedByUserId=VALUES(PublishedByUserId),PublishedAtUtc=COALESCE(PublishedAtUtc,VALUES(PublishedAtUtc));
SET @pipeline_version := (SELECT Id FROM recruitment_pipeline_versions WHERE PipelineDefinitionId=@pipeline_definition AND VersionNumber=1);
UPDATE recruitment_pipeline_definitions SET CurrentPublishedVersionId=@pipeline_version WHERE Id=@pipeline_definition;

INSERT INTO recruitment_pipeline_stages (PipelineVersionId,StageCode,StageName,StageType,StageNumber,DisplayOrder,SlaDurationMinutes,SlaWarningMinutes,RequiresApproval,CalendarEnabled,AllowSkip,IsInitial,IsTerminal,IsActive)
VALUES
(@pipeline_version,'SCREENING','Application Screening','Screening',1,10,1440,1080,FALSE,FALSE,FALSE,TRUE,FALSE,TRUE),
(@pipeline_version,'ATS','ATS Resume Match','ATS',2,20,240,180,FALSE,FALSE,FALSE,FALSE,FALSE,TRUE),
(@pipeline_version,'TECH_INTERVIEW','Technical Interview','Interview',3,30,2880,2160,FALSE,TRUE,FALSE,FALSE,FALSE,TRUE),
(@pipeline_version,'HR_INTERVIEW','HR Discussion','Interview',4,40,1440,1080,FALSE,TRUE,FALSE,FALSE,FALSE,TRUE),
(@pipeline_version,'PRE_ONBOARDING','Pre-Onboarding Documents','Documents',5,50,4320,2880,FALSE,FALSE,FALSE,FALSE,FALSE,TRUE),
(@pipeline_version,'OFFER','Offer & Candidate Decision','Offer',6,60,2880,2160,FALSE,FALSE,FALSE,FALSE,FALSE,TRUE),
(@pipeline_version,'JOINING','Ready to Join','Joining',7,70,0,0,FALSE,FALSE,FALSE,FALSE,TRUE,TRUE),
(@pipeline_version,'REJECTED','Rejected','Rejected',8,80,0,0,FALSE,FALSE,FALSE,FALSE,TRUE,TRUE)
ON DUPLICATE KEY UPDATE StageName=VALUES(StageName),StageType=VALUES(StageType),StageNumber=VALUES(StageNumber),SlaDurationMinutes=VALUES(SlaDurationMinutes),SlaWarningMinutes=VALUES(SlaWarningMinutes),CalendarEnabled=VALUES(CalendarEnabled),IsInitial=VALUES(IsInitial),IsTerminal=VALUES(IsTerminal),IsActive=TRUE;
SET @stage_screening := (SELECT Id FROM recruitment_pipeline_stages WHERE PipelineVersionId=@pipeline_version AND StageCode='SCREENING');
SET @stage_ats := (SELECT Id FROM recruitment_pipeline_stages WHERE PipelineVersionId=@pipeline_version AND StageCode='ATS');
SET @stage_tech := (SELECT Id FROM recruitment_pipeline_stages WHERE PipelineVersionId=@pipeline_version AND StageCode='TECH_INTERVIEW');
SET @stage_hr := (SELECT Id FROM recruitment_pipeline_stages WHERE PipelineVersionId=@pipeline_version AND StageCode='HR_INTERVIEW');
SET @stage_documents := (SELECT Id FROM recruitment_pipeline_stages WHERE PipelineVersionId=@pipeline_version AND StageCode='PRE_ONBOARDING');
SET @stage_offer := (SELECT Id FROM recruitment_pipeline_stages WHERE PipelineVersionId=@pipeline_version AND StageCode='OFFER');
SET @stage_joining := (SELECT Id FROM recruitment_pipeline_stages WHERE PipelineVersionId=@pipeline_version AND StageCode='JOINING');
SET @stage_rejected := (SELECT Id FROM recruitment_pipeline_stages WHERE PipelineVersionId=@pipeline_version AND StageCode='REJECTED');

INSERT INTO recruitment_stage_ats_configurations
(PipelineStageId,ScoringProfileId,MinimumAdvanceScore,MaximumRejectScore,AutoScoreOnEntry,AutoAdvance,AutoReject,RequireHumanConfirmation,AdvanceOutcomeCode,RejectOutcomeCode)
VALUES (@stage_ats,NULL,65,35,TRUE,FALSE,FALSE,TRUE,'SHORTLIST','REJECT')
ON DUPLICATE KEY UPDATE MinimumAdvanceScore=VALUES(MinimumAdvanceScore),MaximumRejectScore=VALUES(MaximumRejectScore),AutoScoreOnEntry=TRUE,RequireHumanConfirmation=TRUE,AdvanceOutcomeCode='SHORTLIST',RejectOutcomeCode='REJECT';
INSERT INTO recruitment_pipeline_stage_actions (PipelineStageId,TriggerEvent,ActionCode,ExecutionOrder,IsBlocking,IsActive)
VALUES
(@stage_ats,'OnEntry','RUN_ATS_SCORE',10,FALSE,TRUE),
(@stage_documents,'OnEntry','GENERATE_ACTION_LINK',10,FALSE,TRUE),
(@stage_offer,'OnEntry','GENERATE_ACTION_LINK',10,FALSE,TRUE)
ON DUPLICATE KEY UPDATE IsBlocking=VALUES(IsBlocking),IsActive=TRUE;
DELETE FROM recruitment_pipeline_stage_actions
WHERE PipelineStageId IN (@stage_ats,@stage_documents,@stage_offer)
  AND ActionCode IN ('ATS_SCORE','CREATE_CANDIDATE_ACTION');
INSERT INTO recruitment_stage_external_form_configurations
(PipelineStageId,FormVersionId,SubmissionRequired,AllowSaveDraft,ActionTokenValidityMinutes,ActionTokenMaximumUses)
VALUES (@stage_documents,@preboarding_form_version,TRUE,TRUE,10080,100)
ON DUPLICATE KEY UPDATE FormVersionId=VALUES(FormVersionId),SubmissionRequired=TRUE,AllowSaveDraft=TRUE,ActionTokenValidityMinutes=VALUES(ActionTokenValidityMinutes),ActionTokenMaximumUses=VALUES(ActionTokenMaximumUses);
INSERT INTO recruitment_stage_attachment_requirements
(PipelineStageId,AttachmentFieldConfigurationId,IsRequired,MinimumFileCount,MaximumFileCount,RequiresVerification,DisplayOrder)
VALUES
(@stage_documents,@identity_config,TRUE,1,1,TRUE,10),
(@stage_documents,@education_config,TRUE,1,5,TRUE,20)
ON DUPLICATE KEY UPDATE IsRequired=TRUE,MinimumFileCount=VALUES(MinimumFileCount),MaximumFileCount=VALUES(MaximumFileCount),RequiresVerification=VALUES(RequiresVerification),DisplayOrder=VALUES(DisplayOrder);
INSERT INTO recruitment_stage_offer_configurations
(PipelineStageId,OfferTemplateId,ApprovalWorkflowId,BudgetBasis,MaximumVariancePercent,RequireApprovalWhenVarianceExceeded,VarianceApprovalWorkflowId,CandidateResponseValidityDays,RequireAcceptedOfferToAdvance)
VALUES (@stage_offer,@offer_template_id,NULL,'ApprovedMaximum',0,FALSE,NULL,7,TRUE)
ON DUPLICATE KEY UPDATE OfferTemplateId=VALUES(OfferTemplateId),BudgetBasis='ApprovedMaximum',MaximumVariancePercent=0,RequireApprovalWhenVarianceExceeded=FALSE,CandidateResponseValidityDays=7,RequireAcceptedOfferToAdvance=TRUE;

INSERT INTO recruitment_interview_stage_configurations
(PipelineStageId,RoundNumber,InterviewType,DefaultDurationMinutes,MinimumPanelCount,MinimumPassingScore,FeedbackRequired,CalendarEnabled,AllowReschedule)
VALUES
(@stage_tech,1,'Technical',60,2,65,TRUE,TRUE,TRUE),
(@stage_hr,2,'HR',45,1,60,TRUE,TRUE,TRUE)
ON DUPLICATE KEY UPDATE RoundNumber=VALUES(RoundNumber),InterviewType=VALUES(InterviewType),DefaultDurationMinutes=VALUES(DefaultDurationMinutes),MinimumPanelCount=VALUES(MinimumPanelCount),MinimumPassingScore=VALUES(MinimumPassingScore),FeedbackRequired=TRUE,CalendarEnabled=TRUE,AllowReschedule=TRUE;
SET @tech_interview_config := (SELECT Id FROM recruitment_interview_stage_configurations WHERE PipelineStageId=@stage_tech);
SET @hr_interview_config := (SELECT Id FROM recruitment_interview_stage_configurations WHERE PipelineStageId=@stage_hr);
SET @competency_tech := (SELECT Id FROM recruitment_interview_competency_definitions WHERE ClientId=@client_id AND CompetencyCode='TECH_DEPTH');
SET @competency_problem := (SELECT Id FROM recruitment_interview_competency_definitions WHERE ClientId=@client_id AND CompetencyCode='PROBLEM_SOLVING');
SET @competency_communication := (SELECT Id FROM recruitment_interview_competency_definitions WHERE ClientId=@client_id AND CompetencyCode='COMMUNICATION');
SET @competency_culture := (SELECT Id FROM recruitment_interview_competency_definitions WHERE ClientId=@client_id AND CompetencyCode='CULTURE_FIT');
INSERT INTO recruitment_interview_stage_competencies (InterviewStageConfigurationId,CompetencyId,WeightPercent,MinimumScore,DisplayOrder)
VALUES
(@tech_interview_config,@competency_tech,60,65,10),
(@tech_interview_config,@competency_problem,40,60,20),
(@hr_interview_config,@competency_communication,50,60,10),
(@hr_interview_config,@competency_culture,50,60,20)
ON DUPLICATE KEY UPDATE WeightPercent=VALUES(WeightPercent),MinimumScore=VALUES(MinimumScore),DisplayOrder=VALUES(DisplayOrder);

INSERT INTO recruitment_pipeline_transitions (PipelineVersionId,FromStageId,ToStageId,OutcomeCode,ActionLabel,RequiresReason,IsActive,DisplayOrder)
VALUES
(@pipeline_version,@stage_screening,@stage_ats,'ADVANCE','Send to ATS',FALSE,TRUE,10),
(@pipeline_version,@stage_ats,@stage_tech,'SHORTLIST','Shortlist for technical interview',FALSE,TRUE,20),
(@pipeline_version,@stage_ats,@stage_rejected,'REJECT','Reject after ATS review',TRUE,TRUE,30),
(@pipeline_version,@stage_tech,@stage_hr,'PASS','Pass technical round',FALSE,TRUE,40),
(@pipeline_version,@stage_tech,@stage_rejected,'REJECT','Reject after technical round',TRUE,TRUE,50),
(@pipeline_version,@stage_hr,@stage_documents,'PASS','Start pre-onboarding',FALSE,TRUE,60),
(@pipeline_version,@stage_hr,@stage_rejected,'REJECT','Reject after HR round',TRUE,TRUE,70),
(@pipeline_version,@stage_documents,@stage_offer,'DOCUMENTS_COMPLETED','Proceed to offer',FALSE,TRUE,80),
(@pipeline_version,@stage_offer,@stage_joining,'ACCEPTED','Mark ready to join',FALSE,TRUE,90)
ON DUPLICATE KEY UPDATE ActionLabel=VALUES(ActionLabel),RequiresReason=VALUES(RequiresReason),IsActive=TRUE,DisplayOrder=VALUES(DisplayOrder);

UPDATE recruitment_job_postings
SET PublicSlug='8f0c4b1a7e2d9c3f6a5b4d8e1f2a3c7b'
WHERE ClientId=@client_id AND PublicSlug='tat-senior-dotnet-engineer';

INSERT INTO recruitment_job_postings
(ClientId,PositionId,JobDescriptionVersionId,ApplicationFormVersionId,PublicSlug,PublicTitle,Status,OpensAtUtc,ClosesAtUtc,MaximumApplications,ApplicationCount,SearchEngineVisible,CreatedByUserId,PublishedAtUtc)
SELECT @client_id,@position_id,@jd_id,@application_form_version,'8f0c4b1a7e2d9c3f6a5b4d8e1f2a3c7b','Senior .NET Engineer','Published',UTC_TIMESTAMP(),DATE_ADD(UTC_TIMESTAMP(),INTERVAL 90 DAY),250,0,FALSE,@user_recruiter,UTC_TIMESTAMP()
WHERE NOT EXISTS (SELECT 1 FROM recruitment_job_postings WHERE ClientId=@client_id AND PublicSlug='8f0c4b1a7e2d9c3f6a5b4d8e1f2a3c7b');
SET @posting_id := (SELECT Id FROM recruitment_job_postings WHERE ClientId=@client_id AND PublicSlug='8f0c4b1a7e2d9c3f6a5b4d8e1f2a3c7b');
UPDATE recruitment_job_postings SET JobDescriptionVersionId=@jd_id,ApplicationFormVersionId=@application_form_version,Status='Published',ClosesAtUtc=DATE_ADD(UTC_TIMESTAMP(),INTERVAL 90 DAY) WHERE Id=@posting_id;
UPDATE recruitment_position_pipeline_assignments SET IsActive=FALSE WHERE PositionId=@position_id AND IsActive=TRUE;
INSERT INTO recruitment_position_pipeline_assignments (PositionId,JobPostingId,PipelineVersionId,IsActive,AssignedByUserId,AssignedAtUtc)
SELECT @position_id,@posting_id,@pipeline_version,TRUE,@user_admin,UTC_TIMESTAMP()
WHERE NOT EXISTS (SELECT 1 FROM recruitment_position_pipeline_assignments WHERE PositionId=@position_id AND JobPostingId=@posting_id AND PipelineVersionId=@pipeline_version AND IsActive=TRUE);

COMMIT;
""";
