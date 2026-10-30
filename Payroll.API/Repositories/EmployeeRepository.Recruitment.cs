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
        BuildImportPreflightAsync(employee.ClientId, RecruitmentIdentityWorkbook([employee]), UpsertImportMode, db, tx, validateConfigured: false);

    internal Task<EmployeeImportPreflightResult> PreviewExcelPayrollIdentitiesAsync(IReadOnlyList<Employee> candidates, MySqlConnection? db = null, MySqlTransaction? tx = null) =>
        BuildImportPreflightAsync(candidates[0].ClientId, RecruitmentIdentityWorkbook(candidates), UpsertImportMode, db, tx, validateConfigured: false, identityOnly: true);

    internal static List<EmployeeImportPreflightRow> ExcelPayrollIdentityRows(IReadOnlyList<Employee> candidates, IReadOnlyList<Employee> current) =>
        BuildImportIdentityPreflightRows(RecruitmentIdentityWorkbook(candidates), UpsertImportMode, current, includeFieldChanges: false);

    internal static EmployeeImportWorkbook RecruitmentIdentityWorkbook(IReadOnlyList<Employee> candidates) => new(new()
    {
        ["Employees"] = new List<List<string>>
        {
            new() { "Employee Code", "First Name", "Last Name", "Work Email", "Mobile", "PAN", "Aadhaar", "Bank Account No", "Date Of Joining", "Department", "Designation", "Grade", "Work Location Id", "Reporting Manager User Id", "Salary Template Id", "Annual CTC" }
        }.Concat(candidates.Select(employee => new List<string>
        {
            employee.EmployeeCode, employee.FirstName, employee.LastName, employee.WorkEmail, employee.PersonalDetails.Mobile,
            employee.PersonalDetails.PanNumber, employee.PersonalDetails.AadhaarNumber, employee.PaymentDetails.BankAccountNo,
            employee.DateOfJoining, employee.Department, employee.Designation, employee.Grade,
            employee.WorkLocationId > 0 ? employee.WorkLocationId.ToString() : "", employee.ReportingManagerUserId?.ToString() ?? "",
            employee.SalaryStructureId, employee.AnnualCtc > 0 ? employee.AnnualCtc.ToString(System.Globalization.CultureInfo.InvariantCulture) : ""
        })).ToList()
    });

    internal static async Task<List<Employee>> ReadExcelPayrollIdentitySnapshotAsync(MySqlConnection db, MySqlTransaction? tx, int clientId)
    {
        var current = (await db.QueryAsync<Employee>("SELECT * FROM employees WHERE ClientId=@ClientId ORDER BY Id", new { ClientId=clientId }, tx)).ToList();
        await PayrollDataTableStore.ApplyEmployeeTablesAsync(db, current, tx);
        return current;
    }

    internal static Task<Employee?> LoadRecruitmentEmployeeAsync(MySqlConnection db, int id, MySqlTransaction? tx = null) => LoadEmployeeAsync(db, id, tx);
    internal static Task<int> SaveRecruitmentEmployeeAsync(MySqlConnection db, MySqlTransaction tx, Employee employee, AuthUser user, string reference) =>
        SaveWithOpenConnectionAsync(db, employee, user.DisplayName, null, $"Joined from recruitment application {reference}", tx);
}
