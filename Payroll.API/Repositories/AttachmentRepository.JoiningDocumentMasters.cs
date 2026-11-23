using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public partial class AttachmentRepository
{
    public sealed record JoiningDocumentMasterMapping(long SourceAttributeId, long CanonicalAttributeId, string CanonicalCode, long AuditLogId);
    public sealed record JoiningDocumentMasterNormalizationResult(int RenamedMasters, int MergedMasters,
        IReadOnlyList<JoiningDocumentMasterMapping> Mappings, IReadOnlyList<string> Skipped);

    private sealed record MasterReference(string Table, string Column, string IdColumn = "id");
    private static readonly MasterReference[] JoiningMasterReferences =
    [
        new("attachment_field_configurations", "attachment_attribute_id"),
        new("entity_attachments", "attachment_attribute_id"),
        new("recruitment_document_checklist", "AttachmentAttributeId", "Id"),
        new("recruitment_candidate_checklist_items", "AttachmentAttributeId", "Id")
    ];
    private sealed class MasterReferenceMetadata { public string TableName { get; set; } = ""; public string ColumnName { get; set; } = ""; }
    private sealed record MasterReferenceReceipt(string Table, string Column, IReadOnlyList<long> Ids);

    internal static bool JoiningMasterRequirementsMatch(AttachmentAttribute left, AttachmentAttribute right) =>
        left.RequiresDocumentNumber == right.RequiresDocumentNumber && left.RequiresIssueDate == right.RequiresIssueDate
        && left.RequiresExpiryDate == right.RequiresExpiryDate;

    internal static string? JoiningMasterClassification(string left, string right)
    {
        string[] levels = ["Public", "Internal", "Confidential", "Restricted"];
        var first = Array.FindIndex(levels, value => value.Equals(left, StringComparison.OrdinalIgnoreCase));
        var second = Array.FindIndex(levels, value => value.Equals(right, StringComparison.OrdinalIgnoreCase));
        if (first < 0 || second < 0) return left.Equals(right, StringComparison.OrdinalIgnoreCase) ? left : null;
        return levels[Math.Max(first, second)];
    }

    /// <summary>
    /// The caller owns the transaction and attachment_joining_document_catalog_v1
    /// lock through commit. Configuration IDs, form keys and file content stay intact.
    /// </summary>
    public static async Task<JoiningDocumentMasterNormalizationResult> NormalizeJoiningDocumentMastersAsync(
        MySqlConnection db, MySqlTransaction tx, int? actorUserId = null)
    {
        var renamed = 0; var merged = 0;
        var mappings = new List<JoiningDocumentMasterMapping>(); var skipped = new List<string>();
        var columns = (await db.QueryAsync<MasterReferenceMetadata>(@"SELECT TABLE_NAME TableName,COLUMN_NAME ColumnName
FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA=DATABASE()
AND (TABLE_NAME IN ('attachment_attributes','attachment_audit_logs','attachment_field_configurations','entity_attachments',
'recruitment_document_checklist','recruitment_candidate_checklist_items')
OR COLUMN_NAME IN ('attachment_attribute_id','AttachmentAttributeId'))", transaction: tx)).ToArray();
        if (!columns.Any(row => row.TableName.Equals("attachment_audit_logs", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The attachment audit log must be initialized before document masters are consolidated.");
        var attributes = (await db.QueryAsync<AttachmentAttribute>(@"SELECT id Id,client_id ClientId,attribute_code AttributeCode,
attribute_name AttributeName,description Description,data_classification DataClassification,
requires_document_number RequiresDocumentNumber,requires_issue_date RequiresIssueDate,requires_expiry_date RequiresExpiryDate,
is_active IsActive,created_by_user_id CreatedByUserId,created_at_utc CreatedAtUtc,updated_by_user_id UpdatedByUserId,updated_at_utc UpdatedAtUtc
FROM attachment_attributes ORDER BY id FOR UPDATE", transaction: tx)).ToList();
        var fields = (await db.QueryAsync<AttachmentFieldConfiguration>(@"SELECT id Id,client_id ClientId,
attachment_attribute_id AttachmentAttributeId,module_code ModuleCode,form_code FormCode,field_key FieldKey,field_label FieldLabel,
is_active IsActive,effective_from_utc EffectiveFromUtc,effective_until_utc EffectiveUntilUtc
FROM attachment_field_configurations", transaction: tx)).ToList();
        var published = await PublishedJoiningConfigurationIdsAsync(db, tx);
        var instant = DateTime.UtcNow;
        var publishedAttributes = fields.Where(field => published.Contains(field.Id) && field.IsActive
                && (!field.EffectiveFromUtc.HasValue || field.EffectiveFromUtc <= instant)
                && (!field.EffectiveUntilUtc.HasValue || field.EffectiveUntilUtc >= instant))
            .Select(field => field.AttachmentAttributeId).ToHashSet();
        var availableReferences = JoiningMasterReferences.Where(reference => columns.Any(column =>
            column.TableName.Equals(reference.Table, StringComparison.OrdinalIgnoreCase)
            && column.ColumnName.Equals(reference.Column, StringComparison.OrdinalIgnoreCase))).ToArray();
        var discovered = columns.Where(column => column.ColumnName.Equals("attachment_attribute_id", StringComparison.OrdinalIgnoreCase)
            || column.ColumnName.Equals("AttachmentAttributeId", StringComparison.OrdinalIgnoreCase)).ToList();
        discovered.AddRange(await db.QueryAsync<MasterReferenceMetadata>(@"SELECT TABLE_NAME TableName,COLUMN_NAME ColumnName
FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE WHERE TABLE_SCHEMA=DATABASE() AND REFERENCED_TABLE_NAME='attachment_attributes'
AND REFERENCED_COLUMN_NAME='id'", transaction: tx));
        var unhandledReferences = discovered.Where(column => !JoiningMasterReferences.Any(reference =>
            column.TableName.Equals(reference.Table, StringComparison.OrdinalIgnoreCase)
            && column.ColumnName.Equals(reference.Column, StringComparison.OrdinalIgnoreCase)))
            .DistinctBy(column => (column.TableName.ToLowerInvariant(), column.ColumnName.ToLowerInvariant())).ToArray();
        var definitions = JoiningDocumentDefaults.Where(definition => definition.Kind != "OFFER")
            .Select(definition => (definition.Kind, definition.Code, definition.Label));

        foreach (var definition in definitions)
        {
            var eligible = attributes.Where(attribute => attribute.IsActive
                && (attribute.ClientId == 0 || publishedAttributes.Contains(attribute.Id))
                && MasterKind(attribute) == definition.Kind).ToList();
            var canonical = eligible.OrderByDescending(attribute => attribute.ClientId == 0 && attribute.AttributeCode.Equals(definition.Code, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(attribute => attribute.ClientId == 0).ThenBy(attribute => attribute.Id).FirstOrDefault();
            if (canonical is null) continue;
            var collision = attributes.FirstOrDefault(attribute => attribute.Id != canonical.Id && attribute.ClientId == 0
                && attribute.AttributeCode.Equals(definition.Code, StringComparison.OrdinalIgnoreCase));
            if (collision is not null && (!collision.IsActive || !eligible.Contains(collision)))
            {
                skipped.Add($"{definition.Kind}: the global generic code belongs to another or disabled master ({collision.Id}).");
                continue;
            }

            foreach (var source in eligible.Where(attribute => attribute.Id != canonical.Id).OrderBy(attribute => attribute.Id))
            {
                var classification = JoiningMasterClassification(canonical.DataClassification, source.DataClassification);
                if (!JoiningMasterRequirementsMatch(canonical, source) || classification is null)
                {
                    skipped.Add($"{definition.Kind}: master {source.Id} has different document metadata requirements.");
                    continue;
                }
                var unhandled = false;
                foreach (var reference in unhandledReferences)
                {
                    if (!SafeMasterIdentifier(reference.TableName) || !SafeMasterIdentifier(reference.ColumnName))
                        throw new InvalidOperationException("An attachment reference identifier is invalid.");
                    if (await db.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM `{reference.TableName}` WHERE `{reference.ColumnName}`=@Id", new { source.Id }, tx) == 0) continue;
                    skipped.Add($"{definition.Kind}: master {source.Id} has an unmapped reference in {reference.TableName}.{reference.ColumnName}.");
                    unhandled = true; break;
                }
                if (unhandled) continue;
                var beforeCanonical = JsonSerializer.SerializeToElement(canonical, MasterAuditJson);
                var beforeSource = JsonSerializer.SerializeToElement(source, MasterAuditJson);
                var receipts = new List<MasterReferenceReceipt>();
                foreach (var reference in availableReferences)
                {
                    var ids = (await db.QueryAsync<long>($"SELECT `{reference.IdColumn}` FROM `{reference.Table}` WHERE `{reference.Column}`=@Id ORDER BY `{reference.IdColumn}` FOR UPDATE", new { source.Id }, tx)).ToArray();
                    receipts.Add(new(reference.Table, reference.Column, ids));
                    if (ids.Length == 0) continue;
                    var changed = await db.ExecuteAsync($"UPDATE `{reference.Table}` SET `{reference.Column}`=@Target WHERE `{reference.Column}`=@Source AND `{reference.IdColumn}` IN @Ids",
                        new { Target = canonical.Id, Source = source.Id, Ids = ids }, tx);
                    if (changed != ids.Length) throw new InvalidOperationException("A document reference changed during consolidation. Retry the operation.");
                }
                foreach (var reference in availableReferences)
                    if (await db.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM `{reference.Table}` WHERE `{reference.Column}`=@Id", new { source.Id }, tx) != 0)
                        throw new InvalidOperationException("The old document master still has references and cannot be retired.");
                canonical.DataClassification = classification;
                await db.ExecuteAsync("UPDATE attachment_attributes SET data_classification=@DataClassification,updated_by_user_id=@Actor WHERE id=@Id",
                    new { canonical.DataClassification, canonical.Id, Actor = actorUserId }, tx);
                var auditId = await WriteMasterNormalizationReceiptAsync(db, tx, canonical.Id, actorUserId, "DOCUMENT_MASTER_MERGE",
                    new { Version = 1, definition.Kind, SourceAttributeId = source.Id, CanonicalAttributeId = canonical.Id,
                        Before = new { Source = beforeSource, Canonical = beforeCanonical }, After = new { Canonical = canonical },
                        AffectedReferences = receipts, Preserved = MasterPreservedFields });
                if (await db.ExecuteAsync("DELETE FROM attachment_attributes WHERE id=@Id AND is_active=TRUE", new { source.Id }, tx) != 1)
                    throw new InvalidOperationException("The old document master changed during consolidation. Retry the operation.");
                attributes.Remove(source);
                foreach (var field in fields.Where(field => field.AttachmentAttributeId == source.Id)) field.AttachmentAttributeId = canonical.Id;
                merged++; mappings.Add(new(source.Id, canonical.Id, definition.Code, auditId));
            }

            if (canonical.ClientId == 0 && canonical.AttributeCode == definition.Code && canonical.AttributeName == definition.Label) continue;
            var before = JsonSerializer.SerializeToElement(canonical, MasterAuditJson);
            canonical.ClientId = 0; canonical.AttributeCode = definition.Code; canonical.AttributeName = definition.Label;
            await db.ExecuteAsync(@"UPDATE attachment_attributes SET client_id=0,attribute_code=@AttributeCode,
attribute_name=@AttributeName,updated_by_user_id=@Actor WHERE id=@Id", new { canonical.Id, canonical.AttributeCode, canonical.AttributeName, Actor = actorUserId }, tx);
            await WriteMasterNormalizationReceiptAsync(db, tx, canonical.Id, actorUserId, "DOCUMENT_MASTER_RENAME",
                new { Version = 1, definition.Kind, CanonicalAttributeId = canonical.Id, Before = new { Canonical = before },
                    After = new { Canonical = canonical }, AffectedReferences = Array.Empty<MasterReferenceReceipt>(), Preserved = MasterPreservedFields });
            renamed++;
        }
        return new(renamed, merged, mappings, skipped);
    }

    private static string? MasterKind(AttachmentAttribute attribute) => attribute.AttributeCode.ToUpperInvariant() is
        "RESUME" or "OFFER_LETTER" or "EXCEL_WORKBOOK" or "EMPLOYEE_COMMUNICATION_FILE" or "RECRUITMENT_WORK_ORDER_FILE"
        or "RECRUITMENT_PROCESS_DOCUMENT" or "RECRUITMENT_REQUEST_SOURCE" or "RECRUITMENT_PROCESS_SUPPORT"
        ? null : JoiningDocumentKind(new() { AttributeCode = attribute.AttributeCode, AttributeName = attribute.AttributeName });
    private static bool SafeMasterIdentifier(string value) => Regex.IsMatch(value, "^[A-Za-z0-9_]+$", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions MasterAuditJson = new(JsonSerializerDefaults.Web);
    private static readonly string[] MasterPreservedFields = ["configurationId", "moduleCode", "formCode", "fieldKey", "formStableFieldCode",
        "clientId", "publicId", "storageKey", "sha256Hash", "versionNumber", "fileRules"];
    private static Task<long> WriteMasterNormalizationReceiptAsync(MySqlConnection db, MySqlTransaction tx, long attributeId, int? actorUserId, string action, object metadata)
        => db.ExecuteScalarAsync<long>(@"INSERT INTO attachment_audit_logs
(attachment_id,client_id,entity_type,entity_id,action,actor_user_id,success,failure_reason,ip_address,user_agent,metadata_json)
VALUES(NULL,0,'ATTACHMENT_ATTRIBUTE',@AttributeId,@Action,@Actor,TRUE,'','','',@Metadata);SELECT LAST_INSERT_ID();",
            new { AttributeId = attributeId, Action = action, Actor = actorUserId, Metadata = JsonSerializer.Serialize(metadata, MasterAuditJson) }, tx);
}
