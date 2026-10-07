using Dapper;

namespace Payroll.API.Repositories;

public partial class EssMssRepository
{
    public async Task<Dictionary<int, string>> GetPayslipRecipientEmailsAsync(int clientId, int[] employeeIds)
    {
        await using var db = Connection();
        await db.OpenAsync();
        var rows = await db.QueryAsync<(int EmployeeId, string WorkEmail)>(
            "SELECT Id AS EmployeeId,COALESCE(WorkEmail,'') AS WorkEmail FROM employees WHERE ClientId=@ClientId AND Id IN @EmployeeIds",
            new { ClientId = clientId, EmployeeIds = employeeIds });
        return rows.ToDictionary(row => row.EmployeeId, row => row.WorkEmail.Trim());
    }
}
