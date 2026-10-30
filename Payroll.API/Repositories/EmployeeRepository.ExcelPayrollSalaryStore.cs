using System.Data;
using Dapper;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public partial class EmployeeRepository
{
    internal static void ValidateExcelSalaryOnlyScope(Employee employee,string? infotypeCode,bool synchronizeAttendancePolicies)
    {
        if(employee.Id<=0 || employee.ClientId<=0 || infotypeCode!="0008" || synchronizeAttendancePolicies)
            throw new InvalidOperationException("Salary-only persistence requires an existing employee, client, basic-pay scope and unchanged attendance policies.");
    }

    // Shared by the real MySQL save and a recording ADO regression. There are no
    // personal/payment, organization, attendance, policy or payroll-run writes.
    internal static async Task WriteExcelPayrollSalaryStateAsync(IDbConnection db,IDbTransaction tx,Employee employee)
    {
        ValidateExcelSalaryOnlyScope(employee,"0008",false);
        if(tx is null || !ReferenceEquals(tx.Connection,db) || db.State!=ConnectionState.Open)
            throw new InvalidOperationException("Salary-only persistence requires the caller's open transaction.");
        var client=await db.ExecuteScalarAsync<int?>("SELECT ClientId FROM employees WHERE Id=@Id FOR UPDATE",new{employee.Id},tx);
        if(client!=employee.ClientId)throw new InvalidOperationException("The reviewed employee does not belong to this client.");
        await PayrollDataTableStore.SyncEmployeeSalaryOnlyAsync(db,employee,tx);
        var affected=await db.ExecuteAsync(@"UPDATE employees SET SalaryStructureId=@SalaryStructureId,AnnualCtc=@AnnualCtc,SalaryJson=@SalaryJson
WHERE Id=@Id AND ClientId=@ClientId",new{employee.Id,employee.ClientId,employee.SalaryStructureId,employee.AnnualCtc,employee.SalaryJson},tx);
        if(affected!=1)throw new InvalidOperationException("The reviewed salary target changed. No salary setup may be committed.");
    }
}
