using Payroll.API.Models;

namespace Payroll.API.Services;

public static class RecruitmentPermissions
{
    public static bool IsAdmin(AuthUser user) => user.Permissions.Contains("settings.manage")
        || user.ClientId is null && user.Roles.Contains("super_admin", StringComparer.OrdinalIgnoreCase);

    public static bool Has(AuthUser user, params string[] permissions) => IsAdmin(user)
        || user.Permissions.Contains("recruitment.manage")
        || permissions.Any(code => user.Permissions.Contains(code, StringComparer.OrdinalIgnoreCase));
}
