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
        if (code == "client-billing-report")
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
WHERE p.ClientId=@ClientId AND r.PayPeriod=@Month AND p.IsSkipped=FALSE
AND LOWER(COALESCE(pay.PaymentMode,''))='bank transfer'
ORDER BY p.EmployeeCode";
        string? sql = code switch
        {
            "salary-register" => @"SELECT r.PayPeriod AS `Pay Period`, p.EmployeeCode AS `Employee Code`, p.EmployeeName AS Employee, p.Department, p.PresentDays AS `Present Days`, p.PayableDays AS `Payable Days`, p.GrossPay AS `Gross Pay`, p.StatutoryDeductions AS `Statutory Deductions`, p.OneTimeDeductions AS `Other Deductions`, p.NetPay AS `Net Pay`, p.PaymentStatus AS `Payment Status` FROM payrunemployees p JOIN payruns r ON r.Id=p.PayRunId WHERE p.ClientId=@ClientId AND r.PayPeriod=@Month AND p.IsSkipped=FALSE ORDER BY r.PayPeriod DESC,p.EmployeeCode",
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
            "pf-register" => @"SELECT e.EmployeeCode AS `Employee Code`, CONCAT(e.FirstName,' ',e.LastName) AS Employee,
MAX(CASE WHEN sc.Code='BASIC' THEN esc.Amount END) AS Basic,
MAX(CASE WHEN sc.Code IN ('PF','EPF') THEN esc.Amount END) AS `Employee PF`
FROM employees e LEFT JOIN employeesalarycomponents esc ON esc.EmployeeId=e.Id LEFT JOIN salarycomponents sc ON CAST(sc.Id AS CHAR)=esc.ComponentId OR sc.Code=esc.ComponentCode
WHERE e.ClientId=@ClientId AND e.IsActive=TRUE GROUP BY e.Id,e.EmployeeCode,Employee ORDER BY e.EmployeeCode",
            "esi-register" => @"SELECT e.EmployeeCode AS `Employee Code`, CONCAT(e.FirstName,' ',e.LastName) AS Employee, COALESCE(p.EsicEmployee,0) AS `Employee ESIC` FROM employees e LEFT JOIN employeepersonaldetails p ON p.EmployeeId=e.Id WHERE e.ClientId=@ClientId AND e.IsActive=TRUE ORDER BY e.EmployeeCode",
            "pt-register" => @"SELECT r.PayPeriod AS `Pay Period`,
p.EmployeeCode AS `Employee Code`,
p.EmployeeName AS Employee,
p.Department,
COALESCE(ep.State,w.State,'') AS State,
COALESCE(o.ProfessionalTaxNumber,'') AS `PT Registration No`,
l.Amount AS `Professional Tax`
FROM payrunemployeelines l
JOIN payrunemployees p ON p.Id=l.PayRunEmployeeId
JOIN payruns r ON r.Id=l.PayRunId
LEFT JOIN employees e ON e.Id=p.EmployeeId
LEFT JOIN employeepersonaldetails ep ON ep.EmployeeId=p.EmployeeId
LEFT JOIN worklocations w ON w.Id=e.WorkLocationId
LEFT JOIN organizations o ON 1=1
WHERE r.ClientId=@ClientId AND r.PayPeriod=@Month AND l.ComponentCode='PT_LWF_WC' AND l.Amount > 0
ORDER BY p.EmployeeCode",
            "tds-register" => @"SELECT e.EmployeeCode AS `Employee Code`, CONCAT(e.FirstName,' ',e.LastName) AS Employee, COALESCE(p.Tds,0) AS TDS FROM employees e LEFT JOIN employeepersonaldetails p ON p.EmployeeId=e.Id WHERE e.ClientId=@ClientId AND e.IsActive=TRUE ORDER BY e.EmployeeCode",
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
            "leave-accrual" => @"SELECT lt.Code AS `Leave Code`, lt.Name AS `Leave Type`, lt.Type, p.entitlement AS Entitlement, p.entitlement_period AS `Entitlement Period`, p.pro_rate_for_new_joinees AS `Pro-rate New Joiners`, p.reset_enabled AS `Reset Enabled`, p.reset_frequency AS `Reset Frequency`, p.carry_forward_unused_leaves AS `Carry Forward`, p.max_carry_forward_limit AS `Carry Forward Limit`, p.encash_unused_leaves AS Encashment, p.effective_from AS `Effective From`, p.expires_on AS `Expires On`, a.applicability_mode AS Applicability, a.department AS Department, a.designation AS Designation, a.work_location AS `Work Location`
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
            "leave-approval-status" => @"SELECT e.EmployeeCode AS `Employee Code`, CONCAT(e.FirstName,' ',e.LastName) AS Employee, lt.Name AS `Leave Type`, r.FromDate AS `From Date`, r.ToDate AS `To Date`, r.Days, r.Status, r.Reason, r.CreatedAt AS `Requested On`
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
  AND r.PayPeriod=@Month
  AND r.Status IN ('Draft','Pending Approval','Approved','Partially Paid','Paid')
  AND (@Department IS NULL OR p.Department=@Department)
  AND (@WorkLocationId IS NULL OR e.WorkLocationId=@WorkLocationId)
ORDER BY r.PayPeriod DESC, r.Id DESC, w.Name, p.EmployeeCode;", filter)).ToList();

        if (employees.Count == 0)
            return new ReportResult { Title = "Client Billing Report", Columns = BaseBillingColumns(), Rows = [] };

        var employeeRowIds = employees.Select(row => row.PayRunEmployeeId).Distinct().ToArray();
        var lines = (await db.QueryAsync<BillingComponentLine>(@"
SELECT PayRunEmployeeId, ComponentCode, Name, Category, Amount, SortOrder
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
            var billing = CalculateBilling(employee, employeeLines, matchingConfigs);
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
            var statutory = lines.Where(line => ContainsAny(line, "statutory", "employer contribution", "pf", "esi", "pt", "lwf")).Sum(line => line.Amount);
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
        var text = $"{line.Category} {line.ComponentCode} {line.Name}".ToLowerInvariant();
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

    private sealed record BillingEmployeeRow(int PayRunId, string PayPeriod, string RunName, string Status, int PayRunEmployeeId, int EmployeeId, string EmployeeCode, string EmployeeName, string Department, decimal PresentDays, decimal PayableDays, decimal GrossPay, decimal StatutoryDeductions, decimal OneTimeEarnings, decimal OneTimeDeductions, decimal NetPay, string ClientName, int WorkLocationId, string WorkLocationName);
    private sealed record BillingComponentLine(int PayRunEmployeeId, string ComponentCode, string Name, string Category, decimal Amount, int SortOrder);
    private sealed record BillingConfigRow(long Id, int ClientId, int? WorkLocationId, string RateCardType, string RateType, decimal Value, bool TaxInclusive, decimal GstRatePercent, DateTime EffectiveFrom, DateTime? EffectiveTo);
    private sealed record BillingComponentColumn(string Category, string ComponentCode, string Name, int SortOrder, string Label);
    private sealed record BillingAmount(decimal BaseTotal, string RuleSummary, string RateSummary, string TaxBasisSummary, string GstRateSummary, decimal AmountBeforeGst, decimal GstAmount, decimal FinalAmount);
}
