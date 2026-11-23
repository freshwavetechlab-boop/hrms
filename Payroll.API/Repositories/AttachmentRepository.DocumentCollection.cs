using Dapper;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public partial class AttachmentRepository
{
    internal sealed record DocumentCollectionUpload(AttachmentFieldConfiguration Field, AttachmentUploadMetadata Metadata, IFormFile File);
    internal sealed record DocumentCollectionStored(long AttachmentId, long FieldConfigurationId);
    internal sealed class DocumentCollectionBatch
    {
        public List<DocumentCollectionStored> Attachments { get; } = [];
        public List<(AttachmentStorageServer Server, string Key)> Blobs { get; } = [];
    }

    // Only the document-request repository calls this after locking and verifying the bearer request.
    // Every entity_attachments/audit write shares its completion transaction. No public download grant is issued.
    internal async Task<DocumentCollectionBatch> StoreDocumentCollectionAsync(MySqlConnection db, MySqlTransaction tx,
        int clientId, long candidateId, IReadOnlyList<DocumentCollectionUpload> uploads,
        string ipAddress, string userAgent, CancellationToken cancellationToken)
    {
        var batch = new DocumentCollectionBatch();
        try
        {
            if (uploads.Count > 0)
            {
                var masterIds = (await db.QueryAsync<long>("SELECT DISTINCT attachment_attribute_id FROM attachment_field_configurations WHERE id IN @Ids ORDER BY attachment_attribute_id", new { Ids = uploads.Select(upload => upload.Field.Id).Distinct().ToArray() }, tx)).ToArray();
                if (masterIds.Length > 0)
                    await db.QueryAsync<long>("SELECT id FROM attachment_attributes WHERE id IN @Ids ORDER BY id LOCK IN SHARE MODE", new { Ids = masterIds }, tx);
            }
            var inspected = new List<(DocumentCollectionUpload Upload, FileInspection Inspection, string Extension)>();
            foreach (var upload in uploads)
            {
                if (upload.Metadata.EntityType != "CANDIDATE" || upload.Metadata.EntityId != candidateId
                    || upload.Field.ClientId != 0 && upload.Field.ClientId != clientId)
                    throw new RecruitmentDocumentCollectionValidationException("An attachment field is not applicable to this request.");
                var currentField = await db.QuerySingleOrDefaultAsync<AttachmentFieldConfiguration>(ConfigurationSelect + @"
WHERE f.id=@Id AND f.client_id IN(0,@ClientId) AND a.client_id IN(0,@ClientId)
AND f.is_active=TRUE AND a.is_active=TRUE
AND (f.effective_from_utc IS NULL OR f.effective_from_utc<=UTC_TIMESTAMP(6))
AND (f.effective_until_utc IS NULL OR f.effective_until_utc>=UTC_TIMESTAMP(6)) LOCK IN SHARE MODE",
                    new { upload.Field.Id, ClientId = clientId }, tx);
                if (currentField is null) throw new RecruitmentDocumentCollectionValidationException("Document configuration changed. Please refresh and try again.");
                var currentUpload = upload with { Field = currentField };
                var validation = ValidateMetadata(currentField, upload.Metadata, upload.File);
                if (validation is not null) throw new RecruitmentDocumentCollectionValidationException(validation);
                var inspection = await InspectFileAsync(upload.File, cancellationToken);
                if (!inspection.Ok) throw new RecruitmentDocumentCollectionValidationException(inspection.Error);
                var extension = Path.GetExtension(upload.File.FileName).TrimStart('.').ToLowerInvariant();
                if (!ReadJsonList(currentField.AllowedExtensionsJson).Contains(extension, StringComparer.OrdinalIgnoreCase)
                    || ReadJsonList(currentField.AllowedMimeTypesJson) is var mimeTypes && mimeTypes.Count > 0
                    && !mimeTypes.Contains(inspection.DetectedMimeType, StringComparer.OrdinalIgnoreCase))
                    throw new RecruitmentDocumentCollectionValidationException("This document file type is not allowed.");
                inspected.Add((currentUpload, inspection, extension));
            }
            foreach (var group in inspected.GroupBy(item => item.Upload.Field.Id))
            {
                var field = group.First().Upload.Field;
                var current = (await db.QueryAsync<EntityAttachment>(@"SELECT id Id,version_number VersionNumber,file_size_bytes FileSizeBytes
FROM entity_attachments WHERE client_id=@ClientId AND entity_type='CANDIDATE' AND entity_id=@CandidateId
AND field_configuration_id=@FieldId AND is_current=TRUE AND is_deleted=FALSE FOR UPDATE",
                    new { ClientId = clientId, CandidateId = candidateId, FieldId = field.Id }, tx)).ToList();
                if (!field.AllowMultiple && current.Count > 0 && !field.VersioningEnabled)
                    throw new RecruitmentDocumentCollectionValidationException("A requested document already exists and replacement is disabled. Please contact HR.");
                if (field.AllowMultiple && current.Count + group.Count() > field.MaximumFileCount)
                    throw new RecruitmentDocumentCollectionValidationException("A requested document exceeds its configured file count. Please contact HR.");
                var totalSize = (field.AllowMultiple ? current.Sum(a => a.FileSizeBytes) : 0) + group.Sum(item => item.Upload.File.Length);
                if (field.MaximumTotalSizeBytes.HasValue && totalSize > field.MaximumTotalSizeBytes.Value)
                    throw new RecruitmentDocumentCollectionValidationException($"Combined file size must not exceed {FormatBytes(field.MaximumTotalSizeBytes.Value)}.");
                var version = current.Count == 0 ? 1 : current.Max(a => a.VersionNumber) + 1;
                if (!field.AllowMultiple && current.Count > 0)
                    await db.ExecuteAsync("UPDATE entity_attachments SET is_current=FALSE WHERE id IN @Ids", new { Ids = current.Select(a => a.Id).ToArray() }, tx);
                foreach (var item in group)
                {
                    var file = item.Upload.File;
                    var metadata = item.Upload.Metadata;
                    var publicId = Guid.NewGuid();
                    var storedFileName = $"{Guid.NewGuid():N}.{item.Extension}";
                    var key = BuildStorageKey(clientId, "CANDIDATE", candidateId, field.AttributeCode, storedFileName);
                    var written = await WriteWithFailoverAsync(file, key, cancellationToken);
                    if (written.Server is null) throw new RecruitmentDocumentCollectionValidationException("Document storage is currently unavailable. Please try again later.");
                    batch.Blobs.Add((written.Server, written.StorageKey));
                    var id = await db.ExecuteScalarAsync<long>(@"INSERT INTO entity_attachments
(public_id,client_id,attachment_attribute_id,field_configuration_id,storage_server_id,entity_type,entity_id,
original_file_name,stored_file_name,storage_key,file_extension,declared_mime_type,detected_mime_type,file_size_bytes,sha256_hash,
version_number,document_number,issue_date,expiry_date,verification_status,uploaded_by_user_id)
VALUES(@PublicId,@ClientId,@AttributeId,@FieldId,@StorageId,'CANDIDATE',@CandidateId,@Original,@Stored,@Key,@Extension,
@DeclaredMime,@DetectedMime,@Size,@Hash,@Version,@DocumentNumber,@IssueDate,@ExpiryDate,@Verification,0); SELECT LAST_INSERT_ID();",
                        new { PublicId = publicId.ToString(), ClientId = clientId, AttributeId = field.AttachmentAttributeId, FieldId = field.Id,
                            StorageId = written.Server.Id, CandidateId = candidateId, Original = SafeDisplayFileName(file.FileName), Stored = storedFileName,
                            Key = written.StorageKey, item.Extension, DeclaredMime = file.ContentType ?? "", DetectedMime = item.Inspection.DetectedMimeType,
                            Size = file.Length, Hash = item.Inspection.Sha256Hash, Version = version++, metadata.DocumentNumber,
                            metadata.IssueDate, metadata.ExpiryDate, Verification = field.RequiresVerification ? "Pending" : "NotRequired" }, tx);
                    batch.Attachments.Add(new(id, field.Id));
                    await WriteAuditAsync(db, tx, id, clientId, "CANDIDATE", candidateId, "EXTERNAL_DOCUMENT_UPLOAD", null, true, "",
                        (ipAddress ?? "")[..Math.Min(ipAddress?.Length ?? 0, 80)], (userAgent ?? "")[..Math.Min(userAgent?.Length ?? 0, 500)],
                        new { publicId, field.FieldKey, file.Length });
                }
            }
            return batch;
        }
        catch
        {
            await DeleteDocumentCollectionBlobsAsync(batch);
            throw;
        }
    }

    internal async Task DeleteDocumentCollectionBlobsAsync(DocumentCollectionBatch batch)
    {
        foreach (var blob in batch.Blobs)
        {
            try { await storageService.DeleteAsync(blob.Server, blob.Key, CancellationToken.None); }
            catch { logger.LogWarning("A staged document-request attachment could not be removed after a failed submission."); }
        }
    }
}
