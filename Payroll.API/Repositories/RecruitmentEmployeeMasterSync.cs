using System.Globalization;
using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

// Current document inputs follow Employee Master. Issued PDFs, deliveries and
// acceptance receipts remain immutable and require an explicit new release/send.
internal static class RecruitmentEmployeeMasterSync
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string Key(string value) => string.Concat(value.Normalize().Where(char.IsLetterOrDigit)).ToUpperInvariant();
    private static string Email(string value) => value.Trim().ToLowerInvariant();

    internal static Employee? Resolve(RecruitmentDocumentRosterRow row, IReadOnlyList<Employee> employees)
    {
        var scoped = employees.Where(e => e.ClientId == row.ClientId).ToList();
        if (row.EmployeeId is > 0) return scoped.SingleOrDefault(e => e.Id == row.EmployeeId);
        var values = Values(row.ValuesJson);
        var code = values.GetValueOrDefault("employeeCode", "").Trim();
        if (code.Length > 0)
        {
            var codes = scoped.Where(e => e.EmployeeCode.Equals(code, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (codes.Length == 1) return codes[0];
            if (codes.Length > 1) return null;
        }
        var email = Email(row.Email);
        if (email.Length == 0) return null;
        // Legacy imports have no stored EmployeeId. A unique same-client email
        // plus matching name is required; ambiguous/shared contacts never link.
        var contacts = scoped.Where(e => Email(e.WorkEmail) == email).ToArray();
        return contacts.Length == 1 && Key(row.CandidateName).Length > 0
            && Key(row.CandidateName) == Key(contacts[0].FirstName + contacts[0].LastName) ? contacts[0] : null;
    }

    private static Dictionary<string, string> Values(string json) => new(
        JsonSerializer.Deserialize<Dictionary<string, string>>(json, Json) ?? [], StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, string> MasterValues(Employee employee) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["candidateName"] = (employee.FirstName + " " + employee.LastName).Trim(),
        ["candidateEmail"] = employee.WorkEmail, ["candidatePhone"] = employee.PersonalDetails.Mobile,
        ["employeeCode"] = employee.EmployeeCode, ["positionTitle"] = employee.Designation,
        ["department"] = employee.Department, ["joiningDate"] = employee.DateOfJoining,
        ["dateOfBirth"] = employee.PersonalDetails.DateOfBirth,
        ["candidateAddress"] = new[] { employee.PersonalDetails.CorrespondenceAddress, employee.PersonalDetails.Address, employee.PersonalDetails.PermanentAddress }.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "",
        ["pan"] = employee.PersonalDetails.PanNumber, ["panNumber"] = employee.PersonalDetails.PanNumber,
        ["aadhaar"] = employee.PersonalDetails.AadhaarNumber, ["aadhaarNumber"] = employee.PersonalDetails.AadhaarNumber,
        ["uan"] = employee.PersonalDetails.UanNumber, ["uanNumber"] = employee.PersonalDetails.UanNumber,
        ["esicNumber"] = employee.PersonalDetails.EsicNumber, ["bankName"] = employee.PaymentDetails.BankName,
        ["accountNo"] = employee.PaymentDetails.BankAccountNo, ["bankAccountNo"] = employee.PaymentDetails.BankAccountNo,
        ["ifsc"] = employee.PaymentDetails.IfscCode, ["ifscCode"] = employee.PaymentDetails.IfscCode,
        ["annualCtc"] = employee.AnnualCtc > 0 ? employee.AnnualCtc.ToString(CultureInfo.InvariantCulture) : ""
    };

    internal static void Apply(RecruitmentDocumentRosterRow row, Employee employee, IReadOnlySet<string>? configuredFields = null, IReadOnlyDictionary<string, string>? componentCodes = null, IReadOnlySet<string>? earningComponentIds = null)
    {
        if (employee.Id <= 0 || employee.ClientId != row.ClientId || row.EmployeeId is > 0 && row.EmployeeId != employee.Id) return;
        var values = Values(row.ValuesJson);
        var available = configuredFields ?? values.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var source = MasterValues(employee);
        if (componentCodes is not null)
        {
            var earningIds = earningComponentIds ?? componentCodes.Keys.ToHashSet();
            decimal Salary(string code) => employee.SalaryComponents.Where(pair => earningIds.Contains(pair.Key) && componentCodes.GetValueOrDefault(pair.Key, pair.Key).Equals(code, StringComparison.OrdinalIgnoreCase)).Sum(pair => pair.Value);
            var basic = Salary("BASIC"); var hra = Salary("HRA");
            if (basic > 0) source["monthlyBasic"] = basic.ToString("0.################", CultureInfo.InvariantCulture);
            if (basic > 0 && hra >= 0 && employee.SalaryComponents.Keys.All(componentCodes.ContainsKey))
            {
                source["monthlyGross"] = employee.SalaryComponents.Where(pair => earningIds.Contains(pair.Key)).Sum(pair => pair.Value).ToString("0.################", CultureInfo.InvariantCulture);
                source["hraRate"] = (hra / basic).ToString("0.################", CultureInfo.InvariantCulture);
            }
        }
        var master = source.Where(pair => (!string.IsNullOrWhiteSpace(pair.Value)
            || row.EmployeeId == employee.Id && pair.Key is "candidateEmail" or "candidatePhone")
            && (available.Contains(pair.Key) || pair.Key is "candidateName" or "candidateEmail" or "candidatePhone")).ToList();
        foreach (var (key, value) in master)
        {
            var old = values.GetValueOrDefault(key, "");
            decimal? Number(string text) => decimal.TryParse(text.TrimEnd('%').Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var n)
                ? key == "hraRate" && (text.EndsWith('%') || n > 1) ? n / 100m : n : null;
            if (key is "monthlyBasic" or "monthlyGross" or "hraRate" && Number(old) is { } prior && Number(value) == prior) continue;
            values[key] = value.Trim();
        }
        // Legacy layouts can bind words to a separate imported text field.
        // Keep that text with the current Master amount when the amount changes.
        if (master.Any(p => p.Key == "monthlyGross") && available.Contains("salaryInWords"))
        {
            var originalGross = Values(row.ValuesJson).GetValueOrDefault("monthlyGross", "");
            if (decimal.TryParse(values.GetValueOrDefault("monthlyGross"), NumberStyles.Number, CultureInfo.InvariantCulture, out var gross)
                && gross >= 0 && gross <= 999999999999m
                && (!decimal.TryParse(originalGross, NumberStyles.Number, CultureInfo.InvariantCulture, out var priorGross) || gross != priorGross))
            {
                var prefix = values.GetValueOrDefault("salaryInWords", "").TrimStart().StartsWith("Rupees ", StringComparison.OrdinalIgnoreCase) ? "Rupees " : "";
                values["salaryInWords"] = prefix + BrandedOfferPdfService.AmountInWords(gross);
            }
            master.Add(new("salaryInWords", values.GetValueOrDefault("salaryInWords", "")));
        }
        row.CandidateName = values.GetValueOrDefault("candidateName", row.CandidateName);
        row.Email = values.GetValueOrDefault("candidateEmail", row.Email);
        row.Phone = values.GetValueOrDefault("candidatePhone", row.Phone);
        if (employee.WorkLocationId > 0) row.WorkLocationId = employee.WorkLocationId;
        row.SyncedEmployeeId = employee.Id;
        row.EmployeeManagedFields = master.Select(pair => pair.Key).ToList();
        row.ValuesJson = JsonSerializer.Serialize(values.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(pair => pair.Key, pair => pair.Value), Json);
        // Monthly gross/HRA/offer-specific terms are not inferred from annual CTC.
    }

    internal static async Task ApplyAsync(MySqlConnection db, IReadOnlyList<RecruitmentDocumentRosterRow> rows, MySqlTransaction? tx = null)
    {
        foreach (var group in rows.GroupBy(row => row.ClientId))
        {
            var employees = await EmployeeRepository.ReadExcelPayrollIdentitySnapshotAsync(db, tx, group.Key);
            var components = (await db.QueryAsync<(string Id, string Code, string Category)>("SELECT CAST(Id AS CHAR),Code,Category FROM salarycomponents", transaction: tx)).ToList();
            var componentCodes = components.ToDictionary(pair => pair.Id, pair => pair.Code);
            var earningIds = components.Where(c => c.Category.Equals("Earning", StringComparison.OrdinalIgnoreCase)).Select(c => c.Id).ToHashSet();
            var templates = (await db.QueryAsync<RecruitmentTemplate>("SELECT Id,BodyTemplate FROM recruitment_templates WHERE Id IN @Ids AND ClientId IN (0,@ClientId)", new { Ids = group.Select(row => row.TemplateId).Distinct().ToArray(), ClientId = group.Key }, tx))
                .Where(t => LayoutDocumentPdfService.IsLayout(t.BodyTemplate)).ToDictionary(t => t.Id,
                    t => (IReadOnlySet<string>)LayoutDocumentPdfService.Parse(t.BodyTemplate).Inputs.Select(f => f.Key).ToHashSet(StringComparer.OrdinalIgnoreCase));
            foreach (var row in group)
                if (Resolve(row, employees) is { } employee) Apply(row, employee, templates.GetValueOrDefault(row.TemplateId), componentCodes, earningIds);
        }
    }

    internal static bool HasChanges(Employee before, Employee after) => before.EmployeeCode != after.EmployeeCode
        || before.FirstName != after.FirstName || before.LastName != after.LastName
        || before.WorkEmail != after.WorkEmail || before.PersonalDetails.Mobile != after.PersonalDetails.Mobile;

    internal static async Task EmployeeSavedAsync(MySqlConnection db, MySqlTransaction? tx, Employee? before, Employee after, string changedBy)
    {
        if (before is null || !HasChanges(before, after)) return;
        var columns = (await db.QueryAsync<(string TableName, string ColumnName)>("""
SELECT TABLE_NAME, COLUMN_NAME FROM information_schema.columns
WHERE TABLE_SCHEMA=DATABASE() AND ((TABLE_NAME='recruitment_candidates' AND COLUMN_NAME='EmployeeId')
 OR (TABLE_NAME='recruitment_document_roster' AND COLUMN_NAME='EmployeeId'))
""", transaction: tx)).ToList();
        if (!columns.Any(c => c.TableName == "recruitment_candidates")) return;
        var hasRoster = columns.Any(c => c.TableName == "recruitment_document_roster");
        var rows = hasRoster ? (await db.QueryAsync<RecruitmentDocumentRosterRow>("""
SELECT r.Id,r.ClientId,r.CandidateId,r.WorkLocationId,CAST(r.ValuesJson AS CHAR) ValuesJson,
 COALESCE(r.EmployeeId,c.EmployeeId) EmployeeId,TRIM(CONCAT_WS(' ',c.FirstName,c.LastName)) CandidateName,c.Email,c.Phone
FROM recruitment_document_roster r JOIN recruitment_candidates c ON c.Id=r.CandidateId AND c.ClientId=r.ClientId
WHERE r.ClientId=@ClientId AND (r.EmployeeId=@Id OR c.EmployeeId=@Id OR
 (r.EmployeeId IS NULL AND c.EmployeeId IS NULL AND (LOWER(TRIM(c.Email)) IN @Emails
  OR JSON_UNQUOTE(JSON_EXTRACT(r.ValuesJson,'$.employeeCode')) IN @Codes)))
ORDER BY r.Id FOR UPDATE
""", new { after.Id, after.ClientId, Emails = new[] { Email(before.WorkEmail), Email(after.WorkEmail) }.Where(e => e.Length > 0).DefaultIfEmpty("<none>").ToArray(), Codes = new[] { before.EmployeeCode, after.EmployeeCode }.Where(c => c.Length > 0).DefaultIfEmpty("<none>").ToArray() }, tx)).ToList() : [];
        if (rows.Count > 0)
        {
            var employees = await EmployeeRepository.ReadExcelPayrollIdentitySnapshotAsync(db, tx, after.ClientId);
            var previous = employees.Select(e => e.Id == before.Id ? before : e).ToList();
            rows = rows.Where(row => Resolve(row, previous)?.Id == after.Id || Resolve(row, employees)?.Id == after.Id).ToList();
            foreach (var row in rows)
            {
                // Persist the existing identity link so a later email/name change
                // cannot disconnect this profile. Never create or rehire staff.
                await db.ExecuteAsync("UPDATE recruitment_document_roster SET EmployeeId=@EmployeeId,UpdatedAt=UTC_TIMESTAMP(6) WHERE Id=@Id AND ClientId=@ClientId AND (EmployeeId IS NULL OR EmployeeId=@EmployeeId)", new { row.Id, row.ClientId, EmployeeId = after.Id }, tx);
                await db.ExecuteAsync("INSERT INTO recruitment_document_roster_audit(RosterId,ClientId,Action,NewValuesJson,ActorUserId) VALUES(@Id,@ClientId,'Employee Master synced',@Receipt,0)", new { row.Id, row.ClientId, Receipt = JsonSerializer.Serialize(new { EmployeeId = after.Id, ChangedBy = changedBy, Source = "Employee Master update" }, Json) }, tx);
            }
        }
        await db.ExecuteAsync("""
UPDATE recruitment_candidates SET FirstName=@FirstName,LastName=@LastName,
 Email=@Email,NormalizedEmail=LOWER(TRIM(@Email)),
 Phone=@Phone,NormalizedPhone=@NormalizedPhone,UpdatedAt=UTC_TIMESTAMP()
WHERE ClientId=@ClientId AND (EmployeeId=@Id OR Id IN @CandidateIds)
""", new { after.Id, after.ClientId, after.FirstName, after.LastName, Email = after.WorkEmail.Trim(), Phone = after.PersonalDetails.Mobile.Trim(), NormalizedPhone = string.Concat(after.PersonalDetails.Mobile.Where(char.IsDigit)), CandidateIds = rows.Select(row => row.CandidateId).DefaultIfEmpty(-1).ToArray() }, tx);
    }
}
