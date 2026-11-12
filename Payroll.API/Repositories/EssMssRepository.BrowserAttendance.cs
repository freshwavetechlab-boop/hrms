using System.Globalization;
using Dapper;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public partial class EssMssRepository
{
    private sealed record BrowserPunchContext(int UserId, string Email, int EmployeeId, int ClientId);

    public Task<AttendancePunchValidationResponse> ValidateBrowserAttendancePunchAsync(AuthRepository auth, AuthUser user, BrowserAttendancePunchRequest request)
        => BrowserAttendancePunchAsync(auth, user, request, false);

    public Task<AttendancePunchValidationResponse> RecordBrowserAttendancePunchAsync(AuthRepository auth, AuthUser user, BrowserAttendancePunchRequest request)
        => BrowserAttendancePunchAsync(auth, user, request, true);

    private async Task<AttendancePunchValidationResponse> BrowserAttendancePunchAsync(AuthRepository auth, AuthUser user, BrowserAttendancePunchRequest request, bool record)
    {
        // Browsers use the authenticated employee session and a camera capture,
        // never a claimed native biometric proof. Discard the legacy password
        // field without verifying or retaining it, for older client compatibility.
        request.TakeConfirmationPassword();
        request.BiometricConfirmed = false;
        request.Facial = null;
        var confirmed = await auth.ConfirmBrowserAttendanceSessionAsync(user);
        if (!confirmed.Allowed)
        {
            return Block(confirmed.Status, confirmed.Message, "SignInAgain");
        }
        var browser = new BrowserPunchContext(user.Id, user.Email, confirmed.EmployeeId!.Value, confirmed.ClientId!.Value);
        AttendancePunchValidationResponse result;
        if (record) result = await RecordAttendancePunchAsync(browser.EmployeeId, browser.ClientId, request, browser);
        else
        {
            await using var db = Connection(); await db.OpenAsync();
            var identity = await GetPunchIdentityAsync(db, null, browser.EmployeeId, browser.ClientId, false);
            result = identity is null ? Block("EmployeeOrClientInactive", "The active employee and client mapping was not found.", "ContactHR")
                : await ValidateAttendancePunchAsync(db, null, browser.ClientId, browser.EmployeeId, request, ResolveAttendanceDateTime(request.CapturedAt), browser);
        }
        result.ConfirmationMethod = "ESSSession";
        return result;
    }

    public Task<IReadOnlyList<EssAttendancePunchLog>> GetOwnAttendancePunchesAsync(AuthUser user, string? date)
    {
        if (!BrowserAttendanceEndpoints.CanRead(user)) throw new UnauthorizedAccessException("Employee attendance access is required.");
        return ReadOwnAttendancePunchesAsync(user.EmployeeId!.Value, user.ClientId, date, user);
    }

    public Task<IReadOnlyList<EssAttendancePunchLog>> GetOwnAttendancePunchesAsync(int employeeId, int? clientId, string? date)
        => ReadOwnAttendancePunchesAsync(employeeId, clientId, date, null);

    private async Task<IReadOnlyList<EssAttendancePunchLog>> ReadOwnAttendancePunchesAsync(int employeeId, int? clientId, string? date, AuthUser? actor)
    {
        var selected = AttendanceNow().Date;
        if (date is not null && (!DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out selected)
            || date.Length != 10 || selected.Year < 1000 || selected >= DateTime.MaxValue.Date))
            throw new InvalidOperationException("Choose a valid punch date (yyyy-MM-dd).");
        await using var db = Connection(); await db.OpenAsync();
        var identity = await GetPunchIdentityAsync(db, null, employeeId, clientId, false)
            ?? throw new InvalidOperationException("The employee attendance profile was not found.");
        if (actor is not null && !SelectedClientAccess.Allows(actor, identity.ClientId))
            throw new UnauthorizedAccessException("The employee attendance profile is outside the assigned client scope.");
        // The requested date is the logical attendance/shift date. A night
        // shift's OUT may be after midnight; flexible shifts can start a day at
        // a configured cutoff. Use the same bounds as today's attendance state.
        var resolver = await AttendanceShiftResolver.LoadAsync(db, null, identity.ClientId, employeeId);
        if (date is null) selected = resolver.ResolvePunch(employeeId, AttendanceNow()).Date;
        var bounds = resolver.Bounds(employeeId, selected);
        var rows = await db.QueryAsync<StoredBrowserPunch>(@"SELECT id Id,action Action,captured_at CapturedAt,latitude Latitude,longitude Longitude,
accuracy_meters AccuracyMeters,validation_status Status,decision Decision FROM employee_attendance_punches
WHERE employee_id=@EmployeeId AND client_id=@ClientId AND captured_at>=@Start AND captured_at<@End
ORDER BY captured_at,id LIMIT 1000", new { EmployeeId = employeeId, identity.ClientId, bounds.Start, bounds.End });
        return rows.Select(row => new EssAttendancePunchLog(row.Id, row.Action,
            new DateTimeOffset(DateTime.SpecifyKind(row.CapturedAt, DateTimeKind.Unspecified), AttendanceTimeZone.GetUtcOffset(row.CapturedAt)),
            row.Latitude, row.Longitude, row.AccuracyMeters, row.Status, row.Decision)).ToArray();
    }

    private sealed class StoredBrowserPunch
    {
        public long Id { get; set; }
        public string Action { get; set; } = "";
        public DateTime CapturedAt { get; set; }
        public decimal Latitude { get; set; }
        public decimal Longitude { get; set; }
        public int AccuracyMeters { get; set; }
        public string Status { get; set; } = "";
        public string Decision { get; set; } = "";
    }
}
