using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public partial class SettingsRepository
{
    private static readonly JsonSerializerOptions TemplateUpdateJson = new(JsonSerializerDefaults.Web);

    public async Task<JsonObject?> UpdateClientSalaryTemplateAsync(int clientId, long templateId, SalaryTemplateUpdateRequest request)
    {
        if (request?.Expected is null || request.Template is null)
            throw new InvalidOperationException("Submit the originally loaded template and its edited configuration.");
        var draft = request.Template;
        if (string.IsNullOrWhiteSpace(draft.Name) || draft.Name.Trim().Length > 200 || draft.Lines is not { Count: >= 1 and <= 100 }
            || draft.Lines.Any(line => line is null || string.IsNullOrWhiteSpace(line.ComponentId))
            || draft.Lines.Select(line => line.ComponentId).Distinct(StringComparer.Ordinal).Count() != draft.Lines.Count)
            throw new InvalidOperationException("Name the template and select up to 100 distinct saved components.");
        var annualCtc = ParseTemplateAnnualCtc(draft.AnnualCtc);

        await using var db = Connection();
        await db.OpenAsync();
        await using var tx = await db.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        // All template writers take the structure header lock before updating its lines.
        var scope = await ReadDeletionScopeAsync(db, tx, clientId, templateId, locked: true);
        if (scope is null || scope.Value.Owners.Count != 1 || scope.Value.Header.ClientId > 0 && scope.Value.Header.ClientId != clientId)
            return null;
        var header = scope.Value.Header;
        var currentLines = await ReadTemplateLinesAsync(db, tx, templateId);
        var current = TemplateContent(header.Name, header.AnnualCtc, header.Active, currentLines.Select(row => row.Line));
        var wanted = TemplateContent(draft.Name.Trim(), annualCtc, draft.Active, draft.Lines);
        if (!JsonNode.DeepEquals(current, wanted))
        {
            var expected = request.Expected;
            if (!JsonNode.DeepEquals(current, TemplateContent(expected.Name, ParseTemplateAnnualCtc(expected.AnnualCtc), expected.Active, expected.Lines)))
                throw new DBConcurrencyException("This salary template changed after you opened it. Reopen the current template and review your edit.");

            var key = templateId.ToString(CultureInfo.InvariantCulture);
            var managedKeys = new[] { ReviewedNamedFormulaInputs.ProfileKey(key), ReportedPeriodInputProfileStore.Key(key) };
            if (await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM modulesettings WHERE ModuleCode IN @managedKeys)",
                    new { managedKeys }, tx))
                throw new DBConcurrencyException("This template has reviewed payroll inputs. Use its reviewed configuration workflow to change it.");

            if (!string.Equals(header.Name, draft.Name.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                var matchingScopes = await db.QueryAsync<string>("""
                    SELECT COALESCE(NULLIF(TRIM(ClientRef),''),CAST(ClientId AS CHAR)) FROM salarystructures
                    WHERE Id<>@templateId AND LOWER(TRIM(Name))=LOWER(@name)
                    """, new { templateId, name = draft.Name.Trim() }, tx);
                if (matchingScopes.Any(raw => ReadClientRefs(new JsonObject { ["clientId"] = raw }) is { } owners && (owners.Count == 0 || owners.Contains(clientId))))
                    throw new InvalidOperationException("A salary template with this name already exists for this client.");
            }

            if (!JsonNode.DeepEquals(current["lines"], wanted["lines"]))
                await UpdateTemplateLinesAsync(db, tx, templateId, currentLines, draft.Lines, wanted["lines"]!.AsArray());
            if (header.Name != draft.Name.Trim() || header.AnnualCtc != annualCtc || header.Active != draft.Active)
                await db.ExecuteAsync("UPDATE salarystructures SET Name=@name,AnnualCtc=@annualCtc,Active=@active WHERE Id=@templateId",
                    new { name = draft.Name.Trim(), annualCtc, draft.Active, templateId }, tx);
            var savedHeader = await db.QuerySingleAsync<DeletionHeader>(
                "SELECT Id,ClientId,ClientRef,Name,AnnualCtc,Active FROM salarystructures WHERE Id=@templateId", new { templateId }, tx);
            var savedLines = await ReadTemplateLinesAsync(db, tx, templateId);
            var saved = TemplateContent(savedHeader.Name, savedHeader.AnnualCtc, savedHeader.Active, savedLines.Select(row => row.Line));
            if (header.ClientId != savedHeader.ClientId || header.ClientRef != savedHeader.ClientRef || !JsonNode.DeepEquals(saved, wanted))
                throw new InvalidOperationException("The saved template differs from your edit. The change was rolled back.");
        }

        await tx.CommitAsync();
        wanted["id"] = templateId;
        wanted["clientId"] = string.IsNullOrWhiteSpace(header.ClientRef) ? header.ClientId.ToString(CultureInfo.InvariantCulture) : header.ClientRef;
        return wanted;
    }

    private static decimal ParseTemplateAnnualCtc(string value)
    {
        if (value is null || !decimal.TryParse(string.IsNullOrWhiteSpace(value) ? "0" : value, NumberStyles.Number,
                CultureInfo.InvariantCulture, out var amount) || amount is < 0 or > 1000000000m)
            throw new InvalidOperationException("Enter a valid annual CTC for this salary template.");
        return decimal.Round(amount, 2);
    }

    private static JsonObject TemplateContent(string name, decimal annualCtc, bool active, IEnumerable<SalaryTemplateCreateLine> lines) =>
        JsonSerializer.SerializeToNode(new { name, annualCtc = annualCtc.ToString("0.00", CultureInfo.InvariantCulture), active, lines }, TemplateUpdateJson)!.AsObject();

    private sealed record StoredTemplateLine(SalaryTemplateCreateLine Line, int SortOrder);

    private static async Task<List<StoredTemplateLine>> ReadTemplateLinesAsync(MySqlConnection db, MySqlTransaction tx, long templateId) =>
        (await db.QueryAsync<SalaryTemplateCreateLine, int, StoredTemplateLine>("""
            SELECT ComponentId, ValueText AS Value, CalculationType, Formula, BaseComponent, ProRataOverride, RoundingMode, SortOrder
            FROM salarystructurelines WHERE StructureId=@templateId ORDER BY SortOrder, Id
            """, (line, sortOrder) => new(line, sortOrder), new { templateId }, tx, splitOn: "SortOrder")).AsList();

    private sealed record TemplateEditMaster(long Id, string Category, string CalculationType, string Formula, string Value, bool Active);

    private static async Task UpdateTemplateLinesAsync(MySqlConnection db, MySqlTransaction tx, long templateId,
        List<StoredTemplateLine> currentLines, List<SalaryTemplateCreateLine> lines, JsonArray wantedLines)
    {
        // An unchanged legacy override can remain in an existing template. Apply current
        // component and formula validation to newly selected or modified lines.
        var previous = currentLines.ToDictionary(row => row.Line.ComponentId);
        var sequenceChanged = !currentLines.Select(row => row.Line.ComponentId).SequenceEqual(lines.Select(line => line.ComponentId));
        var changed = new List<SalaryTemplateCreateLine>();
        var writes = new JsonArray();
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            previous.TryGetValue(line.ComponentId, out var old);
            var same = old is not null && JsonNode.DeepEquals(JsonSerializer.SerializeToNode(old.Line, TemplateUpdateJson), wantedLines[index]);
            var sortOrder = sequenceChanged ? index : old!.SortOrder;
            if (same && old!.SortOrder == sortOrder) continue;
            if (!same) changed.Add(line);
            var write = wantedLines[index]!.DeepClone().AsObject();
            write["sortOrder"] = sortOrder;
            writes.Add(write);
        }
        if (changed.Count > 0)
        {
            var ids = changed.Select(line => long.TryParse(line.ComponentId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0).ToArray();
            var masters = await db.QueryAsync<TemplateEditMaster>("""
                SELECT Id, Category, CalculationType, Formula, ValueText AS Value, Active FROM salarycomponents WHERE Id IN @ids LOCK IN SHARE MODE
                """, new { ids }, tx);
            ValidateNewTemplateLines(changed, JsonSerializer.SerializeToNode(masters, TemplateUpdateJson)!.AsArray());
        }
        await PayrollFormulaStorage.RequireAsync(db, tx, lines.Select(line => line.Formula));
        var removed = previous.Keys.Except(lines.Select(line => line.ComponentId)).ToArray();
        if (removed.Length > 0)
            await db.ExecuteAsync("DELETE FROM salarystructurelines WHERE StructureId=@templateId AND ComponentId=@componentId",
                removed.Select(componentId => new { templateId, componentId }), tx);
        if (writes.Count == 0) return;
        // Upsert only added, edited or moved lines, retaining existing line identities.
        await db.ExecuteAsync("""
            INSERT INTO salarystructurelines (StructureId,ComponentId,ValueText,CalculationType,Formula,BaseComponent,ProRataOverride,RoundingMode,SortOrder)
            SELECT @templateId, src.cid, src.val, src.calc, src.expr, src.baseCode, src.prorata, src.rounding, src.pos
            FROM JSON_TABLE(@linesJson,'$[*]' COLUMNS (
                pos INT PATH '$.sortOrder', cid VARCHAR(80) PATH '$.componentId', val VARCHAR(1000) PATH '$.value',
                calc VARCHAR(80) PATH '$.calculationType', expr VARCHAR(4096) PATH '$.formula', baseCode VARCHAR(80) PATH '$.baseComponent',
                prorata VARCHAR(20) PATH '$.proRataOverride', rounding VARCHAR(40) PATH '$.roundingMode'
            )) AS src
            ON DUPLICATE KEY UPDATE ValueText=src.val, CalculationType=src.calc, Formula=src.expr, BaseComponent=src.baseCode,
                ProRataOverride=src.prorata, RoundingMode=src.rounding, SortOrder=src.pos
            """, new { templateId, linesJson = writes.ToJsonString() }, tx);
    }
}
