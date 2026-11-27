using Dapper;
using MySqlConnector;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public partial class PayRunRepository
{
    private sealed class JoiningDateRow { public int Id { get; set; } public string SalaryStructureId { get; set; } = ""; public string EmployeeCode { get; set; } = ""; public string JoiningDate { get; set; } = ""; }
    private static async Task<string?> BeforeJoiningEmployeeErrorAsync(MySqlConnection db, MySqlTransaction tx,
        int clientId, int employeeId, string month)
    {
        var row = await db.QuerySingleOrDefaultAsync<JoiningDateRow>(@"SELECT EmployeeCode,DATE_FORMAT(DateOfJoining,'%Y-%m-%d') JoiningDate
FROM employees WHERE ClientId=@ClientId AND Id=@EmployeeId", new { ClientId = clientId, EmployeeId = employeeId }, tx);
        return row is null ? null : EmployeeEmploymentRules.BeforeJoiningPayrollError(row.JoiningDate, month, row.EmployeeCode);
    }
    private static async Task<string?> BeforeJoiningTemplateErrorAsync(MySqlConnection db, MySqlTransaction tx,
        int clientId, string? templateId, string month, string search)
    {
        var rows = (await db.QueryAsync<JoiningDateRow>(@"SELECT Id,SalaryStructureId,EmployeeCode,DATE_FORMAT(DateOfJoining,'%Y-%m-%d') JoiningDate
FROM employees WHERE ClientId=@ClientId AND IsActive=TRUE
AND (@Search='' OR EmployeeCode LIKE @Pattern OR CONCAT(FirstName,' ',LastName) LIKE @Pattern)",
            new { ClientId = clientId, Search = search, Pattern = "%" + search + "%" }, tx)).ToArray();
        if (templateId is not null)
        {
            var policies = await EmployeeRepository.ReadTemplatePoliciesAsync(db, clientId, rows.Select(row => row.Id).ToArray(), tx);
            rows = rows.Where(row => policies.GetValueOrDefault(row.Id, row.SalaryStructureId) == templateId).ToArray();
        }
        if (rows.Length == 0 || rows.Any(row => EmployeeEmploymentRules.BeforeJoiningPayrollError(row.JoiningDate, month) is null)) return null;
        var earliest = rows.OrderBy(row => row.JoiningDate, StringComparer.Ordinal).First();
        return EmployeeEmploymentRules.BeforeJoiningPayrollError(earliest.JoiningDate, month, rows.Length == 1 ? earliest.EmployeeCode : null);
    }
}
