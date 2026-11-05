using Payroll.API.Models;

namespace Payroll.API.Services;

public static class SelectedClientAccess
{
    public static bool IsSelected(AuthUser user) => user.ClientAccessMode.Equals("Selected", StringComparison.OrdinalIgnoreCase);
    public static bool Allows(AuthUser user, int clientId) => clientId > 0 && (IsSelected(user)
        ? user.AssignedClientIds.Contains(clientId) : user.ClientId is null || user.ClientId == clientId);
    public static bool IsUnrestrictedAdministrator(AuthUser user) => user.Id > 0 && user.IsActive && !user.MustChangePassword
        && !IsSelected(user) && user.ClientId is null && (user.Roles.Contains("super_admin", StringComparer.OrdinalIgnoreCase)
        || user.Permissions.Any(p => p is "settings.manage" or "security.manage"));
    public static AuthUser ForRequest(AuthUser user, int clientId) => new()
    {
        Id=user.Id, Email=user.Email, DisplayName=user.DisplayName, Mobile=user.Mobile, ClientId=clientId,
        EmployeeId=user.EmployeeId, ClientAccessMode=user.ClientAccessMode, AssignedClientIds=[..user.AssignedClientIds],
        RecruitmentScopeMode=user.RecruitmentScopeMode, RecruitmentLocationIds=[..user.RecruitmentLocationIds],
        IsActive=user.IsActive, MustChangePassword=user.MustChangePassword, Roles=[..user.Roles], Permissions=[..user.Permissions],
        DashboardAccess=[..user.DashboardAccess], DefaultDashboardCode=user.DefaultDashboardCode
    };
    public static bool IsExcelWorkspace(PathString path) => path.StartsWithSegments("/api/excel-payslips")
        || path.StartsWithSegments("/api/excel-payroll-setup");
}
