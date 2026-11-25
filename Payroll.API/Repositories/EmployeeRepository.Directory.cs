using Dapper;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public partial class EmployeeRepository
{
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
