using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using System.Text.Json;

namespace Payroll.API.Repositories;

public class RecruitmentAdminRepository(IConfiguration configuration)
{
    private MySqlConnection Connection() => new(configuration.GetConnectionString("Default"));

    public async Task InitializeAsync()
    {
        await using var db = Connection();
        await db.OpenAsync();
        await EnsureTablesAsync(db);
        await SeedMastersAsync(db);
    }

    public async Task<RecruitmentAdminSetup> GetAsync()
    {
        await using var db = Connection();
        await db.OpenAsync();
        await EnsureTablesAsync(db);
        return new RecruitmentAdminSetup
        {
            Settings = await db.QueryAsync<RecruitmentSetting>(@"SELECT s.*,COALESCE(c.Name,'') ClientName FROM recruitment_settings s LEFT JOIN clients c ON c.Id=s.ClientId ORDER BY c.Name,s.Id"),
            Masters = await db.QueryAsync<RecruitmentMasterValue>(@"SELECT m.*,COALESCE(c.Name,'Global') ClientName FROM recruitment_master_values m LEFT JOIN clients c ON c.Id=m.ClientId ORDER BY m.MasterType,m.SortOrder,m.Name"),
            Consultants = await GetPartnersAsync(db, "Consultant"),
            Vendors = await GetPartnersAsync(db, "Vendor"),
            AssignmentRules = await db.QueryAsync<RecruitmentAssignmentRule>(@"SELECT r.*,COALESCE(c.Name,'') ClientName,COALESCE(u.DisplayName,u.Email,'') RecruiterName FROM recruitment_assignment_rules r LEFT JOIN clients c ON c.Id=r.ClientId LEFT JOIN authusers u ON u.Id=r.RecruiterUserId ORDER BY c.Name,r.SortOrder,r.RuleName"),
            SlaRules = await db.QueryAsync<RecruitmentSlaRule>(@"SELECT s.*,COALESCE(c.Name,'') ClientName FROM recruitment_sla_rules s LEFT JOIN clients c ON c.Id=s.ClientId ORDER BY c.Name,s.ProcessName"),
            DocumentChecklist = await db.QueryAsync<RecruitmentDocumentChecklist>(@"SELECT d.*,COALESCE(c.Name,'') ClientName FROM recruitment_document_checklist d LEFT JOIN clients c ON c.Id=d.ClientId ORDER BY c.Name,d.HiringType,d.DocumentName"),
            ApprovalMappings = await db.QueryAsync<RecruitmentApprovalMapping>(@"SELECT a.*,COALESCE(c.Name,'') ClientName,COALESCE(w.Name,'') WorkflowName FROM recruitment_approval_mappings a LEFT JOIN clients c ON c.Id=a.ClientId LEFT JOIN workflowmasters w ON w.Id=a.WorkflowId ORDER BY c.Name,a.ProcessCode"),
            Templates = await db.QueryAsync<RecruitmentTemplate>(@"SELECT t.*,COALESCE(c.Name,'') ClientName FROM recruitment_templates t LEFT JOIN clients c ON c.Id=t.ClientId ORDER BY c.Name,t.TemplateType,t.TemplateName")
        };
    }

    public async Task<RecruitmentSetting> SaveSettingAsync(RecruitmentSetting row, int userId)
    {
        await using var db = Connection(); await db.OpenAsync(); await EnsureTablesAsync(db);
        var id = await db.ExecuteScalarAsync<int>(@"INSERT INTO recruitment_settings (Id,ClientId,RecruitmentEnabled,AllowEmployeeRfrCreation,AllowReplacementHiring,AllowMultipleHiringManagers,AllowMultipleRecruiters,AutoGeneratePositionCode,AutoGenerateRfrNumber,EnableVendorHiring,EnableConsultantHiring,EnableInternalHiring,EnableReferralHiring,EnableCampusHiring,EnableWalkInHiring,EnableOfferApproval,EnablePreOfferProcess,EnableBackgroundVerification,EnableDocumentVerification,EnableCandidatePortal,EnableVendorPortal,EnableJobPortalIntegration,IsActive)
VALUES (@Id,@ClientId,@RecruitmentEnabled,@AllowEmployeeRfrCreation,@AllowReplacementHiring,@AllowMultipleHiringManagers,@AllowMultipleRecruiters,@AutoGeneratePositionCode,@AutoGenerateRfrNumber,@EnableVendorHiring,@EnableConsultantHiring,@EnableInternalHiring,@EnableReferralHiring,@EnableCampusHiring,@EnableWalkInHiring,@EnableOfferApproval,@EnablePreOfferProcess,@EnableBackgroundVerification,@EnableDocumentVerification,@EnableCandidatePortal,@EnableVendorPortal,@EnableJobPortalIntegration,@IsActive)
ON DUPLICATE KEY UPDATE Id=LAST_INSERT_ID(Id),RecruitmentEnabled=VALUES(RecruitmentEnabled),AllowEmployeeRfrCreation=VALUES(AllowEmployeeRfrCreation),AllowReplacementHiring=VALUES(AllowReplacementHiring),AllowMultipleHiringManagers=VALUES(AllowMultipleHiringManagers),AllowMultipleRecruiters=VALUES(AllowMultipleRecruiters),AutoGeneratePositionCode=VALUES(AutoGeneratePositionCode),AutoGenerateRfrNumber=VALUES(AutoGenerateRfrNumber),EnableVendorHiring=VALUES(EnableVendorHiring),EnableConsultantHiring=VALUES(EnableConsultantHiring),EnableInternalHiring=VALUES(EnableInternalHiring),EnableReferralHiring=VALUES(EnableReferralHiring),EnableCampusHiring=VALUES(EnableCampusHiring),EnableWalkInHiring=VALUES(EnableWalkInHiring),EnableOfferApproval=VALUES(EnableOfferApproval),EnablePreOfferProcess=VALUES(EnablePreOfferProcess),EnableBackgroundVerification=VALUES(EnableBackgroundVerification),EnableDocumentVerification=VALUES(EnableDocumentVerification),EnableCandidatePortal=VALUES(EnableCandidatePortal),EnableVendorPortal=VALUES(EnableVendorPortal),EnableJobPortalIntegration=VALUES(EnableJobPortalIntegration),IsActive=VALUES(IsActive),UpdatedAt=CURRENT_TIMESTAMP;
SELECT LAST_INSERT_ID();", row);
        await AuditAsync(db, "RecruitmentSetting", id, "Save", row, userId);
        return (await db.QueryFirstAsync<RecruitmentSetting>("SELECT s.*,COALESCE(c.Name,'') ClientName FROM recruitment_settings s LEFT JOIN clients c ON c.Id=s.ClientId WHERE s.Id=@Id", new { Id = id }));
    }

    public Task<RecruitmentMasterValue> SaveMasterAsync(RecruitmentMasterValue row, int userId) => SaveAsync<RecruitmentMasterValue>("recruitment_master_values", row, userId, "RecruitmentMaster");
    public Task<RecruitmentPartner> SavePartnerAsync(RecruitmentPartner row, int userId) => SaveAsync<RecruitmentPartner>("recruitment_partners", row, userId, "RecruitmentPartner");
    public Task<RecruitmentAssignmentRule> SaveAssignmentRuleAsync(RecruitmentAssignmentRule row, int userId) => SaveAsync<RecruitmentAssignmentRule>("recruitment_assignment_rules", row, userId, "RecruitmentAssignmentRule");
    public Task<RecruitmentSlaRule> SaveSlaRuleAsync(RecruitmentSlaRule row, int userId) => SaveAsync<RecruitmentSlaRule>("recruitment_sla_rules", row, userId, "RecruitmentSlaRule");
    public Task<RecruitmentDocumentChecklist> SaveDocumentChecklistAsync(RecruitmentDocumentChecklist row, int userId) => SaveAsync<RecruitmentDocumentChecklist>("recruitment_document_checklist", row, userId, "RecruitmentDocumentChecklist");
    public Task<RecruitmentApprovalMapping> SaveApprovalMappingAsync(RecruitmentApprovalMapping row, int userId) => SaveAsync<RecruitmentApprovalMapping>("recruitment_approval_mappings", row, userId, "RecruitmentApprovalMapping");
    public Task<RecruitmentTemplate> SaveTemplateAsync(RecruitmentTemplate row, int userId) => SaveAsync<RecruitmentTemplate>("recruitment_templates", row, userId, "RecruitmentTemplate");

    private async Task<T> SaveAsync<T>(string table, T row, int userId, string auditEntity) where T : class
    {
        await using var db = Connection(); await db.OpenAsync(); await EnsureTablesAsync(db);
        var props = typeof(T).GetProperties().Where(p => p.Name is not ("ClientName" or "RecruiterName" or "WorkflowName")).ToList();
        var cols = props.Select(p => p.Name).ToList();
        var update = string.Join(",", cols.Where(c => c != "Id").Select(c => $"{c}=VALUES({c})"));
        var sql = $@"INSERT INTO {table} ({string.Join(",", cols)}) VALUES ({string.Join(",", cols.Select(c => "@" + c))})
ON DUPLICATE KEY UPDATE Id=LAST_INSERT_ID(Id),{update},UpdatedAt=CURRENT_TIMESTAMP;
SELECT LAST_INSERT_ID();";
        var id = await db.ExecuteScalarAsync<int>(sql, row);
        await AuditAsync(db, auditEntity, id, "Save", row, userId);
        var saved = await db.QueryFirstAsync<T>($"SELECT * FROM {table} WHERE Id=@Id", new { Id = id });
        return saved;
    }

    private static async Task<IEnumerable<RecruitmentPartner>> GetPartnersAsync(MySqlConnection db, string type) =>
        await db.QueryAsync<RecruitmentPartner>(@"SELECT p.*,COALESCE(c.Name,'') ClientName FROM recruitment_partners p LEFT JOIN clients c ON c.Id=p.ClientId WHERE p.PartnerType=@Type ORDER BY c.Name,p.Name", new { Type = type });

    private static Task AuditAsync(MySqlConnection db, string entity, int id, string action, object value, int userId) =>
        db.ExecuteAsync("INSERT INTO recruitment_admin_audit (EntityType,EntityId,Action,NewValueJson,ChangedByUserId) VALUES (@Entity,@Id,@Action,@Json,@UserId)", new { Entity = entity, Id = id, Action = action, Json = JsonSerializer.Serialize(value), UserId = userId });

    private static async Task EnsureTablesAsync(MySqlConnection db)
    {
        await db.ExecuteAsync(@"CREATE TABLE IF NOT EXISTS recruitment_settings (
Id INT PRIMARY KEY AUTO_INCREMENT,ClientId INT NOT NULL,RecruitmentEnabled BOOLEAN NOT NULL DEFAULT FALSE,AllowEmployeeRfrCreation BOOLEAN NOT NULL DEFAULT FALSE,AllowReplacementHiring BOOLEAN NOT NULL DEFAULT TRUE,AllowMultipleHiringManagers BOOLEAN NOT NULL DEFAULT FALSE,AllowMultipleRecruiters BOOLEAN NOT NULL DEFAULT FALSE,AutoGeneratePositionCode BOOLEAN NOT NULL DEFAULT TRUE,AutoGenerateRfrNumber BOOLEAN NOT NULL DEFAULT TRUE,EnableVendorHiring BOOLEAN NOT NULL DEFAULT FALSE,EnableConsultantHiring BOOLEAN NOT NULL DEFAULT FALSE,EnableInternalHiring BOOLEAN NOT NULL DEFAULT TRUE,EnableReferralHiring BOOLEAN NOT NULL DEFAULT TRUE,EnableCampusHiring BOOLEAN NOT NULL DEFAULT FALSE,EnableWalkInHiring BOOLEAN NOT NULL DEFAULT FALSE,EnableOfferApproval BOOLEAN NOT NULL DEFAULT TRUE,EnablePreOfferProcess BOOLEAN NOT NULL DEFAULT FALSE,EnableBackgroundVerification BOOLEAN NOT NULL DEFAULT FALSE,EnableDocumentVerification BOOLEAN NOT NULL DEFAULT FALSE,EnableCandidatePortal BOOLEAN NOT NULL DEFAULT FALSE,EnableVendorPortal BOOLEAN NOT NULL DEFAULT FALSE,EnableJobPortalIntegration BOOLEAN NOT NULL DEFAULT FALSE,IsActive BOOLEAN NOT NULL DEFAULT TRUE,CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,UNIQUE KEY UX_recruitment_settings_client (ClientId));
CREATE TABLE IF NOT EXISTS recruitment_master_values (Id INT PRIMARY KEY AUTO_INCREMENT,ClientId INT NOT NULL DEFAULT 0,MasterType VARCHAR(80) NOT NULL,Code VARCHAR(80) NOT NULL,Name VARCHAR(180) NOT NULL,Description VARCHAR(500) NOT NULL DEFAULT '',SortOrder INT NOT NULL DEFAULT 100,IsSystem BOOLEAN NOT NULL DEFAULT FALSE,IsActive BOOLEAN NOT NULL DEFAULT TRUE,CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,UNIQUE KEY UX_recruitment_master (ClientId,MasterType,Code));
CREATE TABLE IF NOT EXISTS recruitment_partners (Id INT PRIMARY KEY AUTO_INCREMENT,PartnerType VARCHAR(40) NOT NULL,ClientId INT NOT NULL,Code VARCHAR(80) NOT NULL,Name VARCHAR(180) NOT NULL,Company VARCHAR(180) NOT NULL DEFAULT '',ContactPerson VARCHAR(160) NOT NULL DEFAULT '',Email VARCHAR(160) NOT NULL DEFAULT '',Phone VARCHAR(40) NOT NULL DEFAULT '',Address VARCHAR(500) NOT NULL DEFAULT '',Gstin VARCHAR(40) NOT NULL DEFAULT '',Pan VARCHAR(20) NOT NULL DEFAULT '',AgreementStartDate DATE NULL,AgreementEndDate DATE NULL,CommissionType VARCHAR(40) NOT NULL DEFAULT 'Percentage',CommissionValue DECIMAL(18,2) NOT NULL DEFAULT 0,Status VARCHAR(40) NOT NULL DEFAULT 'Active',PerformanceRating DECIMAL(5,2) NOT NULL DEFAULT 0,IsActive BOOLEAN NOT NULL DEFAULT TRUE,CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,UNIQUE KEY UX_recruitment_partner (PartnerType,ClientId,Code));
CREATE TABLE IF NOT EXISTS recruitment_assignment_rules (Id INT PRIMARY KEY AUTO_INCREMENT,ClientId INT NOT NULL,RuleName VARCHAR(180) NOT NULL,BusinessUnit VARCHAR(120) NOT NULL DEFAULT '',Department VARCHAR(120) NOT NULL DEFAULT '',PositionCategory VARCHAR(120) NOT NULL DEFAULT '',SkillCategory VARCHAR(120) NOT NULL DEFAULT '',Project VARCHAR(120) NOT NULL DEFAULT '',Location VARCHAR(120) NOT NULL DEFAULT '',ExperienceRange VARCHAR(80) NOT NULL DEFAULT '',JobLevel VARCHAR(80) NOT NULL DEFAULT '',RecruitmentSource VARCHAR(120) NOT NULL DEFAULT '',Priority VARCHAR(40) NOT NULL DEFAULT '',RecruiterUserId INT NOT NULL DEFAULT 0,MaximumOpenPositions INT NOT NULL DEFAULT 0,WorkloadBased BOOLEAN NOT NULL DEFAULT FALSE,ManualOverrideAllowed BOOLEAN NOT NULL DEFAULT TRUE,SortOrder INT NOT NULL DEFAULT 100,IsActive BOOLEAN NOT NULL DEFAULT TRUE,CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP);
CREATE TABLE IF NOT EXISTS recruitment_sla_rules (Id INT PRIMARY KEY AUTO_INCREMENT,ClientId INT NOT NULL,ProcessName VARCHAR(120) NOT NULL,DurationDays INT NOT NULL DEFAULT 0,ReminderEnabled BOOLEAN NOT NULL DEFAULT TRUE,ReminderBeforeDays INT NOT NULL DEFAULT 1,EscalationEnabled BOOLEAN NOT NULL DEFAULT FALSE,EscalationAfterDays INT NOT NULL DEFAULT 0,NotificationRuleId INT NOT NULL DEFAULT 0,IsActive BOOLEAN NOT NULL DEFAULT TRUE,CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP);
CREATE TABLE IF NOT EXISTS recruitment_document_checklist (Id INT PRIMARY KEY AUTO_INCREMENT,ClientId INT NOT NULL,HiringType VARCHAR(120) NOT NULL,DocumentName VARCHAR(180) NOT NULL,Mandatory BOOLEAN NOT NULL DEFAULT TRUE,Stage VARCHAR(120) NOT NULL DEFAULT 'Pre-Onboarding',IsActive BOOLEAN NOT NULL DEFAULT TRUE,CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,UNIQUE KEY UX_recruitment_doc (ClientId,HiringType,DocumentName));
CREATE TABLE IF NOT EXISTS recruitment_approval_mappings (Id INT PRIMARY KEY AUTO_INCREMENT,ClientId INT NOT NULL,ProcessCode VARCHAR(80) NOT NULL,WorkflowId BIGINT NOT NULL DEFAULT 0,IsActive BOOLEAN NOT NULL DEFAULT TRUE,CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,UNIQUE KEY UX_recruitment_approval (ClientId,ProcessCode));
CREATE TABLE IF NOT EXISTS recruitment_templates (Id INT PRIMARY KEY AUTO_INCREMENT,ClientId INT NOT NULL,TemplateType VARCHAR(80) NOT NULL,TemplateCode VARCHAR(80) NOT NULL,TemplateName VARCHAR(180) NOT NULL,SubjectTemplate VARCHAR(300) NOT NULL DEFAULT '',BodyTemplate TEXT NULL,IsHtml BOOLEAN NOT NULL DEFAULT TRUE,IsActive BOOLEAN NOT NULL DEFAULT TRUE,CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,UNIQUE KEY UX_recruitment_template (ClientId,TemplateCode));
CREATE TABLE IF NOT EXISTS recruitment_admin_audit (Id BIGINT PRIMARY KEY AUTO_INCREMENT,EntityType VARCHAR(80) NOT NULL,EntityId INT NOT NULL,Action VARCHAR(80) NOT NULL,OldValueJson JSON NULL,NewValueJson JSON NULL,ChangedByUserId INT NULL,ChangedOn DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,INDEX IX_recruitment_audit (EntityType,EntityId,ChangedOn));");
    }

    private static async Task SeedMastersAsync(MySqlConnection db)
    {
        await db.ExecuteAsync(@"CREATE TABLE IF NOT EXISTS dropdownmasters (
Id INT PRIMARY KEY AUTO_INCREMENT,
ClientId INT NOT NULL DEFAULT 0,
Type VARCHAR(100) NOT NULL,
Value VARCHAR(255) NOT NULL,
ConfigJson JSON NULL,
IsActive BOOLEAN NOT NULL DEFAULT TRUE,
UNIQUE KEY UX_DropdownMasters_Client_Type_Value (ClientId, Type, Value)
);");

        var seed = new Dictionary<string, string[]>
        {
            ["Recruitment Status"] = ["Draft","Pending Approval","Approved","Sent Back","Withdrawn","Rejected","Closed","Cancelled"],
            ["Position Status"] = ["Open","Recruiter Assigned","Published","Candidate Screening","Interview In Progress","Offer Released","Offer Accepted","Joining Pending","Filled","Partially Filled","Cancelled","Closed","On Hold"],
            ["Recruitment Checklist"] = ["Job Description Attached","Budget Approved","Replacement Approved","Organization Structure Updated","Client Approval","Salary Approval","Business Case Attached"],
            ["Publishing Channel"] = ["Career Portal","Company Website","LinkedIn","Naukri","Indeed","Monster","Internal Job Portal","Employee Referral","Walk-In","Campus","Social Media"],
            ["Assignment Priority"] = ["Low","Normal","High","Critical"],
            ["Recruitment Source"] = ["Company Career Portal","Employee Referral","Consultant","Vendor","Naukri","LinkedIn","Indeed","Walk-In","Campus","Internal Transfer","Social Media","Job Fair","Email","Recruitment Drive"],
            ["Hiring Type"] = ["Permanent","Contract","Intern","Consultant","Temporary","Apprentice","Freelancer","Fixed Term"],
            ["Position Category"] = ["IT","Engineering","Finance","HR","Sales","Procurement","Operations","Legal","Administration","Marketing"],
            ["Experience Range"] = ["0-1 years","1-3 years","3-5 years","5-8 years","8-12 years","12+ years"],
            ["Budget Amount"] = ["300000","500000","750000","1000000","1500000","2000000","2500000","3000000"],
            ["Interview Type"] = ["HR","Technical","Managerial","Client","Panel","Final","Virtual","Face-to-Face","Telephonic","Group Discussion","Assessment","Coding Test"],
            ["Interview Result"] = ["Selected","Rejected","On Hold","No Show","Reschedule","Feedback Pending"],
            ["Interview Round"] = ["Round 1","Round 2","Round 3","HR","Management","Client"],
            ["Candidate Status"] = ["New","Screening","Shortlisted","Interview Scheduled","Interview Completed","Selected","Rejected","Offer Released","Offer Accepted","Offer Rejected","Joined","On Hold"],
            ["Offer Status"] = ["Draft","Internal Approval","Pending Candidate","Negotiation","Accepted","Rejected","Expired","Withdrawn"]
        };
        foreach (var (type, values) in seed)
            for (var i = 0; i < values.Length; i++)
            {
                await db.ExecuteAsync(@"INSERT INTO recruitment_master_values (ClientId,MasterType,Code,Name,SortOrder,IsSystem,IsActive) VALUES (0,@Type,@Code,@Name,@SortOrder,TRUE,TRUE)
ON DUPLICATE KEY UPDATE Name=VALUES(Name),SortOrder=VALUES(SortOrder),IsSystem=TRUE,IsActive=TRUE", new { Type = type, Code = values[i].ToUpperInvariant().Replace(" ", "_").Replace("-", "_"), Name = values[i], SortOrder = (i + 1) * 10 });
                if (CentralDropdownTypes.Contains(type))
                    await db.ExecuteAsync(@"INSERT INTO dropdownmasters (ClientId,Type,Value,ConfigJson,IsActive)
VALUES (0,@Type,@Value,NULL,TRUE)
ON DUPLICATE KEY UPDATE IsActive=VALUES(IsActive)", new { Type = type, Value = values[i] });
            }
    }

    private static readonly HashSet<string> CentralDropdownTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Recruitment Status",
        "Position Status",
        "Publishing Channel",
        "Assignment Priority",
        "Recruitment Source",
        "Hiring Type",
        "Position Category",
        "Experience Range",
        "Budget Amount",
        "Interview Type",
        "Interview Result",
        "Interview Round",
        "Candidate Status",
        "Offer Status"
    };
}
