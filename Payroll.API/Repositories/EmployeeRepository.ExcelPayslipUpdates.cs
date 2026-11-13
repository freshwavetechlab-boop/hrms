using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class EmployeeRepository
{
    private static readonly HashSet<string> ExcelMasterPersonalKeys = new(StringComparer.Ordinal)
        { "employeeName", "gender", "dateOfBirth", "mobile", "pan", "aadhaar", "uan", "esic" };
    private static readonly HashSet<string> ExcelMasterAddressKeys = new(StringComparer.Ordinal)
        { "address", "correspondenceAddress", "permanentAddress", "city", "district", "state" };
    private static readonly HashSet<string> ExcelMasterBankKeys = new(StringComparer.Ordinal)
        { "bankName", "bankAccount", "ifsc" };
    private static readonly HashSet<string> ExcelMasterOrgKeys = new(StringComparer.Ordinal)
        { "email", "department", "designation", "grade", "workLocation" };

    // These helpers deliberately never own a connection/commit, create a Master,
    // provision ESS credentials, or queue welcome/other mail.
    internal static async Task<Dictionary<string, string>> ReadReviewedExcelMasterFieldsAsync(
        MySqlConnection db, MySqlTransaction tx, int employeeId, int clientId)
    {
        var employee = await ReadExcelMasterTargetAsync(db, tx, employeeId, clientId);
        return await ExcelMasterFieldValuesAsync(db, tx, employee);
    }

    internal static async Task<Dictionary<int, Dictionary<string, string>>> ReadReviewedExcelMasterFieldsBatchAsync(
        MySqlConnection db, MySqlTransaction tx, IReadOnlyCollection<int> employeeIds, int clientId)
    {
        if (clientId <= 0 || db.State != ConnectionState.Open || !ReferenceEquals(tx.Connection, db)
            || employeeIds.Count > 1000 || employeeIds.Any(id => id <= 0))
            throw new InvalidOperationException("The reviewed Master catalog requires one client and the caller's transaction.");
        var ids = employeeIds.Distinct().ToArray();
        if (ids.Length == 0) return [];
        var employees = (await db.QueryAsync<Employee>("SELECT * FROM employees WHERE ClientId=@ClientId AND Id IN @Ids",
            new { ClientId = clientId, Ids = ids }, tx)).ToArray();
        if (employees.Length != ids.Length) throw new InvalidOperationException("A matched Master employee is missing or belongs to another client.");
        await PayrollDataTableStore.ApplyEmployeeTablesAsync(db, employees, tx);
        var locations = (await db.QueryAsync<(int Id, string Name)>("SELECT Id,Name FROM worklocations WHERE ClientId=@ClientId",
            new { ClientId = clientId }, tx)).ToDictionary(row => row.Id, row => row.Name);
        var structureIds = employees.Select(employee => long.TryParse(employee.SalaryStructureId, out var id) ? id : 0)
            .Where(id => id > 0).Distinct().ToArray();
        var rules = structureIds.Length == 0 ? [] : (await db.QueryAsync<ExcelMasterInputRule>(ExcelMasterInputRuleSql +
            " AND s.Id IN @StructureIds", new { ClientId = clientId, StructureIds = structureIds }, tx)).ToArray();
        return employees.ToDictionary(employee => employee.Id, employee =>
        {
            var values = ExcelMasterBaseValues(employee, locations.GetValueOrDefault(employee.WorkLocationId, ""));
            foreach (var input in EligibleExcelMasterInputs(employee, rules.Where(rule => rule.StructureId.ToString(CultureInfo.InvariantCulture) == employee.SalaryStructureId)))
                values["salaryInput:" + input.Key] = ExcelMasterNumber(input.Value);
            return values;
        });
    }

    internal static async Task<Dictionary<string, string>> ValidateReviewedExcelMasterFieldsAsync(
        MySqlConnection db, MySqlTransaction tx, int employeeId, int clientId, IReadOnlyDictionary<string, string> changes)
    {
        var employee = await ReadExcelMasterTargetAsync(db, tx, employeeId, clientId);
        return await ValidateExcelMasterChangesAsync(db, tx, employee, changes);
    }

    internal static async Task ApplyReviewedExcelMasterFieldsAsync(MySqlConnection db, MySqlTransaction tx,
        int employeeId, int clientId, IReadOnlyDictionary<string, string> changes, string actor)
    {
        var before = await ReadExcelMasterTargetAsync(db, tx, employeeId, clientId, lockRow: true);
        var normalized = await ValidateExcelMasterChangesAsync(db, tx, before, changes);
        var beforeValues = await ExcelMasterFieldValuesAsync(db, tx, before);
        normalized = normalized.Where(pair => beforeValues.GetValueOrDefault(pair.Key, "") != pair.Value)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        if (normalized.Count == 0) return;

        var employee = CloneEmployee(before);
        foreach (var (key, value) in normalized) SetExcelMasterField(employee, key, value);
        if (normalized.TryGetValue("workLocation", out var location))
            employee.WorkLocationId = await ResolveExcelMasterLocationAsync(db, tx, clientId, location);

        var columns = new List<string>();
        if (normalized.ContainsKey("employeeName")) columns.AddRange(["FirstName=@FirstName", "LastName=@LastName"]);
        if (normalized.ContainsKey("gender")) columns.Add("Gender=@Gender");
        if (normalized.ContainsKey("dateOfJoining")) columns.Add("DateOfJoining=@DateOfJoining");
        if (normalized.ContainsKey("email")) columns.Add("WorkEmail=@WorkEmail");
        if (normalized.ContainsKey("department")) columns.Add("Department=@Department");
        if (normalized.ContainsKey("designation")) columns.Add("Designation=@Designation");
        if (normalized.ContainsKey("grade")) columns.Add("Grade=@Grade");
        if (normalized.ContainsKey("workLocation")) columns.Add("WorkLocationId=@WorkLocationId");
        if (normalized.ContainsKey("annualCtc")) columns.Add("AnnualCtc=@AnnualCtc");

        var personal = normalized.Keys.Any(key => ExcelMasterPersonalKeys.Contains(key) || ExcelMasterAddressKeys.Contains(key));
        if (personal)
        {
            await PayrollDataTableStore.SyncEmployeePersonalOnlyAsync(db, employee, tx);
            // Personal-only persistence retains opaque/custom JSON; update only
            // the address aliases explicitly accepted in this review.
            var json = JsonNode.Parse(employee.PersonalJson)?.AsObject() ?? new JsonObject();
            foreach (var key in normalized.Keys.Where(ExcelMasterAddressKeys.Contains)) json[key] = normalized[key];
            employee.PersonalJson = json.ToJsonString();
            columns.Add("PersonalJson=@PersonalJson");
        }
        if (normalized.Keys.Any(ExcelMasterBankKeys.Contains))
        {
            await PayrollDataTableStore.SyncEmployeePaymentOnlyAsync(db, employee, tx);
            columns.Add("PaymentJson=@PaymentJson");
        }
        if (normalized.Keys.Any(key => key.StartsWith("salaryInput:", StringComparison.Ordinal)))
        {
            await PayrollDataTableStore.SyncEmployeeSalaryOnlyAsync(db, employee, tx);
            columns.Add("SalaryJson=@SalaryJson");
        }
        if (columns.Count > 0)
            await db.ExecuteAsync("UPDATE employees SET " + string.Join(',', columns) + " WHERE Id=@Id AND ClientId=@ClientId", employee, tx);

        var effective = EmployeeEmploymentRules.Today;
        var groups = normalized.Keys.Select(ExcelMasterInfotype).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var group in groups)
            await WritePhysicalInfotypeAsync(db, employee, group, "Master Update", effective,
                "Reviewed existing Excel payslip update", actor, tx);

        // Audit EVERY reviewed field, including UAN/phone/email, which the older
        // generic infotype audit does not enumerate. No unreviewed field is logged
        // as changed and no sensitive values are written to application logs.
        await db.ExecuteAsync(@"INSERT INTO employee_audit_trail
(EmployeeId,EmployeeCode,ActionType,InfotypeCode,FieldName,OldValue,NewValue,EffectiveFrom,ChangedBy)
VALUES(@Id,@EmployeeCode,'Master Update',@Infotype,@Field,@OldValue,@NewValue,@Effective,@Actor)",
            normalized.Select(pair => new { employee.Id, employee.EmployeeCode, Infotype = ExcelMasterInfotype(pair.Key),
                Field = "Excel update: " + pair.Key, OldValue = beforeValues.GetValueOrDefault(pair.Key, ""), NewValue = pair.Value,
                Effective = effective, Actor = actor.Length <= 190 ? actor : actor[..190] }), tx);

        if (normalized.Keys.Any(key => key is "department" or "designation" or "workLocation"))
            await SyncAttendancePolicyMappingsAsync(db, employee, before, tx);
    }

    private static async Task<Employee> ReadExcelMasterTargetAsync(MySqlConnection db, MySqlTransaction tx,
        int employeeId, int clientId, bool lockRow = false)
    {
        if (employeeId <= 0 || clientId <= 0 || db.State != ConnectionState.Open || !ReferenceEquals(tx.Connection, db))
            throw new InvalidOperationException("The reviewed Master update requires the caller's existing client transaction.");
        var owner = await db.ExecuteScalarAsync<int?>("SELECT ClientId FROM employees WHERE Id=@Id" + (lockRow ? " FOR UPDATE" : ""),
            new { Id = employeeId }, tx);
        if (owner != clientId) throw new InvalidOperationException("The matched employee does not belong to this client.");
        return await LoadEmployeeAsync(db, employeeId, tx) ?? throw new InvalidOperationException("The matched employee no longer exists.");
    }

    private static async Task<Dictionary<string, string>> ExcelMasterFieldValuesAsync(MySqlConnection db, MySqlTransaction tx, Employee employee)
    {
        var location = employee.WorkLocationId <= 0 ? "" : await db.ExecuteScalarAsync<string?>(
            "SELECT Name FROM worklocations WHERE Id=@Id AND ClientId=@ClientId", new { Id = employee.WorkLocationId, employee.ClientId }, tx) ?? "";
        var values = ExcelMasterBaseValues(employee, location);
        foreach (var input in await ExcelMasterInputComponentsAsync(db, tx, employee))
            values["salaryInput:" + input.Key] = ExcelMasterNumber(input.Value);
        return values;
    }

    private static Dictionary<string, string> ExcelMasterBaseValues(Employee employee, string location)
    {
        var p = employee.PersonalDetails; var b = employee.PaymentDetails;
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["employeeName"] = (employee.FirstName + " " + employee.LastName).Trim(), ["email"] = employee.WorkEmail,
            ["gender"] = employee.Gender, ["dateOfJoining"] = employee.DateOfJoining,
            ["department"] = employee.Department, ["designation"] = employee.Designation, ["grade"] = employee.Grade,
            ["annualCtc"] = ExcelMasterNumber(employee.AnnualCtc),
            ["dateOfBirth"] = p.DateOfBirth, ["mobile"] = p.Mobile, ["pan"] = p.PanNumber, ["aadhaar"] = p.AadhaarNumber,
            ["uan"] = p.UanNumber, ["esic"] = p.EsicNumber, ["address"] = p.Address, ["correspondenceAddress"] = p.CorrespondenceAddress,
            ["permanentAddress"] = p.PermanentAddress, ["city"] = p.City, ["district"] = p.District, ["state"] = p.State,
            ["bankName"] = b.BankName, ["bankAccount"] = b.BankAccountNo, ["ifsc"] = b.IfscCode, ["workLocation"] = location
        };
        return values.ToDictionary(pair => pair.Key, pair => pair.Value ?? "", StringComparer.Ordinal);
    }

    private static async Task<Dictionary<string, string>> ValidateExcelMasterChangesAsync(MySqlConnection db, MySqlTransaction tx,
        Employee employee, IReadOnlyDictionary<string, string> changes)
    {
        if (changes is null || changes.Count > 100) throw new InvalidOperationException("Review a supported set of Master fields.");
        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        var allowedInputs = await ExcelMasterInputComponentsAsync(db, tx, employee);
        var current = await ExcelMasterFieldValuesAsync(db, tx, employee);
        foreach (var (key, raw) in changes)
        {
            var value = (raw ?? "").Trim();
            if (value.Length == 0) continue; // An empty Excel cell never clears a Master field.
            if (current.TryGetValue(key, out var unchanged) && value == unchanged) continue;
            if (key.StartsWith("salaryInput:", StringComparison.Ordinal))
            {
                var input = key[12..];
                if (!allowedInputs.ContainsKey(input)) throw new InvalidOperationException("This salary input has no existing configured input mapping. Use Configure payroll.");
                normalized[key] = ExcelMasterMoney(value, 4, "Salary input");
                continue;
            }
            if (!ExcelMasterPersonalKeys.Contains(key) && !ExcelMasterAddressKeys.Contains(key) && !ExcelMasterBankKeys.Contains(key)
                && !ExcelMasterOrgKeys.Contains(key) && key is not ("dateOfJoining" or "annualCtc"))
                throw new InvalidOperationException("This Master field is not supported by the reviewed Excel update.");
            var multiline = key is "address" or "correspondenceAddress" or "permanentAddress";
            if (value.Any(ch => char.IsControl(ch) && !(multiline && ch is '\r' or '\n')))
                throw new InvalidOperationException("Master values must not contain control characters.");
            var maximum = ExcelMasterAddressKeys.Contains(key) && key.EndsWith("Address", StringComparison.OrdinalIgnoreCase) ? 800
                : key == "address" ? 800 : key == "employeeName" ? 160 : key == "email" ? 150 : 100;
            if (value.Length > maximum) throw new InvalidOperationException($"{key} exceeds its supported length.");
            switch (key)
            {
                case "employeeName":
                    value = Regex.Replace(value, @"\s+", " ");
                    var parts = value.Split(' ', 2);
                    if (parts[0].Length > 100 || parts.ElementAtOrDefault(1)?.Length > 100)
                        throw new InvalidOperationException("Employee name exceeds the existing Master field lengths.");
                    break;
                case "email":
                    if (!ExcelPayslipRepository.TryEmail(value, out value)) throw new InvalidOperationException("Enter one valid employee email address.");
                    break;
                case "mobile":
                    value = AuthRepository.NormalizeEmployeeLoginPhone(value);
                    if (value.Length == 0) throw new InvalidOperationException("Enter a valid Indian mobile number, optionally with +91.");
                    break;
                case "pan":
                    value = Regex.Replace(value, @"\s+", "").ToUpperInvariant();
                    if (!Regex.IsMatch(value, @"\A[A-Z]{5}[0-9]{4}[A-Z]\z")) throw new InvalidOperationException("PAN must contain five letters, four digits and one letter.");
                    break;
                case "aadhaar": case "uan": case "esic":
                    value = Regex.Replace(value, @"[\s-]+", "");
                    var pattern = key == "aadhaar" ? @"\A[2-9][0-9]{11}\z" : key == "uan" ? @"\A[0-9]{12}\z" : @"\A[0-9]{10}\z";
                    if (!Regex.IsMatch(value, pattern) || value.All(ch => ch == value[0])) throw new InvalidOperationException($"Enter a valid {key} identifier as text, with every digit retained.");
                    break;
                case "bankAccount":
                    value = Regex.Replace(value, @"\s+", "");
                    if (!Regex.IsMatch(value, @"\A[0-9]{6,34}\z")) throw new InvalidOperationException("Bank account must contain all 6–34 digits as text, without decimals or exponent notation.");
                    break;
                case "ifsc":
                    value = value.ToUpperInvariant();
                    if (!Regex.IsMatch(value, @"\A[A-Z]{4}0[A-Z0-9]{6}\z")) throw new InvalidOperationException("Enter a valid 11-character IFSC code.");
                    break;
                case "gender":
                    value = new[] { "Male", "Female", "Other" }.FirstOrDefault(item => item.Equals(value, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException("Gender must be Male, Female or Other.");
                    break;
                case "dateOfBirth": case "dateOfJoining":
                    if (!DateTime.TryParseExact(value, new[] { "yyyy-MM-dd", "dd-MM-yyyy", "dd/MM/yyyy", "dd.MM.yyyy", "d/M/yyyy", "d.M.yyyy" },
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) || key == "dateOfBirth" && date.Date > EmployeeEmploymentRules.Today || date.Year < 1900 || date.Year > 2199)
                        throw new InvalidOperationException("Use a valid date in YYYY-MM-DD or day/month/year format. Birth dates cannot be in the future.");
                    value = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    break;
                case "annualCtc": value = ExcelMasterMoney(value, 2, "Annual CTC"); break;
                case "department": case "designation": case "grade":
                    var type = key == "grade" ? "Employee Grade" : char.ToUpperInvariant(key[0]) + key[1..];
                    var matches = (await db.QueryAsync<string>(@"SELECT DISTINCT Value FROM dropdownmasters
WHERE ClientId IN (0,@ClientId) AND Type=@Type AND IsActive=TRUE AND LOWER(TRIM(Value))=LOWER(@Value)",
                        new { employee.ClientId, Type = type, Value = value }, tx)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    if (matches.Length != 1) throw new InvalidOperationException($"Select an existing active {type} for this client; new Master options are not created by this update.");
                    value = matches[0];
                    break;
                case "workLocation":
                    var locationId = await ResolveExcelMasterLocationAsync(db, tx, employee.ClientId, value);
                    value = await db.ExecuteScalarAsync<string>("SELECT Name FROM worklocations WHERE Id=@Id AND ClientId=@ClientId",
                        new { Id = locationId, employee.ClientId }, tx) ?? throw new InvalidOperationException("The reviewed work location no longer exists.");
                    break;
            }
            normalized[key] = value;
        }
        var candidate = CloneEmployee(employee);
        foreach (var pair in normalized) SetExcelMasterField(candidate, pair.Key, pair.Value);
        if (DateTime.TryParse(candidate.PersonalDetails.DateOfBirth, out var birth)
            && DateTime.TryParse(candidate.DateOfJoining, out var joining) && birth.Date > joining.Date
            && normalized.Keys.Any(key => key is "dateOfBirth" or "dateOfJoining"))
            throw new InvalidOperationException("Date of birth must be before the joining date.");

        await ValidateExcelMasterIdentifierOwnersAsync(db, tx, employee, normalized);
        if (normalized.Keys.Any(key => key == "annualCtc" || key.StartsWith("salaryInput:", StringComparison.Ordinal)))
        {
            // Current/future core payroll must be resolved using its existing
            // correction flow. A monthly payslip is never a salary-revision bypass.
            var overlaps = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM payruns r JOIN payrunemployees e ON e.PayRunId=r.Id
WHERE r.ClientId=@ClientId AND e.EmployeeId=@Id AND r.PayPeriod>=@Month
AND r.Status IN ('Draft','Queued','Processing','Pending Approval','Approved','Partially Paid','Paid')",
                new { employee.ClientId, employee.Id, Month = EmployeeEmploymentRules.Today.ToString("yyyy-MM", CultureInfo.InvariantCulture) }, tx);
            if (overlaps > 0) throw new InvalidOperationException("Resolve this employee's existing core payroll before changing Master salary inputs.");
        }
        return normalized;
    }

    private static async Task ValidateExcelMasterIdentifierOwnersAsync(MySqlConnection db, MySqlTransaction tx,
        Employee employee, IReadOnlyDictionary<string, string> changes)
    {
        var identifiers = new[] { "email", "mobile", "pan", "aadhaar", "uan", "esic", "bankAccount" }.Where(changes.ContainsKey).ToArray();
        if (identifiers.Length == 0) return;
        var others = (await db.QueryAsync<ExcelMasterIdentityOwner>(@"SELECT e.Id,e.WorkEmail Email,
COALESCE(p.Mobile,'') Mobile,COALESCE(p.PanNumber,'') Pan,COALESCE(p.AadhaarNumber,'') Aadhaar,
COALESCE(p.UanNumber,'') Uan,COALESCE(p.EsicNumber,'') Esic,COALESCE(b.BankAccountNo,'') BankAccount
FROM employees e LEFT JOIN employeepersonaldetails p ON p.EmployeeId=e.Id
LEFT JOIN employeepaymentdetails b ON b.EmployeeId=e.Id WHERE e.ClientId=@ClientId AND e.Id<>@Id",
            new { employee.ClientId, employee.Id }, tx)).ToArray();
        foreach (var key in identifiers)
        {
            var value = ExcelMasterIdentityValue(key, changes[key]);
            if (others.Any(other => ExcelMasterIdentityValue(key, other.Value(key)) == value))
                throw new InvalidOperationException($"The proposed {key} already belongs to another employee in this client. Review the employee identity first.");
        }
    }

    private static string ExcelMasterIdentityValue(string key, string? value) => key == "mobile"
        ? AuthRepository.NormalizeEmployeeLoginPhone(value) : Regex.Replace((value ?? "").Trim(), key is "aadhaar" or "uan" or "esic" ? @"[\s-]+" : @"\s+", "").ToUpperInvariant();

    private static async Task<int> ResolveExcelMasterLocationAsync(MySqlConnection db, MySqlTransaction tx, int clientId, string name)
    {
        var matches = (await db.QueryAsync<int>(@"SELECT Id FROM worklocations WHERE ClientId=@ClientId AND IsActive=TRUE
AND LOWER(TRIM(Name))=LOWER(@Name)", new { ClientId = clientId, Name = name.Trim() }, tx)).ToArray();
        return matches.Length == 1 ? matches[0] : throw new InvalidOperationException("Work location must match exactly one existing active location in this client.");
    }

    private const string ExcelMasterInputRuleSql = @"SELECT s.Id StructureId,CAST(c.Id AS CHAR) ComponentKey,
COALESCE(NULLIF(l.CalculationType,''),c.CalculationType) CalculationType,
COALESCE(NULLIF(l.Formula,''),c.Formula) Formula,c.Code,c.Category
FROM salarystructures s JOIN salarystructurelines l ON l.StructureId=s.Id
JOIN salarycomponents c ON l.ComponentId=CAST(c.Id AS CHAR) OR l.ComponentId=c.Code
WHERE s.ClientId IN (0,@ClientId) AND s.Active=TRUE AND c.Active=TRUE";

    private static async Task<Dictionary<string, decimal>> ExcelMasterInputComponentsAsync(MySqlConnection db, MySqlTransaction tx, Employee employee)
    {
        if (employee.SalaryComponents.Count == 0 || !long.TryParse(employee.SalaryStructureId, out var structureId)) return [];
        var rows = (await db.QueryAsync<ExcelMasterInputRule>(ExcelMasterInputRuleSql + " AND s.Id=@StructureId",
            new { StructureId = structureId, employee.ClientId }, tx)).ToArray();
        return EligibleExcelMasterInputs(employee, rows);
    }

    private static Dictionary<string, decimal> EligibleExcelMasterInputs(Employee employee, IEnumerable<ExcelMasterInputRule> rows) =>
        rows.GroupBy(row => row.ComponentKey, StringComparer.Ordinal).Where(group => group.Count() == 1).Select(group => group.Single())
            .Where(row => employee.SalaryComponents.ContainsKey(row.ComponentKey) && string.IsNullOrWhiteSpace(row.Formula)
                && row.CalculationType == "Fixed Amount"
                && !new[] { "CTC", "GROSS", "NET", "NET_PAY", "TOTAL", "TOTAL_DEDUCTIONS" }.Contains(row.Code.ToUpperInvariant()))
            .ToDictionary(row => row.ComponentKey, row => employee.SalaryComponents[row.ComponentKey], StringComparer.Ordinal);

    private static string ExcelMasterMoney(string value, int precision, string label)
    {
        if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) || number < 0
            || number > (precision == 4 ? 99999999999999.9999m : 9999999999999999.99m)
            || decimal.Round(number, precision, MidpointRounding.ToEven) != number)
            throw new InvalidOperationException($"{label} must be a non-negative number with at most {precision} decimal places.");
        return ExcelMasterNumber(number);
    }

    private static string ExcelMasterNumber(decimal number) => number.ToString("0.############################", CultureInfo.InvariantCulture);

    private static void SetExcelMasterField(Employee employee, string key, string value)
    {
        var p = employee.PersonalDetails; var b = employee.PaymentDetails;
        switch (key)
        {
            case "employeeName": var names = value.Split(' ', 2); employee.FirstName = names[0]; employee.LastName = names.ElementAtOrDefault(1) ?? ""; break;
            case "email": employee.WorkEmail = value; break;
            case "gender": employee.Gender = value; break;
            case "dateOfJoining": employee.DateOfJoining = value; break;
            case "department": employee.Department = value; break;
            case "designation": employee.Designation = value; break;
            case "grade": employee.Grade = value; break;
            case "dateOfBirth": p.DateOfBirth = value; break;
            case "mobile": p.Mobile = value; break;
            case "pan": p.PanNumber = value; break;
            case "aadhaar": p.AadhaarNumber = value; break;
            case "uan": p.UanNumber = value; break;
            case "esic": p.EsicNumber = value; break;
            case "bankName": b.BankName = value; break;
            case "bankAccount": b.BankAccountNo = value; break;
            case "ifsc": b.IfscCode = value; break;
            case "address": p.Address = value; break;
            case "correspondenceAddress": p.CorrespondenceAddress = value; break;
            case "permanentAddress": p.PermanentAddress = value; break;
            case "city": p.City = value; break;
            case "district": p.District = value; break;
            case "state": p.State = value; break;
            case "annualCtc": employee.AnnualCtc = decimal.Parse(value, CultureInfo.InvariantCulture); break;
            default:
                if (key.StartsWith("salaryInput:", StringComparison.Ordinal)) employee.SalaryComponents[key[12..]] = decimal.Parse(value, CultureInfo.InvariantCulture);
                break;
        }
    }

    private static string ExcelMasterInfotype(string key) => ExcelMasterPersonalKeys.Contains(key) ? "0002"
        : ExcelMasterAddressKeys.Contains(key) ? "0006" : ExcelMasterBankKeys.Contains(key) ? "0009"
        : key == "dateOfJoining" ? "0000" : key == "annualCtc" || key.StartsWith("salaryInput:", StringComparison.Ordinal) ? "0008" : "0001";

    private sealed class ExcelMasterIdentityOwner
    {
        public int Id { get; set; }
        public string Email { get; set; } = "";
        public string Mobile { get; set; } = "";
        public string Pan { get; set; } = "";
        public string Aadhaar { get; set; } = "";
        public string Uan { get; set; } = "";
        public string Esic { get; set; } = "";
        public string BankAccount { get; set; } = "";
        public string Value(string key) => key switch { "email" => Email, "mobile" => Mobile, "pan" => Pan, "aadhaar" => Aadhaar, "uan" => Uan, "esic" => Esic, "bankAccount" => BankAccount, _ => "" };
    }
    private sealed class ExcelMasterInputRule
    {
        public long StructureId { get; set; }
        public string ComponentKey { get; set; } = "";
        public string CalculationType { get; set; } = "";
        public string Formula { get; set; } = "";
        public string Code { get; set; } = "";
        public string Category { get; set; } = "";
    }
}
