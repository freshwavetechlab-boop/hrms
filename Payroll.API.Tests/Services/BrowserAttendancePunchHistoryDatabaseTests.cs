using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class BrowserAttendancePunchHistoryDatabaseTests
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
    public Task Overnight_history_includes_next_calendar_days_out_and_excludes_adjacent_attendance_days()
        => VerifyHistoryAsync(903, 103, new(2026, 11, 1, 22, 0, 0), new(2026, 11, 2, 6, 0, 0),
            new(2026, 11, 1, 14, 0, 0), new(2026, 11, 2, 14, 0, 0), flexible: false);

    [IsolatedFact]
    public Task Flexible_boundary_history_includes_post_midnight_out_and_excludes_four_am_next_day()
        => VerifyHistoryAsync(904, 104, new(2026, 11, 1, 19, 0, 0), new(2026, 11, 2, 3, 0, 0),
            new(2026, 11, 1, 4, 0, 0), new(2026, 11, 2, 4, 0, 0), flexible: true);

    private static async Task VerifyHistoryAsync(int clientId, int employeeId, DateTime checkIn, DateTime checkOut,
        DateTime start, DateTime end, bool flexible)
    {
        var connection = Environment.GetEnvironmentVariable("FREVO_BROWSER_ATTENDANCE_TEST_DB")!;
        var parsed = new MySqlConnectionStringBuilder(connection);
        Assert.Equal("127.0.0.1", parsed.Server);
        Assert.Equal((uint)33330, parsed.Port);
        Assert.Equal("attendance_location_test", parsed.Database);
        Assert.Contains(clientId, new[] { 903, 904 });
        Assert.Contains(employeeId, new[] { 103, 104 });
        await using var db = new MySqlConnection(connection);
        await db.OpenAsync();
        // Use the same complete isolated schema as the session-punch suite.
        // Parallel test classes must not race to create incompatible minimal tables.
        await db.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "BrowserAttendance", "session-schema.sql")));
        await CleanupAsync(db, clientId, employeeId);
        try
        {
            await db.ExecuteAsync("INSERT INTO clients(Id,Name,IsActive) VALUES(@ClientId,'Synthetic punch history client',TRUE)", new { ClientId = clientId });
            await db.ExecuteAsync("INSERT INTO employees(Id,ClientId,EmployeeCode,FirstName,LastName,IsActive,WorkLocationId) VALUES(@EmployeeId,@ClientId,'GPS-HISTORY','Synthetic','History',TRUE,0)", new { EmployeeId = employeeId, ClientId = clientId });
            await db.ExecuteAsync("INSERT INTO attendance_settings(client_id,check_in_time,check_out_time,shift_id) VALUES(@ClientId,@Start,@End,@ShiftId)",
                new { ClientId = clientId, Start = flexible ? TimeSpan.Zero : TimeSpan.FromHours(22), End = TimeSpan.FromHours(6), ShiftId = flexible ? (int?)404 : null });
            if (flexible)
            {
                var rules = JsonSerializer.Serialize(new AttendanceShiftRules { DayBoundary = TimeSpan.FromHours(4), BreakMode = "None" });
                await db.ExecuteAsync("""
INSERT INTO attendance_shifts(id,client_id,shift_code,shift_name,shift_type,start_time,end_time,is_overnight,
grace_minutes,break_minutes,minimum_full_day_hours,minimum_half_day_hours,effective_from,effective_to,is_active,rules_json)
VALUES(404,@ClientId,'FLEX04','Synthetic flexible shift','Flexible',NULL,NULL,FALSE,0,0,8,4,'2020-01-01',NULL,TRUE,@Rules)
""", new { ClientId = clientId, Rules = rules });
            }
            async Task<long> Punch(DateTime at, string action, int? employee = null, int? client = null)
                => await db.ExecuteScalarAsync<long>("""
INSERT INTO employee_attendance_punches(client_id,employee_id,client_request_id,action,captured_at,latitude,longitude,accuracy_meters,validation_status,decision)
VALUES(@ClientId,@EmployeeId,@RequestId,@Action,@At,19.0760900,72.8774260,14,'NoGeoFenceConfigured','Accepted');
SELECT LAST_INSERT_ID();
""", new { ClientId = client ?? clientId, EmployeeId = employee ?? employeeId, RequestId = Guid.NewGuid().ToString("N"), Action = action, At = at });
            var inId = await Punch(checkIn, "CheckIn");
            var outId = await Punch(checkOut, "CheckOut");
            await Punch(start.AddSeconds(-1), "CheckOut");
            await Punch(end, "CheckIn");
            await Punch(checkIn, "CheckIn", employee: employeeId == 103 ? 104 : 103);
            await Punch(checkIn, "CheckIn", client: clientId == 903 ? 904 : 903);

            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = connection }).Build();
            var repository = new EssMssRepository(configuration);
            var actor = new AuthUser { Id = 1900 + employeeId, IsActive = true, EmployeeId = employeeId,
                ClientId = clientId, ClientAccessMode = "Selected", AssignedClientIds = [clientId], Permissions = ["ess.self"] };
            var logicalDate = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var rows = await repository.GetOwnAttendancePunchesAsync(actor, logicalDate);
            Assert.Equal(new[] { inId, outId }, rows.Select(row => row.Id));
            Assert.Equal(checkOut, rows[1].CapturedAt.DateTime);
            Assert.All(rows, row => Assert.Equal(TimeSpan.FromHours(5.5), row.CapturedAt.Offset));
            Assert.All(rows, row => Assert.Equal(19.0760900m, row.Latitude));

            // A missing date follows the same server-owned logical date as Today.
            var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"));
            var resolver = await AttendanceShiftResolver.LoadAsync(db, null, clientId, employeeId);
            var today = resolver.ResolvePunch(employeeId, now).Date;
            var currentId = await Punch(now, "CheckIn");
            var defaultDate = await repository.GetOwnAttendancePunchesAsync(actor, null);
            var explicitDate = await repository.GetOwnAttendancePunchesAsync(actor, today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            Assert.Equal(explicitDate.Select(row => row.Id), defaultDate.Select(row => row.Id));
            Assert.Contains(defaultDate, row => row.Id == currentId);

            actor.AssignedClientIds = [];
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => repository.GetOwnAttendancePunchesAsync(actor, logicalDate));
        }
        finally { await CleanupAsync(db, clientId, employeeId); }
    }

    private static async Task CleanupAsync(MySqlConnection db, int clientId, int employeeId)
    {
        // Reserved synthetic scopes only; other test employees and UI actor 901/101 remain intact.
        Assert.Contains(clientId, new[] { 903, 904 });
        Assert.Contains(employeeId, new[] { 103, 104 });
        await db.ExecuteAsync("DELETE FROM employee_attendance_punches WHERE client_id IN (903,904) AND employee_id IN (103,104); DELETE FROM attendance_settings WHERE client_id=@ClientId; DELETE FROM attendance_shifts WHERE client_id=@ClientId; DELETE FROM employees WHERE ClientId=@ClientId AND Id=@EmployeeId; DELETE FROM clients WHERE Id=@ClientId;",
            new { ClientId = clientId, EmployeeId = employeeId });
    }

}
