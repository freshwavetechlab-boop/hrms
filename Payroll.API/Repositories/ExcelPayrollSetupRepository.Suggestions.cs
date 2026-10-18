using Dapper;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayrollSetupRepository
{
    internal const string SundayOffConfiguration = "{\"workingDays\":[1,2,3,4,5,6],\"offSaturdays\":[]}";
    private static async Task<object> ReadClientDefaultsAsync(MySqlConnection db, int clientId, string month)
    {
        var policy = await db.QueryFirstOrDefaultAsync<ClientPolicyDefault>(@"SELECT g.id Id,g.name Name,g.work_week WorkWeek,
g.attendance_cycle_start_day StartDay,g.attendance_cycle_end_day EndDay,g.payroll_report_generation_day ReportDay
FROM attendance_groups g LEFT JOIN attendance_group_employees m ON m.attendance_group_id=g.id
WHERE g.client_id=@ClientId AND g.is_active=TRUE
GROUP BY g.id,g.name,g.work_week,g.attendance_cycle_start_day,g.attendance_cycle_end_day,g.payroll_report_generation_day
ORDER BY COUNT(m.employee_id) DESC,g.id DESC LIMIT 1", new { ClientId = clientId });
        async Task<string> Consensus(string column)
        {
            var values = (await db.QueryAsync<string>($"SELECT DISTINCT TRIM({column}) FROM employees WHERE ClientId=@ClientId AND IsActive=TRUE AND TRIM({column})<>'' LIMIT 2", new { ClientId = clientId })).ToArray();
            return values.Length == 1 ? values[0] : "Unassigned";
        }
        var department = await Consensus("Department"); var designation = await Consensus("Designation"); var grade = await Consensus("Grade");
        var states = (await db.QueryAsync<string>("SELECT DISTINCT TRIM(State) FROM worklocations WHERE ClientId=@ClientId AND IsActive=TRUE AND TRIM(State)<>'' LIMIT 2", new { ClientId = clientId })).ToArray();
        var days = DateTime.DaysInMonth(int.Parse(month[..4]), int.Parse(month[5..]));
        return new { department, designation, grade, workLocationState = states.Length == 1 ? states[0] : "", workWeek = policy?.WorkWeek ?? "Sunday off",
            attendanceCycleStartDay = policy?.StartDay ?? 1, attendanceCycleEndDay = policy?.EndDay ?? days,
            payrollReportGenerationDay = policy?.ReportDay ?? 3,
            attendanceSource = policy is null ? "Monthly Excel setup defaults" : $"Existing client policy: {policy.Name}",
            notes = new[] { "Source employee values take priority. Unassigned means the source/client has no unambiguous organizational value.",
                "Existing attendance memberships and statutory settings are retained. Monthly payable days come from the reviewed workbook.",
                "Leave quota, holiday dates and punch shifts cannot be inferred from salary totals; their editable proposals remain inactive." } };
    }

    private sealed class ClientPolicyDefault
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string WorkWeek { get; set; } = "";
        public int StartDay { get; set; }
        public int EndDay { get; set; }
        public int ReportDay { get; set; }
    }

    private static async Task InsertReviewedDropdownsAsync(MySqlConnection db, MySqlTransaction tx, int clientId, ExcelPayrollSetupRequest request, Plan plan)
    {
        if (!request.CreateMissingDropdownValues) return;
        var values = plan.Rows.SelectMany(row => new[] {
            (Type: "Department", Value: row.Employee.Department), (Type: "Designation", Value: row.Employee.Designation), (Type: "Employee Grade", Value: row.Employee.Grade)
        }).Append((Type: "Work Week", Value: request.WorkWeek)).Where(row => !string.IsNullOrWhiteSpace(row.Value)).Distinct().ToList();
        foreach (var row in values)
        {
            if (await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM dropdownmasters WHERE ClientId=@ClientId AND Type=@Type AND LOWER(Value)=LOWER(@Value) AND IsActive=FALSE)", new { ClientId = clientId, row.Type, Value = row.Value.Trim() }, tx))
                throw new InvalidOperationException($"The client's {row.Type} '{row.Value}' is inactive. Review or activate that master before applying this setup.");
            await db.ExecuteAsync(@"INSERT INTO dropdownmasters(ClientId,Type,Value,ConfigJson,IsActive)
SELECT @ClientId,@Type,@Value,CAST(@Config AS JSON),TRUE WHERE NOT EXISTS(SELECT 1 FROM dropdownmasters WHERE ClientId IN (0,@ClientId) AND Type=@Type AND LOWER(Value)=LOWER(@Value) AND IsActive=TRUE)",
                new { ClientId = clientId, row.Type, Value = row.Value.Trim(), Config = row.Type == "Work Week" ? SundayOffConfiguration : null }, tx);
        }
    }
}
