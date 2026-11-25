using System.Globalization;
using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class PayslipDesignRepository
{
    internal static async Task RequireClientSalaryTemplateAsync(MySqlConnection db, MySqlTransaction tx, int clientId)
    {
        using var setup = JsonDocument.Parse(await PayrollDataTableStore.GetSetupJsonAsync(db, tx));
        if (clientId <= 0 || !ClientSalaryStructures(setup.RootElement, clientId).Any())
            throw new InvalidOperationException("Create and save an active salary template for this client before configuring its payslip.");
    }

    internal static async Task<IReadOnlyList<PayslipDesignCode>> ClientSalaryCodesAsync(MySqlConnection db, MySqlTransaction tx, int clientId, bool templateOnly = false)
    {
        using var setup = JsonDocument.Parse(await PayrollDataTableStore.GetSetupJsonAsync(db, tx));
        var structures = ClientSalaryStructures(setup.RootElement, clientId).ToArray();
        if (clientId <= 0 || structures.Length == 0)
            throw new InvalidOperationException("Create and save an active salary template for this client before configuring its payslip.");

        var configured = ConfiguredSalaryCodes(setup.RootElement, structures);
        if (templateOnly) return configured.OrderBy(component => component.Category, StringComparer.Ordinal)
            .ThenBy(component => component.Name, StringComparer.Ordinal).ThenBy(component => component.Code, StringComparer.Ordinal).ToArray();
        return await WithClientPayrollOutputsAsync(db, tx, clientId, setup.RootElement, configured);
    }

    private static PayslipDesignCode[] ConfiguredSalaryCodes(JsonElement setup, IEnumerable<JsonElement> structures)
    {
        // Reuse the setup projection: it resolves ClientRef/ClientId and the
        // normalized structure/line tables exactly as the salary designer does.
        // Compare complete component IDs; never match another client's codes by
        // substring, name, formula dependency or a shared component default.
        var componentIds = structures.SelectMany(structure => ClientSalaryArray(structure, "lines"))
            .Select(line => ClientSalaryText(line, "componentId")).ToHashSet(StringComparer.Ordinal);
        return ClientSalaryArray(setup, "salaryComponents")
            .Where(component => ClientSalaryActive(component) && componentIds.Contains(ClientSalaryText(component, "id")))
            .Select(component => new PayslipDesignCode(ClientSalaryText(component, "code"),
                ClientSalaryText(component, "name"), ClientSalaryText(component, "category")))
            .ToArray();
    }

    private static async Task<IReadOnlyList<PayslipDesignCode>> WithClientPayrollOutputsAsync(
        MySqlConnection db, MySqlTransaction tx, int clientId, JsonElement setup, IEnumerable<PayslipDesignCode> configured)
    {
        var codes = new Dictionary<(string Code, string Kind), PayslipDesignCode>();
        void Add(PayslipDesignCode component)
        {
            var kind = NativePayslipDesignService.LineKind(component.Category);
            if (kind == "") return;
            if (string.IsNullOrWhiteSpace(component.Code) || component.Code.Length > 100)
                throw new InvalidOperationException("A client native payroll output has no supported stable component code. Review its saved payroll mapping.");
            var name = string.IsNullOrWhiteSpace(component.Name) ? component.Code : component.Name;
            // Names are presentation labels only. Stable code/kind identities
            // remain exact, including historical codes without a current master.
            codes.TryAdd((component.Code, kind), component with { Name = name.Length > 150 ? name[..150] : name });
            if (codes.Count > 300) throw new InvalidOperationException("This client's native payroll component catalogue exceeds the bounded design review.");
        }
        foreach (var component in configured) Add(component);

        // Native policy and approved adjustments can append codes outside the
        // salary template. Read the client's own frozen output identifiers,
        // without returning employee, attendance or salary amounts.
        var saved = (await db.QueryAsync<PayslipDesignCode>(@"
SELECT Code,MIN(Name) Name,Category FROM (
 SELECT COALESCE(NULLIF(item.Code,''),item.LowerCode,'') Code,
 COALESCE(NULLIF(item.Name,''),item.LowerName,'') Name,
 COALESCE(NULLIF(item.Category,''),item.LowerCategory,'') Category
 FROM payrunemployees p JOIN payruns r ON r.Id=p.PayRunId AND r.ClientId=p.ClientId
 CROSS JOIN JSON_TABLE(p.DetailsJson,'$[*]' COLUMNS(
 Code VARCHAR(512) PATH '$.Id' NULL ON EMPTY,LowerCode VARCHAR(512) PATH '$.id' NULL ON EMPTY,
 Name VARCHAR(512) PATH '$.Name' NULL ON EMPTY,LowerName VARCHAR(512) PATH '$.name' NULL ON EMPTY,
 Category VARCHAR(100) PATH '$.Category' NULL ON EMPTY,LowerCategory VARCHAR(100) PATH '$.category' NULL ON EMPTY)) item
 WHERE p.ClientId=@ClientId AND p.IsSkipped=FALSE
) client_outputs
WHERE LOWER(Category) IN ('earning','reimbursement','deduction','benefit','employer','employer contribution','employercontribution')
GROUP BY Code,Category LIMIT 301", new { ClientId = clientId }, tx)).ToArray();
        if (saved.Length > 300) throw new InvalidOperationException("This client's saved native payroll component catalogue exceeds the bounded design review.");
        foreach (var component in saved) Add(component);

        var adjustments = (await db.QueryAsync<PayslipDesignCode>(@"
SELECT DISTINCT COALESCE(NULLIF(TRIM(ComponentCode),''),CONCAT('ADJ_',ComponentId)) Code,
COALESCE(NULLIF(TRIM(ComponentName),''),'Payroll adjustment') Name,
CASE WHEN LOWER(AdjustmentType) IN ('deduction','recovery') THEN 'Deduction'
 WHEN LOWER(AdjustmentType)='benefit' THEN 'Benefit'
 WHEN LOWER(AdjustmentType)='reimbursement' THEN 'Reimbursement' ELSE 'Earning' END Category
FROM payrolladjustments WHERE ClientId=@ClientId AND Status='Approved' AND Amount>0 LIMIT 301",
            new { ClientId = clientId }, tx)).ToArray();
        if (adjustments.Length > 300) throw new InvalidOperationException("This client's approved adjustment component catalogue exceeds the bounded design review.");
        foreach (var component in adjustments) Add(component);

        var taxCodes = await db.QueryAsync<string>(@"
SELECT DISTINCT UPPER(COALESCE(NULLIF(TRIM(tax_deduction_component_code),''),'TDS'))
FROM tax_client_settings WHERE client_id=@ClientId AND active=TRUE AND enabled=TRUE AND project_monthly_tds=TRUE
UNION SELECT 'TDS' WHERE EXISTS(SELECT 1 FROM tax_rule_versions v WHERE v.active=TRUE
 AND NOT EXISTS(SELECT 1 FROM tax_client_settings s WHERE s.client_id=@ClientId AND s.financial_year=v.financial_year AND s.active=TRUE))",
            new { ClientId = clientId }, tx);
        foreach (var code in taxCodes) Add(new(code, "Income Tax (TDS)", "Deduction"));

        var personal = await db.QuerySingleAsync<ClientPersonalCodes>(@"
SELECT COALESCE(MAX(CASE WHEN CAST(pd.EsicEmployee AS CHAR) REGEXP '^-?[0-9]+(\\.[0-9]+)?$' AND CAST(pd.EsicEmployee AS DECIMAL(18,4))>0 THEN 1 ELSE 0 END),0) Esic,
COALESCE(MAX(pd.PtLwfWorkmenComp>0),0) Pt,COALESCE(MAX(pd.Tds>0),0) Tds,COALESCE(MAX(pd.Recovery>0),0) Recovery
FROM employees e JOIN employeepersonaldetails pd ON pd.EmployeeId=e.Id WHERE e.ClientId=@ClientId",
            new { ClientId = clientId }, tx);
        if (personal.Esic) Add(new("ESIC", "Employee ESIC", "Deduction"));
        if (personal.Pt) Add(new("PT", "Professional Tax", "Deduction"));
        if (personal.Tds) Add(new("TDS", "Income Tax (TDS)", "Deduction"));
        if (personal.Recovery) Add(new("RECOVERY", "Recovery", "Deduction"));
        var ptEnabled = setup.TryGetProperty("statutory", out var statutory)
            && statutory.ValueKind == JsonValueKind.Object
            && (statutory.TryGetProperty("pt", out var pt) && pt.ValueKind == JsonValueKind.True
                || ClientSalaryArray(statutory, "ptStateSlabs").Any(slab => decimal.TryParse(ClientSalaryText(slab,"deductionAmount"),NumberStyles.Number,CultureInfo.InvariantCulture,out var amount) && amount>0));
        if (ptEnabled || await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM modulesettings WHERE client_id=@ClientId AND IsEnabled=TRUE AND ModuleCode LIKE 'payroll.pt.reviewed:%')",new { ClientId=clientId },tx))
            Add(new("PT", "Professional Tax", "Deduction"));
        return codes.Values.OrderBy(component => component.Category, StringComparer.Ordinal)
            .ThenBy(component => component.Name, StringComparer.Ordinal)
            .ThenBy(component => component.Code, StringComparer.Ordinal).ToArray();
    }

    private sealed class ClientPersonalCodes
    {
        public bool Esic { get; set; }
        public bool Pt { get; set; }
        public bool Tds { get; set; }
        public bool Recovery { get; set; }
    }

    private static IEnumerable<JsonElement> ClientSalaryStructures(JsonElement setup, int clientId)
    {
        if (clientId <= 0) yield break;
        var expected = clientId.ToString(CultureInfo.InvariantCulture);
        foreach (var structure in ClientSalaryArray(setup, "salaryStructures"))
            if (ClientSalaryActive(structure) && ClientSalaryArray(structure, "lines")
                .Any(line => !string.IsNullOrWhiteSpace(ClientSalaryText(line, "componentId")))
                && ClientSalaryText(structure, "clientId")
                .Split([';', ',', '|'], StringSplitOptions.RemoveEmptyEntries)
                .Any(reference => reference.Split(':')[0].Trim() == expected))
                yield return structure;
    }

    private static IEnumerable<JsonElement> ClientSalaryArray(JsonElement owner, string name)
    {
        if (owner.ValueKind != JsonValueKind.Object || !owner.TryGetProperty(name, out var array)
            || array.ValueKind != JsonValueKind.Array) yield break;
        foreach (var item in array.EnumerateArray())
            if (item.ValueKind == JsonValueKind.Object) yield return item;
    }

    private static string ClientSalaryText(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number ? value.ToString() : "";

    private static bool ClientSalaryActive(JsonElement owner) =>
        owner.TryGetProperty("active", out var active) && active.ValueKind == JsonValueKind.True;
}
