using Dapper;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public partial class AuthRepository
{
    // The normal API authentication middleware has already validated expiry,
    // revocation and current permissions. Recheck the linked identity here so
    // a stale employee/client mapping cannot create a trusted browser context.
    internal async Task<BrowserAttendanceSessionResult> ConfirmBrowserAttendanceSessionAsync(AuthUser user)
    {
        if (!BrowserAttendanceEndpoints.CanPunch(user))
            return new(false, "BrowserIdentityMismatch", "Sign in with an employee account permitted to mark attendance.");
        await using var db = CreateConnection(); await db.OpenAsync();
        var row = await db.QuerySingleOrDefaultAsync<BrowserAttendanceUser>(@"SELECT u.ClientId,u.EmployeeId,u.IsActive,u.MustChangePassword,
e.ClientId EmployeeClientId,e.IsActive EmployeeActive,c.IsActive ClientActive
FROM authusers u JOIN employees e ON e.Id=u.EmployeeId JOIN clients c ON c.Id=e.ClientId WHERE u.Id=@Id", new { user.Id });
        if (row is null || !row.IsActive || row.MustChangePassword || !row.EmployeeActive || !row.ClientActive
            || row.EmployeeId != user.EmployeeId || row.ClientId.HasValue && row.ClientId != row.EmployeeClientId
            || user.ClientId.HasValue && user.ClientId != row.EmployeeClientId || !SelectedClientAccess.Allows(user, row.EmployeeClientId))
            return new(false, "BrowserIdentityMismatch", "The active employee and client mapping could not be confirmed. Sign in again.");
        return new(true, "SessionConfirmed", "Employee login session confirmed.", row.EmployeeId, row.EmployeeClientId);
    }

    private sealed class BrowserAttendanceUser
    {
        public int? ClientId { get; set; }
        public int EmployeeId { get; set; }
        public int EmployeeClientId { get; set; }
        public bool IsActive { get; set; }
        public bool MustChangePassword { get; set; }
        public bool EmployeeActive { get; set; }
        public bool ClientActive { get; set; }
    }
}

internal sealed record BrowserAttendanceSessionResult(bool Allowed, string Status, string Message,
    int? EmployeeId = null, int? ClientId = null);
