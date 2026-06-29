using Dapper;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public class EmployeeRepository(IConfiguration configuration)
{
    private MySqlConnection Connection() => new(configuration.GetConnectionString("Default"));

    public async Task<IEnumerable<Employee>> GetAsync()
    {
        await using var db = Connection();
        await db.OpenAsync();
        await db.ExecuteAsync("USE payroll;");
        var rows = (await db.QueryAsync<Employee>("SELECT * FROM Employees ORDER BY FirstName, LastName")).ToList();
        await PayrollDataTableStore.ApplyEmployeeTablesAsync(db, rows);
        return rows;
    }

    public async Task<int> SaveAsync(Employee employee)
    {
        await using var db = Connection();
        await db.OpenAsync();
        await db.ExecuteAsync("USE payroll;");
        if (employee.Id == 0)
            employee.Id = (int)await db.ExecuteScalarAsync<long>(@"INSERT INTO employees (ClientId,EmployeeCode,FirstName,LastName,Gender,DateOfJoining,WorkEmail,Department,Designation,WorkLocationId,ReportingManagerId,PortalAccess,SalaryStructureId,AnnualCtc,SalaryJson,PersonalJson,PaymentJson,IsActive) VALUES (@ClientId,@EmployeeCode,@FirstName,@LastName,@Gender,@DateOfJoining,@WorkEmail,@Department,@Designation,@WorkLocationId,@ReportingManagerId,@PortalAccess,@SalaryStructureId,@AnnualCtc,@SalaryJson,@PersonalJson,@PaymentJson,@IsActive); SELECT LAST_INSERT_ID();", employee);
        else
            await db.ExecuteAsync(@"UPDATE employees SET ClientId=@ClientId,EmployeeCode=@EmployeeCode,FirstName=@FirstName,LastName=@LastName,Gender=@Gender,DateOfJoining=@DateOfJoining,WorkEmail=@WorkEmail,Department=@Department,Designation=@Designation,WorkLocationId=@WorkLocationId,ReportingManagerId=@ReportingManagerId,PortalAccess=@PortalAccess,SalaryStructureId=@SalaryStructureId,AnnualCtc=@AnnualCtc,SalaryJson=@SalaryJson,PersonalJson=@PersonalJson,PaymentJson=@PaymentJson,IsActive=@IsActive WHERE Id=@Id", employee);
        await PayrollDataTableStore.SyncEmployeeTablesAsync(db, employee);
        return employee.Id;
    }

    public async Task<bool> DeleteAsync(int id)
    {
        await using var db = Connection();
        await db.OpenAsync();
        await db.ExecuteAsync("USE payroll;");
        return await db.ExecuteAsync("DELETE FROM employees WHERE Id=@Id", new { Id = id }) > 0;
    }
}
