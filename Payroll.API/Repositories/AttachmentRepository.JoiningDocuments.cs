using System.Text.RegularExpressions;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public partial class AttachmentRepository
{
    // Semantic aliases identify existing personal-document masters; they are not
    // another configuration list. Published forms retain their exact config IDs.
    internal static readonly (string Kind, string Code, string Label, bool SeedMissing, string[] Aliases)[] JoiningDocumentDefaults =
    [
        ("EDUCATION", "EDUCATIONAL_CERTIFICATES", "Educational Certificates", true, ["EDUCATION", "EDUCATIONALCERTIFICATES", "EDUCATIONCERTIFICATES", "ACADEMIC", "ACADEMICDOCUMENTS", "ACADEMICCERTIFICATES", "QUALIFICATION", "QUALIFICATIONCERTIFICATES"]),
        ("EXPERIENCE", "EXPERIENCE_RELIEVING_LETTER", "Experience Letter / Relieving Letter", true, ["EXPERIENCE", "EXPERIENCELETTER", "RELIEVING", "RELIEVINGLETTER", "EXPERIENCERELIEVINGLETTER"]),
        ("PAYSLIPS", "LAST_THREE_MONTHS_PAYSLIPS", "Last Three Months pay slip", true, ["SALARY", "SALARYSLIP", "SALARYSLIPS", "PAYSLIP", "PAYSLIPS", "LASTTHREEMONTHSPAYSLIP", "LASTTHREEMONTHSPAYSLIPS", "LAST3MONTHSPAYSLIP", "LAST3MONTHSPAYSLIPS"]),
        ("ADDRESS", "ADDRESS_PROOF", "Address proof", true, ["ADDRESS", "ADDRESSPROOF", "RESIDENTIALADDRESSPROOF"]),
        ("PHOTO", "PASSPORT_PHOTOGRAPHS", "3 passport size photographs", true, ["PHOTO", "PHOTOGRAPH", "PHOTOGRAPHS", "PASSPORTPHOTO", "PASSPORTPHOTOGRAPHS", "PASSPORTSIZEPHOTOGRAPHS", "3PASSPORTSIZEPHOTOGRAPHS"]),
        ("PAN", "PAN_CARD", "PAN card", true, ["PAN", "PANCARD", "PANDOC"]),
        ("AADHAAR", "AADHAAR_CARD", "Aadhaar Card", true, ["AADHAAR", "AADHAR", "ADHAAR", "AADHAARCARD", "AADHARCARD", "ADHAARCARD", "ADHARCARD", "AADHAARDOC", "AADHARDOC", "ADHAARDOC"]),
        ("FAMILY_DOB", "FAMILY_DATE_OF_BIRTH_PROOF", "Date of Birth & proof of family members (Mother, Father, Spouse, first 02 children)", true, ["FAMILYDOB", "FAMILYDATEOFBIRTHPROOF", "FAMILYMEMBERSDOBPROOF"]),
        ("BANK", "BANK_PASS_BOOK", "Bank Pass Book", true, ["BANK", "BANKDETAILS", "BANKPASSBOOK", "BANKPROOF", "CANCELLEDCHEQUE"]),
        ("POLICE", "POLICE_VERIFICATION_CERTIFICATE", "Police Verification Certificate", true, ["POLICEVERIFICATION", "POLICEVERIFICATIONCERTIFICATE", "PVC"]),
        ("OFFER", "OFFER_LETTER", "Offer letter", true, ["OFFERLETTER", "SIGNEDOFFERLETTER"]),
        ("DOB", "DATE_OF_BIRTH_PROOF", "Date of birth proof", false, ["DOB", "DOBPROOF", "DATEOFBIRTH", "DATEOFBIRTHPROOF", "BIRTHCERTIFICATE"]),
        ("PREVIOUS_APPOINTMENT", "PREVIOUS_APPOINTMENT_LETTER", "Previous appointment letter", false, ["PREVIOUSAPPOINTMENT", "PREVIOUSAPPOINTMENTLETTER", "APPOINTMENTLETTER"]),
        ("REFERENCES", "REFERENCES", "References", false, ["REFERENCE", "REFERENCES", "TWOREFERENCES", "2REFERENCES"]),
        ("UAN", "UAN_PROOF", "UAN proof", false, ["UAN", "UANPROOF", "UANCARD"])
    ];

    private static string JoiningAlias(string? value)
    {
        var scopedCode = Regex.Match(value ?? "", @"^(?:.+?[_\s-])?JOIN(?:ING)?(?:[_\s-]DOCUMENTS?)?[_\s-](.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var normalized = Regex.Replace(scopedCode.Success ? scopedCode.Groups[1].Value : value ?? "", "[^a-zA-Z0-9]", "").ToUpperInvariant();
        foreach (var prefix in new[] { "JOININGDOCUMENTS", "JOININGDOCUMENT" })
            if (normalized.StartsWith(prefix, StringComparison.Ordinal)) return normalized[prefix.Length..];
        return normalized;
    }

    internal static string? JoiningDocumentKind(AttachmentFieldConfiguration field)
    {
        foreach (var value in new[] { field.AttributeCode, field.FieldKey, field.AttributeName, field.FieldLabel })
        {
            var alias = JoiningAlias(value);
            if (alias.Length == 0) continue;
            foreach (var item in JoiningDocumentDefaults)
                if (alias == JoiningAlias(item.Code) || alias == JoiningAlias(item.Label) || item.Aliases.Contains(alias, StringComparer.Ordinal)) return item.Kind;
        }
        return null;
    }

    public static string JoiningDocumentDisplayLabel(AttachmentFieldConfiguration field)
    {
        var kind = JoiningDocumentKind(field);
        return JoiningDocumentDefaults.FirstOrDefault(item => item.Kind == kind).Label ?? field.FieldLabel;
    }

    private static bool JoiningContext(AttachmentFieldConfiguration field, IReadOnlySet<long> publishedIds) => publishedIds.Contains(field.Id)
        || field.ModuleCode.Equals("EMPLOYEE", StringComparison.OrdinalIgnoreCase) && field.FormCode is "EMPLOYEE_CREATE_EDIT" or "EMPLOYEE_PROFILE"
        || field.ModuleCode.Equals("RECRUITMENT", StringComparison.OrdinalIgnoreCase) && field.FormCode.Equals("PRE_ONBOARDING", StringComparison.OrdinalIgnoreCase);

    internal static IReadOnlyList<AttachmentFieldConfiguration> ResolveJoiningDocuments(IEnumerable<AttachmentFieldConfiguration> fields, int clientId,
        IReadOnlySet<long>? publishedConfigIds = null, DateTime? now = null)
    {
        publishedConfigIds ??= new HashSet<long>();
        var instant = now ?? DateTime.UtcNow;
        var applicable = ApplicableJoiningDocuments(fields, clientId, publishedConfigIds, instant);
        // Preserve the core context/key override, then display each document kind once.
        return applicable.GroupBy(field => (Module: field.ModuleCode.ToUpperInvariant(), Form: field.FormCode.ToUpperInvariant(), Key: field.FieldKey.ToUpperInvariant()))
            .Select(group => group.OrderByDescending(field => field.ClientId == clientId).ThenBy(field => field.Id).First())
            .GroupBy(field => JoiningDocumentKind(field)!)
            .Select(group => group.OrderByDescending(field => field.ClientId == clientId)
                .ThenByDescending(field => publishedConfigIds.Contains(field.Id)).ThenBy(field => field.Id).First())
            .OrderBy(field => Array.FindIndex(JoiningDocumentDefaults, item => item.Kind == JoiningDocumentKind(field)))
            .ThenBy(field => field.DisplayOrder).ThenBy(field => field.Id).ToArray();
    }

    private static IEnumerable<AttachmentFieldConfiguration> ApplicableJoiningDocuments(IEnumerable<AttachmentFieldConfiguration> fields, int clientId,
        IReadOnlySet<long> publishedConfigIds, DateTime now) => fields.Where(field => field.Id > 0 && field.IsActive && (field.ClientId == 0 || field.ClientId == clientId)
            && (!field.EffectiveFromUtc.HasValue || field.EffectiveFromUtc <= now) && (!field.EffectiveUntilUtc.HasValue || field.EffectiveUntilUtc >= now)
            && JoiningContext(field, publishedConfigIds) && JoiningDocumentKind(field) is not null);

    private static async Task<HashSet<long>> PublishedJoiningConfigurationIdsAsync(MySqlConnection db, MySqlTransaction? tx)
    {
        var tables = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA=DATABASE()
AND TABLE_NAME IN ('form_definitions','form_versions','form_fields','form_field_types')", transaction: tx);
        if (tables != 4) return [];
        return (await db.QueryAsync<long>(@"SELECT DISTINCT f.AttachmentFieldConfigurationId
FROM form_definitions d JOIN form_versions v ON v.Id=d.CurrentPublishedVersionId AND v.FormDefinitionId=d.Id AND v.Status='Published'
JOIN form_fields f ON f.FormVersionId=v.Id AND f.IsActive=TRUE
JOIN form_field_types t ON t.Id=f.FieldTypeId AND t.TypeCode='UPLOAD'
JOIN attachment_field_configurations cfg ON cfg.id=f.AttachmentFieldConfigurationId AND cfg.client_id IN(0,d.ClientId)
WHERE d.Status='Active' AND d.PurposeCode='DOCUMENT_REQUEST' AND f.AttachmentFieldConfigurationId IS NOT NULL", transaction: tx)).ToHashSet();
    }

    public static async Task<IReadOnlyList<AttachmentFieldConfiguration>> LoadJoiningDocumentConfigurationsAsync(MySqlConnection db, MySqlTransaction? tx, int clientId, bool includeAll = false)
    {
        var rows = await db.QueryAsync<AttachmentFieldConfiguration>(ConfigurationSelect + @"
WHERE f.client_id IN(0,@ClientId) AND a.client_id IN(0,@ClientId) AND a.is_active=TRUE", new { ClientId = clientId }, tx);
        var published = await PublishedJoiningConfigurationIdsAsync(db, tx);
        return includeAll ? ApplicableJoiningDocuments(rows, clientId, published, DateTime.UtcNow).DistinctBy(field => field.Id)
            .OrderBy(field => field.DisplayOrder).ThenBy(field => field.Id).ToArray() : ResolveJoiningDocuments(rows, clientId, published);
    }

    public async Task<IReadOnlyList<AttachmentFieldConfiguration>> GetJoiningDocumentConfigurationsAsync(int clientId)
    {
        if (clientId <= 0) throw new InvalidOperationException("Select a client for the shared document catalog.");
        await using var db = Connection(); await db.OpenAsync();
        await EnsureJoiningDocumentCatalogAsync(db);
        return await LoadJoiningDocumentConfigurationsAsync(db, null, clientId, includeAll: true);
    }

    public static async Task EnsureJoiningDocumentCatalogAsync(MySqlConnection db)
    {
        if (await db.ExecuteScalarAsync<int>("SELECT GET_LOCK('attachment_joining_document_catalog_v1',10)") != 1)
            throw new InvalidOperationException("The shared document catalog is being initialized. Please retry.");
        try
        {
            await using var tx = await db.BeginTransactionAsync();
            await NormalizeJoiningDocumentMastersAsync(db, tx);
            var fields = (await db.QueryAsync<AttachmentFieldConfiguration>(ConfigurationSelect, transaction: tx)).ToList();
            var attributes = (await db.QueryAsync<AttachmentAttribute>(@"SELECT id Id,client_id ClientId,attribute_code AttributeCode,attribute_name AttributeName,is_active IsActive
FROM attachment_attributes ORDER BY id", transaction: tx)).ToList();
            var published = await PublishedJoiningConfigurationIdsAsync(db, tx);
            foreach (var definition in JoiningDocumentDefaults)
            {
                var matchingFields = fields.Where(field => JoiningDocumentKind(field) == definition.Kind && JoiningContext(field, published)).ToList();
                // Disabled/effective-dated global masters remain authoritative; never create a replacement that turns them back on.
                if (matchingFields.Any(field => field.ClientId == 0)) continue;
                var globalAttributes = attributes.Where(attribute => attribute.ClientId == 0 && JoiningDocumentKind(new()
                    { AttributeCode = attribute.AttributeCode, AttributeName = attribute.AttributeName }) == definition.Kind).OrderByDescending(attribute => attribute.IsActive).ThenBy(attribute => attribute.Id).ToList();
                var activePublished = matchingFields.Where(field => published.Contains(field.Id) && field.IsActive
                    && (!field.EffectiveFromUtc.HasValue || field.EffectiveFromUtc <= DateTime.UtcNow)
                    && (!field.EffectiveUntilUtc.HasValue || field.EffectiveUntilUtc >= DateTime.UtcNow)
                    && attributes.Any(attribute => attribute.Id == field.AttachmentAttributeId && attribute.IsActive)).OrderBy(field => field.Id).ToList();
                AttachmentAttribute? attribute = globalAttributes.FirstOrDefault();
                if (attribute is { IsActive: false }) continue;
                if (attribute is null && activePublished.FirstOrDefault() is { } reusable)
                {
                    attribute = attributes.Single(item => item.Id == reusable.AttachmentAttributeId);
                    await db.ExecuteAsync("UPDATE attachment_attributes SET client_id=0 WHERE id=@Id AND client_id=@ClientId", attribute, tx);
                    attribute.ClientId = 0;
                }
                // An explicitly disabled published field must not be cloned into an enabled global field.
                if (attribute is null && matchingFields.Any(field => published.Contains(field.Id))) continue;
                if (attribute is null)
                {
                    if (!definition.SeedMissing) continue;
                    var id = await db.ExecuteScalarAsync<long>(@"INSERT INTO attachment_attributes
(client_id,attribute_code,attribute_name,description,data_classification,is_active)
VALUES(0,@Code,@Label,'Personal joining document','Confidential',TRUE);SELECT LAST_INSERT_ID();", new { definition.Code, definition.Label }, tx);
                    attribute = new() { Id = id, ClientId = 0, AttributeCode = definition.Code, AttributeName = definition.Label, IsActive = true };
                    attributes.Add(attribute);
                }
                var existing = activePublished.FirstOrDefault(field => field.AttachmentAttributeId == attribute.Id);
                if (existing is not null)
                {
                    var occupied = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM attachment_field_configurations
WHERE client_id=0 AND module_code=@ModuleCode AND form_code=@FormCode AND field_key=@FieldKey", existing, tx);
                    if (occupied == 0)
                    {
                        await db.ExecuteAsync("UPDATE attachment_field_configurations SET client_id=0 WHERE id=@Id AND client_id=@ClientId", existing, tx);
                        existing.ClientId = 0;
                        continue;
                    }
                }
                if (!definition.SeedMissing || matchingFields.Any(field => published.Contains(field.Id)) && activePublished.Count == 0) continue;
                // Only genuinely missing kinds get one shared configuration in the existing pre-onboarding target.
                await db.ExecuteAsync(@"INSERT IGNORE INTO attachment_field_configurations
(client_id,attachment_attribute_id,module_code,form_code,section_code,field_key,field_label,is_required,
allow_multiple,minimum_file_count,maximum_file_count,allowed_extensions_json,allowed_mime_types_json,
maximum_file_size_bytes,maximum_total_size_bytes,owner_can_view,owner_can_upload,owner_can_replace,owner_can_delete,
requires_verification,versioning_enabled,display_order,is_active)
VALUES(0,@AttributeId,'RECRUITMENT','PRE_ONBOARDING','DOCUMENTS',@Code,@Label,FALSE,
TRUE,0,10,'[""pdf"",""jpg"",""jpeg"",""png""]','[""application/pdf"",""image/jpeg"",""image/png""]',
10485760,31457280,TRUE,TRUE,FALSE,FALSE,TRUE,TRUE,@DisplayOrder,TRUE)",
                    new { AttributeId = attribute.Id, definition.Code, definition.Label, DisplayOrder = Array.FindIndex(JoiningDocumentDefaults, item => item.Kind == definition.Kind) * 10 + 10 }, tx);
            }
            await tx.CommitAsync();
        }
        finally { await db.ExecuteScalarAsync<int>("SELECT RELEASE_LOCK('attachment_joining_document_catalog_v1')"); }
    }
}
