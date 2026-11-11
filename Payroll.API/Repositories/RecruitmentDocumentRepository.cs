using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

// Existing-manpower profiles, document inputs and explicitly authorized roster
// releases. Release links Employee Master using the existing identity/infotype
// services; native application approvals/acceptance and payroll are separate.
public sealed partial class RecruitmentDocumentRepository(IConfiguration configuration, RecruitmentTalentRepository talent, LayoutDocumentPdfService pdf)
{
    private MySqlConnection Db() => new(configuration.GetConnectionString("Default"));
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string SerializeValues(Dictionary<string, string> values) => JsonSerializer.Serialize(values.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(p => p.Key, p => p.Value), Json);
    private static void Access(AuthUser user, int clientId)
    {
        if (user.Id <= 0 || !user.IsActive || user.MustChangePassword || !RecruitmentPermissions.Has(user, "recruitment.candidate.manage", "recruitment.offer.manage", "recruitment.offer.issue") || !SelectedClientAccess.Allows(user, clientId) || user.ClientId.HasValue && user.ClientId != clientId)
            throw new UnauthorizedAccessException("The employee list is outside your permitted client scope.");
    }
    private static async Task EnsureAsync(MySqlConnection db)
    {
        await db.ExecuteAsync("""
CREATE TABLE IF NOT EXISTS recruitment_document_roster (
 Id BIGINT PRIMARY KEY AUTO_INCREMENT,ClientId INT NOT NULL,CandidateId BIGINT NOT NULL,TemplateId INT NOT NULL,
 SourceGroup VARCHAR(180) NOT NULL,SourceFileName VARCHAR(255) NOT NULL,SourceFileHash CHAR(64) NOT NULL,
 SourceSheetName VARCHAR(100) NOT NULL,SourceRow INT NOT NULL,TemplateHash CHAR(64) NOT NULL,
 ValuesJson JSON NOT NULL,SourceValuesJson JSON NOT NULL,CreatedByUserId INT NOT NULL,UpdatedByUserId INT NOT NULL,
 CreatedAt DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),UpdatedAt DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
 UNIQUE KEY UX_document_roster (ClientId,TemplateId,CandidateId,SourceGroup),INDEX IX_document_roster_candidate(CandidateId));
CREATE TABLE IF NOT EXISTS recruitment_document_roster_audit (
 Id BIGINT PRIMARY KEY AUTO_INCREMENT,RosterId BIGINT NOT NULL,ClientId INT NOT NULL,Action VARCHAR(40) NOT NULL,
 OldValuesJson JSON NULL,NewValuesJson JSON NOT NULL,ActorUserId INT NOT NULL,
 CreatedAt DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),INDEX IX_document_roster_audit(RosterId,Id));
""");
        await EnsureReleaseSchemaAsync(db);
    }
    private static async Task<RecruitmentTemplate> TemplateAsync(MySqlConnection db, int clientId, int templateId, MySqlTransaction? transaction = null)
    {
        var template = await db.QuerySingleOrDefaultAsync<RecruitmentTemplate>("SELECT * FROM recruitment_templates WHERE Id=@Id AND ClientId IN (0,@ClientId) AND IsActive=TRUE AND TemplateType='Offer Letter'", new { Id = templateId, ClientId = clientId }, transaction);
        if (template is null || !LayoutDocumentPdfService.IsLayout(template.BodyTemplate)) throw new InvalidOperationException("Select an active document-layout Offer Letter template for this client.");
        LayoutDocumentPdfService.Parse(template.BodyTemplate); return template;
    }
    public async Task<object[]> TemplatesAsync(int clientId, AuthUser user)
    {
        Access(user, clientId); await using var db = Db(); await db.OpenAsync();
        var rows = await db.QueryAsync<RecruitmentTemplate>("SELECT * FROM recruitment_templates WHERE ClientId IN (0,@ClientId) AND IsActive=TRUE AND TemplateType='Offer Letter' ORDER BY TemplateName", new { ClientId = clientId });
        return rows.Where(t => LayoutDocumentPdfService.IsLayout(t.BodyTemplate)).Select(t => { var layout = LayoutDocumentPdfService.Parse(t.BodyTemplate); return (object)new { t.Id, t.TemplateName, t.ClientId, Fields = layout.Inputs, layout.ImportPresets }; }).ToArray();
    }
    public async Task<List<RecruitmentDocumentRosterRow>> RowsAsync(int clientId, AuthUser user)
    {
        Access(user, clientId); await using var db = Db(); await db.OpenAsync(); await EnsureAsync(db);
        var rows = (await db.QueryAsync<RecruitmentDocumentRosterRow>("""
SELECT r.Id,r.ClientId,r.CandidateId,r.TemplateId,r.WorkLocationId,r.OfferNumber,r.SourceGroup,r.SourceFileName,r.SourceSheetName,r.SourceRow,CAST(r.ValuesJson AS CHAR) ValuesJson,
 (SELECT MAX(ReleasedAt) FROM recruitment_document_roster_releases rr WHERE rr.RosterId=r.Id AND rr.ClientId=r.ClientId) ReleasedAt,
 t.TemplateName,c.CandidateCode,TRIM(CONCAT(c.FirstName,' ',c.LastName)) CandidateName,c.Email,c.Phone,c.ProfileStatus,COALESCE(r.EmployeeId,c.EmployeeId) EmployeeId,
 COALESCE((SELECT EmployeeCode FROM employees e WHERE e.Id=COALESCE(r.EmployeeId,c.EmployeeId) AND e.ClientId=r.ClientId),'') EmployeeCode
FROM recruitment_document_roster r JOIN recruitment_candidates c ON c.Id=r.CandidateId AND c.ClientId=r.ClientId
LEFT JOIN recruitment_templates t ON t.Id=r.TemplateId WHERE r.ClientId=@ClientId ORDER BY r.SourceGroup,r.SourceFileName,r.SourceRow,r.Id
""", new { ClientId = clientId })).ToList();
        var templates = (await db.QueryAsync<RecruitmentTemplate>("SELECT * FROM recruitment_templates WHERE ClientId IN (0,@ClientId)", new { ClientId = clientId })).ToDictionary(t => t.Id);
        var locations = await RecruitmentAccessScope.WorkLocationsAsync(db, user, clientId);
        rows = rows.Where(row => CanSeeLocation(user, row.WorkLocationId, locations)).ToList();
        foreach (var row in rows)
        {
            if (row.ReleasedAt.HasValue) row.ReleasedAt = DateTime.SpecifyKind(row.ReleasedAt.Value, DateTimeKind.Utc);
            row.Values = ReadValues(row.ValuesJson); row.Revision = RowRevision(row.ValuesJson, row.WorkLocationId); row.ValuesJson = "";
            var location = locations.FirstOrDefault(item => item.Id == row.WorkLocationId);
            if (location is not null) ApplyLocationValues(row.Values, location);
            row.WorkLocation = row.Values.GetValueOrDefault("jobLocation", "");
            if (!templates.TryGetValue(row.TemplateId, out var template) || !template.IsActive || !LayoutDocumentPdfService.IsLayout(template.BodyTemplate)) row.DocumentIssues = ["Document template is unavailable or inactive"];
            else
            {
                var values = new Dictionary<string, string>(row.Values, StringComparer.OrdinalIgnoreCase) { ["candidateName"] = row.CandidateName, ["candidateEmail"] = row.Email, ["candidatePhone"] = row.Phone };
                LayoutDocumentPdfService.PrepareValues(LayoutDocumentPdfService.Parse(template.BodyTemplate), values, false, out var missing); row.DocumentIssues = missing;
            }
            if (row.ProfileStatus == "Archived") row.DocumentIssues.Add("The linked profile is archived");
            if (row.WorkLocationId.HasValue && location is null) row.DocumentIssues.Add("The linked work location is unavailable or inactive");
        }
        return rows;
    }
    private static Dictionary<string, string> ReadValues(string json) => new(JsonSerializer.Deserialize<Dictionary<string, string>>(json, Json) ?? [], StringComparer.OrdinalIgnoreCase);

    internal static async Task<int> ReferenceCountAsync(MySqlConnection db, string field, long id)
    {
        if (field is not ("TemplateId" or "CandidateId")) throw new ArgumentException("Invalid roster reference.");
        var exists = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name='recruitment_document_roster'");
        return exists == 0 ? 0 : await db.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM recruitment_document_roster WHERE {field}=@Id", new { Id = id });
    }

    public async Task<RecruitmentRosterImportResult> ImportAsync(byte[] file, string fileName, RecruitmentRosterImport request, AuthUser user)
    {
        Access(user, request.ClientId);
        IntakeAccess(user);
        await using var db = Db(); await db.OpenAsync(); await EnsureAsync(db);
        // Serializes only the new roster intake path. Re-check identities at save;
        // a reviewed preview is never authority to create a duplicate profile.
        if (await db.ExecuteScalarAsync<int>("SELECT GET_LOCK('frevo:document-roster-intake',10)") != 1) throw new InvalidOperationException("Another employee list is being saved. Retry this same list shortly.");
        try
        {
            var preview = await PreviewAsync(file, fileName, request, user);
            if (request.PreviewToken is not { Length: 64 } || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(request.PreviewToken), Encoding.ASCII.GetBytes(preview.PreviewToken)))
                throw new InvalidOperationException("The workbook, template or mapping changed. Preview the current list before saving.");
            if (request.SourceRows.Count == 0 || request.SourceRows.Count != request.SourceRows.Distinct().Count() || request.SourceRows.Any(r => !preview.Rows.Any(p => p.SourceRow == r)))
                throw new InvalidOperationException("Select the reviewed employee rows to import.");
            var selected = preview.Rows.Where(r => request.SourceRows.Contains(r.SourceRow)).ToArray();
            if (selected.Any(r => r.ImportIssues.Count > 0)) throw new InvalidOperationException("Resolve or exclude rows with identity/import issues before saving. Missing document details may remain pending.");
            var result = new RecruitmentRosterImportResult();
            var template = await TemplateAsync(db, request.ClientId, request.TemplateId);
            foreach (var row in selected)
            {
                var candidateId = row.CandidateId;
                if (candidateId is null)
                {
                    var parts = ProfileName(row.CandidateName).Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                    var saved = await talent.SaveCandidateAsync(new SaveRecruitmentCandidate
                    {
                        ClientId = request.ClientId, FirstName = parts[0], LastName = parts.ElementAtOrDefault(1) ?? "", Email = row.Email, Phone = row.Phone,
                        CurrentTitle = row.PositionTitle, CurrentLocation = row.WorkLocationId.HasValue ? "" : row.WorkLocation, CurrentCtc = null, ExpectedCtc = null, NoticePeriodDays = null,
                        SourceType = "Direct", ProfileStatus = "Active", ConsentStatus = "Pending"
                    }, user);
                    if (saved.Row is null) { result.Errors.Add("Source row " + row.SourceRow + ": " + saved.Error); continue; }
                    candidateId = saved.Row.Id; result.CreatedProfiles++;
                }
                else result.ReusedProfiles++;
                await using var tx = await db.BeginTransactionAsync();
                var old = await db.QuerySingleOrDefaultAsync<RosterStored>("SELECT Id,WorkLocationId,CAST(ValuesJson AS CHAR) ValuesJson FROM recruitment_document_roster WHERE ClientId=@ClientId AND CandidateId=@CandidateId AND TemplateId=@TemplateId AND SourceGroup=@SourceGroup FOR UPDATE", new { request.ClientId, CandidateId = candidateId, request.TemplateId, SourceGroup = request.SourceGroup.Trim() }, tx);
                if ((old is null ? "" : RowRevision(old.ValuesJson, old.WorkLocationId)) != row.ExistingRevision)
                    throw new InvalidOperationException("An employee list row changed after preview. Preview the current list before saving.");
                if (row.WorkLocationId.HasValue) await ActiveLocationAsync(db, request.ClientId, row.WorkLocationId.Value, tx);
                var values = MergeValues(old?.ValuesJson, row.Values);
                var serialized = SerializeValues(values);
                var id = await db.ExecuteScalarAsync<long>("""
INSERT INTO recruitment_document_roster(ClientId,CandidateId,TemplateId,WorkLocationId,SourceGroup,SourceFileName,SourceFileHash,SourceSheetName,SourceRow,TemplateHash,ValuesJson,SourceValuesJson,CreatedByUserId,UpdatedByUserId)
VALUES(@ClientId,@CandidateId,@TemplateId,@WorkLocationId,@SourceGroup,@FileName,@FileHash,@SheetName,@SourceRow,@TemplateHash,@ValuesJson,@SourceValuesJson,@UserId,@UserId)
ON DUPLICATE KEY UPDATE Id=LAST_INSERT_ID(Id),SourceFileName=VALUES(SourceFileName),SourceFileHash=VALUES(SourceFileHash),SourceSheetName=VALUES(SourceSheetName),
 WorkLocationId=COALESCE(VALUES(WorkLocationId),WorkLocationId),SourceRow=VALUES(SourceRow),TemplateHash=VALUES(TemplateHash),ValuesJson=VALUES(ValuesJson),SourceValuesJson=VALUES(SourceValuesJson),UpdatedByUserId=VALUES(UpdatedByUserId),UpdatedAt=CURRENT_TIMESTAMP(6);
SELECT LAST_INSERT_ID();
""", new { request.ClientId, CandidateId = candidateId, request.TemplateId, row.WorkLocationId, SourceGroup = request.SourceGroup.Trim(), FileName = preview.FileName, preview.FileHash, SheetName = preview.SheetName, row.SourceRow, TemplateHash = Hash(template.BodyTemplate), ValuesJson = serialized, SourceValuesJson = SerializeValues(row.SourceValues), UserId = user.Id }, tx);
                if (old is null || SerializeValues(ReadValues(old.ValuesJson)) != serialized)
                    await db.ExecuteAsync("INSERT INTO recruitment_document_roster_audit(RosterId,ClientId,Action,OldValuesJson,NewValuesJson,ActorUserId) VALUES(@Id,@ClientId,@Action,@Old,@New,@UserId)", new { Id = id, request.ClientId, Action = old is null ? "Import" : "Update from source", Old = old?.ValuesJson, New = serialized, UserId = user.Id }, tx);
                await tx.CommitAsync(); result.SavedRows++;
            }
            return result;
        }
        finally { await db.ExecuteAsync("SELECT RELEASE_LOCK('frevo:document-roster-intake')"); }
    }
    private static Dictionary<string, string> MergeValues(string? old, Dictionary<string, string> incoming)
    {
        var values = old is null ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) : ReadValues(old);
        // Re-uploading the original list must not erase details completed later.
        foreach (var (key, value) in incoming) if (!string.IsNullOrWhiteSpace(value) || !values.ContainsKey(key)) values[key] = value;
        return values;
    }
    private sealed class RosterStored { public long Id { get; set; } public int? WorkLocationId { get; set; } public string ValuesJson { get; set; } = "{}"; }

    public async Task EditAsync(int clientId, long id, RecruitmentRosterEdit request, AuthUser user)
    {
        Access(user, clientId);
        if (!RecruitmentPermissions.Has(user, "recruitment.candidate.manage", "recruitment.offer.manage")) throw new UnauthorizedAccessException();
        await using var db = Db(); await db.OpenAsync(); await EnsureAsync(db);
        var row = await db.QuerySingleOrDefaultAsync<RecruitmentDocumentRosterRow>("SELECT Id,ClientId,TemplateId,WorkLocationId,CAST(ValuesJson AS CHAR) ValuesJson FROM recruitment_document_roster WHERE Id=@Id AND ClientId=@ClientId", new { Id = id, ClientId = clientId }) ?? throw new InvalidOperationException("Employee list row was not found.");
        await RequireLocationAsync(db, user, clientId, row.WorkLocationId);
        var template = await TemplateAsync(db, clientId, row.TemplateId); var layout = LayoutDocumentPdfService.Parse(template.BodyTemplate);
        if (request.Values is null || request.Values.Count > 80 || request.Values.Any(p => !layout.Inputs.Any(f => f.Key.Equals(p.Key, StringComparison.OrdinalIgnoreCase)) || p.Value is null || p.Value.Length > 4000))
            throw new InvalidOperationException("Only the configured document fields can be edited.");
        var before = ReadValues(row.ValuesJson);
        foreach (var key in new[] { "candidateName", "candidateEmail", "candidatePhone", "candidateCode" })
            if (request.Values.TryGetValue(key, out var changed) && before.GetValueOrDefault(key, "") != changed) throw new InvalidOperationException("Use Edit candidate to change identity/contact details. Document editing cannot change the linked profile identity.");
        var values = new Dictionary<string, string>(before, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in request.Values)
            if (!row.WorkLocationId.HasValue || !LocationInputKeys.Contains(key)) values[key] = value.Trim();
        var next = SerializeValues(values);
        await using var tx = await db.BeginTransactionAsync();
        var locked = await db.QuerySingleOrDefaultAsync<RosterStored>("SELECT WorkLocationId,CAST(ValuesJson AS CHAR) ValuesJson FROM recruitment_document_roster WHERE Id=@Id AND ClientId=@ClientId FOR UPDATE", new { Id = id, ClientId = clientId }, tx);
        var current = locked?.ValuesJson;
        if (current is null || RowRevision(current, locked!.WorkLocationId) != request.Revision) throw new InvalidOperationException("This row changed after it was opened. Refresh and review the current details.");
        await db.ExecuteAsync("UPDATE recruitment_document_roster SET ValuesJson=@Values,UpdatedByUserId=@UserId,UpdatedAt=CURRENT_TIMESTAMP(6) WHERE Id=@Id AND ClientId=@ClientId", new { Id = id, ClientId = clientId, Values = next, UserId = user.Id }, tx);
        await db.ExecuteAsync("INSERT INTO recruitment_document_roster_audit(RosterId,ClientId,Action,OldValuesJson,NewValuesJson,ActorUserId) VALUES(@Id,@ClientId,'Edit inputs',@Old,@New,@UserId)", new { Id = id, ClientId = clientId, Old = current, New = next, UserId = user.Id }, tx);
        await tx.CommitAsync();
    }

    public async Task<(byte[] Bytes, string FileName)> GenerateAsync(int clientId, long id, AuthUser user)
    {
        Access(user, clientId); await using var db = Db(); await db.OpenAsync(); await EnsureAsync(db);
        var row = await db.QuerySingleOrDefaultAsync<RecruitmentDocumentRosterRow>("""
SELECT r.*,CAST(r.ValuesJson AS CHAR) ValuesJson,c.CandidateCode,TRIM(CONCAT(c.FirstName,' ',c.LastName)) CandidateName,c.Email,c.Phone
FROM recruitment_document_roster r JOIN recruitment_candidates c ON c.Id=r.CandidateId AND c.ClientId=r.ClientId AND c.ProfileStatus<>'Archived'
WHERE r.Id=@Id AND r.ClientId=@ClientId
""", new { Id = id, ClientId = clientId }) ?? throw new InvalidOperationException("Employee list row was not found or the linked profile is archived.");
        var template = await TemplateAsync(db, clientId, row.TemplateId);
        await RequireLocationAsync(db, user, clientId, row.WorkLocationId);
        var clientName = await db.ExecuteScalarAsync<string>("SELECT Name FROM clients WHERE Id=@ClientId", new { ClientId = clientId }) ?? "Client";
        var organization = await db.QueryFirstOrDefaultAsync<Organization>("SELECT * FROM organizations ORDER BY Id LIMIT 1");
        var values = ReadValues(row.ValuesJson);
        if (row.WorkLocationId.HasValue) ApplyLocationValues(values, await ActiveLocationAsync(db, clientId, row.WorkLocationId.Value));
        values["candidateName"] = row.CandidateName; values["candidateEmail"] = row.Email; values["candidatePhone"] = row.Phone;
        values["candidateCode"] = row.CandidateCode; values["clientName"] = clientName; values["organizationName"] = organization?.Name ?? "";
        // Current-date unsigned draft; source joining/contract dates are retained.
        values["offerDate"] = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow, OperatingSystem.IsWindows() ? "India Standard Time" : "Asia/Kolkata").ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        values["offerNumber"] = "DRAFT/" + row.CandidateCode + "/" + id.ToString(CultureInfo.InvariantCulture);
        byte[]? logo = null;
        var data = organization?.LogoDataUrl ?? "";
        if (data.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) && data.Contains(";base64,", StringComparison.Ordinal))
        { try { logo = Convert.FromBase64String(data[(data.IndexOf(',') + 1)..]); } catch (FormatException) { } }
        var result = pdf.Create(template.BodyTemplate, values, logo, draft: true);
        if (result.Bytes is null) throw new InvalidOperationException(result.Error);
        // A generation receipt contains no rendered salary/contact fields and
        // grants no approval, signature, employee conversion or email permission.
        await db.ExecuteAsync("INSERT INTO recruitment_document_roster_audit(RosterId,ClientId,Action,NewValuesJson,ActorUserId) VALUES(@Id,@ClientId,'Draft generated',@Receipt,@UserId)", new { Id = id, ClientId = clientId, Receipt = JsonSerializer.Serialize(new { TemplateId = template.Id, TemplateHash = Hash(template.BodyTemplate), InputsHash = Hash(row.ValuesJson), PdfHash = Convert.ToHexString(SHA256.HashData(result.Bytes)), Draft = true }, Json), UserId = user.Id });
        return (result.Bytes, SafeFileName(clientName + "-" + row.CandidateCode + "-" + row.CandidateName + "-draft-offer.pdf"));
    }
    internal static string SafeFileName(string text)
    {
        var name = RegexFileName.Replace(text, "-"); var extension = Path.GetExtension(name);
        if (extension.Length > 10) extension = "";
        return name.Length <= 180 ? name : name[..(180 - extension.Length)] + extension;
    }
    private static readonly System.Text.RegularExpressions.Regex RegexFileName = new(@"[<>:""/\\|?*\p{Cc}]", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    public async Task<(byte[] Bytes, string FileName)> GenerateZipAsync(int clientId, List<long> ids, AuthUser user)
    {
        Access(user, clientId);
        if (ids.Count is < 1 or > 200 || ids.Distinct().Count() != ids.Count) throw new InvalidOperationException("Select 1 to 200 distinct draft-ready rows.");
        var rows = await RowsAsync(clientId, user);
        if (ids.Any(id => !rows.Any(r => r.Id == id && r.DocumentIssues.Count == 0))) throw new InvalidOperationException("Complete the document details for every selected row before downloading drafts.");
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            foreach (var id in ids)
            {
                var generated = await GenerateAsync(clientId, id, user);
                if (stream.Length + generated.Bytes.Length > 32 * 1024 * 1024) throw new InvalidOperationException("Download fewer draft letters at a time (32 MB limit).");
                var entry = zip.CreateEntry(id + "-" + generated.FileName, CompressionLevel.Fastest); using var output = entry.Open(); output.Write(generated.Bytes);
            }
        await using var db = Db(); await db.OpenAsync();
        var clientName = await db.ExecuteScalarAsync<string>("SELECT Name FROM clients WHERE Id=@ClientId", new { ClientId = clientId }) ?? "Client";
        return (stream.ToArray(), SafeFileName(clientName + "-draft-offers.zip"));
    }
}
