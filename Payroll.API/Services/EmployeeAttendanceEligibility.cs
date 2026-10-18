using System.Data;
using System.Text.Json;
using Dapper;
using MySqlConnector;

namespace Payroll.API.Services;

internal static class EmployeeAttendanceEligibility
{
    internal static async Task<string?> ValidateDatesAsync(MySqlConnection db, IDbTransaction? tx, int clientId, IEnumerable<(int EmployeeId, DateTime Date)> rows)
    {
        var values = rows.Distinct().Select(row => new { employeeId = row.EmployeeId, date = row.Date.ToString("yyyy-MM-dd") }).ToArray();
        if (values.Length == 0) return null;
        await LockEmployeesAsync(db, tx, clientId, values.Select(row => row.employeeId));
        var invalid = await db.QueryFirstOrDefaultAsync<InvalidAttendance>(DateValidationSql,
            new { Rows = JsonSerializer.Serialize(values), ClientId = clientId }, tx);
        return invalid is null ? null : $"Employee {invalid.EmployeeId}: attendance on {invalid.AttendanceDate:yyyy-MM-dd} is outside their employment dates. Review the last working date.";
    }

    internal static async Task<string?> ValidateMonthlyAsync(MySqlConnection db, IDbTransaction? tx, int clientId, string month, IEnumerable<(int EmployeeId, decimal Present, decimal Payable)> rows, bool lockEmployees = true)
    {
        var values = rows.Select(row => new { employeeId = row.EmployeeId, present = row.Present, payable = row.Payable }).ToArray();
        if (values.Length == 0) return null;
        if (lockEmployees) await LockEmployeesAsync(db, tx, clientId, values.Select(row => row.employeeId));
        var start = DateTime.ParseExact(month + "-01", "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var end = start.AddMonths(1).AddDays(-1);
        var invalid = await db.QueryFirstOrDefaultAsync<int?>(MonthlyValidationSql,
            new { Rows = JsonSerializer.Serialize(values), ClientId = clientId, Month = month, Start = start, End = end }, tx);
        return invalid is null ? null : $"Employee {invalid}: monthly attendance exceeds the eligible employment days. Review the last working / rehire date before saving.";
    }

    internal static string DateValidationSql => @"SELECT submitted.EmployeeId,submitted.AttendanceDate
FROM JSON_TABLE(@Rows,'$[*]' COLUMNS(EmployeeId INT PATH '$.employeeId',AttendanceDate DATE PATH '$.date')) submitted
LEFT JOIN employees e ON e.Id=submitted.EmployeeId AND e.ClientId=@ClientId
WHERE e.Id IS NULL OR NOT " + EmployeeEmploymentRules.EligibleOn("e", "submitted.AttendanceDate", true) + " LIMIT 1";

    internal static string MonthlyValidationSql
    {
        get
        {
        const string days = "(SELECT 0 n UNION ALL SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL SELECT 4 UNION ALL SELECT 5 UNION ALL SELECT 6 UNION ALL SELECT 7 UNION ALL SELECT 8 UNION ALL SELECT 9 UNION ALL SELECT 10 UNION ALL SELECT 11 UNION ALL SELECT 12 UNION ALL SELECT 13 UNION ALL SELECT 14 UNION ALL SELECT 15 UNION ALL SELECT 16 UNION ALL SELECT 17 UNION ALL SELECT 18 UNION ALL SELECT 19 UNION ALL SELECT 20 UNION ALL SELECT 21 UNION ALL SELECT 22 UNION ALL SELECT 23 UNION ALL SELECT 24 UNION ALL SELECT 25 UNION ALL SELECT 26 UNION ALL SELECT 27 UNION ALL SELECT 28 UNION ALL SELECT 29 UNION ALL SELECT 30)";
        var eligibleDays = "(SELECT COUNT(*) FROM " + days + " calendar WHERE DATE_ADD(@Start,INTERVAL calendar.n DAY)<=@End AND " + EmployeeEmploymentRules.EligibleOn("e", "DATE_ADD(@Start,INTERVAL calendar.n DAY)", true) + ")";
        return @"SELECT submitted.EmployeeId
FROM JSON_TABLE(@Rows,'$[*]' COLUMNS(EmployeeId INT PATH '$.employeeId',Present DECIMAL(12,2) PATH '$.present',Payable DECIMAL(12,2) PATH '$.payable')) submitted
LEFT JOIN employees e ON e.Id=submitted.EmployeeId AND e.ClientId=@ClientId
WHERE e.Id IS NULL OR NOT " + EmployeeEmploymentRules.EligibleInMonth("e", "@Month") + @"
OR (EXISTS(SELECT 1 FROM employee_it0000_actions boundary WHERE boundary.EmployeeId=e.Id AND boundary.ClientId=e.ClientId
AND boundary.Status IN ('Active','Historical') AND boundary.ActionType IN ('Resign','Retire','Terminate','Rehire') AND boundary.EffectiveFrom BETWEEN @Start AND @End)
AND GREATEST(submitted.Present,submitted.Payable)>" + eligibleDays + ") LIMIT 1";
        }
    }

    private static async Task LockEmployeesAsync(MySqlConnection db, IDbTransaction? tx, int clientId, IEnumerable<int> ids)
    {
        if (tx is not null) await db.QueryAsync<int>("SELECT Id FROM employees WHERE ClientId=@ClientId AND Id IN @Ids ORDER BY Id FOR UPDATE", new { ClientId = clientId, Ids = ids.Distinct().ToArray() }, tx);
    }

    private sealed class InvalidAttendance
    {
        public int EmployeeId { get; set; }
        public DateTime AttendanceDate { get; set; }
    }
}
