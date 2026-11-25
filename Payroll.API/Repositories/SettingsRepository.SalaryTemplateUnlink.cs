using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed record EditableSalaryTemplateMapping(string Kind, string Key, string Type, string Label, string Impact, string Version);
public sealed record SalaryTemplateMappingPage(IReadOnlyList<EditableSalaryTemplateMapping> Items, int Total);
public sealed record SalaryTemplateUnlinkRequest(string Kind, string Key, string Version);

public partial class SettingsRepository
{
    private static string EditableMappingVersion(int clientId, long templateId, string kind, string key, object value) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { clientId, templateId, kind, key, value })));

    internal static bool CanUnlinkMapping(AuthUser actor, int clientId, string kind)
    {
        if (!PayrollAdvisorService.CanAccess(actor, clientId)) return false;
        bool Has(string permission) => actor.Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase);
        return kind switch
        {
            "employee" or "basic-pay" => Has("employees.manage"),
            "pf-draft" => Has("tax.statutory.manage"),
            "excel-header" => !SelectedClientAccess.IsSelected(actor) && WorkspacePermissions.CanReadPayslips(actor)
                && (Has("payroll.run") || Has("payroll.approve") || Has("payroll.payments")),
            "excel-draft" => !SelectedClientAccess.IsSelected(actor) && ExcelPayrollSetupEndpoints.CanAccess(actor, clientId),
            _ => false
        };
    }

    public async Task<SalaryTemplateMappingPage?> GetEditableSalaryTemplateMappingsAsync(int clientId, long templateId, int offset, int limit, AuthUser actor)
    {
        await using var db = Connection(); await db.OpenAsync();
        await using var tx = await db.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        var scope = await ReadDeletionScopeAsync(db, tx, clientId, templateId, false);
        if (scope is null) return null;
        if (scope.Value.Owners.Count != 1 || scope.Value.Header.ClientId > 0 && scope.Value.Header.ClientId != clientId) return new([], 0);
        var all = await ReadEditableMappingsAsync(db, tx, clientId, templateId, actor, null);
        return new(all.OrderBy(m => m.Type, StringComparer.Ordinal).ThenBy(m => m.Label, StringComparer.Ordinal)
            .ThenBy(m => m.Key, StringComparer.Ordinal).Skip(offset).Take(limit).ToArray(), all.Count);
    }

    public async Task<bool> UnlinkSalaryTemplateMappingAsync(int clientId, long templateId, SalaryTemplateUnlinkRequest request, AuthUser actor)
    {
        if (!CanUnlinkMapping(actor, clientId, request.Kind)) throw new UnauthorizedAccessException();
        await using var db = Connection(); await db.OpenAsync();
        // In addition to the same client/header locks as Delete, protect the reviewed record
        // and checks for newly staged payroll while detaching its salary-template reference.
        await using var tx = await db.BeginTransactionAsync(IsolationLevel.Serializable);
        var scope = await ReadDeletionScopeAsync(db, tx, clientId, templateId, true);
        if (scope is null) return false;
        if (scope.Value.Owners.Count != 1 || scope.Value.Header.ClientId > 0 && scope.Value.Header.ClientId != clientId)
            throw new InvalidOperationException("Shared salary templates cannot be unlinked from this client.");
        var available = await ReadEditableMappingsAsync(db, tx, clientId, templateId, actor, request.Kind, request.Key);
        var item = available.SingleOrDefault(m => m.Kind == request.Kind && m.Key == request.Key);
        if (item is null || !string.Equals(request.Version, item.Version, StringComparison.Ordinal))
            throw new InvalidOperationException("This mapping changed or is protected. Recheck the mappings before removing a link.");

        if (item.Kind == "pf-draft") await UnlinkPfDraftAsync(db, tx, clientId, templateId, item.Key);
        else if (item.Kind is "employee" or "basic-pay") await UnlinkEmployeeMappingAsync(db, tx, clientId, templateId, item, actor);
        else await UnlinkExcelMappingAsync(db, tx, clientId, templateId, item);
        await tx.CommitAsync();
        return true;
    }

    private static async Task<List<EditableSalaryTemplateMapping>> ReadEditableMappingsAsync(MySqlConnection db, MySqlTransaction tx,
        int clientId, long templateId, AuthUser actor, string? onlyKind, string? onlyKey = null)
    {
        bool Include(string kind) => (onlyKind is null || onlyKind == kind) && CanUnlinkMapping(actor, clientId, kind);
        var items = new List<EditableSalaryTemplateMapping>();
        if (Include("employee") || Include("basic-pay"))
        {
            long? rowId = null;
            if (onlyKey is not null)
            {
                if (!long.TryParse(onlyKey, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0) return items;
                rowId = parsed;
            }
            items.AddRange(await ReadEditableEmployeeMappingsAsync(db, tx, clientId, templateId, Include("employee"), Include("basic-pay"), rowId));
        }
        if (Include("pf-draft"))
        {
            var rows = await PfDraftEntriesAsync(db, tx, clientId, templateId);
            foreach (var (entry, _) in rows)
            {
                var id = ReadProperty(entry, "id")!.ToString();
                var version = ReadProperty(entry, "versionNumber")?.ToString();
                var label = $"{ReadProperty(entry, "name")?.ToString() ?? "PF policy"} · {(version is null ? id : $"v{version}")}";
                items.Add(new("pf-draft", id, "Draft PF policy", label,
                    "Clear this draft’s salary template. Its contribution rules and rates stay available for reassignment.",
                    EditableMappingVersion(clientId, templateId, "pf-draft", id, entry)));
            }
        }
        if (Include("excel-header") || Include("excel-draft"))
            items.AddRange(await ReadEditableExcelMappingsAsync(db, tx, clientId, templateId, Include("excel-header"), Include("excel-draft")));
        return items;
    }

    private static async Task<List<(JsonObject Entry, int Index)>> PfDraftEntriesAsync(MySqlConnection db, MySqlTransaction tx, int clientId, long templateId)
    {
        var json = await db.ExecuteScalarAsync<string?>("SELECT JSON_EXTRACT(SettingsJson,'$.pfPolicyVersions') FROM modulesettings WHERE client_id=@clientId AND ModuleCode='payroll'", new { clientId }, tx);
        var parsed = json is null ? null : JsonNode.Parse(json);
        if (parsed is null) return [];
        var rows = parsed.AsArray();
        var key = templateId.ToString(CultureInfo.InvariantCulture);
        return rows.Select((node, index) => (Entry: node!.AsObject(), Index: index))
            .Where(p => ReadProperty(p.Entry, "status")?.ToString() == "Draft" && ReadProperty(p.Entry, "salaryStructureId")?.ToString() == key
                && !string.IsNullOrWhiteSpace(ReadProperty(p.Entry, "id")?.ToString())).ToList();
    }

    private static async Task UnlinkPfDraftAsync(MySqlConnection db, MySqlTransaction tx, int clientId, long templateId, string id)
    {
        var (entry, index) = (await PfDraftEntriesAsync(db, tx, clientId, templateId)).Single(p => ReadProperty(p.Entry, "id")?.ToString() == id);
        var prop = entry.Single(p => p.Key.Equals("salaryStructureId", StringComparison.OrdinalIgnoreCase)).Key;
        var path = $"$.pfPolicyVersions[{index}].{prop}";
        if (await db.ExecuteAsync("UPDATE modulesettings SET SettingsJson=JSON_SET(SettingsJson,@path,'') WHERE client_id=@clientId AND ModuleCode='payroll' AND JSON_UNQUOTE(JSON_EXTRACT(SettingsJson,@path))=@key",
                new { clientId, path, key = templateId.ToString(CultureInfo.InvariantCulture) }, tx) != 1)
            throw new InvalidOperationException("The draft PF policy changed. Recheck its mapping.");
    }

    private static async Task<List<EditableSalaryTemplateMapping>> ReadEditableEmployeeMappingsAsync(MySqlConnection db,
        MySqlTransaction tx, int clientId, long templateId, bool master, bool basicPay, long? rowId)
    {
        var args = new { clientId, key = templateId.ToString(CultureInfo.InvariantCulture), rowId };
        var items = new List<EditableSalaryTemplateMapping>();
        var tables = (await db.QueryAsync<string>(@"SELECT table_name FROM information_schema.tables WHERE table_schema=DATABASE()
AND table_name IN ('employee_it0008_basic_pay','payruns','payrunemployees')", transaction: tx)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hasHistory = tables.Contains("employee_it0008_basic_pay");
        var hasRuns = tables.Contains("payruns") && tables.Contains("payrunemployees");
        if (tables.Contains("payruns") != tables.Contains("payrunemployees"))
            throw new InvalidOperationException("Payroll tables are incomplete. Employee mappings could not be verified.");
        if (hasRuns && await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM payruns WHERE ClientId=@clientId AND Status IN ('Processing','Queued'))", args, tx))
            return items;
        if (master)
        {
            // Older payroll can rely on Employee Master as the fallback when no effective Basic Pay row exists.
            var savedFallback = hasRuns ? """
                AND NOT EXISTS (SELECT 1 FROM payrunemployees p JOIN payruns r ON r.Id=p.PayRunId AND r.ClientId=p.ClientId
                 WHERE p.EmployeeId=e.Id AND p.ClientId=e.ClientId
                """ + (hasHistory ? "\n" + """
                    AND NOT EXISTS (SELECT 1 FROM employee_it0008_basic_pay h WHERE h.EmployeeId=e.Id AND h.ClientId=e.ClientId
                        AND DATE_FORMAT(h.EffectiveFrom,'%Y-%m')<=r.PayPeriod AND (h.EffectiveTo IS NULL OR DATE_FORMAT(h.EffectiveTo,'%Y-%m')>=r.PayPeriod))
                    """ : "") + ")" : "";
            var rows = await db.QueryAsync<EmployeeMappingRow>("""
                SELECT e.Id,e.EmployeeCode,TRIM(CONCAT_WS(' ',e.FirstName,e.LastName)) Name,e.AnnualCtc,
                       SHA2(COALESCE(CAST(e.SalaryJson AS CHAR),''),256) SalaryHash
                FROM employees e WHERE e.ClientId=@clientId AND e.SalaryStructureId=@key
                """ + (rowId.HasValue ? " AND e.Id=@rowId " : "\n") + savedFallback, args, tx);
            foreach (var row in rows)
            {
                var key = row.Id.ToString(CultureInfo.InvariantCulture);
                items.Add(new("employee", key, "Employee Master", $"{row.EmployeeCode} · {row.Name}",
                    "Clear this employee’s current Master template link. Salary inputs and dated Basic Pay records are retained.",
                    EditableMappingVersion(clientId, templateId, "employee", key, row)));
            }
        }
        if (basicPay && hasHistory)
        {
            // Only ordinary, open setup rows can be detached. Applied source imports and closed/reviewed salary are historical proof.
            var used = hasRuns ? """
                AND NOT EXISTS (SELECT 1 FROM payrunemployees p JOIN payruns r ON r.Id=p.PayRunId AND r.ClientId=p.ClientId
                  WHERE p.EmployeeId=h.EmployeeId AND p.ClientId=h.ClientId AND r.PayPeriod>=DATE_FORMAT(h.EffectiveFrom,'%Y-%m'))
                """ : "";
            var rows = await db.QueryAsync<BasicPayMappingRow>("""
                SELECT h.Id,h.EmployeeId,e.EmployeeCode,h.EffectiveFrom,h.AnnualCtc,SHA2(COALESCE(CAST(h.SalaryJson AS CHAR),''),256) SalaryHash,h.ChangeReason
                FROM employee_it0008_basic_pay h JOIN employees e ON e.Id=h.EmployeeId AND e.ClientId=h.ClientId
                WHERE h.ClientId=@clientId AND h.SalaryStructureId=@key AND h.Status='Active' AND h.EffectiveTo IS NULL
                  AND h.ActionType IN ('Hire','Master Update','Salary Change') AND h.ChangeReason NOT LIKE 'Reviewed salary Excel %'
                  AND NOT EXISTS(SELECT 1 FROM modulesettings m WHERE m.client_id=@clientId
                    AND m.ModuleCode IN (CONCAT('named-input-h:',h.Id),CONCAT('reported-input-h:',h.Id)))
                """ + (rowId.HasValue ? " AND h.Id=@rowId " : "\n") + used, args, tx);
            foreach (var row in rows)
            {
                var key = row.Id.ToString(CultureInfo.InvariantCulture);
                items.Add(new("basic-pay", key, "Unprocessed Basic Pay assignment", $"{row.EmployeeCode} · {row.EffectiveFrom:yyyy-MM-dd} · Basic Pay #{row.Id}",
                    "Clear only the template on this open Basic Pay record. Its effective date, CTC and component amounts stay in the record.",
                    EditableMappingVersion(clientId, templateId, "basic-pay", key, row)));
            }
        }
        return items;
    }

    private static async Task UnlinkEmployeeMappingAsync(MySqlConnection db, MySqlTransaction tx, int clientId, long templateId,
        EditableSalaryTemplateMapping item, AuthUser actor)
    {
        var master = item.Kind == "employee";
        var args = new { clientId, id = long.Parse(item.Key, CultureInfo.InvariantCulture), key = templateId.ToString(CultureInfo.InvariantCulture),
            actor = actor.Email, date = EmployeeEmploymentRules.Today, field = master ? "Salary Template Id" : "Basic Pay salary template #" + item.Key };
        var employeeId = master ? args.id : await db.ExecuteScalarAsync<long>("SELECT EmployeeId FROM employee_it0008_basic_pay WHERE Id=@id AND ClientId=@clientId", args, tx);
        var audit = await db.ExecuteAsync("""
            INSERT INTO employee_audit_trail(EmployeeId,EmployeeCode,ActionType,InfotypeCode,FieldName,OldValue,NewValue,EffectiveFrom,ChangedBy)
            SELECT Id,EmployeeCode,'Salary template unlink','0008',@field,@key,'',@date,@actor FROM employees WHERE ClientId=@clientId AND Id=@employeeId
            """, new { args.clientId, employeeId, args.field, args.key, args.date, args.actor }, tx);
        var table = master ? "employees" : "employee_it0008_basic_pay";
        if (audit != 1 || await db.ExecuteAsync($"UPDATE {table} SET SalaryStructureId='' WHERE ClientId=@clientId AND Id=@id AND SalaryStructureId=@key", args, tx) != 1)
            throw new InvalidOperationException("The employee salary mapping changed. Recheck it before unlinking.");
    }

    private sealed record EmployeeMappingRow(int Id, string EmployeeCode, string Name, decimal AnnualCtc, string SalaryHash);
    private sealed record BasicPayMappingRow(long Id, int EmployeeId, string EmployeeCode, DateTime EffectiveFrom, decimal AnnualCtc, string SalaryHash, string ChangeReason);
}
