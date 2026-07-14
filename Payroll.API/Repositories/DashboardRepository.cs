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

        var departmentHeadcount = sections.Workforce
            ? (await connection.QueryAsync<DashboardChartPoint>(@"
SELECT COALESCE(NULLIF(TRIM(Department), ''), 'Not mapped') AS Label, COUNT(*) AS Value
FROM employees
WHERE IsActive = TRUE
  AND (@ClientId = 0 OR ClientId = @ClientId)
GROUP BY COALESCE(NULLIF(TRIM(Department), ''), 'Not mapped')
ORDER BY Value DESC, Label
LIMIT 8;", parameters)).ToList()
            : [];

        var locationHeadcount = sections.Workforce
            ? (await connection.QueryAsync<DashboardChartPoint>(@"
SELECT COALESCE(NULLIF(TRIM(w.Name), ''), 'Not mapped') AS Label, COUNT(*) AS Value
FROM employees e
LEFT JOIN worklocations w ON w.Id = e.WorkLocationId
WHERE e.IsActive = TRUE
  AND (@ClientId = 0 OR e.ClientId = @ClientId)
GROUP BY COALESCE(NULLIF(TRIM(w.Name), ''), 'Not mapped')
ORDER BY Value DESC, Label
LIMIT 8;", parameters)).ToList()
            : [];

        var payrollTrend = sections.Payroll
            ? (await connection.QueryAsync<DashboardPayrollTrendPoint>(@"
SELECT PayPeriod AS Month, COUNT(*) AS RunCount, COALESCE(SUM(PayrollCost), 0) AS PayrollCost, COALESCE(SUM(NetPay), 0) AS NetPay
FROM payruns
WHERE (@ClientId = 0 OR ClientId = @ClientId)
GROUP BY PayPeriod
ORDER BY PayPeriod DESC
LIMIT 6;", parameters)).OrderBy(item => item.Month).ToList()
            : [];

        var attendanceMix = sections.Attendance
            ? (await connection.QueryAsync<DashboardChartPoint>(@"
SELECT 'Recorded' AS Label, COUNT(DISTINCT employee_id) AS Value
FROM employee_monthly_attendance
WHERE attendance_month = @Month
  AND (@ClientId = 0 OR client_id = @ClientId)
UNION ALL
SELECT 'Missing' AS Label, GREATEST(@ActiveEmployees - COUNT(DISTINCT employee_id), 0) AS Value
FROM employee_monthly_attendance
WHERE attendance_month = @Month
  AND (@ClientId = 0 OR client_id = @ClientId)
UNION ALL
SELECT 'Issues' AS Label, COUNT(*) AS Value
FROM employee_monthly_attendance
WHERE attendance_month = @Month
  AND (@ClientId = 0 OR client_id = @ClientId)
  AND (
      working_days <= 0
      OR present_days > working_days
      OR payable_days > working_days
      OR ABS((payable_days + lop_days) - working_days) > 0.01
  );", new { ClientId = clientId, Month = month, ActiveEmployees = activeEmployees })).ToList()
            : [];

        var attendancePayability = sections.Attendance
            ? (await connection.QueryAsync<DashboardChartPoint>(@"
SELECT 'Present days' AS Label, COALESCE(SUM(present_days), 0) AS Value
FROM employee_monthly_attendance
WHERE attendance_month = @Month
  AND (@ClientId = 0 OR client_id = @ClientId)
UNION ALL
SELECT 'LOP days' AS Label, COALESCE(SUM(lop_days), 0) AS Value
FROM employee_monthly_attendance
WHERE attendance_month = @Month
  AND (@ClientId = 0 OR client_id = @ClientId);", parameters)).ToList()
            : [];

        var approvalStageBreakup = sections.Approvals
            ? (await connection.QueryAsync<DashboardChartPoint>(@"
SELECT COALESCE(NULLIF(TRIM(s.Name), ''), 'Pending') AS Label, COUNT(*) AS Value
FROM workflowtasks t
JOIN workflowstages s ON s.Id = t.StageId
WHERE t.ApproverUserId = @UserId
  AND t.Status = 'Pending'
GROUP BY COALESCE(NULLIF(TRIM(s.Name), ''), 'Pending')
ORDER BY Value DESC, Label
LIMIT 8;", parameters)).ToList()
            : [];

        var approvalActionMix = sections.Approvals
            ? (await connection.QueryAsync<DashboardChartPoint>(@"
SELECT COALESCE(NULLIF(TRIM(Status), ''), 'Actioned') AS Label, COUNT(*) AS Value
FROM workflowtasks
WHERE ApproverUserId = @UserId
  AND Status <> 'Pending'
GROUP BY COALESCE(NULLIF(TRIM(Status), ''), 'Actioned')
ORDER BY Value DESC, Label
LIMIT 8;", parameters)).ToList()
            : [];

        var designationHeadcount = sections.Workforce
            ? (await connection.QueryAsync<DashboardChartPoint>(@"
SELECT COALESCE(NULLIF(TRIM(Designation), ''), 'Not mapped') AS Label, COUNT(*) AS Value
FROM employees
WHERE IsActive = TRUE
  AND (@ClientId = 0 OR ClientId = @ClientId)
GROUP BY COALESCE(NULLIF(TRIM(Designation), ''), 'Not mapped')
ORDER BY Value DESC, Label
LIMIT 8;", parameters)).ToList()
            : [];

        var gradeHeadcount = sections.Workforce
            ? (await connection.QueryAsync<DashboardChartPoint>(@"
SELECT COALESCE(NULLIF(TRIM(Grade), ''), 'Not mapped') AS Label, COUNT(*) AS Value
FROM employees
WHERE IsActive = TRUE
  AND (@ClientId = 0 OR ClientId = @ClientId)
GROUP BY COALESCE(NULLIF(TRIM(Grade), ''), 'Not mapped')
ORDER BY Value DESC, Label
LIMIT 8;", parameters)).ToList()
            : [];

        var genderHeadcount = sections.Workforce
            ? (await connection.QueryAsync<DashboardChartPoint>(@"
SELECT COALESCE(NULLIF(TRIM(Gender), ''), 'Not mapped') AS Label, COUNT(*) AS Value
FROM employees
WHERE IsActive = TRUE
  AND (@ClientId = 0 OR ClientId = @ClientId)
GROUP BY COALESCE(NULLIF(TRIM(Gender), ''), 'Not mapped')
ORDER BY Value DESC, Label;", parameters)).ToList()
            : [];

        var essAdoption = sections.Workforce
            ? new List<DashboardChartPoint>
            {
                new() { Label = "ESS enabled", Value = portalUsers },
                new() { Label = "Not enabled", Value = Math.Max(activeEmployees - portalUsers, 0) }
            }
            : [];

        var payrollPaymentStatus = sections.Payroll
            ? (await connection.QueryAsync<DashboardChartPoint>(@"
SELECT COALESCE(NULLIF(TRIM(e.PaymentStatus), ''), 'Pending') AS Label, COUNT(*) AS Value
FROM payrunemployees e
JOIN payruns r ON r.Id = e.PayRunId
WHERE r.PayPeriod = @Month
  AND e.IsSkipped = FALSE
  AND (@ClientId = 0 OR r.ClientId = @ClientId)
GROUP BY COALESCE(NULLIF(TRIM(e.PaymentStatus), ''), 'Pending')
ORDER BY Value DESC, Label;", parameters)).ToList()
            : [];

        var payrollRunType = sections.Payroll
            ? (await connection.QueryAsync<DashboardChartPoint>(@"
SELECT COALESCE(NULLIF(TRIM(RunType), ''), 'Regular') AS Label, COUNT(*) AS Value
FROM payruns
WHERE PayPeriod = @Month
  AND (@ClientId = 0 OR ClientId = @ClientId)
GROUP BY COALESCE(NULLIF(TRIM(RunType), ''), 'Regular')
ORDER BY Value DESC, Label;", parameters)).ToList()
            : [];

        var payrollCostBreakup = sections.Payroll
            ? (await connection.QueryFirstOrDefaultAsync<DashboardPayrollCostBreakup>(@"
SELECT
    COALESCE(SUM(e.GrossPay + e.OneTimeEarnings), 0) AS GrossEarnings,
    COALESCE(SUM(e.StatutoryDeductions), 0) AS StatutoryDeductions,
    COALESCE(SUM(e.OneTimeDeductions), 0) AS OtherDeductions,
    COALESCE(SUM(e.NetPay), 0) AS NetPay
FROM payrunemployees e
JOIN payruns r ON r.Id = e.PayRunId
WHERE r.PayPeriod = @Month
  AND e.IsSkipped = FALSE
  AND (@ClientId = 0 OR r.ClientId = @ClientId);", parameters) ?? new())
            : new();

        var attendanceDailyStatus = sections.Attendance
            ? (await connection.QueryAsync<DashboardChartPoint>(@"
SELECT COALESCE(NULLIF(TRIM(status), ''), 'Unknown') AS Label, COUNT(*) AS Value
FROM employee_daily_attendance
WHERE attendance_date >= STR_TO_DATE(CONCAT(@Month, '-01'), '%Y-%m-%d')
  AND attendance_date < DATE_ADD(STR_TO_DATE(CONCAT(@Month, '-01'), '%Y-%m-%d'), INTERVAL 1 MONTH)
  AND (@ClientId = 0 OR client_id = @ClientId)
GROUP BY COALESCE(NULLIF(TRIM(status), ''), 'Unknown')
ORDER BY Value DESC, Label;", parameters)).ToList()
            : [];

        var attendanceSourceType = sections.Attendance
            ? (await connection.QueryAsync<DashboardChartPoint>(@"
SELECT COALESCE(NULLIF(TRIM(source_type), ''), 'Monthly') AS Label, COUNT(*) AS Value
FROM employee_monthly_attendance
WHERE attendance_month = @Month
  AND (@ClientId = 0 OR client_id = @ClientId)
GROUP BY COALESCE(NULLIF(TRIM(source_type), ''), 'Monthly')
ORDER BY Value DESC, Label;", parameters)).ToList()
            : [];

        var approvalResourceBreakup = sections.Approvals
            ? (await connection.QueryAsync<DashboardChartPoint>(@"
SELECT COALESCE(NULLIF(TRIM(i.ResourceType), ''), 'Workflow') AS Label, COUNT(*) AS Value
FROM workflowtasks t
JOIN workflowinstances i ON i.Id = t.InstanceId
WHERE t.ApproverUserId = @UserId
  AND t.Status = 'Pending'
GROUP BY COALESCE(NULLIF(TRIM(i.ResourceType), ''), 'Workflow')
ORDER BY Value DESC, Label
LIMIT 8;", parameters)).ToList()
            : [];

        var approvalAging = sections.Approvals
            ? (await connection.QueryAsync<DashboardChartPoint>(@"
SELECT Bucket AS Label, COUNT(*) AS Value
FROM (
    SELECT CASE
        WHEN TIMESTAMPDIFF(HOUR, CreatedAt, UTC_TIMESTAMP()) < 24 THEN '< 1 day'
        WHEN TIMESTAMPDIFF(HOUR, CreatedAt, UTC_TIMESTAMP()) < 72 THEN '1-3 days'
        WHEN TIMESTAMPDIFF(HOUR, CreatedAt, UTC_TIMESTAMP()) < 168 THEN '3-7 days'
        ELSE '> 7 days'
    END AS Bucket
    FROM workflowtasks
    WHERE ApproverUserId = @UserId
      AND Status = 'Pending'
) x
GROUP BY Bucket
ORDER BY FIELD(Bucket, '< 1 day', '1-3 days', '3-7 days', '> 7 days');", parameters)).ToList()
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
            RecentPayRuns = recentPayRuns,
            DepartmentHeadcount = departmentHeadcount,
            LocationHeadcount = locationHeadcount,
            PayrollTrend = payrollTrend,
            AttendanceMix = attendanceMix,
            AttendancePayability = attendancePayability,
            ApprovalStageBreakup = approvalStageBreakup,
            ApprovalActionMix = approvalActionMix,
            DesignationHeadcount = designationHeadcount,
            GradeHeadcount = gradeHeadcount,
            GenderHeadcount = genderHeadcount,
            EssAdoption = essAdoption,
            PayrollPaymentStatus = payrollPaymentStatus,
            PayrollRunType = payrollRunType,
            PayrollCostBreakup = payrollCostBreakup,
            AttendanceDailyStatus = attendanceDailyStatus,
            AttendanceSourceType = attendanceSourceType,
            ApprovalResourceBreakup = approvalResourceBreakup,
            ApprovalAging = approvalAging
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
    public List<DashboardChartPoint> DepartmentHeadcount { get; set; } = [];
    public List<DashboardChartPoint> LocationHeadcount { get; set; } = [];
    public List<DashboardPayrollTrendPoint> PayrollTrend { get; set; } = [];
    public List<DashboardChartPoint> AttendanceMix { get; set; } = [];
    public List<DashboardChartPoint> AttendancePayability { get; set; } = [];
    public List<DashboardChartPoint> ApprovalStageBreakup { get; set; } = [];
    public List<DashboardChartPoint> ApprovalActionMix { get; set; } = [];
    public List<DashboardChartPoint> DesignationHeadcount { get; set; } = [];
    public List<DashboardChartPoint> GradeHeadcount { get; set; } = [];
    public List<DashboardChartPoint> GenderHeadcount { get; set; } = [];
    public List<DashboardChartPoint> EssAdoption { get; set; } = [];
    public List<DashboardChartPoint> PayrollPaymentStatus { get; set; } = [];
    public List<DashboardChartPoint> PayrollRunType { get; set; } = [];
    public DashboardPayrollCostBreakup PayrollCostBreakup { get; set; } = new();
    public List<DashboardChartPoint> AttendanceDailyStatus { get; set; } = [];
    public List<DashboardChartPoint> AttendanceSourceType { get; set; } = [];
    public List<DashboardChartPoint> ApprovalResourceBreakup { get; set; } = [];
    public List<DashboardChartPoint> ApprovalAging { get; set; } = [];
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

public class DashboardChartPoint
{
    public string Label { get; set; } = string.Empty;
    public decimal Value { get; set; }
}

public class DashboardPayrollTrendPoint
{
    public string Month { get; set; } = string.Empty;
    public int RunCount { get; set; }
    public decimal PayrollCost { get; set; }
    public decimal NetPay { get; set; }
}

public class DashboardPayrollCostBreakup
{
    public decimal GrossEarnings { get; set; }
    public decimal StatutoryDeductions { get; set; }
    public decimal OtherDeductions { get; set; }
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
