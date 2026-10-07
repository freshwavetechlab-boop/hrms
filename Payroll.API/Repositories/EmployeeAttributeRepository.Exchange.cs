using System.Globalization;
using System.Text.RegularExpressions;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public sealed partial class EmployeeAttributeRepository
{
    internal static readonly string[] ExchangeInfotypes = ["0001", "0002", "0006", "0008", "0009"];
    private static readonly HashSet<string> CoreFieldCodes = new(
        ("EmployeeCode EmployeeID FirstName LastName Gender DateOfBirth WorkEmail Mobile DateOfJoining Department Designation Grade EmploymentType EmployeeType EmployeeCategory SkillCategory WorkLocation WorkLocationID ReportingManagerEmail ReportingManagerID ReportingManagerUserID PortalAccess Active IsActive SalaryTemplate SalaryStructureID AnnualCTC SalaryJson PAN PanNumber Aadhaar AadhaarNumber UAN UanNumber ESIC EsicNumber Address CorrespondenceAddress PermanentAddress City District State BankName BankAccountNo IFSC IfscCode PaymentMode ChangeReason").Split(' '), StringComparer.OrdinalIgnoreCase);

    internal static string FieldKey(EmployeeAttributeForm form, DynamicFormField field) => $"CUSTOM:{form.InfotypeCode}:{form.FormCode}:{field.StableFieldCode}".ToUpperInvariant();
    internal static string HeaderKey(string header) => Regex.Match(header, @"\[(CUSTOM:[A-Z0-9_]+:[A-Z0-9_]+:[A-Z0-9_]+)\]\s*$", RegexOptions.IgnoreCase).Groups[1].Value.ToUpperInvariant();
    internal static string ConfigurationError(IEnumerable<DynamicFormField> fields, IEnumerable<DynamicFormField>? previous = null)
    {
        var history = (previous ?? []).ToList();
        var before = history.GroupBy(field => field.StableFieldCode, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var byId = history.Where(field => field.Id > 0).DistinctBy(field => field.Id).ToDictionary(field => field.Id);
        foreach (var field in fields)
        {
            if (CoreFieldCodes.Contains(Regex.Replace(field.StableFieldCode, "[^a-zA-Z0-9]", "")) || CoreFieldCodes.Contains(Regex.Replace(field.Label, "[^a-zA-Z0-9]", "")))
                return $"{field.Label}: use the existing core employee field instead of creating a duplicate.";
            if (byId.TryGetValue(field.Id, out var original) && original.StableFieldCode != field.StableFieldCode)
                return $"{field.Label}: keep the published stable field code; rename its label instead.";
            if (before.TryGetValue(field.StableFieldCode, out var old) && old.FieldTypeCode != field.FieldTypeCode)
                return $"{field.Label}: the type of a published field cannot change. Add a new field code instead.";
        }
        return "";
    }

    public async Task<EmployeeFieldExchange> ExchangeAsync(int clientId, AuthUser user, bool includeValues = true)
    {
        if (!ClientAccess(user, clientId) || !HasPermission(user, "employees.view", "employees.manage", "reports.view", "settings.manage", "security.manage"))
            throw new UnauthorizedAccessException();
        await using var db = Db(); await db.OpenAsync();
        var forms = await ExchangeFormsAsync(db, clientId);
        var columns = Columns(forms);
        var columnByIdentity = columns.ToDictionary(column => $"{column.FormDefinitionId}:{column.InfotypeCode}:{column.Field.StableFieldCode}", StringComparer.OrdinalIgnoreCase);
        var data = new Dictionary<int, Dictionary<string, string>>();
        if (!includeValues || columns.Count == 0) return new(forms, columns, data);
        // Read each latest effective submission once, including fields from earlier published versions.
        var submissions = (await db.QueryAsync<ExchangeSubmission>(@"SELECT s.Id,s.EntityId EmployeeId,v.FormDefinitionId,COALESCE(s.EmployeeInfotypeCode,'0002') InfotypeCode
FROM form_submissions s JOIN form_versions v ON v.Id=s.FormVersionId JOIN employees e ON e.Id=s.EntityId AND e.ClientId=s.ClientId
WHERE s.ClientId=@ClientId AND s.EntityType='EMPLOYEE' AND s.Status='Submitted' AND v.FormDefinitionId IN @FormIds
AND COALESCE(s.EffectiveFromUtc,s.SubmittedAtUtc,s.StartedAtUtc)<=UTC_TIMESTAMP(6)
AND (s.EffectiveToUtc IS NULL OR s.EffectiveToUtc>UTC_TIMESTAMP(6))
ORDER BY COALESCE(s.EffectiveFromUtc,s.SubmittedAtUtc,s.StartedAtUtc) DESC,s.Id DESC", new { ClientId = clientId, FormIds = forms.Select(form => form.FormDefinitionId).Distinct().ToArray() }))
            .DistinctBy(row => (row.EmployeeId, row.FormDefinitionId, row.InfotypeCode)).ToDictionary(row => row.Id);
        if (submissions.Count > 0)
        {
            var ids = submissions.Keys.ToArray();
            var cells = await db.QueryAsync<ExchangeCell>(@"SELECT v.SubmissionId,f.StableFieldCode,
COALESCE(v.TextValue,CAST(v.IntegerValue AS CHAR),CAST(v.DecimalValue AS CHAR),DATE_FORMAT(v.DateValue,'%Y-%m-%d'),DATE_FORMAT(v.DateTimeValue,'%Y-%m-%dT%H:%i:%sZ'),IF(v.BooleanValue IS NULL,NULL,IF(v.BooleanValue,'TRUE','FALSE')),'') Value
FROM form_submission_values v JOIN form_fields f ON f.Id=v.FieldId WHERE v.SubmissionId IN @Ids
UNION ALL SELECT v.SubmissionId,f.StableFieldCode,o.OptionCode FROM form_submission_selected_options v JOIN form_fields f ON f.Id=v.FieldId JOIN form_field_options o ON o.Id=v.OptionId WHERE v.SubmissionId IN @Ids
UNION ALL SELECT v.SubmissionId,f.StableFieldCode,v.SelectedValue FROM form_submission_lookup_values v JOIN form_fields f ON f.Id=v.FieldId WHERE v.SubmissionId IN @Ids", new { Ids = ids });
            foreach (var group in cells.GroupBy(cell => (cell.SubmissionId, cell.StableFieldCode)))
            {
                var submission = submissions[group.Key.SubmissionId];
                var column = columnByIdentity.GetValueOrDefault($"{submission.FormDefinitionId}:{submission.InfotypeCode}:{group.Key.StableFieldCode}");
                if (column is null) continue;
                if (!data.TryGetValue(submission.EmployeeId, out var values)) data[submission.EmployeeId] = values = new();
                values[column.Code] = string.Join("; ", group.Select(cell => cell.Value));
            }
        }
        return new(forms, columns, data);
    }

    internal static List<EmployeeFieldColumn> Columns(IEnumerable<EmployeeAttributeForm> forms) => forms.SelectMany(form => form.Sections.SelectMany(section => section.Fields)
        .Where(field => field.IsActive && field.FieldTypeCode != "UPLOAD")
        .Select(field => new EmployeeFieldColumn(FieldKey(form, field), $"{field.Label} [{FieldKey(form, field)}]", form.InfotypeCode, form.FormDefinitionId, field))).ToList();

    internal static async Task<List<EmployeeAttributeForm>> ExchangeFormsAsync(MySqlConnection db, int clientId, MySqlTransaction? tx = null)
    {
        var forms = new List<EmployeeAttributeForm>();
        foreach (var code in ExchangeInfotypes) forms.AddRange(await LoadBoundFormsAsync(db, clientId, code, tx));
        foreach (var form in forms) await PopulateFormAsync(db, form, tx);
        return forms;
    }

    internal static EmployeeAttributeValue ParseCell(DynamicFormField field, string text)
    {
        var value = new EmployeeAttributeValue { FieldId = field.Id }; text = text.Trim();
        if (text.Length == 0) return value;
        switch (field.FieldTypeCode)
        {
            case "NUMBER":
                if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)) throw new InvalidOperationException($"{field.Label}: enter a number.");
                value.DecimalValue = number; break;
            case "DATE": case "DATETIME":
                if (!(field.FieldTypeCode == "DATE" ? Regex.IsMatch(text, @"^\d{4}-\d{2}-\d{2}$") : Regex.IsMatch(text, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}"))) throw new InvalidOperationException($"{field.Label}: enter a valid ISO date.");
                if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)) throw new InvalidOperationException($"{field.Label}: enter a valid ISO date.");
                if (field.FieldTypeCode == "DATE") value.DateValue = date.Date; else value.DateTimeValue = date; break;
            case "CHECKBOX":
                if (!new[] { "true", "false", "yes", "no", "1", "0" }.Contains(text.ToLowerInvariant())) throw new InvalidOperationException($"{field.Label}: enter TRUE or FALSE.");
                value.BooleanValue = text.Equals("true", StringComparison.OrdinalIgnoreCase) || text.Equals("yes", StringComparison.OrdinalIgnoreCase) || text == "1"; break;
            case "RADIO": case "SELECT": case "SEARCH_SELECT": case "MULTI_SELECT":
                var tokens = field.FieldTypeCode == "MULTI_SELECT" ? text.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) : [text];
                if (field.Options.Count == 0 && field.LookupSourceCode.Length > 0) value.SelectedOptionValues = tokens.ToList();
                else foreach (var token in tokens)
                {
                    var options = field.Options.Where(option => option.IsActive && option.OptionCode.Equals(token, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (options.Count == 0) options = field.Options.Where(option => option.IsActive && option.OptionLabel.Equals(token, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (options.Count != 1) throw new InvalidOperationException($"{field.Label}: '{token}' is not an unambiguous active option.");
                    value.SelectedOptionIds.Add(options[0].Id);
                }
                break;
            default: value.TextValue = text; break;
        }
        return value;
    }

    internal static async Task<string> SaveExchangeAsync(MySqlConnection db, MySqlTransaction tx, int employeeId, int clientId,
        List<EmployeeAttributeForm> forms, List<EmployeeFieldInput> input, string source, string reference, int userId, bool allowIncomplete = false, bool fillBlanksOnly = false, bool validateOnly = false)
    {
        var columns = Columns(forms).ToDictionary(column => column.Code, StringComparer.OrdinalIgnoreCase);
        if (input.GroupBy(row => row.Code, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1)) return "A configured field was supplied twice.";
        if (input.Any(row => !columns.ContainsKey(row.Code))) return "A configured field is no longer active for this client. Download the latest template.";
        var effectiveAt = DateTime.UtcNow;
        foreach (var form in forms)
        {
            var supplied = input.Where(row => columns[row.Code].FormDefinitionId == form.FormDefinitionId && columns[row.Code].InfotypeCode == form.InfotypeCode && !string.IsNullOrWhiteSpace(row.Value)).ToList();
            if (supplied.Count == 0) continue;
            var merged = (await LoadCurrentValuesAsync(db, form, employeeId, clientId, effectiveAt, tx)).ToDictionary(row => row.FieldId, CloneValue);
            foreach (var row in supplied)
            {
                var field = columns[row.Code].Field;
                if (fillBlanksOnly && merged.TryGetValue(field.Id, out var current) && HasValue(current)) continue;
                try { merged[field.Id] = ParseCell(field, row.Value); }
                catch (InvalidOperationException exception) { return exception.Message; }
            }
            var runtime = await LoadRuntimeFieldsAsync(db, form.Id, tx);
            if (allowIncomplete) runtime = runtime.Where(field => merged.TryGetValue(field.Id, out var value) && HasValue(value)).ToList();
            var error = await ValidateSnapshotAsync(db, tx, runtime, merged, employeeId, clientId);
            if (error.Length > 0) return error;
            // Keep the complete snapshot even when joining allows missing required fields.
            var fullRuntime = await LoadRuntimeFieldsAsync(db, form.Id, tx);
            if (validateOnly) continue;
            var (_, revisionError) = await InsertRevisionAsync(db, tx, form, employeeId, clientId, form.InfotypeCode, effectiveAt, source, reference, source, userId, fullRuntime, merged);
            if (revisionError.Length > 0) return revisionError;
        }
        return "";
    }

    private sealed class ExchangeSubmission { public long Id { get; set; } public int EmployeeId { get; set; } public long FormDefinitionId { get; set; } public string InfotypeCode { get; set; } = ""; }
    private sealed class ExchangeCell { public long SubmissionId { get; set; } public string StableFieldCode { get; set; } = ""; public string Value { get; set; } = ""; }
}
