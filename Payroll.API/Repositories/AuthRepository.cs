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
);");
        await EnsureForeignKeyAsync(connection, "authuserroles", "FK_AuthUserRoles_User", "FOREIGN KEY (UserId) REFERENCES authusers(Id) ON DELETE CASCADE");
        await EnsureForeignKeyAsync(connection, "authuserroles", "FK_AuthUserRoles_Role", "FOREIGN KEY (RoleId) REFERENCES authroles(Id) ON DELETE CASCADE");
        await EnsureForeignKeyAsync(connection, "authrolepermissions", "FK_AuthRolePermissions_Role", "FOREIGN KEY (RoleId) REFERENCES authroles(Id) ON DELETE CASCADE");
        await EnsureForeignKeyAsync(connection, "authrolepermissions", "FK_AuthRolePermissions_Permission", "FOREIGN KEY (PermissionId) REFERENCES authpermissions(Id) ON DELETE CASCADE");
        await EnsureForeignKeyAsync(connection, "authsessions", "FK_AuthSessions_User", "FOREIGN KEY (UserId) REFERENCES authusers(Id) ON DELETE CASCADE");
        await EnsureColumnAsync(connection, "authusers", "EmployeeId", "INT NULL");
        await SeedDashboardPermissionsAsync(connection);

    }

    public async Task<LoginResponse?> LoginAsync(LoginRequest request, string ipAddress, string userAgent)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        var row = await connection.QueryFirstOrDefaultAsync<AuthUserRecord>("SELECT * FROM authusers WHERE Email = @Email", new { Email = NormalizeEmail(request.Email) });
        if (row is null || !row.IsActive || !VerifyPassword(request.Password, row.PasswordHash))
            return null;

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(TokenBytes));
        var tokenHash = HashToken(token);
        var expiresAt = DateTime.UtcNow.AddHours(12);
        await connection.ExecuteAsync(@"INSERT INTO authsessions (UserId, TokenHash, IpAddress, UserAgent, ExpiresAt) VALUES (@UserId, @TokenHash, @IpAddress, @UserAgent, @ExpiresAt);
UPDATE authusers SET LastLoginAt = UTC_TIMESTAMP() WHERE Id = @UserId;", new { UserId = row.Id, TokenHash = tokenHash, IpAddress = ipAddress, UserAgent = userAgent, ExpiresAt = expiresAt });
        await WriteAuditAsync(connection, row.Id, row.Email, "auth.login", "AuthSession", "POST", "/api/auth/login", 200, ipAddress, userAgent, "{}");
        return new LoginResponse { Token = token, ExpiresAt = expiresAt, User = await BuildUserAsync(connection, row.Id) ?? new AuthUser() };
    }

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
        var userIds = await connection.QueryAsync<int>("SELECT Id FROM authusers ORDER BY DisplayName");
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
        var user = await connection.QueryFirstOrDefaultAsync<AuthUser>("SELECT Id, Email, DisplayName, ClientId, EmployeeId, IsActive, MustChangePassword FROM authusers WHERE Id = @UserId", new { UserId = userId });
        if (user is null) return null;
        user.Roles = (await connection.QueryAsync<string>(@"SELECT r.Code FROM authroles r JOIN authuserroles ur ON ur.RoleId = r.Id WHERE ur.UserId = @UserId ORDER BY r.Code", new { UserId = userId })).ToList();
        user.Permissions = (await connection.QueryAsync<string>(@"SELECT DISTINCT p.Code FROM authpermissions p JOIN authrolepermissions rp ON rp.PermissionId = p.Id JOIN authuserroles ur ON ur.RoleId = rp.RoleId WHERE ur.UserId = @UserId ORDER BY p.Code", new { UserId = userId })).ToList();
        return user;
    }

    private static Task WriteAuditAsync(MySqlConnection connection, int? userId, string userEmail, string action, string resource, string method, string path, int statusCode, string ipAddress, string userAgent, string detailsJson) =>
        connection.ExecuteAsync(@"INSERT INTO auditlogs (UserId, UserEmail, Action, Resource, Method, Path, StatusCode, IpAddress, UserAgent, DetailsJson)
VALUES (@UserId, @UserEmail, @Action, @Resource, @Method, @Path, @StatusCode, @IpAddress, @UserAgent, @DetailsJson);", new { UserId = userId, UserEmail = userEmail, Action = action, Resource = resource, Method = method, Path = path, StatusCode = statusCode, IpAddress = ipAddress, UserAgent = userAgent, DetailsJson = detailsJson });

    public async Task<IEnumerable<AuthPermission>> GetPermissionsAsync()
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        return await connection.QueryAsync<AuthPermission>("SELECT * FROM authpermissions ORDER BY Module, Code");
    }

    public async Task<AuthUser?> SaveUserAsync(SaveAuthUserRequest request)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var email = NormalizeEmail(request.Email);
        var userId = request.Id;
        if (userId == 0)
        {
            userId = (int)await connection.ExecuteScalarAsync<long>(@"INSERT INTO authusers (Email, DisplayName, PasswordHash, ClientId, EmployeeId, IsActive, MustChangePassword)
VALUES (@Email, @DisplayName, @PasswordHash, @ClientId, @EmployeeId, @IsActive, @MustChangePassword);
SELECT LAST_INSERT_ID();", new { Email = email, request.DisplayName, PasswordHash = HashPassword(request.Password), request.ClientId, request.EmployeeId, request.IsActive, request.MustChangePassword }, transaction);
        }
        else
        {
            await connection.ExecuteAsync(@"UPDATE authusers SET Email=@Email, DisplayName=@DisplayName, ClientId=@ClientId, EmployeeId=@EmployeeId, IsActive=@IsActive, MustChangePassword=@MustChangePassword WHERE Id=@Id", new { Id = userId, Email = email, request.DisplayName, request.ClientId, request.EmployeeId, request.IsActive, request.MustChangePassword }, transaction);
            if (!string.IsNullOrWhiteSpace(request.Password))
                await connection.ExecuteAsync("UPDATE authusers SET PasswordHash=@PasswordHash, MustChangePassword=TRUE WHERE Id=@Id", new { Id = userId, PasswordHash = HashPassword(request.Password) }, transaction);
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
        await using var transaction = await connection.BeginTransactionAsync();
        var code = request.Code.Trim().ToLowerInvariant().Replace(' ', '_');
        var roleId = request.Id;
        if (roleId == 0)
            roleId = (int)await connection.ExecuteScalarAsync<long>(@"INSERT INTO authroles (Code, Name, Description, IsSystem) VALUES (@Code, @Name, @Description, FALSE);
SELECT LAST_INSERT_ID();", new { Code = code, request.Name, request.Description }, transaction);
        else if (await connection.ExecuteScalarAsync<bool>("SELECT IsSystem FROM authroles WHERE Id=@Id", new { Id = roleId }, transaction))
        {
            await transaction.RollbackAsync();
            return (await GetRolesAsync()).FirstOrDefault(role => role.Id == roleId);
        }
        else
            await connection.ExecuteAsync("UPDATE authroles SET Name=@Name, Description=@Description WHERE Id=@Id AND IsSystem=FALSE", new { Id = roleId, request.Name, request.Description }, transaction);
        await connection.ExecuteAsync("DELETE FROM authrolepermissions WHERE RoleId=@RoleId", new { RoleId = roleId }, transaction);
        if (request.Permissions.Count > 0)
            await connection.ExecuteAsync(@"INSERT IGNORE INTO authrolepermissions (RoleId, PermissionId)
SELECT @RoleId, Id FROM authpermissions WHERE Code IN @Permissions;", new { RoleId = roleId, request.Permissions }, transaction);
        await transaction.CommitAsync();
        return (await GetRolesAsync()).FirstOrDefault(role => role.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
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

    private static Task SeedDashboardPermissionsAsync(MySqlConnection connection)
    {
        var permissions = new[]
        {
            new { Code = "dashboard.view", Name = "View dashboard", Module = "Dashboard", Description = "Access the HRMS dashboard shell." },
            new { Code = "dashboard.workforce.view", Name = "View workforce dashboard", Module = "Dashboard", Description = "View employee and ESS adoption dashboard metrics." },
            new { Code = "dashboard.payroll.view", Name = "View payroll dashboard", Module = "Dashboard", Description = "View payroll run, net pay, validation and recent payroll dashboard metrics." },
            new { Code = "dashboard.attendance.view", Name = "View attendance dashboard", Module = "Dashboard", Description = "View attendance readiness and exception dashboard metrics." },
            new { Code = "dashboard.approvals.view", Name = "View approvals dashboard", Module = "Dashboard", Description = "View workflow and leave approval dashboard metrics." }
        };

        return connection.ExecuteAsync(@"
INSERT INTO authpermissions (Code, Name, Module, Description)
VALUES (@Code, @Name, @Module, @Description)
ON DUPLICATE KEY UPDATE
    Name = VALUES(Name),
    Module = VALUES(Module),
    Description = VALUES(Description);", permissions);
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
