using Dapper;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public class DashboardRepository(IConfiguration configuration)
{
    private MySqlConnection CreateConnection()
    {
        var connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Connection string 'Default' is not configured.");
        return new MySqlConnection(connectionString);
    }

    public async Task<DashboardSnapshot> GetAsync(int clientId, AuthUser user)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();

        var month = DateTime.Today.ToString("yyyy-MM");
        var parameters = new { ClientId = clientId, Month = month, UserId = user.Id };
        var sections = DashboardAccess.For(user);

        var clients = (await connection.QueryAsync<DashboardClient>(
            "SELECT Id, Name FROM clients WHERE IsActive = TRUE ORDER BY Name;")).ToList();

        var activeEmployees = sections.Workforce || sections.Attendance
            ? await connection.ExecuteScalarAsync<int>(@"
SELECT COUNT(*)
FROM employees
WHERE IsActive = TRUE
  AND (@ClientId = 0 OR ClientId = @ClientId);", parameters)
            : 0;

        var portalUsers = sections.Workforce
            ? await connection.ExecuteScalarAsync<int>(@"
SELECT COUNT(*)
FROM employees
WHERE IsActive = TRUE
  AND PortalAccess = TRUE
  AND (@ClientId = 0 OR ClientId = @ClientId);", parameters)
            : 0;

        var payRunStatuses = sections.Payroll
            ? (await connection.QueryAsync<DashboardStatusTotal>(@"
SELECT Status, COUNT(*) AS Count, COALESCE(SUM(NetPay), 0) AS NetPay
FROM payruns
WHERE PayPeriod = @Month
  AND (@ClientId = 0 OR ClientId = @ClientId)
GROUP BY Status
ORDER BY Status;", parameters)).ToList()
            : [];

        var monthlyPayRuns = payRunStatuses.Sum(item => item.Count);
        var monthlyNetPay = payRunStatuses.Sum(item => item.NetPay);

        var attendanceRows = sections.Attendance
            ? await connection.ExecuteScalarAsync<int>(@"
SELECT COUNT(DISTINCT employee_id)
FROM employee_monthly_attendance
WHERE attendance_month = @Month
  AND (@ClientId = 0 OR client_id = @ClientId);", parameters)
            : 0;

        var attendanceIssues = sections.Attendance
            ? await connection.ExecuteScalarAsync<int>(@"
SELECT COUNT(*)
FROM employee_monthly_attendance
WHERE attendance_month = @Month
  AND (@ClientId = 0 OR client_id = @ClientId)
  AND (
      working_days <= 0
      OR present_days > working_days
      OR payable_days > working_days
      OR ABS((payable_days + lop_days) - working_days) > 0.01
  );", parameters)
            : 0;

        var pendingTasks = sections.Approvals
            ? await connection.ExecuteScalarAsync<int>(@"
SELECT COUNT(*)
FROM workflowtasks
WHERE ApproverUserId = @UserId
  AND Status = 'Pending';", parameters)
            : 0;

        var pendingLeaveRequests = sections.Approvals
            ? await connection.ExecuteScalarAsync<int>(@"
SELECT COUNT(*)
FROM essleaverequests
WHERE Status IN ('Pending', 'Pending Approval')
  AND (@ClientId = 0 OR ClientId = @ClientId);", parameters)
            : 0;

        var payrollExceptions = sections.Payroll
            ? await connection.ExecuteScalarAsync<int>(@"
SELECT COUNT(*)
FROM payroll_validation_issues v
JOIN payruns r ON r.Id = v.PayRunId
WHERE r.PayPeriod = @Month
  AND v.IsBlocking = TRUE
  AND (@ClientId = 0 OR r.ClientId = @ClientId);", parameters)
            : 0;

        var recentPayRuns = sections.Payroll
            ? (await connection.QueryAsync<DashboardRecentPayRun>(@"
SELECT
    r.Id,
    r.ClientId,
    COALESCE(r.ClientName, c.Name, '') AS ClientName,
    r.PayPeriod,
    r.RunType,
    r.RunName,
    r.Status,
    r.NetPay,
    COUNT(e.Id) AS EmployeeCount,
    r.UpdatedAt
FROM payruns r
LEFT JOIN clients c ON c.Id = r.ClientId
LEFT JOIN payrunemployees e ON e.PayRunId = r.Id AND e.IsSkipped = FALSE
WHERE (@ClientId = 0 OR r.ClientId = @ClientId)
GROUP BY r.Id, r.ClientId, r.ClientName, c.Name, r.PayPeriod, r.RunType, r.RunName, r.Status, r.NetPay, r.UpdatedAt
ORDER BY r.UpdatedAt DESC, r.Id DESC
LIMIT 5;", parameters)).ToList()
            : [];

        return new DashboardSnapshot
        {
            Month = month,
            SelectedClientId = clientId,
            Clients = clients,
            Sections = sections.Visible,
            Metrics = new DashboardMetrics
            {
                ActiveEmployees = activeEmployees,
                PortalUsers = portalUsers,
                CurrentMonthPayRuns = monthlyPayRuns,
                CurrentMonthNetPay = monthlyNetPay,
                AttendanceRecorded = attendanceRows,
                AttendanceMissing = Math.Max(activeEmployees - attendanceRows, 0),
                AttendanceIssues = attendanceIssues,
                PendingTasks = pendingTasks,
                PendingLeaveRequests = pendingLeaveRequests,
                PayrollExceptions = payrollExceptions
            },
            PayRunStatuses = payRunStatuses,
            RecentPayRuns = recentPayRuns
        };
    }
}

public class DashboardSnapshot
{
    public string Month { get; set; } = string.Empty;
    public int SelectedClientId { get; set; }
    public List<string> Sections { get; set; } = [];
    public List<DashboardClient> Clients { get; set; } = [];
    public DashboardMetrics Metrics { get; set; } = new();
    public List<DashboardStatusTotal> PayRunStatuses { get; set; } = [];
    public List<DashboardRecentPayRun> RecentPayRuns { get; set; } = [];
}

public class DashboardClient
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class DashboardMetrics
{
    public int ActiveEmployees { get; set; }
    public int PortalUsers { get; set; }
    public int CurrentMonthPayRuns { get; set; }
    public decimal CurrentMonthNetPay { get; set; }
    public int AttendanceRecorded { get; set; }
    public int AttendanceMissing { get; set; }
    public int AttendanceIssues { get; set; }
    public int PendingTasks { get; set; }
    public int PendingLeaveRequests { get; set; }
    public int PayrollExceptions { get; set; }
}

public class DashboardStatusTotal
{
    public string Status { get; set; } = string.Empty;
    public int Count { get; set; }
    public decimal NetPay { get; set; }
}

public class DashboardRecentPayRun
{
    public int Id { get; set; }
    public int ClientId { get; set; }
    public string ClientName { get; set; } = string.Empty;
    public string PayPeriod { get; set; } = string.Empty;
    public string RunType { get; set; } = string.Empty;
    public string RunName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public decimal NetPay { get; set; }
    public int EmployeeCount { get; set; }
    public DateTime UpdatedAt { get; set; }
}

internal sealed class DashboardAccess
{
    public bool Workforce { get; init; }
    public bool Payroll { get; init; }
    public bool Attendance { get; init; }
    public bool Approvals { get; init; }
    public List<string> Visible { get; init; } = [];

    public static DashboardAccess For(AuthUser user)
    {
        var permissions = user.Permissions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var admin = permissions.Contains("security.manage");

        var workforce = admin || permissions.Contains("dashboard.workforce.view") || permissions.Contains("employees.manage");
        var payroll = admin || permissions.Contains("dashboard.payroll.view") || permissions.Contains("payroll.run") || permissions.Contains("payroll.approve") || permissions.Contains("payroll.payments");
        var attendance = admin || permissions.Contains("dashboard.attendance.view") || permissions.Contains("settings.manage");
        var approvals = admin || permissions.Contains("dashboard.approvals.view") || permissions.Contains("workflow.manage");

        var visible = new List<string>();
        if (workforce) visible.Add("workforce");
        if (payroll) visible.Add("payroll");
        if (attendance) visible.Add("attendance");
        if (approvals) visible.Add("approvals");

        return new DashboardAccess
        {
            Workforce = workforce,
            Payroll = payroll,
            Attendance = attendance,
            Approvals = approvals,
            Visible = visible
        };
    }
}
