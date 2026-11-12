using Dapper;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public partial class AuthRepository
{
    private static readonly BrowserAttendancePasswordThrottle BrowserAttendanceAttempts = new();

    internal async Task<BrowserAttendancePasswordResult> ConfirmBrowserAttendancePasswordAsync(AuthUser user, string password)
    {
        if (!BrowserAttendanceEndpoints.CanPunch(user))
            return new(false, "BrowserIdentityMismatch", "Sign in with an employee account permitted to mark attendance.");
        if (!BrowserAttendanceAttempts.TryBegin(user.Id, DateTimeOffset.UtcNow, out var retry))
            return new(false, "PasswordConfirmationRateLimited", "Too many unsuccessful password confirmations. Try again later.", RetryAfterSeconds: retry);
        if (string.IsNullOrEmpty(password) || password.Length > 1024)
            return new(false, "PasswordConfirmationRequired", "Enter your current ESS password to confirm this punch.");

        await using var db = CreateConnection(); await db.OpenAsync();
        var row = await db.QuerySingleOrDefaultAsync<BrowserAttendanceUser>(@"SELECT u.PasswordHash,u.ClientId,u.EmployeeId,u.IsActive,u.MustChangePassword,
e.ClientId EmployeeClientId,e.IsActive EmployeeActive,c.IsActive ClientActive
FROM authusers u JOIN employees e ON e.Id=u.EmployeeId JOIN clients c ON c.Id=e.ClientId WHERE u.Id=@Id", new { user.Id });
        if (row is null || !row.IsActive || row.MustChangePassword || !row.EmployeeActive || !row.ClientActive
            || row.EmployeeId != user.EmployeeId || row.ClientId.HasValue && row.ClientId != row.EmployeeClientId
            || user.ClientId.HasValue && user.ClientId != row.EmployeeClientId || !SelectedClientAccess.Allows(user, row.EmployeeClientId))
            return new(false, "BrowserIdentityMismatch", "The active employee and client mapping could not be confirmed. Sign in again.");
        bool verified;
        try { verified = VerifyPassword(password, row.PasswordHash); }
        catch (FormatException) { verified = false; }
        catch (ArgumentException) { verified = false; }
        if (!verified) return new(false, "PasswordConfirmationRequired", "Your ESS password could not be confirmed. Check it and retry.");
        BrowserAttendanceAttempts.Succeeded(user.Id);
        return new(true, "PasswordConfirmed", "ESS password confirmed.", row.EmployeeId, row.EmployeeClientId);
    }

    private sealed class BrowserAttendanceUser
    {
        public string PasswordHash { get; set; } = "";
        public int? ClientId { get; set; }
        public int EmployeeId { get; set; }
        public int EmployeeClientId { get; set; }
        public bool IsActive { get; set; }
        public bool MustChangePassword { get; set; }
        public bool EmployeeActive { get; set; }
        public bool ClientActive { get; set; }
    }
}

internal sealed record BrowserAttendancePasswordResult(bool Allowed, string Status, string Message,
    int? EmployeeId = null, int? ClientId = null, int? RetryAfterSeconds = null);
