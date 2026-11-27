using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public partial class EmployeeRepository
{
    private static async Task EnsureTemplatePolicySchemaAsync(MySqlConnection db)
    {
        using var script = typeof(EmployeeRepository).Assembly.GetManifestResourceStream("Payroll.API.Database.EmployeeSalaryTemplatePolicies.sql")
            ?? throw new InvalidOperationException("The employee salary template policy schema is unavailable.");
        using var reader = new StreamReader(script);
        await db.ExecuteAsync(await reader.ReadToEndAsync());
    }

    private static async Task EnsureTemplatePolicyAvailableForWriteAsync(MySqlConnection db)
    {
        try { await db.ExecuteAsync("SELECT 1 FROM employee_salary_template_policies LIMIT 0"); }
        catch (MySqlException error) when (error.Number == 1146) { await EnsureTemplatePolicySchemaAsync(db); }
    }

    internal static async Task<Dictionary<int,string>> ReadTemplatePoliciesAsync(MySqlConnection db, int? clientId, int[] employeeIds,
        MySqlTransaction? tx = null, bool lockRows = false)
    {
        if (employeeIds.Length == 0) return [];
        try
        {
            var rows = await db.QueryAsync<(int EmployeeId,string SalaryStructureId)>(@"SELECT p.EmployeeId,p.SalaryStructureId
FROM employee_salary_template_policies p JOIN employees e ON e.Id=p.EmployeeId AND e.ClientId=p.ClientId
WHERE (@clientId IS NULL OR p.ClientId=@clientId) AND p.EmployeeId IN @employeeIds" + (lockRows ? " FOR UPDATE" : ""), new { clientId, employeeIds }, tx);
            return rows.ToDictionary(row => row.EmployeeId, row => row.SalaryStructureId);
        }
        catch (MySqlException error) when (!lockRows && error.Number == 1146) { return []; }
    }

    private static async Task WriteTemplatePolicyAsync(MySqlConnection db, MySqlTransaction tx, Employee employee,
        string previous, string selected, string actor)
    {
        var args = new { employeeId = employee.Id, employee.ClientId, employee.EmployeeCode, previous, selected, actor, today = EmployeeEmploymentRules.Today };
        if (selected.Length == 0)
            await db.ExecuteAsync("DELETE FROM employee_salary_template_policies WHERE EmployeeId=@employeeId AND ClientId=@ClientId", args, tx);
        else
            await db.ExecuteAsync(@"INSERT INTO employee_salary_template_policies (EmployeeId,ClientId,SalaryStructureId,AssignedBy,AssignedAtUtc)
VALUES (@employeeId,@ClientId,@selected,@actor,UTC_TIMESTAMP(6))
ON DUPLICATE KEY UPDATE ClientId=@ClientId,SalaryStructureId=@selected,AssignedBy=@actor,AssignedAtUtc=UTC_TIMESTAMP(6)", args, tx);
        await db.ExecuteAsync(@"INSERT INTO employee_audit_trail
(EmployeeId,EmployeeCode,ActionType,InfotypeCode,FieldName,OldValue,NewValue,EffectiveFrom,ChangedBy)
VALUES(@employeeId,@EmployeeCode,'Payroll template assignment','0008','Employee payroll template',@previous,@selected,@today,@actor)", args, tx);
    }
}
