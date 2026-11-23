using System.Globalization;
using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class RecruitmentDocumentRepository
{
    private static Dictionary<string, string> StandardStoredValues(Dictionary<string, string> old, RosterExchangePreviewRow row, Employee employee, StandardContext context)
        => StandardStoredMasterInputs(old, row, employee, context.Components.SingleOrDefault(c => c.Code.Equals("BASIC", StringComparison.OrdinalIgnoreCase))?.Id);

    internal static Dictionary<string, string> StandardStoredMasterInputs(Dictionary<string, string> old, RosterExchangePreviewRow row, Employee employee, string? basicComponentId)
    {
        var values = new Dictionary<string, string>(old, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in row.Values.Where(p => p.Key is not ("revision" or "profileCode" or "rowAction" or "changeReason"))) values[pair.Key.StartsWith("OFFER:", StringComparison.OrdinalIgnoreCase) ? pair.Key[6..] : pair.Key] = pair.Value;
        foreach (var pair in StandardEmployeeValues(employee)) values[pair.Key] = pair.Value;
        values["candidateName"] = (employee.FirstName + " " + employee.LastName).Trim();
        values["salaryComponentsJson"] = JsonSerializer.Serialize(employee.SalaryComponents, Json);
        values["employeeCity"] = employee.PersonalDetails.City; values["employeeState"] = employee.PersonalDetails.State;
        values["standardTemplateVersion"] = "FREVO-EMPLOYEE-1";
        if (basicComponentId is not null && employee.SalaryComponents.TryGetValue(basicComponentId, out var monthlyBasic)) values["monthlyBasic"] = monthlyBasic.ToString(CultureInfo.InvariantCulture);
        // Basic is a source input, not a complete pay breakup. Do not derive or
        // validate gross/CTC from it here; the selected salary template owns pay.
        if (decimal.TryParse(values.GetValueOrDefault("monthlyGross", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var gross) && gross >= 0 && gross <= 999999999999m)
            values["salaryInWords"] = "Rupees " + BrandedOfferPdfService.AmountInWords(gross);
        return values;
    }

    public async Task<RosterExchangeResult> ImportStandardTemplateAsync(byte[] file, string name, RosterExchangeSettings settings, AuthUser user)
    {
        StandardAccess(settings, user);
        await using var db = Db(); await db.OpenAsync();
        if (await db.ExecuteScalarAsync<int>("SELECT GET_LOCK('frevo:document-roster-intake',10)") != 1) throw new InvalidOperationException("Another employee list is being saved. Retry shortly.");
        try
        {
            var (preview, context) = await StandardPreviewAsync(file, name, settings, user);
            if (settings.PreviewToken.Length != 64 || settings.PreviewToken != preview.PreviewToken) throw new InvalidOperationException("The workbook or current records changed. Preview the same file again before saving.");
            if (settings.SourceRows.Count != settings.SourceRows.Distinct().Count() || settings.SourceRows.Any(n => !preview.Rows.Any(r => r.SourceRow == n && r.Action is "Update" or "Insert")))
                throw new InvalidOperationException("Select only the reviewed, eligible source rows.");
            var selected = settings.SourceRows.ToHashSet(); var result = new RosterExchangeResult();
            foreach (var row in preview.Rows)
            {
                if (row.Action == "Unchanged") { result.Unchanged++; result.Items.Add(row); continue; }
                if (row.Action == "Skip" || !selected.Contains(row.SourceRow)) { row.Action = "Skip"; if (row.Reason.Length == 0) row.Reason = "Not selected for this upload."; result.Skipped++; result.Items.Add(row); continue; }
                try
                {
                    var existing = row.RosterId.HasValue ? context.Rows.Single(r => r.Id == row.RosterId) : null;
                    Employee? original = null;
                    var candidateId = existing?.CandidateId ?? 0;
                    await using var tx = await db.BeginTransactionAsync();
                    if (await db.ExecuteScalarAsync<int?>("SELECT Id FROM clients WHERE Id=@ClientId AND IsActive=TRUE FOR UPDATE", settings, tx) is null) throw new InvalidOperationException("The selected client is inactive.");
                    if (existing is not null)
                    {
                        existing = await db.QuerySingleOrDefaultAsync<RecruitmentDocumentRosterRow>(RosterJoiningRowsSql.Replace("r.TemplateId,r.WorkLocationId", "r.TemplateId,r.SourceGroup,r.WorkLocationId")
                            + " WHERE r.Id=@Id AND r.ClientId=@ClientId AND r.TemplateId=@TemplateId AND r.SourceGroup=@SourceGroup AND c.ProfileStatus<>'Archived' AND (r.EmployeeId IS NULL OR c.EmployeeId IS NULL OR r.EmployeeId=c.EmployeeId) FOR UPDATE", new { existing.Id, settings.ClientId, settings.TemplateId, settings.SourceGroup }, tx)
                            ?? throw new InvalidOperationException("Profile no longer exists in this batch.");
                        if (!StandardCanSeeLocation(user, existing.WorkLocationId, context.AllowedLocations)) throw new UnauthorizedAccessException();
                        if (existing.EmployeeId is { } employeeId)
                        {
                            await db.ExecuteScalarAsync<int>("SELECT Id FROM employees WHERE Id=@Id AND ClientId=@ClientId FOR UPDATE", new { Id = employeeId, settings.ClientId }, tx);
                            original = await EmployeeRepository.LoadRecruitmentEmployeeAsync(db, employeeId, tx);
                            if (original is null || original.ClientId != settings.ClientId || !StandardCanSeeLocation(user, original.WorkLocationId, context.AllowedLocations)) throw new UnauthorizedAccessException();
                        }
                        var custom = original is not null && context.Custom.Fields.Count > 0 ? (await new EmployeeAttributeRepository(configuration).ExchangeAsync(settings.ClientId, user)).Values.GetValueOrDefault(original.Id) : null;
                        if (StandardRosterRevision(existing, original, custom) != row.Revision) throw new InvalidOperationException("Record changed during upload; download its current version and retry this row.");
                    }
                    var proposed = StandardRosterCorrectionEmployee(original, settings.ClientId, existing is null ? new Dictionary<string, string>() : context.Current[existing.Id], row.Values, context.ComponentIds, context.BaselineComponentIds);
                    if (row.Values.TryGetValue("reportingManagerEmail", out var managerEmail))
                    {
                        var managers = (await db.QueryAsync<WorkflowApprover>("SELECT Id,Email,ClientId FROM authusers WHERE IsActive=TRUE AND (ClientId IS NULL OR ClientId=@ClientId)", settings, tx)).ToList();
                        proposed.ReportingManagerUserId = StandardReportingManagerId(managerEmail, settings.ClientId, managers);
                    }
                    var location = StandardLocationPlan(row, existing, proposed, context, settings.ClientId, user);
                    if (location.ImportIssues.Count > 0) throw new InvalidOperationException(string.Join(" ", location.ImportIssues));
                    await SaveImportLocationAsync(db, tx, settings.ClientId, user, location);
                    if (location.WorkLocationId.HasValue) proposed.WorkLocationId = location.WorkLocationId.Value;
                    var values = StandardStoredValues(existing is null ? [] : ReadValues(existing.ValuesJson), row, proposed, context);
                    if (location.WorkLocationId.HasValue) ApplyLocationValues(values, context.Locations.FirstOrDefault(l => l.Id == location.WorkLocationId) ?? location.ProposedWorkLocation!);
                    if (existing is null)
                    {
                        var candidate = await talent.CreateStandardRosterCandidateAsync(db, tx, new SaveRecruitmentCandidate { ClientId = settings.ClientId,
                            FirstName = proposed.FirstName, LastName = proposed.LastName, Email = proposed.WorkEmail, Phone = proposed.PersonalDetails.Mobile, CurrentTitle = proposed.Designation }, user);
                        candidateId = candidate.Id; row.ProfileCode = candidate.CandidateCode;
                    }
                    if (original is not null)
                    {
                        var current = await EmployeeRepository.ReadExcelPayrollIdentitySnapshotAsync(db, tx, settings.ClientId);
                        var identity = EmployeeRepository.PreviewRosterMasterCorrections([proposed], current).Single();
                        if (identity.BlockingReasons.Count > 0 || identity.MatchedEmployeeId.HasValue && identity.MatchedEmployeeId != original.Id) throw new InvalidOperationException("Employee identity now conflicts. Review this row before retrying.");
                        var reason = row.Values.GetValueOrDefault("changeReason", "");
                        if (reason.Length == 0) reason = "Corrected employee-list version: " + row.ProfileCode;
                        await EmployeeRepository.SaveRosterMasterCorrectionAsync(db, tx, original, proposed, user, reason);
                        var customInput = context.Custom.Fields.Where(c => row.Values.ContainsKey(c.Code)).Select(c => new EmployeeFieldInput(c.Code, row.Values[c.Code])).ToList();
                        var error = await EmployeeAttributeRepository.SaveExchangeAsync(db, tx, original.Id, settings.ClientId, context.Custom.Forms, customInput, "ROSTER_CORRECTION", row.ProfileCode, user.Id, allowIncomplete: true);
                        if (error.Length > 0) throw new InvalidOperationException(error);
                    }
                    var email = proposed.WorkEmail.Trim(); var phone = proposed.PersonalDetails.Mobile.Trim();
                    await db.ExecuteAsync("UPDATE recruitment_candidates SET FirstName=@FirstName,LastName=@LastName,Email=@Email,Phone=@Phone,NormalizedEmail=LOWER(@Email),NormalizedPhone=@NormalizedPhone WHERE Id=@Id AND ClientId=@ClientId",
                        new { proposed.FirstName, proposed.LastName, Email = email, Phone = phone, NormalizedPhone = Phones(phone).FirstOrDefault() ?? "", Id = candidateId, settings.ClientId }, tx);
                    var serialized = SerializeValues(values);
                    long rosterId;
                    if (existing is null)
                        rosterId = await db.ExecuteScalarAsync<long>("""
INSERT INTO recruitment_document_roster(ClientId,CandidateId,TemplateId,WorkLocationId,SourceGroup,SourceFileName,SourceFileHash,SourceSheetName,SourceRow,TemplateHash,ValuesJson,SourceValuesJson,CreatedByUserId,UpdatedByUserId)
VALUES(@ClientId,@CandidateId,@TemplateId,@WorkLocationId,@SourceGroup,@FileName,@FileHash,'Employees',@SourceRow,@TemplateHash,@Values,@Source,@UserId,@UserId); SELECT LAST_INSERT_ID();
""", new { settings.ClientId, CandidateId = candidateId, settings.TemplateId, location.WorkLocationId, settings.SourceGroup, FileName = preview.FileName, FileHash = BytesHash(file), row.SourceRow,
                            TemplateHash = Hash((await TemplateAsync(db, settings.ClientId, settings.TemplateId, tx)).BodyTemplate), Values = serialized, Source = SerializeValues(row.Values), UserId = user.Id }, tx);
                    else
                    {
                        rosterId = existing.Id;
                        await db.ExecuteAsync("UPDATE recruitment_document_roster SET WorkLocationId=@WorkLocationId,ValuesJson=@Values,SourceValuesJson=@Source,SourceFileName=@FileName,SourceFileHash=@FileHash,SourceSheetName='Employees',SourceRow=@SourceRow,UpdatedByUserId=@UserId,UpdatedAt=CURRENT_TIMESTAMP(6) WHERE Id=@Id AND ClientId=@ClientId",
                            new { Id = rosterId, settings.ClientId, location.WorkLocationId, Values = serialized, Source = SerializeValues(row.Values), FileName = preview.FileName, FileHash = BytesHash(file), row.SourceRow, UserId = user.Id }, tx);
                    }
                    await db.ExecuteAsync("INSERT INTO recruitment_document_roster_audit(RosterId,ClientId,Action,OldValuesJson,NewValuesJson,ActorUserId) VALUES(@Id,@ClientId,@Action,@Old,@New,@UserId)",
                        new { Id = rosterId, settings.ClientId, Action = existing is null ? "Standard template insertion" : "Corrected version imported", Old = existing?.ValuesJson, New = serialized, UserId = user.Id }, tx);
                    await tx.CommitAsync();
                    if (existing is null) { row.Action = "Inserted"; result.Inserted++; } else { row.Action = "Updated"; result.Updated++; }
                }
                catch (Exception e) when (e is InvalidOperationException or ArgumentException or FormatException or MySqlException or UnauthorizedAccessException)
                {
                    row.Action = "Skip"; row.Reason = e is MySqlException ? "This row could not be saved; its transaction was rolled back. Review identifiers and retry." : e is UnauthorizedAccessException ? "This row is no longer in your permitted scope." : e.Message;
                    result.Skipped++;
                }
                result.Items.Add(row);
            }
            return result;
        }
        finally { await db.ExecuteAsync("SELECT RELEASE_LOCK('frevo:document-roster-intake')"); }
    }
}
