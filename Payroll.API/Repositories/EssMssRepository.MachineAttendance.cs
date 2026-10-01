using System.Globalization;
using System.Text.RegularExpressions;
using Dapper;
using Payroll.API.Models;
using Payroll.API.Services;
using MySqlConnector;

namespace Payroll.API.Repositories;

public partial class EssMssRepository
{
    private class AttendanceRequestType { public string Code { get; set; } = ""; public string Name { get; set; } = ""; public bool AllowHalfDay { get; set; } }
    public async Task<object?> GetRegularizationOptionsAsync(int employeeId, int? clientId)
    {
        await using var db = Connection(); await db.OpenAsync();
        var identity = await GetPunchIdentityAsync(db, null, employeeId, clientId, false);
        if (identity is null) return null;
        var options = await db.QueryAsync<AttendanceRequestType>("SELECT l.code Code,l.name Name,COALESCE(p.allow_half_day,FALSE) AllowHalfDay FROM leave_types l JOIN leave_type_policies p ON p.leave_type_id=l.id WHERE l.client_id=@ClientId AND l.is_active=TRUE AND p.attendance_action='Mark as present' ORDER BY l.name", new { identity.ClientId });
        var settings = await new LeaveAttendanceRepository(configuration).GetAttendanceSettingsAsync(identity.ClientId);
        return new { settings, leaveTypes = options };
    }
    private record MachineEmployee(int Id, int ClientId, int WorkLocationId);
    private record MachineExisting(int EmployeeId, string Action, DateTime CapturedAt);
    private record MachineDaily(string Status, string Remarks);

    public async Task<List<MachinePunchResult>> ImportMachinePunchesAsync(AttendanceDevice device, string token, MachineAttendanceRequest request)
    {
        var results = new List<MachinePunchResult>();
        foreach (var punch in request.Punches)
        {
            var action = punch.Action?.Trim() switch { "IN" or "CheckIn" => "CheckIn", "OUT" or "CheckOut" => "CheckOut", _ => "" };
            MachinePunchResult Reject(string error) => new(punch.PunchId ?? "", "Rejected", error);
            if (string.IsNullOrWhiteSpace(punch.PunchId) || punch.PunchId.Length > 100 || string.IsNullOrWhiteSpace(punch.EmployeeCode) || punch.EmployeeCode.Length > 100 || action.Length == 0)
            { results.Add(Reject("Punch id, employee code and IN/OUT action are required.")); continue; }
            if (punch.CapturedAt is null || !Regex.IsMatch(punch.CapturedAt, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$") || !DateTimeOffset.TryParse(punch.CapturedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant))
            { results.Add(Reject("capturedAt must be ISO 8601 with timezone, for example 2026-09-01T09:00:00+05:30.")); continue; }
            if (instant > DateTimeOffset.UtcNow.AddMinutes(10)) { results.Add(Reject("Punch time is ahead of the server time.")); continue; }
            var local = instant.ToOffset(TimeSpan.FromHours(5.5)).DateTime;
            local = new DateTime(local.Ticks - local.Ticks % TimeSpan.TicksPerSecond);
            await using var db = Connection(); await db.OpenAsync(); await using var tx = await db.BeginTransactionAsync();
            // Recheck credentials under the same lock as token rotation / device edits.
            var integration = await AttendanceIntegrationRepository.ReadAsync(db, tx, device.ClientId, true);
            var current = integration.Devices.FirstOrDefault(d => d.DeviceId == device.DeviceId && d.IsActive && d.TokenHash == AttendanceIntegrationRepository.Hash(token) && d.TokenExpiresAt > DateTime.UtcNow);
            if (current is null) { results.Add(Reject("Device token expired or was revoked.")); continue; }
            var employee = await db.QueryFirstOrDefaultAsync<MachineEmployee>("SELECT e.Id,e.ClientId,e.WorkLocationId FROM employees e JOIN clients c ON c.Id=e.ClientId AND c.IsActive=TRUE JOIN worklocations w ON w.Id=e.WorkLocationId AND w.ClientId=e.ClientId AND w.IsActive=TRUE WHERE e.EmployeeCode=@Code AND e.ClientId=@ClientId AND e.IsActive=TRUE FOR UPDATE", new { Code = punch.EmployeeCode.Trim(), device.ClientId }, tx);
            if (employee is null || employee.WorkLocationId != current.WorkLocationId) { results.Add(Reject("Employee is not active at the device's client and work location.")); continue; }
            var key = "machine:" + AttendanceIntegrationRepository.Hash(device.DeviceId + ":" + punch.PunchId.Trim());
            var existing = await db.QueryFirstOrDefaultAsync<MachineExisting>("SELECT employee_id EmployeeId,action Action,captured_at CapturedAt FROM employee_attendance_punches WHERE client_id=@ClientId AND client_request_id=@Key LIMIT 1", new { device.ClientId, Key = key }, tx);
            if (existing is not null)
            {
                results.Add(existing.EmployeeId == employee.Id && existing.Action == action && existing.CapturedAt == local ? new(punch.PunchId, "Duplicate") : Reject("Punch id was already used for different data."));
                continue;
            }
            var settings = await GetAttendanceSettingsAsync(db, tx, device.ClientId);
            if (settings.Id == 0) { results.Add(Reject("Save attendance shift and hours settings before importing punches.")); continue; }
            var date = AttendancePunchCalculator.AttendanceDate(local, settings);
            if (await IsLeavePeriodLockedAsync(db, tx, device.ClientId, employee.Id, date, date))
            { results.Add(Reject("Attendance belongs to a submitted or completed payroll period.")); continue; }
            var daily = await db.QueryFirstOrDefaultAsync<MachineDaily>("SELECT status Status,COALESCE(remarks,'') Remarks FROM employee_daily_attendance WHERE client_id=@ClientId AND employee_id=@EmployeeId AND attendance_date=@Date FOR UPDATE", new { device.ClientId, EmployeeId = employee.Id, Date = date }, tx);
            if (daily is not null && !(daily.Remarks.StartsWith("Machine attendance:", StringComparison.Ordinal) || daily.Remarks.StartsWith("Mobile Punch", StringComparison.Ordinal) || daily.Status is "A" or "Absent" && daily.Remarks.Length == 0))
            { results.Add(Reject("Existing leave, holiday, manual or approved attendance is protected. Use regularization.")); continue; }
            await db.ExecuteAsync(@"INSERT INTO employee_attendance_punches
(client_id,employee_id,client_request_id,action,captured_at,latitude,longitude,accuracy_meters,validation_status,decision,reason,device_id,network_type,app_version,camera_capture_confirmed,biometric_confirmed)
VALUES(@ClientId,@EmployeeId,@Key,@Action,@At,0,0,0,'RegisteredMachine','Accepted','',@DeviceId,'Machine','machine-v1',FALSE,FALSE)", new { device.ClientId, EmployeeId = employee.Id, Key = key, Action = action, At = local, device.DeviceId }, tx);
            var calculated = await ProjectRecordedPunchesAsync(db, tx, device.ClientId, employee.Id, date, settings, integration.Rules, "Machine attendance:");
            await tx.CommitAsync();
            results.Add(new(punch.PunchId, "Accepted", AttendanceDate: date, TotalHours: calculated.Hours, PayableValue: calculated.Payable, Remarks: calculated.Remarks));
        }
        return results;
    }

    private static async Task<AttendancePunchCalculator.Result> ProjectRecordedPunchesAsync(MySqlConnection db, MySqlTransaction tx, int clientId, int employeeId, DateTime date, AttendanceSettings settings, AttendanceRules rules, string source)
    {
        var bounds = AttendancePunchCalculator.Bounds(date, settings);
        var raw = await db.QueryAsync<AttendancePunchCalculator.Punch>("SELECT action Action,captured_at At FROM employee_attendance_punches WHERE client_id=@ClientId AND employee_id=@EmployeeId AND captured_at>=@Start AND captured_at<@End AND decision IN ('Accepted','AcceptedWithReason','SubmittedWithReason') ORDER BY captured_at,id", new { ClientId = clientId, EmployeeId = employeeId, bounds.Start, bounds.End }, tx);
        var calculated = AttendancePunchCalculator.Calculate(raw, date, settings, rules);
        var remarks = source + " " + (calculated.Remarks.Length > 0 ? calculated.Remarks : "Complete");
        await db.ExecuteAsync(@"INSERT INTO employee_daily_attendance(client_id,employee_id,attendance_date,status,payable_value,check_in_time,check_out_time,total_hours,remarks)
VALUES(@ClientId,@EmployeeId,@Date,@Status,@Payable,@CheckIn,@CheckOut,@Hours,@Remarks)
ON DUPLICATE KEY UPDATE status=VALUES(status),payable_value=VALUES(payable_value),check_in_time=VALUES(check_in_time),check_out_time=VALUES(check_out_time),total_hours=VALUES(total_hours),remarks=VALUES(remarks)", new { ClientId = clientId, EmployeeId = employeeId, Date = date, Status = calculated.Payable > 0 ? "Present" : "A", calculated.Payable, calculated.CheckIn, calculated.CheckOut, calculated.Hours, Remarks = remarks }, tx);
        await RollupMobileAttendanceAsync(db, tx, clientId, employeeId, date);
        return calculated;
    }
}
