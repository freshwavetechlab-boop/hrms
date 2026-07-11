using Dapper;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public class ReportingRepository(IConfiguration configuration)
{
    private MySqlConnection Connection() => new(configuration.GetConnectionString("Default"));
    public async Task<ReportResult> RunAsync(string code, ReportFilter filter)
    {
        await using var db = Connection(); await db.OpenAsync();
        filter.Month = string.IsNullOrWhiteSpace(filter.Month) ? DateTime.Today.ToString("yyyy-MM") : filter.Month;
        filter.FromDate = string.IsNullOrWhiteSpace(filter.FromDate) ? $"{filter.Month}-01" : filter.FromDate;
        filter.ToDate = string.IsNullOrWhiteSpace(filter.ToDate) ? DateTime.Parse($"{filter.Month}-01").AddMonths(1).AddDays(-1).ToString("yyyy-MM-dd") : filter.ToDate;
        if (code == "client-billing-report" || code == "payroll-cost-report")
            return await RunClientBillingReportAsync(db, filter);
        var monthlyAdviceSql = @"SELECT p.EmployeeCode AS `Emp Code`,
p.EmployeeName AS `Name`,
p.Department,
COALESCE(pay.BankAccountNo,'') AS `Bank Account Number`,
COALESCE(pd.PanNumber,'') AS `PAN`,
COALESCE(pay.IfscCode,'') AS `IFSC Code`,
p.NetPay AS `Net Salary`
FROM payrunemployees p
JOIN payruns r ON r.Id=p.PayRunId
LEFT JOIN employeepersonaldetails pd ON pd.EmployeeId=p.EmployeeId
LEFT JOIN employeepaymentdetails pay ON pay.EmployeeId=p.EmployeeId
WHERE p.ClientId=@ClientId AND (@PayRunId IS NULL OR r.Id=@PayRunId) AND (@PayRunId IS NOT NULL OR r.PayPeriod=@Month) AND (@EmployeeId IS NULL OR p.EmployeeId=@EmployeeId) AND p.IsSkipped=FALSE
AND LOWER(COALESCE(pay.PaymentMode,''))='bank transfer'
ORDER BY p.EmployeeCode";
        var pfRegisterSql = @"SELECT x.`Pay Period`,
x.`Employee Code`,
x.Employee,
x.Department,
x.Basic,
x.`Employee PF`,
x.VPF,
x.EPS,
CASE WHEN x.`Employer PF Actual` <> 0 OR x.EPS <> 0 THEN x.`Employer PF Actual` ELSE x.`Employee PF` END AS `Employer PF`
FROM (
SELECT r.PayPeriod AS `Pay Period`,
p.EmployeeCode AS `Employee Code`,
p.EmployeeName AS Employee,
p.Department,
SUM(CASE WHEN UPPER(TRIM(l.ComponentCode))='BASIC' THEN l.Amount ELSE 0 END) AS Basic,
SUM(CASE WHEN COALESCE(l.StatutoryType,'')='PF Employee' OR UPPER(TRIM(l.ComponentCode)) IN ('PF','EPF') THEN l.Amount ELSE 0 END) AS `Employee PF`,
SUM(CASE WHEN COALESCE(l.StatutoryType,'')='VPF' OR UPPER(TRIM(l.ComponentCode))='VPF' THEN l.Amount ELSE 0 END) AS VPF,
SUM(CASE WHEN COALESCE(l.StatutoryType,'')='EPS' OR UPPER(TRIM(l.ComponentCode))='EPS' THEN l.Amount ELSE 0 END) AS EPS,
SUM(CASE WHEN COALESCE(l.StatutoryType,'')='PF Employer' OR UPPER(TRIM(l.ComponentCode)) IN ('EPF_ER','EPF_ER_BAL','PF_ER','PF_EMPLOYER') THEN l.Amount ELSE 0 END) AS `Employer PF Actual`
FROM payrunemployees p
JOIN payruns r ON r.Id=p.PayRunId
LEFT JOIN payrunemployeelines l ON l.PayRunEmployeeId=p.Id
WHERE p.ClientId=@ClientId AND (@PayRunId IS NULL OR r.Id=@PayRunId) AND (@PayRunId IS NOT NULL OR r.PayPeriod=@Month) AND (@EmployeeId IS NULL OR p.EmployeeId=@EmployeeId) AND p.IsSkipped=FALSE
GROUP BY r.PayPeriod,p.EmployeeCode,p.EmployeeName,p.Department
) x
WHERE x.`Employee PF` <> 0 OR x.VPF <> 0 OR x.EPS <> 0 OR x.`Employer PF Actual` <> 0
ORDER BY x.`Employee Code`";
        var pfEcrReportSql = @"SELECT
COALESCE(pd.UanNumber,'') AS UAN,
p.EmployeeName AS `Member Name`,
CAST(ROUND(x.GrossWages,0) AS SIGNED) AS `Gross Wages`,
CAST(ROUND(x.EpfWages,0) AS SIGNED) AS `EPF Wages`,
CAST(ROUND(x.EpsWages,0) AS SIGNED) AS `EPS Wages`,
CAST(ROUND(x.EdliWages,0) AS SIGNED) AS `EDLI Wages`,
CAST(ROUND(x.EmployeePf,0) AS SIGNED) AS `EPF Contribution Remitted`,
CAST(ROUND(x.EpsContribution,0) AS SIGNED) AS `EPS Contribution Remitted`,
CAST(ROUND(CASE WHEN x.EmployerPfActual <> 0 OR x.EpsContribution <> 0 THEN x.EmployerPfActual ELSE x.EmployeePf END,0) AS SIGNED) AS `EPF EPS Difference Remitted`,
CAST(ROUND(GREATEST(0, r.TotalWorkingDays - p.PayableDays),0) AS SIGNED) AS `NCP Days`,
0 AS `Refund Of Advances`
FROM payrunemployees p
JOIN payruns r ON r.Id=p.PayRunId
LEFT JOIN employeepersonaldetails pd ON pd.EmployeeId=p.EmployeeId
JOIN (
    SELECT l.PayRunEmployeeId,
    SUM(CASE WHEN COALESCE(l.Category,'') IN ('Earning','Reimbursement') THEN l.Amount ELSE 0 END) AS GrossWages,
    LEAST(15000, SUM(CASE WHEN UPPER(TRIM(l.ComponentCode))='BASIC' THEN l.Amount ELSE 0 END)) AS EpfWages,
    CASE WHEN SUM(CASE WHEN COALESCE(l.StatutoryType,'')='EPS' OR UPPER(TRIM(l.ComponentCode))='EPS' THEN l.Amount ELSE 0 END) > 0
        THEN LEAST(15000, ROUND(SUM(CASE WHEN COALESCE(l.StatutoryType,'')='EPS' OR UPPER(TRIM(l.ComponentCode))='EPS' THEN l.Amount ELSE 0 END) / 0.0833, 0))
        ELSE 0 END AS EpsWages,
    LEAST(15000, SUM(CASE WHEN UPPER(TRIM(l.ComponentCode))='BASIC' THEN l.Amount ELSE 0 END)) AS EdliWages,
    SUM(CASE WHEN COALESCE(l.StatutoryType,'')='PF Employee' OR UPPER(TRIM(l.ComponentCode)) IN ('PF','EPF') THEN l.Amount ELSE 0 END) AS EmployeePf,
    SUM(CASE WHEN COALESCE(l.StatutoryType,'')='EPS' OR UPPER(TRIM(l.ComponentCode))='EPS' THEN l.Amount ELSE 0 END) AS EpsContribution,
    SUM(CASE WHEN COALESCE(l.StatutoryType,'')='PF Employer' OR UPPER(TRIM(l.ComponentCode)) IN ('EPF_ER','EPF_ER_BAL','PF_ER','PF_EMPLOYER') THEN l.Amount ELSE 0 END) AS EmployerPfActual
    FROM payrunemployeelines l
    GROUP BY l.PayRunEmployeeId
) x ON x.PayRunEmployeeId=p.Id
WHERE p.ClientId=@ClientId
AND (@PayRunId IS NULL OR r.Id=@PayRunId)
AND (@PayRunId IS NOT NULL OR r.PayPeriod=@Month)
AND (@EmployeeId IS NULL OR p.EmployeeId=@EmployeeId)
AND p.IsSkipped=FALSE
AND (x.EmployeePf <> 0 OR x.EpsContribution <> 0 OR x.EmployerPfActual <> 0)
ORDER BY p.EmployeeCode";
        var esiRegisterSql = @"SELECT r.PayPeriod AS `Pay Period`,
p.EmployeeCode AS `Employee Code`,
p.EmployeeName AS Employee,
p.Department,
SUM(CASE WHEN COALESCE(l.StatutoryType,'')='ESI Employee' OR UPPER(TRIM(l.ComponentCode)) IN ('ESI','ESIC','ESI_EE','ESIC_EE') THEN l.Amount ELSE 0 END) AS `Employee ESI`,
SUM(CASE WHEN COALESCE(l.StatutoryType,'')='ESI Employer' OR UPPER(TRIM(l.ComponentCode)) IN ('ESI_ER','ESIC_ER') THEN l.Amount ELSE 0 END) AS `Employer ESI`
FROM payrunemployees p
JOIN payruns r ON r.Id=p.PayRunId
LEFT JOIN payrunemployeelines l ON l.PayRunEmployeeId=p.Id
WHERE p.ClientId=@ClientId AND (@PayRunId IS NULL OR r.Id=@PayRunId) AND (@PayRunId IS NOT NULL OR r.PayPeriod=@Month) AND (@EmployeeId IS NULL OR p.EmployeeId=@EmployeeId) AND p.IsSkipped=FALSE
GROUP BY r.PayPeriod,p.EmployeeCode,p.EmployeeName,p.Department
HAVING `Employee ESI` <> 0 OR `Employer ESI` <> 0
ORDER BY p.EmployeeCode";
        var ptRegisterSql = @"SELECT r.PayPeriod AS `Pay Period`,
p.EmployeeCode AS `Employee Code`,
p.EmployeeName AS Employee,
p.Department,
COALESCE(ep.State,w.State,'') AS State,
COALESCE(o.ProfessionalTaxNumber,'') AS `PT Registration No`,
SUM(l.Amount) AS `Professional Tax`
FROM payrunemployeelines l
JOIN payrunemployees p ON p.Id=l.PayRunEmployeeId
JOIN payruns r ON r.Id=l.PayRunId
LEFT JOIN employees e ON e.Id=p.EmployeeId
LEFT JOIN employeepersonaldetails ep ON ep.EmployeeId=p.EmployeeId
LEFT JOIN worklocations w ON w.Id=e.WorkLocationId
LEFT JOIN organizations o ON 1=1
WHERE r.ClientId=@ClientId AND (@PayRunId IS NULL OR r.Id=@PayRunId) AND (@PayRunId IS NOT NULL OR r.PayPeriod=@Month) AND (@EmployeeId IS NULL OR p.EmployeeId=@EmployeeId) AND (COALESCE(l.StatutoryType,'')='Professional Tax' OR UPPER(TRIM(l.ComponentCode)) IN ('PT','PT_LWF_WC')) AND l.Amount > 0
GROUP BY r.PayPeriod,p.EmployeeCode,p.EmployeeName,p.Department,ep.State,w.State,o.ProfessionalTaxNumber
ORDER BY p.EmployeeCode";
        var statutorySummarySql = @"SELECT x.`Pay Period`,
x.`Pay Run Id`,
x.`Employee Code`,
x.Employee,
x.Department,
x.`Statutory Group`,
x.`Statutory Type`,
x.`Component Code`,
x.Component,
SUM(x.Amount) AS Amount
FROM (
SELECT r.PayPeriod AS `Pay Period`,
r.Id AS `Pay Run Id`,
p.EmployeeCode AS `Employee Code`,
p.EmployeeName AS Employee,
p.Department,
CASE
    WHEN COALESCE(l.StatutoryType,'') IN ('PF Employee','PF Employer','VPF','EPS') OR UPPER(TRIM(l.ComponentCode)) IN ('PF','EPF','VPF','EPS','EPF_ER','EPF_ER_BAL','PF_ER','PF_EMPLOYER') THEN 'Provident Fund'
    WHEN COALESCE(l.StatutoryType,'') IN ('ESI Employee','ESI Employer') OR UPPER(TRIM(l.ComponentCode)) IN ('ESI','ESIC','ESI_EE','ESIC_EE','ESI_ER','ESIC_ER') THEN 'ESI'
    WHEN COALESCE(l.StatutoryType,'')='Professional Tax' OR UPPER(TRIM(l.ComponentCode)) IN ('PT','PT_LWF_WC') THEN 'Professional Tax'
    WHEN COALESCE(l.StatutoryType,'') IN ('LWF Employee','LWF Employer') OR UPPER(TRIM(l.ComponentCode)) LIKE 'LWF%' THEN 'Labour Welfare Fund'
    WHEN COALESCE(l.StatutoryType,'')='TDS' OR UPPER(TRIM(l.ComponentCode))='TDS' THEN 'Income Tax'
    WHEN COALESCE(l.StatutoryType,'') IN ('NPS Employee','NPS Employer') OR UPPER(TRIM(l.ComponentCode)) LIKE 'NPS%' THEN 'NPS'
    WHEN COALESCE(l.StatutoryType,'')='Workmen Compensation' OR UPPER(TRIM(l.ComponentCode)) IN ('WC','WORKMEN_COMP') THEN 'Workmen Compensation'
    ELSE ''
END AS `Statutory Group`,
CASE WHEN COALESCE(l.StatutoryType,'None')='None' THEN
    CASE
        WHEN UPPER(TRIM(l.ComponentCode)) IN ('PF','EPF') THEN 'PF Employee'
        WHEN UPPER(TRIM(l.ComponentCode))='VPF' THEN 'VPF'
        WHEN UPPER(TRIM(l.ComponentCode))='EPS' THEN 'EPS'
        WHEN UPPER(TRIM(l.ComponentCode)) IN ('EPF_ER','EPF_ER_BAL','PF_ER','PF_EMPLOYER') THEN 'PF Employer'
        WHEN UPPER(TRIM(l.ComponentCode)) IN ('ESI','ESIC','ESI_EE','ESIC_EE') THEN 'ESI Employee'
        WHEN UPPER(TRIM(l.ComponentCode)) IN ('ESI_ER','ESIC_ER') THEN 'ESI Employer'
        WHEN UPPER(TRIM(l.ComponentCode)) IN ('PT','PT_LWF_WC') THEN 'Professional Tax'
        WHEN UPPER(TRIM(l.ComponentCode))='TDS' THEN 'TDS'
        ELSE COALESCE(l.StatutoryType,'None')
    END
ELSE COALESCE(l.StatutoryType,'None') END AS `Statutory Type`,
l.ComponentCode AS `Component Code`,
l.Name AS Component,
l.Amount
FROM payrunemployeelines l
JOIN payrunemployees p ON p.Id=l.PayRunEmployeeId
JOIN payruns r ON r.Id=l.PayRunId
WHERE r.ClientId=@ClientId
AND (@PayRunId IS NULL OR r.Id=@PayRunId)
AND (@PayRunId IS NOT NULL OR r.PayPeriod=@Month)
AND (@EmployeeId IS NULL OR p.EmployeeId=@EmployeeId)
AND p.IsSkipped=FALSE
) x
WHERE x.`Statutory Group` <> ''
GROUP BY x.`Pay Period`,x.`Pay Run Id`,x.`Employee Code`,x.Employee,x.Department,x.`Statutory Group`,x.`Statutory Type`,x.`Component Code`,x.Component
ORDER BY x.`Pay Period` DESC,x.`Employee Code`,x.`Statutory Group`,x.`Component Code`";
        string? sql = code switch
        {
            "salary-register" => @"SELECT r.Id AS `Pay Run Id`, r.PayPeriod AS `Pay Period`, p.EmployeeCode AS `Employee Code`, p.EmployeeName AS Employee, p.Department, p.PresentDays AS `Present Days`, p.PayableDays AS `Payable Days`, p.GrossPay AS `Gross Pay`, p.StatutoryDeductions AS `Statutory Deductions`, p.OneTimeDeductions AS `Other Deductions`, p.NetPay AS `Net Pay`, p.PaymentStatus AS `Payment Status` FROM payrunemployees p JOIN payruns r ON r.Id=p.PayRunId WHERE p.ClientId=@ClientId AND (@PayRunId IS NULL OR r.Id=@PayRunId) AND (@PayRunId IS NOT NULL OR r.PayPeriod=@Month) AND (@EmployeeId IS NULL OR p.EmployeeId=@EmployeeId) AND p.IsSkipped=FALSE ORDER BY r.PayPeriod DESC,p.EmployeeCode",
            "component-ledger" => @"SELECT r.PayPeriod AS `Pay Period`,
r.Id AS `Pay Run Id`,
p.EmployeeCode AS `Employee Code`,
p.EmployeeName AS Employee,
p.Department,
l.ComponentCode AS `Component Code`,
l.Name AS Component,
l.Category,
COALESCE(l.ComponentRole,'') AS `Component Role`,
COALESCE(l.StatutoryType,'None') AS `Statutory Type`,
l.MonthlyAmount AS `Monthly Rate`,
l.Amount,
CASE WHEN l.Amount >= 0 THEN 'Payable' ELSE 'Recoverable' END AS Impact
FROM payrunemployeelines l
JOIN payrunemployees p ON p.Id=l.PayRunEmployeeId
JOIN payruns r ON r.Id=l.PayRunId
WHERE r.ClientId=@ClientId
AND (@PayRunId IS NULL OR r.Id=@PayRunId)
AND (@PayRunId IS NOT NULL OR @Month IS NULL OR r.PayPeriod=@Month)
AND (@EmployeeId IS NULL OR p.EmployeeId=@EmployeeId)
AND (@ComponentCode IS NULL OR @ComponentCode='' OR l.ComponentCode=@ComponentCode OR l.Category=@ComponentCode)
AND p.IsSkipped=FALSE
ORDER BY r.PayPeriod DESC,p.EmployeeCode,l.SortOrder,l.ComponentCode",
            "monthly-advice-report" => monthlyAdviceSql,
            "bank-transfer-report" => monthlyAdviceSql,
            "net-pay-estimate" => @"SELECT e.EmployeeCode AS `Employee Code`, CONCAT(e.FirstName,' ',e.LastName) AS Employee,
ROUND(COALESCE(s.Gross,0),2) AS `Gross Estimate`,
ROUND(COALESCE(s.Deductions,0)+COALESCE(p.EsicEmployee,0)+COALESCE(p.PtLwfWorkmenComp,0)+COALESCE(p.Tds,0)+COALESCE(p.Recovery,0),2) AS Deductions,
ROUND(COALESCE(s.Gross,0)-COALESCE(s.Deductions,0)-COALESCE(p.EsicEmployee,0)-COALESCE(p.PtLwfWorkmenComp,0)-COALESCE(p.Tds,0)-COALESCE(p.Recovery,0),2) AS `Net Pay Estimate`
FROM employees e
LEFT JOIN (SELECT esc.EmployeeId,
SUM(CASE WHEN COALESCE(sc.Category,'Earning') IN ('Earning','Reimbursement') THEN esc.Amount ELSE 0 END) Gross,
SUM(CASE WHEN COALESCE(sc.Category,'')='Deduction' THEN esc.Amount ELSE 0 END) Deductions
FROM employeesalarycomponents esc LEFT JOIN salarycomponents sc ON CAST(sc.Id AS CHAR)=esc.ComponentId OR sc.Code=esc.ComponentCode GROUP BY esc.EmployeeId) s ON s.EmployeeId=e.Id
LEFT JOIN employeepersonaldetails p ON p.EmployeeId=e.Id
WHERE e.ClientId=@ClientId AND e.IsActive=TRUE ORDER BY e.EmployeeCode",
            "pf-register" => pfRegisterSql,
            "pf-ecr-report" => pfEcrReportSql,
            "esi-register" => esiRegisterSql,
            "pt-register" => ptRegisterSql,
            "tds-register" => @"SELECT r.PayPeriod AS `Pay Period`,
p.EmployeeCode AS `Employee Code`,
p.EmployeeName AS Employee,
p.Department,
COALESCE(t.regime,'') AS Regime,
COALESCE(t.taxable_income,0) AS `Taxable Income`,
COALESCE(t.total_annual_tax,0) AS `Annual Tax`,
COALESCE(t.remaining_tax,0) AS `Remaining Tax`,
SUM(CASE WHEN l.StatutoryType='TDS' OR l.ComponentCode='TDS' THEN l.Amount ELSE 0 END) AS TDS
FROM payrunemployees p
JOIN payruns r ON r.Id=p.PayRunId
LEFT JOIN payrunemployeelines l ON l.PayRunEmployeeId=p.Id
LEFT JOIN tax_computation_snapshots t ON t.pay_run_id=r.Id AND t.employee_id=p.EmployeeId
WHERE p.ClientId=@ClientId AND (@PayRunId IS NULL OR r.Id=@PayRunId) AND (@PayRunId IS NOT NULL OR r.PayPeriod=@Month) AND (@EmployeeId IS NULL OR p.EmployeeId=@EmployeeId) AND p.IsSkipped=FALSE
GROUP BY r.PayPeriod,p.EmployeeCode,p.EmployeeName,p.Department,t.regime,t.taxable_income,t.total_annual_tax,t.remaining_tax
HAVING TDS <> 0 OR `Annual Tax` <> 0
ORDER BY p.EmployeeCode",
            "statutory-summary" => statutorySummarySql,
            "employee-master" => @"SELECT e.EmployeeCode AS `Employee Code`, CONCAT(e.FirstName,' ',e.LastName) AS Employee, e.Department, e.Designation, w.Name AS Location, e.DateOfJoining AS `Joining Date`, e.IsActive AS Active FROM employees e LEFT JOIN worklocations w ON w.Id=e.WorkLocationId WHERE e.ClientId=@ClientId AND (@Department IS NULL OR e.Department=@Department) AND (@WorkLocationId IS NULL OR e.WorkLocationId=@WorkLocationId) ORDER BY e.FirstName,e.LastName",
            "new-joiners" => @"SELECT e.EmployeeCode AS `Employee Code`, CONCAT(e.FirstName,' ',e.LastName) AS Employee, e.DateOfJoining AS `Joining Date`, e.Designation, w.Name AS Location FROM employees e LEFT JOIN worklocations w ON w.Id=e.WorkLocationId WHERE e.ClientId=@ClientId AND e.DateOfJoining >= DATE_SUB(CURDATE(), INTERVAL 90 DAY) ORDER BY e.DateOfJoining DESC",
            "tenure" => @"SELECT e.EmployeeCode AS `Employee Code`, CONCAT(e.FirstName,' ',e.LastName) AS Employee, e.DateOfJoining AS `Joining Date`, ROUND(DATEDIFF(CURDATE(), STR_TO_DATE(e.DateOfJoining,'%Y-%m-%d')) / 365.25, 1) AS `Tenure Years`, e.Designation FROM employees e WHERE e.ClientId=@ClientId AND e.IsActive=TRUE ORDER BY `Tenure Years` DESC",
            "headcount" => @"SELECT e.Department, COUNT(*) AS Headcount, SUM(e.AnnualCtc) AS `Annual CTC` FROM employees e WHERE e.ClientId=@ClientId AND e.IsActive=TRUE AND (@Department IS NULL OR e.Department=@Department) GROUP BY e.Department ORDER BY Headcount DESC",
            "location-cost" => @"SELECT w.Name AS Location, COUNT(e.Id) AS Headcount, SUM(e.AnnualCtc) AS `Annual CTC` FROM employees e LEFT JOIN worklocations w ON w.Id=e.WorkLocationId WHERE e.ClientId=@ClientId AND e.IsActive=TRUE GROUP BY w.Name ORDER BY `Annual CTC` DESC",
            "daily-attendance" => @"SELECT e.EmployeeCode AS `Employee Code`, CONCAT(e.FirstName,' ',e.LastName) AS Employee, e.Department, DATE_FORMAT(a.attendance_date,'%Y-%m-%d') AS Date, DAYNAME(a.attendance_date) AS Day, a.status AS Status, a.payable_value AS `Payable Value`, COALESCE(a.remarks,'') AS Remarks
FROM employee_daily_attendance a
JOIN employees e ON e.Id=a.employee_id
WHERE a.client_id=@ClientId AND a.attendance_date BETWEEN @FromDate AND @ToDate
AND (@Department IS NULL OR e.Department=@Department) AND (@WorkLocationId IS NULL OR e.WorkLocationId=@WorkLocationId)
ORDER BY a.attendance_date,e.EmployeeCode",
            "monthly-attendance" => @"SELECT e.EmployeeCode AS `Employee Code`, CONCAT(e.FirstName,' ',e.LastName) AS Employee, e.Department, a.attendance_month AS Month, a.working_days AS `Working Days`, a.present_days AS `Present Days`, a.payable_days AS `Payable Days`, a.lop_days AS `LOP Days`, a.source_type AS Source, COALESCE(a.remarks,'') AS Remarks
FROM employee_monthly_attendance a
JOIN employees e ON e.Id=a.employee_id
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
FROM employees e
LEFT JOIN employee_monthly_attendance a ON a.employee_id=e.Id AND a.client_id=e.ClientId AND a.attendance_month=@Month
WHERE e.ClientId=@ClientId AND e.IsActive=TRUE
AND (@Department IS NULL OR e.Department=@Department) AND (@WorkLocationId IS NULL OR e.WorkLocationId=@WorkLocationId)
HAVING Exception <> 'Ready'
ORDER BY e.EmployeeCode",
            "attendance-trend" => @"SELECT DATE_FORMAT(a.attendance_date,'%Y-%m-%d') AS Date, DAYNAME(a.attendance_date) AS Day, a.status AS Status, COUNT(*) AS Employees, SUM(a.payable_value) AS `Payable Value`
FROM employee_daily_attendance a
JOIN employees e ON e.Id=a.employee_id
WHERE a.client_id=@ClientId AND a.attendance_date BETWEEN @FromDate AND @ToDate
AND (@Department IS NULL OR e.Department=@Department) AND (@WorkLocationId IS NULL OR e.WorkLocationId=@WorkLocationId)
GROUP BY a.attendance_date,a.status
ORDER BY a.attendance_date, a.status",
            "leave-balance" => @"SELECT e.EmployeeCode AS `Employee Code`, CONCAT(e.FirstName,' ',e.LastName) AS Employee, lt.Name AS `Leave Type`, b.BalanceDate AS Date, b.BalanceCount AS Balance FROM employee_leave_balances b JOIN employees e ON e.Id=b.employee_id JOIN leave_types lt ON lt.Id=b.leave_type_id WHERE b.client_id=@ClientId ORDER BY e.EmployeeCode,lt.Name,b.BalanceDate",
            "lwp-balance" => @"SELECT e.EmployeeCode AS `Employee Code`, CONCAT(e.FirstName,' ',e.LastName) AS Employee, b.BalanceDate AS Date, b.BalanceCount AS `LWP Balance` FROM employee_leave_balances b JOIN employees e ON e.Id=b.employee_id JOIN leave_types lt ON lt.Id=b.leave_type_id WHERE b.client_id=@ClientId AND lt.Code='LWP' ORDER BY e.EmployeeCode",
            "leave-accrual" => @"SELECT lt.Code AS `Leave Code`, lt.Name AS `Leave Type`, lt.Type, p.entitlement AS Entitlement, p.entitlement_period AS `Entitlement Period`, p.pro_rate_for_new_joinees AS `Pro-rate New Joiners`, p.reset_enabled AS `Reset Enabled`, p.reset_frequency AS `Reset Frequency`, p.carry_forward_unused_leaves AS `Carry Forward`, p.max_carry_forward_limit AS `Carry Forward Limit`, p.encash_unused_leaves AS Encashment, COALESCE(p.allow_half_day,TRUE) AS `Half Day Allowed`, p.effective_from AS `Effective From`, p.expires_on AS `Expires On`, a.applicability_mode AS Applicability, a.department AS Department, a.designation AS Designation, a.work_location AS `Work Location`
FROM leave_types lt
JOIN leave_type_policies p ON p.leave_type_id=lt.id
JOIN leave_type_applicability a ON a.leave_type_id=lt.id
WHERE lt.client_id=@ClientId
ORDER BY lt.Name",
            "leave-utilization" => @"SELECT e.EmployeeCode AS `Employee Code`, CONCAT(e.FirstName,' ',e.LastName) AS Employee, e.Department, DATE_FORMAT(a.attendance_date,'%Y-%m') AS Month, a.status AS `Leave/Absence Type`, COUNT(*) AS Days, SUM(a.payable_value) AS `Payable Value`
FROM employee_daily_attendance a
JOIN employees e ON e.Id=a.employee_id
WHERE a.client_id=@ClientId AND a.attendance_date BETWEEN @FromDate AND @ToDate AND a.status IN ('Paid Leave','Absent','Half Day')
AND (@Department IS NULL OR e.Department=@Department) AND (@WorkLocationId IS NULL OR e.WorkLocationId=@WorkLocationId)
GROUP BY e.EmployeeCode, Employee, e.Department, DATE_FORMAT(a.attendance_date,'%Y-%m'), a.status
ORDER BY e.EmployeeCode, Month, `Leave/Absence Type`",
            "leave-approval-status" => @"SELECT e.EmployeeCode AS `Employee Code`, CONCAT(e.FirstName,' ',e.LastName) AS Employee, lt.Name AS `Leave Type`, COALESCE(r.DayType,'Full Day') AS `Day Type`, r.FromDate AS `From Date`, r.ToDate AS `To Date`, r.Days, r.Status, r.Reason, r.CreatedAt AS `Requested On`
FROM essleaverequests r
JOIN employees e ON e.Id=r.EmployeeId
JOIN leave_types lt ON lt.Id=r.LeaveTypeId
WHERE r.ClientId=@ClientId AND r.FromDate <= @ToDate AND r.ToDate >= @FromDate
AND (@Department IS NULL OR e.Department=@Department) AND (@WorkLocationId IS NULL OR e.WorkLocationId=@WorkLocationId)
ORDER BY r.CreatedAt DESC",
            "payroll-summary" => @"SELECT p.PayPeriod AS `Pay Period`, p.Status, COUNT(e.Id) AS Employees, p.PayrollCost AS `Payroll Cost`, p.NetPay AS `Net Pay` FROM payruns p LEFT JOIN payrunemployees e ON e.PayRunId=p.Id AND e.IsSkipped=FALSE WHERE p.ClientId=@ClientId GROUP BY p.Id,p.PayPeriod,p.Status,p.PayrollCost,p.NetPay ORDER BY p.PayPeriod DESC",
            _ => null
        };
        if (sql is null)
            return new ReportResult { Title = code, Columns = [], Rows = [] };
        var rows = (await db.QueryAsync(sql, filter)).Select(row => ((IDictionary<string, object>)row).ToDictionary(x => x.Key, x => (object?)x.Value)).ToList();
        return new ReportResult { Title = code, Columns = rows.FirstOrDefault()?.Keys.ToList() ?? [], Rows = rows };
    }

    private static async Task<ReportResult> RunClientBillingReportAsync(MySqlConnection db, ReportFilter filter)
    {
        var periodDate = DateTime.TryParse($"{filter.Month}-01", out var parsed) ? parsed.Date : DateTime.Today;
        var employees = (await db.QueryAsync<BillingEmployeeRow>(@"
SELECT r.Id AS PayRunId,
       r.PayPeriod,
       r.RunName,
       r.Status,
       p.Id AS PayRunEmployeeId,
       p.EmployeeId,
       p.EmployeeCode,
       p.EmployeeName,
       p.Department,
       p.PresentDays,
       p.PayableDays,
       p.GrossPay,
       p.StatutoryDeductions,
       p.OneTimeEarnings,
       p.OneTimeDeductions,
       p.NetPay,
       c.Name AS ClientName,
       COALESCE(e.WorkLocationId,0) AS WorkLocationId,
       COALESCE(w.Name,'All locations') AS WorkLocationName
FROM payruns r
JOIN payrunemployees p ON p.PayRunId=r.Id AND p.IsSkipped=FALSE
LEFT JOIN employees e ON e.Id=p.EmployeeId
LEFT JOIN clients c ON c.Id=r.ClientId
LEFT JOIN worklocations w ON w.Id=e.WorkLocationId
WHERE r.ClientId=@ClientId
  AND (@PayRunId IS NULL OR r.Id=@PayRunId)
  AND (@PayRunId IS NOT NULL OR r.PayPeriod=@Month)
  AND r.Status IN ('Draft','Pending Approval','Approved','Partially Paid','Paid')
  AND (@Department IS NULL OR p.Department=@Department)
  AND (@WorkLocationId IS NULL OR e.WorkLocationId=@WorkLocationId)
ORDER BY r.PayPeriod DESC, r.Id DESC, w.Name, p.EmployeeCode;", filter)).ToList();

        if (employees.Count == 0)
            return new ReportResult { Title = "Client Billing Report", Columns = BaseBillingColumns(), Rows = [] };

        var employeeRowIds = employees.Select(row => row.PayRunEmployeeId).Distinct().ToArray();
        var lines = (await db.QueryAsync<BillingComponentLine>(@"
SELECT PayRunEmployeeId, ComponentCode, Name, Category, COALESCE(ComponentRole,'') ComponentRole, COALESCE(StatutoryType,'None') StatutoryType, Amount, SortOrder
FROM payrunemployeelines
WHERE PayRunEmployeeId IN @EmployeeRowIds
ORDER BY SortOrder, ComponentCode;", new { EmployeeRowIds = employeeRowIds })).ToList();
        var linesByEmployeeRow = lines.GroupBy(row => row.PayRunEmployeeId).ToDictionary(group => group.Key, group => group.ToList());
        var componentColumns = lines
            .GroupBy(line => new { line.Category, line.ComponentCode, line.Name })
            .Select(group => new BillingComponentColumn(group.Key.Category, group.Key.ComponentCode, group.Key.Name, group.Min(line => line.SortOrder), ComponentColumnLabel(group.Key.Category, group.Key.ComponentCode, group.Key.Name)))
            .OrderBy(column => ComponentGroupOrder(column.Category)).ThenBy(column => column.SortOrder).ThenBy(column => column.Label)
            .ToList();

        var configs = (await db.QueryAsync<BillingConfigRow>(@"
SELECT Id, ClientId, WorkLocationId, RateCardType, RateType, Value, TaxInclusive, GstRatePercent, EffectiveFrom, EffectiveTo
FROM client_billing_configurations
WHERE ClientId=@ClientId
  AND IsActive=TRUE
  AND EffectiveFrom<=@PeriodDate
  AND (EffectiveTo IS NULL OR EffectiveTo>=@PeriodDate)
ORDER BY CASE WHEN WorkLocationId IS NULL THEN 1 ELSE 0 END, RateCardType, Id;", new { filter.ClientId, PeriodDate = periodDate })).ToList();
        var advancedEnabled = await db.ExecuteScalarAsync<bool?>(@"SELECT AdvancedCostingEnabled FROM client_billing_settings WHERE Id=1") ?? false;
        var advancedRules = advancedEnabled ? (await db.QueryAsync<AdvancedBillingRuleRow>(@"
SELECT h.Id HeaderId,h.ClientId,h.WorkLocationId,h.RuleName,h.GstRatePercent,l.Id LineId,l.LineType,l.MatchValue,l.BillingTreatment,l.BaseType,l.RateType,l.RateValue,l.TaxApplicable,l.CommissionApplicable,l.DisplayGroup,l.SortOrder
FROM client_billing_cost_rule_headers h
JOIN client_billing_cost_rule_lines l ON l.HeaderId=h.Id AND l.IsActive=TRUE
WHERE h.ClientId=@ClientId
  AND h.IsActive=TRUE
  AND h.EffectiveFrom<=@PeriodDate
  AND (h.EffectiveTo IS NULL OR h.EffectiveTo>=@PeriodDate)
ORDER BY CASE WHEN h.WorkLocationId IS NULL THEN 1 ELSE 0 END,h.Id,l.SortOrder,l.Id", new { filter.ClientId, PeriodDate = periodDate })).ToList() : [];

        var columns = BaseBillingColumns().Concat(componentColumns.Select(column => column.Label)).Concat([
            "Gross Pay",
            "Statutory Deductions",
            "Other Deductions",
            "Net Pay",
            "Billing Base Total",
            "Billing Rules",
            "Configured Rate",
            "Tax Basis",
            "Billing Amount Before GST",
            "GST Rate %",
            "GST Amount",
            "Final Billable Amount"
        ]).ToList();

        var rows = new List<Dictionary<string, object?>>();
        foreach (var employee in employees)
        {
            var employeeLines = linesByEmployeeRow.GetValueOrDefault(employee.PayRunEmployeeId) ?? [];
            var matchingConfigs = configs.Where(config => !config.WorkLocationId.HasValue || config.WorkLocationId.Value == employee.WorkLocationId).ToList();
            var matchingAdvanced = advancedRules.Where(rule => !rule.WorkLocationId.HasValue || rule.WorkLocationId.Value == employee.WorkLocationId).ToList();
            var billing = matchingAdvanced.Count > 0 ? CalculateAdvancedBilling(employee, employeeLines, matchingAdvanced) : CalculateBilling(employee, employeeLines, matchingConfigs);
            var row = new Dictionary<string, object?>
            {
                ["Pay Run"] = string.IsNullOrWhiteSpace(employee.RunName) ? $"Run #{employee.PayRunId}" : employee.RunName,
                ["Pay Run Id"] = employee.PayRunId,
                ["Pay Period"] = employee.PayPeriod,
                ["Status"] = employee.Status,
                ["Client"] = employee.ClientName,
                ["Work Location"] = employee.WorkLocationName,
                ["Employee Code"] = employee.EmployeeCode,
                ["Employee"] = employee.EmployeeName,
                ["Department"] = employee.Department,
                ["Present Days"] = employee.PresentDays,
                ["Payable Days"] = employee.PayableDays
            };

            foreach (var column in componentColumns)
            {
                row[column.Label] = employeeLines
                    .Where(line => line.ComponentCode == column.ComponentCode && line.Category == column.Category)
                    .Sum(line => line.Amount);
            }

            row["Gross Pay"] = employee.GrossPay;
            row["Statutory Deductions"] = employee.StatutoryDeductions;
            row["Other Deductions"] = employee.OneTimeDeductions;
            row["Net Pay"] = employee.NetPay;
            row["Billing Base Total"] = billing.BaseTotal;
            row["Billing Rules"] = billing.RuleSummary;
            row["Configured Rate"] = billing.RateSummary;
            row["Tax Basis"] = billing.TaxBasisSummary;
            row["Billing Amount Before GST"] = billing.AmountBeforeGst;
            row["GST Rate %"] = billing.GstRateSummary;
            row["GST Amount"] = billing.GstAmount;
            row["Final Billable Amount"] = billing.FinalAmount;
            rows.Add(row);
        }

        return new ReportResult { Title = "Client Billing Report", Columns = columns, Rows = rows };
    }

    private static List<string> BaseBillingColumns() => [
        "Pay Run",
        "Pay Run Id",
        "Pay Period",
        "Status",
        "Client",
        "Work Location",
        "Employee Code",
        "Employee",
        "Department",
        "Present Days",
        "Payable Days"
    ];

    private static BillingAmount CalculateBilling(BillingEmployeeRow employee, List<BillingComponentLine> lines, List<BillingConfigRow> configs)
    {
        decimal baseTotal = 0;
        decimal beforeGst = 0;
        decimal gst = 0;
        decimal final = 0;
        var rules = new List<string>();
        var rates = new List<string>();
        var taxBasis = new List<string>();

        foreach (var config in configs)
        {
            var baseAmount = BaseAmountFor(config.RateCardType, employee, lines);
            var configuredAmount = config.RateType.Equals("Percentage", StringComparison.OrdinalIgnoreCase)
                ? decimal.Round(baseAmount * config.Value / 100m, 2)
                : decimal.Round(config.Value, 2);
            var gstRate = Math.Max(0, config.GstRatePercent);
            var lineBeforeGst = config.TaxInclusive ? decimal.Round(configuredAmount / (1 + gstRate / 100m), 2) : configuredAmount;
            var lineGst = config.TaxInclusive ? decimal.Round(configuredAmount - lineBeforeGst, 2) : decimal.Round(lineBeforeGst * gstRate / 100m, 2);
            baseTotal += baseAmount;
            beforeGst += lineBeforeGst;
            gst += lineGst;
            final += config.TaxInclusive ? configuredAmount : lineBeforeGst + lineGst;
            rules.Add(config.RateCardType);
            rates.Add(config.RateType.Equals("Percentage", StringComparison.OrdinalIgnoreCase) ? $"{config.Value:0.####}% on {config.RateCardType}" : $"{config.Value:0.##} fixed {config.RateCardType}");
            taxBasis.Add(config.TaxInclusive ? "Inclusive" : "Excluding");
        }

        return new BillingAmount(decimal.Round(baseTotal, 2), string.Join(", ", rules.Distinct()), string.Join(", ", rates), string.Join(", ", taxBasis.Distinct()), string.Join(", ", configs.Select(config => $"{config.GstRatePercent:0.####}%").Distinct()), decimal.Round(beforeGst, 2), decimal.Round(gst, 2), decimal.Round(final, 2));
    }

    private static BillingAmount CalculateAdvancedBilling(BillingEmployeeRow employee, List<BillingComponentLine> lines, List<AdvancedBillingRuleRow> rules)
    {
        decimal baseTotal = 0;
        decimal beforeGst = 0;
        decimal gst = 0;
        var summaries = new List<string>();
        var rates = new List<string>();
        var taxBasis = new List<string>();
        var selectedHeaderId = rules.Where(rule => rule.WorkLocationId == employee.WorkLocationId).Select(rule => (long?)rule.HeaderId).FirstOrDefault()
            ?? rules.Select(rule => (long?)rule.HeaderId).FirstOrDefault();
        var activeRules = rules.Where(rule => rule.HeaderId == selectedHeaderId).OrderBy(rule => rule.SortOrder).ThenBy(rule => rule.LineId).ToList();
        decimal billableSalaryBase = 0;

        foreach (var rule in activeRules.Where(rule => !rule.LineType.Equals("Commission", StringComparison.OrdinalIgnoreCase)))
        {
            var baseAmount = AdvancedBaseAmount(rule, employee, lines, beforeGst);
            var amount = AdvancedRuleAmount(rule, baseAmount);
            if (amount == 0 && rule.RateType.Equals("Actual", StringComparison.OrdinalIgnoreCase)) continue;
            baseTotal += amount;
            beforeGst += amount;
            if (rule.CommissionApplicable) billableSalaryBase += amount;
            if (rule.TaxApplicable) gst += decimal.Round(amount * Math.Max(0, rule.GstRatePercent) / 100m, 2);
            summaries.Add($"{rule.DisplayGroup}: {RuleLabel(rule)}");
            rates.Add(RateLabel(rule, baseAmount));
            if (rule.TaxApplicable) taxBasis.Add("GST");
        }

        foreach (var rule in activeRules.Where(rule => rule.LineType.Equals("Commission", StringComparison.OrdinalIgnoreCase)))
        {
            var baseAmount = rule.BaseType.Equals("Billable Salary", StringComparison.OrdinalIgnoreCase) ? billableSalaryBase : AdvancedBaseAmount(rule, employee, lines, beforeGst);
            var amount = AdvancedRuleAmount(rule, baseAmount);
            beforeGst += amount;
            if (rule.TaxApplicable) gst += decimal.Round(amount * Math.Max(0, rule.GstRatePercent) / 100m, 2);
            summaries.Add($"{rule.DisplayGroup}: {RuleLabel(rule)}");
            rates.Add(RateLabel(rule, baseAmount));
            if (rule.TaxApplicable) taxBasis.Add("GST");
        }

        return new BillingAmount(decimal.Round(baseTotal, 2), string.Join(", ", summaries.Distinct()), string.Join(", ", rates), string.Join(", ", taxBasis.Distinct()), string.Join(", ", activeRules.Select(rule => $"{rule.GstRatePercent:0.####}%").Distinct()), decimal.Round(beforeGst, 2), decimal.Round(gst, 2), decimal.Round(beforeGst + gst, 2));
    }

    private static decimal AdvancedBaseAmount(AdvancedBillingRuleRow rule, BillingEmployeeRow employee, List<BillingComponentLine> lines, decimal currentBillable)
    {
        if (rule.BaseType.Equals("Gross Pay", StringComparison.OrdinalIgnoreCase)) return employee.GrossPay + employee.OneTimeEarnings;
        if (rule.BaseType.Equals("Net Pay", StringComparison.OrdinalIgnoreCase)) return employee.NetPay;
        if (rule.BaseType.Equals("Employer Cost", StringComparison.OrdinalIgnoreCase)) return employee.GrossPay + employee.OneTimeEarnings + lines.Where(line => line.Category.Contains("Benefit", StringComparison.OrdinalIgnoreCase) || line.ComponentRole.Contains("Employer", StringComparison.OrdinalIgnoreCase)).Sum(line => line.Amount);
        if (rule.BaseType.Equals("Billable Salary", StringComparison.OrdinalIgnoreCase)) return currentBillable;
        if (rule.LineType.Equals("Base Amount", StringComparison.OrdinalIgnoreCase)) return rule.BaseType.Equals("Net Pay", StringComparison.OrdinalIgnoreCase) ? employee.NetPay : employee.GrossPay + employee.OneTimeEarnings;
        if (rule.LineType.Equals("Payroll Component", StringComparison.OrdinalIgnoreCase)) return lines.Where(line => line.ComponentCode.Equals(rule.MatchValue, StringComparison.OrdinalIgnoreCase)).Sum(line => line.Amount);
        if (rule.LineType.Equals("Component Category", StringComparison.OrdinalIgnoreCase)) return lines.Where(line => line.Category.Equals(rule.MatchValue, StringComparison.OrdinalIgnoreCase)).Sum(line => line.Amount);
        if (rule.LineType.Equals("Statutory Type", StringComparison.OrdinalIgnoreCase)) return lines.Where(line => line.StatutoryType.Equals(rule.MatchValue, StringComparison.OrdinalIgnoreCase)).Sum(line => line.Amount);
        return 0;
    }

    private static decimal AdvancedRuleAmount(AdvancedBillingRuleRow rule, decimal baseAmount) =>
        rule.RateType.Equals("Percent", StringComparison.OrdinalIgnoreCase) ? decimal.Round(baseAmount * rule.RateValue / 100m, 2) :
        rule.RateType.Equals("Fixed", StringComparison.OrdinalIgnoreCase) || rule.LineType.Equals("Fixed Charge", StringComparison.OrdinalIgnoreCase) ? decimal.Round(rule.RateValue, 2) :
        decimal.Round(baseAmount, 2);

    private static string RuleLabel(AdvancedBillingRuleRow rule) =>
        string.IsNullOrWhiteSpace(rule.MatchValue) ? rule.LineType : $"{rule.LineType} {rule.MatchValue}";

    private static string RateLabel(AdvancedBillingRuleRow rule, decimal baseAmount) =>
        rule.RateType.Equals("Percent", StringComparison.OrdinalIgnoreCase) ? $"{rule.RateValue:0.####}% on {RuleLabel(rule)} ({baseAmount:0.##})" :
        rule.RateType.Equals("Fixed", StringComparison.OrdinalIgnoreCase) ? $"{rule.RateValue:0.##} fixed {RuleLabel(rule)}" :
        $"Actual {RuleLabel(rule)}";

    private static decimal BaseAmountFor(string rateCardType, BillingEmployeeRow employee, List<BillingComponentLine> lines)
    {
        var key = (rateCardType ?? "").ToLowerInvariant();
        if (key.Contains("reimbursement"))
            return SumLines(lines, "reimburs");
        if (key.Contains("bonus"))
        {
            var bonus = SumLines(lines, "bonus");
            return bonus > 0 ? bonus : employee.OneTimeEarnings;
        }
        if (key.Contains("statutory"))
        {
            var statutory = lines.Where(line => !string.IsNullOrWhiteSpace(line.StatutoryType) && !line.StatutoryType.Equals("None", StringComparison.OrdinalIgnoreCase) || ContainsAny(line, "statutory", "employer contribution", "pf", "esi", "pt", "lwf")).Sum(line => line.Amount);
            return statutory > 0 ? statutory : employee.StatutoryDeductions;
        }
        if (key.Contains("service"))
            return employee.GrossPay + employee.OneTimeEarnings;
        return employee.GrossPay + employee.OneTimeEarnings + employee.StatutoryDeductions;
    }

    private static decimal SumLines(List<BillingComponentLine> lines, string token) =>
        lines.Where(line => ContainsAny(line, token)).Sum(line => line.Amount);

    private static bool ContainsAny(BillingComponentLine line, params string[] tokens)
    {
        var text = $"{line.Category} {line.ComponentCode} {line.Name} {line.StatutoryType}".ToLowerInvariant();
        return tokens.Any(text.Contains);
    }

    private static string ComponentColumnLabel(string category, string code, string name)
    {
        var prefix = category switch
        {
            "Earning" => "E",
            "Deduction" => "D",
            "Reimbursement" => "R",
            "Employer Contribution" => "ER",
            _ => string.IsNullOrWhiteSpace(category) ? "Component" : category
        };
        var component = !string.IsNullOrWhiteSpace(code) ? code : name;
        return $"{prefix}: {component}";
    }

    private static int ComponentGroupOrder(string category) => category switch
    {
        "Earning" => 1,
        "Reimbursement" => 2,
        "Employer Contribution" => 3,
        "Deduction" => 4,
        _ => 9
    };

    private sealed record BillingEmployeeRow(int PayRunId, string PayPeriod, string RunName, string Status, int PayRunEmployeeId, int EmployeeId, string EmployeeCode, string EmployeeName, string Department, decimal PresentDays, decimal PayableDays, decimal GrossPay, decimal StatutoryDeductions, decimal OneTimeEarnings, decimal OneTimeDeductions, decimal NetPay, string ClientName, long WorkLocationId, string WorkLocationName);
    private sealed record BillingComponentLine(int PayRunEmployeeId, string ComponentCode, string Name, string Category, string ComponentRole, string StatutoryType, decimal Amount, int SortOrder);
    private sealed record BillingConfigRow(long Id, int ClientId, int? WorkLocationId, string RateCardType, string RateType, decimal Value, bool TaxInclusive, decimal GstRatePercent, DateTime EffectiveFrom, DateTime? EffectiveTo);
    private sealed record AdvancedBillingRuleRow(long HeaderId, int ClientId, int? WorkLocationId, string RuleName, decimal GstRatePercent, long LineId, string LineType, string MatchValue, string BillingTreatment, string BaseType, string RateType, decimal RateValue, bool TaxApplicable, bool CommissionApplicable, string DisplayGroup, int SortOrder);
    private sealed record BillingComponentColumn(string Category, string ComponentCode, string Name, int SortOrder, string Label);
    private sealed record BillingAmount(decimal BaseTotal, string RuleSummary, string RateSummary, string TaxBasisSummary, string GstRateSummary, decimal AmountBeforeGst, decimal GstAmount, decimal FinalAmount);
}
