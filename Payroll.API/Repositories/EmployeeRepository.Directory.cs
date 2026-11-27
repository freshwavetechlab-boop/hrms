using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public partial class EmployeeRepository
{
    private static async Task ApplyDirectoryTemplateLabelsAsync(MySqlConnection db, List<Employee> employees, int? clientId)
    {
        if (employees.Count == 0) return;
        var policies = await ReadTemplatePoliciesAsync(db, clientId, employees.Select(employee => employee.Id).ToArray());
        foreach (var employee in employees)
            if (policies.TryGetValue(employee.Id, out var template))
            {
                employee.SalaryStructureId = template;
                employee.SalaryTemplatePolicyId = template;
            }
        // A template assigned for a past window can differ from the one effective today.
        // The audit sequence also picks up assignments made in-place on older Basic Pay rows.
        var latest = (await db.QueryAsync<ClosedSalaryHistoryRow>(@"SELECT h.EmployeeId,h.SalaryStructureId,h.EffectiveFrom,h.EffectiveTo
FROM (
  SELECT a.EmployeeId,MAX(a.Id) AuditId FROM employee_audit_trail a JOIN employees e ON e.Id=a.EmployeeId
  WHERE a.ActionType=@Action AND a.InfotypeCode='0008' AND a.FieldName='SalaryStructureId'
  AND (@ClientId IS NULL OR e.ClientId=@ClientId) GROUP BY a.EmployeeId
) changes JOIN employee_audit_trail a ON a.Id=changes.AuditId
JOIN employee_it0008_basic_pay h ON h.EmployeeId=a.EmployeeId AND h.EffectiveFrom=a.EffectiveFrom AND h.SalaryStructureId=a.NewValue
JOIN employees e ON e.Id=h.EmployeeId AND e.ClientId=h.ClientId
ORDER BY h.Id DESC", new { ClientId = clientId, Action = BulkSalaryAction }))
            .GroupBy(row => row.EmployeeId).ToDictionary(group => group.Key, group => group.First());

        static long TemplateKey(string value) => long.TryParse(value.Split(':', 2)[0], out var id) && id > 0 ? id : 0;
        var ids = employees.Select(row => TemplateKey(row.SalaryStructureId))
            .Concat(latest.Values.Select(row => TemplateKey(row.SalaryStructureId))).Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0) return;
        var templates = (await db.QueryAsync<(long Id, int ClientId, string ClientRef, string Name)>(
            "SELECT Id,ClientId,ClientRef,Name FROM salarystructures WHERE Id IN @ids", new { ids })).ToDictionary(item => item.Id);
        string? TemplateName(string value, int ownerClient)
        {
            if (!templates.TryGetValue(TemplateKey(value), out var item)) return null;
            var owner = SelectedClientOwnershipStore.ResolveSalaryStructureOwner(item.ClientId, item.ClientRef, ownerClient);
            return owner == 0 || owner == ownerClient ? item.Name : null;
        }
        foreach (var employee in employees)
        {
            employee.SalaryTemplateName = TemplateName(employee.SalaryStructureId, employee.ClientId);
            if (latest.TryGetValue(employee.Id, out var row))
                employee.LatestBulkSalaryAssignment = new(row.SalaryStructureId, TemplateName(row.SalaryStructureId, employee.ClientId), row.EffectiveFrom, row.EffectiveTo);
        }
    }

    // The selector must not wait for every employee's salary, bank and personal
    // records just to display a client name and its active-employee indicator.
    public async Task<IEnumerable<EmployeeDirectoryClient>> GetDirectoryClientsAsync(int? clientId = null)
    {
        await using var db = Connection();
        await db.OpenAsync();
        return await db.QueryAsync<EmployeeDirectoryClient>($@"SELECT c.Id,c.Name,c.Code,c.IsActive,COUNT(e.Id) ActiveEmployeeCount
FROM clients c
LEFT JOIN employees e ON e.ClientId=c.Id AND {EmployeeEmploymentRules.EligibleOn("e", "@Today")}
WHERE c.IsActive=TRUE AND (@ClientId IS NULL OR c.Id=@ClientId)
GROUP BY c.Id,c.Name,c.Code,c.IsActive
ORDER BY c.Name", new { ClientId = clientId, Today = EmployeeEmploymentRules.Today });
    }
}

public sealed class EmployeeDirectoryClient
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Code { get; set; } = "";
    public bool IsActive { get; set; }
    public int ActiveEmployeeCount { get; set; }
}
