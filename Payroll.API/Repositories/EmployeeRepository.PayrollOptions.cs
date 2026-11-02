using Payroll.API.Models;

namespace Payroll.API.Repositories;

public partial class EmployeeRepository
{
    public async Task<IEnumerable<Employee>> GetPayrollOptionsAsync(int? clientId=null)=>
        (await GetAsync(clientId)).Select(employee=>new Employee
        {
            Id=employee.Id,ClientId=employee.ClientId,EmployeeCode=employee.EmployeeCode,
            FirstName=employee.FirstName,LastName=employee.LastName,Department=employee.Department,
            AnnualCtc=employee.AnnualCtc,IsActive=employee.IsActive
        }).ToList();
}
