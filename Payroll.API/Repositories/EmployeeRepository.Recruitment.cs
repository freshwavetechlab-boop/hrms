using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

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
    internal static bool CanInitializeRecruitmentSalary(Employee employee, IReadOnlyDictionary<string, decimal> inputs, decimal annualCtc = 0, bool hasComponentHistory = false) =>
        employee.Id > 0 && employee.ClientId > 0 && employee.IsActive && employee.AnnualCtc == 0 && string.IsNullOrWhiteSpace(employee.SalaryStructureId)
        && annualCtc >= 0 && annualCtc <= 9999999999999999.99m && decimal.Round(annualCtc, 2) == annualCtc
        && inputs.All(p => p.Value > 0 && p.Value <= 999999999999.99m && decimal.Round(p.Value, 2) == p.Value)
        && (annualCtc > 0 || inputs.Count > 0 && !hasComponentHistory && !employee.SalaryComponents.Values.Any(v => v != 0));

    internal static bool ApplyRecruitmentSalarySeed(Employee employee, Dictionary<string, decimal> inputs, decimal annualCtc = 0, bool hasComponentHistory = false)
    {
        if (!CanInitializeRecruitmentSalary(employee, inputs, annualCtc, hasComponentHistory)) return false;
        if (!hasComponentHistory && !employee.SalaryComponents.Values.Any(v => v != 0) && inputs.Count > 0)
        {
            employee.SalaryComponents = new(inputs);
            employee.SalaryJson = System.Text.Json.JsonSerializer.Serialize(inputs);
        }
        if (annualCtc > 0) employee.AnnualCtc = annualCtc;
        return true;
    }

    internal static async Task<bool> InitializeRecruitmentSalaryAsync(MySqlConnection db, MySqlTransaction tx, Employee employee, Dictionary<string, decimal> inputs, decimal annualCtc = 0)
    {
        if (!CanInitializeRecruitmentSalary(employee, inputs, annualCtc)) return false;
        if (await db.ExecuteScalarAsync<bool>("SELECT COALESCE(" + EmployeeEmploymentRules.LatestAction("e", "@Today") + ",'Rehire') IN ('Resign','Retire','Terminate') FROM employees e WHERE e.Id=@Id AND e.ClientId=@ClientId", new { employee.Id, employee.ClientId, Today = EmployeeEmploymentRules.Today }, tx)) return false;
        if (await db.ExecuteScalarAsync<bool>("""
SELECT EXISTS(SELECT 1 FROM employee_it0008_basic_pay WHERE EmployeeId=@Id AND ClientId=@ClientId
 AND (AnnualCtc<>0 OR COALESCE(TRIM(SalaryStructureId),'')<>''))
 OR EXISTS(SELECT 1 FROM payrunemployees WHERE EmployeeId=@Id)
""", employee, tx)) return false;
        var hasComponentHistory = await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM employee_it0008_basic_pay WHERE EmployeeId=@Id AND ClientId=@ClientId AND JSON_LENGTH(COALESCE(SalaryJson,JSON_OBJECT()))>0)", employee, tx);
        if (!ApplyRecruitmentSalarySeed(employee, inputs, annualCtc, hasComponentHistory)) return false;
        await SaveWithOpenConnectionAsync(db, employee, "Automatic recruitment salary sync", "0008",
            annualCtc > 0 ? "Initialized missing annual CTC base from released remuneration terms; configured components preserved"
                : "Initialized empty monthly basic/HRA from released employee-list offer", tx,
            effectiveDate: EmployeeEmploymentRules.Today, synchronizeAttendancePolicies: false, salaryOnly: true);
        return true;
    }
    internal static Task<int> SaveRecruitmentEmployeeAsync(MySqlConnection db, MySqlTransaction tx, Employee employee, AuthUser user, string reference) =>
        SaveWithOpenConnectionAsync(db, employee, user.DisplayName, null, $"Joined from recruitment application {reference}", tx);
}
