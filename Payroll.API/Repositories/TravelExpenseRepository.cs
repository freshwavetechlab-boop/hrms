using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using System.Text.Json;

namespace Payroll.API.Repositories;

public class TravelExpenseRepository(IConfiguration configuration)
{
    private static readonly string[] PolicyStatuses = ["Draft", "Active", "Inactive"];
    private static readonly string[] ExceptionModes = ["Warning", "Block", "Approval Override"];
    private static readonly string[] RuleTypes = ["Travel Mode", "Hotel", "Meal", "Per Diem", "Local Conveyance", "Travel Advance", "Policy Violation"];
    private MySqlConnection Connection() => new(configuration.GetConnectionString("Default"));

    public async Task InitializeAsync()
    {
        await using var db = Connection(); await db.OpenAsync(); await EnsureTablesAsync(db); await SeedSampleDataAsync(db);
    }

    public async Task<TravelExpenseSetup> GetAsync()
    {
        await using var db = Connection(); await db.OpenAsync(); await EnsureTablesAsync(db); await SeedSampleDataAsync(db);
        return new TravelExpenseSetup
        {
            ClientSettings = await db.QueryAsync<TravelExpenseClientSetting>(@"SELECT s.*, COALESCE(c.Name,'') ClientName
FROM travel_expense_client_settings s
JOIN clients c ON c.Id=s.ClientId
ORDER BY c.Name"),
            Policies = await db.QueryAsync<TravelPolicy>(@"SELECT p.*, COALESCE(c.Name,'') CompanyName FROM travel_policies p LEFT JOIN clients c ON c.Id=p.CompanyId ORDER BY p.CompanyId, p.EffectiveFrom DESC, p.PolicyName"),
            Assignments = await db.QueryAsync<TravelPolicyAssignment>(@"SELECT a.*, p.PolicyName, COALESCE(c.Name,'') CompanyName, COALESCE(w.Name,'') BranchName, COALESCE(CONCAT(e.FirstName,' ',e.LastName,' / ',e.EmployeeCode),'') EmployeeName
FROM travel_policy_assignments a
JOIN travel_policies p ON p.Id=a.PolicyId
LEFT JOIN clients c ON c.Id=a.CompanyId
LEFT JOIN worklocations w ON w.Id=a.BranchId
LEFT JOIN employees e ON e.Id=a.EmployeeId
ORDER BY a.Priority, p.PolicyName, a.Id DESC"),
            Rules = await db.QueryAsync<TravelPolicyRule>(@"SELECT r.*, p.PolicyName, COALESCE(w.Name,'') WorkflowName FROM travel_policy_rules r JOIN travel_policies p ON p.Id=r.PolicyId LEFT JOIN workflowmasters w ON w.Id=r.WorkflowId ORDER BY p.PolicyName, r.RuleType, r.RuleName"),
            Categories = await db.QueryAsync<TravelExpenseCategory>(@"SELECT h.Id,hs.ClientId,COALESCE(client.Name,'') ClientName,NULL ParentId,'' ParentName,h.HeaderName ExpenseType,TRUE IsClaimHeader,h.HeaderCode CategoryCode,h.HeaderName CategoryName,
FALSE ReceiptMandatory,FALSE GstApplicable,NULL DailyLimit,NULL MaximumClaim,FALSE RequiresFinanceApproval,FALSE RequiresManagerApproval,hs.IsEnabled IsActive,h.CreatedAt,h.UpdatedAt
FROM expense_headers h
JOIN client_expense_header_settings hs ON hs.HeaderId=h.Id
JOIN clients client ON client.Id=hs.ClientId
UNION ALL
SELECT c.Id,cs.ClientId,COALESCE(client.Name,'') ClientName,h.Id ParentId,h.HeaderName ParentName,h.HeaderName ExpenseType,FALSE IsClaimHeader,c.CategoryCode,c.CategoryName,
cs.ReceiptMandatory,cs.GstApplicable,cs.DailyLimit,cs.MaximumClaim,cs.RequiresFinanceApproval,cs.RequiresManagerApproval,cs.IsEnabled IsActive,c.CreatedAt,c.UpdatedAt
FROM expense_categories c
JOIN expense_headers h ON h.Id=c.HeaderId
JOIN client_expense_category_settings cs ON cs.CategoryId=c.Id
JOIN clients client ON client.Id=cs.ClientId
ORDER BY ClientId, ExpenseType, IsClaimHeader DESC, CategoryName"),
            Audit = await db.QueryAsync<TravelPolicyAudit>("SELECT * FROM travel_policy_audit ORDER BY ChangedOn DESC, Id DESC LIMIT 250")
        };
    }

    public async Task<TravelExpenseClientSetting> SaveClientSettingAsync(TravelExpenseClientSetting row, string changedBy)
    {
        if (row.ClientId <= 0) throw new InvalidOperationException("Select a client.");
        if (row.EffectiveTo.HasValue && row.EffectiveFrom.HasValue && row.EffectiveTo.Value.Date < row.EffectiveFrom.Value.Date)
            throw new InvalidOperationException("Effective to cannot be before effective from.");
        await using var db = Connection(); await db.OpenAsync(); await EnsureTablesAsync(db);
        if (await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM clients WHERE Id=@ClientId AND IsActive=TRUE", row) == 0)
            throw new InvalidOperationException("Select an active client.");
        var old = row.Id > 0 ? await db.QueryFirstOrDefaultAsync<TravelExpenseClientSetting>("SELECT * FROM travel_expense_client_settings WHERE Id=@Id", row) : null;
        var id = await db.ExecuteScalarAsync<long>(@"INSERT INTO travel_expense_client_settings (Id,ClientId,IsEnabled,EffectiveFrom,EffectiveTo,Remarks)
VALUES (@Id,@ClientId,@IsEnabled,@EffectiveFrom,@EffectiveTo,@Remarks)
ON DUPLICATE KEY UPDATE Id=LAST_INSERT_ID(Id),IsEnabled=VALUES(IsEnabled),EffectiveFrom=VALUES(EffectiveFrom),EffectiveTo=VALUES(EffectiveTo),Remarks=VALUES(Remarks),UpdatedAt=CURRENT_TIMESTAMP;
SELECT LAST_INSERT_ID();", row);
        await AuditAsync(db, "TravelExpenseClientSetting", id, old is null ? "Create" : "Update", old, row, changedBy);
        return await db.QueryFirstAsync<TravelExpenseClientSetting>(@"SELECT s.*, COALESCE(c.Name,'') ClientName
FROM travel_expense_client_settings s JOIN clients c ON c.Id=s.ClientId WHERE s.Id=@Id", new { Id = id });
    }

    public async Task<(long Id, string Error)> SavePolicyAsync(TravelPolicy row, string changedBy)
    {
        var error = ValidatePolicy(row);
        if (!string.IsNullOrWhiteSpace(error)) return (0, error);
        await using var db = Connection(); await db.OpenAsync(); await EnsureTablesAsync(db);
        if (row.CompanyId > 0 && await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM clients WHERE Id=@CompanyId", row) == 0) return (0, "Company/client was not found.");
        row.PolicyCode = row.PolicyCode.Trim().ToUpperInvariant();
        row.PolicyName = row.PolicyName.Trim();
        row.BusinessUnit = row.BusinessUnit.Trim();
        row.Description = row.Description.Trim();
        var old = row.Id > 0 ? await db.QueryFirstOrDefaultAsync<TravelPolicy>("SELECT * FROM travel_policies WHERE Id=@Id", row) : null;
        if (row.Id <= 0)
        {
            var id = await db.ExecuteScalarAsync<long>(@"INSERT INTO travel_policies (PolicyCode,PolicyName,CompanyId,BusinessUnit,EffectiveFrom,EffectiveTo,Status,Description,IsActive)
VALUES (@PolicyCode,@PolicyName,@CompanyId,@BusinessUnit,@EffectiveFrom,@EffectiveTo,@Status,@Description,@IsActive); SELECT LAST_INSERT_ID();", row);
            await AuditAsync(db, "TravelPolicy", id, "Create", null, row, changedBy);
            return (id, "");
        }
        await db.ExecuteAsync(@"UPDATE travel_policies SET PolicyCode=@PolicyCode,PolicyName=@PolicyName,CompanyId=@CompanyId,BusinessUnit=@BusinessUnit,EffectiveFrom=@EffectiveFrom,EffectiveTo=@EffectiveTo,Status=@Status,Description=@Description,IsActive=@IsActive,UpdatedAt=CURRENT_TIMESTAMP WHERE Id=@Id", row);
        await AuditAsync(db, "TravelPolicy", row.Id, "Update", old, row, changedBy);
        return (row.Id, "");
    }

    public async Task<(long Id, string Error)> SaveAssignmentAsync(TravelPolicyAssignment row, string changedBy)
    {
        var error = ValidateAssignment(row);
        if (!string.IsNullOrWhiteSpace(error)) return (0, error);
        await using var db = Connection(); await db.OpenAsync(); await EnsureTablesAsync(db);
        var policy = await db.QueryFirstOrDefaultAsync<TravelPolicy>("SELECT * FROM travel_policies WHERE Id=@PolicyId", row);
        if (policy is null) return (0, "Select a valid travel policy.");
        row.CompanyId = row.CompanyId <= 0 ? policy.CompanyId : row.CompanyId;
        row.BranchId = row.BranchId is > 0 ? row.BranchId : null;
        row.EmployeeId = row.EmployeeId is > 0 ? row.EmployeeId : null;
        var old = row.Id > 0 ? await db.QueryFirstOrDefaultAsync<TravelPolicyAssignment>("SELECT * FROM travel_policy_assignments WHERE Id=@Id", row) : null;
        if (row.Id <= 0)
        {
            var id = await db.ExecuteScalarAsync<long>(@"INSERT INTO travel_policy_assignments (PolicyId,CompanyId,BranchId,Department,Grade,Designation,EmployeeCategory,EmploymentType,EmployeeId,Priority,EffectiveFrom,EffectiveTo,IsActive)
VALUES (@PolicyId,@CompanyId,@BranchId,@Department,@Grade,@Designation,@EmployeeCategory,@EmploymentType,@EmployeeId,@Priority,@EffectiveFrom,@EffectiveTo,@IsActive); SELECT LAST_INSERT_ID();", row);
            await AuditAsync(db, "TravelPolicyAssignment", id, "Create", null, row, changedBy);
            return (id, "");
        }
        await db.ExecuteAsync(@"UPDATE travel_policy_assignments SET PolicyId=@PolicyId,CompanyId=@CompanyId,BranchId=@BranchId,Department=@Department,Grade=@Grade,Designation=@Designation,EmployeeCategory=@EmployeeCategory,EmploymentType=@EmploymentType,EmployeeId=@EmployeeId,Priority=@Priority,EffectiveFrom=@EffectiveFrom,EffectiveTo=@EffectiveTo,IsActive=@IsActive,UpdatedAt=CURRENT_TIMESTAMP WHERE Id=@Id", row);
        await AuditAsync(db, "TravelPolicyAssignment", row.Id, "Update", old, row, changedBy);
        return (row.Id, "");
    }

    public async Task<(long Id, string Error)> SaveRuleAsync(TravelPolicyRule row, string changedBy)
    {
        var error = ValidateRule(row);
        if (!string.IsNullOrWhiteSpace(error)) return (0, error);
        await using var db = Connection(); await db.OpenAsync(); await EnsureTablesAsync(db);
        if (await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM travel_policies WHERE Id=@PolicyId", row) == 0) return (0, "Select a valid travel policy.");
        if (row.WorkflowId is <= 0) row.WorkflowId = null;
        row.EligibilityJson = CleanJson(row.EligibilityJson);
        row.ConfigJson = CleanJson(row.ConfigJson);
        var old = row.Id > 0 ? await db.QueryFirstOrDefaultAsync<TravelPolicyRule>("SELECT * FROM travel_policy_rules WHERE Id=@Id", row) : null;
        if (row.Id <= 0)
        {
            var id = await db.ExecuteScalarAsync<long>(@"INSERT INTO travel_policy_rules (PolicyId,RuleType,RuleName,AppliesTo,IsAllowed,EligibilityJson,LimitAmount,LimitCurrency,ReceiptMandatory,ApprovalRequired,WorkflowId,ExceptionHandling,ConfigJson,IsActive)
VALUES (@PolicyId,@RuleType,@RuleName,@AppliesTo,@IsAllowed,@EligibilityJson,@LimitAmount,@LimitCurrency,@ReceiptMandatory,@ApprovalRequired,@WorkflowId,@ExceptionHandling,@ConfigJson,@IsActive); SELECT LAST_INSERT_ID();", row);
            await AuditAsync(db, "TravelPolicyRule", id, "Create", null, row, changedBy);
            return (id, "");
        }
        await db.ExecuteAsync(@"UPDATE travel_policy_rules SET PolicyId=@PolicyId,RuleType=@RuleType,RuleName=@RuleName,AppliesTo=@AppliesTo,IsAllowed=@IsAllowed,EligibilityJson=@EligibilityJson,LimitAmount=@LimitAmount,LimitCurrency=@LimitCurrency,ReceiptMandatory=@ReceiptMandatory,ApprovalRequired=@ApprovalRequired,WorkflowId=@WorkflowId,ExceptionHandling=@ExceptionHandling,ConfigJson=@ConfigJson,IsActive=@IsActive,UpdatedAt=CURRENT_TIMESTAMP WHERE Id=@Id", row);
        await AuditAsync(db, "TravelPolicyRule", row.Id, "Update", old, row, changedBy);
        return (row.Id, "");
    }

    public async Task<(long Id, string Error)> SaveCategoryAsync(TravelExpenseCategory row, string changedBy)
    {
        if (string.IsNullOrWhiteSpace(row.CategoryCode) || string.IsNullOrWhiteSpace(row.CategoryName)) return (0, "Category code and name are required.");
        await using var db = Connection(); await db.OpenAsync(); await EnsureTablesAsync(db);
        row.CategoryCode = row.CategoryCode.Trim().ToUpperInvariant();
        row.CategoryName = row.CategoryName.Trim();
        row.ExpenseType = string.IsNullOrWhiteSpace(row.ExpenseType) ? row.CategoryName.Trim() : row.ExpenseType.Trim();
        if (row.ClientId <= 0) return (0, "Client is required for expense category.");
        if (await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM clients WHERE Id=@ClientId AND IsActive=TRUE", row) == 0) return (0, "Select an active client.");
        row.ParentId = row.ParentId is > 0 ? row.ParentId : null;
        if (row.IsClaimHeader || row.ParentId is null)
        {
            var old = row.Id > 0 ? await db.QueryFirstOrDefaultAsync<object>("SELECT * FROM expense_headers WHERE Id=@Id", row) : null;
            var existingHeaderId = await db.ExecuteScalarAsync<long?>("SELECT Id FROM expense_headers WHERE HeaderCode=@CategoryCode LIMIT 1", row);
            if (row.Id > 0 && existingHeaderId is not null && existingHeaderId.Value != row.Id) return (0, "Header code already exists globally.");
            var headerId = row.Id > 0 && await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM expense_headers WHERE Id=@Id", row) > 0
                ? row.Id
                : existingHeaderId ?? await db.ExecuteScalarAsync<long>(@"INSERT INTO expense_headers (HeaderCode,HeaderName,IsActive) VALUES (@CategoryCode,@CategoryName,TRUE); SELECT LAST_INSERT_ID();", row);
            if (row.Id > 0 && headerId == row.Id)
                await db.ExecuteAsync("UPDATE expense_headers SET HeaderCode=@CategoryCode,HeaderName=@CategoryName,IsActive=TRUE,UpdatedAt=CURRENT_TIMESTAMP WHERE Id=@Id", row);
            await db.ExecuteAsync(@"INSERT INTO client_expense_header_settings (ClientId,HeaderId,IsEnabled)
VALUES (@ClientId,@HeaderId,@IsActive)
ON DUPLICATE KEY UPDATE IsEnabled=VALUES(IsEnabled),UpdatedAt=CURRENT_TIMESTAMP", new { row.ClientId, HeaderId = headerId, row.IsActive });
            await AuditAsync(db, "ExpenseHeader", headerId, old is null ? "Create" : "Update", old, row, changedBy);
            return (headerId, "");
        }

        if (row.ParentId == row.Id) return (0, "A category cannot be its own parent.");
        var header = await db.QueryFirstOrDefaultAsync<ExpenseHeaderRow>("SELECT Id,HeaderName FROM expense_headers WHERE Id=@ParentId AND IsActive=TRUE", row);
        if (header is null) return (0, "Select a valid global expense header.");
        row.ExpenseType = header.HeaderName;
        var categoryExists = row.Id > 0 && await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM expense_categories WHERE Id=@Id", row) > 0;
        var oldCategory = categoryExists ? await db.QueryFirstOrDefaultAsync<object>("SELECT * FROM expense_categories WHERE Id=@Id", row) : null;
        var existingCategoryId = await db.ExecuteScalarAsync<long?>("SELECT Id FROM expense_categories WHERE HeaderId=@ParentId AND CategoryCode=@CategoryCode LIMIT 1", row);
        if (row.Id > 0 && existingCategoryId is not null && existingCategoryId.Value != row.Id) return (0, "Category code already exists under this global header.");
        var categoryId = categoryExists
            ? row.Id
            : existingCategoryId ?? await db.ExecuteScalarAsync<long>(@"INSERT INTO expense_categories (HeaderId,CategoryCode,CategoryName,IsActive) VALUES (@ParentId,@CategoryCode,@CategoryName,TRUE); SELECT LAST_INSERT_ID();", row);
        if (categoryExists)
            await db.ExecuteAsync("UPDATE expense_categories SET HeaderId=@ParentId,CategoryCode=@CategoryCode,CategoryName=@CategoryName,IsActive=TRUE,UpdatedAt=CURRENT_TIMESTAMP WHERE Id=@Id", row);
        await db.ExecuteAsync(@"INSERT INTO client_expense_header_settings (ClientId,HeaderId,IsEnabled)
VALUES (@ClientId,@HeaderId,TRUE)
ON DUPLICATE KEY UPDATE IsEnabled=TRUE,UpdatedAt=CURRENT_TIMESTAMP", new { row.ClientId, HeaderId = row.ParentId });
        await db.ExecuteAsync(@"INSERT INTO client_expense_category_settings (ClientId,CategoryId,ReceiptMandatory,GstApplicable,DailyLimit,MaximumClaim,RequiresFinanceApproval,RequiresManagerApproval,IsEnabled)
VALUES (@ClientId,@CategoryId,@ReceiptMandatory,@GstApplicable,@DailyLimit,@MaximumClaim,@RequiresFinanceApproval,@RequiresManagerApproval,@IsActive)
ON DUPLICATE KEY UPDATE ReceiptMandatory=VALUES(ReceiptMandatory),GstApplicable=VALUES(GstApplicable),DailyLimit=VALUES(DailyLimit),MaximumClaim=VALUES(MaximumClaim),RequiresFinanceApproval=VALUES(RequiresFinanceApproval),RequiresManagerApproval=VALUES(RequiresManagerApproval),IsEnabled=VALUES(IsEnabled),UpdatedAt=CURRENT_TIMESTAMP",
            new { row.ClientId, CategoryId = categoryId, row.ReceiptMandatory, row.GstApplicable, row.DailyLimit, row.MaximumClaim, row.RequiresFinanceApproval, row.RequiresManagerApproval, row.IsActive });
        await AuditAsync(db, "ExpenseCategory", categoryId, oldCategory is null ? "Create" : "Update", oldCategory, row, changedBy);
        return (categoryId, "");
    }

    private static string ValidatePolicy(TravelPolicy row)
    {
        if (string.IsNullOrWhiteSpace(row.PolicyCode) || string.IsNullOrWhiteSpace(row.PolicyName)) return "Policy code and name are required.";
        if (row.CompanyId <= 0) return "Company/client is required.";
        if (!PolicyStatuses.Contains(row.Status)) return "Policy status is invalid.";
        if (row.EffectiveTo.HasValue && row.EffectiveTo.Value.Date < row.EffectiveFrom.Date) return "Effective to cannot be before effective from.";
        return "";
    }

    private static string ValidateAssignment(TravelPolicyAssignment row)
    {
        if (row.PolicyId <= 0) return "Travel policy is required.";
        if (row.Priority <= 0) return "Priority must be greater than zero.";
        if (row.EffectiveTo.HasValue && row.EffectiveTo.Value.Date < row.EffectiveFrom.Date) return "Effective to cannot be before effective from.";
        return "";
    }

    private static string ValidateRule(TravelPolicyRule row)
    {
        if (row.PolicyId <= 0) return "Travel policy is required.";
        if (!RuleTypes.Contains(row.RuleType)) return "Rule type is invalid.";
        if (string.IsNullOrWhiteSpace(row.RuleName) || string.IsNullOrWhiteSpace(row.AppliesTo)) return "Rule name and applies-to value are required.";
        if (!ExceptionModes.Contains(row.ExceptionHandling)) return "Exception handling value is invalid.";
        if (row.LimitAmount is < 0) return "Limit amount cannot be negative.";
        return "";
    }

    private static string CleanJson(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "{}";
        JsonDocument.Parse(value);
        return value;
    }

    private static async Task AuditAsync(MySqlConnection db, string entityType, long entityId, string action, object? oldValue, object newValue, string changedBy)
    {
        await db.ExecuteAsync(@"INSERT INTO travel_policy_audit (EntityType,EntityId,Action,OldValueJson,NewValueJson,ChangedBy)
VALUES (@EntityType,@EntityId,@Action,@OldValueJson,@NewValueJson,@ChangedBy)", new
        {
            EntityType = entityType,
            EntityId = entityId,
            Action = action,
            OldValueJson = oldValue is null ? null : JsonSerializer.Serialize(oldValue),
            NewValueJson = JsonSerializer.Serialize(newValue),
            ChangedBy = changedBy
        });
    }

    private static async Task EnsureTablesAsync(MySqlConnection db)
    {
        await db.ExecuteAsync(@"CREATE TABLE IF NOT EXISTS travel_policies (
Id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
PolicyCode VARCHAR(40) NOT NULL,
PolicyName VARCHAR(160) NOT NULL,
CompanyId INT NOT NULL,
BusinessUnit VARCHAR(120) NOT NULL DEFAULT '',
EffectiveFrom DATE NOT NULL,
EffectiveTo DATE NULL,
Status VARCHAR(30) NOT NULL DEFAULT 'Draft',
Description TEXT NULL,
IsActive BOOLEAN NOT NULL DEFAULT TRUE,
CreatedAt TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
UpdatedAt TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
UNIQUE KEY UX_TravelPolicies_Code (PolicyCode),
KEY IX_TravelPolicies_Company (CompanyId, Status, EffectiveFrom)
);
CREATE TABLE IF NOT EXISTS travel_policy_assignments (
Id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
PolicyId BIGINT NOT NULL,
CompanyId INT NOT NULL,
BranchId INT NULL,
Department VARCHAR(120) NOT NULL DEFAULT '',
Grade VARCHAR(80) NOT NULL DEFAULT '',
Designation VARCHAR(120) NOT NULL DEFAULT '',
EmployeeCategory VARCHAR(80) NOT NULL DEFAULT '',
EmploymentType VARCHAR(80) NOT NULL DEFAULT '',
EmployeeId INT NULL,
Priority INT NOT NULL DEFAULT 100,
EffectiveFrom DATE NOT NULL,
EffectiveTo DATE NULL,
IsActive BOOLEAN NOT NULL DEFAULT TRUE,
CreatedAt TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
UpdatedAt TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
KEY IX_TravelPolicyAssignments_Policy (PolicyId),
KEY IX_TravelPolicyAssignments_Resolve (CompanyId, BranchId, Department, Grade, Designation, EmployeeId, Priority)
);
CREATE TABLE IF NOT EXISTS travel_policy_rules (
Id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
PolicyId BIGINT NOT NULL,
RuleType VARCHAR(60) NOT NULL,
RuleName VARCHAR(160) NOT NULL,
AppliesTo VARCHAR(120) NOT NULL,
IsAllowed BOOLEAN NOT NULL DEFAULT TRUE,
EligibilityJson JSON NULL,
LimitAmount DECIMAL(18,2) NULL,
LimitCurrency VARCHAR(10) NOT NULL DEFAULT 'INR',
ReceiptMandatory BOOLEAN NOT NULL DEFAULT FALSE,
ApprovalRequired BOOLEAN NOT NULL DEFAULT FALSE,
WorkflowId BIGINT NULL,
ExceptionHandling VARCHAR(40) NOT NULL DEFAULT 'Warning',
ConfigJson JSON NULL,
IsActive BOOLEAN NOT NULL DEFAULT TRUE,
CreatedAt TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
UpdatedAt TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
KEY IX_TravelPolicyRules_PolicyType (PolicyId, RuleType, AppliesTo)
);
CREATE TABLE IF NOT EXISTS travel_expense_categories (
Id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
ClientId INT NOT NULL DEFAULT 0,
ParentId BIGINT NULL,
ExpenseType VARCHAR(120) NOT NULL DEFAULT '',
IsClaimHeader BOOLEAN NOT NULL DEFAULT FALSE,
CategoryCode VARCHAR(40) NOT NULL,
CategoryName VARCHAR(160) NOT NULL,
ReceiptMandatory BOOLEAN NOT NULL DEFAULT FALSE,
GstApplicable BOOLEAN NOT NULL DEFAULT FALSE,
DailyLimit DECIMAL(18,2) NULL,
MaximumClaim DECIMAL(18,2) NULL,
RequiresFinanceApproval BOOLEAN NOT NULL DEFAULT FALSE,
RequiresManagerApproval BOOLEAN NOT NULL DEFAULT FALSE,
IsActive BOOLEAN NOT NULL DEFAULT TRUE,
CreatedAt TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
UpdatedAt TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
UNIQUE KEY UX_TravelExpenseCategories_Code (CategoryCode),
KEY IX_TravelExpenseCategories_Parent (ParentId)
);
CREATE TABLE IF NOT EXISTS expense_headers (
Id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
HeaderCode VARCHAR(60) NOT NULL,
HeaderName VARCHAR(160) NOT NULL,
IsActive BOOLEAN NOT NULL DEFAULT TRUE,
CreatedAt TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
UpdatedAt TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
UNIQUE KEY UX_ExpenseHeaders_Code (HeaderCode)
);
CREATE TABLE IF NOT EXISTS expense_categories (
Id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
HeaderId BIGINT NOT NULL,
CategoryCode VARCHAR(60) NOT NULL,
CategoryName VARCHAR(160) NOT NULL,
IsActive BOOLEAN NOT NULL DEFAULT TRUE,
CreatedAt TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
UpdatedAt TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
UNIQUE KEY UX_ExpenseCategories_HeaderCode (HeaderId, CategoryCode),
KEY IX_ExpenseCategories_Header (HeaderId)
);
CREATE TABLE IF NOT EXISTS client_expense_header_settings (
Id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
ClientId INT NOT NULL,
HeaderId BIGINT NOT NULL,
IsEnabled BOOLEAN NOT NULL DEFAULT TRUE,
EffectiveFrom DATE NULL,
EffectiveTo DATE NULL,
CreatedAt TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
UpdatedAt TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
UNIQUE KEY UX_ClientExpenseHeader (ClientId, HeaderId),
KEY IX_ClientExpenseHeader_Client (ClientId, IsEnabled)
);
CREATE TABLE IF NOT EXISTS client_expense_category_settings (
Id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
ClientId INT NOT NULL,
CategoryId BIGINT NOT NULL,
ReceiptMandatory BOOLEAN NOT NULL DEFAULT FALSE,
GstApplicable BOOLEAN NOT NULL DEFAULT FALSE,
DailyLimit DECIMAL(18,2) NULL,
MaximumClaim DECIMAL(18,2) NULL,
RequiresFinanceApproval BOOLEAN NOT NULL DEFAULT FALSE,
RequiresManagerApproval BOOLEAN NOT NULL DEFAULT FALSE,
IsEnabled BOOLEAN NOT NULL DEFAULT TRUE,
EffectiveFrom DATE NULL,
EffectiveTo DATE NULL,
CreatedAt TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
UpdatedAt TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
UNIQUE KEY UX_ClientExpenseCategory (ClientId, CategoryId),
KEY IX_ClientExpenseCategory_Client (ClientId, IsEnabled)
);
CREATE TABLE IF NOT EXISTS travel_expense_client_settings (
Id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
ClientId INT NOT NULL,
IsEnabled BOOLEAN NOT NULL DEFAULT FALSE,
EffectiveFrom DATE NULL,
EffectiveTo DATE NULL,
Remarks VARCHAR(500) NOT NULL DEFAULT '',
CreatedAt TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
UpdatedAt TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
UNIQUE KEY UX_TravelExpenseClientSettings_Client (ClientId),
KEY IX_TravelExpenseClientSettings_Enabled (ClientId, IsEnabled)
);
CREATE TABLE IF NOT EXISTS travel_policy_audit (
Id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
EntityType VARCHAR(80) NOT NULL,
EntityId BIGINT NOT NULL,
Action VARCHAR(40) NOT NULL,
OldValueJson JSON NULL,
NewValueJson JSON NULL,
ChangedBy VARCHAR(160) NOT NULL DEFAULT '',
ChangedOn TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
KEY IX_TravelPolicyAudit_Entity (EntityType, EntityId),
KEY IX_TravelPolicyAudit_ChangedOn (ChangedOn)
);");
        await EnsureColumnAsync(db, "travel_expense_categories", "ClientId", "INT NOT NULL DEFAULT 0 AFTER Id");
        await EnsureColumnAsync(db, "travel_expense_categories", "ExpenseType", "VARCHAR(120) NOT NULL DEFAULT '' AFTER ParentId");
        await EnsureColumnAsync(db, "travel_expense_categories", "IsClaimHeader", "BOOLEAN NOT NULL DEFAULT FALSE AFTER ExpenseType");
        await db.ExecuteAsync(@"INSERT INTO travel_expense_client_settings (ClientId,IsEnabled)
SELECT Id,FALSE FROM clients WHERE IsActive=TRUE
ON DUPLICATE KEY UPDATE ClientId=ClientId");
        await DropIndexIfExistsAsync(db, "travel_expense_categories", "UX_TravelExpenseCategories_Code");
        await MigrateLegacyExpenseCategoriesAsync(db);
    }

    private static async Task MigrateLegacyExpenseCategoriesAsync(MySqlConnection db) =>
        await db.ExecuteAsync(@"INSERT INTO expense_headers (HeaderCode,HeaderName,IsActive)
SELECT DISTINCT CategoryCode,CategoryName,TRUE FROM travel_expense_categories WHERE IsClaimHeader=TRUE AND CategoryCode<>''
ON DUPLICATE KEY UPDATE HeaderName=VALUES(HeaderName), IsActive=TRUE;
INSERT INTO expense_categories (HeaderId,CategoryCode,CategoryName,IsActive)
SELECT h.Id,child.CategoryCode,child.CategoryName,TRUE
FROM travel_expense_categories child
JOIN travel_expense_categories parent ON parent.Id=child.ParentId
JOIN expense_headers h ON h.HeaderCode=parent.CategoryCode
WHERE child.IsClaimHeader=FALSE AND child.CategoryCode<>''
ON DUPLICATE KEY UPDATE CategoryName=VALUES(CategoryName), IsActive=TRUE;
INSERT INTO client_expense_header_settings (ClientId,HeaderId,IsEnabled)
SELECT old.ClientId,h.Id,old.IsActive
FROM travel_expense_categories old
JOIN expense_headers h ON h.HeaderCode=old.CategoryCode
WHERE old.IsClaimHeader=TRUE AND old.ClientId>0
ON DUPLICATE KEY UPDATE IsEnabled=VALUES(IsEnabled);
INSERT INTO client_expense_category_settings (ClientId,CategoryId,ReceiptMandatory,GstApplicable,DailyLimit,MaximumClaim,RequiresFinanceApproval,RequiresManagerApproval,IsEnabled)
SELECT old.ClientId,c.Id,old.ReceiptMandatory,old.GstApplicable,old.DailyLimit,old.MaximumClaim,old.RequiresFinanceApproval,old.RequiresManagerApproval,old.IsActive
FROM travel_expense_categories old
JOIN travel_expense_categories parent ON parent.Id=old.ParentId
JOIN expense_headers h ON h.HeaderCode=parent.CategoryCode
JOIN expense_categories c ON c.HeaderId=h.Id AND c.CategoryCode=old.CategoryCode
WHERE old.IsClaimHeader=FALSE AND old.ClientId>0
ON DUPLICATE KEY UPDATE ReceiptMandatory=VALUES(ReceiptMandatory),GstApplicable=VALUES(GstApplicable),DailyLimit=VALUES(DailyLimit),MaximumClaim=VALUES(MaximumClaim),RequiresFinanceApproval=VALUES(RequiresFinanceApproval),RequiresManagerApproval=VALUES(RequiresManagerApproval),IsEnabled=VALUES(IsEnabled);");

    private static async Task SeedSampleDataAsync(MySqlConnection db)
    {
        var clientIds = (await db.QueryAsync<int>("SELECT Id FROM clients WHERE IsActive=TRUE ORDER BY Id")).ToList();
        foreach (var activeClientId in clientIds) await SeedExpenseCategoriesForClientAsync(db, activeClientId);

        var hasPolicy = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM travel_policies");
        if (hasPolicy > 0) return;

        var clientId = await db.ExecuteScalarAsync<int?>("SELECT Id FROM clients WHERE IsActive=TRUE ORDER BY Id LIMIT 1");
        if (clientId is null) return;
        var branchId = await db.ExecuteScalarAsync<int?>("SELECT Id FROM worklocations WHERE ClientId=@ClientId AND IsActive=TRUE ORDER BY IsPrimary DESC, Id LIMIT 1", new { ClientId = clientId.Value });
        var employeeId = await db.ExecuteScalarAsync<int?>("SELECT Id FROM employees WHERE ClientId=@ClientId AND IsActive=TRUE ORDER BY Id LIMIT 1", new { ClientId = clientId.Value });
        var workflowId = await db.ExecuteScalarAsync<int?>("SELECT Id FROM workflowmasters WHERE IsActive=TRUE ORDER BY Id LIMIT 1");

        var stdPolicyId = await db.ExecuteScalarAsync<long>(@"INSERT INTO travel_policies (PolicyCode,PolicyName,CompanyId,BusinessUnit,EffectiveFrom,EffectiveTo,Status,Description,IsActive)
VALUES ('TE-IN-STD','India Standard Travel Policy',@ClientId,'Corporate','2026-04-01',NULL,'Active','Sample domestic travel and expense policy for normal business travel.',TRUE); SELECT LAST_INSERT_ID();", new { ClientId = clientId.Value });
        var intlPolicyId = await db.ExecuteScalarAsync<long>(@"INSERT INTO travel_policies (PolicyCode,PolicyName,CompanyId,BusinessUnit,EffectiveFrom,EffectiveTo,Status,Description,IsActive)
VALUES ('TE-INTL-DRAFT','International Travel Policy',@ClientId,'Corporate','2026-04-01',NULL,'Draft','Sample international travel policy kept in draft for admin review.',TRUE); SELECT LAST_INSERT_ID();", new { ClientId = clientId.Value });

        await db.ExecuteAsync(@"INSERT INTO travel_policy_assignments (PolicyId,CompanyId,BranchId,Department,Grade,Designation,EmployeeCategory,EmploymentType,EmployeeId,Priority,EffectiveFrom,EffectiveTo,IsActive)
VALUES (@PolicyId,@ClientId,NULL,'','','','','',NULL,100,'2026-04-01',NULL,TRUE);", new { PolicyId = stdPolicyId, ClientId = clientId.Value });
        if (branchId is not null)
        {
            await db.ExecuteAsync(@"INSERT INTO travel_policy_assignments (PolicyId,CompanyId,BranchId,Department,Grade,Designation,EmployeeCategory,EmploymentType,EmployeeId,Priority,EffectiveFrom,EffectiveTo,IsActive)
VALUES (@PolicyId,@ClientId,@BranchId,'','','G1','','',NULL,50,'2026-04-01',NULL,TRUE);", new { PolicyId = stdPolicyId, ClientId = clientId.Value, BranchId = branchId.Value });
        }
        if (employeeId is not null)
        {
            await db.ExecuteAsync(@"INSERT INTO travel_policy_assignments (PolicyId,CompanyId,BranchId,Department,Grade,Designation,EmployeeCategory,EmploymentType,EmployeeId,Priority,EffectiveFrom,EffectiveTo,IsActive)
VALUES (@PolicyId,@ClientId,NULL,'','','','','',@EmployeeId,10,'2026-04-01',NULL,TRUE);", new { PolicyId = stdPolicyId, ClientId = clientId.Value, EmployeeId = employeeId.Value });
        }

        var rules = new[]
        {
            new { PolicyId = stdPolicyId, RuleType = "Travel Mode", RuleName = "Economy flight for domestic travel", AppliesTo = "Flight", IsAllowed = true, EligibilityJson = @"{""grades"":[""G1"",""G2"",""G3""],""tripType"":""Domestic""}", LimitAmount = (decimal?)25000m, LimitCurrency = "INR", ReceiptMandatory = true, ApprovalRequired = true, WorkflowId = workflowId, ExceptionHandling = "Approval Override", ConfigJson = @"{""travelClass"":""Economy"",""maximumFare"":25000,""bookingWindowDays"":7}", IsActive = true },
            new { PolicyId = stdPolicyId, RuleType = "Travel Mode", RuleName = "Train travel allowed up to AC 2 tier", AppliesTo = "Train", IsAllowed = true, EligibilityJson = @"{""tripType"":""Domestic""}", LimitAmount = (decimal?)10000m, LimitCurrency = "INR", ReceiptMandatory = true, ApprovalRequired = false, WorkflowId = (int?)null, ExceptionHandling = "Warning", ConfigJson = @"{""travelClass"":""AC 2 Tier"",""maximumFare"":10000}", IsActive = true },
            new { PolicyId = stdPolicyId, RuleType = "Travel Mode", RuleName = "Own vehicle mileage reimbursement", AppliesTo = "Own Vehicle", IsAllowed = true, EligibilityJson = @"{""requiresManagerApproval"":true}", LimitAmount = (decimal?)null, LimitCurrency = "INR", ReceiptMandatory = false, ApprovalRequired = true, WorkflowId = workflowId, ExceptionHandling = "Approval Override", ConfigJson = @"{""mileageRatePerKm"":12,""maximumKmPerDay"":250}", IsActive = true },
            new { PolicyId = stdPolicyId, RuleType = "Hotel", RuleName = "Metro city hotel limit", AppliesTo = "Business Hotel", IsAllowed = true, EligibilityJson = @"{""cityCategory"":""Metro""}", LimitAmount = (decimal?)6000m, LimitCurrency = "INR", ReceiptMandatory = true, ApprovalRequired = true, WorkflowId = workflowId, ExceptionHandling = "Approval Override", ConfigJson = @"{""starRating"":""3 Star"",""roomCategory"":""Standard"",""cityLimits"":{""Delhi"":6000,""Mumbai"":7000,""Bengaluru"":6500},""sharedAccommodation"":false}", IsActive = true },
            new { PolicyId = stdPolicyId, RuleType = "Meal", RuleName = "Daily meal allowance", AppliesTo = "Lunch", IsAllowed = true, EligibilityJson = @"{""tripType"":""Domestic""}", LimitAmount = (decimal?)500m, LimitCurrency = "INR", ReceiptMandatory = false, ApprovalRequired = false, WorkflowId = (int?)null, ExceptionHandling = "Warning", ConfigJson = @"{""fixedLimit"":250,""dailyLimit"":1200,""receiptMandatoryAbove"":500}", IsActive = true },
            new { PolicyId = stdPolicyId, RuleType = "Meal", RuleName = "Client entertainment requires approval", AppliesTo = "Client Entertainment", IsAllowed = true, EligibilityJson = @"{""businessPurposeRequired"":true}", LimitAmount = (decimal?)3000m, LimitCurrency = "INR", ReceiptMandatory = true, ApprovalRequired = true, WorkflowId = workflowId, ExceptionHandling = "Approval Override", ConfigJson = @"{""dailyLimit"":3000,""clientNameRequired"":true}", IsActive = true },
            new { PolicyId = stdPolicyId, RuleType = "Per Diem", RuleName = "Domestic full day allowance", AppliesTo = "Domestic Full Day", IsAllowed = true, EligibilityJson = @"{""country"":""India""}", LimitAmount = (decimal?)1500m, LimitCurrency = "INR", ReceiptMandatory = false, ApprovalRequired = false, WorkflowId = (int?)null, ExceptionHandling = "Warning", ConfigJson = @"{""cityCategory"":""Metro"",""halfDay"":750,""fullDay"":1500,""travelDay"":1000,""nonWorkingDay"":750}", IsActive = true },
            new { PolicyId = stdPolicyId, RuleType = "Local Conveyance", RuleName = "Cab aggregator reimbursement", AppliesTo = "Cab Aggregator", IsAllowed = true, EligibilityJson = @"{""withinCity"":true}", LimitAmount = (decimal?)2500m, LimitCurrency = "INR", ReceiptMandatory = true, ApprovalRequired = false, WorkflowId = (int?)null, ExceptionHandling = "Warning", ConfigJson = @"{""dailyLimit"":2500,""gstInvoicePreferred"":true}", IsActive = true },
            new { PolicyId = stdPolicyId, RuleType = "Local Conveyance", RuleName = "Fuel reimbursement", AppliesTo = "Fuel", IsAllowed = true, EligibilityJson = @"{""ownVehicleApproved"":true}", LimitAmount = (decimal?)1500m, LimitCurrency = "INR", ReceiptMandatory = true, ApprovalRequired = true, WorkflowId = workflowId, ExceptionHandling = "Approval Override", ConfigJson = @"{""mileageRate"":12,""dailyLimit"":1500}", IsActive = true },
            new { PolicyId = stdPolicyId, RuleType = "Travel Advance", RuleName = "Domestic travel advance", AppliesTo = "Domestic Travel Advance", IsAllowed = true, EligibilityJson = @"{""minimumTripDays"":2}", LimitAmount = (decimal?)20000m, LimitCurrency = "INR", ReceiptMandatory = false, ApprovalRequired = true, WorkflowId = workflowId, ExceptionHandling = "Approval Override", ConfigJson = @"{""maximumAdvancePercent"":80,""settlementDays"":7,""recoveryRule"":""Recover from salary if not settled""}", IsActive = true },
            new { PolicyId = stdPolicyId, RuleType = "Policy Violation", RuleName = "Hotel limit exceeded warning", AppliesTo = "Hotel limit exceeded", IsAllowed = true, EligibilityJson = "{}", LimitAmount = (decimal?)0m, LimitCurrency = "INR", ReceiptMandatory = false, ApprovalRequired = true, WorkflowId = workflowId, ExceptionHandling = "Approval Override", ConfigJson = @"{""message"":""Hotel claim exceeds configured city limit."",""severity"":""Warning""}", IsActive = true },
            new { PolicyId = stdPolicyId, RuleType = "Policy Violation", RuleName = "Receipt missing block", AppliesTo = "Receipt missing", IsAllowed = false, EligibilityJson = "{}", LimitAmount = (decimal?)0m, LimitCurrency = "INR", ReceiptMandatory = true, ApprovalRequired = false, WorkflowId = (int?)null, ExceptionHandling = "Block", ConfigJson = @"{""message"":""Receipt is mandatory for this category."",""severity"":""Block""}", IsActive = true },
            new { PolicyId = intlPolicyId, RuleType = "Per Diem", RuleName = "International full day allowance", AppliesTo = "International Full Day", IsAllowed = true, EligibilityJson = @"{""country"":""Any""}", LimitAmount = (decimal?)75m, LimitCurrency = "USD", ReceiptMandatory = false, ApprovalRequired = true, WorkflowId = workflowId, ExceptionHandling = "Approval Override", ConfigJson = @"{""country"":""Any"",""halfDay"":40,""fullDay"":75,""travelDay"":60}", IsActive = true }
        };
        await db.ExecuteAsync(@"INSERT INTO travel_policy_rules (PolicyId,RuleType,RuleName,AppliesTo,IsAllowed,EligibilityJson,LimitAmount,LimitCurrency,ReceiptMandatory,ApprovalRequired,WorkflowId,ExceptionHandling,ConfigJson,IsActive)
VALUES (@PolicyId,@RuleType,@RuleName,@AppliesTo,@IsAllowed,@EligibilityJson,@LimitAmount,@LimitCurrency,@ReceiptMandatory,@ApprovalRequired,@WorkflowId,@ExceptionHandling,@ConfigJson,@IsActive);", rules);
    }

    private static async Task SeedExpenseCategoriesForClientAsync(MySqlConnection db, int clientId)
    {
        await SeedHeaderAsync(db, clientId, "MEDICAL_EXPENSE", "Medical Expense");
        await SeedHeaderAsync(db, clientId, "TRAVEL_EXPENSE", "Travel Expense");
        await SeedHeaderAsync(db, clientId, "OFFICE_EXPENSE", "Office Expense");
        await SeedHeaderAsync(db, clientId, "LOCAL_CONVEYANCE", "Local Conveyance");

        await SeedChildAsync(db, clientId, "MEDICAL_EXPENSE", "MEDICINE", "Medicine", true, false, 5000, 50000);
        await SeedChildAsync(db, clientId, "MEDICAL_EXPENSE", "CONSULTANCY", "Consultancy", true, false, 3000, 30000);
        await SeedChildAsync(db, clientId, "MEDICAL_EXPENSE", "PATHOLOGY", "Pathology", true, false, 5000, 50000);
        await SeedChildAsync(db, clientId, "MEDICAL_EXPENSE", "HOSPITALIZATION", "Hospitalization", true, false, null, 200000, true);

        await SeedChildAsync(db, clientId, "TRAVEL_EXPENSE", "AIR_FARE", "Air Fare", true, true, null, 50000, true);
        await SeedChildAsync(db, clientId, "TRAVEL_EXPENSE", "TRAIN_FARE", "Train Fare", true, true, null, 10000);
        await SeedChildAsync(db, clientId, "TRAVEL_EXPENSE", "BUS_FARE", "Bus Fare", true, true, null, 8000);
        await SeedChildAsync(db, clientId, "TRAVEL_EXPENSE", "HOTEL_STAY", "Hotel Stay", true, true, 6000, 60000, true);
        await SeedChildAsync(db, clientId, "TRAVEL_EXPENSE", "MEALS", "Meals", false, false, 1200, 12000);
        await SeedChildAsync(db, clientId, "TRAVEL_EXPENSE", "CAB_TAXI", "Cab / Taxi", true, true, 2500, 25000);

        await SeedChildAsync(db, clientId, "OFFICE_EXPENSE", "INTERNET", "Internet", true, true, null, 2000);
        await SeedChildAsync(db, clientId, "OFFICE_EXPENSE", "MOBILE", "Mobile", true, true, null, 1500);
        await SeedChildAsync(db, clientId, "OFFICE_EXPENSE", "STATIONERY", "Stationery", true, true, null, 5000);

        await SeedChildAsync(db, clientId, "LOCAL_CONVEYANCE", "FUEL", "Fuel", true, true, 1500, 15000);
        await SeedChildAsync(db, clientId, "LOCAL_CONVEYANCE", "PARKING", "Parking", true, true, 500, 5000);
        await SeedChildAsync(db, clientId, "LOCAL_CONVEYANCE", "TOLL", "Toll", true, true, 1000, 10000);
        await SeedChildAsync(db, clientId, "LOCAL_CONVEYANCE", "METRO", "Metro", false, false, 500, 5000);
    }

    private static async Task SeedHeaderAsync(MySqlConnection db, int clientId, string code, string name) =>
        await db.ExecuteAsync(@"INSERT INTO expense_headers (HeaderCode,HeaderName,IsActive)
SELECT @Code,@Name,TRUE WHERE NOT EXISTS (SELECT 1 FROM expense_headers WHERE HeaderCode=@Code);
INSERT INTO client_expense_header_settings (ClientId,HeaderId,IsEnabled)
SELECT @ClientId,h.Id,TRUE FROM expense_headers h WHERE h.HeaderCode=@Code
ON DUPLICATE KEY UPDATE IsEnabled=VALUES(IsEnabled),UpdatedAt=CURRENT_TIMESTAMP", new { ClientId = clientId, Code = code, Name = name });

    private static async Task SeedChildAsync(MySqlConnection db, int clientId, string parentCode, string code, string name, bool receipt, bool gst, decimal? dailyLimit, decimal? maximumClaim, bool financeApproval = false) =>
        await db.ExecuteAsync(@"INSERT INTO expense_categories (HeaderId,CategoryCode,CategoryName,IsActive)
SELECT h.Id,@Code,@Name,TRUE FROM expense_headers h
WHERE h.HeaderCode=@ParentCode AND NOT EXISTS (SELECT 1 FROM expense_categories c WHERE c.HeaderId=h.Id AND c.CategoryCode=@Code);
INSERT INTO client_expense_category_settings (ClientId,CategoryId,ReceiptMandatory,GstApplicable,DailyLimit,MaximumClaim,RequiresFinanceApproval,RequiresManagerApproval,IsEnabled)
SELECT @ClientId,c.Id,@Receipt,@Gst,@DailyLimit,@MaximumClaim,@FinanceApproval,TRUE,TRUE
FROM expense_categories c JOIN expense_headers h ON h.Id=c.HeaderId
WHERE h.HeaderCode=@ParentCode AND c.CategoryCode=@Code
ON DUPLICATE KEY UPDATE ReceiptMandatory=VALUES(ReceiptMandatory),GstApplicable=VALUES(GstApplicable),DailyLimit=VALUES(DailyLimit),MaximumClaim=VALUES(MaximumClaim),RequiresFinanceApproval=VALUES(RequiresFinanceApproval),RequiresManagerApproval=VALUES(RequiresManagerApproval),IsEnabled=VALUES(IsEnabled),UpdatedAt=CURRENT_TIMESTAMP", new { ClientId = clientId, ParentCode = parentCode, Code = code, Name = name, Receipt = receipt, Gst = gst, DailyLimit = dailyLimit, MaximumClaim = maximumClaim, FinanceApproval = financeApproval });

    private static async Task EnsureColumnAsync(MySqlConnection db, string tableName, string columnName, string definition)
    {
        var exists = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME=@TableName AND COLUMN_NAME=@ColumnName", new { TableName = tableName, ColumnName = columnName });
        if (exists == 0) await db.ExecuteAsync($"ALTER TABLE `{tableName}` ADD COLUMN `{columnName}` {definition}");
    }

    private static async Task DropIndexIfExistsAsync(MySqlConnection db, string tableName, string indexName)
    {
        var exists = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME=@TableName AND INDEX_NAME=@IndexName", new { TableName = tableName, IndexName = indexName });
        if (exists > 0) await db.ExecuteAsync($"ALTER TABLE `{tableName}` DROP INDEX `{indexName}`");
    }

    private sealed class ExpenseHeaderRow { public long Id { get; set; } public string HeaderName { get; set; } = ""; }
}
