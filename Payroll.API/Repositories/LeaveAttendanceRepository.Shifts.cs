using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using System.Data;
using System.Text.RegularExpressions;

namespace Payroll.API.Repositories;

public partial class LeaveAttendanceRepository
{
    internal const string ShiftSelectSql = @"SELECT id Id,client_id ClientId,shift_code ShiftCode,shift_name ShiftName,
shift_type ShiftType,start_time StartTime,end_time EndTime,is_overnight IsOvernight,grace_minutes GraceMinutes,
break_minutes BreakMinutes,minimum_full_day_hours MinimumFullDayHours,minimum_half_day_hours MinimumHalfDayHours,
effective_from EffectiveFrom,effective_to EffectiveTo,is_active IsActive,created_at CreatedAt,updated_at UpdatedAt FROM attendance_shifts";

    public async Task InitializeShiftsAsync()
    {
        await using var db = CreateConnection(); await db.OpenAsync();
        await InitializeShiftsAsync(db);
    }

    private static async Task InitializeShiftsAsync(MySqlConnection db)
    {
        await db.ExecuteAsync(@"CREATE TABLE IF NOT EXISTS attendance_shifts (
id INT PRIMARY KEY AUTO_INCREMENT, client_id INT NOT NULL, shift_code VARCHAR(50) NOT NULL, shift_name VARCHAR(180) NOT NULL,
shift_type VARCHAR(20) NOT NULL DEFAULT 'Fixed', start_time TIME NULL, end_time TIME NULL, is_overnight BOOLEAN NOT NULL DEFAULT FALSE,
grace_minutes INT NOT NULL DEFAULT 0, break_minutes INT NOT NULL DEFAULT 0,
minimum_full_day_hours DECIMAL(5,2) NOT NULL DEFAULT 8, minimum_half_day_hours DECIMAL(5,2) NOT NULL DEFAULT 4,
effective_from DATE NOT NULL, effective_to DATE NULL, is_active BOOLEAN NOT NULL DEFAULT TRUE,
created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP, updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
UNIQUE KEY ux_attendance_shifts_client_code(client_id,shift_code), UNIQUE KEY ux_attendance_shifts_id_client(id,client_id),
CONSTRAINT fk_attendance_shifts_client FOREIGN KEY(client_id) REFERENCES clients(Id) ON DELETE CASCADE);");
        foreach (var table in new[] { "attendance_settings", "attendance_groups" })
        {
            await EnsureColumnAsync(db, table, "shift_id", "INT NULL AFTER client_id");
            await EnsureForeignKeyAsync(db, table, $"fk_{table}_shift_client", "FOREIGN KEY (shift_id,client_id) REFERENCES attendance_shifts(id,client_id)");
        }
    }

    public async Task<IEnumerable<AttendanceShift>> GetShiftsAsync(int clientId)
    {
        await using var db = CreateConnection(); await db.OpenAsync();
        return await db.QueryAsync<AttendanceShift>(ShiftSelectSql + " WHERE client_id=@ClientId ORDER BY shift_name,id", new { ClientId = clientId });
    }

    public static string? ValidateShift(AttendanceShift shift)
    {
        if (shift.ClientId <= 0) return "Client is required.";
        if (string.IsNullOrWhiteSpace(shift.ShiftCode)) return "Shift code is required.";
        if (!Regex.IsMatch(shift.ShiftCode.Trim(), @"^[A-Za-z0-9][A-Za-z0-9_-]{0,49}$")) return "Shift code must be 1–50 letters, numbers, underscores or hyphens.";
        if (string.IsNullOrWhiteSpace(shift.ShiftName) || shift.ShiftName.Trim().Length > 180) return "Shift name is required and must be at most 180 characters.";
        if (shift.ShiftType is not ("Fixed" or "Flexible")) return "Choose Fixed or Flexible shift type.";
        if ((shift.ShiftType == "Fixed" || shift.IsOvernight) && (shift.StartTime is null || shift.EndTime is null)) return "Start and end times are required for fixed or overnight shifts.";
        if (shift.StartTime is TimeSpan start && (start < TimeSpan.Zero || start >= TimeSpan.FromDays(1)) || shift.EndTime is TimeSpan end && (end < TimeSpan.Zero || end >= TimeSpan.FromDays(1))) return "Enter valid start/end times.";
        if (shift.StartTime.HasValue && shift.EndTime.HasValue && (shift.IsOvernight ? shift.EndTime >= shift.StartTime : shift.EndTime <= shift.StartTime)) return "End time must be after start time, or before start time for an overnight shift.";
        if (shift.GraceMinutes is < 0 or > 1440 || shift.BreakMinutes is < 0 or > 1440) return "Grace and break minutes must be between 0 and 1440.";
        if (shift.MinimumHalfDayHours <= 0 || shift.MinimumFullDayHours > 24 || shift.MinimumFullDayHours <= shift.MinimumHalfDayHours) return "Minimum full-day hours must exceed positive half-day hours and cannot exceed 24.";
        if (shift.EffectiveFrom == default || shift.EffectiveTo?.Date < shift.EffectiveFrom.Date) return "Enter a valid effective-from date; effective-to cannot be earlier.";
        return null;
    }

    internal static async Task<string?> ValidateShiftReferenceAsync(MySqlConnection db, IDbTransaction? tx, int clientId, int? shiftId)
    {
        if (shiftId is null) return null;
        return await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM attendance_shifts WHERE id=@ShiftId AND client_id=@ClientId AND is_active=TRUE", new { ShiftId = shiftId, ClientId = clientId }, tx) == 1
            ? null : "Choose an active shift belonging to this client.";
    }

    public async Task<(AttendanceShift? Shift, string? Error)> SaveShiftAsync(AttendanceShift request, int id = 0)
    {
        var error = ValidateShift(request); if (error is not null) return (null, error);
        request.Id = id; request.ShiftCode = request.ShiftCode.Trim().ToUpperInvariant(); request.ShiftName = request.ShiftName.Trim();
        await using var db = CreateConnection(); await db.OpenAsync();
        if (await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM clients WHERE Id=@ClientId AND IsActive=TRUE", request) != 1) return (null, "Select an active client.");
        try
        {
            if (id == 0)
                id = (int)await db.ExecuteScalarAsync<long>(@"INSERT INTO attendance_shifts(client_id,shift_code,shift_name,shift_type,start_time,end_time,is_overnight,grace_minutes,break_minutes,minimum_full_day_hours,minimum_half_day_hours,effective_from,effective_to,is_active)
VALUES(@ClientId,@ShiftCode,@ShiftName,@ShiftType,@StartTime,@EndTime,@IsOvernight,@GraceMinutes,@BreakMinutes,@MinimumFullDayHours,@MinimumHalfDayHours,@EffectiveFrom,@EffectiveTo,@IsActive); SELECT LAST_INSERT_ID();", request);
            else if (await db.ExecuteAsync(@"UPDATE attendance_shifts SET shift_code=@ShiftCode,shift_name=@ShiftName,shift_type=@ShiftType,start_time=@StartTime,end_time=@EndTime,is_overnight=@IsOvernight,grace_minutes=@GraceMinutes,break_minutes=@BreakMinutes,minimum_full_day_hours=@MinimumFullDayHours,minimum_half_day_hours=@MinimumHalfDayHours,effective_from=@EffectiveFrom,effective_to=@EffectiveTo,is_active=@IsActive WHERE id=@Id AND client_id=@ClientId", request) == 0)
                return (null, "Shift was not found for this client.");
        }
        catch (MySqlException exception) when (exception.Number == 1062) { return (null, "This shift code already exists for this client."); }
        return (await db.QuerySingleAsync<AttendanceShift>(ShiftSelectSql + " WHERE id=@Id AND client_id=@ClientId", new { Id = id, request.ClientId }), null);
    }

    public async Task<bool> DeactivateShiftAsync(int id, int clientId)
    {
        await using var db = CreateConnection(); await db.OpenAsync();
        return await db.ExecuteAsync("UPDATE attendance_shifts SET is_active=FALSE WHERE id=@Id AND client_id=@ClientId", new { Id = id, ClientId = clientId }) > 0;
    }

    private static string ShiftRowRemarks(string? original, AttendanceSettings settings, Services.AttendancePunchCalculator.Result? result)
    {
        if (result is null) return original ?? "";
        if (result.Remarks == "Manual half day") return string.IsNullOrWhiteSpace(original) ? result.Remarks
            : original.Contains(result.Remarks, StringComparison.Ordinal) ? original : $"{original} | {result.Remarks}";
        var text = original ?? "";
        var previous = text.IndexOf("| Shift:", StringComparison.Ordinal);
        if (previous >= 0) text = text[..previous].TrimEnd();
        return $"{text} | Shift: {settings.Shift!.ShiftCode}; {result.Remarks}".Trim();
    }
}
