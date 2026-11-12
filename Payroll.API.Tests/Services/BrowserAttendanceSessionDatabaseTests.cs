using System.Reflection;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class BrowserAttendanceSessionDatabaseTests
{
    private sealed class IsolatedFactAttribute : FactAttribute
    {
        public IsolatedFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("FREVO_BROWSER_ATTENDANCE_TEST_DB") is null)
                Skip = "Explicit isolated localhost browser attendance database required.";
        }
    }

    [IsolatedFact]
    public async Task Session_and_camera_allow_no_fence_punches_without_a_password_and_preserve_coarse_GPS()
    {
        await using var f = await Fixture.CreateAsync();
        var validation = await f.Repository.ValidateBrowserAttendancePunchAsync(f.Auth, f.Actor(), f.Request());
        Assert.True(validation.Allowed);
        Assert.False(validation.PunchRecorded);
        Assert.Equal("NoGeoFenceConfigured", validation.Status);
        Assert.Equal("ESSSession", validation.ConfirmationMethod);
        await f.AssertCountsAsync(0, 0);

        const string legacySecret = "ignored-legacy-confirmation-password";
        var incoming = f.Request();
        incoming.ConfirmationPassword = legacySecret;
        incoming.BiometricConfirmed = true;
        incoming.Facial = new() { Passed = true, Provider = "untrusted-browser-claim" };
        var first = await f.Repository.RecordBrowserAttendancePunchAsync(f.Auth, f.Actor(), incoming);
        var outgoing = f.Request("CheckOut", accuracy: 876);
        outgoing.Latitude = 19.0761100m;
        var last = await f.Repository.RecordBrowserAttendancePunchAsync(f.Auth, f.Actor(), outgoing);
        Assert.True(first.PunchRecorded);
        Assert.True(last.PunchRecorded);
        Assert.Equal("ESSSession", first.ConfirmationMethod);
        Assert.Equal("ESSSession", last.ConfirmationMethod);
        Assert.Empty(incoming.TakeConfirmationPassword());
        await f.AssertCountsAsync(2, 2);

        var stored = (await f.Db.QueryAsync<StoredPunch>("SELECT action Action,latitude Latitude,longitude Longitude,accuracy_meters Accuracy,biometric_confirmed Biometric,camera_capture_confirmed Camera,face_verified Face FROM employee_attendance_punches WHERE client_id=905 AND employee_id=105 ORDER BY id")).ToArray();
        Assert.Collection(stored,
            row => { Assert.Equal("CheckIn", row.Action); Assert.Equal(19.0760900m, row.Latitude); Assert.Equal(72.8774260m, row.Longitude); Assert.Equal(241, row.Accuracy); },
            row => { Assert.Equal("CheckOut", row.Action); Assert.Equal(19.0761100m, row.Latitude); Assert.Equal(876, row.Accuracy); });
        Assert.All(stored, row => { Assert.True(row.Camera); Assert.False(row.Biometric); Assert.False(row.Face); });
        var audits = (await f.Db.QueryAsync<StoredAudit>("SELECT Action,CAST(DetailsJson AS CHAR) Details FROM auditlogs WHERE UserId=9905 ORDER BY Id")).ToArray();
        Assert.All(audits, row => { Assert.Equal("attendance.browser.session-confirmed", row.Action); Assert.Contains("ESSSession", row.Details); Assert.DoesNotContain(legacySecret, row.Details); });
        Assert.DoesNotContain(legacySecret, JsonSerializer.Serialize(incoming));
        var history = await f.Repository.GetOwnAttendancePunchesAsync(f.Actor(), f.Day.ToString("yyyy-MM-dd"));
        Assert.Equal(new[] { 241, 876 }, history.Select(row => row.AccuracyMeters));
        Assert.Equal(9m, await f.Db.ExecuteScalarAsync<decimal>("SELECT total_hours FROM employee_daily_attendance WHERE client_id=905 AND employee_id=105"));
    }

    [IsolatedFact]
    public async Task Retry_preserves_original_GPS_and_a_revoked_or_remapped_identity_cannot_replay_it()
    {
        await using var f = await Fixture.CreateAsync();
        var firstRequest = f.Request();
        var first = await f.Repository.RecordBrowserAttendancePunchAsync(f.Auth, f.Actor(), firstRequest);
        Assert.True(first.PunchRecorded);
        var retry = f.Request();
        retry.ClientRequestId = firstRequest.ClientRequestId;
        retry.Latitude = 20m;
        retry.AccuracyMeters = 999;
        var replay = await f.Repository.RecordBrowserAttendancePunchAsync(f.Auth, f.Actor(), retry);
        Assert.True(replay.IdempotentReplay);
        Assert.Equal(first.PunchId, replay.PunchId);
        await f.AssertCountsAsync(1, 1);
        Assert.Equal(19.0760900m, await f.Db.ExecuteScalarAsync<decimal>("SELECT latitude FROM employee_attendance_punches WHERE id=@Id", new { Id = first.PunchId }));
        Assert.Equal(241, await f.Db.ExecuteScalarAsync<int>("SELECT accuracy_meters FROM employee_attendance_punches WHERE id=@Id", new { Id = first.PunchId }));

        var revokedScope = f.Actor();
        revokedScope.AssignedClientIds = [];
        var revoked = await f.Repository.RecordBrowserAttendancePunchAsync(f.Auth, revokedScope, retry);
        Assert.Equal("BrowserIdentityMismatch", revoked.Status);
        Assert.False(revoked.PunchRecorded);
        await f.Db.ExecuteAsync("UPDATE authusers SET EmployeeId=106 WHERE Id=9905");
        var remapped = await f.Repository.RecordBrowserAttendancePunchAsync(f.Auth, f.Actor(), retry);
        Assert.Equal("BrowserIdentityMismatch", remapped.Status);
        Assert.False(remapped.PunchRecorded);
        await f.AssertCountsAsync(1, 1);
    }

    [IsolatedFact]
    public async Task Configured_fences_keep_the_100_metre_accuracy_limit_inside_outside_and_reason_rules()
    {
        await using var f = await Fixture.CreateAsync();
        await f.FenceAsync();
        foreach (var accuracy in new[] { 101, 241 })
            await f.AssertDeniedAsync(f.Request(accuracy: accuracy), "LocationAccuracyTooLow");

        var inside = await f.Repository.RecordBrowserAttendancePunchAsync(f.Auth, f.Actor(), f.Request(accuracy: 100));
        Assert.True(inside.PunchRecorded);
        Assert.Equal("InsideFence", inside.Status);
        await f.ResetPunchesAsync();

        var outside = f.Request(accuracy: 14);
        outside.Latitude = 19.0860900m;
        await f.AssertDeniedAsync(outside, "OutsideFence");
        await f.Db.ExecuteAsync("UPDATE attendance_geo_fence_rules SET strictness='Allow with reason' WHERE client_id=905");
        await f.AssertDeniedAsync(outside, "ReasonRequired");
        outside.Reason = "Synthetic authorized field duty";
        var withReason = await f.Repository.RecordBrowserAttendancePunchAsync(f.Auth, f.Actor(), outside);
        Assert.True(withReason.PunchRecorded);
        Assert.Equal("AcceptedWithReason", withReason.Decision);
        await f.AssertCountsAsync(1, 1);
    }

    [IsolatedFact]
    public async Task Native_punches_keep_the_accuracy_and_biometric_rules_even_without_a_fence()
    {
        await using var f = await Fixture.CreateAsync();
        var native = f.Request();
        native.BiometricConfirmed = true;
        var inaccurate = await f.Repository.RecordAttendancePunchAsync(105, 905, native);
        Assert.Equal("LocationAccuracyTooLow", inaccurate.Status);
        Assert.False(inaccurate.PunchRecorded);
        native.AccuracyMeters = 14;
        native.BiometricConfirmed = false;
        var noBiometric = await f.Repository.RecordAttendancePunchAsync(105, 905, native);
        Assert.Equal("BiometricConfirmationRequired", noBiometric.Status);
        Assert.False(noBiometric.PunchRecorded);
        await f.AssertCountsAsync(0, 0);
    }

    [IsolatedFact]
    public async Task Fresh_account_employee_and_client_changes_are_rejected_without_writes()
    {
        await using var f = await Fixture.CreateAsync();
        var staleActor = f.Actor();
        foreach (var change in new[] {
            "UPDATE authusers SET IsActive=FALSE WHERE Id=9905",
            "UPDATE authusers SET MustChangePassword=TRUE WHERE Id=9905",
            "UPDATE authusers SET EmployeeId=106 WHERE Id=9905",
            "UPDATE authusers SET ClientId=906 WHERE Id=9905",
            "UPDATE employees SET ClientId=906 WHERE Id=105",
            "UPDATE employees SET IsActive=FALSE WHERE Id=105",
            "UPDATE clients SET IsActive=FALSE WHERE Id=905" })
        {
            await f.RestoreIdentityAsync();
            await f.Db.ExecuteAsync(change);
            var result = await f.Repository.RecordBrowserAttendancePunchAsync(f.Auth, staleActor, f.Request());
            Assert.False(result.PunchRecorded);
            Assert.Equal("BrowserIdentityMismatch", result.Status);
            await f.AssertCountsAsync(0, 0);
        }
        await f.RestoreIdentityAsync();
        var noPermission = f.Actor(); noPermission.Permissions = ["ess.self"];
        var denied = await f.Repository.RecordBrowserAttendancePunchAsync(f.Auth, noPermission, f.Request());
        Assert.Equal("BrowserIdentityMismatch", denied.Status);
        Assert.False(denied.PunchRecorded);
        await f.AssertCountsAsync(0, 0);
    }

    [IsolatedFact]
    public async Task No_fence_does_not_allow_invalid_locations_negative_accuracy_missing_camera_or_locked_payroll()
    {
        await using var f = await Fixture.CreateAsync();
        var negative = f.Request(accuracy: -1);
        await f.AssertDeniedAsync(negative, "LocationAccuracyTooLow");
        var invalid = f.Request(); invalid.Latitude = 91;
        await f.AssertDeniedAsync(invalid, "InvalidLocation");
        var absent = f.Request(); absent.Latitude = 0; absent.Longitude = 0;
        await f.AssertDeniedAsync(absent, "InvalidLocation");
        var noCamera = f.Request(); noCamera.CameraCaptureConfirmed = false;
        await f.AssertDeniedAsync(noCamera, "CameraConfirmationRequired");
        await f.Db.ExecuteAsync("INSERT INTO payruns(Id,ClientId,PayPeriod,Status) VALUES(905,905,@Period,'Approved'); INSERT INTO payrunemployees(PayRunId,EmployeeId) VALUES(905,105)", new { Period = f.Day.ToString("yyyy-MM") });
        await f.AssertDeniedAsync(f.Request(), "PayrollPeriodLocked");
    }

    [IsolatedFact]
    public async Task Authentication_rejects_expired_and_revoked_session_tokens_before_building_the_user()
    {
        await using var f = await Fixture.CreateAsync();
        var hashToken = typeof(AuthRepository).GetMethod("HashToken", BindingFlags.NonPublic | BindingFlags.Static)!;
        foreach (var revoked in new[] { false, true })
        {
            var token = "synthetic-attendance-" + Guid.NewGuid().ToString("N");
            var hash = (string)hashToken.Invoke(null, [token])!;
            await f.Db.ExecuteAsync("INSERT INTO authsessions(UserId,TokenHash,ExpiresAt,RevokedAt) VALUES(9905,@Hash,@Expires,@Revoked)", new { Hash = hash, Expires = DateTime.UtcNow.AddMinutes(revoked ? 30 : -1), Revoked = revoked ? (DateTime?)DateTime.UtcNow : null });
            Assert.Null(await f.Auth.GetUserByTokenAsync(token));
        }
        await f.AssertCountsAsync(0, 0);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public MySqlConnection Db { get; }
        public EssMssRepository Repository { get; }
        public AuthRepository Auth { get; }
        public DateTime Day { get; } = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata")).Date.AddDays(-1);
        private Fixture(string connection)
        {
            Db = new(connection);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = connection }).Build();
            Repository = new(config); Auth = new(config);
        }
        public static async Task<Fixture> CreateAsync()
        {
            var connection = Environment.GetEnvironmentVariable("FREVO_BROWSER_ATTENDANCE_TEST_DB")!;
            var parsed = new MySqlConnectionStringBuilder(connection);
            Assert.Equal("127.0.0.1", parsed.Server);
            Assert.Equal((uint)33330, parsed.Port);
            Assert.Equal("attendance_location_test", parsed.Database);
            var f = new Fixture(connection);
            await f.Db.OpenAsync();
            await f.Db.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "BrowserAttendance", "session-schema.sql")));
            await f.CleanupAsync();
            await f.Db.ExecuteAsync("INSERT INTO clients(Id,Name,IsActive) VALUES(905,'Synthetic browser session client',TRUE),(906,'Synthetic other session client',TRUE); INSERT INTO employees(Id,ClientId,EmployeeCode,FirstName,LastName,IsActive,WorkLocationId) VALUES(105,905,'BROWSER-SESSION-105','Synthetic','Session',TRUE,0),(106,906,'BROWSER-SESSION-106','Other','Synthetic',TRUE,0); INSERT INTO authusers(Id,PasswordHash,ClientId,EmployeeId,IsActive,MustChangePassword) VALUES(9905,'not-a-password-verification-test',905,105,TRUE,FALSE)");
            return f;
        }
        public AuthUser Actor() => new() { Id = 9905, Email = "browser-session@example.test", EmployeeId = 105, ClientId = 905, IsActive = true, ClientAccessMode = "Selected", AssignedClientIds = [905], Permissions = ["ess.self", "ess.attendance.mark"] };
        public BrowserAttendancePunchRequest Request(string action = "CheckIn", int accuracy = 241) => new()
        {
            ClientRequestId = Guid.NewGuid().ToString("N"), Action = action, Latitude = 19.0760900m, Longitude = 72.8774260m, AccuracyMeters = accuracy,
            CapturedAt = DateTime.SpecifyKind(Day.AddHours(action == "CheckIn" ? 9 : 18), DateTimeKind.Unspecified),
            DeviceId = "synthetic-session-device", NetworkType = "WiFi", AppVersion = "isolated-session-test", DeviceModel = "Synthetic browser", OsVersion = "Synthetic",
            CameraCaptureConfirmed = true, BiometricConfirmed = false
        };
        public Task RestoreIdentityAsync() => Db.ExecuteAsync("UPDATE authusers SET IsActive=TRUE,MustChangePassword=FALSE,EmployeeId=105,ClientId=905 WHERE Id=9905; UPDATE employees SET ClientId=905,IsActive=TRUE WHERE Id=105; UPDATE clients SET IsActive=TRUE WHERE Id=905");
        public Task FenceAsync() => Db.ExecuteAsync("INSERT INTO attendance_geo_fence_rules(client_id,name,scope_type,latitude,longitude,radius_meters,gps_tolerance_meters,strictness,effective_from) VALUES(905,'Synthetic session office','Client Default',19.0760900,72.8774260,100,30,'Block outside fence','2020-01-01')");
        public async Task AssertCountsAsync(int punches, int audits)
        {
            Assert.Equal(punches, await Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employee_attendance_punches WHERE client_id IN (905,906) AND employee_id IN (105,106)"));
            Assert.Equal(audits, await Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM auditlogs WHERE UserId=9905"));
        }
        public async Task AssertDeniedAsync(BrowserAttendancePunchRequest request, string status)
        {
            var result = await Repository.RecordBrowserAttendancePunchAsync(Auth, Actor(), request);
            Assert.False(result.Allowed);
            Assert.False(result.PunchRecorded);
            Assert.Equal(status, result.Status);
            await AssertCountsAsync(0, 0);
        }
        public Task ResetPunchesAsync() => Db.ExecuteAsync("DELETE FROM employee_attendance_punches WHERE client_id IN (905,906) AND employee_id IN (105,106); DELETE FROM employee_daily_attendance WHERE client_id IN (905,906) AND employee_id IN (105,106); DELETE FROM employee_monthly_attendance WHERE client_id IN (905,906) AND employee_id IN (105,106); DELETE FROM auditlogs WHERE UserId=9905");
        private async Task CleanupAsync()
        {
            await ResetPunchesAsync();
            await Db.ExecuteAsync("DELETE FROM authsessions WHERE UserId=9905; DELETE FROM authusers WHERE Id=9905; DELETE FROM attendance_geo_fence_rule_employees WHERE geo_fence_rule_id IN (SELECT id FROM attendance_geo_fence_rules WHERE client_id IN (905,906)); DELETE FROM attendance_geo_fence_rules WHERE client_id IN (905,906); DELETE FROM attendance_settings WHERE client_id IN (905,906); DELETE FROM attendance_shift_assignments WHERE client_id IN (905,906); DELETE FROM attendance_group_employees WHERE employee_id IN (105,106); DELETE FROM attendance_groups WHERE client_id IN (905,906); DELETE FROM attendance_shifts WHERE client_id IN (905,906); DELETE FROM employee_it0000_actions WHERE ClientId IN (905,906) AND EmployeeId IN (105,106); DELETE FROM payrunemployees WHERE PayRunId=905 AND EmployeeId=105; DELETE FROM payruns WHERE Id=905 AND ClientId=905; DELETE FROM employees WHERE Id IN (105,106) AND ClientId IN (905,906); DELETE FROM clients WHERE Id IN (905,906)");
        }
        public async ValueTask DisposeAsync() { try { await CleanupAsync(); } finally { await Db.DisposeAsync(); } }
    }

    private sealed class StoredPunch { public string Action { get; set; } = ""; public decimal Latitude { get; set; } public decimal Longitude { get; set; } public int Accuracy { get; set; } public bool Biometric { get; set; } public bool Camera { get; set; } public bool Face { get; set; } }
    private sealed class StoredAudit { public string Action { get; set; } = ""; public string Details { get; set; } = ""; }
}
