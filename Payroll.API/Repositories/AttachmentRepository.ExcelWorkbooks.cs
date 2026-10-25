using System.IO.Compression;
using System.Security.Cryptography;
using Dapper;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public partial class AttachmentRepository
{
    internal const string ExcelOriginalEntity = "EXCEL_PAYSLIP_ORIGINAL";
    internal static void ValidateWorkbookField(AttachmentFieldConfiguration field, IFormFile file, string mime)
    {
        if (!field.IsActive || field.AttachmentAttributeId <= 0) throw new InvalidOperationException("Original workbook attachment configuration is unavailable.");
        if (file.Length > field.MaximumFileSizeBytes || field.MaximumTotalSizeBytes.HasValue && file.Length > field.MaximumTotalSizeBytes.Value)
            throw new InvalidOperationException("Original workbook exceeds its configured attachment field size limit.");
        if (!ReadJsonList(field.AllowedExtensionsJson).Contains(Path.GetExtension(file.FileName).TrimStart('.'), StringComparer.OrdinalIgnoreCase)
            || !ReadJsonList(field.AllowedMimeTypesJson).Contains(mime, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("The original workbook type is not allowed by the configured attachment field.");
        if (field.RequiresVerification || field.RequiresDocumentNumber || field.RequiresIssueDate || field.RequiresExpiryDate)
            throw new InvalidOperationException("This original workbook field requires a document/verification workflow. Review its configuration before importing.");
    }
    internal static async Task<(string Mime, string Hash)> InspectWorkbookAsync(IFormFile file, long maximumBytes, CancellationToken cancellationToken)
    {
        if (file.Length <= 0 || file.Length > maximumBytes) throw new InvalidOperationException("Original workbook must be non-empty and within the attachment size limit.");
        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        await using var buffer = new MemoryStream();
        await using (var input = file.OpenReadStream())
        {
            var chunk = new byte[81920]; int count;
            while ((count = await input.ReadAsync(chunk, cancellationToken)) != 0)
            {
                if (buffer.Length + count > maximumBytes) throw new InvalidOperationException("Original workbook exceeds the attachment size limit.");
                await buffer.WriteAsync(chunk.AsMemory(0, count), cancellationToken);
            }
        }
        if (buffer.Length != file.Length) throw new InvalidOperationException("Original workbook size changed during upload.");
        var bytes = buffer.ToArray();
        string mime;
        try
        {
            if (extension is ".xlsx" or ".xlsm")
            {
                buffer.Position = 0;
                using var zip = new ZipArchive(buffer, ZipArchiveMode.Read, leaveOpen: true);
                if (zip.Entries.Count > 20000 || zip.Entries.Sum(e => e.Length) > 512L * 1024 * 1024
                    || zip.GetEntry("[Content_Types].xml") is null || zip.GetEntry("xl/workbook.xml") is null
                    || extension == ".xlsx" && zip.GetEntry("xl/vbaProject.bin") is not null)
                    throw new InvalidOperationException("Invalid or oversized Excel workbook container.");
                mime = extension == ".xlsx" ? "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" : "application/vnd.ms-excel.sheet.macroEnabled.12";
            }
            else if (extension == ".xls")
            {
                // The existing reader validates the binary workbook container; it does not execute VBA or formulas.
                buffer.Position = 0;
                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
                using var reader = ExcelDataReader.ExcelReaderFactory.CreateBinaryReader(buffer, new() { LeaveOpen = true });
                if (reader.ResultsCount <= 0) throw new InvalidOperationException("Invalid XLS workbook.");
                mime = "application/vnd.ms-excel";
            }
            else if (extension == ".csv")
            {
                var text = new System.Text.UTF8Encoding(false, true).GetString(bytes);
                if (text.Contains('\0')) throw new InvalidOperationException("CSV must be UTF-8 text without null bytes.");
                mime = "text/csv";
            }
            else throw new InvalidOperationException("Original workbook must be XLSX, XLSM, XLS or UTF-8 CSV.");
        }
        catch (Exception error) when (error is InvalidDataException or System.Text.DecoderFallbackException or ExcelDataReader.Exceptions.ExcelReaderException)
        { throw new InvalidOperationException("File content does not match a supported workbook format."); }
        return (mime, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    public async Task<ExcelPayslipOriginal> RetainExcelWorkbookAsync(int clientId, IFormFile file, AuthUser user, string ipAddress, string userAgent, CancellationToken cancellationToken)
    {
        if (user.Id <= 0 || user.ClientId.HasValue && user.ClientId != clientId) throw new InvalidOperationException("Client access denied.");
        var inspection = await InspectWorkbookAsync(file, Math.Min(GlobalMaximumBytes(), 25L * 1024 * 1024), cancellationToken);
        var name = SafeDisplayFileName(file.FileName);
        await using var db = Connection(); await db.OpenAsync(cancellationToken); await using var tx = await db.BeginTransactionAsync(cancellationToken);
        var active = await db.ExecuteScalarAsync<int>("SELECT Id FROM clients WHERE Id=@ClientId AND IsActive=TRUE FOR UPDATE", new { ClientId = clientId }, tx);
        if (active == 0) throw new InvalidOperationException("Select an active client.");
        var field = await db.QuerySingleOrDefaultAsync<AttachmentFieldConfiguration>($@"{ConfigurationSelect}
WHERE f.client_id=0 AND f.module_code='PAYROLL' AND f.form_code='EXCEL_PAYSLIP_ORIGINAL' AND f.field_key='originalWorkbook'
AND f.is_active=TRUE AND a.is_active=TRUE AND (f.effective_from_utc IS NULL OR f.effective_from_utc<=UTC_TIMESTAMP(6))
AND (f.effective_until_utc IS NULL OR f.effective_until_utc>UTC_TIMESTAMP(6))", transaction: tx)
            ?? throw new InvalidOperationException("Run the reviewed migration and enable the original workbook attachment field.");
        ValidateWorkbookField(field, file, inspection.Mime);
        var existing = await db.QuerySingleOrDefaultAsync<ExcelPayslipOriginal>(@"SELECT o.id AS Id,a.original_file_name AS FileName,a.file_size_bytes AS SizeBytes,
a.sha256_hash AS Sha256,a.uploaded_at_utc AS UploadedAtUtc FROM excel_payslip_originals o
JOIN entity_attachments a ON a.id=o.attachment_id AND a.is_deleted=FALSE WHERE o.client_id=@ClientId AND o.sha256_hash=@Hash",
            new { ClientId = clientId, inspection.Hash }, tx);
        if (existing is not null)
        {
            var retainedId = await db.ExecuteScalarAsync<long>("SELECT attachment_id FROM excel_payslip_originals WHERE client_id=@ClientId AND id=@Id", new { ClientId = clientId, existing.Id }, tx);
            var retained = await GetAttachmentByIdAsync(retainedId);
            var retainedServer = retained is null ? null : await GetStorageServerAsync(retained.StorageServerId, true);
            if (retained is null || retainedServer is null || !retainedServer.IsActive || !retainedServer.IsReadEnabled)
                throw new InvalidOperationException("The existing original attachment storage is unavailable; no new batch was saved.");
            try { await using var content = await storageService.OpenReadAsync(retainedServer, retained.StorageKey, cancellationToken); }
            catch (FileNotFoundException) { throw new InvalidOperationException("The original attachment bytes are missing. Restore the stored original before reusing this upload."); }
            await tx.CommitAsync(cancellationToken);
            return existing with { FileName = name }; // Alias is recorded on each batch link; bytes are reused only within this client.
        }
        var id = Guid.NewGuid().ToString("N"); var publicId = Guid.NewGuid();
        var storedName = id + Path.GetExtension(name).ToLowerInvariant();
        var key = BuildStorageKey(clientId, ExcelOriginalEntity, 0, "EXCEL_WORKBOOK", storedName);
        // Use configured global attachment storage only. Do not provision or repair a local fallback here.
        var write = await WriteWithFailoverAsync(file, key, cancellationToken, allowLocalFallback: false);
        if (write.Server is null) throw new InvalidOperationException(write.Error ?? "No configured global attachment server is writable.");
        var committed = false;
        try
        {
            var attachmentId = await db.ExecuteScalarAsync<long>(@"INSERT INTO entity_attachments
(public_id,client_id,attachment_attribute_id,field_configuration_id,storage_server_id,entity_type,entity_id,original_file_name,stored_file_name,storage_key,
file_extension,declared_mime_type,detected_mime_type,file_size_bytes,sha256_hash,version_number,uploaded_by_user_id)
VALUES(@PublicId,@ClientId,@AttributeId,@FieldId,@ServerId,@EntityType,0,@Name,@StoredName,@Key,@Extension,@Mime,@Mime,@Size,@Hash,1,@UserId);
SELECT LAST_INSERT_ID();", new { PublicId = publicId.ToString(), ClientId = clientId, AttributeId = field.AttachmentAttributeId, FieldId = field.Id,
                ServerId = write.Server.Id, EntityType = ExcelOriginalEntity, Name = name, StoredName = storedName, Key = write.StorageKey,
                Extension = Path.GetExtension(name).TrimStart('.'), inspection.Mime, Size = file.Length, inspection.Hash, UserId = user.Id }, tx);
            await db.ExecuteAsync("INSERT INTO excel_payslip_originals(client_id,id,attachment_id,sha256_hash) VALUES(@ClientId,@Id,@AttachmentId,@Hash)",
                new { ClientId = clientId, Id = id, AttachmentId = attachmentId, inspection.Hash }, tx);
            // Each original is its own immutable attachment owner, not another version in one shared field.
            await db.ExecuteAsync("UPDATE entity_attachments SET entity_id=@Id WHERE id=@Id", new { Id = attachmentId }, tx);
            await WriteAuditAsync(db, tx, attachmentId, clientId, ExcelOriginalEntity, attachmentId, "UPLOAD", user.Id, true, "", ipAddress, userAgent, new { originalId = id, sha256 = inspection.Hash });
            await tx.CommitAsync(cancellationToken); committed = true;
            return new(id, name, file.Length, inspection.Hash, DateTime.UtcNow);
        }
        finally
        {
            // Never delete a file after an uncertain commit: retain the metadata/storage for operator reconciliation.
            if (!committed) logger.LogWarning("Workbook attachment {OriginalId} needs storage/transaction reconciliation; no automatic file purge was attempted.", id);
        }
    }

    public async Task<(EntityAttachment Attachment, AttachmentStorageServer Server)?> GetExcelWorkbookForContentAsync(int clientId, string originalId, string batchId, AuthUser user, string ipAddress, string userAgent, CancellationToken cancellationToken)
    {
        if (user.Id <= 0 || user.ClientId.HasValue && user.ClientId != clientId) return null;
        await using var db = Connection(); await db.OpenAsync(cancellationToken);
        var reference = await db.QuerySingleOrDefaultAsync<WorkbookDownloadReference>(@"SELECT o.attachment_id AS AttachmentId,l.original_file_name AS FileName FROM excel_payslip_originals o
JOIN excel_payslip_batch_originals l ON l.client_id=o.client_id AND l.original_id=o.id AND l.batch_id=@BatchId
WHERE o.client_id=@ClientId AND o.id=@Id",
            new { ClientId = clientId, Id = originalId, BatchId = batchId });
        if (reference is null) return null;
        var attachment = await GetAttachmentByIdAsync(reference.AttachmentId);
        if (attachment is null || attachment.IsDeleted) return null;
        var server = await GetStorageServerAsync(attachment.StorageServerId, true);
        if (server is null || !server.IsActive || !server.IsReadEnabled) throw new InvalidOperationException("Original attachment storage is unavailable.");
        attachment.OriginalFileName = reference.FileName;
        await WriteAuditAsync(db, null, attachment.Id, clientId, ExcelOriginalEntity, attachment.EntityId, "DOWNLOAD_REQUEST", user.Id, true, "", ipAddress, userAgent, new { originalId, batchId });
        return (attachment, server);
    }
    private sealed class WorkbookDownloadReference { public long AttachmentId { get; set; } public string FileName { get; set; } = ""; }
}
