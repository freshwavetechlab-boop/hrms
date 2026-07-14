namespace Payroll.API.Models;

public class AuthUser
{
    public int Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Mobile { get; set; } = string.Empty;
    public int? ClientId { get; set; }
    public int? EmployeeId { get; set; }
    public bool IsActive { get; set; }
    public bool MustChangePassword { get; set; }
    public List<string> Roles { get; set; } = [];
    public List<string> Permissions { get; set; } = [];
    public List<DashboardAccessItem> DashboardAccess { get; set; } = [];
    public string DefaultDashboardCode { get; set; } = string.Empty;
}

public class DashboardAccessItem
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Route { get; set; } = string.Empty;
    public int SortOrder { get; set; }
}

public class SaveAuthUserRequest
{
    public int Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Mobile { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public int? ClientId { get; set; }
    public int? EmployeeId { get; set; }
    public bool IsActive { get; set; } = true;
    public bool MustChangePassword { get; set; } = true;
    public List<string> Roles { get; set; } = [];
}

public class SaveAuthRoleRequest
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<string> Permissions { get; set; } = [];
}

public class EmployeeLoginProvisionPreview
{
    public int EmployeeId { get; set; }
    public int ClientId { get; set; }
    public string ClientName { get; set; } = string.Empty;
    public string EmployeeCode { get; set; } = string.Empty;
    public string EmployeeName { get; set; } = string.Empty;
    public string WorkEmail { get; set; } = string.Empty;
    public string AadhaarNumber { get; set; } = string.Empty;
    public string Department { get; set; } = string.Empty;
    public string Designation { get; set; } = string.Empty;
}

public class ProvisionEmployeeLoginsRequest
{
    public List<int> EmployeeIds { get; set; } = [];
    public List<string> Roles { get; set; } = ["employee"];
    public string TemporaryPassword { get; set; } = string.Empty;
    public bool MustChangePassword { get; set; } = true;
}

public class ProvisionEmployeeLoginResult
{
    public int EmployeeId { get; set; }
    public int? UserId { get; set; }
    public string EmployeeCode { get; set; } = string.Empty;
    public string EmployeeName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public class EmployeeLoginProvisionResult : ProvisionEmployeeLoginResult
{
    public string TemporaryPassword { get; set; } = string.Empty;
    public int ClientId { get; set; }
    public string NotificationEmail { get; set; } = string.Empty;
}

public class ProvisionEmployeeLoginsResponse
{
    public List<ProvisionEmployeeLoginResult> Results { get; set; } = [];
    public string TemporaryPassword { get; set; } = string.Empty;
    public int CreatedCount => Results.Count(item => item.Status == "Created");
    public int SkippedCount => Results.Count(item => item.Status != "Created");
}

public class AuthPermission
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Module { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

public class AuthRole
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsSystem { get; set; }
    public string Permissions { get; set; } = string.Empty;
}

public class LoginRequest
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Portal { get; set; } = string.Empty;
}

public class ChangePasswordRequest
{
    public string CurrentPassword { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
}

public class LoginResponse
{
    public string Token { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public AuthUser User { get; set; } = new();
}

public class AuditLog
{
    public long Id { get; set; }
    public int? UserId { get; set; }
    public string UserEmail { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Resource { get; set; } = string.Empty;
    public string Method { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public string IpAddress { get; set; } = string.Empty;
    public string UserAgent { get; set; } = string.Empty;
    public string DetailsJson { get; set; } = "{}";
    public DateTime CreatedAt { get; set; }
}
