using Payroll.API.Models;

namespace Payroll.API.Services;

// Narrow workspace grants coexist with the original full-report/settings grants.
public static class WorkspacePermissions
{
    private static bool Has(AuthUser user,params string[] codes)=>user.Permissions.Any(permission=>codes.Contains(permission,StringComparer.OrdinalIgnoreCase));
    private static readonly HashSet<string> PayrollReports=new(StringComparer.OrdinalIgnoreCase)
    {"salary-register","payslip-register","excel-payslips","payroll-summary","component-ledger","monthly-advice-report","headcount","location-cost","net-pay-estimate","bank-transfer-report"};
    private static readonly HashSet<string> ComplianceReports=new(StringComparer.OrdinalIgnoreCase)
    {"pf-register","pf-ecr-report","esi-register","pt-register","statutory-summary","tds-register","form16-register"};
    private static readonly HashSet<string> BillingReports=new(StringComparer.OrdinalIgnoreCase)
    {"client-billing-report","payroll-cost-report"};

    public static bool CanReadReport(AuthUser user,string code)=>Has(user,"reports.view")
        ||Has(user,"reports.payroll.view")&&PayrollReports.Contains(code)
        ||Has(user,"reports.compliance.view")&&ComplianceReports.Contains(code)
        ||Has(user,"reports.billing.view")&&BillingReports.Contains(code);
    public static bool CanReadPayrollContext(AuthUser user)=>Has(user,"payroll.run","payroll.approve","payroll.payments","reports.view","reports.payroll.view","reports.compliance.view","reports.billing.view");
    public static bool CanReadPayslips(AuthUser user)=>Has(user,"payroll.run","payroll.approve","payroll.payments","reports.view","reports.payroll.view");
    public static bool CanManageBilling(AuthUser user)=>Has(user,"settings.manage")||user.ClientId is null&&Has(user,"billing.manage");
    public static bool CanReadPayrollAttendance(AuthUser user)=>Has(user,"payroll.run","payroll.approve","payroll.payments");
}
