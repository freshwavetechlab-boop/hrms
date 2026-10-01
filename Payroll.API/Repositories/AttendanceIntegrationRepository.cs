using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

// Integration configuration lives in the existing module JSON, never in new tables.
public class AttendanceIntegrationRepository(IConfiguration configuration)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private MySqlConnection Db() => new(configuration.GetConnectionString("Default"));
    internal class StoredDevice : AttendanceDevice { public string TokenHash { get; set; } = ""; }
    internal class IntegrationSettings { public List<StoredDevice> Devices { get; set; } = []; public AttendanceRules Rules { get; set; } = new(); }
    private class GapClient { public int Id { get; set; } public string Name { get; set; } = ""; public string? IntegrationJson { get; set; } public bool HasShift { get; set; } public bool HasLocation { get; set; } public bool HasRequestType { get; set; } public bool HasPolicy { get; set; } public bool HasWorkflow { get; set; } public bool MissingManager { get; set; } }

    internal static async Task<IntegrationSettings> ReadAsync(MySqlConnection db, System.Data.IDbTransaction? tx, int clientId, bool locked = false)
    {
        var json = await db.ExecuteScalarAsync<string?>("SELECT JSON_EXTRACT(SettingsJson,'$.attendanceIntegration') FROM modulesettings WHERE client_id=@ClientId AND ModuleCode='leave_attendance'" + (locked ? " FOR UPDATE" : ""), new { ClientId = clientId }, tx);
        return string.IsNullOrWhiteSpace(json) || json == "null" ? new() : JsonSerializer.Deserialize<IntegrationSettings>(json, JsonOptions) ?? new();
    }

    internal static async Task WriteAsync(MySqlConnection db, System.Data.IDbTransaction tx, int clientId, IntegrationSettings settings) =>
        await db.ExecuteAsync("UPDATE modulesettings SET SettingsJson=JSON_SET(COALESCE(SettingsJson,JSON_OBJECT()),'$.attendanceIntegration',CAST(@Json AS JSON)) WHERE client_id=@ClientId AND ModuleCode='leave_attendance'", new { ClientId = clientId, Json = JsonSerializer.Serialize(settings) }, tx);

    internal static async Task EnsureRowAsync(MySqlConnection db, System.Data.IDbTransaction tx, int clientId) =>
        await db.ExecuteAsync("INSERT INTO modulesettings(client_id,ModuleCode,IsEnabled,SettingsJson) VALUES(@ClientId,'leave_attendance',FALSE,JSON_OBJECT()) ON DUPLICATE KEY UPDATE ModuleCode=VALUES(ModuleCode)", new { ClientId = clientId }, tx);

    public async Task<IEnumerable<AttendanceDevice>> ListAsync(int clientId)
    {
        await using var db = Db(); await db.OpenAsync();
        return (await ReadAsync(db, null, clientId)).Devices.Select(PublicDevice).ToArray();
    }

    private static AttendanceDevice PublicDevice(StoredDevice device) => new()
    { DeviceId = device.DeviceId, Name = device.Name, ClientId = device.ClientId, WorkLocationId = device.WorkLocationId, IsActive = device.IsActive, TokenExpiresAt = device.TokenExpiresAt, HasToken = device.TokenHash.Length > 0, TokenHint = device.TokenHash.Length == 0 ? "" : device.TokenHint.Length > 0 ? device.TokenHint : "att_••••••" };

    public async Task<string?> SaveDeviceAsync(AttendanceDeviceRegistration request)
    {
        request.DeviceId = request.DeviceId?.Trim() ?? ""; request.Name = request.Name?.Trim() ?? "";
        if (request.DeviceId.Length is < 1 or > 100 || request.Name.Length is < 1 or > 120) return "Device id and name are required (maximum 100 / 120 characters).";
        await using var db = Db(); await db.OpenAsync(); await using var tx = await db.BeginTransactionAsync();
        if (await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM worklocations w JOIN clients c ON c.Id=w.ClientId WHERE w.Id=@WorkLocationId AND w.ClientId=@ClientId AND w.IsActive=TRUE AND c.IsActive=TRUE", request, tx) == 0) return "Select an active work location belonging to this client.";
        await EnsureRowAsync(db, tx, request.ClientId);
        var settings = await ReadAsync(db, tx, request.ClientId, true);
        var device = settings.Devices.FirstOrDefault(x => x.DeviceId == request.DeviceId);
        if (device is null) { device = new StoredDevice { DeviceId = request.DeviceId, ClientId = request.ClientId }; settings.Devices.Add(device); }
        // Moving a device invalidates its previous location-bound credential.
        if (device.WorkLocationId != request.WorkLocationId || !request.IsActive) { device.TokenHash = ""; device.TokenHint = ""; device.TokenExpiresAt = null; }
        device.Name = request.Name; device.WorkLocationId = request.WorkLocationId; device.IsActive = request.IsActive;
        await WriteAsync(db, tx, request.ClientId, settings); await tx.CommitAsync(); return null;
    }

    public async Task<(string? Token, string? Error)> TokenAsync(int clientId, string deviceId, int validDays, bool revoke = false)
    {
        if (!revoke && validDays is < 1 or > 365) return (null, "Token validity must be 1–365 days.");
        await using var db = Db(); await db.OpenAsync(); await using var tx = await db.BeginTransactionAsync();
        var settings = await ReadAsync(db, tx, clientId, true);
        var device = settings.Devices.FirstOrDefault(x => x.DeviceId == deviceId);
        if (device is null || (!revoke && !device.IsActive)) return (null, "Active device not found.");
        var token = revoke ? null : "att_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        device.TokenHash = token is null ? "" : Hash(token); device.TokenExpiresAt = token is null ? null : DateTime.UtcNow.AddDays(validDays);
        device.TokenHint = token is null ? "" : "att_••••" + token[^6..];
        await WriteAsync(db, tx, clientId, settings); await tx.CommitAsync(); return (token, null);
    }

    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public async Task<AttendanceDevice?> AuthenticateAsync(string token)
    {
        if (token.Length != 68 || !token.StartsWith("att_", StringComparison.Ordinal)) return null;
        await using var db = Db(); await db.OpenAsync();
        var hash = Hash(token);
        var clients = await db.QueryAsync<int>("SELECT m.client_id FROM modulesettings m JOIN clients c ON c.Id=m.client_id AND c.IsActive=TRUE WHERE m.ModuleCode='leave_attendance' AND JSON_SEARCH(m.SettingsJson,'one',@Hash,NULL,'$.attendanceIntegration.Devices[*].TokenHash') IS NOT NULL", new { Hash = hash });
        foreach (var clientId in clients)
        {
            var device = (await ReadAsync(db, null, clientId)).Devices.FirstOrDefault(d => d.TokenHash == hash && d.IsActive && d.TokenExpiresAt > DateTime.UtcNow);
            if (device is not null) return PublicDevice(device);
        }
        return null;
    }

    public async Task<IEnumerable<AttendanceConfigurationGap>> GapsAsync(int? clientId, AuthUser user)
    {
        await using var db = Db(); await db.OpenAsync();
        var clients = await db.QueryAsync<GapClient>(@"SELECT c.Id,c.Name,JSON_EXTRACT(m.SettingsJson,'$.attendanceIntegration') IntegrationJson,
EXISTS(SELECT 1 FROM attendance_settings WHERE client_id=c.Id) HasShift,
EXISTS(SELECT 1 FROM worklocations WHERE ClientId=c.Id AND IsActive=TRUE) HasLocation,
EXISTS(SELECT 1 FROM leave_types l JOIN leave_type_policies p ON p.leave_type_id=l.id WHERE l.client_id=c.Id AND l.is_active=TRUE AND p.attendance_action='Mark as present') HasRequestType,
EXISTS(SELECT 1 FROM attendance_groups WHERE client_id=c.Id AND is_active=TRUE) AND NOT EXISTS(SELECT 1 FROM employees e WHERE e.ClientId=c.Id AND e.IsActive=TRUE AND NOT EXISTS(SELECT 1 FROM attendance_group_employees a JOIN attendance_groups g ON g.id=a.attendance_group_id AND g.client_id=c.Id AND g.is_active=TRUE WHERE a.employee_id=e.Id)) HasPolicy,
EXISTS(SELECT 1 FROM workflowmasters w JOIN workflowstages s ON s.WorkflowId=w.Id WHERE w.ResourceType IN ('LeaveRequest','AttendanceRegularization') AND w.IsActive=TRUE AND s.ApproverType='Reporting Manager' AND (w.ClientId=c.Id OR w.ClientId IS NULL)) HasWorkflow,
EXISTS(SELECT 1 FROM employees e WHERE e.ClientId=c.Id AND e.IsActive=TRUE AND NOT EXISTS(SELECT 1 FROM authusers u WHERE u.IsActive=TRUE AND (u.Id=e.ReportingManagerUserId OR (COALESCE(e.ReportingManagerUserId,0)=0 AND u.EmployeeId=e.ReportingManagerId)))) MissingManager
FROM clients c LEFT JOIN modulesettings m ON m.client_id=c.Id AND m.ModuleCode='leave_attendance'
WHERE c.IsActive=TRUE AND (@ClientId IS NULL OR c.Id=@ClientId)", new { ClientId = clientId });
        var gaps = new List<AttendanceConfigurationGap>();
        foreach (var client in clients)
        {
            var settings = string.IsNullOrWhiteSpace(client.IntegrationJson) || client.IntegrationJson == "null" ? new IntegrationSettings() : JsonSerializer.Deserialize<IntegrationSettings>(client.IntegrationJson, JsonOptions) ?? new();
            void Add(string key, string title, string route)
            {
                string[] permissions = key switch { "policy" => ["settings.manage", "attendance.manage"], "workflow" => ["workflow.manage"], "manager" => ["employees.manage"], _ => ["settings.manage", "client.settings.manage"] };
                if ((!user.ClientId.HasValue || user.ClientId == client.Id) && permissions.Any(p => user.Permissions.Contains(p, StringComparer.OrdinalIgnoreCase)))
                    gaps.Add(new($"attendance:{client.Id}:{key}", client.Id, client.Name, title, "Required attendance configuration is missing.", route + $"?clientId={client.Id}", permissions));
            }
            var route = "/settings/leave-attendance/attendance";
            if (!client.HasShift) Add("shift", "Save shift, workday hours and regularization settings", route);
            if (settings.Rules.LateGraceMinutes is null) Add("late", "Set late-coming grace minutes", route);
            if (settings.Rules.EarlyGraceMinutes is null) Add("early", "Set early-going grace minutes", route);
            if (settings.Rules.MonthlyMissPunchLimit is null) Add("miss", "Set monthly miss-punch limit", route);
            if (settings.Rules.MonthlyOdLimit is null) Add("od", "Set monthly OD limit", route);
            if (settings.Devices.All(d => !d.IsActive)) Add("device", "Add a punch machine and map its work location", route);
            else if (settings.Devices.All(d => !d.IsActive || d.TokenHash.Length == 0 || d.TokenExpiresAt is null || d.TokenExpiresAt <= DateTime.UtcNow)) Add("token", "Generate an attendance API key", "/workflows/api-catalog");
            if (!client.HasLocation) Add("location", "Add a client work location", "/settings/work-locations");
            if (!client.HasRequestType) Add("regularization-type", "Configure OD / miss-punch attendance request types", "/settings/leave-attendance/leave-types");
            if (!client.HasPolicy) Add("policy", "Set workweek and attendance cycle", "/settings/leave-attendance/attendance-policies");
            if (!client.HasWorkflow) Add("workflow", "Configure reporting-manager approval for leave / regularization", "/workflows/workflow-setup");
            if (client.MissingManager) Add("manager", "Assign active reporting-manager accounts to employees", "/employees/master");
        }
        return gaps;
    }
}
