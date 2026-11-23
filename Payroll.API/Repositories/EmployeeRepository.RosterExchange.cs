using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public partial class EmployeeRepository
{
    internal static List<EmployeeImportPreflightRow> PreviewRosterMasterCorrections(IReadOnlyList<Employee> proposed, IReadOnlyList<Employee> current)
    {
        var workbook = RecruitmentIdentityWorkbook(proposed);
        workbook.Sheets["Employees"][0].Insert(0, "Employee ID");
        for (var i = 0; i < proposed.Count; i++) workbook.Sheets["Employees"][i + 1].Insert(0, proposed[i].Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return BuildImportIdentityPreflightRows(workbook, UpdateImportMode, current, includeFieldChanges: false, allowEmployeeCodeCorrection: true);
    }

    internal static async Task SaveRosterMasterCorrectionAsync(MySqlConnection db, MySqlTransaction tx, Employee original, Employee proposed, AuthUser user, string reason)
    {
        if (original.Id <= 0 || original.ClientId != proposed.ClientId || original.Id != proposed.Id)
            throw new InvalidOperationException("The corrected version must target the same existing employee and client.");
        if (!original.IsActive || await db.ExecuteScalarAsync<bool>("SELECT COALESCE(" + EmployeeEmploymentRules.LatestAction("e", "@Today") + ",'Rehire') IN ('Resign','Retire','Terminate') FROM employees e WHERE e.Id=@Id AND e.ClientId=@ClientId", new { original.Id, original.ClientId, Today = EmployeeEmploymentRules.Today }, tx))
            throw new InvalidOperationException("This employee has left. Use the dated employment action before changing the active master.");
        var actor = string.IsNullOrWhiteSpace(user.DisplayName) ? user.Email : user.DisplayName;
        var current = original;
        var personal = PersonalInfotypeState(current, proposed);
        personal.DateOfJoining = current.DateOfJoining; // Joining-date history belongs to IT0000 below.
        if (JsonSerializer.Serialize(PersonalInfotypeState(current, current)) != JsonSerializer.Serialize(personal))
        {
            await SaveWithOpenConnectionAsync(db, personal, actor, "0002", reason, tx, effectiveDate: EmployeeEmploymentRules.Today, synchronizeAttendancePolicies: false, personalOnly: true);
            current = await LoadEmployeeAsync(db, original.Id, tx) ?? throw new InvalidOperationException("Employee no longer exists.");
        }
        var bank = BankInfotypeState(current, proposed);
        if (JsonSerializer.Serialize(current.PaymentDetails) != JsonSerializer.Serialize(bank.PaymentDetails))
        {
            await SaveWithOpenConnectionAsync(db, bank, actor, "0009", reason, tx, effectiveDate: EmployeeEmploymentRules.Today, synchronizeAttendancePolicies: false, bankOnly: true);
            current = await LoadEmployeeAsync(db, original.Id, tx) ?? throw new InvalidOperationException("Employee no longer exists.");
        }
        if (current.AnnualCtc != proposed.AnnualCtc || current.SalaryStructureId != proposed.SalaryStructureId
            || !current.SalaryComponents.OrderBy(p => p.Key).SequenceEqual(proposed.SalaryComponents.OrderBy(p => p.Key)))
        {
            current.AnnualCtc = proposed.AnnualCtc; current.SalaryStructureId = proposed.SalaryStructureId;
            current.SalaryComponents = new(proposed.SalaryComponents); current.SalaryJson = JsonSerializer.Serialize(current.SalaryComponents);
            await SaveWithOpenConnectionAsync(db, current, actor, "0008", reason, tx, effectiveDate: EmployeeEmploymentRules.Today, synchronizeAttendancePolicies: false, salaryOnly: true);
            current = await LoadEmployeeAsync(db, original.Id, tx) ?? throw new InvalidOperationException("Employee no longer exists.");
        }
        var codes = new List<string>();
        if (current.DateOfJoining != proposed.DateOfJoining) codes.Add("0000");
        if (current.EmployeeCode != proposed.EmployeeCode || current.WorkEmail != proposed.WorkEmail || current.Department != proposed.Department || current.Designation != proposed.Designation
            || current.Grade != proposed.Grade || current.WorkLocationId != proposed.WorkLocationId || current.ReportingManagerUserId != proposed.ReportingManagerUserId
            || current.PersonalDetails.EmploymentType != proposed.PersonalDetails.EmploymentType || current.PersonalDetails.SkillCategory != proposed.PersonalDetails.SkillCategory) codes.Add("0001");
        var oldAddress = new[] { current.PersonalDetails.Address, current.PersonalDetails.CorrespondenceAddress, current.PersonalDetails.PermanentAddress, current.PersonalDetails.City, current.PersonalDetails.District, current.PersonalDetails.State };
        var newAddress = new[] { proposed.PersonalDetails.Address, proposed.PersonalDetails.CorrespondenceAddress, proposed.PersonalDetails.PermanentAddress, proposed.PersonalDetails.City, proposed.PersonalDetails.District, proposed.PersonalDetails.State };
        if (!oldAddress.SequenceEqual(newAddress)) codes.Add("0006");
        if (codes.Count == 0) return;
        current.EmployeeCode = proposed.EmployeeCode; current.WorkEmail = proposed.WorkEmail; current.DateOfJoining = proposed.DateOfJoining; current.Department = proposed.Department;
        current.Designation = proposed.Designation; current.Grade = proposed.Grade; current.WorkLocationId = proposed.WorkLocationId;
        current.ReportingManagerUserId = proposed.ReportingManagerUserId;
        current.PersonalDetails.EmploymentType = proposed.PersonalDetails.EmploymentType; current.PersonalDetails.SkillCategory = proposed.PersonalDetails.SkillCategory;
        current.PersonalDetails.Address = proposed.PersonalDetails.Address; current.PersonalDetails.CorrespondenceAddress = proposed.PersonalDetails.CorrespondenceAddress;
        current.PersonalDetails.PermanentAddress = proposed.PersonalDetails.PermanentAddress; current.PersonalDetails.City = proposed.PersonalDetails.City;
        current.PersonalDetails.District = proposed.PersonalDetails.District; current.PersonalDetails.State = proposed.PersonalDetails.State;
        await SaveWithOpenConnectionAsync(db, current, actor, string.Join(',', codes), reason, tx, effectiveDate: EmployeeEmploymentRules.Today, synchronizeAttendancePolicies: codes.Contains("0001"));
    }
}
