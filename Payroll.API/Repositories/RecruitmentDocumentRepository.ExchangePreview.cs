using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class RecruitmentDocumentRepository
{
    internal static List<RosterExchangePreviewRow> ReadStandardRosterWorkbook(byte[] file, RosterExchangeSettings settings, IReadOnlyList<RosterExchangeColumn> columns)
    {
        var sheets = ReadWorkbook(file);
        if (sheets.Count != 1) throw new InvalidOperationException("Use the single-sheet standard employee template.");
        var sheet = sheets.SingleOrDefault(s => s.Name.Equals("Employees", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Keep the Employees sheet in the standard template.");
        var header = sheet.Rows.FirstOrDefault(r => r.Row == 1) ?? throw new InvalidOperationException("Keep the standard column headers on row 1.");
        const string markerPrefix = "__FREVO_TEMPLATE__:";
        var reference = header.Values.SingleOrDefault(v => v.StartsWith(markerPrefix, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Download the standard employee template, fill its Employees sheet and upload that file.");
        var meta = JsonSerializer.Deserialize<StandardWorkbookReference>(reference[markerPrefix.Length..], Json)
            ?? throw new InvalidOperationException("Download the current standard template.");
        if (meta.Version != "FREVO-EMPLOYEE-1") throw new InvalidOperationException("This template version is unsupported. Download the current standard template.");
        if (meta.ClientId != settings.ClientId || meta.TemplateId != settings.TemplateId)
            throw new InvalidOperationException("This workbook belongs to a different client or offer template.");
        if (settings.Mode == "Update" && meta.ImportBatch != settings.SourceGroup.Trim())
            throw new InvalidOperationException("Select the same import batch as the downloaded corrected version.");
        var known = columns.ToDictionary(c => c.Key, StringComparer.OrdinalIgnoreCase);
        var mapped = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < header.Values.Count; i++)
        {
            if (header.Values[i].StartsWith(markerPrefix, StringComparison.Ordinal)) continue;
            var marker = Regex.Match(header.Values[i], @"\[([^\[\]]+)\]\s*$").Groups[1].Value;
            if (marker.Length == 0) continue; // Unmarked working columns are never imported.
            if (!known.ContainsKey(marker)) throw new InvalidOperationException($"Column '{header.Values[i]}' is no longer configured. Download the current template.");
            if (!mapped.TryAdd(marker, i)) throw new InvalidOperationException($"Column '{header.Values[i]}' appears twice.");
        }
        if (!mapped.ContainsKey("profileCode") || settings.Mode == "Update" && !mapped.ContainsKey("revision"))
            throw new InvalidOperationException("Keep Profile Code and Record Version in the corrected-version template.");
        var rows = new List<RosterExchangePreviewRow>();
        foreach (var source in sheet.Rows.Where(r => r.Row > 1))
        {
            var values = mapped.ToDictionary(p => known[p.Key].Key, p => source.Values.ElementAtOrDefault(p.Value)?.Trim() ?? "", StringComparer.OrdinalIgnoreCase);
            if (!values.Any(p => p.Key is not ("rowAction" or "revision") && p.Value.Length > 0)) continue;
            rows.Add(new() { SourceRow = source.Row, ProfileCode = values.GetValueOrDefault("profileCode", ""), EmployeeCode = values.GetValueOrDefault("employeeCode", ""),
                EmployeeName = (values.GetValueOrDefault("firstName", "") + " " + values.GetValueOrDefault("lastName", "")).Trim(), Values = values });
        }
        if (rows.Count is < 1 or > 2000) throw new InvalidOperationException("The Employees sheet must contain 1 to 2000 populated rows.");
        return rows;
    }
    private sealed record StandardWorkbookReference(string Version, int ClientId, int TemplateId, string ImportBatch);

    private async Task<(RosterExchangePreview Preview, StandardContext Context)> StandardPreviewAsync(byte[] file, string fileName, RosterExchangeSettings settings, AuthUser user)
    {
        StandardAccess(settings, user);
        settings.SourceGroup = settings.SourceGroup.Trim();
        if (settings.SourceGroup.Length == 0) throw new InvalidOperationException("Select or enter the import batch name.");
        fileName = Path.GetFileName(fileName.Replace('\\', '/'));
        if (!fileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) || fileName.Length > 255) throw new InvalidOperationException("Upload the standard .xlsx employee workbook.");
        await using var db = Db(); await db.OpenAsync();
        var context = await StandardContextAsync(db, settings, user);
        var preview = new RosterExchangePreview { FileName = fileName, Rows = ReadStandardRosterWorkbook(file, settings, context.Template.Columns) };
        var duplicates = preview.Rows.Where(r => r.ProfileCode.Length > 0).GroupBy(r => r.ProfileCode, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var proposed = new Dictionary<int, Employee>();
        foreach (var row in preview.Rows)
        {
            row.Reason = StandardRosterMatchIssue(settings.Mode, row, context.Rows, duplicates);
            if (row.Reason.Length > 0) continue;
            var existing = MatchStandardRosterProfile(row.ProfileCode, context.Rows);
            try
            {
                row.Values = NormalizeStandardRosterValues(row.Values, context.Template.Columns);
                var current = existing is null ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) : context.Current[existing.Id];
                if (existing is not null)
                {
                    row.RosterId = existing.Id; row.EmployeeId = existing.EmployeeId; row.Revision = context.Revisions[existing.Id];
                    if (row.Values.GetValueOrDefault("revision", "") != row.Revision) throw new InvalidOperationException("Record changed since download. Download the current corrected version for this row.");
                    foreach (var column in context.Template.Columns.Where(c => c.ReadOnly && c.Key is not ("profileCode" or "revision")))
                        if (row.Values.TryGetValue(column.Key, out var value) && !value.Equals(current.GetValueOrDefault(column.Key, ""), StringComparison.OrdinalIgnoreCase)
                            && !StandardRosterCellEquals(value, current.GetValueOrDefault(column.Key, ""), column.Type))
                            throw new InvalidOperationException($"{column.Header} is reference-only; use Employee Master actions to change it.");
                }
                else
                {
                    if (context.Template.Columns.Any(c => c.ReadOnly && c.Key is not ("profileCode" or "revision") && row.Values.ContainsKey(c.Key))) throw new InvalidOperationException("Leave reference-only columns blank in the insertion template.");
                    if (context.Template.Columns.Any(c => c.RequiredOnInsert && !row.Values.ContainsKey(c.Key))) throw new InvalidOperationException("Fill the required columns listed in Instructions.");
                    if (!row.Values.ContainsKey("candidateEmail") && !row.Values.ContainsKey("candidatePhone")) throw new InvalidOperationException("Work Email or Mobile is required for insertion.");
                    if (row.Values.GetValueOrDefault("revision", "").Length > 0) throw new InvalidOperationException("Use the blank insertion template for a new profile.");
                }
                var employee = StandardRosterCorrectionEmployee(existing?.EmployeeId is { } employeeId ? context.Employees.GetValueOrDefault(employeeId) : null, settings.ClientId, current, row.Values, context.ComponentIds);
                if (employee.FirstName.Length == 0) throw new InvalidOperationException("First Name is required.");
                row.EmployeeName = (employee.FirstName + " " + employee.LastName).Trim(); row.EmployeeCode = employee.EmployeeCode;
                if (row.Values.TryGetValue("salaryStructureId", out var structure) && !await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM salarystructures WHERE CAST(Id AS CHAR)=@Id AND ClientId IN(0,@ClientId) AND Active=TRUE)", new { Id = structure, settings.ClientId }))
                    throw new InvalidOperationException("Salary Template ID is not active for this client.");
                if (employee.ReportingManagerUserId is { } manager && !await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM authusers WHERE Id=@Id AND IsActive=TRUE AND (ClientId IS NULL OR ClientId=@ClientId))", new { Id = manager, settings.ClientId }))
                    throw new InvalidOperationException("Reporting manager is not active for this client.");
                foreach (var column in context.Custom.Fields)
                    if (row.Values.TryGetValue(column.Code, out var value)) EmployeeAttributeRepository.ParseCell(column.Field, value);
                var location = StandardLocationPlan(row, existing, employee, context, settings.ClientId, user);
                if (location.ImportIssues.Count > 0) throw new InvalidOperationException(string.Join(" ", location.ImportIssues));
                if (location.WorkLocationId is > 0) employee.WorkLocationId = location.WorkLocationId.Value;
                _ = StandardStoredValues(new Dictionary<string, string>(current, StringComparer.OrdinalIgnoreCase), row, employee, context);
                var email = employee.WorkEmail.Trim().ToLowerInvariant(); var phone = Phones(employee.PersonalDetails.Mobile).FirstOrDefault() ?? "";
                if (await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM recruitment_candidates WHERE Id<>@Id AND ProfileStatus<>'Archived' AND ((@Email<>'' AND NormalizedEmail=@Email) OR (@Phone<>'' AND RIGHT(NormalizedPhone,10)=RIGHT(@Phone,10))))", new { Id = existing?.CandidateId ?? 0, Email = email, Phone = phone }))
                    throw new InvalidOperationException("Email or mobile belongs to another profile. Review this row; other rows can continue.");
                row.ChangedFields = context.Template.Columns.Where(c => !c.ReadOnly && c.Key is not ("rowAction" or "changeReason") && row.Values.TryGetValue(c.Key, out var value)
                    && !StandardRosterCellEquals(value, current.GetValueOrDefault(c.Key, ""), c.Type)).Select(c => c.Header).ToList();
                row.Action = existing is null ? "Insert" : row.ChangedFields.Count > 0 ? "Update" : "Unchanged";
                if (existing?.EmployeeId is > 0 && row.Action == "Update") proposed[row.SourceRow] = employee;
            }
            catch (Exception e) when (e is InvalidOperationException or FormatException or OverflowException or ArgumentException)
            { row.Action = "Skip"; row.Reason = e.Message; }
        }
        foreach (var key in new[] { "candidateEmail", "candidatePhone" })
        {
            string Contact(RosterExchangePreviewRow row) => key == "candidateEmail" ? row.Values.GetValueOrDefault(key, "").ToLowerInvariant() : Phones(row.Values.GetValueOrDefault(key, "")).FirstOrDefault() ?? "";
            var conflicts = preview.Rows.Where(r => r.Action is "Insert" or "Update" && Contact(r).Length > 0).GroupBy(Contact).Where(g => g.Count() > 1).SelectMany(g => g).ToList();
            foreach (var row in conflicts) { row.Action = "Skip"; row.Reason = "Email or mobile appears in multiple rows. Review these rows; other rows can continue."; proposed.Remove(row.SourceRow); }
        }
        var duplicateMasters = preview.Rows.Where(r => r.Action == "Update" && r.EmployeeId.HasValue).GroupBy(r => r.EmployeeId).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        foreach (var row in preview.Rows.Where(r => duplicateMasters.Contains(r.EmployeeId))) { row.Action = "Skip"; row.Reason = "Multiple profiles target the same Employee Master. Review these rows separately."; proposed.Remove(row.SourceRow); }
        if (proposed.Count > 0)
        {
            var plans = proposed.ToArray();
            var identities = EmployeeRepository.PreviewRosterMasterCorrections(plans.Select(p => p.Value).ToList(), context.Employees.Values.ToList());
            for (var i = 0; i < plans.Length; i++)
            {
                var identity = identities[i];
                if (identity.BlockingReasons.Count == 0 && (!identity.MatchedEmployeeId.HasValue || identity.MatchedEmployeeId == plans[i].Value.Id)) continue;
                var row = preview.Rows.Single(r => r.SourceRow == plans[i].Key); row.Action = "Skip"; row.Reason = "Employee identity details conflict or need review. Check the employee code, contact and bank identifiers; other rows can continue.";
            }
        }
        preview.PreviewToken = Hash(JsonSerializer.Serialize(new { File = BytesHash(file), settings.ClientId, settings.TemplateId, settings.SourceGroup, settings.Mode, UserId = user.Id,
            Columns = context.Template.Columns, Rows = preview.Rows.Select(r => new { r.SourceRow, r.Revision, r.Action, r.Reason }) }, Json));
        return (preview, context);
    }

    private static RecruitmentRosterPreviewRow StandardLocationPlan(RosterExchangePreviewRow row, RecruitmentDocumentRosterRow? existing, Employee proposed, StandardContext context, int clientId, AuthUser user)
    {
        var name = row.Values.GetValueOrDefault("workLocation", "");
        var location = new RecruitmentRosterPreviewRow { SourceRow = row.SourceRow, Values = new(row.Values, StringComparer.OrdinalIgnoreCase),
            WorkLocationId = name.Length == 0 ? proposed.WorkLocationId > 0 ? proposed.WorkLocationId : existing?.WorkLocationId : null };
        if (name.Length > 0) location.ProposedWorkLocation = new WorkLocation { ClientId = clientId, Name = name, Address = row.Values.GetValueOrDefault("workLocationAddress", ""),
            City = row.Values.GetValueOrDefault("workLocationCity", ""), State = row.Values.GetValueOrDefault("workLocationState", ""), PostalCode = row.Values.GetValueOrDefault("workLocationPostalCode", "") };
        ResolveImportLocation(location, clientId, user, context.Locations, context.AllowedLocations);
        return location;
    }

    public async Task<RosterExchangePreview> PreviewStandardTemplateAsync(byte[] file, string name, RosterExchangeSettings settings, AuthUser user)
        => (await StandardPreviewAsync(file, name, settings, user)).Preview;
}
