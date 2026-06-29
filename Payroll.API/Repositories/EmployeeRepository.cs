using Dapper;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public class EmployeeRepository(IConfiguration configuration)
{
    private MySqlConnection Connection() => new(configuration.GetConnectionString("Default"));
    public async Task<IEnumerable<Employee>> GetAsync() { await using var db = Connection(); await db.OpenAsync(); var rows = (await db.QueryAsync<Employee>("SELECT * FROM employees ORDER BY FirstName, LastName")).ToList(); await PayrollDataTableStore.ApplyEmployeeTablesAsync(db, rows); return rows; }
    public async Task<int> SaveAsync(Employee employee) { await using var db = Connection(); await db.OpenAsync(); if (employee.Id == 0) employee.Id = (int)await db.ExecuteScalarAsync<long>(@"INSERT INTO employees (ClientId,EmployeeCode,FirstName,LastName,Gender,DateOfJoining,WorkEmail,Department,Designation,WorkLocationId,ReportingManagerId,PortalAccess,SalaryStructureId,AnnualCtc,SalaryJson,PersonalJson,PaymentJson,IsActive) VALUES (@ClientId,@EmployeeCode,@FirstName,@LastName,@Gender,@DateOfJoining,@WorkEmail,@Department,@Designation,@WorkLocationId,@ReportingManagerId,@PortalAccess,@SalaryStructureId,@AnnualCtc,@SalaryJson,@PersonalJson,@PaymentJson,@IsActive); SELECT LAST_INSERT_ID();", employee); else await db.ExecuteAsync(@"UPDATE employees SET ClientId=@ClientId,EmployeeCode=@EmployeeCode,FirstName=@FirstName,LastName=@LastName,Gender=@Gender,DateOfJoining=@DateOfJoining,WorkEmail=@WorkEmail,Department=@Department,Designation=@Designation,WorkLocationId=@WorkLocationId,ReportingManagerId=@ReportingManagerId,PortalAccess=@PortalAccess,SalaryStructureId=@SalaryStructureId,AnnualCtc=@AnnualCtc,IsActive=@IsActive WHERE Id=@Id", employee); await PayrollDataTableStore.SyncEmployeeTablesAsync(db, employee); await db.ExecuteAsync("UPDATE employees SET SalaryJson=@SalaryJson,PersonalJson=@PersonalJson,PaymentJson=@PaymentJson WHERE Id=@Id", employee); return employee.Id; }
}
