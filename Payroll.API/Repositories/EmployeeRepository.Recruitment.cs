using Dapper;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public partial class EmployeeRepository
{
    internal static async Task PrepareRecruitmentSaveAsync(MySqlConnection db)
    {
        await EnsureEmployeeInfotypeTablesAsync(db);
        await PayrollDataTableStore.EnsureAsync(db);
        await EnsureDefaultTaxProfileTableAsync(db);
    }

    internal Task<EmployeeImportPreflightResult> PreviewRecruitmentAsync(Employee employee, MySqlConnection? db = null, MySqlTransaction? tx = null) =>
        BuildImportPreflightAsync(employee.ClientId, new EmployeeImportWorkbook(new()
        {
            ["Employees"] = [
                ["Employee Code", "First Name", "Last Name", "Work Email", "Mobile", "PAN", "Aadhaar", "Bank Account No", "Date Of Joining", "Department", "Designation", "Grade", "Work Location Id", "Reporting Manager User Id", "Salary Template Id", "Annual CTC"],
                [employee.EmployeeCode, employee.FirstName, employee.LastName, employee.WorkEmail, employee.PersonalDetails.Mobile,
                    employee.PersonalDetails.PanNumber, employee.PersonalDetails.AadhaarNumber, employee.PaymentDetails.BankAccountNo,
                    employee.DateOfJoining, employee.Department, employee.Designation, employee.Grade,
                    employee.WorkLocationId > 0 ? employee.WorkLocationId.ToString() : "", employee.ReportingManagerUserId?.ToString() ?? "",
                    employee.SalaryStructureId, employee.AnnualCtc > 0 ? employee.AnnualCtc.ToString(System.Globalization.CultureInfo.InvariantCulture) : ""]
            ]
        }), UpsertImportMode, db, tx, validateConfigured: false);

    internal static Task<Employee?> LoadRecruitmentEmployeeAsync(MySqlConnection db, int id, MySqlTransaction? tx = null) => LoadEmployeeAsync(db, id, tx);
    internal static Task<int> SaveRecruitmentEmployeeAsync(MySqlConnection db, MySqlTransaction tx, Employee employee, AuthUser user, string reference) =>
        SaveWithOpenConnectionAsync(db, employee, user.DisplayName, null, $"Joined from recruitment application {reference}", tx);
}
