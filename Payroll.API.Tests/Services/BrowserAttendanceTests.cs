using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class BrowserAttendanceTests
{
    [Fact]
    public void Browser_password_is_write_only_and_removed_after_it_is_consumed()
    {
        var json = """{"confirmationPassword":"synthetic password","action":"CheckIn","latitude":19.1234567,"biometricConfirmed":true}""";
        var request = JsonSerializer.Deserialize<BrowserAttendancePunchRequest>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.DoesNotContain("synthetic password", JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.DoesNotContain("confirmationPassword", JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal("synthetic password", request.TakeConfirmationPassword());
        Assert.Equal("", request.TakeConfirmationPassword());
        Assert.Equal(19.1234567m, request.Latitude);
    }

    [Fact]
    public void Browser_attendance_requires_the_existing_self_and_mark_permissions_and_a_current_employee_login()
    {
        var user = new AuthUser { Id = 7, IsActive = true, EmployeeId = 17, Permissions = ["ess.self", "ess.attendance.mark"] };
        Assert.True(BrowserAttendanceEndpoints.CanPunch(user));
        user.Permissions = ["ess.self"];
        Assert.False(BrowserAttendanceEndpoints.CanPunch(user));
        user.Permissions = ["ess.attendance.mark"];
        Assert.False(BrowserAttendanceEndpoints.CanPunch(user));
        user.Permissions = ["ess.self", "ess.attendance.mark"]; user.MustChangePassword = true;
        Assert.False(BrowserAttendanceEndpoints.CanPunch(user));
        user.MustChangePassword = false; user.IsActive = false;
        Assert.False(BrowserAttendanceEndpoints.CanPunch(user));
        user.IsActive = true; user.EmployeeId = null;
        Assert.False(BrowserAttendanceEndpoints.CanPunch(user));
    }

    [Fact]
    public async Task A_forged_client_confirmation_flag_cannot_create_a_trusted_browser_context()
    {
        var configuration = new ConfigurationBuilder().Build();
        var repository = new EssMssRepository(configuration);
        var request = new BrowserAttendancePunchRequest { BiometricConfirmed = true, CameraCaptureConfirmed = true, ConfirmationPassword = "irrelevant" };
        var result = await repository.RecordBrowserAttendancePunchAsync(new AuthRepository(configuration), new AuthUser { Id = 0 }, request);
        Assert.False(result.Allowed);
        Assert.False(result.PunchRecorded);
        Assert.Equal("BrowserIdentityMismatch", result.Status);
        Assert.False(request.BiometricConfirmed);
        Assert.Null(result.ConfirmationMethod);
    }

    [Fact]
    public async Task Native_punch_validation_still_requires_biometric_confirmation()
    {
        var validate = typeof(EssMssRepository).GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Single(method => method.Name == "ValidateAttendancePunchAsync");
        var request = new ValidateAttendancePunchRequest { ClientRequestId = "synthetic", DeviceId = "synthetic-device", NetworkType = "Online", AppVersion = "test", CameraCaptureConfirmed = true, BiometricConfirmed = false, Latitude = 19, Longitude = 72, AccuracyMeters = 10 };
        var result = await (Task<AttendancePunchValidationResponse>)validate.Invoke(null, [null, null, 1, 17, request, DateTime.UtcNow, null])!;
        Assert.False(result.Allowed);
        Assert.Equal("BiometricConfirmationRequired", result.Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("2026-11")]
    [InlineData("2026-11-13,2026-11-14")]
    [InlineData("2026-02-31")]
    [InlineData("0001-01-01")]
    [InlineData("9999-12-31")]
    public async Task Punch_location_history_rejects_invalid_or_non_single_day_dates_before_connecting(string date)
    {
        var repository = new EssMssRepository(new ConfigurationBuilder().Build());
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.GetOwnAttendancePunchesAsync(17, 1, date));
    }

    [Fact]
    public async Task Punch_location_history_requires_an_active_self_service_employee_before_connecting()
    {
        var repository = new EssMssRepository(new ConfigurationBuilder().Build());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => repository.GetOwnAttendancePunchesAsync(new AuthUser { Id = 7, IsActive = true, Permissions = ["ess.self"] }, "2026-11-13"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => repository.GetOwnAttendancePunchesAsync(new AuthUser { Id = 7, IsActive = true, EmployeeId = 17 }, "2026-11-13"));
    }

    [Fact]
    public void Confirmation_password_failures_are_limited_per_user_and_expire_without_unbounded_memory()
    {
        var limit = new BrowserAttendancePasswordThrottle();
        var now = new DateTimeOffset(2026, 11, 13, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < BrowserAttendancePasswordThrottle.AttemptLimit; i++) Assert.True(limit.TryBegin(7, now, out _));
        Assert.False(limit.TryBegin(7, now, out var retry));
        Assert.Equal(300, retry);
        Assert.True(limit.TryBegin(8, now, out _));
        Assert.True(limit.TryBegin(7, now + BrowserAttendancePasswordThrottle.Window, out _));
        limit.Succeeded(7);
        Assert.True(limit.TryBegin(7, now + BrowserAttendancePasswordThrottle.Window, out _));

        var bounded = new BrowserAttendancePasswordThrottle();
        for (var i = 1; i <= BrowserAttendancePasswordThrottle.MaximumUsers; i++) Assert.True(bounded.TryBegin(i, now, out _));
        Assert.False(bounded.TryBegin(BrowserAttendancePasswordThrottle.MaximumUsers + 1, now, out _));
        Assert.Equal(BrowserAttendancePasswordThrottle.MaximumUsers, bounded.Count);
        Assert.True(bounded.TryBegin(BrowserAttendancePasswordThrottle.MaximumUsers + 1, now + BrowserAttendancePasswordThrottle.Window, out _));
        Assert.Equal(1, bounded.Count);
    }
}
