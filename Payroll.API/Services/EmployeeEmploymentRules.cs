using System.Globalization;

namespace Payroll.API.Services;

// IT0000 is the source of dated exits and rehires. Saved payroll/payslip snapshots
// are never removed when an employee leaves.
internal static class EmployeeEmploymentRules
{
    internal static DateTime Today => TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.UtcNow, "Asia/Kolkata").Date;
    internal static readonly string[] ExitActions = ["Resign", "Retire", "Terminate"];
    internal static string LatestAction(string employeeAlias, string dateExpression) => $@"(SELECT lifecycle.ActionType
FROM employee_it0000_actions lifecycle WHERE lifecycle.EmployeeId={employeeAlias}.Id AND lifecycle.ClientId={employeeAlias}.ClientId
AND lifecycle.ActionType IN ('Resign','Retire','Terminate','Rehire') AND lifecycle.Status IN ('Active','Historical')
AND lifecycle.EffectiveFrom<=DATE({dateExpression}) ORDER BY lifecycle.EffectiveFrom DESC,lifecycle.Id DESC LIMIT 1)";

    internal static string EligibleOn(string employeeAlias, string dateExpression, bool historical = false)
    {
        var baseActive = $"{employeeAlias}.IsActive=TRUE";
        if (historical)
            baseActive += $@" OR EXISTS(SELECT 1 FROM employee_it0000_actions futureExit
WHERE futureExit.EmployeeId={employeeAlias}.Id AND futureExit.ClientId={employeeAlias}.ClientId
AND futureExit.ActionType IN ('Resign','Retire','Terminate') AND futureExit.Status IN ('Active','Historical')
AND futureExit.EffectiveFrom>DATE({dateExpression}))";
        return $"(({baseActive}) AND COALESCE({LatestAction(employeeAlias, dateExpression)},'Rehire') NOT IN ('Resign','Retire','Terminate'))";
    }

    internal static string EligibleInMonth(string alias, string monthExpression) =>
        $"({EligibleOn(alias, $"STR_TO_DATE(CONCAT({monthExpression},'-01'),'%Y-%m-%d')", true)} OR EXISTS(SELECT 1 FROM employee_it0000_actions monthRehire WHERE monthRehire.EmployeeId={alias}.Id AND monthRehire.ClientId={alias}.ClientId AND monthRehire.ActionType='Rehire' AND monthRehire.Status IN ('Active','Historical') AND DATE_FORMAT(monthRehire.EffectiveFrom,'%Y-%m')={monthExpression} AND {EligibleOn(alias, "monthRehire.EffectiveFrom", true)}))";

    internal static DateTime InactiveFrom(string lastWorkingDate) => DateTime.TryParseExact(lastWorkingDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) && date.Year is >= 1900 and < 9999
        ? date.Date.AddDays(1) : throw new InvalidOperationException("Enter a valid last working date (YYYY-MM-DD).");
}
