using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public partial class SettingsRepository
{
    private sealed record HeaderLink(string StorageCode, string Signature, string FirstHeader, string Fingerprint);
    private sealed record DraftLink(string Id, string RequestJson, string ResultJson);
    private static readonly JsonSerializerOptions DraftJsonOptions = new(JsonSerializerDefaults.Web);

    internal static async Task<List<EditableSalaryTemplateMapping>> ReadEditableExcelMappingsAsync(
        MySqlConnection db, MySqlTransaction tx, int clientId, long templateId, bool includeHeaders = true, bool includeDrafts = true)
    {
        var templateKey = templateId.ToString(CultureInfo.InvariantCulture);
        var links = new List<EditableSalaryTemplateMapping>();
        var headers = includeHeaders ? await db.QueryAsync<HeaderLink>("""
            SELECT m.ModuleCode StorageCode, h.signature Signature,
                   COALESCE(JSON_UNQUOTE(JSON_EXTRACT(m.SettingsJson, CONCAT('$.',JSON_QUOTE(h.signature),'.columns[0].sourceHeader'))),'') FirstHeader,
                   SHA2(CAST(JSON_EXTRACT(m.SettingsJson,CONCAT('$.',JSON_QUOTE(h.signature))) AS CHAR),256) Fingerprint
            FROM modulesettings m JOIN JSON_TABLE(JSON_KEYS(m.SettingsJson),'$[*]' COLUMNS(signature VARCHAR(128) PATH '$')) h
            WHERE m.client_id=@clientId AND m.ModuleCode IN ('excel_payslip_profile',@scopedCode)
              AND JSON_UNQUOTE(JSON_EXTRACT(m.SettingsJson, CONCAT('$.',JSON_QUOTE(h.signature),'.salaryTemplateId')))=@templateKey
            ORDER BY h.signature,m.ModuleCode
            """, new { clientId, templateKey, scopedCode = ExcelPayslipRepository.MappingProfileStorageCode(clientId) }, tx) : [];
        foreach (var group in headers.GroupBy(header => header.Signature, StringComparer.Ordinal))
        {
            var signature = group.Key;
            var heading = group.First().FirstHeader;
            var shortId = signature[..Math.Min(signature.Length, 12)];
            links.Add(new("excel-header", signature, "Excel payslip header mappings",
                string.IsNullOrWhiteSpace(heading) ? $"Header layout {shortId}" : $"{heading} · layout {shortId}",
                "Clear the salary template selection for future imports using this header layout. Saved columns and payslip batches are kept.",
                EditableMappingVersion(clientId, templateId, "excel-header", signature, group.Select(x => new { x.StorageCode, x.Fingerprint }))));
        }

        if (!includeDrafts || !await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name='excel_payroll_setup_imports')", transaction: tx))
            return links;
        var drafts = await db.QueryAsync<DraftLink>("""
            SELECT id,CAST(request_json AS CHAR) RequestJson,CAST(result_json AS CHAR) ResultJson FROM excel_payroll_setup_imports
            WHERE client_id=@clientId AND status='Draft' AND (
               JSON_OVERLAPS(JSON_EXTRACT(request_json,'$**.existingTemplateId'),JSON_ARRAY(@templateKey))
            OR JSON_OVERLAPS(JSON_EXTRACT(request_json,'$**.templateId'),JSON_ARRAY(@templateKey))
            OR JSON_OVERLAPS(JSON_EXTRACT(result_json,'$**.templateId'),JSON_ARRAY(@templateKey)))
            ORDER BY id
            """, new { clientId, templateKey }, tx);
        foreach (var draft in drafts)
        {
            var request = JsonNode.Parse(draft.RequestJson)!.AsObject();
            var metadata = JsonNode.Parse(draft.ResultJson)!.AsObject();
            if (!ClearExcelDraftTemplateSelection(request, metadata, templateKey)) continue;
            var batch = request["batchId"]?.ToString();
            links.Add(new("excel-draft", draft.Id, "Excel payroll setup records",
                string.IsNullOrWhiteSpace(batch) ? $"Setup draft {draft.Id}" : $"Batch {batch} · draft {draft.Id}",
                "Clear this template selection from the draft. Its rows and salary inputs are retained. Review the draft again before applying.",
                EditableMappingVersion(clientId, templateId, "excel-draft", draft.Id, new { draft.RequestJson, draft.ResultJson })));
        }
        return links;
    }

    internal static async Task UnlinkExcelMappingAsync(MySqlConnection db, MySqlTransaction tx, int clientId, long templateId, EditableSalaryTemplateMapping mapping)
    {
        var key = mapping.Key;
        var templateKey = templateId.ToString(CultureInfo.InvariantCulture);
        if (mapping.Kind == "excel-header")
        {
            var updated = await db.ExecuteAsync("""
                UPDATE modulesettings SET SettingsJson=JSON_REMOVE(SettingsJson,CONCAT('$.',JSON_QUOTE(@key),'.salaryTemplateId'))
                WHERE client_id=@clientId AND ModuleCode IN ('excel_payslip_profile',@scopedCode)
                  AND JSON_UNQUOTE(JSON_EXTRACT(SettingsJson,CONCAT('$.',JSON_QUOTE(@key),'.salaryTemplateId')))=@templateKey
                """, new { clientId, key, templateKey, scopedCode = ExcelPayslipRepository.MappingProfileStorageCode(clientId) }, tx);
            if (updated == 0) throw new InvalidOperationException("The header layout mapping changed. Refresh its mappings.");
            return;
        }
        if (mapping.Kind != "excel-draft") throw new InvalidOperationException("Choose an editable Excel mapping.");
        var draft = await db.QuerySingleOrDefaultAsync<DraftLink>("""
            SELECT id,CAST(request_json AS CHAR) RequestJson,CAST(result_json AS CHAR) ResultJson
            FROM excel_payroll_setup_imports WHERE client_id=@clientId AND id=@key AND status='Draft' FOR UPDATE
            """, new { clientId, key }, tx);
        if (draft is null || mapping.Version != EditableMappingVersion(clientId, templateId, mapping.Kind, key, new { draft.RequestJson, draft.ResultJson }))
            throw new InvalidOperationException("The payroll setup draft changed. Refresh its mappings.");
        var request = JsonNode.Parse(draft.RequestJson)!.AsObject();
        var metadata = JsonNode.Parse(draft.ResultJson)!.AsObject();
        if (!ClearExcelDraftTemplateSelection(request, metadata, templateKey))
            throw new InvalidOperationException("The draft no longer has this editable selection.");
        var typedRequest = request.Deserialize<ExcelPayrollSetupRequest>(DraftJsonOptions)!;
        var hash = ExcelPayrollSetupRepository.Hash(JsonSerializer.Serialize(typedRequest, DraftJsonOptions));
        if (await db.ExecuteAsync("""
            UPDATE excel_payroll_setup_imports SET request_json=CAST(@requestJson AS JSON),result_json=CAST(@resultJson AS JSON),
                request_hash=@hash,plan_hash='' WHERE client_id=@clientId AND id=@key AND status='Draft'
            """, new { clientId, key, hash, requestJson = request.ToJsonString(), resultJson = metadata.ToJsonString() }, tx) != 1)
            throw new InvalidOperationException("The draft could not be unlinked. Refresh its mappings.");
    }

    private static bool ClearExcelDraftTemplateSelection(JsonObject request, JsonObject metadata, string templateKey)
    {
        // A kept/applied result can survive a later draft edit. Its receipts are historical evidence.
        if (!string.IsNullOrWhiteSpace(metadata["importId"]?.ToString())) return false;
        var changed = false;
        if (request["templateDecisions"] is JsonArray decisions)
        {
            foreach (var decision in decisions.OfType<JsonObject>().Where(x => x["existingTemplateId"]?.ToString() == templateKey).ToArray())
                changed |= decisions.Remove(decision);
        }
        if (request["locationAllocations"] is JsonObject location && location["groups"] is JsonArray groups)
        {
            foreach (var group in groups.OfType<JsonObject>())
            {
                if (group["parts"] is not JsonArray parts) continue;
                foreach (var part in parts.OfType<JsonObject>().Where(x => x["templateId"]?.ToString() == templateKey))
                {
                    part["templateId"] = "";
                    part["componentScopes"] = new JsonObject();
                    location["reviewed"] = false;
                    location["reviewedSettingsApplicability"] = false;
                    changed = true;
                }
            }
        }
        if (!changed && !ContainsDraftTemplate(metadata["transfer"], templateKey) && !ContainsDraftTemplate(metadata["locationAllocations"], templateKey))
            return false;
        request.Remove("quickPreparation");
        metadata.Remove("transfer");
        metadata.Remove("locationAllocations");
        metadata.Remove("previewPlanHash");
        metadata["previewCanApply"] = false;
        return true;
    }

    private static bool ContainsDraftTemplate(JsonNode? node, string templateKey) => node switch
    {
        JsonObject obj => obj["templateId"]?.ToString() == templateKey || obj.Any(pair => ContainsDraftTemplate(pair.Value, templateKey)),
        JsonArray array => array.Any(item => ContainsDraftTemplate(item, templateKey)),
        _ => false
    };
}
