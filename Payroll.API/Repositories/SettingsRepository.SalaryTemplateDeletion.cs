using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using MySqlConnector;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed record SalaryTemplateMapping(string Type, int Count, IReadOnlyList<string> Examples)
{
    public IReadOnlyList<SalaryTemplatePayslipDependency>? PayslipDesigns { get; init; }
}
public sealed record SalaryTemplateDeletionPreview(long TemplateId, string Name, IReadOnlyList<SalaryTemplateMapping> Mappings, string Version);

public partial class SettingsRepository
{
    public async Task<(SalaryTemplateDeletionPreview? Preview, bool Deleted)> ReviewSalaryTemplateDeletionAsync(
        int clientId, long templateId, PayslipDesignRepository designs, string? expectedVersion = null)
    {
        var deleting = expectedVersion is not null;
        await using var db = Connection();
        await db.OpenAsync();
        // Mapping writers lock the client or the selected structure before assigning it.
        // Take those same locks first, then read one consistent dependency snapshot.
        // Plain snapshot reads do not lock other clients' employee or payroll tables.
        await using var tx = await db.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        var scope = await ReadDeletionScopeAsync(db, tx, clientId, templateId, deleting);
        if (scope is null) return (null, false);
        var (row, owners) = scope.Value;
        var args = new { clientId, templateId };
        var lockRow = deleting ? " FOR UPDATE" : "";
        var lines = (await db.QueryAsync(@"SELECT ComponentId,ValueText,CalculationType,Formula,BaseComponent,ProRataOverride,RoundingMode,SortOrder
FROM salarystructurelines WHERE StructureId=@templateId ORDER BY SortOrder,Id" + lockRow, args, tx)).AsList();
        var version = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { row, lines })));
        var mappings = new List<SalaryTemplateMapping>();
        if (owners.Count != 1 || row.ClientId > 0 && row.ClientId != clientId)
        {
            var count = owners.Append(row.ClientId).Where(id => id > 0).Distinct().Count();
            mappings.Add(new("Shared template", Math.Max(count, 1), [owners.Count == 0 ? "Global salary template" : $"Shared across {count} clients"]));
        }
        else
            await ReadSalaryTemplateMappingsAsync(db, tx, clientId, templateId, row.Active && lines.Count > 0, designs, mappings);

        var preview = new SalaryTemplateDeletionPreview(templateId, row.Name, mappings, version);
        if (!deleting || version != expectedVersion || mappings.Count != 0) return (preview, false);

        await PayrollDataTableStore.ReserveDeletedTemplateIdAsync(db, tx, clientId, templateId);
        await db.ExecuteAsync("DELETE FROM salarystructurelines WHERE StructureId=@templateId", args, tx);
        if (await db.ExecuteAsync("DELETE FROM salarystructures WHERE Id=@templateId", args, tx) != 1)
            throw new DBConcurrencyException("The salary template changed during deletion. Refresh the list.");
        await tx.CommitAsync();
        return (preview, true);
    }

    private static async Task ReadSalaryTemplateMappingsAsync(MySqlConnection db, MySqlTransaction tx, int clientId,
        long templateId, bool activeWithLines, PayslipDesignRepository designs, List<SalaryTemplateMapping> mappings)
    {
        var key = templateId.ToString(CultureInfo.InvariantCulture);
        var args = new { clientId, templateId, key, named = ReviewedNamedFormulaInputs.ProfileKey(key), reported = ReportedPeriodInputProfileStore.Key(key),
            profile = ExcelPayslipRepository.MappingProfileStorageCode(clientId) };
        // Some installations have never enabled the optional payroll/location/import modules.
        // If a table exists, a missing column or an unreadable query must fail closed.
        var tables = (await db.QueryAsync<string>(@"SELECT table_name FROM information_schema.tables WHERE table_schema=DATABASE()
AND table_name IN ('employee_it0008_basic_pay','employee_payroll_location_assignments','excel_payslip_batches',
'excel_payroll_setup_imports','payrunemployees','payroll_calculation_traces')", transaction: tx)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        async Task Add(string type, string query)
        {
            var examples = (await db.QueryAsync<(string Label, int Total)>($"""
                SELECT Label,COUNT(*) OVER() AS Total FROM ({query}) AS mapping ORDER BY Label LIMIT 5
                """, args, tx)).AsList();
            if (examples.Count != 0) mappings.Add(new(type, examples[0].Total, examples.Select(x => x.Label).ToArray()));
        }

        await Add("Employee Master", """
            SELECT CASE WHEN ClientId=@clientId THEN CONCAT(EmployeeCode,' · ',TRIM(CONCAT_WS(' ',FirstName,LastName)))
                        ELSE 'Assignment outside this client' END Label
            FROM employees WHERE SalaryStructureId=@key
            """);
        if (tables.Contains("employee_it0008_basic_pay"))
            await Add("Employee salary history", """
                SELECT CASE WHEN ClientId=@clientId THEN CONCAT('Employee #',EmployeeId,' · ',DATE_FORMAT(EffectiveFrom,'%Y-%m-%d'),' · ',Status)
                            ELSE 'Salary history outside this client' END Label
                FROM employee_it0008_basic_pay WHERE SalaryStructureId=@key
                """);
        if (tables.Contains("employee_payroll_location_assignments"))
            await Add("Location salary assignments", """
                SELECT CASE WHEN client_id=@clientId THEN CONCAT('Employee #',employee_id,' · location #',work_location_id,' · ',status)
                            ELSE 'Salary assignment outside this client' END Label
                FROM employee_payroll_location_assignments WHERE salary_structure_id=@key
                """);

        var policies = (await PfPolicyRepository.ReadVersionsAsync(db, tx, clientId)).Where(p => p.SalaryStructureId == key).ToArray();
        if (policies.Length > 0) mappings.Add(new("PF policy versions", policies.Length,
            policies.Take(5).Select(p => $"{p.Name} · v{p.VersionNumber} · {p.Status}").ToArray()));
        await Add("Managed formula input declarations", """
            SELECT CASE WHEN ModuleCode=@named THEN 'Named formula inputs' ELSE 'Reported period inputs' END Label
            FROM modulesettings WHERE ModuleCode IN (@named,@reported)
            """);
        await Add("Excel payslip header mappings", """
            SELECT LEFT(header.signature,12) Label FROM modulesettings m
            JOIN JSON_TABLE(JSON_KEYS(m.SettingsJson),'$[*]' COLUMNS(signature VARCHAR(128) PATH '$')) header
            WHERE m.client_id=@clientId AND m.ModuleCode IN ('excel_payslip_profile',@profile)
              AND JSON_UNQUOTE(JSON_EXTRACT(m.SettingsJson,CONCAT('$.',JSON_QUOTE(header.signature),'.salaryTemplateId')))=@key
            GROUP BY header.signature
            """);
        if (tables.Contains("excel_payslip_batches"))
            await Add("Saved Excel payslip batches", """
                SELECT CONCAT(COALESCE(JSON_UNQUOTE(JSON_EXTRACT(batch_json,'$.month')),''),' · ',CONVERT(id USING utf8mb4)) Label
                FROM excel_payslip_batches WHERE client_id=@clientId AND JSON_UNQUOTE(JSON_EXTRACT(batch_json,'$.salaryTemplateId'))=@key
                """);
        if (tables.Contains("excel_payroll_setup_imports"))
            await Add("Excel payroll setup records", """
                SELECT CONCAT(status,' · ',id) Label FROM excel_payroll_setup_imports WHERE client_id=@clientId AND (
                   JSON_OVERLAPS(JSON_EXTRACT(request_json,'$**.existingTemplateId'),JSON_ARRAY(@key,@templateId))
                OR JSON_OVERLAPS(JSON_EXTRACT(request_json,'$**.templateId'),JSON_ARRAY(@key,@templateId))
                OR JSON_OVERLAPS(JSON_EXTRACT(result_json,'$**.templateId'),JSON_ARRAY(@key,@templateId))
                OR JSON_OVERLAPS(JSON_EXTRACT(result_json,'$**.templateIds[*]'),JSON_ARRAY(@key,@templateId)))
                """);
        if (tables.Contains("payrunemployees"))
            await Add("Saved payroll", """
                SELECT DISTINCT CONCAT('Run #',PayRunId) Label FROM payrunemployees WHERE ClientId=@clientId AND (
                   JSON_OVERLAPS(JSON_EXTRACT(DetailsJson,'$**.SalaryStructureId'),JSON_ARRAY(@key,@templateId))
                OR JSON_OVERLAPS(JSON_EXTRACT(DetailsJson,'$**.salaryStructureId'),JSON_ARRAY(@key,@templateId)))
                """);
        if (tables.Contains("payroll_calculation_traces"))
            await Add("Saved payroll calculation traces", """
                SELECT DISTINCT CONCAT('Run #',t.PayRunId) Label FROM payruns r JOIN payroll_calculation_traces t ON t.PayRunId=r.Id
                WHERE r.ClientId=@clientId AND JSON_UNQUOTE(JSON_EXTRACT(t.InputJson,'$.nativeCurrencyContext.templateId'))=@key
                """);

        if (activeWithLines)
        {
            // The payslip designer needs at least one active client template with component lines.
            var otherScopes = await db.QueryAsync<string>(@"SELECT COALESCE(NULLIF(TRIM(ClientRef),''),CAST(ClientId AS CHAR))
FROM salarystructures s WHERE Active=TRUE AND Id<>@templateId AND EXISTS(SELECT 1 FROM salarystructurelines l
WHERE l.StructureId=s.Id AND TRIM(l.ComponentId)<>'')", args, tx);
            if (!otherScopes.Any(scope => ReadClientRefs(new JsonObject { ["clientId"] = scope })?.Contains(clientId) == true))
                await Add("Payslip templates requiring an active salary template", "SELECT Name Label FROM paysliptemplates WHERE ClientId=@clientId");
            else
            {
                var blockedDesigns = await designs.ListSalaryTemplatePayslipDependenciesAsync(db, tx, clientId, templateId);
                if (blockedDesigns.Count > 0) mappings.Add(new("Payslip template field mappings", blockedDesigns.Count,
                    blockedDesigns.Take(5).Select(d => $"{d.Name} · {string.Join(", ", d.Codes)}").ToArray()) { PayslipDesigns = blockedDesigns });
            }
        }
    }

    private sealed record DeletionHeader(long Id, int ClientId, string ClientRef, string Name, decimal AnnualCtc, bool Active);

    private static async Task<(DeletionHeader Header, List<int> Owners)?> ReadDeletionScopeAsync(MySqlConnection db, MySqlTransaction tx, int clientId, long templateId, bool locked)
    {
        var suffix = locked ? " FOR UPDATE" : "";
        if (await db.ExecuteScalarAsync<int?>("SELECT Id FROM clients WHERE Id=@clientId AND IsActive=TRUE" + suffix, new { clientId }, tx) is null) return null;
        var row = await db.QuerySingleOrDefaultAsync<DeletionHeader>(
            "SELECT Id,ClientId,ClientRef,Name,AnnualCtc,Active FROM salarystructures WHERE Id=@templateId" + suffix, new { templateId }, tx);
        if (row is null) return null;
        var raw = string.IsNullOrWhiteSpace(row.ClientRef) ? row.ClientId.ToString(CultureInfo.InvariantCulture) : row.ClientRef;
        var owners = ReadClientRefs(new JsonObject { ["clientId"] = raw });
        return owners is null || owners.Count > 0 && !owners.Contains(clientId) ? null : (row, owners);
    }
}
