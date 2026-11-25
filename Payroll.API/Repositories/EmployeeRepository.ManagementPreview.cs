using Dapper;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public partial class EmployeeRepository
{
    private sealed record EmployeeDependencySpec(string Key, string Label, string Table, string Column, string Route, string Filter = "", string? Predicate = null);
    private sealed class EmployeeDependencyCount
    {
        public string Key { get; set; } = "";
        public int Count { get; set; }
    }
    private sealed record EmployeeDependencyColumn(string TableName, string ColumnName);

    private async Task<EmployeeDeletePreview?> ReadManagementPreviewAsync(int id)
    {
        await using var db = Connection(); await db.OpenAsync();
        var employee = await db.QueryFirstOrDefaultAsync<Employee>("SELECT Id,ClientId,EmployeeCode,FirstName,LastName FROM employees WHERE Id=@id", new { id });
        if (employee is null) return null;
        // These are the same organization, attendance/leave, payroll and portal
        // areas surfaced in Client Config Check. Only this employee's actual
        // references are counted; nothing in this preview removes history.
        EmployeeDependencySpec[] sources =
        [
            new("reporting", "reporting employee", "employees", "ReportingManagerId", "/employees/org-structure", "AND IsActive=TRUE"),
            new("offers", "released offer employee link", "recruitment_document_roster", "EmployeeId", "/recruitment/offers-and-pre-onboarding"),
            new("manager-users", "reporting manager user mapping", "employees", "ReportingManagerUserId", "/employees/org-structure", "AND IsActive=TRUE",
                "ReportingManagerUserId IN (SELECT Id FROM authusers WHERE EmployeeId=@id)"),
            new("logins", "login user", "authusers", "EmployeeId", "/security/users", "AND IsActive=TRUE"),
            new("payroll", "pay run row", "payrunemployees", "EmployeeId", "/reports/payroll-reports?report=payslip-register"),
            new("adjustments", "payroll adjustment", "payrolladjustments", "EmployeeId", "/payroll/adjustments"),
            new("attendance-policy", "attendance group mapping", "attendance_group_employees", "employee_id", "/settings/leave-attendance/attendance-policies"),
            new("geofence", "geo fence mapping", "attendance_geo_fence_rule_employees", "employee_id", "/settings/leave-attendance/attendance"),
            new("monthly-attendance", "monthly attendance row", "employee_monthly_attendance", "employee_id", "/reports/attendance-reports?report=monthly-attendance"),
            new("daily-attendance", "daily attendance row", "employee_daily_attendance", "employee_id", "/reports/attendance-reports?report=daily-attendance"),
            new("punches", "attendance punch", "employee_attendance_punches", "employee_id", "/settings/leave-attendance/attendance"),
            new("leave-balances", "leave balance", "employee_leave_balances", "employee_id", "/settings/leave-attendance/leave-types"),
            new("leave-requests", "ESS leave request", "essleaverequests", "EmployeeId", "/settings/leave-attendance/leave-types"),
            new("tax-declarations", "tax declaration", "employee_tax_declaration_headers", "employee_id", "/employees/master"),
            new("tax-computations", "tax computation", "tax_computation_snapshots", "employee_id", "/employees/master")
        ];
        // Optional modules may not have created their tables yet. Inspect once,
        // then count all available dependency types in one read instead of one
        // network round trip for every module.
        var columns = (await db.QueryAsync<EmployeeDependencyColumn>(@"SELECT TABLE_NAME TableName,COLUMN_NAME ColumnName
FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME IN @Tables",
            new { Tables = sources.Select(row => row.Table).Distinct().ToArray() }))
            .Select(row => $"{row.TableName}.{row.ColumnName}").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var available = sources.Where(row => columns.Contains($"{row.Table}.{row.Column}")
            && (row.Key != "manager-users" || columns.Contains("authusers.EmployeeId"))).ToArray();
        var counts = available.Length == 0 ? new Dictionary<string, int>()
            : (await db.QueryAsync<EmployeeDependencyCount>(string.Join(" UNION ALL ", available.Select(row =>
                $"SELECT '{row.Key}' `Key`,COUNT(*) Count FROM `{row.Table}` WHERE {(row.Predicate ?? $"`{row.Column}`=@id")} {row.Filter}")), new { id }))
                .ToDictionary(row => row.Key, row => row.Count);
        var dependencies = available.Where(row => counts.GetValueOrDefault(row.Key) > 0)
            .Select(row => new EmployeeLinkedRecord(row.Key, row.Label, counts[row.Key],
                row.Route + (row.Route.Contains('?') ? "&" : "?") + $"clientId={employee.ClientId}&employeeId={id}"))
            .ToList();
        var linkedUsers = columns.Contains("authusers.EmployeeId")
            ? (await db.QueryAsync<EmployeeLinkedLogin>("SELECT Id,DisplayName,Email,IsActive FROM authusers WHERE EmployeeId=@id ORDER BY IsActive DESC,DisplayName,Id", new { id })).ToList()
            : [];
        var links = dependencies.Select(row => $"{row.Count} {row.Label}{(row.Count == 1 ? "" : "s")}").ToList();
        var name = $"{employee.FirstName} {employee.LastName}".Trim();
        return new EmployeeDeletePreview(id, employee.EmployeeCode, string.IsNullOrWhiteSpace(name) ? employee.EmployeeCode : name, links, links.Count == 0)
        {
            ClientId = employee.ClientId, LinkedUsers = linkedUsers, Dependencies = dependencies
        };
    }
}
