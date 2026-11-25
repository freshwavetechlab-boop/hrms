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
    private sealed class EmployeeLinkRouteContext
    {
        public int? ClientId { get; set; }
        public int? PayRunId { get; set; }
        public string? Month { get; set; }
        public string? FromDate { get; set; }
        public string? ToDate { get; set; }
        public string? FinancialYear { get; set; }
    }

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
            new("payroll", "pay run row", "payrunemployees", "EmployeeId", "/reports/payroll-reports?report=employee-payroll-history"),
            new("adjustments", "payroll adjustment", "payrolladjustments", "EmployeeId", "/payroll/adjustments"),
            new("attendance-policy", "attendance group mapping", "attendance_group_employees", "employee_id", "/settings/leave-attendance/attendance-policies"),
            new("geofence", "geo fence mapping", "attendance_geo_fence_rule_employees", "employee_id", "/settings/leave-attendance/geo-fencing"),
            new("monthly-attendance", "monthly attendance row", "employee_monthly_attendance", "employee_id", "/reports/attendance-reports?report=monthly-attendance"),
            new("daily-attendance", "daily attendance row", "employee_daily_attendance", "employee_id", "/reports/attendance-reports?report=daily-attendance"),
            new("punches", "attendance punch", "employee_attendance_punches", "employee_id", "/reports/attendance-reports?report=attendance-punches"),
            new("leave-balances", "leave balance", "employee_leave_balances", "employee_id", "/reports/leave-reports?report=leave-balance"),
            new("leave-requests", "ESS leave request", "essleaverequests", "EmployeeId", "/reports/leave-reports?report=leave-approval-status"),
            new("tax-declarations", "tax declaration", "employee_tax_declaration_headers", "employee_id", "/payroll/tax-profile"),
            new("tax-computations", "tax computation", "tax_computation_snapshots", "employee_id", "/reports/tax-reports?report=tax-computation-history")
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
        var linkedUsers = columns.Contains("authusers.EmployeeId")
            ? (await db.QueryAsync<EmployeeLinkedLogin>("SELECT Id,DisplayName,Email,IsActive FROM authusers WHERE EmployeeId=@id ORDER BY IsActive DESC,DisplayName,Id", new { id })).ToList()
            : [];
        // Carry an existing period, not today's default, into destination pages.
        // Optional modules are queried only when this employee has references.
        var contextSql = new Dictionary<string, string>
        {
            ["payroll"] = "SELECT r.ClientId,r.Id PayRunId,r.PayPeriod Month FROM payrunemployees p JOIN payruns r ON r.Id=p.PayRunId WHERE p.EmployeeId=@id ORDER BY r.PayPeriod DESC,r.Id DESC LIMIT 1",
            ["adjustments"] = "SELECT ClientId,PayPeriod Month FROM payrolladjustments WHERE EmployeeId=@id ORDER BY PayPeriod DESC,Id DESC LIMIT 1",
            ["monthly-attendance"] = "SELECT client_id ClientId,MAX(attendance_month) Month FROM employee_monthly_attendance WHERE employee_id=@id GROUP BY client_id ORDER BY Month DESC LIMIT 1",
            ["daily-attendance"] = "SELECT client_id ClientId,DATE_FORMAT(MIN(attendance_date),'%Y-%m-%d') FromDate,DATE_FORMAT(MAX(attendance_date),'%Y-%m-%d') ToDate FROM employee_daily_attendance WHERE employee_id=@id GROUP BY client_id ORDER BY ToDate DESC LIMIT 1",
            ["punches"] = "SELECT client_id ClientId,DATE_FORMAT(MIN(captured_at),'%Y-%m-%d') FromDate,DATE_FORMAT(MAX(captured_at),'%Y-%m-%d') ToDate FROM employee_attendance_punches WHERE employee_id=@id GROUP BY client_id ORDER BY ToDate DESC LIMIT 1",
            ["leave-balances"] = "SELECT lt.client_id ClientId,DATE_FORMAT(MAX(b.balance_date),'%Y-%m') Month FROM employee_leave_balances b JOIN leave_types lt ON lt.id=b.leave_type_id WHERE b.employee_id=@id GROUP BY lt.client_id ORDER BY Month DESC LIMIT 1",
            ["leave-requests"] = "SELECT ClientId,DATE_FORMAT(MIN(FromDate),'%Y-%m-%d') FromDate,DATE_FORMAT(MAX(ToDate),'%Y-%m-%d') ToDate FROM essleaverequests WHERE EmployeeId=@id GROUP BY ClientId ORDER BY ToDate DESC LIMIT 1",
            ["tax-declarations"] = "SELECT client_id ClientId,financial_year FinancialYear FROM employee_tax_declaration_headers WHERE employee_id=@id ORDER BY financial_year DESC,id DESC LIMIT 1",
            ["tax-computations"] = "SELECT client_id ClientId,pay_period Month,financial_year FinancialYear FROM tax_computation_snapshots WHERE employee_id=@id ORDER BY pay_period DESC,id DESC LIMIT 1"
        };
        var contexts = new Dictionary<string, EmployeeLinkRouteContext>();
        var contextKeys = available.Where(row => counts.GetValueOrDefault(row.Key) > 0 && contextSql.ContainsKey(row.Key)).Select(row => row.Key).ToArray();
        if (contextKeys.Length > 0)
        {
            using var grid = await db.QueryMultipleAsync(string.Join(";\n", contextKeys.Select(key => contextSql[key])), new { id });
            foreach (var key in contextKeys)
                if (await grid.ReadFirstOrDefaultAsync<EmployeeLinkRouteContext>() is { } context) contexts[key] = context;
        }
        string Route(EmployeeDependencySpec source)
        {
            var context = contexts.GetValueOrDefault(source.Key);
            var parameters = new Dictionary<string, string>
            {
                ["clientId"] = (context?.ClientId ?? employee.ClientId).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["employeeId"] = id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["source"] = "employee-links", ["employeeLink"] = source.Key
            };
            void Add(string key, string? value) { if (!string.IsNullOrWhiteSpace(value)) parameters[key] = value; }
            Add("month", context?.Month); Add("fromDate", context?.FromDate); Add("toDate", context?.ToDate);
            Add("financialYear", context?.FinancialYear); Add("payRunId", context?.PayRunId?.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (source.Key == "manager-users") Add("managerUserIds", string.Join(",", linkedUsers.Select(row => row.Id)));
            return source.Route + (source.Route.Contains('?') ? "&" : "?")
                + string.Join("&", parameters.Select(pair => pair.Key + "=" + Uri.EscapeDataString(pair.Value)));
        }
        var dependencies = available.Where(row => counts.GetValueOrDefault(row.Key) > 0)
            .Select(row => new EmployeeLinkedRecord(row.Key, row.Label, counts[row.Key], Route(row))).ToList();
        var links = dependencies.Select(row => $"{row.Count} {row.Label}{(row.Count == 1 ? "" : "s")}").ToList();
        var name = $"{employee.FirstName} {employee.LastName}".Trim();
        return new EmployeeDeletePreview(id, employee.EmployeeCode, string.IsNullOrWhiteSpace(name) ? employee.EmployeeCode : name, links, links.Count == 0)
        {
            ClientId = employee.ClientId, LinkedUsers = linkedUsers, Dependencies = dependencies
        };
    }
}
