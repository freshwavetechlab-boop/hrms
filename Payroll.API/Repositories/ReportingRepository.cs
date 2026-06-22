using Dapper;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public class ReportingRepository(IConfiguration configuration)
{
    private MySqlConnection Connection() => new(configuration.GetConnectionString("Default"));

    public async Task<ReportResult> RunAsync(string code, ReportFilter filter)
    {
        await using var db = Connection(); await db.OpenAsync(); await db.ExecuteAsync("USE payroll;");
        filter.Month = string.IsNullOrWhiteSpace(filter.Month) ? DateTime.Today.ToString("yyyy-MM") : filter.Month;
        filter.FromDate = string.IsNullOrWhiteSpace(filter.FromDate) ? $"{filter.Month}-01" : filter.FromDate;
        filter.ToDate = string.IsNullOrWhiteSpace(filter.ToDate) ? DateTime.Parse($"{filter.Month}-01").AddMonths(1).AddDays(-1).ToString("yyyy-MM-dd") : filter.ToDate;
        var sql = code switch
        {
            "salary-register" => @"SELECT r.PayPeriod AS `Pay Period`, p.EmployeeCode AS `Employee Code`, p.EmployeeName AS Employee, p.Department, p.PresentDays AS `Present Days`, p.PayableDays AS `Payable Days`, p.GrossPay AS `Gross Pay`, p.StatutoryDeductions AS `Statutory Deductions`, p.OneTimeDeductions AS `Other Deductions`, p.NetPay AS `Net Pay`, p.PaymentStatus AS `Payment Status` FROM PayRunEmployees p JOIN PayRuns r ON r.Id=p.PayRunId WHERE p.ClientId=@ClientId AND r.PayPeriod=@Month AND p.IsSkipped=FALSE ORDER BY r.PayPeriod DESC,p.EmployeeCode",
            "net-pay-estimate" => @"SELECT e.EmployeeCode AS `Employee Code`, CONCAT(e.FirstName,' ',e.LastName) AS Employee, ROUND(COALESCE(CAST(JSON_UNQUOTE(JSON_EXTRACT(e.SalaryJson,'$.401')) AS DECIMAL(18,2)),0)+COALESCE(CAST(JSON_UNQUOTE(JSON_EXTRACT(e.SalaryJson,'$.402')) AS DECIMAL(18,2)),0)+COALESCE(CAST(JSON_UNQUOTE(JSON_EXTRACT(e.SalaryJson,'$.403')) AS DECIMAL(18,2)),0)+COALESCE(CAST(JSON_UNQUOTE(JSON_EXTRACT(e.SalaryJson,'$.404')) AS DECIMAL(18,2)),0)+COALESCE(CAST(JSON_UNQUOTE(JSON_EXTRACT(e.SalaryJson,'$.405')) AS DECIMAL(18,2)),0)+COALESCE(CAST(JSON_UNQUOTE(JSON_EXTRACT(e.SalaryJson,'$.406')) AS DECIMAL(18,2)),0)+COALESCE(CAST(JSON_UNQUOTE(JSON_EXTRACT(e.SalaryJson,'$.407')) AS DECIMAL(18,2)),0),2) AS `Gross Estimate`, ROUND(COALESCE(CAST(JSON_UNQUOTE(JSON_EXTRACT(e.SalaryJson,'$.408')) AS DECIMAL(18,2)),0)+COALESCE(CAST(JSON_UNQUOTE(JSON_EXTRACT(e.SalaryJson,'$.409')) AS DECIMAL(18,2)),0)+COALESCE(CAST(JSON_UNQUOTE(JSON_EXTRACT(e.SalaryJson,'$.410')) AS DECIMAL(18,2)),0)+COALESCE(CAST(JSON_UNQUOTE(JSON_EXTRACT(e.SalaryJson,'$.411')) AS DECIMAL(18,2)),0)+COALESCE(CAST(JSON_UNQUOTE(JSON_EXTRACT(e.SalaryJson,'$.412')) AS DECIMAL(18,2)),0),2) AS Deductions, ROUND(COALESCE(CAST(JSON_UNQUOTE(JSON_EXTRACT(e.SalaryJson,'$.401')) AS DECIMAL(18,2)),0)+COALESCE(CAST(JSON_UNQUOTE(JSON_EXTRACT(e.SalaryJson,'$.402')) AS DECIMAL(18,2)),0)+COALESCE(CAST(JSON_UNQUOTE(JSON_EXTRACT(e.SalaryJson,'$.403')) AS DECIMAL(18,2)),0)+COALESCE(CAST(JSON_UNQUOTE(JSON_EXTRACT(e.SalaryJson,'$.404')) AS DECIMAL(18,2)),0)+COALESCE(CAST(JSON_UNQUOTE(JSON_EXTRACT(e.SalaryJson,'$.405')) AS DECIMAL(18,2)),0)+COALESCE(CAST(JSON_UNQUOTE(JSON_EXTRACT(e.SalaryJson,'$.406')) AS DECIMAL(18,2)),0)+COALESCE(CAST(JSON_UNQUOTE(JSON_EXTRACT(e.SalaryJson,'$.407')) AS DECIMAL(18,2)),0)-COALESCE(CAST(JSON_UNQUOTE(JSON_EXTRACT(e.SalaryJson,'$.408')) AS DECIMAL(18,2)),0)-COALESCE(CAST(JSON_UNQUOTE(JSON_EXTRACT(e.SalaryJson,'$.409')) AS DECIMAL(18,2)),0)-COALESCE(CAST(JSON_UNQUOTE(JSON_EXTRACT(e.SalaryJson,'$.410')) AS DECIMAL(18,2)),0)-COALESCE(CAST(JSON_UNQUOTE(JSON_EXTRACT(e.SalaryJson,'$.411')) AS DECIMAL(18,2)),0)-COALESCE(CAST(JSON_UNQUOTE(JSON_EXTRACT(e.SalaryJson,'$.412')) AS DECIMAL(18,2)),0),2) AS `Net Pay Estimate` FROM Employees e WHERE e.ClientId=@ClientId AND e.IsActive=TRUE ORDER BY e.EmployeeCode",
            "pf-register" => @"SELECT e.EmployeeCode AS `Employee Code`, CONCAT(e.FirstName,' ',e.LastName) AS Employee, JSON_UNQUOTE(JSON_EXTRACT(e.SalaryJson,'$.401')) AS Basic, JSON_UNQUOTE(JSON_EXTRACT(e.SalaryJson,'$.408')) AS `Employee PF` FROM Employees e WHERE e.ClientId=@ClientId AND e.IsActive=TRUE ORDER BY e.EmployeeCode",
            "esi-register" => @"SELECT e.EmployeeCode AS `Employee Code`, CONCAT(e.FirstName,' ',e.LastName) AS Employee, JSON_UNQUOTE(JSON_EXTRACT(e.SalaryJson,'$.409')) AS `Employee ESIC` FROM Employees e WHERE e.ClientId=@ClientId AND e.IsActive=TRUE ORDER BY e.EmployeeCode",
            "tds-register" => @"SELECT e.EmployeeCode AS `Employee Code`, CONCAT(e.FirstName,' ',e.LastName) AS Employee, JSON_UNQUOTE(JSON_EXTRACT(e.SalaryJson,'$.411')) AS TDS FROM Employees e WHERE e.ClientId=@ClientId AND e.IsActive=TRUE ORDER BY e.EmployeeCode",
            "employee-master" => @"SELECT e.EmployeeCode AS `Employee Code`, CONCAT(e.FirstName,' ',e.LastName) AS Employee, e.Department, e.Designation, w.Name AS Location, e.DateOfJoining AS `Joining Date`, e.IsActive AS Active FROM Employees e LEFT JOIN WorkLocations w ON w.Id=e.WorkLocationId WHERE e.ClientId=@ClientId AND (@Department IS NULL OR e.Department=@Department) AND (@WorkLocationId IS NULL OR e.WorkLocationId=@WorkLocationId) ORDER BY e.FirstName,e.LastName",
            "new-joiners" => @"SELECT e.EmployeeCode AS `Employee Code`, CONCAT(e.FirstName,' ',e.LastName) AS Employee, e.DateOfJoining AS `Joining Date`, e.Designation, w.Name AS Location FROM Employees e LEFT JOIN WorkLocations w ON w.Id=e.WorkLocationId WHERE e.ClientId=@ClientId AND e.DateOfJoining >= DATE_SUB(CURDATE(), INTERVAL 90 DAY) ORDER BY e.DateOfJoining DESC",
            "tenure" => @"SELECT e.EmployeeCode AS `Employee Code`, CONCAT(e.FirstName,' ',e.LastName) AS Employee, e.DateOfJoining AS `Joining Date`, ROUND(DATEDIFF(CURDATE(), STR_TO_DATE(e.DateOfJoining,'%Y-%m-%d')) / 365.25, 1) AS `Tenure Years`, e.Designation FROM Employees e WHERE e.ClientId=@ClientId AND e.IsActive=TRUE ORDER BY `Tenure Years` DESC",
            "headcount" => @"SELECT e.Department, COUNT(*) AS Headcount, SUM(e.AnnualCtc) AS `Annual CTC` FROM Employees e WHERE e.ClientId=@ClientId AND e.IsActive=TRUE AND (@Department IS NULL OR e.Department=@Department) GROUP BY e.Department ORDER BY Headcount DESC",
            "location-cost" => @"SELECT w.Name AS Location, COUNT(e.Id) AS Headcount, SUM(e.AnnualCtc) AS `Annual CTC` FROM Employees e LEFT JOIN WorkLocations w ON w.Id=e.WorkLocationId WHERE e.ClientId=@ClientId AND e.IsActive=TRUE GROUP BY w.Name ORDER BY `Annual CTC` DESC",
            "daily-attendance" => @"SELECT e.EmployeeCode AS `Employee Code`, CONCAT(e.FirstName,' ',e.LastName) AS Employee, e.Department, DATE_FORMAT(a.attendance_date,'%Y-%m-%d') AS Date, DAYNAME(a.attendance_date) AS Day, a.status AS Status, a.payable_value AS `Payable Value`, COALESCE(a.remarks,'') AS Remarks
FROM employee_daily_attendance a
JOIN Employees e ON e.Id=a.employee_id
WHERE a.client_id=@ClientId AND a.attendance_date BETWEEN @FromDate AND @ToDate
AND (@Department IS NULL OR e.Department=@Department) AND (@WorkLocationId IS NULL OR e.WorkLocationId=@WorkLocationId)
ORDER BY a.attendance_date,e.EmployeeCode",
            "monthly-attendance" => @"SELECT e.EmployeeCode AS `Employee Code`, CONCAT(e.FirstName,' ',e.LastName) AS Employee, e.Department, a.attendance_month AS Month, a.working_days AS `Working Days`, a.present_days AS `Present Days`, a.payable_days AS `Payable Days`, a.lop_days AS `LOP Days`, a.source_type AS Source, COALESCE(a.remarks,'') AS Remarks
FROM employee_monthly_attendance a
JOIN Employees e ON e.Id=a.employee_id
WHERE a.client_id=@ClientId AND a.attendance_month=@Month
AND (@Department IS NULL OR e.Department=@Department) AND (@WorkLocationId IS NULL OR e.WorkLocationId=@WorkLocationId)
ORDER BY e.EmployeeCode",
            "attendance-exception" => @"SELECT e.EmployeeCode AS `Employee Code`, CONCAT(e.FirstName,' ',e.LastName) AS Employee, e.Department, @Month AS Month,
CASE
    WHEN a.employee_id IS NULL THEN 'Missing monthly attendance'
    WHEN a.working_days <= 0 AND a.present_days <= 0 AND a.payable_days <= 0 THEN 'Missing attendance values'
    WHEN a.payable_days > a.working_days OR a.present_days > a.working_days THEN 'Values exceed working days'
    WHEN ABS((a.present_days + a.lop_days) - a.working_days) > 0.01 THEN 'Present + LOP does not match working days'
    ELSE 'Ready'
END AS Exception,
COALESCE(a.working_days,0) AS `Working Days`, COALESCE(a.present_days,0) AS `Present Days`, COALESCE(a.payable_days,0) AS `Payable Days`, COALESCE(a.lop_days,0) AS `LOP Days`
FROM Employees e
LEFT JOIN employee_monthly_attendance a ON a.employee_id=e.Id AND a.client_id=e.ClientId AND a.attendance_month=@Month
WHERE e.ClientId=@ClientId AND e.IsActive=TRUE
AND (@Department IS NULL OR e.Department=@Department) AND (@WorkLocationId IS NULL OR e.WorkLocationId=@WorkLocationId)
HAVING Exception <> 'Ready'
ORDER BY e.EmployeeCode",
            "attendance-trend" => @"SELECT DATE_FORMAT(a.attendance_date,'%Y-%m-%d') AS Date, DAYNAME(a.attendance_date) AS Day, a.status AS Status, COUNT(*) AS Employees, SUM(a.payable_value) AS `Payable Value`
FROM employee_daily_attendance a
JOIN Employees e ON e.Id=a.employee_id
WHERE a.client_id=@ClientId AND a.attendance_date BETWEEN @FromDate AND @ToDate
AND (@Department IS NULL OR e.Department=@Department) AND (@WorkLocationId IS NULL OR e.WorkLocationId=@WorkLocationId)
GROUP BY a.attendance_date,a.status
ORDER BY a.attendance_date, a.status",
            "leave-balance" => @"SELECT e.EmployeeCode AS `Employee Code`, CONCAT(e.FirstName,' ',e.LastName) AS Employee, lt.Name AS `Leave Type`, b.BalanceDate AS Date, b.BalanceCount AS Balance FROM employee_leave_balances b JOIN Employees e ON e.Id=b.employee_id JOIN leave_types lt ON lt.Id=b.leave_type_id WHERE b.client_id=@ClientId ORDER BY e.EmployeeCode,lt.Name,b.BalanceDate",
            "lwp-balance" => @"SELECT e.EmployeeCode AS `Employee Code`, CONCAT(e.FirstName,' ',e.LastName) AS Employee, b.BalanceDate AS Date, b.BalanceCount AS `LWP Balance` FROM employee_leave_balances b JOIN Employees e ON e.Id=b.employee_id JOIN leave_types lt ON lt.Id=b.leave_type_id WHERE b.client_id=@ClientId AND lt.Code='LWP' ORDER BY e.EmployeeCode",
            "leave-accrual" => @"SELECT lt.Code AS `Leave Code`, lt.Name AS `Leave Type`, lt.Type, p.entitlement AS Entitlement, p.entitlement_period AS `Entitlement Period`, p.pro_rate_for_new_joinees AS `Pro-rate New Joiners`, p.reset_enabled AS `Reset Enabled`, p.reset_frequency AS `Reset Frequency`, p.carry_forward_unused_leaves AS `Carry Forward`, p.max_carry_forward_limit AS `Carry Forward Limit`, p.encash_unused_leaves AS Encashment, p.effective_from AS `Effective From`, p.expires_on AS `Expires On`, a.applicability_mode AS Applicability, a.department AS Department, a.designation AS Designation, a.work_location AS `Work Location`
FROM leave_types lt
JOIN leave_type_policies p ON p.leave_type_id=lt.id
JOIN leave_type_applicability a ON a.leave_type_id=lt.id
WHERE lt.client_id=@ClientId
ORDER BY lt.Name",
            "leave-utilization" => @"SELECT e.EmployeeCode AS `Employee Code`, CONCAT(e.FirstName,' ',e.LastName) AS Employee, e.Department, DATE_FORMAT(a.attendance_date,'%Y-%m') AS Month, a.status AS `Leave/Absence Type`, COUNT(*) AS Days, SUM(a.payable_value) AS `Payable Value`
FROM employee_daily_attendance a
JOIN Employees e ON e.Id=a.employee_id
WHERE a.client_id=@ClientId AND a.attendance_date BETWEEN @FromDate AND @ToDate AND a.status IN ('Paid Leave','Absent','Half Day')
AND (@Department IS NULL OR e.Department=@Department) AND (@WorkLocationId IS NULL OR e.WorkLocationId=@WorkLocationId)
GROUP BY e.EmployeeCode, Employee, e.Department, DATE_FORMAT(a.attendance_date,'%Y-%m'), a.status
ORDER BY e.EmployeeCode, Month, `Leave/Absence Type`",
            "leave-approval-status" => @"SELECT e.EmployeeCode AS `Employee Code`, CONCAT(e.FirstName,' ',e.LastName) AS Employee, lt.Name AS `Leave Type`, r.FromDate AS `From Date`, r.ToDate AS `To Date`, r.Days, r.Status, r.Reason, r.CreatedAt AS `Requested On`
FROM EssLeaveRequests r
JOIN Employees e ON e.Id=r.EmployeeId
JOIN leave_types lt ON lt.Id=r.LeaveTypeId
WHERE r.ClientId=@ClientId AND r.FromDate <= @ToDate AND r.ToDate >= @FromDate
AND (@Department IS NULL OR e.Department=@Department) AND (@WorkLocationId IS NULL OR e.WorkLocationId=@WorkLocationId)
ORDER BY r.CreatedAt DESC",
            "payroll-summary" => @"SELECT p.PayPeriod AS `Pay Period`, p.Status, COUNT(e.Id) AS Employees, p.PayrollCost AS `Payroll Cost`, p.NetPay AS `Net Pay` FROM PayRuns p LEFT JOIN PayRunEmployees e ON e.PayRunId=p.Id AND e.IsSkipped=FALSE WHERE p.ClientId=@ClientId GROUP BY p.Id,p.PayPeriod,p.Status,p.PayrollCost,p.NetPay ORDER BY p.PayPeriod DESC",
            _ => "SELECT e.Department, COUNT(*) AS Headcount FROM Employees e WHERE e.ClientId=@ClientId GROUP BY e.Department"
        };
        var rows = (await db.QueryAsync(sql, filter)).Select(row => ((IDictionary<string, object>)row).ToDictionary(x => x.Key, x => (object?)x.Value)).ToList();
        return new ReportResult { Title = code, Columns = rows.FirstOrDefault()?.Keys.ToList() ?? [], Rows = rows };
    }
}
