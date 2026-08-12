using System.Security.Cryptography;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public class AuthRepository(IConfiguration configuration)
{
    private const int TokenBytes = 32;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int Iterations = 120_000;

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
CREATE TABLE IF NOT EXISTS authusers (
    Id INT PRIMARY KEY AUTO_INCREMENT,
    Email VARCHAR(190) NOT NULL,
    DisplayName VARCHAR(190) NOT NULL,
    Mobile VARCHAR(40) NOT NULL DEFAULT '',
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
CREATE TABLE IF NOT EXISTS authroles (
    Id INT PRIMARY KEY AUTO_INCREMENT,
    Code VARCHAR(80) NOT NULL,
    Name VARCHAR(150) NOT NULL,
    Description VARCHAR(500),
    IsSystem BOOLEAN NOT NULL DEFAULT FALSE,
    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE KEY UX_AuthRoles_Code (Code)
);
CREATE TABLE IF NOT EXISTS authpermissions (
    Id INT PRIMARY KEY AUTO_INCREMENT,
    Code VARCHAR(120) NOT NULL,
    Name VARCHAR(150) NOT NULL,
    Module VARCHAR(80) NOT NULL,
    Description VARCHAR(500),
    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE KEY UX_AuthPermissions_Code (Code)
);
CREATE TABLE IF NOT EXISTS authuserroles (
    UserId INT NOT NULL,
    RoleId INT NOT NULL,
    PRIMARY KEY (UserId, RoleId)
);
CREATE TABLE IF NOT EXISTS authrolepermissions (
    RoleId INT NOT NULL,
    PermissionId INT NOT NULL,
    PRIMARY KEY (RoleId, PermissionId)
);
CREATE TABLE IF NOT EXISTS authsessions (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    UserId INT NOT NULL,
    TokenHash CHAR(64) NOT NULL,
    IpAddress VARCHAR(80),
    UserAgent VARCHAR(500),
    ExpiresAt DATETIME NOT NULL,
    RevokedAt DATETIME NULL,
    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE KEY UX_AuthSessions_TokenHash (TokenHash),
    INDEX IX_AuthSessions_User (UserId)
);
CREATE TABLE IF NOT EXISTS auditlogs (
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
CREATE TABLE IF NOT EXISTS schema_migrations (
    MigrationKey VARCHAR(190) PRIMARY KEY,
    AppliedAtUtc DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
);");
        await EnsureForeignKeyAsync(connection, "authuserroles", "FK_AuthUserRoles_User", "FOREIGN KEY (UserId) REFERENCES authusers(Id) ON DELETE CASCADE");
        await EnsureForeignKeyAsync(connection, "authuserroles", "FK_AuthUserRoles_Role", "FOREIGN KEY (RoleId) REFERENCES authroles(Id) ON DELETE CASCADE");
        await EnsureForeignKeyAsync(connection, "authrolepermissions", "FK_AuthRolePermissions_Role", "FOREIGN KEY (RoleId) REFERENCES authroles(Id) ON DELETE CASCADE");
        await EnsureForeignKeyAsync(connection, "authrolepermissions", "FK_AuthRolePermissions_Permission", "FOREIGN KEY (PermissionId) REFERENCES authpermissions(Id) ON DELETE CASCADE");
        await EnsureForeignKeyAsync(connection, "authsessions", "FK_AuthSessions_User", "FOREIGN KEY (UserId) REFERENCES authusers(Id) ON DELETE CASCADE");
        await EnsureColumnAsync(connection, "authusers", "EmployeeId", "INT NULL");
        await EnsureColumnAsync(connection, "authusers", "Mobile", "VARCHAR(40) NOT NULL DEFAULT '' AFTER DisplayName");
        await SeedSecurityCatalogAsync(connection);

    }

    public async Task<LoginResponse?> LoginAsync(LoginRequest request, string ipAddress, string userAgent)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        var row = await connection.QueryFirstOrDefaultAsync<AuthUserRecord>("SELECT * FROM authusers WHERE Email = @Email", new { Email = NormalizeEmail(request.Email) });
        if (row is null || !row.IsActive || !VerifyPassword(request.Password, row.PasswordHash))
            return null;

        var user = await BuildUserAsync(connection, row.Id) ?? new AuthUser();
        if (request.Portal.Equals("Admin", StringComparison.OrdinalIgnoreCase) && !HasBackofficeAccess(user))
            return null;

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(TokenBytes));
        var tokenHash = HashToken(token);
        var expiresAt = DateTime.UtcNow.AddHours(12);
        await connection.ExecuteAsync(@"INSERT INTO authsessions (UserId, TokenHash, IpAddress, UserAgent, ExpiresAt) VALUES (@UserId, @TokenHash, @IpAddress, @UserAgent, @ExpiresAt);
UPDATE authusers SET LastLoginAt = UTC_TIMESTAMP() WHERE Id = @UserId;", new { UserId = row.Id, TokenHash = tokenHash, IpAddress = ipAddress, UserAgent = userAgent, ExpiresAt = expiresAt });
        await WriteAuditAsync(connection, row.Id, row.Email, "auth.login", "AuthSession", "POST", "/api/auth/login", 200, ipAddress, userAgent, "{}");
        return new LoginResponse { Token = token, ExpiresAt = expiresAt, User = user };
    }

    private static readonly string[] BackofficePermissionCodes =
    [
        "security.manage",
        "settings.manage",
        "employees.view",
        "employees.manage",
        "employee.communication.view",
        "employee.communication.send",
        "payroll.run",
        "payroll.approve",
        "payroll.payments",
        "leave.manage",
        "attendance.manage",
        "tax.statutory.manage",
        "workflow.manage",
        "reports.view",
        "audit.view",
        "recruitment.manage",
        "recruitment.position.view",
        "recruitment.position.manage",
        "recruitment.assign.recruiter",
        "recruitment.assign.partner",
        "recruitment.publish",
        "recruitment.referral.manage",
        "recruitment.work-order.view",
        "recruitment.work-order.manage",
        "recruitment.hiring-case.view",
        "recruitment.hiring-case.manage",
        "recruitment.sla.pause",
        "recruitment.candidate.view",
        "recruitment.candidate.manage",
        "recruitment.candidate.request-profile",
        "recruitment.shortlist.approve",
        "recruitment.shortlist.forward",
        "recruitment.ats.review",
        "recruitment.ats.override",
        "recruitment.interview.schedule",
        "recruitment.interview.panel",
        "recruitment.document.view",
        "recruitment.document.manage",
        "recruitment.document.sign",
        "recruitment.proposal.manage",
        "recruitment.proposal.approve",
        "recruitment.offer.manage",
        "recruitment.offer.issue",
        "recruitment.configuration.manage",
        "attachment.config.manage",
        "attachment.employee.view",
        "attachment.employee.upload",
        "attachment.employee.verify",
        "attachment.recruitment.view",
        "attachment.recruitment.upload",
        "attachment.recruitment.verify"
    ];

    public static bool HasBackofficeAccess(AuthUser user) =>
        user.Permissions.Any(permission => BackofficePermissionCodes.Contains(permission, StringComparer.OrdinalIgnoreCase));

    public async Task<AuthUser?> GetUserByTokenAsync(string token)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        var userId = await connection.ExecuteScalarAsync<int?>(@"SELECT UserId FROM authsessions WHERE TokenHash = @TokenHash AND RevokedAt IS NULL AND ExpiresAt > UTC_TIMESTAMP()", new { TokenHash = HashToken(token) });
        return userId is null ? null : await BuildUserAsync(connection, userId.Value);
    }

    public async Task LogoutAsync(string token, AuthUser? user, string ipAddress, string userAgent)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("UPDATE authsessions SET RevokedAt = UTC_TIMESTAMP() WHERE TokenHash = @TokenHash AND RevokedAt IS NULL", new { TokenHash = HashToken(token) });
        await WriteAuditAsync(connection, user?.Id, user?.Email ?? "", "auth.logout", "AuthSession", "POST", "/api/auth/logout", 200, ipAddress, userAgent, "{}");
    }

    public async Task<IEnumerable<AuthUser>> GetUsersAsync()
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        var users = (await connection.QueryAsync<AuthUser>(@"SELECT Id, Email, DisplayName, Mobile, ClientId, EmployeeId, IsActive, MustChangePassword
FROM authusers
ORDER BY DisplayName;")).ToList();
        var roleRows = await connection.QueryAsync<UserSecurityCodeRow>(@"SELECT ur.UserId, r.Code
FROM authuserroles ur
JOIN authroles r ON r.Id = ur.RoleId
ORDER BY ur.UserId, r.Code;");
        var permissionRows = await connection.QueryAsync<UserSecurityCodeRow>(@"SELECT DISTINCT ur.UserId, p.Code
FROM authuserroles ur
JOIN authrolepermissions rp ON rp.RoleId = ur.RoleId
JOIN authpermissions p ON p.Id = rp.PermissionId
ORDER BY ur.UserId, p.Code;");
        var rolesByUser = roleRows.ToLookup(row => row.UserId, row => row.Code);
        var permissionsByUser = permissionRows.ToLookup(row => row.UserId, row => row.Code);
        foreach (var user in users)
        {
            user.Roles = rolesByUser[user.Id].ToList();
            user.Permissions = permissionsByUser[user.Id].ToList();
            user.DashboardAccess = BuildDashboardAccess(user.Permissions);
            user.DefaultDashboardCode = user.DashboardAccess.FirstOrDefault()?.Code ?? string.Empty;
        }
        return users;
    }

    private sealed class UserSecurityCodeRow
    {
        public int UserId { get; set; }
        public string Code { get; set; } = string.Empty;
    }

    public async Task<IEnumerable<AuthRole>> GetRolesAsync()
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await SeedSecurityCatalogAsync(connection);
        return await connection.QueryAsync<AuthRole>(@"SELECT r.Id, r.Code, r.Name, r.Description, r.IsSystem, COALESCE(GROUP_CONCAT(p.Code ORDER BY p.Code), '') AS Permissions
FROM authroles r
LEFT JOIN authrolepermissions rp ON rp.RoleId = r.Id
LEFT JOIN authpermissions p ON p.Id = rp.PermissionId
GROUP BY r.Id
ORDER BY r.Name;");
    }

    public async Task<IEnumerable<AuditLog>> GetAuditLogsAsync(int limit = 100)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        return await connection.QueryAsync<AuditLog>("SELECT * FROM auditlogs ORDER BY CreatedAt DESC LIMIT @Limit", new { Limit = Math.Clamp(limit, 1, 500) });
    }

    public async Task WriteAuditAsync(AuthUser? user, string action, string resource, string method, string path, int statusCode, string ipAddress, string userAgent, string detailsJson = "{}")
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await WriteAuditAsync(connection, user?.Id, user?.Email ?? "", action, resource, method, path, statusCode, ipAddress, userAgent, detailsJson);
    }

    private static async Task<AuthUser?> BuildUserAsync(MySqlConnection connection, int userId)
    {
        var user = await connection.QueryFirstOrDefaultAsync<AuthUser>("SELECT Id, Email, DisplayName, Mobile, ClientId, EmployeeId, IsActive, MustChangePassword FROM authusers WHERE Id = @UserId", new { UserId = userId });
        if (user is null) return null;
        user.Roles = (await connection.QueryAsync<string>(@"SELECT r.Code FROM authroles r JOIN authuserroles ur ON ur.RoleId = r.Id WHERE ur.UserId = @UserId ORDER BY r.Code", new { UserId = userId })).ToList();
        user.Permissions = (await connection.QueryAsync<string>(@"SELECT DISTINCT p.Code FROM authpermissions p JOIN authrolepermissions rp ON rp.PermissionId = p.Id JOIN authuserroles ur ON ur.RoleId = rp.RoleId WHERE ur.UserId = @UserId ORDER BY p.Code", new { UserId = userId })).ToList();
        user.DashboardAccess = BuildDashboardAccess(user.Permissions);
        user.DefaultDashboardCode = user.DashboardAccess.FirstOrDefault()?.Code ?? string.Empty;
        return user;
    }

    private sealed record DashboardAccessRule(string Code, string Name, string Description, string Route, int SortOrder, string[] PermissionHints);

    private static readonly DashboardAccessRule[] DashboardCatalog =
    [
        new("overview", "Overview Dashboard", "Combined HR, payroll, attendance and approval summary.", "/dashboard", 10, ["dashboard.view", "security.manage"]),
        new("workforce", "Workforce Dashboard", "Employee strength, ESS adoption and workforce movement.", "/dashboard/workforce", 20, ["dashboard.workforce.view", "employees.view", "employees.manage", "security.manage"]),
        new("payroll", "Payroll Dashboard", "Pay run status, payroll cost and recent run activity.", "/dashboard/payroll", 30, ["dashboard.payroll.view", "payroll.run", "payroll.approve", "payroll.payments", "security.manage"]),
        new("attendance", "Attendance Dashboard", "Attendance readiness, exceptions and leave blockers.", "/dashboard/attendance", 40, ["dashboard.attendance.view", "leave.manage", "attendance.manage", "settings.manage", "security.manage"]),
        new("approvals", "Approvals Dashboard", "Pending workflow tasks and approval workload.", "/dashboard/approvals", 50, ["dashboard.approvals.view", "workflow.manage", "payroll.approve", "security.manage"])
    ];

    private static List<DashboardAccessItem> BuildDashboardAccess(IEnumerable<string> permissions)
    {
        var granted = permissions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return DashboardCatalog
            .Where(rule => rule.PermissionHints.Any(granted.Contains))
            .OrderBy(rule => rule.SortOrder)
            .Select(rule => new DashboardAccessItem
            {
                Code = rule.Code,
                Name = rule.Name,
                Description = rule.Description,
                Route = rule.Route,
                SortOrder = rule.SortOrder
            })
            .ToList();
    }

    private static Task WriteAuditAsync(MySqlConnection connection, int? userId, string userEmail, string action, string resource, string method, string path, int statusCode, string ipAddress, string userAgent, string detailsJson) =>
        connection.ExecuteAsync(@"INSERT INTO auditlogs (UserId, UserEmail, Action, Resource, Method, Path, StatusCode, IpAddress, UserAgent, DetailsJson)
VALUES (@UserId, @UserEmail, @Action, @Resource, @Method, @Path, @StatusCode, @IpAddress, @UserAgent, @DetailsJson);", new { UserId = userId, UserEmail = userEmail, Action = action, Resource = resource, Method = method, Path = path, StatusCode = statusCode, IpAddress = ipAddress, UserAgent = userAgent, DetailsJson = detailsJson });

    public async Task<IEnumerable<AuthPermission>> GetPermissionsAsync()
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await SeedSecurityCatalogAsync(connection);
        return await connection.QueryAsync<AuthPermission>("SELECT * FROM authpermissions ORDER BY Module, Code");
    }

    public async Task<IEnumerable<EmployeeLoginProvisionPreview>> GetEmployeeProvisionPreviewAsync(int? clientId = null)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        return await connection.QueryAsync<EmployeeLoginProvisionPreview>(@"
SELECT
    e.Id AS EmployeeId,
    e.ClientId,
    COALESCE(c.Name, '') AS ClientName,
    e.EmployeeCode,
    TRIM(CONCAT(COALESCE(e.FirstName, ''), ' ', COALESCE(e.LastName, ''))) AS EmployeeName,
    e.WorkEmail,
    COALESCE(JSON_UNQUOTE(JSON_EXTRACT(e.PersonalJson, '$.aadhaarNumber')), JSON_UNQUOTE(JSON_EXTRACT(e.PersonalJson, '$.AadhaarNumber')), '') AS AadhaarNumber,
    e.Department,
    e.Designation
FROM employees e
LEFT JOIN clients c ON c.Id = e.ClientId
WHERE e.IsActive = TRUE
  AND (c.Id IS NULL OR c.IsActive = TRUE)
  AND NULLIF(TRIM(e.EmployeeCode), '') IS NOT NULL
  AND (@ClientId IS NULL OR @ClientId = 0 OR e.ClientId = @ClientId)
  AND NOT EXISTS (
      SELECT 1
      FROM authusers u
      WHERE u.EmployeeId = e.Id
         OR LOWER(TRIM(u.Email)) = LOWER(TRIM(e.EmployeeCode))
  )
ORDER BY c.Name, e.FirstName, e.LastName, e.EmployeeCode;", new { ClientId = clientId.GetValueOrDefault() });
    }

    public async Task<ProvisionEmployeeLoginsResponse> ProvisionEmployeeLoginsAsync(ProvisionEmployeeLoginsRequest request)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await SeedSecurityCatalogAsync(connection);
        await using var transaction = await connection.BeginTransactionAsync();

        var response = new ProvisionEmployeeLoginsResponse();
        var employeeIds = request.EmployeeIds
            .Where(id => id > 0)
            .Distinct()
            .ToArray();
        var roles = request.Roles
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .Select(role => role.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .DefaultIfEmpty("employee")
            .ToArray();
        var fixedTemporaryPassword = string.IsNullOrWhiteSpace(request.TemporaryPassword)
            ? ""
            : request.TemporaryPassword.Trim();
        response.TemporaryPassword = fixedTemporaryPassword;

        if (employeeIds.Length == 0)
        {
            await transaction.CommitAsync();
            return response;
        }

        var employees = (await connection.QueryAsync<EmployeeLoginProvisionPreview>(@"
SELECT
    e.Id AS EmployeeId,
    e.ClientId,
    COALESCE(c.Name, '') AS ClientName,
    e.EmployeeCode,
    TRIM(CONCAT(COALESCE(e.FirstName, ''), ' ', COALESCE(e.LastName, ''))) AS EmployeeName,
    e.WorkEmail,
    COALESCE(JSON_UNQUOTE(JSON_EXTRACT(e.PersonalJson, '$.aadhaarNumber')), JSON_UNQUOTE(JSON_EXTRACT(e.PersonalJson, '$.AadhaarNumber')), '') AS AadhaarNumber,
    e.Department,
    e.Designation
FROM employees e
LEFT JOIN clients c ON c.Id = e.ClientId
WHERE e.Id IN @EmployeeIds
ORDER BY c.Name, e.FirstName, e.LastName, e.EmployeeCode;", new { EmployeeIds = employeeIds }, transaction)).ToList();

        foreach (var employee in employees)
        {
            var result = new ProvisionEmployeeLoginResult
            {
                EmployeeId = employee.EmployeeId,
                EmployeeCode = employee.EmployeeCode,
                EmployeeName = employee.EmployeeName,
                Email = NormalizeLoginId(employee.EmployeeCode)
            };

            if (string.IsNullOrWhiteSpace(employee.EmployeeCode))
            {
                result.Status = "Skipped";
                result.Message = "Employee code is missing.";
                response.Results.Add(result);
                continue;
            }

            var existingUser = await connection.QueryFirstOrDefaultAsync<(int Id, int? EmployeeId)>(
                "SELECT Id, EmployeeId FROM authusers WHERE LOWER(TRIM(Email)) = LOWER(TRIM(@Email)) OR EmployeeId = @EmployeeId LIMIT 1",
                new { Email = result.Email, employee.EmployeeId },
                transaction);
            if (existingUser.Id > 0)
            {
                result.UserId = existingUser.Id;
                result.Status = "Skipped";
                result.Message = existingUser.EmployeeId == employee.EmployeeId
                    ? "Employee already has a login."
                    : "Login ID already exists.";
                response.Results.Add(result);
                continue;
            }

            var displayName = string.IsNullOrWhiteSpace(employee.EmployeeName) ? employee.EmployeeCode : employee.EmployeeName;
            var temporaryPassword = await ResolveInitialPasswordAsync(connection, employee, fixedTemporaryPassword, transaction);
            var userId = (int)await connection.ExecuteScalarAsync<long>(@"
INSERT INTO authusers (Email, DisplayName, PasswordHash, ClientId, EmployeeId, IsActive, MustChangePassword)
VALUES (@Email, @DisplayName, @PasswordHash, @ClientId, @EmployeeId, TRUE, @MustChangePassword);
SELECT LAST_INSERT_ID();", new
            {
                Email = result.Email,
                DisplayName = displayName,
                PasswordHash = HashPassword(temporaryPassword),
                employee.ClientId,
                employee.EmployeeId,
                request.MustChangePassword
            }, transaction);

            await connection.ExecuteAsync(@"
INSERT IGNORE INTO authuserroles (UserId, RoleId)
SELECT @UserId, Id FROM authroles WHERE Code IN @Roles;", new { UserId = userId, Roles = roles }, transaction);

            var assignedRoleCount = await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM authuserroles WHERE UserId = @UserId",
                new { UserId = userId },
                transaction);
            if (assignedRoleCount == 0)
            {
                await connection.ExecuteAsync(@"
INSERT IGNORE INTO authuserroles (UserId, RoleId)
SELECT @UserId, Id FROM authroles WHERE Code = 'employee';", new { UserId = userId }, transaction);
            }

            result.UserId = userId;
            result.Status = "Created";
            result.Message = "Login created.";
            response.Results.Add(result);
        }

        var missingIds = employeeIds.Except(employees.Select(employee => employee.EmployeeId)).ToArray();
        foreach (var employeeId in missingIds)
        {
            response.Results.Add(new ProvisionEmployeeLoginResult
            {
                EmployeeId = employeeId,
                Status = "Skipped",
                Message = "Employee not found."
            });
        }

        await transaction.CommitAsync();
        return response;
    }

    public async Task<EmployeeLoginProvisionResult> EnsureEmployeeLoginAsync(int employeeId, bool resetExistingPassword = false)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await SeedSecurityCatalogAsync(connection);
        await using var transaction = await connection.BeginTransactionAsync();
        var employee = await connection.QueryFirstOrDefaultAsync<EmployeeLoginProvisionPreview>(@"
SELECT
    e.Id AS EmployeeId,
    e.ClientId,
    COALESCE(c.Name, '') AS ClientName,
    e.EmployeeCode,
    TRIM(CONCAT(COALESCE(e.FirstName, ''), ' ', COALESCE(e.LastName, ''))) AS EmployeeName,
    e.WorkEmail,
    COALESCE(JSON_UNQUOTE(JSON_EXTRACT(e.PersonalJson, '$.aadhaarNumber')), JSON_UNQUOTE(JSON_EXTRACT(e.PersonalJson, '$.AadhaarNumber')), '') AS AadhaarNumber,
    e.Department,
    e.Designation
FROM employees e
LEFT JOIN clients c ON c.Id = e.ClientId
WHERE e.Id=@EmployeeId AND e.IsActive=TRUE;", new { EmployeeId = employeeId }, transaction);
        var result = new EmployeeLoginProvisionResult { EmployeeId = employeeId };
        if (employee is null)
        {
            result.Status = "Skipped";
            result.Message = "Employee not found or inactive.";
            await transaction.CommitAsync();
            return result;
        }

        result.ClientId = employee.ClientId;
        result.EmployeeCode = employee.EmployeeCode;
        result.EmployeeName = employee.EmployeeName;
        result.Email = NormalizeLoginId(employee.EmployeeCode);
        result.NotificationEmail = NormalizeEmail(employee.WorkEmail);
        if (string.IsNullOrWhiteSpace(result.Email))
        {
            result.Status = "Skipped";
            result.Message = "Employee code is missing.";
            await transaction.CommitAsync();
            return result;
        }

        var displayName = string.IsNullOrWhiteSpace(employee.EmployeeName) ? employee.EmployeeCode : employee.EmployeeName;
        var temporaryPassword = await ResolveInitialPasswordAsync(connection, employee, transaction: transaction);
        var existingUser = await connection.QueryFirstOrDefaultAsync<(int Id, int? EmployeeId)>(
            "SELECT Id, EmployeeId FROM authusers WHERE LOWER(TRIM(Email)) = LOWER(TRIM(@Email)) OR EmployeeId = @EmployeeId LIMIT 1",
            new { Email = result.Email, employee.EmployeeId },
            transaction);
        if (existingUser.Id > 0)
        {
            result.UserId = existingUser.Id;
            if (resetExistingPassword)
            {
                await connection.ExecuteAsync(@"UPDATE authusers
SET Email=@Email, DisplayName=@DisplayName, ClientId=@ClientId, EmployeeId=@EmployeeId, IsActive=TRUE, PasswordHash=@PasswordHash, MustChangePassword=TRUE
WHERE Id=@Id", new { Id = existingUser.Id, Email = result.Email, DisplayName = displayName, employee.ClientId, employee.EmployeeId, PasswordHash = HashPassword(temporaryPassword) }, transaction);
                result.Status = "Reset";
                result.Message = "Existing login reset.";
                result.TemporaryPassword = temporaryPassword;
            }
            else
            {
                await connection.ExecuteAsync(@"UPDATE authusers
SET Email=@Email, DisplayName=@DisplayName, ClientId=@ClientId, EmployeeId=@EmployeeId, IsActive=TRUE
WHERE Id=@Id", new { Id = existingUser.Id, Email = result.Email, DisplayName = displayName, employee.ClientId, employee.EmployeeId }, transaction);
                result.Status = "Existing";
                result.Message = "Existing login enabled/linked.";
            }
        }
        else
        {
            var userId = (int)await connection.ExecuteScalarAsync<long>(@"
INSERT INTO authusers (Email, DisplayName, PasswordHash, ClientId, EmployeeId, IsActive, MustChangePassword)
VALUES (@Email, @DisplayName, @PasswordHash, @ClientId, @EmployeeId, TRUE, TRUE);
SELECT LAST_INSERT_ID();", new
            {
                Email = result.Email,
                DisplayName = displayName,
                PasswordHash = HashPassword(temporaryPassword),
                employee.ClientId,
                employee.EmployeeId
            }, transaction);
            result.UserId = userId;
            result.Status = "Created";
            result.Message = "Login created.";
            result.TemporaryPassword = temporaryPassword;
        }

        if (result.UserId is not null)
            await connection.ExecuteAsync(@"INSERT IGNORE INTO authuserroles (UserId, RoleId)
SELECT @UserId, Id FROM authroles WHERE Code = 'employee';", new { UserId = result.UserId.Value }, transaction);

        await transaction.CommitAsync();
        return result;
    }

    public async Task<(AuthUser? User, string? Error)> ChangePasswordAsync(int userId, string currentPassword, string newPassword)
    {
        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Trim().Length < 8)
            return (null, "New password must be at least 8 characters.");
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        var row = await connection.QueryFirstOrDefaultAsync<AuthUserRecord>("SELECT * FROM authusers WHERE Id=@UserId AND IsActive=TRUE", new { UserId = userId });
        if (row is null) return (null, "User was not found.");
        if (!VerifyPassword(currentPassword, row.PasswordHash)) return (null, "Current password is incorrect.");
        await connection.ExecuteAsync("UPDATE authusers SET PasswordHash=@PasswordHash, MustChangePassword=FALSE WHERE Id=@UserId", new { UserId = userId, PasswordHash = HashPassword(newPassword.Trim()) });
        return (await BuildUserAsync(connection, userId), null);
    }

    public async Task<AuthUser?> SaveUserAsync(SaveAuthUserRequest request)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await SeedSecurityCatalogAsync(connection);
        await using var transaction = await connection.BeginTransactionAsync();
        var email = NormalizeEmail(request.Email);
        var userId = request.Id;
        if (userId == 0)
        {
            userId = (int)await connection.ExecuteScalarAsync<long>(@"INSERT INTO authusers (Email, DisplayName, Mobile, PasswordHash, ClientId, EmployeeId, IsActive, MustChangePassword)
VALUES (@Email, @DisplayName, @Mobile, @PasswordHash, @ClientId, @EmployeeId, @IsActive, @MustChangePassword);
SELECT LAST_INSERT_ID();", new { Email = email, request.DisplayName, Mobile = request.Mobile.Trim(), PasswordHash = HashPassword(request.Password), request.ClientId, request.EmployeeId, request.IsActive, MustChangePassword = request.MustChangePassword ?? true }, transaction);
        }
        else
        {
            await connection.ExecuteAsync(@"UPDATE authusers SET Email=@Email, DisplayName=@DisplayName, Mobile=@Mobile, ClientId=@ClientId, EmployeeId=@EmployeeId, IsActive=@IsActive WHERE Id=@Id", new { Id = userId, Email = email, request.DisplayName, Mobile = request.Mobile.Trim(), request.ClientId, request.EmployeeId, request.IsActive }, transaction);
            if (!string.IsNullOrWhiteSpace(request.Password))
                await connection.ExecuteAsync("UPDATE authusers SET PasswordHash=@PasswordHash, MustChangePassword=@MustChangePassword WHERE Id=@Id", new { Id = userId, PasswordHash = HashPassword(request.Password), MustChangePassword = request.MustChangePassword ?? true }, transaction);
            else if (request.MustChangePassword.HasValue)
                await connection.ExecuteAsync("UPDATE authusers SET MustChangePassword=@MustChangePassword WHERE Id=@Id", new { Id = userId, MustChangePassword = request.MustChangePassword.Value }, transaction);
        }

        await connection.ExecuteAsync("DELETE FROM authuserroles WHERE UserId=@UserId", new { UserId = userId }, transaction);
        if (request.Roles.Count > 0)
            await connection.ExecuteAsync(@"INSERT IGNORE INTO authuserroles (UserId, RoleId)
SELECT @UserId, Id FROM authroles WHERE Code IN @Roles;", new { UserId = userId, request.Roles }, transaction);
        await transaction.CommitAsync();
        return await GetUserByIdAsync(userId);
    }

    public async Task<AuthRole?> SaveRoleAsync(SaveAuthRoleRequest request)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await SeedSecurityCatalogAsync(connection);
        await using var transaction = await connection.BeginTransactionAsync();
        var code = request.Code.Trim().ToLowerInvariant().Replace(' ', '_');
        var roleId = request.Id;
        if (roleId == 0)
            roleId = (int)await connection.ExecuteScalarAsync<long>(@"INSERT INTO authroles (Code, Name, Description, IsSystem) VALUES (@Code, @Name, @Description, FALSE);
SELECT LAST_INSERT_ID();", new { Code = code, request.Name, request.Description }, transaction);
        else
        {
            var isSystem = await connection.ExecuteScalarAsync<bool>("SELECT IsSystem FROM authroles WHERE Id=@Id", new { Id = roleId }, transaction);
            if (!isSystem)
                await connection.ExecuteAsync("UPDATE authroles SET Name=@Name, Description=@Description WHERE Id=@Id", new { Id = roleId, request.Name, request.Description }, transaction);
        }
        await connection.ExecuteAsync("DELETE FROM authrolepermissions WHERE RoleId=@RoleId", new { RoleId = roleId }, transaction);
        if (request.Permissions.Count > 0)
            await connection.ExecuteAsync(@"INSERT IGNORE INTO authrolepermissions (RoleId, PermissionId)
SELECT @RoleId, Id FROM authpermissions WHERE Code IN @Permissions;", new { RoleId = roleId, request.Permissions }, transaction);
        await transaction.CommitAsync();
        return (await GetRolesAsync()).FirstOrDefault(role => role.Id == roleId);
    }

    public async Task<bool> DeleteUserAsync(int id)
    {
        if (id <= 0) return false;
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await SeedSecurityCatalogAsync(connection);
        await using var transaction = await connection.BeginTransactionAsync();

        var exists = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM authusers WHERE Id=@Id", new { Id = id }, transaction);
        if (exists == 0)
        {
            await transaction.RollbackAsync();
            return false;
        }

        var targetIsSecurityAdmin = await connection.ExecuteScalarAsync<int>(@"
SELECT COUNT(DISTINCT u.Id)
FROM authusers u
JOIN authuserroles ur ON ur.UserId = u.Id
JOIN authrolepermissions rp ON rp.RoleId = ur.RoleId
JOIN authpermissions p ON p.Id = rp.PermissionId
WHERE u.Id = @Id AND u.IsActive = TRUE AND p.Code = 'security.manage';", new { Id = id }, transaction);
        if (targetIsSecurityAdmin > 0)
        {
            var remainingSecurityAdmins = await connection.ExecuteScalarAsync<int>(@"
SELECT COUNT(DISTINCT u.Id)
FROM authusers u
JOIN authuserroles ur ON ur.UserId = u.Id
JOIN authrolepermissions rp ON rp.RoleId = ur.RoleId
JOIN authpermissions p ON p.Id = rp.PermissionId
WHERE u.Id <> @Id AND u.IsActive = TRUE AND p.Code = 'security.manage';", new { Id = id }, transaction);
            if (remainingSecurityAdmins == 0)
                throw new InvalidOperationException("At least one active security administrator is required.");
        }

        await connection.ExecuteAsync("DELETE FROM authsessions WHERE UserId=@Id", new { Id = id }, transaction);
        await connection.ExecuteAsync("DELETE FROM authuserroles WHERE UserId=@Id", new { Id = id }, transaction);
        var affected = await connection.ExecuteAsync("DELETE FROM authusers WHERE Id=@Id", new { Id = id }, transaction);
        await transaction.CommitAsync();
        return affected > 0;
    }

    public async Task<bool> DeleteRoleAsync(int id)
    {
        if (id <= 0) return false;
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await SeedSecurityCatalogAsync(connection);
        await using var transaction = await connection.BeginTransactionAsync();

        var exists = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM authroles WHERE Id=@Id", new { Id = id }, transaction);
        if (exists == 0)
        {
            await transaction.RollbackAsync();
            return false;
        }

        var isSystem = await connection.ExecuteScalarAsync<bool>("SELECT IsSystem FROM authroles WHERE Id=@Id", new { Id = id }, transaction);
        if (isSystem)
            throw new InvalidOperationException("System role is protected by the security catalog and cannot be deleted.");

        var assignedUsers = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM authuserroles WHERE RoleId=@Id", new { Id = id }, transaction);
        if (assignedUsers > 0)
        {
            var linkedUsers = (await connection.QueryAsync<string>(@"
SELECT COALESCE(NULLIF(DisplayName, ''), Email)
FROM authusers u
JOIN authuserroles ur ON ur.UserId = u.Id
WHERE ur.RoleId = @Id
ORDER BY DisplayName
LIMIT 5;", new { Id = id }, transaction)).ToList();
            var sample = linkedUsers.Count > 0 ? $": {string.Join(", ", linkedUsers)}{(assignedUsers > linkedUsers.Count ? "..." : "")}" : "";
            throw new InvalidOperationException($"Role is linked with {assignedUsers} user{(assignedUsers == 1 ? "" : "s")}{sample} and cannot be deleted.");
        }

        await connection.ExecuteAsync("DELETE FROM authrolepermissions WHERE RoleId=@Id", new { Id = id }, transaction);
        var affected = await connection.ExecuteAsync("DELETE FROM authroles WHERE Id=@Id AND IsSystem=FALSE", new { Id = id }, transaction);
        await transaction.CommitAsync();
        return affected > 0;
    }

    private async Task<AuthUser?> GetUserByIdAsync(int userId)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        return await BuildUserAsync(connection, userId);
    }

    private static async Task EnsureColumnAsync(MySqlConnection connection, string tableName, string columnName, string definition)
    {
        var exists = await connection.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @TableName AND COLUMN_NAME = @ColumnName", new { TableName = tableName, ColumnName = columnName });
        if (exists == 0) await connection.ExecuteAsync($"ALTER TABLE `{tableName}` ADD COLUMN `{columnName}` {definition}");
    }

    private static async Task SeedSecurityCatalogAsync(MySqlConnection connection)
    {
        var existingAttendancePermissions = (await connection.QueryAsync<string>(@"
SELECT Code
FROM authpermissions
WHERE Code IN ('mss.attendance.manage', 'ess.attendance.mark');")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mssAttendancePermissionIsNew = !existingAttendancePermissions.Contains("mss.attendance.manage");
        var attendanceMarkPermissionIsNew = !existingAttendancePermissions.Contains("ess.attendance.mark");
        var permissions = new[]
        {
            new { Code = "dashboard.view", Name = "View dashboard", Module = "Dashboard", Description = "Access the HRMS dashboard shell." },
            new { Code = "dashboard.workforce.view", Name = "View workforce dashboard", Module = "Dashboard", Description = "View employee and ESS adoption dashboard metrics." },
            new { Code = "dashboard.payroll.view", Name = "View payroll dashboard", Module = "Dashboard", Description = "View payroll run, net pay, validation and recent payroll dashboard metrics." },
            new { Code = "dashboard.attendance.view", Name = "View attendance dashboard", Module = "Dashboard", Description = "View attendance readiness and exception dashboard metrics." },
            new { Code = "dashboard.approvals.view", Name = "View approvals dashboard", Module = "Dashboard", Description = "View workflow and leave approval dashboard metrics." },
            new { Code = "employees.view", Name = "View employee master", Module = "Employees", Description = "Open employee master and employee reports." },
            new { Code = "employees.manage", Name = "Manage employees", Module = "Employees", Description = "Create, update and maintain employee master data." },
            new { Code = "employee.communication.view", Name = "View employee communication", Module = "Employees", Description = "Open employee communication campaigns, delivery history and conversations." },
            new { Code = "employee.communication.send", Name = "Send employee communication", Module = "Employees", Description = "Preview, send, reply to and retry employee communications." },
            new { Code = "payroll.run", Name = "Run payroll", Module = "Payroll", Description = "Create payroll runs and manage payroll inputs." },
            new { Code = "payroll.approve", Name = "Approve payroll", Module = "Payroll", Description = "Approve, recall and review payroll runs." },
            new { Code = "payroll.payments", Name = "Record payroll payments", Module = "Payroll", Description = "Mark payroll payments and payment dates." },
            new { Code = "leave.manage", Name = "Manage leave", Module = "Leave & Attendance", Description = "Configure and process leave records." },
            new { Code = "attendance.manage", Name = "Manage attendance", Module = "Leave & Attendance", Description = "Configure attendance and review attendance data." },
            new { Code = "mss.attendance.manage", Name = "Manage direct-report attendance in MSS", Module = "MSS", Description = "Review and correct attendance only for active employees who directly report to the signed-in manager." },
            new { Code = "mss.attendance.client.manage", Name = "Manage client attendance in MSS", Module = "MSS", Description = "Review and correct attendance for all active employees of the user's assigned client." },
            new { Code = "ess.attendance.mark", Name = "Mark attendance in ESS", Module = "ESS", Description = "Show and use attendance punch actions in ESS web and mobile apps." },
            new { Code = "settings.manage", Name = "Manage settings", Module = "Settings", Description = "Configure organization, clients, masters and setup data." },
            new { Code = "attachment.config.manage", Name = "Manage attachment configuration", Module = "Attachments", Description = "Configure attachment attributes, form fields and storage servers." },
            new { Code = "attachment.employee.view", Name = "View employee attachments", Module = "Attachments", Description = "View and download employee attachments." },
            new { Code = "attachment.employee.upload", Name = "Upload employee attachments", Module = "Attachments", Description = "Upload, replace and delete employee attachments." },
            new { Code = "attachment.employee.verify", Name = "Verify employee attachments", Module = "Attachments", Description = "Verify or reject employee attachments." },
            new { Code = "attachment.recruitment.view", Name = "View recruitment attachments", Module = "Attachments", Description = "View candidate and recruitment attachments." },
            new { Code = "attachment.recruitment.upload", Name = "Upload recruitment attachments", Module = "Attachments", Description = "Upload and maintain candidate and recruitment attachments." },
            new { Code = "attachment.recruitment.verify", Name = "Verify recruitment attachments", Module = "Attachments", Description = "Verify or reject candidate and recruitment attachments." },
            new { Code = "tax.statutory.manage", Name = "Manage statutory tax", Module = "Settings", Description = "Maintain statutory and income tax rules." },
            new { Code = "workflow.manage", Name = "Manage workflows", Module = "Workflows", Description = "Configure approval workflows and department heads." },
            new { Code = "recruitment.manage", Name = "Manage recruitment", Module = "Talent Acquisition", Description = "Monitor recruitment requisitions and open positions." },
            new { Code = "recruitment.position.view", Name = "View recruitment positions", Module = "Talent Acquisition", Description = "View recruitment open positions and workspace." },
            new { Code = "recruitment.position.manage", Name = "Manage recruitment positions", Module = "Talent Acquisition", Description = "Manage recruitment open position operations." },
            new { Code = "recruitment.assign.recruiter", Name = "Assign recruiter", Module = "Talent Acquisition", Description = "Assign or reassign recruiters to open positions." },
            new { Code = "recruitment.assign.partner", Name = "Assign recruitment partners", Module = "Talent Acquisition", Description = "Assign vendors and consultants to open positions." },
            new { Code = "recruitment.publish", Name = "Publish job", Module = "Talent Acquisition", Description = "Publish open positions to configured channels." },
            new { Code = "recruitment.referral.manage", Name = "Manage referral campaigns", Module = "Talent Acquisition", Description = "Create and manage employee referral campaigns." },
            new { Code = "recruitment.rfr.create", Name = "Create recruitment requisition", Module = "Talent Acquisition", Description = "Create recruitment requisitions on behalf of the organization." },
            new { Code = "recruitment.rfr.view", Name = "View recruitment requisitions", Module = "Talent Acquisition", Description = "View recruitment requisitions for permitted scope." },
            new { Code = "recruitment.work-order.view", Name = "View work orders", Module = "Talent Acquisition", Description = "View client work orders and their position lines." },
            new { Code = "recruitment.work-order.manage", Name = "Manage work orders", Module = "Talent Acquisition", Description = "Create and maintain manual client work orders and linked position lines." },
            new { Code = "recruitment.hiring-case.view", Name = "View hiring cases", Module = "Talent Acquisition", Description = "View position-level recruitment timelines and cumulative SLA." },
            new { Code = "recruitment.hiring-case.manage", Name = "Manage hiring cases", Module = "Talent Acquisition", Description = "Start and move position-level hiring cases through configured stages." },
            new { Code = "recruitment.sla.pause", Name = "Pause recruitment SLA", Module = "Talent Acquisition", Description = "Pause and resume a recruitment SLA with a mandatory audited reason." },
            new { Code = "recruitment.candidate.view", Name = "View recruitment candidates", Module = "Talent Acquisition", Description = "View candidate profiles within permitted client or panel scope." },
            new { Code = "recruitment.candidate.manage", Name = "Manage recruitment candidates", Module = "Talent Acquisition", Description = "Edit candidate profiles and structured profile sections." },
            new { Code = "recruitment.candidate.request-profile", Name = "Request candidate details", Module = "Talent Acquisition", Description = "Send a secure candidate-specific information update request." },
            new { Code = "recruitment.shortlist.approve", Name = "Approve shortlisted profiles", Module = "Talent Acquisition", Description = "Approve a complete candidate profile batch before it can be shared with the client." },
            new { Code = "recruitment.shortlist.forward", Name = "Forward shortlisted profiles", Module = "Talent Acquisition", Description = "Create and forward an auditable batch of approved candidate profiles." },
            new { Code = "recruitment.ats.review", Name = "Review ATS results", Module = "Talent Acquisition", Description = "Review ATS scores and supporting evidence." },
            new { Code = "recruitment.ats.override", Name = "Override ATS score", Module = "Talent Acquisition", Description = "Override an ATS result with an audited reason." },
            new { Code = "recruitment.interview.schedule", Name = "Schedule interviews", Module = "Talent Acquisition", Description = "Schedule and manage interviews and candidate-specific panels." },
            new { Code = "recruitment.interview.panel", Name = "Interview panel workspace", Module = "Talent Acquisition", Description = "View assigned interviews and submit feedback as the signed-in panel member." },
            new { Code = "recruitment.document.view", Name = "View recruitment process documents", Module = "Talent Acquisition", Description = "View MoM, score annexure, proposal and joining documents." },
            new { Code = "recruitment.document.manage", Name = "Manage recruitment process documents", Module = "Talent Acquisition", Description = "Generate, upload and version recruitment process documents." },
            new { Code = "recruitment.document.sign", Name = "Sign recruitment documents", Module = "Talent Acquisition", Description = "Mark approved recruitment documents as signed with an audit trail." },
            new { Code = "recruitment.proposal.manage", Name = "Manage HR proposals", Module = "Talent Acquisition", Description = "Prepare and submit candidate proposals to the client HR division." },
            new { Code = "recruitment.proposal.approve", Name = "Approve HR proposals", Module = "Talent Acquisition", Description = "Approve, reject or send back recruitment proposals." },
            new { Code = "recruitment.offer.manage", Name = "Manage offers", Module = "Talent Acquisition", Description = "Prepare and maintain candidate offers." },
            new { Code = "recruitment.offer.issue", Name = "Issue offers", Module = "Talent Acquisition", Description = "Release an approved offer to the candidate." },
            new { Code = "recruitment.configuration.manage", Name = "Configure recruitment", Module = "Talent Acquisition", Description = "Configure client pipelines, forms, templates, panels, recipients and SLA rules." },
            new { Code = "reports.view", Name = "View reports", Module = "Reports", Description = "Open reports and exports." },
            new { Code = "security.manage", Name = "Manage security", Module = "Security", Description = "Manage users, roles and permissions." },
            new { Code = "audit.view", Name = "View audit logs", Module = "Security", Description = "View identity and operational audit logs." },
            new { Code = "ess.self", Name = "Employee self service", Module = "ESS", Description = "Access employee self-service profile, pay, leave, tax and attendance." }
        };

        await connection.ExecuteAsync(@"
INSERT INTO authpermissions (Code, Name, Module, Description)
VALUES (@Code, @Name, @Module, @Description)
ON DUPLICATE KEY UPDATE
    Name = VALUES(Name),
    Module = VALUES(Module),
    Description = VALUES(Description);", permissions);

        var roles = new[]
        {
            new { Code = "admin", Name = "Administrator", Description = "Full HRMS administration access.", IsSystem = true },
            new { Code = "employee", Name = "Employee", Description = "Employee self-service access.", IsSystem = true },
            new { Code = "mss_manager", Name = "MSS Manager", Description = "Manager self-service access for approvals and assigned workflow tasks.", IsSystem = true },
            new { Code = "client_attendance_operator", Name = "Client Attendance Operator", Description = "MSS attendance access for every active employee of the user's assigned client, without HRMS administration access.", IsSystem = true },
            new { Code = "payroll_maker", Name = "Payroll Maker", Description = "Payroll preparation and employee master operations.", IsSystem = true },
            new { Code = "payroll_approver", Name = "Payroll Approver", Description = "Payroll approval and review access.", IsSystem = true },
            new { Code = "hr_manager", Name = "HR Manager", Description = "HR, attendance, leave and employee operations.", IsSystem = true }
        };

        await connection.ExecuteAsync(@"
INSERT INTO authroles (Code, Name, Description, IsSystem)
VALUES (@Code, @Name, @Description, @IsSystem)
ON DUPLICATE KEY UPDATE
    Name = VALUES(Name),
    Description = VALUES(Description),
    IsSystem = VALUES(IsSystem);", roles);

        // Apply new capabilities to the standard roles once. Later role edits remain
        // authoritative, so removing either permission in Security survives restarts.
        if (attendanceMarkPermissionIsNew)
            await connection.ExecuteAsync(@"
INSERT IGNORE INTO authrolepermissions (RoleId, PermissionId)
SELECT r.Id, p.Id
FROM authroles r
JOIN authpermissions p ON p.Code = 'ess.attendance.mark'
WHERE r.Code IN ('employee', 'mss_manager');");
        if (mssAttendancePermissionIsNew)
            await connection.ExecuteAsync(@"
INSERT IGNORE INTO authrolepermissions (RoleId, PermissionId)
SELECT r.Id, p.Id
FROM authroles r
JOIN authpermissions p ON p.Code = 'mss.attendance.manage'
WHERE r.Code = 'mss_manager';");
        const string communicationGrantMigration = "20260728.employee-communication-default-role-grants.v2";
        var communicationRoleGrantsApplied = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM schema_migrations WHERE MigrationKey=@MigrationKey",
            new { MigrationKey = communicationGrantMigration }) > 0;
        if (!communicationRoleGrantsApplied)
        {
            await using var transaction = await connection.BeginTransactionAsync();
            await connection.ExecuteAsync(@"
INSERT IGNORE INTO authrolepermissions (RoleId, PermissionId)
SELECT r.Id, p.Id
FROM authroles r
JOIN authpermissions p ON p.Code IN ('employee.communication.view','employee.communication.send')
WHERE r.Code IN ('admin','hr_manager')
   OR EXISTS (
       SELECT 1
       FROM authrolepermissions existingGrant
       JOIN authpermissions existingPermission ON existingPermission.Id=existingGrant.PermissionId
       WHERE existingGrant.RoleId=r.Id AND existingPermission.Code='security.manage'
   );", transaction: transaction);
            await connection.ExecuteAsync(
                "INSERT INTO schema_migrations (MigrationKey) VALUES (@MigrationKey)",
                new { MigrationKey = communicationGrantMigration }, transaction);
            await transaction.CommitAsync();
        }

        var rolePermissions = new Dictionary<string, string[]>
        {
            ["admin"] = permissions.Select(permission => permission.Code).ToArray(),
            ["employee"] = ["ess.self", "ess.attendance.mark"],
            ["mss_manager"] = ["ess.self", "ess.attendance.mark", "mss.attendance.manage", "dashboard.approvals.view"],
            ["client_attendance_operator"] = ["mss.attendance.client.manage"],
            ["payroll_maker"] = ["dashboard.view", "dashboard.payroll.view", "dashboard.workforce.view", "employees.view", "employees.manage", "attachment.employee.view", "attachment.employee.upload", "payroll.run", "reports.view"],
            ["payroll_approver"] = ["dashboard.view", "dashboard.payroll.view", "payroll.approve", "reports.view"],
            ["hr_manager"] = ["dashboard.view", "dashboard.workforce.view", "dashboard.attendance.view", "employees.view", "employees.manage", "employee.communication.view", "employee.communication.send", "attachment.employee.view", "attachment.employee.upload", "attachment.employee.verify", "attachment.recruitment.view", "attachment.recruitment.upload", "attachment.recruitment.verify", "leave.manage", "attendance.manage", "workflow.manage", "recruitment.manage", "recruitment.position.view", "recruitment.position.manage", "recruitment.assign.recruiter", "recruitment.assign.partner", "recruitment.publish", "recruitment.referral.manage", "recruitment.rfr.create", "recruitment.rfr.view", "reports.view"]
        };

        foreach (var (roleCode, permissionCodes) in rolePermissions)
        {
            var existingPermissionCount = await connection.ExecuteScalarAsync<int>(@"
SELECT COUNT(*)
FROM authrolepermissions rp
JOIN authroles r ON r.Id = rp.RoleId
WHERE r.Code = @RoleCode;", new { RoleCode = roleCode });
            var hasRequiredPermission = roleCode switch
            {
                "admin" => await connection.ExecuteScalarAsync<int>(@"
SELECT COUNT(*)
FROM authrolepermissions rp
JOIN authroles r ON r.Id = rp.RoleId
JOIN authpermissions p ON p.Id = rp.PermissionId
WHERE r.Code = 'admin'
  AND p.Code = 'security.manage';") > 0,
                "employee" => await connection.ExecuteScalarAsync<int>(@"
SELECT COUNT(*)
FROM authrolepermissions rp
JOIN authroles r ON r.Id = rp.RoleId
JOIN authpermissions p ON p.Id = rp.PermissionId
WHERE r.Code = 'employee'
  AND p.Code = 'ess.self';") > 0,
                "mss_manager" => await connection.ExecuteScalarAsync<int>(@"
SELECT COUNT(*)
FROM authrolepermissions rp
JOIN authroles r ON r.Id = rp.RoleId
JOIN authpermissions p ON p.Id = rp.PermissionId
WHERE r.Code = 'mss_manager'
  AND p.Code IN ('ess.self', 'dashboard.approvals.view');") >= 2,
                "client_attendance_operator" => await connection.ExecuteScalarAsync<int>(@"
SELECT COUNT(*)
FROM authrolepermissions rp
JOIN authroles r ON r.Id = rp.RoleId
JOIN authpermissions p ON p.Id = rp.PermissionId
WHERE r.Code = 'client_attendance_operator'
  AND p.Code = 'mss.attendance.client.manage';") > 0,
                _ => true
            };
            if (existingPermissionCount > 0 && hasRequiredPermission)
                continue;

            await connection.ExecuteAsync(@"
INSERT IGNORE INTO authrolepermissions (RoleId, PermissionId)
SELECT r.Id, p.Id
FROM authroles r
JOIN authpermissions p ON p.Code IN @PermissionCodes
WHERE r.Code = @RoleCode;", new { RoleCode = roleCode, PermissionCodes = permissionCodes });
        }
    }

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

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();
    private static string NormalizeLoginId(string value) => value.Trim().ToLowerInvariant();

    private async Task<string> ResolveInitialPasswordAsync(MySqlConnection connection, EmployeeLoginProvisionPreview employee, string fixedPassword = "", System.Data.IDbTransaction? transaction = null)
    {
        if (!string.IsNullOrWhiteSpace(fixedPassword))
            return fixedPassword.Trim();

        var clientSetting = await connection.QueryFirstOrDefaultAsync<ClientPasswordSetting>(@"
SELECT InitialPasswordMode, FixedPassword
FROM ess_client_settings
WHERE ClientId=@ClientId AND IsActive=TRUE
LIMIT 1;", new { employee.ClientId }, transaction);

        var mode = CleanMode(clientSetting?.InitialPasswordMode);
        var clientFixedPassword = clientSetting?.FixedPassword?.Trim() ?? "";
        if (mode == "App Default")
        {
            mode = CleanMode(configuration["EmployeeLogin:InitialPasswordMode"] ?? configuration["EmployeeLogin:InitialPasswordSource"] ?? "Random");
            clientFixedPassword = configuration["EmployeeLogin:FixedPassword"]?.Trim() ?? "";
        }

        var configured = mode.ToLowerInvariant() switch
        {
            "aadhaar" or "aadhar" => CleanPasswordValue(employee.AadhaarNumber),
            "employeecode" or "employee_code" or "empcode" => CleanPasswordValue(employee.EmployeeCode),
            "fixed" => clientFixedPassword,
            _ => ""
        };

        return configured.Length >= 8 ? configured : GenerateTemporaryPassword();
    }

    private static string CleanMode(string? value)
    {
        var mode = string.IsNullOrWhiteSpace(value) ? "App Default" : value.Trim();
        return mode.Equals("Aadhar", StringComparison.OrdinalIgnoreCase) ? "Aadhaar" : mode;
    }

    private static string CleanPasswordValue(string value) =>
        new((value ?? string.Empty).Trim().Where(char.IsLetterOrDigit).ToArray());

    private static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"PBKDF2-SHA256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    private static bool VerifyPassword(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "PBKDF2-SHA256") return false;
        var iterations = int.Parse(parts[1]);
        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static string HashToken(string token)
    {
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string GenerateTemporaryPassword()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        Span<byte> bytes = stackalloc byte[10];
        RandomNumberGenerator.Fill(bytes);
        var chars = bytes.ToArray().Select(value => alphabet[value % alphabet.Length]).ToArray();
        return $"Hr@{new string(chars)}1";
    }

    private sealed class AuthUserRecord
    {
        public int Id { get; set; }
        public string Email { get; set; } = string.Empty;
        public string PasswordHash { get; set; } = string.Empty;
        public bool IsActive { get; set; }
    }

    private sealed class ClientPasswordSetting
    {
        public string InitialPasswordMode { get; set; } = "App Default";
        public string FixedPassword { get; set; } = string.Empty;
    }
}
