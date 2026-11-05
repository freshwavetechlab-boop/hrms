using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class RecruitmentDocumentRepository
{
    private static void IntakeAccess(AuthUser user)
    {
        if (!RecruitmentPermissions.Has(user, "recruitment.candidate.manage")) throw new UnauthorizedAccessException();
    }
    private static void OfferAccess(AuthUser user, int clientId)
    {
        Access(user, clientId);
        if (!RecruitmentPermissions.Has(user, "recruitment.offer.manage")) throw new UnauthorizedAccessException();
    }
    private static void IssueAccess(AuthUser user, int clientId)
    {
        Access(user, clientId);
        if (!RecruitmentPermissions.Has(user, "recruitment.offer.issue")) throw new UnauthorizedAccessException();
    }
    private static readonly HashSet<string> LocationInputKeys = new(new[] { "jobLocation", "workLocation", "workLocationName", "instituteName", "institutionName", "institutionShortName", "instituteAddress", "institutionAddress", "workLocationAddress", "workLocationCity", "workLocationState", "city", "state", "institutionLocation" }, StringComparer.OrdinalIgnoreCase);
    internal static string RowRevision(string valuesJson, int? workLocationId) => Hash(valuesJson + "\nworkLocation:" + (workLocationId?.ToString(CultureInfo.InvariantCulture) ?? "null"));
    internal static void ApplyLocationValues(Dictionary<string, string> values, WorkLocation location)
    {
        foreach (var key in new[] { "jobLocation", "workLocation", "workLocationName", "instituteName", "institutionName" }) values[key] = location.Name;
        foreach (var key in new[] { "instituteAddress", "institutionAddress", "workLocationAddress" }) values[key] = location.Address;
        values["city"] = location.City; values["state"] = location.State;
        values["workLocationCity"] = location.City; values["workLocationState"] = location.State;
        var shortName = location.Name;
        if (!string.IsNullOrWhiteSpace(location.City))
            foreach (var separator in new[] { " - ", " \u2013 ", ", " })
            {
                var suffix = separator + location.City;
                if (shortName.Length > suffix.Length && shortName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                { shortName = shortName[..^suffix.Length]; break; }
            }
        values["institutionShortName"] = shortName;
        values["institutionLocation"] = !string.IsNullOrWhiteSpace(location.City) ? location.City : !string.IsNullOrWhiteSpace(location.State) ? location.State : location.Address;
    }
    private static bool CanSeeLocation(AuthUser user, int? locationId, List<WorkLocation> locations) =>
        !RecruitmentAccessScope.IsRestricted(user) || locationId.HasValue && locations.Any(location => location.Id == locationId);
    private static async Task RequireLocationAsync(MySqlConnection db, AuthUser user, int clientId, int? locationId)
    {
        if (!await RecruitmentAccessScope.CanAccessWorkLocationIdAsync(db, user, clientId, locationId)) throw new UnauthorizedAccessException("The employee list row is outside your permitted work locations.");
    }
    private static async Task<WorkLocation> ActiveLocationAsync(MySqlConnection db, int clientId, int locationId, MySqlTransaction? tx = null) =>
        await db.QuerySingleOrDefaultAsync<WorkLocation>("SELECT * FROM worklocations WHERE Id=@Id AND ClientId=@ClientId AND IsActive=TRUE" + (tx is null ? "" : " LOCK IN SHARE MODE"), new { Id = locationId, ClientId = clientId }, tx)
        ?? throw new InvalidOperationException("Select an existing active work location belonging to this client.");

    private static async Task EnsureReleaseSchemaAsync(MySqlConnection db)
    {
        // The roster and asset metadata are loaded together in the UI. Serialize
        // the additive schema check so their first requests cannot race ALTERs.
        if (await db.ExecuteScalarAsync<int>("SELECT GET_LOCK('frevo:document-roster-schema',10)") != 1) throw new InvalidOperationException("The document workspace is being initialized. Retry shortly.");
        try { await EnsureReleaseSchemaCoreAsync(db); }
        finally { await db.ExecuteAsync("SELECT RELEASE_LOCK('frevo:document-roster-schema')"); }
    }
    private static async Task EnsureReleaseSchemaCoreAsync(MySqlConnection db)
    {
        foreach (var column in new[] { (Name: "WorkLocationId", Sql: "INT NULL"), (Name: "OfferNumber", Sql: "VARCHAR(160) NOT NULL DEFAULT ''") })
            if (await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='recruitment_document_roster' AND column_name=@Name", new { column.Name }) == 0)
                await db.ExecuteAsync($"ALTER TABLE recruitment_document_roster ADD COLUMN {column.Name} {column.Sql}");
        if (await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM information_schema.referential_constraints WHERE constraint_schema=DATABASE() AND constraint_name='FK_document_roster_work_location'") == 0)
            await db.ExecuteAsync("ALTER TABLE recruitment_document_roster ADD CONSTRAINT FK_document_roster_work_location FOREIGN KEY (WorkLocationId) REFERENCES worklocations(Id) ON DELETE RESTRICT");
        await db.ExecuteAsync("""
CREATE TABLE IF NOT EXISTS recruitment_document_signing_assets (
 ClientId INT PRIMARY KEY,FileName VARCHAR(180) NOT NULL,ContentType VARCHAR(40) NOT NULL,
 Width INT NOT NULL,Height INT NOT NULL,Sha256 CHAR(64) NOT NULL,ImageBytes MEDIUMBLOB NOT NULL,
 UpdatedByUserId INT NOT NULL,UpdatedAt DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
 CONSTRAINT FK_document_signing_client FOREIGN KEY (ClientId) REFERENCES clients(Id) ON DELETE RESTRICT);
CREATE TABLE IF NOT EXISTS recruitment_document_roster_releases (
 Id BIGINT PRIMARY KEY AUTO_INCREMENT,RosterId BIGINT NOT NULL,ClientId INT NOT NULL,OfferNumber VARCHAR(160) NOT NULL,
 RevisionKey CHAR(64) NOT NULL,TemplateHash CHAR(64) NOT NULL,InputsHash CHAR(64) NOT NULL,LocationHash CHAR(64) NOT NULL,
 SignatureHash VARCHAR(64) NOT NULL,HasSignature BOOLEAN NOT NULL,PdfHash CHAR(64) NOT NULL,PdfBytes LONGBLOB NOT NULL,
 FileName VARCHAR(180) NOT NULL,ReleasedByUserId INT NOT NULL,ReleasedAt DATETIME(6) NOT NULL,
 UNIQUE KEY UX_document_roster_release(RosterId,RevisionKey),INDEX IX_document_roster_release_client(ClientId,RosterId),
 CONSTRAINT FK_document_roster_release_row FOREIGN KEY (RosterId) REFERENCES recruitment_document_roster(Id) ON DELETE RESTRICT);
""");
    }
    public async Task<List<WorkLocation>> WorkLocationsAsync(int clientId, AuthUser user)
    {
        Access(user, clientId); await using var db = Db(); await db.OpenAsync();
        return await RecruitmentAccessScope.WorkLocationsAsync(db, user, clientId);
    }
    public async Task AssignWorkLocationAsync(int clientId, RecruitmentRosterWorkLocationSelection request, AuthUser user)
    {
        OfferAccess(user, clientId); ValidateIds(request.Ids);
        if (request.WorkLocationId <= 0 || request.Revisions is null || request.Revisions.Count > 200 || request.Ids.Any(id => !request.Revisions.TryGetValue(id.ToString(CultureInfo.InvariantCulture), out var revision) || revision is not { Length: 64 }))
            throw new InvalidOperationException("Refresh the employee list and select its current rows and an existing work location.");
        await using var db = Db(); await db.OpenAsync(); await EnsureAsync(db);
        var allowed = await RecruitmentAccessScope.WorkLocationsAsync(db, user, clientId);
        if (!allowed.Any(location => location.Id == request.WorkLocationId)) throw new UnauthorizedAccessException("The selected work location is outside your permitted client or locations.");
        await using var tx = await db.BeginTransactionAsync();
        await ActiveLocationAsync(db, clientId, request.WorkLocationId, tx);
        var rows = (await db.QueryAsync<RecruitmentDocumentRosterRow>("SELECT Id,WorkLocationId,CAST(ValuesJson AS CHAR) ValuesJson FROM recruitment_document_roster WHERE ClientId=@ClientId AND Id IN @Ids ORDER BY Id FOR UPDATE", new { ClientId = clientId, request.Ids }, tx)).ToList();
        if (rows.Count != request.Ids.Count || rows.Any(row => !CanSeeLocation(user, row.WorkLocationId, allowed))) throw new UnauthorizedAccessException();
        foreach (var row in rows)
        {
            if (RowRevision(row.ValuesJson, row.WorkLocationId) != request.Revisions[row.Id.ToString(CultureInfo.InvariantCulture)]) throw new InvalidOperationException("A selected row changed after it was loaded. Refresh before assigning work locations.");
            if (row.WorkLocationId == request.WorkLocationId) continue;
            await db.ExecuteAsync("UPDATE recruitment_document_roster SET WorkLocationId=@WorkLocationId,UpdatedByUserId=@UserId,UpdatedAt=CURRENT_TIMESTAMP(6) WHERE Id=@Id AND ClientId=@ClientId", new { request.WorkLocationId, UserId = user.Id, row.Id, ClientId = clientId }, tx);
            await db.ExecuteAsync("INSERT INTO recruitment_document_roster_audit(RosterId,ClientId,Action,OldValuesJson,NewValuesJson,ActorUserId) VALUES(@Id,@ClientId,'Assign work location',@Old,@New,@UserId)", new { row.Id, ClientId = clientId, Old = JsonSerializer.Serialize(new { row.WorkLocationId }), New = JsonSerializer.Serialize(new { request.WorkLocationId }), UserId = user.Id }, tx);
        }
        await tx.CommitAsync();
    }
    public async Task<RecruitmentSigningAssetMetadata> SigningAssetAsync(int clientId, AuthUser user)
    {
        Access(user, clientId);
        if (!RecruitmentPermissions.Has(user, "recruitment.offer.manage", "recruitment.offer.issue")) throw new UnauthorizedAccessException();
        await using var db = Db(); await db.OpenAsync(); await EnsureAsync(db);
        var metadata = await db.QuerySingleOrDefaultAsync<RecruitmentSigningAssetMetadata>("SELECT TRUE Available,FileName,ContentType,Width,Height,Sha256,UpdatedAt FROM recruitment_document_signing_assets WHERE ClientId=@ClientId", new { ClientId = clientId }) ?? new();
        if (metadata.UpdatedAt.HasValue) metadata.UpdatedAt = DateTime.SpecifyKind(metadata.UpdatedAt.Value, DateTimeKind.Utc);
        return metadata;
    }
    public async Task<RecruitmentSigningAssetMetadata> SaveSigningAssetAsync(int clientId, byte[] bytes, string fileName, AuthUser user)
    {
        OfferAccess(user, clientId); var image = DocumentSigningImage.Validate(bytes);
        var baseName = Path.GetFileNameWithoutExtension(Path.GetFileName(fileName.Replace('\\', '/')));
        if (string.IsNullOrWhiteSpace(baseName)) baseName = "signature-and-stamp";
        fileName = SafeFileName(baseName + (image.ContentType == "image/png" ? ".png" : ".jpg"));
        await using var db = Db(); await db.OpenAsync(); await EnsureAsync(db);
        await db.ExecuteAsync("""
INSERT INTO recruitment_document_signing_assets(ClientId,FileName,ContentType,Width,Height,Sha256,ImageBytes,UpdatedByUserId,UpdatedAt)
VALUES(@ClientId,@FileName,@ContentType,@Width,@Height,@Sha256,@Bytes,@UserId,UTC_TIMESTAMP(6))
ON DUPLICATE KEY UPDATE FileName=VALUES(FileName),ContentType=VALUES(ContentType),Width=VALUES(Width),Height=VALUES(Height),Sha256=VALUES(Sha256),ImageBytes=VALUES(ImageBytes),UpdatedByUserId=VALUES(UpdatedByUserId),UpdatedAt=UTC_TIMESTAMP(6)
""", new { ClientId = clientId, FileName = fileName, image.ContentType, image.Width, image.Height, Sha256 = BytesHash(image.Bytes), image.Bytes, UserId = user.Id });
        return await SigningAssetAsync(clientId, user);
    }
    public async Task DeleteSigningAssetAsync(int clientId, AuthUser user)
    {
        OfferAccess(user, clientId); await using var db = Db(); await db.OpenAsync(); await EnsureAsync(db);
        await db.ExecuteAsync("DELETE FROM recruitment_document_signing_assets WHERE ClientId=@ClientId", new { ClientId = clientId });
    }
    private static void ValidateIds(List<long>? ids)
    {
        if (ids is null || ids.Count is < 1 or > 200 || ids.Any(id => id <= 0) || ids.Distinct().Count() != ids.Count) throw new InvalidOperationException("Select 1 to 200 distinct employee list rows.");
    }
    private static string BytesHash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    internal static string NewOfferNumber(string clientCode, int year, long rowId)
    {
        var safeCode = System.Text.RegularExpressions.Regex.Replace(clientCode.ToUpperInvariant(), "[^A-Z0-9_-]", "");
        if (safeCode.Length == 0) throw new InvalidOperationException("Set the client code before releasing offer letters.");
        return $"GAD/HR/{safeCode}/{year}/{rowId:D6}";
    }
    public async Task<(byte[] Bytes, string FileName)> ReleaseAsync(int clientId, long id, bool applySignature, AuthUser user)
    {
        var results = await ReleaseDocumentsAsync(clientId, new() { Ids = [id], ApplySignature = applySignature }, user);
        return (results[0].Bytes, results[0].FileName);
    }
    public async Task<(byte[] Bytes, string FileName)> ReleaseZipAsync(int clientId, RecruitmentRosterReleaseSelection request, AuthUser user)
    {
        var results = await ReleaseDocumentsAsync(clientId, request, user);
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            foreach (var result in results)
            { using var output = zip.CreateEntry(result.RowId + "-" + result.FileName, CompressionLevel.Fastest).Open(); output.Write(result.Bytes); }
        return (stream.ToArray(), SafeFileName(results[0].ClientName + "-released-offers.zip"));
    }
    private async Task<List<ReleasedDocument>> ReleaseDocumentsAsync(int clientId, RecruitmentRosterReleaseSelection request, AuthUser user)
    {
        IssueAccess(user, clientId); ValidateIds(request.Ids);
        if (request.Revisions is { Count: > 200 }) throw new InvalidOperationException("Select fewer rows to release.");
        await using var db = Db(); await db.OpenAsync(); await EnsureAsync(db);
        var allowed = await RecruitmentAccessScope.WorkLocationsAsync(db, user, clientId);
        await using var tx = await db.BeginTransactionAsync();
        var rows = (await db.QueryAsync<RecruitmentDocumentRosterRow>("""
SELECT r.*,CAST(r.ValuesJson AS CHAR) ValuesJson,c.CandidateCode,TRIM(CONCAT(c.FirstName,' ',c.LastName)) CandidateName,c.Email,c.Phone
FROM recruitment_document_roster r JOIN recruitment_candidates c ON c.Id=r.CandidateId AND c.ClientId=r.ClientId AND c.ProfileStatus<>'Archived'
WHERE r.ClientId=@ClientId AND r.Id IN @Ids ORDER BY r.Id FOR UPDATE
""", new { ClientId = clientId, request.Ids }, tx)).ToList();
        if (rows.Count != request.Ids.Count || rows.Any(row => !CanSeeLocation(user, row.WorkLocationId, allowed))) throw new UnauthorizedAccessException("One or more selected employee rows are unavailable in your permitted locations.");
        if (rows.Any(row => row.WorkLocationId is null or <= 0)) throw new InvalidOperationException("Link an existing work location for every selected employee before releasing offer letters. Unlinked rows may still be downloaded as unsigned drafts.");
        var client = await db.QuerySingleAsync<Client>("SELECT * FROM clients WHERE Id=@ClientId AND IsActive=TRUE", new { ClientId = clientId }, tx);
        var organization = await db.QueryFirstOrDefaultAsync<Organization>("SELECT * FROM organizations ORDER BY Id LIMIT 1", transaction: tx);
        byte[]? logo = LogoBytes(organization?.LogoDataUrl);
        var asset = request.ApplySignature ? await db.QuerySingleOrDefaultAsync<SigningStored>("SELECT ImageBytes Bytes,Sha256 FROM recruitment_document_signing_assets WHERE ClientId=@ClientId FOR UPDATE", new { ClientId = clientId }, tx) : null;
        if (request.ApplySignature && asset is null) throw new InvalidOperationException("Upload this client's signature and stamp image before applying it, or turn Apply signature off.");
        if (asset is not null) DocumentSigningImage.Validate(asset.Bytes);
        var results = new List<ReleasedDocument>(); long size = 0;
        var now = DateTime.UtcNow; var india = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(new DateTimeOffset(now, TimeSpan.Zero), OperatingSystem.IsWindows() ? "India Standard Time" : "Asia/Kolkata");
        foreach (var row in rows)
        {
            if (request.Revisions is not null && (!request.Revisions.TryGetValue(row.Id.ToString(CultureInfo.InvariantCulture), out var expected) || expected != RowRevision(row.ValuesJson, row.WorkLocationId))) throw new InvalidOperationException("A selected row changed. Refresh and review before release.");
            var template = await TemplateAsync(db, clientId, row.TemplateId, tx);
            var values = ReadValues(row.ValuesJson);
            WorkLocation? location = row.WorkLocationId.HasValue ? await ActiveLocationAsync(db, clientId, row.WorkLocationId.Value, tx) : null;
            if (location is not null) ApplyLocationValues(values, location);
            var offerNumber = row.OfferNumber.Length > 0 ? row.OfferNumber : NewOfferNumber(client.Code, india.Year, row.Id);
            values["candidateName"] = row.CandidateName; values["candidateEmail"] = row.Email; values["candidatePhone"] = row.Phone; values["candidateCode"] = row.CandidateCode;
            values["clientName"] = client.Name; values["organizationName"] = organization?.Name ?? ""; values["offerNumber"] = offerNumber;
            values.Remove("offerDate"); values.Remove("date");
            var inputsHash = Hash(SerializeValues(values)); var templateHash = Hash(template.BodyTemplate);
            var locationHash = Hash(JsonSerializer.Serialize(location, Json)); var signatureHash = asset?.Sha256 ?? "";
            var revisionKey = Hash(string.Join("|", inputsHash, templateHash, locationHash, signatureHash, logo is null ? "" : BytesHash(logo)));
            var receipt = await db.QuerySingleOrDefaultAsync<ReleaseStored>("SELECT PdfBytes Bytes,FileName FROM recruitment_document_roster_releases WHERE RosterId=@Id AND ClientId=@ClientId AND RevisionKey=@RevisionKey", new { row.Id, ClientId = clientId, RevisionKey = revisionKey }, tx);
            if (receipt is null)
            {
                values["offerDate"] = india.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
                var rendered = pdf.Create(template.BodyTemplate, values, logo, draft: false, signatureAndStamp: asset?.Bytes);
                if (rendered.Bytes is null) throw new InvalidOperationException(rendered.Error);
                if (rendered.Bytes.Length > 10 * 1024 * 1024) throw new InvalidOperationException("The released PDF exceeds 10 MB. Reduce the signature image size.");
                receipt = new() { Bytes = rendered.Bytes, FileName = SafeFileName(client.Name + "-" + row.CandidateCode + "-" + row.CandidateName + "-offer.pdf") };
                await db.ExecuteAsync("""
INSERT INTO recruitment_document_roster_releases(RosterId,ClientId,OfferNumber,RevisionKey,TemplateHash,InputsHash,LocationHash,SignatureHash,HasSignature,PdfHash,PdfBytes,FileName,ReleasedByUserId,ReleasedAt)
VALUES(@Id,@ClientId,@OfferNumber,@RevisionKey,@TemplateHash,@InputsHash,@LocationHash,@SignatureHash,@HasSignature,@PdfHash,@Bytes,@FileName,@UserId,@Now)
""", new { row.Id, ClientId = clientId, OfferNumber = offerNumber, RevisionKey = revisionKey, TemplateHash = templateHash, InputsHash = inputsHash, LocationHash = locationHash, SignatureHash = signatureHash, HasSignature = asset is not null, PdfHash = BytesHash(receipt.Bytes), receipt.Bytes, receipt.FileName, UserId = user.Id, Now = now }, tx);
                await db.ExecuteAsync("UPDATE recruitment_document_roster SET OfferNumber=@OfferNumber WHERE Id=@Id AND ClientId=@ClientId AND OfferNumber=''", new { OfferNumber = offerNumber, row.Id, ClientId = clientId }, tx);
                await db.ExecuteAsync("INSERT INTO recruitment_document_roster_audit(RosterId,ClientId,Action,NewValuesJson,ActorUserId) VALUES(@Id,@ClientId,'Offer released',@Receipt,@UserId)", new { row.Id, ClientId = clientId, Receipt = JsonSerializer.Serialize(new { OfferNumber = offerNumber, RevisionKey = revisionKey, PdfHash = BytesHash(receipt.Bytes), SignatureHash = signatureHash, ReleasedAt = now }, Json), UserId = user.Id }, tx);
            }
            size += receipt.Bytes.Length; if (size > 32 * 1024 * 1024) throw new InvalidOperationException("Release fewer letters at a time (32 MB limit).");
            results.Add(new(row.Id, receipt.Bytes, receipt.FileName, client.Name));
        }
        await tx.CommitAsync(); return results;
    }
    private static byte[]? LogoBytes(string? data)
    {
        if (data?.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) == true && data.Contains(";base64,", StringComparison.Ordinal))
            try { return Convert.FromBase64String(data[(data.IndexOf(',') + 1)..]); } catch (FormatException) { }
        return null;
    }
    private sealed class SigningStored { public byte[] Bytes { get; set; } = []; public string Sha256 { get; set; } = ""; }
    private sealed class ReleaseStored { public byte[] Bytes { get; set; } = []; public string FileName { get; set; } = ""; }
    private sealed record ReleasedDocument(long RowId, byte[] Bytes, string FileName, string ClientName);
}
