using Dapper;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public partial class EmployeeRepository
{
    // Picker and reference lists need the same effective active flag as Employee Master.
    // Loading salary components, personal details and bank history adds no data they display.
    private async Task<List<Employee>> GetReferenceEmployeesAsync(int? clientId, bool includeCtc)
    {
        await using var db = Connection();
        await db.OpenAsync();
        var columns = includeCtc ? "Id,ClientId,EmployeeCode,FirstName,LastName,Department,AnnualCtc,IsActive"
            : "Id,ClientId,Department,Designation,WorkLocationId,IsActive";
        var where = clientId.HasValue ? " WHERE ClientId=@ClientId" : "";
        var rows = (await db.QueryAsync<Employee>($"SELECT {columns} FROM employees{where} ORDER BY FirstName, LastName", new { ClientId = clientId })).AsList();
        await ApplyEmploymentStatusAsync(db, rows);
        return rows;
    }

    public async Task<IEnumerable<Employee>> GetPayrollOptionsAsync(int? clientId = null) =>
        (await GetReferenceEmployeesAsync(clientId, true)).Select(employee => new Employee
        {
            Id=employee.Id,ClientId=employee.ClientId,EmployeeCode=employee.EmployeeCode,
            FirstName=employee.FirstName,LastName=employee.LastName,Department=employee.Department,
            AnnualCtc=employee.AnnualCtc,IsActive=employee.IsActive
        }).ToList();

    public sealed record SettingsEmployeeReference(int Id, int ClientId, string Department, string Designation, int WorkLocationId, bool IsActive);

    public async Task<IEnumerable<SettingsEmployeeReference>> GetSettingsReferencesAsync(int? clientId) =>
        (await GetReferenceEmployeesAsync(clientId, false)).Select(employee =>
            new SettingsEmployeeReference(employee.Id, employee.ClientId, employee.Department, employee.Designation, employee.WorkLocationId, employee.IsActive));
}
