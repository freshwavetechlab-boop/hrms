using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using MimeKit;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

// Excel amounts never use employee/pay-run data or salary formulas. Templates provide display mapping presets only.
public sealed partial class ExcelPayslipRepository(IConfiguration configuration, NotificationRepository notifications, ExcelPayslipPdfService pdfService)
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    internal const string BatchPrefix = "excel_payslip:";
    private const string ProfileCode = "excel_payslip_profile";
    private const int MaximumBatchBytes = 8 * 1024 * 1024;
    private const int MaximumProfileBytes = 64 * 1024;
    private const decimal MaximumAmount = 10000000000m;
    private MySqlConnection Db() => new(configuration.GetConnectionString("Default"));

    internal static bool IsSupportedClient(Client client) => client.Id > 0 && client.IsActive;

    public async Task<bool> IsSupportedClientAsync(int clientId)
    {
        await using var db = Db(); await db.OpenAsync();
        return await SupportedAsync(db, clientId);
    }

    private static async Task<bool> SupportedAsync(MySqlConnection db, int clientId, MySqlTransaction? tx = null)
    {
        if (clientId <= 0) return false;
        var client = await db.QueryFirstOrDefaultAsync<Client>("SELECT Id,Name,Code,IsActive FROM clients WHERE Id=@ClientId", new { ClientId = clientId }, tx);
        return client is not null && IsSupportedClient(client);
    }

    public async Task<IReadOnlyList<ExcelPayslipClient>> ListClientsAsync(int? scopedClientId = null)
    {
        await using var db = Db(); await db.OpenAsync();
        var clients = await db.QueryAsync<Client>("SELECT Id,Name,Code,IsActive FROM clients WHERE IsActive=TRUE AND (@ClientId IS NULL OR Id=@ClientId) ORDER BY Name", new { ClientId = scopedClientId });
        return clients.Where(IsSupportedClient).Select(c => new ExcelPayslipClient { Id = c.Id, Name = c.Name }).ToArray();
    }

    public async Task<IReadOnlyList<ExcelPayslipTemplate>> GetTemplatesAsync(int clientId)
    {
        await using var db = Db(); await db.OpenAsync();
        if (!await SupportedAsync(db, clientId)) return [];
        return await ReadTemplatesAsync(db, clientId);
    }

    private static async Task<IReadOnlyList<ExcelPayslipTemplate>> ReadTemplatesAsync(MySqlConnection db, int clientId, MySqlTransaction? tx = null)
    {
        // Mapping only needs this client's labels. Keep the legacy JSON component fallback,
        // but do not load every client's salary setup and unrelated statutory/payslip settings.
        using var result = await db.QueryMultipleAsync(@"
SELECT CAST(s.Id AS CHAR) AS Id,
COALESCE(NULLIF(TRIM(s.ClientRef),''),CAST(s.ClientId AS CHAR)) AS ClientRef,
s.Name,l.ComponentId,CAST(c.Id AS CHAR) AS MasterId,c.Code,c.Name AS ComponentName,c.Category
FROM salarystructures s
LEFT JOIN salarystructurelines l ON l.StructureId=s.Id
LEFT JOIN salarycomponents c ON BINARY CAST(c.Id AS CHAR)=BINARY l.ComponentId AND c.Active=TRUE
WHERE s.Active=TRUE AND CAST(SUBSTRING_INDEX(COALESCE(NULLIF(TRIM(s.ClientRef),''),CAST(s.ClientId AS CHAR)),':',1) AS SIGNED)=@ClientId
ORDER BY s.Id,l.SortOrder,l.Id;
SELECT CAST(JSON_EXTRACT(SetupJson,'$.salaryComponents') AS CHAR) FROM payrollsetups
WHERE NOT EXISTS (SELECT 1 FROM salarycomponents) ORDER BY Id LIMIT 1;", new { ClientId = clientId }, tx);
        var rows = (await result.ReadAsync<TemplateMappingRow>()).ToArray();
        var legacyComponents = await result.ReadFirstOrDefaultAsync<string?>();
        return ReadTemplateMappings(rows, legacyComponents, clientId);
    }

    internal static IReadOnlyList<ExcelPayslipTemplate> ReadTemplateMappings(IEnumerable<TemplateMappingRow> rows, string? legacyComponents, int clientId)
    {
        var mapped = rows.ToArray();
        var structures = mapped.GroupBy(r => r.Id, StringComparer.Ordinal).Select(g => new
        {
            id = g.Key, clientId = g.First().ClientRef, name = g.First().Name,
            lines = g.Where(r => r.ComponentId is not null).Select(r => new { componentId = r.ComponentId })
        });
        var components = legacyComponents is null ? JsonSerializer.SerializeToElement(mapped.Where(r => r.MasterId is not null)
            .Select(r => new { id = r.MasterId, code = r.Code, name = r.ComponentName, category = r.Category }), JsonOptions)
            : JsonSerializer.Deserialize<JsonElement>(legacyComponents, JsonOptions);
        return ReadTemplates(JsonSerializer.Serialize(new { salaryStructures = structures, salaryComponents = components }, JsonOptions), clientId);
    }

    internal sealed class TemplateMappingRow
    {
        public string Id { get; set; } = "";
        public string ClientRef { get; set; } = "";
        public string Name { get; set; } = "";
        public string? ComponentId { get; set; }
        public string? MasterId { get; set; }
        public string Code { get; set; } = "";
        public string ComponentName { get; set; } = "";
        public string Category { get; set; } = "";
    }

    internal static IReadOnlyList<ExcelPayslipTemplate> ReadTemplates(string setupJson, int clientId)
    {
        if (clientId <= 0) return [];
        using var document = JsonDocument.Parse(setupJson);
        var root = document.RootElement;
        if (!root.TryGetProperty("salaryStructures", out var structures) || structures.ValueKind != JsonValueKind.Array
            || !root.TryGetProperty("salaryComponents", out var components) || components.ValueKind != JsonValueKind.Array) return [];
        static string Text(JsonElement item, string name) => item.TryGetProperty(name, out var value) ? value.ToString() : "";
        static bool Active(JsonElement item) => !item.TryGetProperty("active", out var value) || value.ValueKind != JsonValueKind.False;
        var masters = components.EnumerateArray().Where(Active).Where(c => !string.IsNullOrWhiteSpace(Text(c, "id")))
            .GroupBy(c => Text(c, "id"), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var templates = new List<ExcelPayslipTemplate>();
        foreach (var structure in structures.EnumerateArray().Where(Active))
        {
            if (!int.TryParse(Text(structure, "clientId").Split(':')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var owner) || owner != clientId) continue;
            var id = Text(structure, "id");
            if (string.IsNullOrWhiteSpace(id) || !structure.TryGetProperty("lines", out var lines) || lines.ValueKind != JsonValueKind.Array) continue;
            var template = new ExcelPayslipTemplate { Id = id, Name = Text(structure, "name") };
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in lines.EnumerateArray())
            {
                var componentId = Text(line, "componentId");
                if (!used.Add(componentId) || !masters.TryGetValue(componentId, out var component)) continue;
                var category = Text(component, "category").ToLowerInvariant() switch
                {
                    "earning" or "reimbursement" => "Earning", "deduction" => "Deduction", "benefit" => "Employer", _ => "Information"
                };
                template.Components.Add(new() { Id = componentId, Code = Text(component, "code"), Name = Text(component, "name"), Category = category });
            }
            templates.Add(template);
        }
        return templates.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<IReadOnlyList<ExcelPayslipBatchSummary>> ListAsync(int clientId)
    {
        await using var db = Db(); await db.OpenAsync();
        if (!await SupportedAsync(db, clientId)) return [];
        return await ReadBatchSummariesAsync(db, clientId);
    }

    private static async Task<IReadOnlyList<ExcelPayslipBatchSummary>> ReadBatchSummariesAsync(MySqlConnection db, int clientId)
    {
        return (await db.QueryAsync<ExcelPayslipBatchSummary>(@"SELECT
id AS Id,client_id AS ClientId,
COALESCE(JSON_UNQUOTE(JSON_EXTRACT(batch_json,'$.clientName')),'') AS ClientName,
COALESCE(JSON_UNQUOTE(JSON_EXTRACT(batch_json,'$.salaryTemplateId')),'') AS SalaryTemplateId,
COALESCE(JSON_UNQUOTE(JSON_EXTRACT(batch_json,'$.salaryTemplateName')),'') AS SalaryTemplateName,
JSON_UNQUOTE(JSON_EXTRACT(batch_json,'$.month')) AS Month,
JSON_UNQUOTE(JSON_EXTRACT(batch_json,'$.sourceFileName')) AS SourceFileName,
JSON_UNQUOTE(JSON_EXTRACT(batch_json,'$.sheetName')) AS SheetName,
JSON_LENGTH(JSON_EXTRACT(batch_json,'$.rows')) AS RowCount,
JSON_UNQUOTE(JSON_EXTRACT(batch_json,'$.createdAtUtc')) AS CreatedAtUtc,
JSON_UNQUOTE(JSON_EXTRACT(batch_json,'$.createdBy')) AS CreatedBy
FROM excel_payslip_batches WHERE client_id=@ClientId
ORDER BY created_at DESC,id DESC", new { ClientId = clientId })).ToArray();
    }

    public async Task<ExcelPayslipBatch?> GetAsync(int clientId, string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) return null;
        await using var db = Db(); await db.OpenAsync();
        if (!await SupportedAsync(db, clientId)) return null;
        return await ReadBatchAsync(db, clientId, id);
    }

    private static async Task<ExcelPayslipBatch?> ReadBatchAsync(MySqlConnection db, int clientId, string id, MySqlTransaction? tx = null)
    {
        var json = await db.ExecuteScalarAsync<string?>("SELECT CAST(batch_json AS CHAR) FROM excel_payslip_batches WHERE client_id=@ClientId AND id=@Id", new { ClientId = clientId, Id = id }, tx);
        var batch = string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<ExcelPayslipBatch>(json, JsonOptions);
        return batch?.ClientId == clientId && batch.Id == id ? batch : null;
    }

    public async Task<(ExcelPayslipBatch? Item, string? Error)> SaveAsync(int clientId, ExcelPayslipBatch request, string actor)
    {
        var error = ValidateBatch(request);
        if (error is not null) return (null, error);
        // Clone before normalizing warnings/metadata so callers cannot modify an accepted snapshot in memory.
        var batch = JsonSerializer.Deserialize<ExcelPayslipBatch>(JsonSerializer.Serialize(request, JsonOptions), JsonOptions)!;
        var calculationSource = batch.CalculationSource;
        batch.CalculationSource = null;
        if (calculationSource is not null)
        {
            var sourceError = ExcelPayslipCalculationService.ValidateSource(calculationSource, batch);
            if (sourceError is not null) return (null, sourceError);
            if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(calculationSource, JsonOptions)) > 16 * 1024 * 1024)
                return (null, "Calculation source exceeds 16 MiB. Save a smaller worksheet.");
        }
        batch.Id = Guid.NewGuid().ToString("N"); batch.ClientId = clientId;
        batch.ClientName = ""; batch.SalaryTemplateName = ""; batch.SalaryTemplateId = batch.SalaryTemplateId?.Trim() ?? "";
        batch.CreatedAtUtc = DateTime.UtcNow; batch.CreatedBy = Limit(actor, 190);
        foreach (var row in batch.Rows) row.Warnings = RowWarnings(row);
        if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(batch, JsonOptions)) > MaximumBatchBytes) return (null, "The batch is too large. Save a smaller selection of source rows (maximum 8 MiB).");
        await using var db = Db(); await db.OpenAsync(); await using var tx = await db.BeginTransactionAsync();
        // Reuse the per-client transaction lock used for delivery; no master records are changed.
        // All saves take the lock so a supplied code cannot race an auto-assigned code.
        var client = await db.QueryFirstOrDefaultAsync<Client>("SELECT Id,Name,Code,IsActive FROM clients WHERE Id=@ClientId FOR UPDATE", new { ClientId = clientId }, tx);
        if (client is null || !IsSupportedClient(client)) return (null, "Select an active client.");
        var historicalCodes = batch.Rows.Any(row => string.IsNullOrWhiteSpace(row.EmployeeCode))
            ? await db.QueryAsync<string>(@"SELECT DISTINCT codes.employee_code
FROM excel_payslip_batches batches
CROSS JOIN JSON_TABLE(batches.batch_json,'$.rows[*]' COLUMNS(employee_code VARCHAR(80) PATH '$.employeeCode' NULL ON EMPTY)) codes
WHERE batches.client_id=@ClientId AND codes.employee_code IS NOT NULL AND TRIM(codes.employee_code)<>''", new { ClientId = clientId }, tx)
            : [];
        var masterEmployees = batch.Rows.Any(row => string.IsNullOrWhiteSpace(row.EmployeeCode))
            ? await ReadEmployeeCodeMatchesAsync(db, clientId, tx)
            : [];
        try { AssignEmployeeCodes(batch, calculationSource, client.Code, historicalCodes, masterEmployees); }
        catch (InvalidOperationException codeError) { return (null, codeError.Message); }
        if (calculationSource is not null)
        {
            var sourceError = ExcelPayslipCalculationService.ValidateSource(calculationSource, batch);
            if (sourceError is not null) return (null, sourceError);
            if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(calculationSource, JsonOptions)) > 16 * 1024 * 1024)
                return (null, "Calculation source exceeds 16 MiB. Save a smaller worksheet.");
        }
        ExcelPayslipBatch? calculationParent = null;
        if (!string.IsNullOrEmpty(calculationSource?.SourceBatchId))
        {
            calculationParent = await ReadBatchAsync(db, clientId, calculationSource.SourceBatchId, tx);
            if (calculationParent is null) return (null, "The calculation source batch does not belong to this client.");
            if (batch.SalaryTemplateId != calculationParent.SalaryTemplateId) return (null, "Keep the source batch's saved component mapping when calculating another month.");
        }
        batch.ClientName = client.Name;
        if (calculationParent is not null) batch.SalaryTemplateName = calculationParent.SalaryTemplateName;
        else if (!string.IsNullOrWhiteSpace(batch.SalaryTemplateId))
        {
            var template = (await ReadTemplatesAsync(db, clientId, tx)).SingleOrDefault(t => t.Id == batch.SalaryTemplateId);
            if (template is null) return (null, "Select an active salary template belonging to this client.");
            batch.SalaryTemplateName = template.Name;
        }
        try { pdfService.ValidateLayout(batch, batch.Rows); }
        catch (InvalidOperationException layoutError) { return (null, layoutError.Message); }
        var json = JsonSerializer.Serialize(batch, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > MaximumBatchBytes) return (null, "The batch is too large. Save a smaller selection of source rows (maximum 8 MiB).");
        await db.ExecuteAsync("INSERT INTO excel_payslip_batches(client_id,id,batch_json,send_state_json,created_at) VALUES(@ClientId,@Id,CAST(@Json AS JSON),JSON_OBJECT(),@CreatedAtUtc)", new { ClientId = clientId, batch.Id, Json = json, batch.CreatedAtUtc }, tx);
        if (calculationSource is not null)
            await db.ExecuteAsync("INSERT INTO excel_payslip_calculation_sources(client_id,batch_id,source_json,created_at) VALUES(@ClientId,@Id,CAST(@Json AS JSON),@CreatedAtUtc)",
                new { ClientId = clientId, batch.Id, Json = JsonSerializer.Serialize(calculationSource, JsonOptions), batch.CreatedAtUtc }, tx);
        await tx.CommitAsync(); return (batch, null);
    }

    public async Task<JsonElement?> GetProfileAsync(int clientId, string signature)
    {
        if (!ValidSignature(signature)) return null;
        await using var db = Db(); await db.OpenAsync();
        if (!await SupportedAsync(db, clientId)) return null;
        var json = await db.ExecuteScalarAsync<string?>("SELECT CAST(SettingsJson AS CHAR) FROM modulesettings WHERE client_id=@ClientId AND ModuleCode=@Code", new { ClientId = clientId, Code = ProfileCode });
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty(signature.ToLowerInvariant(), out var saved) ? saved.Clone() : null;
    }

    public async Task<(bool Ok, string? Error)> SaveProfileAsync(int clientId, string signature, JsonElement profile)
    {
        var error = ValidateProfile(profile); if (error is not null) return (false, error);
        if (!ValidSignature(signature) || !signature.Equals(profile.GetProperty("headerSignature").GetString(), StringComparison.OrdinalIgnoreCase)) return (false, "The mapping profile must match its SHA-256 header signature.");
        await using var db = Db(); await db.OpenAsync(); await using var tx = await db.BeginTransactionAsync();
        if (!await SupportedAsync(db, clientId, tx)) return (false, "Select an active client.");
        if (profile.TryGetProperty("salaryTemplateId", out var selectedTemplate) && !string.IsNullOrWhiteSpace(selectedTemplate.GetString()))
        {
            var templates = await ReadTemplatesAsync(db, clientId, tx);
            if (!templates.Any(t => t.Id == selectedTemplate.GetString()!.Trim())) return (false, "The mapping profile salary template must belong to this client and be active.");
        }
        await db.ExecuteAsync("INSERT INTO modulesettings(client_id,ModuleCode,IsEnabled,SettingsJson) VALUES(@ClientId,@Code,TRUE,JSON_OBJECT()) ON DUPLICATE KEY UPDATE ModuleCode=VALUES(ModuleCode)", new { ClientId = clientId, Code = ProfileCode }, tx);
        var json = await db.ExecuteScalarAsync<string>("SELECT CAST(SettingsJson AS CHAR) FROM modulesettings WHERE client_id=@ClientId AND ModuleCode=@Code FOR UPDATE", new { ClientId = clientId, Code = ProfileCode }, tx);
        var profiles = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, JsonOptions) ?? [];
        var key = signature.ToLowerInvariant();
        if (profiles.Count >= 50 && !profiles.ContainsKey(key)) return (false, "This client already has 50 mapping formats. Reuse an existing format.");
        profiles[key] = profile.Clone();
        await db.ExecuteAsync("UPDATE modulesettings SET SettingsJson=CAST(@Json AS JSON) WHERE client_id=@ClientId AND ModuleCode=@Code", new { ClientId = clientId, Code = ProfileCode, Json = JsonSerializer.Serialize(profiles, JsonOptions) }, tx);
        await tx.CommitAsync();
        return (true, null);
    }

    private static bool ValidSignature(string? value) => Regex.IsMatch(value ?? "", @"\A[0-9a-fA-F]{64}\z");

    internal static string? ValidateProfile(JsonElement profile)
    {
        if (profile.ValueKind != JsonValueKind.Object || !profile.TryGetProperty("headerSignature", out var signature) || signature.ValueKind != JsonValueKind.String || !ValidSignature(signature.GetString())
            || !profile.TryGetProperty("columns", out var columns) || columns.ValueKind != JsonValueKind.Array)
            return "A mapping profile needs a headerSignature string and columns array.";
        if (Encoding.UTF8.GetByteCount(profile.GetRawText()) > MaximumProfileBytes) return "Mapping profiles cannot exceed 64 KiB.";
        if (profile.EnumerateObject().Any(p => p.Name is not ("headerSignature" or "columns" or "salaryTemplateId"))) return "The mapping profile contains unsupported fields.";
        if (profile.TryGetProperty("salaryTemplateId", out var template) && (template.ValueKind != JsonValueKind.String || (template.GetString()?.Length ?? 0) > 100)) return "The optional salary template ID must be a string of up to 100 characters.";
        if (columns.GetArrayLength() > 512) return "Mapping profiles support up to 512 columns.";
        var seen = new HashSet<int>();
        foreach (var column in columns.EnumerateArray())
        {
            if (column.ValueKind != JsonValueKind.Object || !column.TryGetProperty("columnIndex", out var index) || index.ValueKind != JsonValueKind.Number || !index.TryGetInt32(out var number) || number is < 0 or > 511 || !seen.Add(number)) return "Each mapping column needs a distinct integer columnIndex between 0 and 511.";
            if (!column.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String || kind.GetString() is not ("employeeName" or "employeeCode" or "email" or "info" or "earning" or "deduction" or "employer" or "netPay" or "grossTotal" or "deductionTotal" or "ignore")) return "A mapping column has an unsupported kind.";
            if (!column.TryGetProperty("sourceHeader", out var header) || header.ValueKind != JsonValueKind.String || !TextValid(header.GetString(), 512)
                || !column.TryGetProperty("label", out var label) || label.ValueKind != JsonValueKind.String || !TextValid(label.GetString(), 80, kind.GetString() != "ignore")) return "Mapping columns need a sourceHeader (up to 512 characters) and label (up to 80 characters).";
            if (column.TryGetProperty("componentId", out var component) && (component.ValueKind != JsonValueKind.String || !TextValid(component.GetString(), 100))) return "An optional mapping componentId must be a string of up to 100 characters.";
            if (column.EnumerateObject().Any(p => p.Name is not ("columnIndex" or "sourceHeader" or "label" or "kind" or "componentId"))) return "Mapping columns may contain display mappings only; formulas and other fields are not supported.";
        }
        return null;
    }

    internal static string? ValidateBatch(ExcelPayslipBatch? batch)
    {
        if (batch is null) return "Supply a payslip batch.";
        if (!DateOnly.TryParseExact(batch.Month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return "Select a valid salary month (yyyy-MM).";
        if (!TextValid(batch.SourceFileName, 255, true) || !TextValid(batch.SheetName, 100, true)) return "Supply a source filename and worksheet name within the allowed lengths.";
        if (!TextValid(batch.SalaryTemplateId ?? "", 100)) return "The optional salary template ID exceeds its allowed length.";
        if (batch.HeaderRow < 1 || batch.HeaderRow > 1048576) return "Select the source header row.";
        if (batch.Rows is null || batch.Rows.Count is < 1 or > 1000) return "A batch must contain between 1 and 1000 source rows.";
        var ids = new HashSet<string>(StringComparer.Ordinal); var sourceRows = new HashSet<int>();
        foreach (var row in batch.Rows)
        {
            if (row is null || !Regex.IsMatch(row.Id ?? "", @"\A[A-Za-z0-9._:-]{1,100}\z") || !ids.Add(row.Id)) return "Each source row needs a unique row ID (up to 100 letters, numbers, dots, underscores, colons or hyphens).";
            if (row.SourceRow <= batch.HeaderRow || row.SourceRow > 1048576 || !sourceRows.Add(row.SourceRow)) return "Source row numbers must be unique and follow the selected header row.";
            if (!TextValid(row.EmployeeName, 200, true) || !TextValid(row.EmployeeCode, 80)) return $"Source row {row.SourceRow}: supply an employee name and a valid optional display code.";
            if (!TextValid(row.Email, 254)) return $"Source row {row.SourceRow}: the optional email is too long or contains control characters.";
            if (row.Information is null || row.Information.Count > 32 || row.Information.Any(i => i is null || !TextValid(i.Label, 80, true) || !TextValid(i.Value, 1000))) return $"Source row {row.SourceRow}: information fields exceed their allowed limits.";
            if (!AmountsValid(row.Earnings, 32) || !AmountsValid(row.Deductions, 32) || !AmountsValid(row.EmployerContributions, 16)) return $"Source row {row.SourceRow}: component labels or amounts are invalid or exceed their allowed limits.";
            if (row.Earnings.Count == 0) return $"Source row {row.SourceRow}: map at least one earning amount.";
            if (!AmountValid(row.NetPay) || row.DeclaredGross is decimal gross && !AmountValid(gross) || row.DeclaredDeductions is decimal deductions && !AmountValid(deductions)) return $"Source row {row.SourceRow}: totals exceed the supported amount limit.";
        }
        return null;
    }

    private static bool AmountValid(decimal value) => value >= -MaximumAmount && value <= MaximumAmount;
    private static bool AmountsValid(List<ExcelPayslipAmount>? values, int maximum) => values is not null && values.Count <= maximum && values.All(v => v is not null && TextValid(v.Label, 80, true) && AmountValid(v.Amount));
    private static bool TextValid(string? value, int length, bool required = false) => value is not null && value.Length <= length && (!required || !string.IsNullOrWhiteSpace(value)) && !value.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t');
    private static string Limit(string? text, int limit) => string.IsNullOrEmpty(text) ? "" : text.Length <= limit ? text : text[..limit];

    internal static List<string> RowWarnings(ExcelPayslipRow row)
    {
        var warnings = new List<string>();
        var gross = row.Earnings.Sum(a => a.Amount); var deductions = row.Deductions.Sum(a => a.Amount);
        if (row.DeclaredGross.HasValue && Math.Abs(gross - row.DeclaredGross.Value) > .01m) warnings.Add("Mapped earnings do not match the source gross total. Source amounts are preserved.");
        if (row.DeclaredDeductions.HasValue && Math.Abs(deductions - row.DeclaredDeductions.Value) > .01m) warnings.Add("Mapped deductions do not match the source deduction total. Source amounts are preserved.");
        if (Math.Abs(gross - deductions - row.NetPay) > .01m) warnings.Add("Mapped earnings less deductions do not match source net pay. Source net pay is preserved.");
        if (row.Earnings.Concat(row.Deductions).Concat(row.EmployerContributions).Any(a => a.Amount < 0) || row.NetPay < 0) warnings.Add("This source row contains a negative amount; review it before export or sending.");
        return warnings;
    }

    public static (IReadOnlyList<ExcelPayslipRow> Rows, string? Error) SelectRows(ExcelPayslipBatch batch, ExcelPayslipSelection request)
    {
        if (request.AmountDecimalPlaces is not (0 or 2)) return ([], "Choose whole rupees or two decimal places for amount display.");
        if (request.RowIds is null || request.RowIds.Count is < 1 or > 1000 || request.RowIds.Any(string.IsNullOrWhiteSpace) || request.RowIds.Distinct(StringComparer.Ordinal).Count() != request.RowIds.Count) return ([], "Select one or more distinct saved source rows.");
        var selectedIds = request.RowIds.ToHashSet(StringComparer.Ordinal);
        var selected = batch.Rows.Where(r => selectedIds.Contains(r.Id)).ToArray();
        if (selected.Length != selectedIds.Count) return ([], "A selected row does not belong to this saved batch.");
        if (!request.AcknowledgeWarnings && selected.Any(r => RowWarnings(r).Count > 0)) return ([], "Review and acknowledge the selected source-row warnings before export or sending.");
        return (selected, null);
    }

    internal static bool TryEmail(string? input, out string email)
    {
        email = "";
        var value = input?.Trim() ?? "";
        if (value.Length is < 3 or > 254 || value.Any(char.IsControl) || !MailboxAddress.TryParse(value, out var mailbox) || mailbox.Address != value || !mailbox.Address.Contains('@')) return false;
        email = mailbox.Address; return true;
    }

    internal static ExcelPayslipDeliveryResult ReviewIndividualRecipients(IReadOnlyList<ExcelPayslipRow> rows, IReadOnlyDictionary<string, string> overrides) => new()
    {
        Items = rows.Select(row =>
        {
            var address = overrides.GetValueOrDefault(row.Id, row.Email);
            return TryEmail(address, out var email)
                ? DeliveryItem(row, email, "Ready", "")
                : DeliveryItem(row, address ?? "", "Error", "Add a valid recipient email for this row; no message was queued.");
        }).ToList()
    };

    public async Task<(ExcelPayslipDeliveryResult? Item, string? Error)> SendAsync(int clientId, string batchId, SendExcelPayslipsRequest request, string actor)
    {
        if (ValidateSendRequest(batchId, request, 25) is string requestError) return (null, requestError);
        var requestGuid = Guid.Parse(request.RequestId);
        await using var db = Db(); await db.OpenAsync(); await using var tx = await db.BeginTransactionAsync();
        if (!await SupportedAsync(db, clientId, tx)) return (null, "Select an active client.");
        // Serialize sends within a client, retaining request-ID uniqueness across its batches.
        await db.ExecuteScalarAsync<int>("SELECT Id FROM clients WHERE Id=@ClientId FOR UPDATE", new { ClientId = clientId }, tx);
        var stateJson = await db.ExecuteScalarAsync<string?>("SELECT CAST(send_state_json AS CHAR) FROM excel_payslip_batches WHERE client_id=@ClientId AND id=@Id FOR UPDATE", new { ClientId = clientId, Id = batchId }, tx);
        var batch = await ReadBatchAsync(db, clientId, batchId, tx);
        if (batch is null) return (null, "The saved batch was not found for this client.");
        var state = JsonSerializer.Deserialize<ExcelPayslipSendState>(stateJson!, JsonOptions) ?? new();
        var (selected, error) = SelectRows(batch, request); if (error is not null) return (null, error);
        if (request.EmailOverrides.Keys.Any(id => !selected.Any(r => r.Id == id))) return (null, "Email overrides must refer only to selected source rows.");
        if (request.Mode == "Combined" && !TryEmail(request.Email, out _)) return (null, "Enter one valid recipient email for the combined PDF.");
        var fingerprint = SendFingerprint(batchId, request);
        var requestId = requestGuid.ToString("N");
        var previousJson = state.Requests.TryGetValue(requestId, out var receipt) ? receipt.GetRawText() :
            await db.ExecuteScalarAsync<string?>("SELECT CAST(JSON_EXTRACT(send_state_json,@Path) AS CHAR) FROM excel_payslip_batches WHERE client_id=@ClientId AND id<>@Id AND JSON_CONTAINS_PATH(send_state_json,'one',@Path) LIMIT 1 FOR UPDATE",
                new { ClientId = clientId, Id = batchId, Path = "$.requests.\"" + requestId + "\"" }, tx);
        var existingResult = RestoreSendRequest(previousJson, fingerprint);
        if (existingResult.Item is not null || existingResult.Error is not null) return existingResult;
        // The locked batch's persisted history protects against new request IDs and reopened browser sessions too.
        var statuses = BuildDeliveryStatuses(batch, state, await ReadDeliveryQueueAsync(db, clientId, state, tx));
        var reviewed = ReviewDeliverySelection(selected, request, statuses);
        NotificationTemplate? mailTemplate = null;
        var dispatched = await DispatchDeliverySelectionAsync(selected, request, reviewed,
            async () =>
            {
                if (await notifications.ExcelPayslipDeliveryReadyAsync(db, tx) is string readyError) return readyError;
                mailTemplate = await NotificationRepository.ReadExcelPayslipTemplateAsync(db, tx);
                return null;
            }, async (rows, email) =>
            {
                var pdf = pdfService.Create(batch, rows, request.IncludeSeal, request.AmountDecimalPlaces);
                if (NotificationRepository.ValidateExcelPayslipPdf(pdf) is string pdfError) return pdfError;
                var delivery = await notifications.QueueExcelPayslipPdfAsync(db, tx, batch, rows, email, pdf, requestId, actor, request.IncludeSeal, request.AmountDecimalPlaces, mailTemplate!);
                state.Deliveries.Add(delivery.Id, delivery);
                return null;
            });
        if (dispatched.Item is null) return dispatched;
        var result = dispatched.Item;
        // Missing recipients and previously handled rows neither queue mail nor alter saved snapshots/receipts.
        if (result.Items.All(item => item.Status != "Queued")) return (result, null);
        var manifest = new SendManifest { Fingerprint = fingerprint, CreatedBy = Limit(actor, 190), CreatedAtUtc = DateTime.UtcNow, Result = result };
        state.Requests.Add(requestId, JsonSerializer.SerializeToElement(manifest, JsonOptions));
        await db.ExecuteAsync("UPDATE excel_payslip_batches SET send_state_json=CAST(@Json AS JSON) WHERE client_id=@ClientId AND id=@Id", new { ClientId = clientId, Id = batchId, Json = JsonSerializer.Serialize(state, JsonOptions) }, tx);
        await tx.CommitAsync(); return (result, null);
    }

    private static ExcelPayslipDeliveryItem DeliveryItem(ExcelPayslipRow row, string email, string status, string message) => new() { RowId = row.Id, EmployeeName = row.EmployeeName, Email = email, Status = status, Message = message };

    internal static (ExcelPayslipDeliveryResult? Item, string? Error) RestoreSendRequest(string? json, string fingerprint)
    {
        var previous = string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<SendManifest>(json, JsonOptions);
        if (string.IsNullOrEmpty(previous?.Fingerprint)) return (null, null);
        if (previous.Fingerprint != fingerprint) return (null, "This send request ID was already used with different rows, recipients or seal options. Start a new send action.");
        foreach (var item in previous.Result.Items.Where(i => i.Status == "Queued"))
        {
            item.Status = "Already queued";
            item.Message = "This request was already queued. Check Settings > Notifications for delivery status.";
        }
        return (previous.Result, null);
    }

    internal static string SendFingerprint(string batchId, SendExcelPayslipsRequest request) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    {
        BatchId = batchId, request.Mode, request.IncludeSeal, request.AmountDecimalPlaces,
        RowIds = request.RowIds.OrderBy(id => id, StringComparer.Ordinal),
        Email = request.Email?.Trim().ToLowerInvariant() ?? "",
        Overrides = request.EmailOverrides.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new { p.Key, Value = p.Value?.Trim().ToLowerInvariant() })
    }, JsonOptions))));

    private sealed class SendManifest
    {
        public string Fingerprint { get; set; } = "";
        public string CreatedBy { get; set; } = "";
        public DateTime CreatedAtUtc { get; set; }
        public ExcelPayslipDeliveryResult Result { get; set; } = new();
    }
}
