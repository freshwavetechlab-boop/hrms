using System.Globalization;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.AspNetCore.DataProtection;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public sealed partial class RecruitmentDocumentCollectionRepository(
    IConfiguration configuration, AttachmentRepository attachments, IDataProtectionProvider dataProtectionProvider)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IDataProtector tokenProtector = dataProtectionProvider.CreateProtector("Payroll.API.RecruitmentDocumentCollectionToken.v1");
    internal const long MaximumSubmissionBytes = 100L * 1024 * 1024;
    private MySqlConnection Db() => new(configuration.GetConnectionString("Default"));

    // Keep DDL outside the mail transaction: MySQL DDL implicitly commits transactions.
    public static Task EnsureSchemaAsync(MySqlConnection db) => db.ExecuteAsync(@"
CREATE TABLE IF NOT EXISTS recruitment_document_collection_requests (
 Id BIGINT PRIMARY KEY AUTO_INCREMENT,ClientId INT NOT NULL,CandidateId BIGINT NOT NULL,
 RosterId BIGINT NULL,OfferId BIGINT NULL,ApplicationId BIGINT NULL,
 RecipientHash CHAR(64) NOT NULL,TokenHash CHAR(64) NOT NULL,TokenCipherText TEXT NOT NULL,
 Status VARCHAR(30) NOT NULL DEFAULT 'Open',RequirementsJson JSON NOT NULL,
 ExpiresAtUtc DATETIME(6) NOT NULL,CreatedByUserId INT NOT NULL,CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
 UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),CompletedAtUtc DATETIME(6) NULL,
 UNIQUE KEY UX_document_collection_candidate(ClientId,CandidateId),UNIQUE KEY UX_document_collection_token(TokenHash)
);
CREATE TABLE IF NOT EXISTS recruitment_document_collection_files (
 RequestId BIGINT NOT NULL,AttachmentId BIGINT NOT NULL,FieldConfigurationId BIGINT NOT NULL,
 PRIMARY KEY(RequestId,AttachmentId),UNIQUE KEY UX_document_collection_attachment(AttachmentId)
);");

    public async Task<string> EnsureLinkAsync(MySqlConnection db, MySqlTransaction tx,
        int clientId, long candidateId, string recipientEmail, int actorUserId,
        long? rosterId = null, long? offerId = null, long? applicationId = null,
        IReadOnlyList<RecruitmentRequestedDocument>? documentRequests = null)
    {
        if (clientId <= 0 || candidateId <= 0 || actorUserId <= 0 || !IsEmail(recipientEmail))
            throw new RecruitmentDocumentCollectionValidationException("A valid offer recipient is required for document collection.");
        // Candidate lock serializes first-request creation as well as repeat mail attempts.
        var found = await db.ExecuteScalarAsync<long?>(@"SELECT c.Id FROM recruitment_candidates c
WHERE c.Id=@CandidateId AND (c.ClientId=@ClientId OR EXISTS(SELECT 1 FROM recruitment_candidate_applications a
WHERE a.CandidateId=c.Id AND a.ClientId=@ClientId)) FOR UPDATE", new { CandidateId = candidateId, ClientId = clientId }, tx);
        if (found is null) throw new UnauthorizedAccessException("The offer recipient does not belong to this client.");
        if (rosterId.HasValue && await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_document_roster WHERE Id=@Id AND ClientId=@ClientId AND CandidateId=@CandidateId", new { Id = rosterId.Value, ClientId = clientId, CandidateId = candidateId }, tx) != 1)
            throw new UnauthorizedAccessException("The employee list record does not match the offer recipient.");
        if (offerId.HasValue && await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM recruitment_offers o JOIN recruitment_candidate_applications a ON a.Id=o.ApplicationId
WHERE o.Id=@Id AND o.ClientId=@ClientId AND a.ClientId=@ClientId AND a.CandidateId=@CandidateId", new { Id = offerId.Value, ClientId = clientId, CandidateId = candidateId }, tx) != 1)
            throw new UnauthorizedAccessException("The offer does not match the recipient.");
        if (applicationId.HasValue && await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_candidate_applications WHERE Id=@Id AND ClientId=@ClientId AND CandidateId=@CandidateId", new { Id = applicationId.Value, ClientId = clientId, CandidateId = candidateId }, tx) != 1)
            throw new UnauthorizedAccessException("The hiring application does not match the recipient.");
        var existing = await db.QuerySingleOrDefaultAsync<RequestRow>(@"SELECT * FROM recruitment_document_collection_requests
WHERE ClientId=@ClientId AND CandidateId=@CandidateId FOR UPDATE", new { ClientId = clientId, CandidateId = candidateId }, tx);
        if (existing?.Status == "Completed") return tokenProtector.Unprotect(existing.TokenCipherText);

        var available = await AttachmentRepository.LoadJoiningDocumentConfigurationsAsync(db, tx, clientId, includeAll: existing is not null);
        var previousRequirements = existing is null ? null : ReadRequirements(existing.RequirementsJson);
        var selections = documentRequests ?? previousRequirements?.Select(r => new RecruitmentRequestedDocument { FieldConfigurationId = r.FieldConfigurationId, Required = r.Required }).ToArray()
            ?? available.Select(f => new RecruitmentRequestedDocument { FieldConfigurationId = f.Id, Required = true }).ToArray();
        ValidateSelections(selections, available.Select(f => f.Id));
        if (previousRequirements is not null && !previousRequirements.Select(r => (r.FieldConfigurationId, r.Required)).OrderBy(r => r.FieldConfigurationId)
            .SequenceEqual(selections.Select(s => (s.FieldConfigurationId, s.Required)).OrderBy(s => s.FieldConfigurationId)))
            throw new RecruitmentDocumentCollectionValidationException("This recipient already has an open document request. Keep the originally requested documents and mandatory settings when resending the offer.");
        var snapshots = selections.Select(s =>
        {
            var field = available.Single(f => f.Id == s.FieldConfigurationId);
            return new RequirementSnapshot { FieldConfigurationId = field.Id, Required = s.Required, Label = AttachmentRepository.JoiningDocumentDisplayLabel(field) };
        }).ToArray();
        var recipientHash = EmailHash(recipientEmail);
        var reuse = existing is not null && existing.RecipientHash == recipientHash && existing.ExpiresAtUtc > DateTime.UtcNow;
        var token = reuse ? tokenProtector.Unprotect(existing!.TokenCipherText) : NewToken();
        var expiry = DateTime.UtcNow.AddDays(Math.Clamp(configuration.GetValue("PublicPortal:DocumentRequestValidityDays", 30), 1, 365));
        await db.ExecuteAsync(@"INSERT INTO recruitment_document_collection_requests
(ClientId,CandidateId,RosterId,OfferId,ApplicationId,RecipientHash,TokenHash,TokenCipherText,RequirementsJson,ExpiresAtUtc,CreatedByUserId)
VALUES(@ClientId,@CandidateId,@RosterId,@OfferId,@ApplicationId,@RecipientHash,@TokenHash,@Cipher,@Requirements,@Expiry,@Actor)
ON DUPLICATE KEY UPDATE RosterId=COALESCE(VALUES(RosterId),RosterId),OfferId=COALESCE(VALUES(OfferId),OfferId),
ApplicationId=COALESCE(VALUES(ApplicationId),ApplicationId),RecipientHash=VALUES(RecipientHash),TokenHash=VALUES(TokenHash),
TokenCipherText=VALUES(TokenCipherText),RequirementsJson=VALUES(RequirementsJson),ExpiresAtUtc=VALUES(ExpiresAtUtc),UpdatedAtUtc=UTC_TIMESTAMP(6)",
            new { ClientId = clientId, CandidateId = candidateId, RosterId = rosterId, OfferId = offerId, ApplicationId = applicationId,
                RecipientHash = recipientHash, TokenHash = Hash(token), Cipher = tokenProtector.Protect(token), Requirements = existing?.RequirementsJson ?? JsonSerializer.Serialize(snapshots, Json), Expiry = expiry, Actor = actorUserId }, tx);
        return token;
    }

    public async Task<PublicRecruitmentDocumentRequest?> GetPublicAsync(string token)
    {
        if (!IsToken(token)) return null;
        await using var db = Db(); await db.OpenAsync();
        var row = await db.QuerySingleOrDefaultAsync<RequestRow>("SELECT * FROM recruitment_document_collection_requests WHERE TokenHash=@Hash", new { Hash = Hash(token) });
        if (row is null) return null;
        var status = PublicStatus(row.Status, row.ExpiresAtUtc, DateTime.UtcNow);
        if (status != "Open") return State(status);
        if (!await CandidateInRequestScopeAsync(db, null, row)) return State("Unavailable");
        var requirements = ReadRequirements(row.RequirementsJson);
        var fields = await AttachmentRepository.LoadJoiningDocumentConfigurationsAsync(db, null, row.ClientId, includeAll: true);
        if (requirements.Count == 0 || requirements.Any(r => !fields.Any(f => f.Id == r.FieldConfigurationId))) return State("Unavailable");
        return new PublicRecruitmentDocumentRequest { ExpiresAtUtc = row.ExpiresAtUtc,
            Documents = requirements.Select(r => PublicDocument(r, fields.Single(f => f.Id == r.FieldConfigurationId))).ToArray() };
    }

    public async Task<RecruitmentDocumentCollectionResult?> SubmitAsync(string token, string email,
        PublicRecruitmentDocumentMetadata metadata, IFormFileCollection files, string ipAddress, string userAgent, CancellationToken cancellationToken)
    {
        if (!IsToken(token)) return null;
        await using var db = Db(); await db.OpenAsync(cancellationToken);
        await using var tx = await db.BeginTransactionAsync(cancellationToken);
        AttachmentRepository.DocumentCollectionBatch? batch = null;
        var committed = false;
        try
        {
            var initial = await db.QuerySingleOrDefaultAsync<RequestRow>("SELECT * FROM recruitment_document_collection_requests WHERE TokenHash=@Hash", new { Hash = Hash(token) }, tx);
            if (initial is null) return null;
            // Match the candidate -> request lock order used by outgoing mail.
            var candidate = await db.ExecuteScalarAsync<long?>("SELECT Id FROM recruitment_candidates WHERE Id=@Id FOR UPDATE", new { Id = initial.CandidateId }, tx);
            var row = await db.QuerySingleOrDefaultAsync<RequestRow>("SELECT * FROM recruitment_document_collection_requests WHERE Id=@Id AND TokenHash=@Hash FOR UPDATE", new { initial.Id, Hash = Hash(token) }, tx);
            if (row is null) return null;
            var state = PublicStatus(row.Status, row.ExpiresAtUtc, DateTime.UtcNow);
            if (state == "Completed") return Completed(true);
            if (state != "Open") throw new RecruitmentDocumentCollectionValidationException("This link has expired or is unavailable. Please contact HR.");
            if (candidate is null || !await CandidateInRequestScopeAsync(db, tx, row))
                throw new RecruitmentDocumentCollectionValidationException("Document collection is unavailable. Please contact HR.");
            if (!IsEmail(email) || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(row.RecipientHash), Convert.FromHexString(EmailHash(email))))
                throw new RecruitmentDocumentCollectionValidationException("Enter the email address that received this offer letter.");
            var requirements = ReadRequirements(row.RequirementsJson);
            var fields = await AttachmentRepository.LoadJoiningDocumentConfigurationsAsync(db, tx, row.ClientId, includeAll: true);
            if (requirements.Count == 0 || requirements.Any(r => !fields.Any(f => f.Id == r.FieldConfigurationId)))
                throw new RecruitmentDocumentCollectionValidationException("Document collection is unavailable. Please contact HR.");
            ValidateSubmission(requirements.Select(r => (Id: r.FieldConfigurationId.ToString(CultureInfo.InvariantCulture), r.Required,
                Minimum: Math.Max(1, fields.Single(f => f.Id == r.FieldConfigurationId).MinimumFileCount),
                Maximum: fields.Single(f => f.Id == r.FieldConfigurationId).AllowMultiple ? fields.Single(f => f.Id == r.FieldConfigurationId).MaximumFileCount : 1)).ToArray(), metadata, files);
            var uploads = files.Select(file =>
            {
                var id = file.Name[6..];
                var field = fields.Single(f => f.Id.ToString(CultureInfo.InvariantCulture) == id);
                var item = metadata.Documents.SingleOrDefault(d => d.Id == id);
                return new AttachmentRepository.DocumentCollectionUpload(field, new AttachmentUploadMetadata
                { FieldConfigurationId = field.Id, EntityType = "CANDIDATE", EntityId = row.CandidateId,
                    DocumentNumber = item?.DocumentNumber?.Trim() ?? "", IssueDate = item?.IssueDate, ExpiryDate = item?.ExpiryDate }, file);
            }).ToArray();
            batch = await attachments.StoreDocumentCollectionAsync(db, tx, row.ClientId, row.CandidateId, uploads, ipAddress, userAgent, cancellationToken);
            foreach (var stored in batch.Attachments)
                await db.ExecuteAsync("INSERT INTO recruitment_document_collection_files(RequestId,AttachmentId,FieldConfigurationId) VALUES(@RequestId,@AttachmentId,@FieldId)",
                    new { RequestId = row.Id, AttachmentId = stored.AttachmentId, FieldId = stored.FieldConfigurationId }, tx);
            await db.ExecuteAsync("UPDATE recruitment_document_collection_requests SET Status='Completed',CompletedAtUtc=UTC_TIMESTAMP(6),UpdatedAtUtc=UTC_TIMESTAMP(6) WHERE Id=@Id AND Status='Open'", new { row.Id }, tx);
            var activityMetadata = JsonSerializer.Serialize(new { requestId = row.Id, attachmentCount = batch.Attachments.Count }, Json);
            await db.ExecuteAsync(@"INSERT INTO person_activity_events
(ClientId,CandidateId,EmployeeId,ModuleCode,EventType,EventTitle,EventSummary,ResourceType,ResourceId,ActorUserId,Visibility,IsSensitive,MetadataJson,OccurredAt)
SELECT @ClientId,c.Id,c.EmployeeId,'RECRUITMENT','OFFER_DOCUMENTS_SUBMITTED','Candidate submitted joining documents',
'External document request completed','RecruitmentDocumentCollection',@ResourceId,NULL,'HR',TRUE,@Metadata,UTC_TIMESTAMP()
FROM recruitment_candidates c WHERE c.Id=@CandidateId;
INSERT INTO recruitment_audit(EntityType,EntityId,Action,NewValueJson,ChangedByUserId)
VALUES('RecruitmentDocumentCollection',@RequestId,'Candidate submitted documents',@Metadata,0)",
                new { row.ClientId, row.CandidateId, RequestId = row.Id, ResourceId = row.Id.ToString(CultureInfo.InvariantCulture), Metadata = activityMetadata }, tx);
            await tx.CommitAsync(cancellationToken);
            committed = true;
            return Completed(false);
        }
        finally
        {
            if (!committed)
            {
                await tx.RollbackAsync(CancellationToken.None);
                if (batch is not null) await attachments.DeleteDocumentCollectionBlobsAsync(batch);
            }
        }
    }

    internal static void ValidateSelections(IReadOnlyList<RecruitmentRequestedDocument> selections, IEnumerable<long> available)
    {
        var allowed = available.ToHashSet();
        if (selections.Count == 0) throw new RecruitmentDocumentCollectionValidationException("Select at least one requested document.");
        if (selections.Count > 64 || selections.Any(s => s is null) || selections.Select(s => s.FieldConfigurationId).Distinct().Count() != selections.Count || selections.Any(s => !allowed.Contains(s.FieldConfigurationId)))
            throw new RecruitmentDocumentCollectionValidationException("One or more requested documents are unavailable. Refresh the document list.");
    }

    internal static void ValidateSubmission(IReadOnlyList<(string Id, bool Required, int Minimum, int Maximum)> requirements,
        PublicRecruitmentDocumentMetadata metadata, IFormFileCollection files)
    {
        var ids = requirements.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        if (files.Count > 64 || files.Sum(f => f.Length) > MaximumSubmissionBytes)
            throw new RecruitmentDocumentCollectionValidationException("The submitted documents exceed the total upload limit of 100 MB.");
        if (metadata.Documents is null || metadata.Documents.Count > 64 || metadata.Documents.Any(d => d is null || !ids.Contains(d.Id) || (d.DocumentNumber?.Length ?? 0) > 180)
            || metadata.Documents.Select(d => d.Id).Distinct(StringComparer.Ordinal).Count() != metadata.Documents.Count
            || files.Any(f => !f.Name.StartsWith("files-", StringComparison.Ordinal) || !ids.Contains(f.Name[6..])))
            throw new RecruitmentDocumentCollectionValidationException("The submitted document fields do not match this request.");
        foreach (var requirement in requirements)
        {
            var count = files.Count(f => f.Name == "files-" + requirement.Id);
            if (count < (requirement.Required ? requirement.Minimum : 0) || count > requirement.Maximum)
                throw new RecruitmentDocumentCollectionValidationException("Upload all mandatory documents within the allowed file counts.");
        }
        if (files.Count == 0) throw new RecruitmentDocumentCollectionValidationException("Select at least one document to submit.");
    }

    internal static string PublicStatus(string status, DateTime expires, DateTime now) => status == "Completed" ? "Completed" : status != "Open" ? "Unavailable" : expires <= now ? "Expired" : "Open";
    internal static string EmailHash(string email) => Hash(email.Trim().ToLowerInvariant());
    internal static string PublicLabel(string value, string clientName)
    {
        var label = value ?? "";
        if (!string.IsNullOrWhiteSpace(clientName)) label = Regex.Replace(label, Regex.Escape(clientName), "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        // Legacy fields sometimes use a client acronym rather than the full client name.
        foreach (var word in (clientName ?? "").Split([' ', '-', '/', ','], StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 3))
            label = Regex.Replace(label, @"\b" + Regex.Escape(word) + @"\b", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        label = Regex.Replace(label, @"\s+", " ").Trim(' ', '-', '/', ':');
        return string.IsNullOrEmpty(label) ? "Document" : label;
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static async Task<bool> CandidateInRequestScopeAsync(MySqlConnection db, MySqlTransaction? tx, RequestRow row) =>
        await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM recruitment_candidates c WHERE c.Id=@CandidateId
AND (c.ClientId=@ClientId OR EXISTS(SELECT 1 FROM recruitment_candidate_applications a WHERE a.CandidateId=c.Id AND a.ClientId=@ClientId))",
            new { row.CandidateId, row.ClientId }, tx) == 1;
    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static bool IsToken(string value) => !string.IsNullOrEmpty(value) && Regex.IsMatch(value, "^[A-Za-z0-9_-]{43}$", RegexOptions.CultureInvariant);
    private static bool IsEmail(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 254 && MailAddress.TryCreate(value.Trim(), out var parsed) && string.Equals(parsed.Address, value.Trim(), StringComparison.OrdinalIgnoreCase);
    private static RecruitmentDocumentCollectionResult Completed(bool already) => new("Completed", already, already ? "Already submitted." : "Your documents were submitted successfully.");
    private static PublicRecruitmentDocumentRequest State(string status) => new() { Status = status, Message = status == "Completed" ? "Already submitted." : status == "Expired" ? "This link has expired. Please contact HR." : "Document collection is unavailable. Please contact HR." };
    private static List<RequirementSnapshot> ReadRequirements(string json) => JsonSerializer.Deserialize<List<RequirementSnapshot>>(json, Json) ?? [];
    private static PublicRecruitmentRequestedDocument PublicDocument(RequirementSnapshot r, AttachmentFieldConfiguration f) => new()
    {
        Id = f.Id.ToString(CultureInfo.InvariantCulture), Label = r.Label, Required = r.Required,
        MinimumFileCount = r.Required ? Math.Max(1, f.MinimumFileCount) : 0, MaximumFileCount = f.AllowMultiple ? f.MaximumFileCount : 1,
        MaximumFileSizeBytes = f.MaximumFileSizeBytes, AllowedExtensions = JsonSerializer.Deserialize<string[]>(f.AllowedExtensionsJson) ?? [],
        RequiresDocumentNumber = f.RequiresDocumentNumber, RequiresIssueDate = f.RequiresIssueDate, RequiresExpiryDate = f.RequiresExpiryDate
    };
    private sealed class RequirementSnapshot
    {
        public long FieldConfigurationId { get; set; }
        public bool Required { get; set; }
        public string Label { get; set; } = "Document";
    }
    private sealed class RequestRow
    {
        public long Id { get; set; }
        public int ClientId { get; set; }
        public long CandidateId { get; set; }
        public string RecipientHash { get; set; } = "";
        public string TokenCipherText { get; set; } = "";
        public string Status { get; set; } = "";
        public string RequirementsJson { get; set; } = "[]";
        public DateTime ExpiresAtUtc { get; set; }
    }
}
