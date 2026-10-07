using System.Globalization;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public sealed partial class EmployeeAttributeRepository
{
    internal static async Task<List<EmployeeFieldInput>> CandidateValuesAsync(MySqlConnection db, int clientId, long candidateId, long applicationId,
        List<EmployeeAttributeForm> forms, MySqlTransaction? tx = null)
    {
        var submissions = (await db.QueryAsync<CandidateSubmission>(@"SELECT s.Id,v.FormDefinitionId FROM form_submissions s
JOIN form_versions v ON v.Id=s.FormVersionId JOIN form_definitions d ON d.Id=v.FormDefinitionId
WHERE s.ClientId=@ClientId AND d.ClientId=@ClientId AND d.ModuleCode='RECRUITMENT' AND s.CandidateId=@CandidateId
AND (s.ApplicationId IS NULL OR s.ApplicationId=@ApplicationId) AND s.Status='Submitted'
ORDER BY (s.ApplicationId=@ApplicationId) DESC,COALESCE(s.SubmittedAtUtc,s.UpdatedAtUtc) DESC,s.Id DESC",
            new { ClientId = clientId, CandidateId = candidateId, ApplicationId = applicationId }, tx)).DistinctBy(row => row.FormDefinitionId).Select(row => row.Id).ToArray();
        if (submissions.Length == 0) return [];
        var cells = (await db.QueryAsync<CandidateCell>(@"SELECT f.StableFieldCode,t.TypeCode,
COALESCE(v.TextValue,CAST(v.IntegerValue AS CHAR),CAST(v.DecimalValue AS CHAR),DATE_FORMAT(v.DateValue,'%Y-%m-%d'),DATE_FORMAT(v.DateTimeValue,'%Y-%m-%dT%H:%i:%sZ'),IF(v.BooleanValue IS NULL,NULL,IF(v.BooleanValue,'TRUE','FALSE')),'') Value
FROM form_submission_values v JOIN form_fields f ON f.Id=v.FieldId JOIN form_field_types t ON t.Id=f.FieldTypeId WHERE v.SubmissionId IN @Ids
UNION ALL SELECT f.StableFieldCode,t.TypeCode,GROUP_CONCAT(o.OptionCode ORDER BY o.DisplayOrder SEPARATOR '; ')
FROM form_submission_selected_options v JOIN form_fields f ON f.Id=v.FieldId JOIN form_field_types t ON t.Id=f.FieldTypeId JOIN form_field_options o ON o.Id=v.OptionId
WHERE v.SubmissionId IN @Ids GROUP BY v.SubmissionId,f.Id,t.TypeCode
UNION ALL SELECT f.StableFieldCode,t.TypeCode,GROUP_CONCAT(v.SelectedValue ORDER BY v.DisplayOrder SEPARATOR '; ')
FROM form_submission_lookup_values v JOIN form_fields f ON f.Id=v.FieldId JOIN form_field_types t ON t.Id=f.FieldTypeId
WHERE v.SubmissionId IN @Ids GROUP BY v.SubmissionId,f.Id,t.TypeCode", new { Ids = submissions }, tx)).Where(cell => !string.IsNullOrWhiteSpace(cell.Value)).ToLookup(cell => cell.StableFieldCode, StringComparer.OrdinalIgnoreCase);
        var result = new List<EmployeeFieldInput>();
        foreach (var column in Columns(forms))
        {
            var source = cells[column.Field.StableFieldCode].Distinct().ToList();
            if (source.Count == 0) continue;
            if (source.Select(cell => cell.Value).Distinct(StringComparer.Ordinal).Count() > 1 || source.Any(cell => !CompatibleTransferTypes(cell.TypeCode, column.Field.FieldTypeCode)))
                throw new InvalidOperationException($"{column.Field.Label}: candidate field values or types conflict. Correct the candidate form mapping before joining.");
            ParseCell(column.Field, source[0].Value); // Never guess or silently coerce a dropdown/type mismatch.
            result.Add(new(column.Code, source[0].Value));
        }
        return result;
    }

    internal static bool CompatibleTransferTypes(string source, string target) => source == target ||
        new[] { "TEXT", "TEXTAREA", "EMAIL", "PHONE" }.Contains(source) && new[] { "TEXT", "TEXTAREA", "EMAIL", "PHONE" }.Contains(target) ||
        new[] { "RADIO", "SELECT", "SEARCH_SELECT" }.Contains(source) && new[] { "RADIO", "SELECT", "SEARCH_SELECT" }.Contains(target);

    internal static async Task<List<EmployeeImportFieldChange>> TransferChangesAsync(MySqlConnection db, int employeeId, int clientId, List<EmployeeAttributeForm> forms, List<EmployeeFieldInput> input, MySqlTransaction? tx = null)
    {
        var changes = new List<EmployeeImportFieldChange>();
        foreach (var form in forms)
        {
            var current = (await LoadCurrentValuesAsync(db, form, employeeId, clientId, DateTime.UtcNow, tx)).ToDictionary(value => value.FieldId);
            foreach (var column in Columns([form]))
            {
                var value = input.FirstOrDefault(value => value.Code == column.Code)?.Value;
                if (value is null) continue;
                var old = current.GetValueOrDefault(column.Field.Id);
                var oldText = old is null ? "" : CellText(column.Field, old);
                changes.Add(new(column.Code, column.Field.Label, oldText, value, false, false));
            }
        }
        return changes;
    }

    internal static async Task<EmployeeFieldExchange> ProfileExchangeAsync(MySqlConnection db, int employeeId, int clientId)
    {
        var forms = await ExchangeFormsAsync(db, clientId);
        var values = new Dictionary<string, string>();
        foreach (var form in forms)
        {
            var current = (await LoadCurrentValuesAsync(db, form, employeeId, clientId, DateTime.UtcNow)).ToDictionary(value => value.FieldId);
            foreach (var column in Columns([form]))
            {
                if (!current.TryGetValue(column.Field.Id, out var value)) continue;
                values[column.Code] = CellText(column.Field, value);
            }
        }
        return new(forms, Columns(forms), new() { [employeeId] = values });
    }

    internal static string CellText(DynamicFormField field, EmployeeAttributeValue value) => value.TextValue ??
        value.DecimalValue?.ToString(CultureInfo.InvariantCulture) ?? value.IntegerValue?.ToString(CultureInfo.InvariantCulture) ??
        value.DateValue?.ToString("yyyy-MM-dd") ?? value.DateTimeValue?.ToString("yyyy-MM-ddTHH:mm:ssZ") ?? value.BooleanValue?.ToString().ToUpperInvariant() ??
        string.Join("; ", value.SelectedOptionIds.Select(id => field.Options.FirstOrDefault(option => option.Id == id)?.OptionCode).Concat(value.SelectedOptionValues));

    private sealed class CandidateSubmission { public long Id { get; set; } public long FormDefinitionId { get; set; } }
    private sealed class CandidateCell { public string StableFieldCode { get; set; } = ""; public string TypeCode { get; set; } = ""; public string Value { get; set; } = ""; }
}
