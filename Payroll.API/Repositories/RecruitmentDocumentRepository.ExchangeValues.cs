using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class RecruitmentDocumentRepository
{
    internal static Dictionary<string, string> NormalizeStandardRosterValues(IReadOnlyDictionary<string, string> supplied, IReadOnlyList<RosterExchangeColumn> columns)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in columns)
        {
            var value = supplied.GetValueOrDefault(column.Key, "").Trim();
            if (value.Length == 0) continue;
            if (value.Length > 4000) throw new InvalidOperationException($"{column.Header}: value exceeds 4000 characters.");
            if (column.Type == "date")
            {
                if (!EmployeeImportDate.TryNormalize(value, out value)) throw new InvalidOperationException($"{column.Header}: enter a valid date.");
            }
            else if (column.Type is "money" or "amount")
            {
                var precision = column.Type == "amount" ? 4 : 2;
                if (!decimal.TryParse(value.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var amount)
                    || Math.Abs(amount) > 999999999999.99m || decimal.Round(amount, precision) != amount
                    || column.Type == "money" && amount < 0)
                    throw new InvalidOperationException($"{column.Header}: enter a valid amount with at most {precision} decimal places.");
                value = amount.ToString(CultureInfo.InvariantCulture);
            }
            else if (column.Type == "integer" && (!int.TryParse(value, out var id) || id < 0))
                throw new InvalidOperationException($"{column.Header}: enter a valid nonnegative ID.");
            result[column.Key] = value;
        }
        if (result.TryGetValue("rowAction", out var action) && !new[] { "Apply", "Skip" }.Contains(action, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Row Action must be Apply or Skip.");
        if (result.TryGetValue("candidateEmail", out var email) && (email.Length > 190 || !Regex.IsMatch(email, @"^[^\s@<>;,]+@[^\s@<>;,]+\.[^\s@<>;,]+$")))
            throw new InvalidOperationException("Enter a valid Work Email.");
        if (result.TryGetValue("gender", out var gender) && !new[] { "Male", "Female", "Other" }.Contains(gender, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Gender must be Male, Female or Other.");
        foreach (var key in new[] { "firstName", "lastName" })
            if (result.GetValueOrDefault(key, "").Length > 120) throw new InvalidOperationException("First/last name must not exceed 120 characters.");
        if (result.GetValueOrDefault("bankAccountNo", "").Length > 60) throw new InvalidOperationException("Bank account number is too long.");
        return result;
    }

    internal static Employee StandardRosterEmployee(Employee? current, int clientId, IReadOnlyDictionary<string, string> values, IReadOnlyDictionary<string, string> componentIds)
    {
        var employee = current is null ? new Employee { ClientId = clientId } : JsonSerializer.Deserialize<Employee>(JsonSerializer.Serialize(current, Json), Json)!;
        if (employee.ClientId != clientId) throw new UnauthorizedAccessException("The employee is outside this client.");
        void Text(string key, Action<string> set) { if (values.TryGetValue(key, out var value) && value.Length > 0) set(value); }
        Text("employeeCode", v => employee.EmployeeCode = v); Text("firstName", v => employee.FirstName = v); Text("lastName", v => employee.LastName = v);
        Text("gender", v => employee.Gender = v); Text("candidateEmail", v => employee.WorkEmail = v); Text("joiningDate", v => employee.DateOfJoining = v);
        Text("department", v => employee.Department = v); Text("positionTitle", v => employee.Designation = v); Text("grade", v => employee.Grade = v);
        Text("reportingManagerUserId", v => employee.ReportingManagerUserId = int.Parse(v, CultureInfo.InvariantCulture) is var id && id > 0 ? id : null);
        Text("salaryStructureId", v => employee.SalaryStructureId = v); Text("annualCtc", v => employee.AnnualCtc = decimal.Parse(v, CultureInfo.InvariantCulture));
        var personal = employee.PersonalDetails; var payment = employee.PaymentDetails;
        Text("candidatePhone", v => personal.Mobile = v); Text("dateOfBirth", v => personal.DateOfBirth = v); Text("employmentType", v => personal.EmploymentType = v);
        Text("skillCategory", v => personal.SkillCategory = v); Text("panNumber", v => personal.PanNumber = v); Text("aadhaarNumber", v => personal.AadhaarNumber = v);
        Text("uanNumber", v => personal.UanNumber = v); Text("esicNumber", v => personal.EsicNumber = v); Text("address", v => personal.Address = v);
        Text("correspondenceAddress", v => personal.CorrespondenceAddress = v); Text("permanentAddress", v => personal.PermanentAddress = v);
        Text("city", v => personal.City = v); Text("district", v => personal.District = v); Text("state", v => personal.State = v);
        Text("bankName", v => payment.BankName = v); Text("bankAccountNo", v => payment.BankAccountNo = v); Text("ifscCode", v => payment.IfscCode = v);
        Text("paymentMode", v => payment.PaymentMode = v);
        foreach (var (key, componentId) in componentIds)
            if (values.TryGetValue(key, out var amount) && amount.Length > 0) employee.SalaryComponents[componentId] = decimal.Parse(amount, CultureInfo.InvariantCulture);
        employee.SalaryJson = JsonSerializer.Serialize(employee.SalaryComponents, Json);
        employee.PersonalJson = JsonSerializer.Serialize(personal, Json); employee.PaymentJson = JsonSerializer.Serialize(payment, Json);
        return employee;
    }

    // Pending recruitment profiles have no master yet. Build their baseline from
    // the current exported values so a blank correction retains every other field.
    internal static Employee StandardRosterCorrectionEmployee(Employee? current, int clientId, IReadOnlyDictionary<string, string> baseline,
        IReadOnlyDictionary<string, string> supplied, IReadOnlyDictionary<string, string> componentIds)
        => StandardRosterEmployee(current ?? StandardRosterEmployee(null, clientId, baseline, componentIds), clientId, supplied, componentIds);

    internal static string StandardRosterMatchIssue(string mode, RosterExchangePreviewRow row, IReadOnlyList<RecruitmentDocumentRosterRow> current, IReadOnlySet<string> duplicateCodes)
    {
        if (row.Values.GetValueOrDefault("rowAction", "").Equals("Skip", StringComparison.OrdinalIgnoreCase)) return "Marked Skip in the workbook.";
        if (duplicateCodes.Contains(row.ProfileCode)) return "Profile Code appears more than once; all duplicate rows are skipped.";
        if (mode == "Update" && MatchStandardRosterProfile(row.ProfileCode, current) is null) return "Unknown Profile Code in this client/batch. Skip this row; other updates can continue.";
        if (mode == "Insert" && row.ProfileCode.Length > 0) return "Insertion generates a new Profile Code. Use Update Corrected Version for existing codes.";
        return "";
    }

    internal static RecruitmentDocumentRosterRow? MatchStandardRosterProfile(string code, IReadOnlyList<RecruitmentDocumentRosterRow> rows)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var matches = rows.Where(row => row.CandidateCode.Equals(code.Trim(), StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    internal static bool StandardRosterCellEquals(string left, string right, string type)
        => type is "money" or "amount" && decimal.TryParse(left.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var a)
            && decimal.TryParse(right.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var b) ? a == b : left == right;
}
