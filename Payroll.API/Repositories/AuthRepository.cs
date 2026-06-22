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
        await connection.ExecuteAsync("USE payroll;");
        await connection.ExecuteAsync(@"
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
);");
        await EnsureColumnAsync(connection, "AuthUsers", "EmployeeId", "INT NULL");

        await SeedSecurityAsync(connection);
    }

    public async Task<LoginResponse?> LoginAsync(LoginRequest request, string ipAddress, string userAgent)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("USE payroll;");
        var row = await connection.QueryFirstOrDefaultAsync<AuthUserRecord>("SELECT * FROM AuthUsers WHERE Email = @Email", new { Email = NormalizeEmail(request.Email) });
        if (row is null || !row.IsActive || !VerifyPassword(request.Password, row.PasswordHash))
            return null;

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(TokenBytes));
        var tokenHash = HashToken(token);
        var expiresAt = DateTime.UtcNow.AddHours(12);
        await connection.ExecuteAsync(@"INSERT INTO AuthSessions (UserId, TokenHash, IpAddress, UserAgent, ExpiresAt) VALUES (@UserId, @TokenHash, @IpAddress, @UserAgent, @ExpiresAt);
UPDATE AuthUsers SET LastLoginAt = UTC_TIMESTAMP() WHERE Id = @UserId;", new { UserId = row.Id, TokenHash = tokenHash, IpAddress = ipAddress, UserAgent = userAgent, ExpiresAt = expiresAt });
        await WriteAuditAsync(connection, row.Id, row.Email, "auth.login", "AuthSession", "POST", "/api/auth/login", 200, ipAddress, userAgent, "{}");
        return new LoginResponse { Token = token, ExpiresAt = expiresAt, User = await BuildUserAsync(connection, row.Id) ?? new AuthUser() };
    }

    public async Task<AuthUser?> GetUserByTokenAsync(string token)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("USE payroll;");
        var userId = await connection.ExecuteScalarAsync<int?>(@"SELECT UserId FROM AuthSessions WHERE TokenHash = @TokenHash AND RevokedAt IS NULL AND ExpiresAt > UTC_TIMESTAMP()", new { TokenHash = HashToken(token) });
        return userId is null ? null : await BuildUserAsync(connection, userId.Value);
    }

    public async Task LogoutAsync(string token, AuthUser? user, string ipAddress, string userAgent)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("USE payroll;");
        await connection.ExecuteAsync("UPDATE AuthSessions SET RevokedAt = UTC_TIMESTAMP() WHERE TokenHash = @TokenHash AND RevokedAt IS NULL", new { TokenHash = HashToken(token) });
        await WriteAuditAsync(connection, user?.Id, user?.Email ?? "", "auth.logout", "AuthSession", "POST", "/api/auth/logout", 200, ipAddress, userAgent, "{}");
    }

    public async Task<IEnumerable<AuthUser>> GetUsersAsync()
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("USE payroll;");
        var userIds = await connection.QueryAsync<int>("SELECT Id FROM AuthUsers ORDER BY DisplayName");
        var users = new List<AuthUser>();
        foreach (var userId in userIds)
        {
            var user = await BuildUserAsync(connection, userId);
            if (user is not null) users.Add(user);
        }
        return users;
    }

    public async Task<IEnumerable<AuthRole>> GetRolesAsync()
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("USE payroll;");
        return await connection.QueryAsync<AuthRole>(@"SELECT r.Id, r.Code, r.Name, r.Description, r.IsSystem, COALESCE(GROUP_CONCAT(p.Code ORDER BY p.Code), '') AS Permissions
FROM AuthRoles r
LEFT JOIN AuthRolePermissions rp ON rp.RoleId = r.Id
LEFT JOIN AuthPermissions p ON p.Id = rp.PermissionId
GROUP BY r.Id
ORDER BY r.Name;");
    }

    public async Task<IEnumerable<AuditLog>> GetAuditLogsAsync(int limit = 100)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("USE payroll;");
        return await connection.QueryAsync<AuditLog>("SELECT * FROM AuditLogs ORDER BY CreatedAt DESC LIMIT @Limit", new { Limit = Math.Clamp(limit, 1, 500) });
    }

    public async Task WriteAuditAsync(AuthUser? user, string action, string resource, string method, string path, int statusCode, string ipAddress, string userAgent, string detailsJson = "{}")
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("USE payroll;");
        await WriteAuditAsync(connection, user?.Id, user?.Email ?? "", action, resource, method, path, statusCode, ipAddress, userAgent, detailsJson);
    }

    private static async Task<AuthUser?> BuildUserAsync(MySqlConnection connection, int userId)
    {
        var user = await connection.QueryFirstOrDefaultAsync<AuthUser>("SELECT Id, Email, DisplayName, ClientId, EmployeeId, IsActive, MustChangePassword FROM AuthUsers WHERE Id = @UserId", new { UserId = userId });
        if (user is null) return null;
        user.Roles = (await connection.QueryAsync<string>(@"SELECT r.Code FROM AuthRoles r JOIN AuthUserRoles ur ON ur.RoleId = r.Id WHERE ur.UserId = @UserId ORDER BY r.Code", new { UserId = userId })).ToList();
        user.Permissions = (await connection.QueryAsync<string>(@"SELECT DISTINCT p.Code FROM AuthPermissions p JOIN AuthRolePermissions rp ON rp.PermissionId = p.Id JOIN AuthUserRoles ur ON ur.RoleId = rp.RoleId WHERE ur.UserId = @UserId ORDER BY p.Code", new { UserId = userId })).ToList();
        return user;
    }

    private static async Task SeedSecurityAsync(MySqlConnection connection)
    {
        var permissions = new[]
        {
            ("settings.manage", "Manage settings", "Settings"),
            ("clients.manage", "Manage clients", "Clients"),
            ("employees.manage", "Manage employees", "Employees"),
            ("payroll.run", "Run payroll", "Payroll"),
            ("payroll.approve", "Approve payroll", "Payroll"),
            ("payroll.payments", "Record payments", "Payroll"),
            ("hiring.manage", "Manage hiring", "Hiring"),
            ("ess.self", "Employee self service", "ESS"),
            ("security.manage", "Manage security", "Security"),
            ("audit.view", "View audit logs", "Security"),
            ("reports.view", "View and export reports", "Reporting")
            ,("workflow.manage", "Manage workflows", "Workflow"),("workflow.approve", "Approve workflow tasks", "Workflow")
        };

        foreach (var permission in permissions)
            await connection.ExecuteAsync(@"INSERT INTO AuthPermissions (Code, Name, Module) VALUES (@Code, @Name, @Module)
ON DUPLICATE KEY UPDATE Name = @Name, Module = @Module;", new { Code = permission.Item1, Name = permission.Item2, Module = permission.Item3 });

        await connection.ExecuteAsync(@"INSERT INTO AuthRoles (Code, Name, Description, IsSystem) VALUES
('super_admin', 'Super Admin', 'Full platform access', TRUE),
('payroll_maker', 'Payroll Maker', 'Can prepare and maintain payroll drafts', TRUE),
('payroll_approver', 'Payroll Approver', 'Can approve payroll and review audit evidence', TRUE),
('hr_manager', 'HR Manager', 'Can manage employees and HR setup', TRUE),
('hiring_manager', 'Hiring Manager', 'Can manage hiring workflows', TRUE),
('employee', 'Employee', 'Employee self-service access', TRUE)
ON DUPLICATE KEY UPDATE Name = VALUES(Name), Description = VALUES(Description), IsSystem = TRUE;");

        await GrantAsync(connection, "super_admin", permissions.Select(permission => permission.Item1).ToArray());
        await GrantAsync(connection, "payroll_maker", ["payroll.run", "employees.manage"]);
        await GrantAsync(connection, "payroll_approver", ["payroll.approve", "audit.view", "reports.view"]);
        await GrantAsync(connection, "hr_manager", ["employees.manage", "clients.manage", "reports.view", "workflow.manage", "workflow.approve"]);
        await GrantAsync(connection, "hiring_manager", ["hiring.manage"]);
        await GrantAsync(connection, "employee", ["ess.self"]);

        var adminExists = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM AuthUsers WHERE Email = 'admin@paymint.local'");
        if (adminExists == 0)
        {
            var adminId = (int)await connection.ExecuteScalarAsync<long>(@"INSERT INTO AuthUsers (Email, DisplayName, PasswordHash, MustChangePassword) VALUES ('admin@paymint.local', 'System Administrator', @PasswordHash, TRUE);
SELECT LAST_INSERT_ID();", new { PasswordHash = HashPassword("Admin@12345") });
            await connection.ExecuteAsync(@"INSERT INTO AuthUserRoles (UserId, RoleId)
SELECT @UserId, Id FROM AuthRoles WHERE Code = 'super_admin';", new { UserId = adminId });
        }
    }

    private static async Task GrantAsync(MySqlConnection connection, string roleCode, string[] permissionCodes)
    {
        await connection.ExecuteAsync(@"INSERT IGNORE INTO AuthRolePermissions (RoleId, PermissionId)
SELECT r.Id, p.Id FROM AuthRoles r JOIN AuthPermissions p ON p.Code IN @PermissionCodes WHERE r.Code = @RoleCode;", new { RoleCode = roleCode, PermissionCodes = permissionCodes });
    }

    private static Task WriteAuditAsync(MySqlConnection connection, int? userId, string userEmail, string action, string resource, string method, string path, int statusCode, string ipAddress, string userAgent, string detailsJson) =>
        connection.ExecuteAsync(@"INSERT INTO AuditLogs (UserId, UserEmail, Action, Resource, Method, Path, StatusCode, IpAddress, UserAgent, DetailsJson)
VALUES (@UserId, @UserEmail, @Action, @Resource, @Method, @Path, @StatusCode, @IpAddress, @UserAgent, @DetailsJson);", new { UserId = userId, UserEmail = userEmail, Action = action, Resource = resource, Method = method, Path = path, StatusCode = statusCode, IpAddress = ipAddress, UserAgent = userAgent, DetailsJson = detailsJson });

    public async Task<IEnumerable<AuthPermission>> GetPermissionsAsync()
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("USE payroll;");
        return await connection.QueryAsync<AuthPermission>("SELECT * FROM AuthPermissions ORDER BY Module, Code");
    }

    public async Task<AuthUser?> SaveUserAsync(SaveAuthUserRequest request)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("USE payroll;");
        await using var transaction = await connection.BeginTransactionAsync();
        var email = NormalizeEmail(request.Email);
        var userId = request.Id;
        if (userId == 0)
        {
            var password = string.IsNullOrWhiteSpace(request.Password) ? "Welcome@12345" : request.Password;
            userId = (int)await connection.ExecuteScalarAsync<long>(@"INSERT INTO AuthUsers (Email, DisplayName, PasswordHash, ClientId, EmployeeId, IsActive, MustChangePassword)
VALUES (@Email, @DisplayName, @PasswordHash, @ClientId, @EmployeeId, @IsActive, @MustChangePassword);
SELECT LAST_INSERT_ID();", new { Email = email, request.DisplayName, PasswordHash = HashPassword(password), request.ClientId, request.EmployeeId, request.IsActive, request.MustChangePassword }, transaction);
        }
        else
        {
            await connection.ExecuteAsync(@"UPDATE AuthUsers SET Email=@Email, DisplayName=@DisplayName, ClientId=@ClientId, EmployeeId=@EmployeeId, IsActive=@IsActive, MustChangePassword=@MustChangePassword WHERE Id=@Id", new { Id = userId, Email = email, request.DisplayName, request.ClientId, request.EmployeeId, request.IsActive, request.MustChangePassword }, transaction);
            if (!string.IsNullOrWhiteSpace(request.Password))
                await connection.ExecuteAsync("UPDATE AuthUsers SET PasswordHash=@PasswordHash, MustChangePassword=TRUE WHERE Id=@Id", new { Id = userId, PasswordHash = HashPassword(request.Password) }, transaction);
        }

        await connection.ExecuteAsync("DELETE FROM AuthUserRoles WHERE UserId=@UserId", new { UserId = userId }, transaction);
        if (request.Roles.Count > 0)
            await connection.ExecuteAsync(@"INSERT IGNORE INTO AuthUserRoles (UserId, RoleId)
SELECT @UserId, Id FROM AuthRoles WHERE Code IN @Roles;", new { UserId = userId, request.Roles }, transaction);
        await transaction.CommitAsync();
        return await GetUserByIdAsync(userId);
    }

    public async Task<AuthRole?> SaveRoleAsync(SaveAuthRoleRequest request)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("USE payroll;");
        await using var transaction = await connection.BeginTransactionAsync();
        var code = request.Code.Trim().ToLowerInvariant().Replace(' ', '_');
        var roleId = request.Id;
        if (roleId == 0)
            roleId = (int)await connection.ExecuteScalarAsync<long>(@"INSERT INTO AuthRoles (Code, Name, Description, IsSystem) VALUES (@Code, @Name, @Description, FALSE);
SELECT LAST_INSERT_ID();", new { Code = code, request.Name, request.Description }, transaction);
        else if (await connection.ExecuteScalarAsync<bool>("SELECT IsSystem FROM AuthRoles WHERE Id=@Id", new { Id = roleId }, transaction))
        {
            await transaction.RollbackAsync();
            return (await GetRolesAsync()).FirstOrDefault(role => role.Id == roleId);
        }
        else
            await connection.ExecuteAsync("UPDATE AuthRoles SET Name=@Name, Description=@Description WHERE Id=@Id AND IsSystem=FALSE", new { Id = roleId, request.Name, request.Description }, transaction);
        await connection.ExecuteAsync("DELETE FROM AuthRolePermissions WHERE RoleId=@RoleId", new { RoleId = roleId }, transaction);
        if (request.Permissions.Count > 0)
            await connection.ExecuteAsync(@"INSERT IGNORE INTO AuthRolePermissions (RoleId, PermissionId)
SELECT @RoleId, Id FROM AuthPermissions WHERE Code IN @Permissions;", new { RoleId = roleId, request.Permissions }, transaction);
        await transaction.CommitAsync();
        return (await GetRolesAsync()).FirstOrDefault(role => role.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<AuthUser?> GetUserByIdAsync(int userId)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("USE payroll;");
        return await BuildUserAsync(connection, userId);
    }

    private static async Task EnsureColumnAsync(MySqlConnection connection, string tableName, string columnName, string definition)
    {
        var exists = await connection.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'payroll' AND TABLE_NAME = @TableName AND COLUMN_NAME = @ColumnName", new { TableName = tableName, ColumnName = columnName });
        if (exists == 0) await connection.ExecuteAsync($"ALTER TABLE `{tableName}` ADD COLUMN `{columnName}` {definition}");
    }

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

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

    private sealed class AuthUserRecord
    {
        public int Id { get; set; }
        public string Email { get; set; } = string.Empty;
        public string PasswordHash { get; set; } = string.Empty;
        public bool IsActive { get; set; }
    }
}
